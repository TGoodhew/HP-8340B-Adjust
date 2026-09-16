using HP8340B.Instruments.Model;

namespace HP8340B.Measurements.Squegging;

/// <summary>One point of a level ramp.</summary>
/// <param name="RequestedDbm">What the DUT was asked for.</param>
/// <param name="MeasuredDbm">What came out.</param>
/// <param name="Unleveled">Whether the UNLEVELED bit was set — extended status byte #2 bit 6.</param>
public sealed record RampPoint(double RequestedDbm, double MeasuredDbm, bool Unleveled);

/// <summary>Why the output stopped following the request.</summary>
public enum ReversalKind
{
    /// <summary>It did not — output rose with every step.</summary>
    None,

    /// <summary>
    /// Output stopped rising but did not fall, and UNLEVELED was set. The ALC has simply run out
    /// of range: the instrument is producing all it can. Expected, not a fault.
    /// </summary>
    OutOfRange,

    /// <summary>
    /// Output FELL as the request rose. This is the manual's "power reversal" and it is one of the
    /// three named symptoms of squegging.
    /// </summary>
    Reversal,
}

/// <summary>What a ramp at one frequency found.</summary>
/// <param name="Hz">Frequency.</param>
/// <param name="Kind">What happened.</param>
/// <param name="OnsetDbm">The requested level at which it started, where something did.</param>
/// <param name="WorstDropDb">The largest single fall in output, in dB.</param>
/// <param name="Pot">The A24 pot owning this frequency, from the verified map.</param>
/// <param name="Points">The ramp, kept so a marginal call can be re-examined.</param>
public sealed record MonotonicityResult(
    double Hz,
    ReversalKind Kind,
    double? OnsetDbm,
    double WorstDropDb,
    string? Pot,
    IReadOnlyList<RampPoint> Points)
{
    /// <summary>This is a relative check: only the deltas matter, not the absolute levels.</summary>
    public TraceabilityClass Traceability => TraceabilityClass.Relative;

    /// <summary>A line naming the frequency, what happened and what to turn.</summary>
    public string Describe() => Kind switch
    {
        ReversalKind.None =>
            $"{Hz / 1e9:0.000} GHz: monotonic across the ramp.",

        ReversalKind.OutOfRange =>
            $"{Hz / 1e9:0.000} GHz: output stopped rising at {OnsetDbm:+0.#;-0.#} dBm with "
            + "UNLEVELED set. That is the ALC out of range, not squegging — the instrument is "
            + "producing all it can here.",

        _ =>
            $"{Hz / 1e9:0.000} GHz: POWER REVERSAL from {OnsetDbm:+0.#;-0.#} dBm, worst fall "
            + $"{WorstDropDb:0.0} dB."
            + (Pot is null
                ? " No SRD bias pot in this band — see M1-08 if this is band 1."
                : $" Turn {Pot} counter-clockwise."),
    };
}

/// <summary>
/// The power-reversal test.
///
/// <para>Power reversal — output falling as requested output rises — is one of the three symptoms
/// the 5-14 footnote names, and it is the one that can be detected numerically rather than by
/// eye. Ramp the level and look for a step where the output went the wrong way.</para>
///
/// <para><b>The distinction that makes it useful.</b> An output that stops rising is not
/// necessarily squegging: past maximum leveled power the ALC simply runs out and the instrument
/// produces all it can, which is expected. The UNLEVELED bit tells the two apart — and without
/// consulting it this test would report every band's top end as a fault.</para>
/// </summary>
public static class MonotonicityTest
{
    /// <summary>
    /// How far a step has to fall before it counts as a reversal rather than measurement noise.
    ///
    /// <para>PROVISIONAL: never checked against an instrument. A power meter averaging properly
    /// should be well inside 0.1 dB at these levels, but what the DUT plus the measurement path
    /// actually wobbles by between two 1 dB steps is unmeasured.</para>
    /// </summary>
    public const double ReversalThresholdDb = 0.2;

    /// <summary>The ramp the issue specifies: -20 dBm to maximum in 1 dB steps.</summary>
    public static IReadOnlyList<double> DefaultRamp(double maximumDbm = 20.0)
    {
        if (maximumDbm <= -20.0)
            throw new ArgumentOutOfRangeException(
                nameof(maximumDbm), maximumDbm, "The ramp starts at -20 dBm.");

        var levels = new List<double>();
        for (var dbm = -20.0; dbm <= maximumDbm + 1e-9; dbm += 1.0) levels.Add(dbm);

        return levels;
    }

    /// <summary>
    /// The frequencies worth ramping: every band edge, and the half-band split points where one
    /// pot hands over to the next.
    ///
    /// <para>The splits matter because a pot set wrongly shows worst at the edge of its own half,
    /// where it is doing the most work — and those are exactly the frequencies a 100 MHz scan is
    /// least likely to land on.</para>
    /// </summary>
    public static IReadOnlyList<double> DefaultFrequencies()
    {
        var points = new List<double>();

        foreach (var band in Bands.All.Where(b => b.Id != BandId.Band0))
        {
            points.Add(band.StartGHz * 1e9);
            points.Add((band.StopGHz - 0.001) * 1e9);
        }

        // Where the half-band pots hand over: band 2 at 10 GHz, band 3 at 15, band 4 at 23.
        points.AddRange([10e9, 15e9, 23e9]);

        return points.Distinct().OrderBy(hz => hz).ToList();
    }

    /// <summary>
    /// Examines one ramp.
    /// </summary>
    public static MonotonicityResult Examine(double hz, IReadOnlyList<RampPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (points.Count < 2)
            throw new ArgumentException(
                "A monotonicity check needs at least two points; one level proves nothing about "
                + "which way the output moves.", nameof(points));

        var pot = Bands.UnleveledPotFor(hz / 1e9);

        var worstDrop = 0.0;
        double? reversalOnset = null;
        double? plateauOnset = null;

        for (var i = 1; i < points.Count; i++)
        {
            var previous = points[i - 1];
            var current = points[i];

            // Only interested in steps where MORE was asked for.
            if (current.RequestedDbm <= previous.RequestedDbm) continue;

            var change = current.MeasuredDbm - previous.MeasuredDbm;

            if (change <= -ReversalThresholdDb)
            {
                reversalOnset ??= previous.RequestedDbm;
                worstDrop = Math.Max(worstDrop, -change);
                continue;
            }

            // Stopped rising, but did not fall. Only interesting if the ALC has run out.
            if (change < ReversalThresholdDb && current.Unleveled)
                plateauOnset ??= previous.RequestedDbm;
        }

        if (reversalOnset is not null)
            return new MonotonicityResult(
                hz, ReversalKind.Reversal, reversalOnset, worstDrop, pot, points);

        return plateauOnset is not null
            ? new MonotonicityResult(hz, ReversalKind.OutOfRange, plateauOnset, 0, pot, points)
            : new MonotonicityResult(hz, ReversalKind.None, null, 0, pot, points);
    }

    /// <summary>
    /// Ramps each frequency and examines the result.
    /// </summary>
    /// <param name="frequencies">Frequencies to test.</param>
    /// <param name="levels">Levels to step through at each.</param>
    /// <param name="measure">
    /// Sets frequency and level, then returns the measured output and the UNLEVELED bit.
    /// </param>
    public static IReadOnlyList<MonotonicityResult> Run(
        IReadOnlyList<double> frequencies,
        IReadOnlyList<double> levels,
        Func<double, double, (double MeasuredDbm, bool Unleveled)> measure,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frequencies);
        ArgumentNullException.ThrowIfNull(levels);
        ArgumentNullException.ThrowIfNull(measure);

        var results = new List<MonotonicityResult>();

        foreach (var hz in frequencies)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var points = new List<RampPoint>();

            foreach (var dbm in levels)
            {
                var (measured, unleveled) = measure(hz, dbm);
                points.Add(new RampPoint(dbm, measured, unleveled));
            }

            results.Add(Examine(hz, points));
        }

        return results;
    }
}
