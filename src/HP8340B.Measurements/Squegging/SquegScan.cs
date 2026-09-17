using HP8340B.Instruments;
using HP8340B.Instruments.Model;

namespace HP8340B.Measurements.Squegging;

/// <summary>How serious a spurious response is.</summary>
public enum SpurSeverity
{
    /// <summary>Below the warning threshold. Recorded, not acted on.</summary>
    None,

    /// <summary>
    /// Real, and <b>expected</b>: band-1 squegging at maximum unleveled power.
    ///
    /// <para>The manual is explicit that this is a function of SYTM input power, occurs only when
    /// maximum unleveled power is requested, and <b>cannot be adjusted out</b> — there is no A24
    /// pot for band 1. Reporting it as a fault would send somebody hunting for a control that does
    /// not exist, which is why it has a severity of its own rather than being folded into Warn or
    /// silently dropped.</para>
    /// </summary>
    Expected,

    /// <summary>Above the warning threshold — worth looking at.</summary>
    Warn,

    /// <summary>Well above — the SRD bias needs backing off.</summary>
    Strong,

    /// <summary>
    /// Real, and <b>not adjustable</b>: found with the ALC asking for more than the maximum
    /// <i>specified</i> leveled power for the band.
    ///
    /// <para>5-16 steps 32-34 draw this line explicitly. If the requested ALC level is at or below
    /// maximum specified leveled power a response is a fault and the leveled bias pot wants
    /// backing off; above it, "what you are seeing is probably the RF output going unleveled and
    /// cannot be adjusted out". Turning the pot to chase it would leave the instrument worse than
    /// it started.</para>
    ///
    /// <para>Last in the enum only because it is the later addition. Nothing here depends on
    /// that: every use is an equality or a pattern match, never an order comparison, and the
    /// session writer serialises enums through a <c>JsonStringEnumConverter</c>, so the name is
    /// what reaches disk and the number carries no meaning to preserve.</para>
    /// </summary>
    Unleveled,
}

/// <summary>One spurious response found beside a carrier.</summary>
/// <param name="CarrierHz">Where the DUT was set.</param>
/// <param name="OffsetHz">Offset of the response from the carrier, signed.</param>
/// <param name="Dbc">Level relative to the carrier.</param>
/// <param name="Severity">How serious.</param>
/// <param name="Pot">
/// The A24 pot that owns this frequency — <see cref="Bands.UnleveledPotFor"/> for the 5-14 scan,
/// <see cref="Bands.LeveledPotFor"/> for the 5-16 one. Null in bands 0 and 1, which have neither.
/// </param>
/// <param name="AnalyzerNoiseFloorDbc">
/// The analyzer's own floor at this point, relative to the carrier. A spur is only a finding if it
/// is above this; recording it is what makes the result defensible later (rule 3).
/// </param>
/// <param name="RequestedDbm">
/// What the DUT was being asked for when the response appeared. 5-16 step 32 turns on exactly this
/// — "examine the ENTRY DISPLAY to determine the requested ALC level" — because whether the level
/// is at or above maximum specified leveled power is what decides if there is anything to adjust.
/// The leveled scan visits one carrier at several levels, so a finding without its level cannot be
/// acted on.
/// </param>
public sealed record SquegFinding(
    double CarrierHz,
    double OffsetHz,
    double Dbc,
    SpurSeverity Severity,
    string? Pot,
    double AnalyzerNoiseFloorDbc,
    double RequestedDbm)
{
    /// <summary>True if this needs somebody to turn something.</summary>
    public bool IsActionable => Severity is SpurSeverity.Warn or SpurSeverity.Strong;

    /// <summary>A line naming the frequency, the level, the response and what to turn.</summary>
    public string Describe()
    {
        var head = $"{CarrierHz / 1e9:0.000} GHz at {RequestedDbm:+0.#;-0.#} dBm: {Dbc:0.0} dBc at "
                   + $"{OffsetHz / 1e6:+0.0;-0.0} MHz [{Severity}]";

        if (Severity == SpurSeverity.Unleveled)
            return head + " — ABOVE maximum specified leveled power. Per 5-16 step 32 this is "
                        + "probably the RF output going unleveled, not squegging, and cannot be "
                        + "adjusted out. Do not turn the pot for this one.";

        if (Severity == SpurSeverity.Expected)
            return head + " — EXPECTED. Band-1 squegging is a function of SYTM input power and "
                        + "occurs only at maximum unleveled power. The manual says it cannot be "
                        + "adjusted out, and there is no A24 pot for band 1. Not a fault; do not "
                        + "go looking for a control.";

        return head + (Pot is null
            ? " — no SRD bias pot in this band."
            : $" — turn {Pot} counter-clockwise.");
    }
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

    /// <summary>Findings that need somebody to act. Excludes the expected band-1 case.</summary>
    public IReadOnlyList<SquegFinding> Actionable =>
        Findings.Where(f => f.IsActionable).ToList();

    /// <summary>
    /// Findings that are real and expected — band 1 at maximum unleveled power. Reported so the
    /// record is complete, kept out of <see cref="Actionable"/> so nobody chases them.
    /// </summary>
    public IReadOnlyList<SquegFinding> Expected =>
        Findings.Where(f => f.Severity == SpurSeverity.Expected).ToList();

    /// <summary>The pots implicated, in the order they should be worked.</summary>
    public IReadOnlyList<string> PotsToAdjust =>
        Findings.Where(f => f.IsActionable && f.Pot is not null)
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
/// <para><b>Band 1 is not in the default set, and is not refused either.</b> Its squegging is a
/// function of SYTM input power, appears only at maximum unleveled power, and cannot be adjusted
/// out — there is no A24 pot for it. So a response there is classified
/// <see cref="SpurSeverity.Expected"/> rather than reported as a fault: real, recorded, and kept
/// out of the list of things to go and turn. Band 0 <i>is</i> refused, because being heterodyne it
/// has no SRD and no multiplication and cannot squeg at all. See M1-08.</para>
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
            // Band 0 is heterodyne — no SRD, no multiplication, nothing that can squeg — so
            // scanning it is meaningless rather than merely unhelpful.
            if (id is BandId.Band0)
                throw new ArgumentException(
                    "Band 0 is heterodyne: the YO is mixed with the A8 oscillator, so there is no "
                    + "step recovery diode and no multiplication to squeg. Steps 70-80 walk bands "
                    + "2, 3 and 4.",
                    nameof(bands));

            // Band 1 IS scannable, and is not in the default set. Its squegging is real but
            // expected and unadjustable, so it is classified as Expected rather than refused —
            // see M1-08 and SpurSeverity.Expected.

            var band = Bands.Get(id);

            // Indexed, not accumulated. Adding 0.1 GHz repeatedly from 2.3 drifts far enough that
            // the last point of band 1 rounds to exactly 7.000 GHz -- which belongs to band 2, and
            // would be handed band 2's pot. The loop condition never sees it because the drift is
            // in the other direction from the comparison.
            var steps = (int)Math.Ceiling((band.StopGHz - band.StartGHz) * 1e9 / stepHz);

            for (var i = 0; i < steps; i++)
            {
                var hz = Math.Round(band.StartGHz * 1e9 + i * stepHz);

                // Belt and braces: a point must lie in the band that generated it, or the pot
                // lookup would be wrong.
                if (Bands.ForFrequency(hz / 1e9)?.Id == id) points.Add(hz);
            }
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
    /// Classifies a response knowing which band it is in and how hard the DUT is being driven.
    ///
    /// <para>Band 1 is the special case (M1-08). Its squegging is a function of SYTM input power
    /// and occurs only when maximum unleveled power is asked for, so a response there is
    /// <see cref="SpurSeverity.Expected"/> rather than a fault — the manual says it cannot be
    /// adjusted out, and <see cref="Bands.UnleveledPotFor"/> returns null for it because there is
    /// no control. <b>Below</b> maximum specified leveled power band 1 should not squeg at all, so
    /// a response there is classified normally and is worth knowing about.</para>
    /// </summary>
    /// <param name="carrierHz">Where the DUT is set.</param>
    /// <param name="requestedDbm">What it was asked for.</param>
    /// <param name="option">Output option, which sets the Table 4-9 leveled limits.</param>
    /// <param name="dbc">The response level.</param>
    /// <param name="noiseFloorDbc">The analyzer's floor here.</param>
    public static SpurSeverity ClassifyInBand(
        double carrierHz,
        double requestedDbm,
        InstrumentOption option,
        double dbc,
        double noiseFloorDbc)
    {
        var plain = Classify(dbc, noiseFloorDbc);

        if (plain == SpurSeverity.None) return plain;

        var band = Bands.ForFrequency(carrierHz / 1e9);
        if (band?.Id != BandId.Band1) return plain;

        // Above maximum specified leveled power, band 1 is doing what the manual says it does.
        var maxSpecified = MaxLeveledPower.ForFrequency(option, carrierHz / 1e9);

        return requestedDbm > maxSpecified ? SpurSeverity.Expected : plain;
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
    /// <param name="requestedDbm">
    /// What the DUT is being asked for. Needed because band 1's squegging is only expected at
    /// maximum unleveled power — see <see cref="ClassifyInBand"/>.
    /// </param>
    /// <param name="option">Output option, for the Table 4-9 leveled limits.</param>
    public static SquegScanResult Scan(
        IReadOnlyList<double> points,
        Func<double, (IReadOnlyList<(double OffsetHz, double Dbc)> Responses, double NoiseFloorDbc)> measure,
        double requestedDbm = 20.0,
        InstrumentOption option = InstrumentOption.Standard,
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

                var severity = ClassifyInBand(carrierHz, requestedDbm, option, dbc, floor);
                if (severity == SpurSeverity.None) continue;

                findings.Add(new SquegFinding(
                    carrierHz, offsetHz, dbc, severity,
                    Bands.UnleveledPotFor(carrierHz / 1e9),
                    floor, requestedDbm));
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
                : findings.All(f => f.Severity == SpurSeverity.Expected)
                    ? $"{findings.Count} response(s), all of them the expected band-1 case at "
                      + "maximum unleveled power. Nothing to adjust."
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
