namespace HP8340B.Instruments.Timing;

/// <summary>
/// The distribution of how long an operation took, over a number of repeats.
///
/// <para><b>Why a distribution rather than an average.</b> The question these figures exist to
/// answer is whether somebody can turn a pot while watching a trace, and that is decided by the
/// bad frames, not the typical ones. A view that runs at 20 Hz and stalls for 400 ms every couple
/// of seconds has an excellent mean and feels broken: the operator's hand and the trace come
/// apart exactly when they are trying to be precise. So <see cref="P95"/> and <see cref="Max"/>
/// are reported beside the median and none of them is allowed to hide the others.</para>
///
/// <para><b>Why it records whether it is simulated.</b> A simulated link can be given any latency
/// at all, which makes the framework testable offline and makes its output worthless as evidence.
/// A figure measured against a simulator is not a measurement of anything, and
/// <see cref="IsSimulated"/> travels with the number so nothing downstream can quietly present it
/// as a bench result.</para>
/// </summary>
/// <param name="Label">What was being timed.</param>
/// <param name="Count">How many samples the figures come from, after any warm-up was discarded.</param>
/// <param name="Min">Fastest sample.</param>
/// <param name="Median">Middle sample.</param>
/// <param name="P95">95th percentile — the stall that spoils a live view.</param>
/// <param name="Max">Slowest sample.</param>
/// <param name="Total">Wall-clock time the samples took together.</param>
/// <param name="IsSimulated">True if this came from a simulated link, and is therefore not evidence.</param>
public sealed record LatencyProfile(
    string Label,
    int Count,
    TimeSpan Min,
    TimeSpan Median,
    TimeSpan P95,
    TimeSpan Max,
    TimeSpan Total,
    bool IsSimulated)
{
    /// <summary>
    /// Operations per second at the median. The optimistic figure, and the one to quote least:
    /// see <see cref="SustainedPerSecond"/>.
    /// </summary>
    public double MedianPerSecond => PerSecond(Median);

    /// <summary>
    /// Operations per second at the 95th percentile — what the slow frames allow.
    ///
    /// <para>This is the honest number for "can I tune against this", because the operator
    /// experiences the slow frames, not the average of all of them.</para>
    /// </summary>
    public double SustainedPerSecond => PerSecond(P95);

    private static double PerSecond(TimeSpan t) =>
        t <= TimeSpan.Zero ? double.PositiveInfinity : 1.0 / t.TotalSeconds;

    /// <summary>
    /// Builds a profile from raw samples.
    ///
    /// <para>Percentiles use the nearest-rank method on the sorted samples: no interpolation, so
    /// every figure reported is a duration that actually occurred rather than one computed
    /// between two that did. With the small sample counts a bench run produces, an interpolated
    /// p95 is a number nothing ever measured.</para>
    /// </summary>
    /// <param name="label">What was timed.</param>
    /// <param name="samples">The durations. Must not be empty.</param>
    /// <param name="isSimulated">True if these came from a simulated link.</param>
    public static LatencyProfile From(
        string label, IEnumerable<TimeSpan> samples, bool isSimulated)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(samples);

        var sorted = samples.OrderBy(t => t).ToArray();

        if (sorted.Length == 0)
            throw new ArgumentException(
                $"No samples for '{label}'. An empty profile would report a rate of zero or "
                + "infinity, either of which reads like a result.",
                nameof(samples));

        var total = TimeSpan.Zero;
        foreach (var t in sorted) total += t;

        return new LatencyProfile(
            label,
            sorted.Length,
            sorted[0],
            NearestRank(sorted, 0.50),
            NearestRank(sorted, 0.95),
            sorted[^1],
            total,
            isSimulated);
    }

    /// <summary>Nearest-rank percentile: rank = ceil(p * n), clamped into the array.</summary>
    internal static TimeSpan NearestRank(TimeSpan[] sorted, double percentile)
    {
        var rank = (int)Math.Ceiling(percentile * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }

    /// <summary>One line for the CLI table and the session summary.</summary>
    public string Describe()
    {
        var line = $"{Label}: median {Ms(Median)}, p95 {Ms(P95)}, max {Ms(Max)} "
                   + $"over {Count} samples ({SustainedPerSecond:0.#}/s sustained)";

        return IsSimulated ? line + "  [SIMULATED — not a measurement]" : line;
    }

    private static string Ms(TimeSpan t) => $"{t.TotalMilliseconds:0.##} ms";
}
