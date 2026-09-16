using HP8340B.Instruments;
using HP8340B.Instruments.Visa;
using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// What the settle-wait does when the bus misbehaves.
///
/// <para>These cases cannot be reached with <see cref="SimulatedInstrumentLink"/>, because it
/// models an instrument that works — and an instrument that works never fails a poll. Scripting
/// the transport instead leaves the driver under test. The pattern is the HP-Attenuator session's
/// <c>--cal-selftest</c> approach, including its observation that the adversarial cases are the
/// ones that find things.</para>
/// </summary>
public class ScriptedPollTests
{
    private const byte Settled = (byte)StatusByte1.RfSettled;
    private const byte NotSettled = 0;
    private const byte SyntaxError = (byte)StatusByte1.SyntaxError;

    /// <summary>
    /// The deadline logic as it was before the fix: poll, check readiness, check the clock, sleep.
    /// The clock is only consulted BETWEEN polls, so a blocking poll runs past the budget.
    ///
    /// <para>Kept solely so the cases below can report whether the old code reached the same
    /// verdict. Without that, a passing suite proves the new code works but not that it does
    /// anything the old code did not.</para>
    /// </summary>
    private static (byte? Result, TimeSpan Elapsed) LegacyWaitFor(
        IInstrumentLink link, Func<byte, bool> isReady, TimeSpan timeout, TimeSpan gap)
    {
        var started = DateTime.UtcNow;
        var deadline = started + timeout;

        while (true)
        {
            var status = link.SerialPoll();

            if (isReady(status)) return (status, DateTime.UtcNow - started);
            if (DateTime.UtcNow >= deadline) return (null, DateTime.UtcNow - started);

            Thread.Sleep(gap);
        }
    }

    // --- A failed poll is not a status of zero ------------------------------------------------

    [Fact]
    public void AFailedPollPropagatesRatherThanReadingAsNotReady()
    {
        // The distinction that matters: a wedged bus and a quiet instrument are different
        // problems. Collapsing them turns a bus fault into a measurement that merely looks slow,
        // and the wait would burn its whole budget before reporting a timeout that names nothing.
        var link = new ScriptedInstrumentLink([NotSettled, ScriptedInstrumentLink.PollFault]);

        Assert.Throws<TimeoutException>(() => StatusPoller.WaitFor(
            link,
            isReady: s => ((StatusByte1)s).HasFlag(StatusByte1.RfSettled),
            timeout: TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void AFailedPollIsStillRecordedInTheBusLog()
    {
        // An audit log that omits the failure shows an unbroken run of successful traffic right
        // up to the point where everything stopped, which is the least useful moment to go quiet.
        var link = new ScriptedInstrumentLink([ScriptedInstrumentLink.PollFault]);

        Assert.Throws<TimeoutException>(() => link.SerialPoll());

        var polls = link.Transactions.Where(t => t.Operation == BusOperation.SerialPoll).ToList();

        Assert.Single(polls);
        Assert.Contains("FAILED", polls[0].Text);
        Assert.Null(polls[0].StatusByte);
    }

    [Fact]
    public void ElapsedIsRecordedEvenWhenThePollThrows()
    {
        // The finally block earns its keep here: a wait that died still says how long it ran.
        var link = new ScriptedInstrumentLink([ScriptedInstrumentLink.PollFault])
        {
            PollTakes = TimeSpan.FromMilliseconds(20),
        };

        Assert.Throws<TimeoutException>(() => StatusPoller.WaitFor(
            link, isReady: _ => false, timeout: TimeSpan.FromSeconds(1)));

        Assert.True(StatusPoller.LastElapsed >= TimeSpan.FromMilliseconds(10));
    }

    [Fact]
    public void AZeroStatusByteIsNotMistakenForAFailure()
    {
        // The other direction. A genuinely quiet instrument returns 0x00 and that is a legitimate
        // "not ready yet", not an error — so the wait must keep going and then time out cleanly.
        var link = new ScriptedInstrumentLink { TrailingPoll = NotSettled };

        var result = StatusPoller.WaitFor(
            link, isReady: _ => false, timeout: TimeSpan.FromMilliseconds(60),
            interval: TimeSpan.FromMilliseconds(5));

        Assert.Null(result);
        Assert.True(link.Polls > 1);
    }

    // --- The deadline, against the algorithm it replaced -----------------------------------------

    [Fact]
    public void TheDeadlineBoundsTheWholeWaitWhereTheOldAlgorithmDidNot()
    {
        // The case the fix exists for, run against BOTH algorithms so the difference is visible
        // rather than asserted. 120 ms budget, 60 ms per poll, and the poll never reports ready.
        //
        // Old: polls, checks the clock, sleeps, polls again — starting a 60 ms poll it has no
        // budget for. New: will not start a poll it cannot finish.
        static ScriptedInstrumentLink Link() => new()
        {
            TrailingPoll = NotSettled,
            PollTakes = TimeSpan.FromMilliseconds(60),
        };

        var budget = TimeSpan.FromMilliseconds(120);
        var gap = TimeSpan.FromMilliseconds(5);

        var fixedLink = Link();
        var started = DateTime.UtcNow;
        var fixedResult = StatusPoller.WaitFor(
            fixedLink, isReady: _ => false, timeout: budget, interval: gap);
        var fixedElapsed = DateTime.UtcNow - started;

        var legacyLink = Link();
        var (legacyResult, legacyElapsed) =
            LegacyWaitFor(legacyLink, _ => false, budget, gap);

        // Both time out — the verdict is the same, which is why the old code looked fine.
        Assert.Null(fixedResult);
        Assert.Null(legacyResult);

        // The difference is entirely in how long they took and how many polls they started.
        Assert.True(fixedLink.Polls < legacyLink.Polls,
            $"fixed made {fixedLink.Polls} poll(s), old code {legacyLink.Polls}");

        Assert.True(fixedElapsed < legacyElapsed,
            $"fixed took {fixedElapsed.TotalMilliseconds:0} ms, old code "
            + $"{legacyElapsed.TotalMilliseconds:0} ms against a {budget.TotalMilliseconds:0} ms budget");
    }

    [Fact]
    public void AReadyPollEndsTheWaitImmediatelyUnderBothAlgorithms()
    {
        // The fix must not change the happy path, and saying so out loud is worth a test: a
        // deadline fix that also made normal waits slower would be a poor trade.
        var link = new ScriptedInstrumentLink([Settled]);

        var result = StatusPoller.WaitFor(
            link, isReady: s => ((StatusByte1)s).HasFlag(StatusByte1.RfSettled),
            timeout: TimeSpan.FromSeconds(5));

        Assert.NotNull(result);
        Assert.Equal(1, link.Polls);
    }

    [Fact]
    public void ItSettlesOnTheThirdPollWhenThatIsWhenTheInstrumentIsReady()
    {
        // Scripted rather than modelled, so the number of polls is exactly what the test says.
        var link = new ScriptedInstrumentLink([NotSettled, NotSettled, Settled]);

        var result = StatusPoller.WaitFor(
            link, isReady: s => ((StatusByte1)s).HasFlag(StatusByte1.RfSettled),
            timeout: TimeSpan.FromSeconds(5), interval: TimeSpan.FromMilliseconds(1));

        Assert.NotNull(result);
        Assert.Equal(3, link.Polls);
    }

    // --- The abort path -------------------------------------------------------------------------

    [Fact]
    public void ASyntaxErrorAbortsRatherThanWaitingOutTheBudget()
    {
        // A rejected command never settles. Waiting out the timeout would report a slow
        // instrument instead of the bad command that is actually the fault.
        var link = new ScriptedInstrumentLink([NotSettled, SyntaxError]);

        var ex = Assert.Throws<InvalidOperationException>(() => StatusPoller.WaitFor(
            link,
            isReady: s => ((StatusByte1)s).HasFlag(StatusByte1.RfSettled),
            timeout: TimeSpan.FromSeconds(5),
            abort: s => ((StatusByte1)s).HasFlag(StatusByte1.SyntaxError),
            abortMessage: _ => "syntax error",
            interval: TimeSpan.FromMilliseconds(1)));

        Assert.Equal("syntax error", ex.Message);
        Assert.Equal(2, link.Polls);
    }

    [Fact]
    public void TheDutDriverSurfacesABusFaultRatherThanReportingNotSettled()
    {
        // End to end through the real driver: WaitSettled returns false for "not ready in time",
        // so a bus fault must NOT come back as false. It throws, and the caller can tell the
        // difference between an instrument that is slow and a bus that is gone.
        var link = new ScriptedInstrumentLink([ScriptedInstrumentLink.PollFault]);
        var dut = new Hp8340B(link);

        Assert.Throws<TimeoutException>(() => dut.WaitSettled(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void TheDutDriverReturnsFalseForAGenuinelySlowInstrument()
    {
        // The other half of the same distinction.
        var link = new ScriptedInstrumentLink { TrailingPoll = NotSettled };
        var dut = new Hp8340B(link);

        Assert.False(dut.WaitSettled(TimeSpan.FromMilliseconds(30)));
    }
}

/// <summary>
/// What the bus log records when a WRITE fails.
///
/// <para>The HP-Attenuator session found the mirror of my failed-poll gap on their side, in their
/// <c>Write</c> as well as their trace. Checking here found the same shape in four places: the
/// log and the history were both appended only after a successful call, so a write that threw
/// left no record at all.</para>
///
/// <para>That is worse here than a diagnostics inconvenience. Repo rule 1 requires every write to
/// the DUT to be logged with its timestamp, and a write that threw may still have reached the
/// instrument — a partial transfer is not a non-event.</para>
/// </summary>
public class FailedWriteTests
{
    [Fact]
    public void AFailedWriteIsRecordedInTheTransactionLog()
    {
        var link = new ScriptedInstrumentLink { FailWrites = true };

        Assert.Throws<TimeoutException>(() => link.Write("CW 10 GZ"));

        var writes = link.Transactions.Where(t => t.Operation == BusOperation.Write).ToList();

        Assert.Single(writes);
        Assert.Contains("WRITE FAILED", writes[0].Text);
        Assert.Contains("CW 10 GZ", writes[0].Text);   // names the command that was in flight
    }

    [Fact]
    public void AFailedWriteDoesNotEnterTheHistory()
    {
        // History means "commands that went out", and drivers assert against its last entry.
        // Putting a failure there would claim something was sent that may not have been. The
        // transaction log is the audit record; the two are deliberately different.
        var link = new ScriptedInstrumentLink { FailWrites = true };

        Assert.Throws<TimeoutException>(() => link.Write("CW 10 GZ"));

        Assert.Empty(link.History);
        Assert.NotEmpty(link.Transactions);
    }

    [Fact]
    public void ADriverCommandThatCannotBeSentSurfacesRatherThanBeingSwallowed()
    {
        var link = new ScriptedInstrumentLink { FailWrites = true };
        var dut = new Hp8340B(link);

        Assert.Throws<TimeoutException>(() => dut.SetCwGHz(10));
    }

    // --- The listen-only case, which has no readback to fall back on --------------------------

    [Fact]
    public void AFailedRelayCommandLeavesTheSwitchDriversStateUnknown()
    {
        // The worst combination on this bench: a write that failed to a device that cannot talk.
        // The pads may or may not have moved and nothing can be asked, so anything depending on
        // the attenuation is now depending on a guess.
        var link = new ScriptedInstrumentLink();
        var driver = new Hp11713A(link);

        driver.SetRelays("A1B2");
        Assert.True(driver.StateIsKnown);
        Assert.Equal("A1B2", driver.LastRelaySetting);

        link.FailWrites = true;
        Assert.Throws<TimeoutException>(() => driver.SetRelays("A2B1"));

        Assert.False(driver.StateIsKnown);
        Assert.Null(driver.LastRelaySetting);
    }

    [Fact]
    public void TheStaleSettingIsNotLeftStandingAsIfItWereStillTrue()
    {
        // The tempting alternative — keep the last good value — would be a lie: the failed
        // command may well have moved the relays.
        var link = new ScriptedInstrumentLink();
        var driver = new Hp11713A(link);

        driver.SetRelays("A1B2");
        link.FailWrites = true;

        Assert.Throws<TimeoutException>(() => driver.SetRelays("A2B1"));

        Assert.DoesNotContain("A1B2", driver.Probe().Detail!);
        Assert.Contains("FAILED", driver.Probe().Detail!);
    }

    [Fact]
    public void ASuccessfulCommandAfterwardsRestoresConfidence()
    {
        var link = new ScriptedInstrumentLink { FailWrites = true };
        var driver = new Hp11713A(link);

        Assert.Throws<TimeoutException>(() => driver.SetRelays("A1B2"));
        Assert.False(driver.StateIsKnown);

        link.FailWrites = false;
        driver.SetRelays("A1B2");

        Assert.True(driver.StateIsKnown);
        Assert.Contains("A1B2", driver.Probe().Detail!);
    }
}

/// <summary>
/// A learn-string write that fails part way through.
///
/// <para>Raised by the HP-Attenuator session, which found the same shape in its switch driver and
/// pointed out that this one is worse. Their 11713A case is <i>unknowable</i> — the device cannot
/// be asked. This one is <b>silent and persistent</b>: the DUT will answer every query perfectly
/// happily from a configuration that is part old and part new, and nothing about the readings
/// will look wrong.</para>
/// </summary>
public class LearnStringFailureTests
{
    private static byte[] AState(byte fill) => Enumerable.Repeat(fill, 123).ToArray();

    [Fact]
    public void AFailedLearnStringWriteLeavesTheStateUnknown()
    {
        var link = new ScriptedInstrumentLink();
        var dut = new Hp8340B(link);

        Assert.True(dut.StateIsKnown);

        // The payload fails, not the IL command — that is the case that leaves the instrument
        // half-configured, because it had already started listening for 123 bytes.
        link.FailWriteBytes = true;
        Assert.ThrowsAny<Exception>(() => dut.WriteLearnString(AState(0x5A)));

        Assert.False(dut.StateIsKnown);
        Assert.Contains("part way through", dut.LastStateChange!);
    }

    [Fact]
    public void AnILThatNeverWentOutLeavesTheStateKnown()
    {
        // The non-obvious half. If the command itself failed the instrument never started
        // listening, so its configuration is exactly what it was. Marking it unknown here would
        // send somebody to preset an instrument that was fine.
        var link = new ScriptedInstrumentLink { FailWrites = true };
        var dut = new Hp8340B(link);

        Assert.ThrowsAny<Exception>(() => dut.WriteLearnString(AState(0x5A)));

        Assert.True(dut.StateIsKnown);
    }

    [Fact]
    public void ASuccessfulWriteLeavesItKnown()
    {
        var link = new ScriptedInstrumentLink();
        var dut = new Hp8340B(link);

        dut.WriteLearnString(AState(0x5A));

        Assert.True(dut.StateIsKnown);
        Assert.Contains("restored", dut.LastStateChange!);
    }

    [Fact]
    public void TheProbeSaysTheStateIsUnknownBecauseNoStatusBitWill()
    {
        // This is the whole reason it has to be tracked in the driver: a half-written learn string
        // sets no flag on the instrument. If nothing says so here, nothing says so at all.
        var model = new SimulatedSweeper();
        var link = model.CreateLink();
        var dut = new Hp8340B(link);

        Assert.DoesNotContain("STATE UNKNOWN", dut.Probe().Detail ?? "");

        // Force the failure through a link that refuses the payload, then check the same driver.
        var failing = new ScriptedInstrumentLink { FailWriteBytes = true, Response = "HP8340B SIM" };
        var broken = new Hp8340B(failing);

        Assert.ThrowsAny<Exception>(() => broken.WriteLearnString(AState(0x11)));

        failing.FailWriteBytes = false;
        Assert.Contains("STATE UNKNOWN", broken.Probe().Detail!);
        Assert.Contains("part old and part new", broken.Probe().Detail!);
    }

    [Fact]
    public void StateCanOnlyBeDeclaredKnownAgainExplicitly()
    {
        // Nothing else can establish it, so nothing else gets to claim it. A preset or a
        // successful restore is the only cure.
        var link = new ScriptedInstrumentLink { FailWriteBytes = true };
        var dut = new Hp8340B(link);

        Assert.ThrowsAny<Exception>(() => dut.WriteLearnString(AState(0x11)));
        Assert.False(dut.StateIsKnown);

        dut.DeclareStateKnown("Instrument preset by hand.");

        Assert.True(dut.StateIsKnown);
        Assert.Equal("Instrument preset by hand.", dut.LastStateChange);
    }

    [Fact]
    public void ADeclarationNeedsToSayHow()
    {
        // It goes into the session record as the reason the state is trusted again, and "because
        // somebody said so" is not a reason.
        var dut = new Hp8340B(new ScriptedInstrumentLink());

        Assert.Throws<ArgumentException>(() => dut.DeclareStateKnown("  "));
    }

    [Fact]
    public void AWrongLengthLearnStringIsRefusedBeforeAnythingIsSent()
    {
        // The state stays known, because nothing went out: IL was never sent, so the instrument
        // is not waiting for bytes.
        var link = new ScriptedInstrumentLink();
        var dut = new Hp8340B(link);

        Assert.Throws<ArgumentException>(() => dut.WriteLearnString(new byte[100]));

        Assert.True(dut.StateIsKnown);
        Assert.Empty(link.History);
    }
}
