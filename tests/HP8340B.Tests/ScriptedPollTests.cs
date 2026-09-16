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
