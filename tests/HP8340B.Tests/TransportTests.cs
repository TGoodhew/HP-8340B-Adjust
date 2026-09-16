using HP8340B.Instruments;
using HP8340B.Instruments.Config;
using HP8340B.Instruments.Visa;
using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// M0-02: the transport's per-instrument timeouts, the transaction log that satisfies repo
/// rule 1, and the shared status poller that settle-waits are built on.
/// </summary>
public class TransportTests
{
    private static SimulatedInstrumentLink NewLink() =>
        new("SIM::test::INSTR", cmd => cmd.StartsWith("*IDN?") ? "SIM,TEST,0,0" : "");

    [Fact]
    public void EveryBusOperationIsLoggedWithATimestamp()
    {
        var link = NewLink();
        var before = DateTime.UtcNow;

        link.Write("IP");
        link.Query("*IDN?");
        link.SerialPoll();

        var log = link.Transactions;

        // Write, then Query's write + read, then the poll.
        Assert.Equal(4, log.Count);
        Assert.Equal(BusOperation.Write, log[0].Operation);
        Assert.Equal("IP", log[0].Text);
        Assert.Equal(BusOperation.Write, log[1].Operation);
        Assert.Equal(BusOperation.Read, log[2].Operation);
        Assert.Equal("SIM,TEST,0,0", log[2].Text);
        Assert.Equal(BusOperation.SerialPoll, log[3].Operation);

        Assert.All(log, t => Assert.True(t.At >= before));
        Assert.All(log, t => Assert.Equal("SIM::test::INSTR", t.Resource));
    }

    [Fact]
    public void SerialPollsAreLoggedWithTheirStatusByte()
    {
        // Polls are the evidence for whether an instrument had settled when a measurement was
        // taken, so they have to be in the record, not just the writes.
        var link = NewLink();
        link.StatusByte = 0x08;
        link.SerialPoll();

        var poll = link.Transactions.Single(t => t.Operation == BusOperation.SerialPoll);
        Assert.Equal((byte)0x08, poll.StatusByte);
        Assert.Contains("0x08", poll.ToString());
    }

    [Fact]
    public void ClearWipesHistoryButNotTheAuditLog()
    {
        // History is a convenience for "what state did I put this instrument in". The log is the
        // audit trail and must never lose entries, or rule 1's guarantee is worthless.
        var link = NewLink();
        link.Write("IP");
        link.Clear();

        Assert.Empty(link.History);
        Assert.Equal(2, link.Transactions.Count);
        Assert.Equal(BusOperation.Clear, link.Transactions[^1].Operation);
    }

    [Fact]
    public void TimeoutComesFromConfigPerInstrument()
    {
        // The 8902A averages for up to 10 s; a flat 5 s would fail it. bench.json carries the
        // per-instrument value and the factory must apply it.
        var config = new InstrumentConfig
        {
            Role = "measuring-receiver", Model = "HP 8902A",
            Address = "GPIB0::14::INSTR", TimeoutMs = 20000,
        };

        using var instrument = InstrumentFactory.Create(config, simulate: true);
        Assert.Equal(TimeSpan.FromSeconds(20), instrument.Link.Timeout);
    }

    [Fact]
    public void BenchConfigGivesTheSlowInstrumentsLongerTimeouts()
    {
        var bench = BenchConfig.Load(Path.Combine(AppContext.BaseDirectory, "bench.json"));

        Assert.Equal(20000, bench.ByRole("dut")!.TimeoutMs);
        Assert.Equal(20000, bench.ByRole("measuring-receiver")!.TimeoutMs);
        Assert.Equal(15000, bench.ByRole("power-meter")!.TimeoutMs);

        // Everything else takes the default rather than carrying a pointless value.
        Assert.Null(bench.ByRole("counter")!.TimeoutMs);
    }

    [Fact]
    public void PollerReturnsTheStatusByteThatSatisfiedTheCondition()
    {
        var link = NewLink();
        link.StatusByte = 0x18;

        var status = StatusPoller.WaitFor(link, s => (s & 0x08) != 0, TimeSpan.FromMilliseconds(200));
        Assert.Equal((byte)0x18, status);
    }

    [Fact]
    public void PollerKeepsPollingUntilTheInstrumentIsReady()
    {
        // Models an instrument that takes a few polls to settle, so the wait is genuinely
        // exercised rather than succeeding on the first try as a static simulator would.
        var link = NewLink();
        link.StatusSequence = poll => poll < 3 ? (byte)0x00 : (byte)0x08;

        var status = StatusPoller.WaitFor(
            link, s => (s & 0x08) != 0, TimeSpan.FromSeconds(2),
            interval: TimeSpan.FromMilliseconds(1));

        Assert.Equal((byte)0x08, status);
        Assert.Equal(4, link.PollCount);
    }

    [Fact]
    public void PollerReturnsNullOnTimeoutRatherThanThrowing()
    {
        var link = NewLink();
        link.StatusByte = 0x00;

        Assert.Null(StatusPoller.WaitFor(
            link, s => (s & 0x08) != 0, TimeSpan.FromMilliseconds(30),
            interval: TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public void PollerPollsAtLeastOnceEvenWithAZeroTimeout()
    {
        // "Are you ready now?" should get an answer, not an immediate timeout.
        var link = NewLink();
        link.StatusByte = 0x08;

        Assert.Equal((byte)0x08, StatusPoller.WaitFor(link, s => (s & 0x08) != 0, TimeSpan.Zero));
        Assert.Equal(1, link.PollCount);
    }

    [Fact]
    public void PollerAbortsOnAnErrorBitRatherThanWaitingOutTheTimeout()
    {
        var link = NewLink();
        link.StatusByte = 0x20;   // 8340B status byte #1 bit 5: HP-IB syntax error.

        var ex = Assert.Throws<InvalidOperationException>(() => StatusPoller.WaitFor(
            link,
            isReady: s => (s & 0x08) != 0,
            timeout: TimeSpan.FromSeconds(30),
            abort: s => (s & 0x20) != 0,
            abortMessage: _ => "syntax error"));

        Assert.Equal("syntax error", ex.Message);
    }

    [Fact]
    public void DutWaitsForEndOfSweepAsWellAsSettled()
    {
        var link = SimulatedBench.LinkFor("HP 8340B", "SIM::dut::INSTR");
        var dut = new Hp8340B(link);

        // The simulator reports settled but not end-of-sweep until a sweep is taken.
        Assert.True(dut.WaitSettled(TimeSpan.FromMilliseconds(50)));

        link.StatusByte = (byte)(StatusByte1.RfSettled | StatusByte1.EndOfSweep);
        Assert.True(dut.WaitEndOfSweep(TimeSpan.FromMilliseconds(50)));
    }
}

/// <summary>
/// The settle-wait's deadline, and the one case where it cannot hold.
///
/// <para>Found by the HP-Attenuator session working on the same bench: a deadline enforced only
/// BETWEEN blocking reads does not bound the wait at all. Their 30 s budget overran to 134 s.
/// This project had the same shape — SerialPoll blocks for up to the instrument's VISA timeout,
/// which is 20 s for the DUT and the 8902A.</para>
/// </summary>
public class StatusPollerDeadlineTests
{
    /// <summary>A link whose serial poll takes real time and never reports ready.</summary>
    private sealed class SlowLink(TimeSpan pollTakes) : IInstrumentLink
    {
        public string ResourceName => "SIM::slow::INSTR";
        public bool IsSimulated => true;
        public TimeSpan Timeout { get; set; }
        public IReadOnlyList<string> History => Array.Empty<string>();
        public IReadOnlyList<InstrumentTransaction> Transactions => Array.Empty<InstrumentTransaction>();

        public int Polls { get; private set; }

        public void Clear() { }
        public void Write(string command) { }
        public string Read() => string.Empty;
        public string Query(string command) => string.Empty;
        public byte[] ReadBytes(int count) => new byte[count];
        public void WriteBytes(byte[] data) { }

        public byte SerialPoll()
        {
            Polls++;
            Thread.Sleep(pollTakes);
            return 0;                     // never ready
        }

        public void Dispose() { }
    }

    [Fact]
    public void ASlowPollDoesNotLetTheWaitOverrunItsBudget()
    {
        // 120 ms budget, and each poll takes 50 ms. Two polls fit; a third would not, so the
        // wait must stop rather than starting it. Before this was fixed the loop only checked
        // the clock between polls and would happily begin one that ran well past the deadline.
        var link = new SlowLink(TimeSpan.FromMilliseconds(50));
        var started = DateTime.UtcNow;

        var result = StatusPoller.WaitFor(
            link,
            isReady: _ => false,
            timeout: TimeSpan.FromMilliseconds(120),
            interval: TimeSpan.FromMilliseconds(5));

        var elapsed = DateTime.UtcNow - started;

        Assert.Null(result);

        // Generous on a loaded machine, but far tighter than the un-fixed behaviour, which would
        // have started a third 50 ms poll after the 120 ms mark.
        Assert.True(elapsed < TimeSpan.FromMilliseconds(400),
            $"The wait took {elapsed.TotalMilliseconds:0} ms against a 120 ms budget.");
    }

    [Fact]
    public void TheFirstPollIsAlwaysMadeEvenWithNoBudget()
    {
        // A caller asking "are you ready now?" deserves an answer. This is the documented case
        // where the wait can still overrun, and it is deliberate.
        var link = new SlowLink(TimeSpan.FromMilliseconds(30));

        StatusPoller.WaitFor(link, isReady: _ => false, timeout: TimeSpan.Zero);

        Assert.Equal(1, link.Polls);
    }

    [Fact]
    public void HowLongItActuallyTookIsRecorded()
    {
        // So an overrun shows up as a fact rather than as a mysteriously slow measurement.
        var link = new SlowLink(TimeSpan.FromMilliseconds(30));

        StatusPoller.WaitFor(link, isReady: _ => false, timeout: TimeSpan.Zero);

        Assert.True(StatusPoller.LastElapsed >= TimeSpan.FromMilliseconds(20),
            $"LastElapsed was {StatusPoller.LastElapsed.TotalMilliseconds:0} ms.");
    }

    [Fact]
    public void AFastReadyPollStillReturnsImmediately()
    {
        // The fix must not cost anything in the normal case.
        var link = SimulatedBench.LinkFor("HP 8340B", "SIM::dut::INSTR");

        var result = StatusPoller.WaitFor(
            link, isReady: s => (s & 0x08) != 0, timeout: TimeSpan.FromSeconds(5));

        Assert.NotNull(result);
    }
}
