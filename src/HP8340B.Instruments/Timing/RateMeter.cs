using System.Diagnostics;
using HP8340B.Instruments.Visa;

namespace HP8340B.Instruments.Timing;

/// <summary>
/// Times repeated operations and decomposes what the bus spent its time on.
///
/// <para><b>What this is for.</b> Every rate figure in this project started as a guess reasoned
/// from transport characteristics — how fast a live view can run, what an analyzer point costs,
/// whether a given scope can be tuned against. Those guesses drive architecture rather than
/// pass/fail, which makes them more expensive to get wrong than an ordinary provisional number:
/// they decide which instrument goes in the hand-tuning loop. This replaces them with
/// measurements.</para>
///
/// <para><b>Why the decomposition matters.</b> "It is slow" is not actionable. A frame rate only
/// tells you what to do about it once you can see whether the time went into the transport, the
/// instrument, or the DUT's own sweep — the last of which nothing can beat and is a signal to stop
/// optimising. <see cref="BreakDown"/> splits the recorded transactions by operation so the answer
/// is visible rather than inferred.</para>
/// </summary>
public static class RateMeter
{
    /// <summary>
    /// Repeats discarded before timing starts.
    ///
    /// <para>The first calls are not representative and never will be: JIT, the VISA session's
    /// first exchange with the instrument, and on several instruments here a slower response to the
    /// first command after idle. Including them would make every profile's <see cref="LatencyProfile.Max"/>
    /// a measurement of start-up.</para>
    /// </summary>
    public const int DefaultWarmup = 3;

    /// <summary>
    /// Timed repeats. Enough that a 95th percentile means something — at 30 samples the p95 is the
    /// second-slowest, which is about the least that can honestly be called a tail.
    /// </summary>
    public const int DefaultSamples = 30;

    /// <summary>
    /// Runs <paramref name="operation"/> repeatedly and profiles how long it took.
    /// </summary>
    /// <param name="label">What is being timed, for the report.</param>
    /// <param name="operation">The operation. Run <paramref name="warmup"/> extra times first.</param>
    /// <param name="link">
    /// The link the operation runs over, used only to mark the result as simulated. Passing null
    /// marks the profile simulated, because a caller that cannot say where the work happened has
    /// not established that it happened on hardware.
    /// </param>
    /// <param name="samples">Timed repeats.</param>
    /// <param name="warmup">Untimed repeats first.</param>
    /// <param name="cancellationToken">Stops a long run.</param>
    public static LatencyProfile Measure(
        string label,
        Action operation,
        IInstrumentLink? link,
        int samples = DefaultSamples,
        int warmup = DefaultWarmup,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(operation);

        if (samples <= 0)
            throw new ArgumentOutOfRangeException(nameof(samples), samples, "Need at least one sample.");

        if (warmup < 0)
            throw new ArgumentOutOfRangeException(nameof(warmup), warmup, "Warm-up cannot be negative.");

        for (var i = 0; i < warmup; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            operation();
        }

        var timings = new TimeSpan[samples];
        var watch = new Stopwatch();

        for (var i = 0; i < samples; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            watch.Restart();
            operation();
            watch.Stop();

            timings[i] = watch.Elapsed;
        }

        // Null link means the caller could not say where this ran. Treated as simulated: claiming
        // a hardware measurement is the error that matters, so the doubt falls that way.
        return LatencyProfile.From(label, timings, link?.IsSimulated ?? true);
    }

    /// <summary>
    /// Profiles the link's recorded transactions by operation — writes, reads and serial polls
    /// separately.
    ///
    /// <para>This is the transport half of the decomposition. A read that costs far more than a
    /// write points at transfer size; writes and reads both slow points at the link or the
    /// instrument's command handling.</para>
    ///
    /// <para>Transactions with no recorded duration are skipped rather than counted as instant.
    /// Not every link times its operations, and a pile of zeroes would produce a flattering
    /// profile that means nothing.</para>
    /// </summary>
    /// <param name="link">The link whose transactions to analyse.</param>
    /// <param name="since">
    /// Ignore transactions recorded before this index, so one run can be profiled without the
    /// setup that preceded it.
    /// </param>
    public static IReadOnlyList<LatencyProfile> BreakDown(IInstrumentLink link, int since = 0)
    {
        ArgumentNullException.ThrowIfNull(link);

        if (since < 0)
            throw new ArgumentOutOfRangeException(nameof(since), since, "Index cannot be negative.");

        return link.Transactions
            .Skip(since)
            .Where(t => t.Elapsed > TimeSpan.Zero)
            .GroupBy(t => t.Operation)
            .OrderBy(g => g.Key)
            .Select(g => LatencyProfile.From(
                g.Key.ToString(), g.Select(t => t.Elapsed), link.IsSimulated))
            .ToList();
    }
}
