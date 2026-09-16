using System.Globalization;
using HP8340B.Instruments.Model;

namespace HP8340B.Measurements.PhaseNoise;

/// <summary>One point of a phase-noise trace.</summary>
/// <param name="OffsetHz">Offset from the carrier.</param>
/// <param name="DbcPerHz">Single-sideband phase noise, dBc/Hz.</param>
public sealed record PnPoint(double OffsetHz, double DbcPerHz);

/// <summary>
/// A phase-noise trace exported by KE5FX PN.EXE, with everything needed to defend it later
/// (repo rule 3).
/// </summary>
/// <param name="Points">Offset against dBc/Hz.</param>
/// <param name="CarrierHz">DUT carrier the trace was taken at.</param>
/// <param name="CarrierDbm">DUT level.</param>
/// <param name="AnalyzerSettings">8563E settings in force, free text from the session.</param>
/// <param name="SourceFile">Where it was imported from.</param>
/// <param name="ImportedAt">When.</param>
public sealed record PnTrace(
    IReadOnlyList<PnPoint> Points,
    double CarrierHz,
    double CarrierDbm,
    string? AnalyzerSettings,
    string? SourceFile,
    DateTime ImportedAt)
{
    /// <summary>Phase noise at <paramref name="offsetHz"/>, interpolated in log offset.</summary>
    public double At(double offsetHz)
    {
        if (Points.Count == 0)
            throw new InvalidOperationException("The trace has no points.");

        var ordered = Points.OrderBy(p => p.OffsetHz).ToList();

        if (offsetHz <= ordered[0].OffsetHz) return ordered[0].DbcPerHz;
        if (offsetHz >= ordered[^1].OffsetHz) return ordered[^1].DbcPerHz;

        for (var i = 1; i < ordered.Count; i++)
        {
            if (offsetHz > ordered[i].OffsetHz) continue;

            var (low, high) = (ordered[i - 1], ordered[i]);

            // Phase-noise plots are log-log, so interpolate in log offset rather than linear —
            // between a decade-wide pair the two differ by several dB.
            var fraction = (Math.Log10(offsetHz) - Math.Log10(low.OffsetHz))
                           / (Math.Log10(high.OffsetHz) - Math.Log10(low.OffsetHz));

            return low.DbcPerHz + fraction * (high.DbcPerHz - low.DbcPerHz);
        }

        throw new InvalidOperationException("Unreachable: the offset is inside the trace.");
    }
}

/// <summary>
/// Reads the files PN.EXE writes.
///
/// <para><b>The two formats are not the same shape, and that matters.</b> Taken from PN's own
/// source (`pn.cpp`, CMD_export):</para>
/// <list type="bullet">
/// <item><c>.TXT</c> writes <c>"%.20lf, %.20lf\n"</c> per point — one pair per line.</item>
/// <item><c>.CSV</c> writes <c>"%.20lf,%.20lf"</c> per point with a bare <c>","</c> between
/// points and <b>no line breaks at all</b>. The whole trace is one line of 2N comma-separated
/// values, not N rows of two columns.</item>
/// </list>
/// <para>An importer written to the usual CSV shape would read a PN .CSV as a single row and
/// either fail or, worse, misalign offsets against amplitudes. So this parses both by flattening
/// every number in the file and pairing them, which is correct for either.</para>
/// </summary>
public static class PnImport
{
    /// <summary>Reads a PN export, .TXT or .CSV.</summary>
    public static PnTrace Read(
        string path, double carrierHz, double carrierDbm, string? analyzerSettings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var points = Parse(File.ReadAllText(path));

        return new PnTrace(points, carrierHz, carrierDbm, analyzerSettings, path, DateTime.UtcNow);
    }

    /// <summary>
    /// Parses the contents of a PN export. Both formats reduce to a flat list of numbers that
    /// pair up as offset, amplitude.
    /// </summary>
    public static IReadOnlyList<PnPoint> Parse(string contents)
    {
        ArgumentNullException.ThrowIfNull(contents);

        var numbers = contents
            .Split([',', '\n', '\r', '\t', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? v
                : throw new InvalidOperationException(
                    $"'{t}' in a PN export is not a number. PN writes plain offset/amplitude pairs "
                    + "with no header, so anything else means the file is not a PN export or has "
                    + "been edited."))
            .ToList();

        if (numbers.Count == 0)
            throw new InvalidOperationException("The PN export is empty.");

        if (numbers.Count % 2 != 0)
            throw new InvalidOperationException(
                $"The PN export has {numbers.Count} values, which is odd. Every point is an "
                + "offset and an amplitude, so an odd count means the file is truncated.");

        var points = new List<PnPoint>(numbers.Count / 2);

        for (var i = 0; i < numbers.Count; i += 2)
            points.Add(new PnPoint(numbers[i], numbers[i + 1]));

        return points;
    }
}

/// <summary>How a phase-noise point compares with the manual's limit.</summary>
public enum PnVerdict
{
    /// <summary>Composite is below the limit, so the DUT alone must be too.</summary>
    Pass,

    /// <summary>Composite is above the limit and the baseline is not far enough below to subtract.</summary>
    Inconclusive,

    /// <summary>The DUT's own noise, recovered by subtracting the baseline, is above the limit.</summary>
    Fail,
}

/// <summary>One offset judged against the 4-9 limit.</summary>
/// <param name="OffsetHz">Offset.</param>
/// <param name="CompositeDbcPerHz">What was measured: DUT plus analyzer.</param>
/// <param name="BaselineDbcPerHz">The analyzer's own noise here, if a baseline was supplied.</param>
/// <param name="LimitDbcPerHz">The manual's limit.</param>
/// <param name="Verdict">The outcome.</param>
/// <param name="DutOnlyDbcPerHz">DUT noise with the baseline subtracted, where that was valid.</param>
/// <param name="Reason">Why this verdict.</param>
public sealed record PnJudgement(
    double OffsetHz,
    double CompositeDbcPerHz,
    double? BaselineDbcPerHz,
    double LimitDbcPerHz,
    PnVerdict Verdict,
    double? DutOnlyDbcPerHz,
    string Reason)
{
    /// <summary>The traceability this judgement can claim.</summary>
    public TraceabilityClass Traceability => Verdict switch
    {
        // A composite below the limit cannot be anything but a pass: the DUT alone is lower still.
        PnVerdict.Pass => TraceabilityClass.Spec,

        // A subtraction against a baseline 10 dB down is a real measurement, but a derived one.
        PnVerdict.Fail => BaselineDbcPerHz is null ? TraceabilityClass.Typical : TraceabilityClass.Relative,

        _ => TraceabilityClass.NotPossible,
    };
}

/// <summary>
/// The conservative pass rule for 4-9 on this bench.
///
/// <para>The manual's two-source quadrature method needs an LNA, a mixer and a 3585A, none of
/// which are here. PN.EXE measures the DUT directly instead, so what comes back is <b>DUT plus
/// analyzer</b> noise. That makes one direction of the comparison sound and the other not:</para>
///
/// <list type="bullet">
/// <item>Composite <b>below</b> the limit is an unambiguous pass. Adding the analyzer's noise can
/// only have made the figure worse, so the DUT alone is lower still.</item>
/// <item>Composite <b>above</b> the limit proves nothing on its own — it could be the analyzer.
/// It is only a failure if the analyzer's own baseline is far enough below the composite for the
/// subtraction to mean something.</item>
/// </list>
///
/// <para>Ten dB is the threshold, and it is not arbitrary: at 10 dB down the baseline contributes
/// about 0.4 dB to the composite, so subtracting it recovers the DUT to well inside a dB. Closer
/// than that and the subtraction amplifies the baseline's own uncertainty faster than it removes
/// the error.</para>
/// </summary>
public static class PnPassRule
{
    /// <summary>How far below the composite a baseline has to be before it can be subtracted.</summary>
    public const double BaselineMarginDb = 10.0;

    /// <summary>Judges one offset.</summary>
    /// <param name="offsetHz">Offset from the carrier.</param>
    /// <param name="compositeDbcPerHz">Measured, DUT plus analyzer.</param>
    /// <param name="limitDbcPerHz">The manual's limit at this offset and frequency.</param>
    /// <param name="baselineDbcPerHz">The analyzer's own noise here, if it has been measured.</param>
    public static PnJudgement Judge(
        double offsetHz,
        double compositeDbcPerHz,
        double limitDbcPerHz,
        double? baselineDbcPerHz = null)
    {
        // Phase noise is negative dBc/Hz, so "below the limit" means more negative.
        if (compositeDbcPerHz <= limitDbcPerHz)
            return new PnJudgement(
                offsetHz, compositeDbcPerHz, baselineDbcPerHz, limitDbcPerHz,
                PnVerdict.Pass, null,
                $"Composite {compositeDbcPerHz:0.0} dBc/Hz is at or below the {limitDbcPerHz:0.0} "
                + "limit. The analyzer's own noise is in that figure and can only have made it "
                + "worse, so the DUT alone is lower still.");

        if (baselineDbcPerHz is not { } baseline)
            return new PnJudgement(
                offsetHz, compositeDbcPerHz, null, limitDbcPerHz,
                PnVerdict.Inconclusive, null,
                $"Composite {compositeDbcPerHz:0.0} dBc/Hz is above the {limitDbcPerHz:0.0} limit, "
                + "and there is no analyzer baseline at this offset. The excess could be the "
                + "analyzer rather than the DUT. Measure the 8563E against the E4438C here.");

        var margin = compositeDbcPerHz - baseline;

        if (margin < BaselineMarginDb)
            return new PnJudgement(
                offsetHz, compositeDbcPerHz, baseline, limitDbcPerHz,
                PnVerdict.Inconclusive, null,
                $"Composite {compositeDbcPerHz:0.0} dBc/Hz is above the {limitDbcPerHz:0.0} limit, "
                + $"but the analyzer baseline is {baseline:0.0} dBc/Hz — only {margin:0.0} dB "
                + $"below, against the {BaselineMarginDb:0.#} dB this rule needs before "
                + "subtracting. The analyzer is a large enough part of the measurement that "
                + "removing it would amplify its own uncertainty faster than it removes the error.");

        var dutOnly = SubtractInPower(compositeDbcPerHz, baseline);

        return dutOnly <= limitDbcPerHz
            ? new PnJudgement(
                offsetHz, compositeDbcPerHz, baseline, limitDbcPerHz,
                PnVerdict.Pass, dutOnly,
                $"Composite {compositeDbcPerHz:0.0} dBc/Hz was above the limit, but with the "
                + $"analyzer baseline ({baseline:0.0} dBc/Hz, {margin:0.0} dB down) subtracted in "
                + $"power the DUT alone is {dutOnly:0.0} dBc/Hz, inside the {limitDbcPerHz:0.0} "
                + "limit.")
            : new PnJudgement(
                offsetHz, compositeDbcPerHz, baseline, limitDbcPerHz,
                PnVerdict.Fail, dutOnly,
                $"With the analyzer baseline ({baseline:0.0} dBc/Hz, {margin:0.0} dB down) "
                + $"subtracted in power, the DUT alone is {dutOnly:0.0} dBc/Hz, above the "
                + $"{limitDbcPerHz:0.0} limit.");
    }

    /// <summary>
    /// Removes the baseline from the composite in power terms, which is how two uncorrelated
    /// noise contributions actually add.
    /// </summary>
    public static double SubtractInPower(double compositeDbcPerHz, double baselineDbcPerHz)
    {
        var composite = Math.Pow(10, compositeDbcPerHz / 10.0);
        var baseline = Math.Pow(10, baselineDbcPerHz / 10.0);
        var difference = composite - baseline;

        if (difference <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(baselineDbcPerHz), baselineDbcPerHz,
                "The baseline is at or above the composite, so there is nothing left after "
                + "subtracting it. That means the measurement is the analyzer, not the DUT.");

        return 10.0 * Math.Log10(difference);
    }

    /// <summary>Judges a whole trace against a limit function and an optional baseline.</summary>
    public static IReadOnlyList<PnJudgement> JudgeTrace(
        PnTrace trace,
        IReadOnlyList<double> offsetsHz,
        Func<double, double> limitAt,
        PnTrace? baseline = null)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(offsetsHz);
        ArgumentNullException.ThrowIfNull(limitAt);

        return offsetsHz
            .Select(offset => Judge(
                offset,
                trace.At(offset),
                limitAt(offset),
                baseline?.At(offset)))
            .ToList();
    }
}
