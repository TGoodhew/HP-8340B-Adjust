using HP8340B.Instruments;
using HP8340B.Instruments.Timing;
using HP8340B.Instruments.Visa;
using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// M0-30, the instrument speed framework. Every rate figure in this project began as a guess
/// reasoned from transport characteristics; these are the mechanics that replace them with
/// measurements.
///
/// <para>The framework's own correctness matters more than most, because its output is used to
/// decide architecture — which instrument goes in the hand-tuning loop — and a flattering number
/// would send that decision the wrong way silently.</para>
/// </summary>
public class RateMeterTests
{
    private static SimulatedInstrumentLink Link(TimeSpan? perOperation = null, TimeSpan? perByte = null)
    {
        var link = new SimulatedInstrumentLink("SIM::rate::INSTR", _ => "ok");

        if (perOperation is not null || perByte is not null)
            link.Latency = new SimulatedLatency(
                perOperation ?? TimeSpan.Zero, perByte ?? TimeSpan.Zero);

        return link;
    }

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    // --- The distribution ----------------------------------------------------------------------

    [Fact]
    public void PercentilesAreDurationsThatActuallyOccurred()
    {
        // Nearest-rank, not interpolated. At bench sample counts an interpolated p95 is a number
        // nothing ever measured, and the whole point of this framework is to stop reporting those.
        var samples = new[] { Ms(1), Ms(2), Ms(3), Ms(4), Ms(100) };

        var profile = LatencyProfile.From("q", samples, isSimulated: true);

        Assert.Contains(profile.Median, samples);
        Assert.Contains(profile.P95, samples);
        Assert.Equal(Ms(1), profile.Min);
        Assert.Equal(Ms(100), profile.Max);
    }

    [Fact]
    public void TheTailIsReportedSeparatelyFromTheTypicalCase()
    {
        // A view that runs well and stalls occasionally has an excellent median and feels broken.
        // If the stall did not survive into the profile, the profile could not answer the question
        // it exists for.
        // Two stalls in twenty. Nearest-rank puts p95 at the second-slowest sample, so a single
        // outlier in twenty sits just inside the tail and does not move it -- which is correct,
        // and the reason Max is reported as well.
        var samples = Enumerable.Repeat(Ms(5), 18).Concat([Ms(400), Ms(400)]);

        var profile = LatencyProfile.From("frame", samples, isSimulated: true);

        Assert.Equal(Ms(5), profile.Median);
        Assert.Equal(Ms(400), profile.P95);
        Assert.Equal(Ms(400), profile.Max);
        Assert.True(profile.SustainedPerSecond < profile.MedianPerSecond,
            "Sustained rate should be the pessimistic one, or it cannot warn anybody.");
    }

    [Fact]
    public void AnEmptyProfileIsRefusedRatherThanReportedAsZero()
    {
        // Zero or infinity both read like results.
        Assert.Throws<ArgumentException>(
            () => LatencyProfile.From("nothing", Array.Empty<TimeSpan>(), isSimulated: true));
    }

    // --- Simulated figures must never pass as measurements --------------------------------------

    [Fact]
    public void ASimulatedProfileSaysSoWhereverItIsPrinted()
    {
        var profile = LatencyProfile.From("q", [Ms(1)], isSimulated: true);

        Assert.True(profile.IsSimulated);
        Assert.Contains("SIMULATED", profile.Describe());
    }

    [Fact]
    public void AProfileWithNoKnownLinkIsTreatedAsSimulated()
    {
        // A caller that cannot say where the work ran has not established that it ran on hardware.
        // The doubt has to fall towards "not evidence".
        var profile = RateMeter.Measure("work", () => { }, link: null, samples: 2, warmup: 0);

        Assert.True(profile.IsSimulated);
    }

    [Fact]
    public void AMeasurementOverASimulatedLinkIsMarkedSimulated()
    {
        var link = Link();
        var profile = RateMeter.Measure("query", () => link.Query("X"), link, samples: 2, warmup: 0);

        Assert.True(profile.IsSimulated);
    }

    // --- Measuring -----------------------------------------------------------------------------

    [Fact]
    public void WarmUpRepeatsAreRunButNotTimed()
    {
        // The first calls include JIT and the session's first exchange. Counting them would make
        // every profile's maximum a measurement of start-up.
        var calls = 0;

        var profile = RateMeter.Measure(
            "counted", () => calls++, link: null, samples: 4, warmup: 3);

        Assert.Equal(7, calls);
        Assert.Equal(4, profile.Count);
    }

    [Fact]
    public void ConfiguredLatencyShowsUpInTheMeasuredTime()
    {
        // A query is two bus operations — the write and the read that answers it — so a 2 ms
        // per-operation cost should be visible as at least 3 ms even allowing for a coarse clock.
        var link = Link(perOperation: Ms(2));

        var profile = RateMeter.Measure("query", () => link.Query("X"), link, samples: 4, warmup: 1);

        Assert.True(profile.Median >= Ms(3),
            $"Median was {profile.Median.TotalMilliseconds:0.##} ms; the configured cost should "
            + "have been visible.");
    }

    [Fact]
    public void ACancelledRunStops()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => RateMeter.Measure(
            "work", () => { }, link: null, samples: 10, warmup: 0,
            cancellationToken: cancelled.Token));
    }

    // --- The decomposition ----------------------------------------------------------------------

    [Fact]
    public void WritesAndReadsAreProfiledSeparately()
    {
        // "It is slow" is not actionable. Splitting the operations is what turns a frame rate into
        // a decision about what to change.
        var link = Link(perOperation: Ms(1));

        link.Query("X");
        link.SerialPoll();

        var breakdown = RateMeter.BreakDown(link);
        var labels = breakdown.Select(p => p.Label).ToList();

        Assert.Contains("Write", labels);
        Assert.Contains("Read", labels);
        Assert.Contains("SerialPoll", labels);
    }

    [Fact]
    public void TransactionsWithNoRecordedDurationAreSkippedRatherThanCountedAsInstant()
    {
        // Not every link times its operations. A pile of zeroes would produce a flattering profile
        // that means nothing at all.
        var link = Link();

        link.Query("X");

        Assert.Empty(RateMeter.BreakDown(link));
    }

    [Fact]
    public void AProfileCanBeTakenFromPartWayThroughARun()
    {
        var link = Link(perOperation: Ms(1));

        link.Query("setup");
        var since = link.Transactions.Count;
        link.Query("measured");

        var breakdown = RateMeter.BreakDown(link, since);

        Assert.All(breakdown, p => Assert.Equal(1, p.Count));
    }

    // --- The question the framework exists to answer ---------------------------------------------

    [Fact]
    public void ItCanShowThatStreamingAFrameBeatsTheOneShotPath()
    {
        // The #87 argument, checked rather than asserted in a commit message: under a cost model
        // with a real per-byte term, a streamed frame should beat a one-shot capture, because it
        // skips the setup writes and the preamble query.
        var link = SimulatedBench.LinkFor("Rigol DS1104Z", "SIM::scope::INSTR");
        link.Latency = new SimulatedLatency(Ms(1), TimeSpan.FromTicks(20));

        var scope = new RigolDs1104Z(link);

        scope.ReadWaveformStreaming(1);

        var streamed = RateMeter.Measure(
            "streamed", () => scope.ReadWaveformStreaming(1), link, samples: 3, warmup: 1);

        var oneShot = RateMeter.Measure(
            "one-shot", () => scope.ReadWaveform(1), link, samples: 3, warmup: 1);

        Assert.True(streamed.Median < oneShot.Median,
            $"Streamed {streamed.Median.TotalMilliseconds:0.##} ms vs one-shot "
            + $"{oneShot.Median.TotalMilliseconds:0.##} ms — the cached preamble should be winning.");
    }
}
