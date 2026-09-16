using HP8340B.Instruments;
using HP8340B.Instruments.Model;

namespace HP8340B.Measurements.Squegging;

/// <summary>How serious a spurious response is.</summary>
public enum SpurSeverity
{
    /// <summary>Below the warning threshold. Recorded, not acted on.</summary>
    None,

    /// <summary>Above the warning threshold — worth looking at.</summary>
    Warn,

    /// <summary>Well above — the SRD bias needs backing off.</summary>
    Strong,
}

/// <summary>One spurious response found beside a carrier.</summary>
/// <param name="CarrierHz">Where the DUT was set.</param>
/// <param name="OffsetHz">Offset of the response from the carrier, signed.</param>
/// <param name="Dbc">Level relative to the carrier.</param>
/// <param name="Severity">How serious.</param>
/// <param name="Pot">
/// The A24 pot that owns this frequency, from the verified <see cref="Bands.UnleveledPotFor"/>
/// map — null in bands 0 and 1, which have none.
/// </param>
/// <param name="AnalyzerNoiseFloorDbc">
/// The analyzer's own floor at this point, relative to the carrier. A spur is only a finding if it
/// is above this; recording it is what makes the result defensible later (rule 3).
/// </param>
public sealed record SquegFinding(
    double CarrierHz,
    double OffsetHz,
    double Dbc,
    SpurSeverity Severity,
    string? Pot,
    double AnalyzerNoiseFloorDbc)
{
    /// <summary>A line naming the frequency, the response and what to turn.</summary>
    public string Describe() =>
        $"{CarrierHz / 1e9:0.000} GHz: {Dbc:0.0} dBc at {OffsetHz / 1e6:+0.0;-0.0} MHz "
        + $"[{Severity}]"
        + (Pot is null
            ? " — no SRD bias pot in this band."
            : $" — turn {Pot} counter-clockwise.");
}

/// <summary>What one scan found.</summary>
/// <param name="Findings">Everything above the warning threshold.</param>
/// <param name="PointsScanned">How many carriers were looked at.</param>
/// <param name="Traceability">How far the result can be trusted.</param>
/// <param name="Note">Anything qualifying it.</param>
public sealed record SquegScanResult(
    IReadOnlyList<SquegFinding> Findings,
    int PointsScanned,
    TraceabilityClass Traceability,
    string? Note = null)
{
    /// <summary>Findings bad enough to need a pot moved.</summary>
    public IReadOnlyList<SquegFinding> Strong =>
        Findings.Where(f => f.Severity == SpurSeverity.Strong).ToList();

    /// <summary>The pots implicated, in the order they should be worked.</summary>
    public IReadOnlyList<string> PotsToAdjust =>
        Findings.Where(f => f.Severity != SpurSeverity.None && f.Pot is not null)
            .Select(f => f.Pot!)
            .Distinct()
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
}

/// <summary>
/// The unleveled squegging test of 5-14 steps 70-80, done directly on the 8563E.
///
/// <para><b>What the manual does and what this does instead.</b> The manual mixes the DUT against
/// a second 8340B 600 MHz below and views the IF on an 8566B, because in 1985 that was how you
/// looked at 26.5 GHz. This bench has an 8563E that reaches 26.5 GHz directly, so the carrier is
/// looked at in place. That removes the mixing products the manual has to warn about — which is
/// why its caveat, "if the control is adjusted and there is no effect on the response, the
/// response is probably a mixing product", <b>does not apply here</b>. A response found by this
/// scan is on the DUT's output.</para>
///
/// <para><b>Band 1 is not scanned and that is deliberate.</b> Its squegging is a function of SYTM
/// input power, appears only at maximum unleveled power, and cannot be adjusted out — there is no
/// A24 pot for it. Scanning it would produce findings naming no pot, which reads as a fault
/// nobody can fix. See M1-08.</para>
/// </summary>
public static class SquegScanner
{
    /// <summary>The bands 5-14 steps 70-80 actually walks.</summary>
    public static readonly IReadOnlyList<BandId> ScannedBands =
        [BandId.Band2, BandId.Band3, BandId.Band4];

    /// <summary>
    /// Step size the manual uses: 100 MHz through each band.
    /// </summary>
    public const double DefaultStepHz = 100e6;

    /// <summary>
    /// How far either side of the carrier to look. The manual's IF window is 590-800 MHz around a
    /// 600 MHz LO offset, so plus or minus about 200 MHz; 300 MHz is a little wider and matches
    /// what the 8563E can show at a sensible sweep time.
    /// </summary>
    public const double DefaultSpanHz = 600e6;

    /// <summary>
    /// How close to the carrier to ignore. The carrier itself and its skirts are not spurious
    /// responses, and a peak search would find the carrier every time without this.
    /// </summary>
    public const double CarrierExclusionHz = 5e6;

    /// <summary>
    /// Level above which a response is worth looking at.
    ///
    /// <para>PROVISIONAL: never checked against an instrument. The manual describes squegging as
    /// showing "a higher-amplitude spurious response" than the mixing products, and gives no
    /// number — it is an eyeball test against a display. These two thresholds turn that into
    /// something measurable, and what a healthy 8340B actually shows here is unknown.</para>
    /// </summary>
    public const double WarnDbc = -40.0;

    /// <summary>
    /// Level above which the SRD bias needs backing off.
    ///
    /// <para>PROVISIONAL: never checked against an instrument. See <see cref="WarnDbc"/>.</para>
    /// </summary>
    public const double StrongDbc = -25.0;

    /// <summary>
    /// The frequencies the scan visits, 100 MHz apart through the multiplying bands.
    /// </summary>
    public static IReadOnlyList<double> ScanPoints(
        IEnumerable<BandId>? bands = null, double stepHz = DefaultStepHz)
    {
        if (stepHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(stepHz), stepHz, "Step must be positive.");

        var points = new List<double>();

        foreach (var id in bands ?? ScannedBands)
        {
            if (id is BandId.Band0 or BandId.Band1)
                throw new ArgumentException(
                    $"Band {(int)id} is not part of the unleveled squegging test. Steps 70-80 walk "
                    + "bands 2, 3 and 4. Band 1's squegging is a function of SYTM input power, "
                    + "happens only at maximum unleveled power and cannot be adjusted out — "
                    + "scanning it would produce findings naming no pot. See M1-08.",
                    nameof(bands));

            var band = Bands.Get(id);

            for (var ghz = band.StartGHz; ghz < band.StopGHz; ghz += stepHz / 1e9)
                points.Add(Math.Round(ghz * 1e9));
        }

        return points;
    }

    /// <summary>
    /// Classifies one response.
    /// </summary>
    /// <param name="dbc">Its level relative to the carrier.</param>
    /// <param name="noiseFloorDbc">
    /// The analyzer's own floor here. A response at or below it is the analyzer, not the DUT.
    /// </param>
    public static SpurSeverity Classify(double dbc, double noiseFloorDbc)
    {
        // Below the floor it is not a measurement of anything.
        if (dbc <= noiseFloorDbc) return SpurSeverity.None;

        if (dbc >= StrongDbc) return SpurSeverity.Strong;
        if (dbc >= WarnDbc) return SpurSeverity.Warn;

        return SpurSeverity.None;
    }

    /// <summary>
    /// True if <paramref name="offsetHz"/> is close enough to the carrier to be the carrier.
    /// </summary>
    public static bool IsCarrier(double offsetHz, double exclusionHz = CarrierExclusionHz) =>
        Math.Abs(offsetHz) <= exclusionHz;

    /// <summary>
    /// Whether a response at <paramref name="carrierHz"/> plus <paramref name="offsetHz"/> is a
    /// harmonic or subharmonic of the carrier, which is not a squegging product.
    /// </summary>
    /// <remarks>
    /// The 8340B's own harmonics are specified and expected; finding one and blaming the SRD bias
    /// would send somebody to turn a pot that has nothing to do with it.
    /// </remarks>
    public static bool IsHarmonicallyRelated(
        double carrierHz, double offsetHz, double toleranceHz = 1e6)
    {
        var responseHz = carrierHz + offsetHz;
        if (responseHz <= 0) return false;

        // Harmonics and subharmonics up to the fourth, which is as far as the SYTM multiplies.
        for (var n = 2; n <= 4; n++)
        {
            if (Math.Abs(responseHz - carrierHz * n) <= toleranceHz) return true;
            if (Math.Abs(responseHz - carrierHz / n) <= toleranceHz) return true;
        }

        return false;
    }

    /// <summary>
    /// Runs the scan.
    /// </summary>
    /// <param name="points">Carriers to visit.</param>
    /// <param name="measure">
    /// Sets the DUT to a carrier and returns the responses beside it, as (offset, dBc) pairs,
    /// together with the analyzer's noise floor there. Kept as a delegate so the scan is testable
    /// against the M0-12 model and does not have to own the instruments.
    /// </param>
    /// <param name="cancellationToken">Stops a long scan.</param>
    public static SquegScanResult Scan(
        IReadOnlyList<double> points,
        Func<double, (IReadOnlyList<(double OffsetHz, double Dbc)> Responses, double NoiseFloorDbc)> measure,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(measure);

        var findings = new List<SquegFinding>();

        foreach (var carrierHz in points)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (responses, floor) = measure(carrierHz);

            foreach (var (offsetHz, dbc) in responses)
            {
                if (IsCarrier(offsetHz)) continue;
                if (IsHarmonicallyRelated(carrierHz, offsetHz)) continue;

                var severity = Classify(dbc, floor);
                if (severity == SpurSeverity.None) continue;

                findings.Add(new SquegFinding(
                    carrierHz, offsetHz, dbc, severity,
                    Bands.UnleveledPotFor(carrierHz / 1e9),
                    floor));
            }
        }

        return new SquegScanResult(
            findings,
            points.Count,
            // Spec, but bounded by the analyzer's dynamic range at each point — which is why the
            // floor is recorded against every finding rather than assumed.
            TraceabilityClass.Spec,
            findings.Count == 0
                ? $"No responses above {WarnDbc:0.#} dBc across {points.Count} points."
                : null);
    }

    /// <summary>
    /// The warning that must be shown before the run, because +20 dBm unleveled is about to appear
    /// at the DUT output (rule 5).
    /// </summary>
    public static string SafetyWarning(double requestedDbm) =>
        $"This scan asks the DUT for {requestedDbm:+0.#} dBm with the ALC bypassed, so the output "
        + "will be UNLEVELED at whatever the instrument can produce — up to about +20 dBm. Before "
        + "starting: confirm the 8563E has at least 10 dB of input attenuation or a characterised "
        + "pad, that no power sensor is connected without a pad (the 848x series and the 11792A "
        + "are refused above +17 dBm, the 478A above +7 dBm), and that the 5351A is padded. "
        + "Nothing downstream of the DUT should be assumed safe at this level.";
}
