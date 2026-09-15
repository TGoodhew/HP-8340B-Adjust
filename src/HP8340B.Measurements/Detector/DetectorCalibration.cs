using HP8340B.Instruments.Model;

namespace HP8340B.Measurements.Detector;

/// <summary>
/// A crystal detector, and the few figures that decide whether a calibration of it is any good.
/// </summary>
/// <param name="Name">Model designation.</param>
/// <param name="MinHz">Lower frequency limit.</param>
/// <param name="MaxHz">Upper frequency limit.</param>
/// <param name="OutputImpedanceOhms">
/// Video-side source impedance. This is the figure that makes the load matter: it forms a divider
/// with whatever the detector is driving.
/// </param>
/// <param name="MinLoadForSensitivitySpecOhms">
/// Load below which the published sensitivity no longer applies.
/// </param>
/// <param name="MinLoadForSquareLawOhms">Load below which the square-law specification no longer applies.</param>
/// <param name="LowLevelSensitivityVoltsPerMilliwatt">Square-law sensitivity into a high load.</param>
/// <param name="MaxInputMilliwatts">Maximum operating input power.</param>
/// <param name="Source">Where these figures come from.</param>
public sealed record DetectorModel(
    string Name,
    double MinHz,
    double MaxHz,
    double OutputImpedanceOhms,
    double MinLoadForSensitivitySpecOhms,
    double MinLoadForSquareLawOhms,
    double LowLevelSensitivityVoltsPerMilliwatt,
    double MaxInputMilliwatts,
    string Source)
{
    /// <summary>
    /// The fraction of the detector's open-circuit output that reaches a load of
    /// <paramref name="loadOhms"/>.
    /// </summary>
    public double LoadFactor(double loadOhms)
    {
        if (loadOhms <= 0)
            throw new ArgumentOutOfRangeException(nameof(loadOhms), loadOhms, "Load must be positive.");

        return loadOhms / (OutputImpedanceOhms + loadOhms);
    }

    /// <summary>How much the load costs, in dB, relative to an open circuit.</summary>
    public double LoadLossDb(double loadOhms) => -20.0 * Math.Log10(LoadFactor(loadOhms));
}

/// <summary>The detectors on this bench.</summary>
public static class Detectors
{
    private const string Manual =
        "Keysight 423B and 8470B Detectors manual (`8470B.pdf` in the local manual library), "
        + "Specifications";

    /// <summary>
    /// HP 8470B, 10 MHz to 18 GHz, Type-N.
    ///
    /// <para>Every figure here is from its own manual. The two that matter most: output impedance
    /// is "1 to 2 kohm (typically 1.3 kohm)", and the sensitivity specification carries footnote 5,
    /// "External load resistance > 50 kohm".</para>
    /// </summary>
    public static readonly DetectorModel Hp8470B = new(
        "HP 8470B", 10e6, 18e9,
        OutputImpedanceOhms: 1300,
        MinLoadForSensitivitySpecOhms: 50_000,
        MinLoadForSquareLawOhms: 8_000,
        LowLevelSensitivityVoltsPerMilliwatt: 0.5,
        MaxInputMilliwatts: 200,
        Source: $"{Manual}: output impedance 1 to 2 kohm (typically 1.3 kohm); low-level "
                + "sensitivity > 0.5 mVdc/uW CW with footnote 5 \"External load resistance "
                + "> 50 kohm\"; Option 002 square law within +/-0.5 dB over at least 30 dB "
                + "\"working into an external load > 8 kohm\"; maximum operating input 200 mW.");

    /// <summary>
    /// HP 8473C, 3.5 mm, to 26.5 GHz — the manual's own choice for 5-14 and the only detector here
    /// that reaches band 4.
    ///
    /// <para><b>Its own manual is not in the local library.</b> The figures below are the 8470B's,
    /// carried across because they are the same detector family, and they are the ones this
    /// project's warnings are built on. Confirm them against an 8473C data sheet before quoting
    /// any of them as fact — see the warning <see cref="DetectorCalibrator"/> raises.</para>
    /// </summary>
    public static readonly DetectorModel Hp8473C = Hp8470B with
    {
        Name = "HP 8473C",
        MaxHz = 26.5e9,
        Source = "NOT from an 8473C manual — that is not in the local manual library. These are "
                 + "the 8470B's figures, carried across as same-family estimates. Confirm before "
                 + "relying on them.",
    };

    /// <summary>HP 8472B, SMA, to 18 GHz. Same caveat as the 8473C.</summary>
    public static readonly DetectorModel Hp8472B = Hp8470B with
    {
        Name = "HP 8472B",
        Source = "NOT from an 8472B manual. The 8470B's figures, carried across as same-family "
                 + "estimates. Confirm before relying on them.",
    };

    public static readonly IReadOnlyList<DetectorModel> All = [Hp8470B, Hp8472B, Hp8473C];
}

/// <summary>One point of a detector calibration: a requested level and the volts it produced.</summary>
/// <param name="Dbm">Level the DUT was asked for.</param>
/// <param name="Volts">Detector output, as read. Negative — these detectors are negative polarity.</param>
public sealed record DetectorPoint(double Dbm, double Volts);

/// <summary>
/// The fitted curve: detector volts to dBm.
///
/// <para><b>Why not a straight line.</b> A crystal detector is square-law at low level — output
/// volts proportional to input <i>power</i> — and linear at high level, where output volts follow
/// input <i>voltage</i> instead. In log-log terms the slope changes from 1 to 0.5 across the
/// transition. A single straight line through both regions is wrong at both ends and least wrong
/// in the middle, which is the worst possible failure shape because it looks plausible.</para>
///
/// <para>So the fit is a low-order polynomial in log10 of the detector magnitude, which
/// accommodates a slope that varies smoothly. It is fitted in the direction it is used — volts in,
/// dBm out — so there is no inversion to go wrong.</para>
/// </summary>
/// <param name="Coefficients">Polynomial coefficients, constant term first.</param>
/// <param name="LogCentre">
/// The mean of log10|V| over the calibration points. Powers are taken of (log10|V| - this), which
/// keeps the normal equations well conditioned.
/// </param>
/// <param name="MinVolts">Smallest magnitude covered by the calibration.</param>
/// <param name="MaxVolts">Largest magnitude covered.</param>
/// <param name="RmsResidualDb">RMS of the fit residuals, in dB.</param>
/// <param name="WorstResidualDb">Largest single residual, in dB.</param>
public sealed record DetectorFit(
    IReadOnlyList<double> Coefficients,
    double LogCentre,
    double MinVolts,
    double MaxVolts,
    double RmsResidualDb,
    double WorstResidualDb)
{
    /// <summary>Level in dBm for a detector reading of <paramref name="volts"/>.</summary>
    public double DbmFor(double volts)
    {
        var magnitude = Math.Abs(volts);

        if (magnitude <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(volts), volts,
                "A detector reading of zero has no level: the detector is disconnected, the RF is "
                + "off, or the scope channel is wrong.");

        var u = Math.Log10(magnitude) - LogCentre;
        var dbm = 0.0;
        var power = 1.0;

        foreach (var coefficient in Coefficients)
        {
            dbm += coefficient * power;
            power *= u;
        }

        return dbm;
    }

    /// <summary>
    /// True if <paramref name="volts"/> is inside the range the calibration actually covered.
    /// A polynomial extrapolates confidently and wrongly, so this is worth asking.
    /// </summary>
    public bool Covers(double volts)
    {
        var magnitude = Math.Abs(volts);
        return magnitude >= MinVolts && magnitude <= MaxVolts;
    }
}

/// <summary>
/// A stored detector calibration, and everything that has to still be true for it to apply.
/// </summary>
/// <param name="Detector">Which detector was calibrated.</param>
/// <param name="VideoLoadOhms">
/// What the detector was driving. Part of the identity, not a footnote: changing it changes the
/// sensitivity by the divider it forms with the detector's own output impedance, so a calibration
/// taken with a 50 ohm feedthrough fitted is meaningless once it is removed, and vice versa.
/// </param>
/// <param name="Band">Band the calibration was taken in.</param>
/// <param name="CalibrationHz">The CW frequency it was taken at.</param>
/// <param name="Fit">The curve.</param>
/// <param name="At">When it was taken.</param>
/// <param name="Points">The raw data, kept so a suspect fit can be re-examined.</param>
/// <param name="Warnings">Anything that makes this calibration less trustworthy than it looks.</param>
public sealed record DetectorCalibration(
    DetectorModel Detector,
    double VideoLoadOhms,
    BandId Band,
    double CalibrationHz,
    DetectorFit Fit,
    DateTime At,
    IReadOnlyList<DetectorPoint> Points,
    IReadOnlyList<string> Warnings)
{
    /// <summary>
    /// This calibrates a relative scale, not an absolute one: it says how many dB apart two points
    /// on a trace are, not what either is in absolute terms.
    /// </summary>
    public TraceabilityClass Traceability => TraceabilityClass.Relative;

    /// <summary>
    /// Whether this calibration can be used for the setup described.
    ///
    /// <para>All three have to match. The detector is obvious; the band matters because detector
    /// sensitivity varies with frequency; and the load is the one people forget — see
    /// <see cref="VideoLoadOhms"/>.</para>
    /// </summary>
    public bool AppliesTo(DetectorModel detector, double videoLoadOhms, BandId band) =>
        detector.Name == Detector.Name
        && band == Band
        && Math.Abs(videoLoadOhms - VideoLoadOhms) / VideoLoadOhms < 0.01;

    /// <summary>Why this calibration does not apply, for a message worth reading.</summary>
    public string ExplainMismatch(DetectorModel detector, double videoLoadOhms, BandId band)
    {
        var reasons = new List<string>();

        if (detector.Name != Detector.Name)
            reasons.Add($"calibrated with the {Detector.Name}, now using the {detector.Name}");

        if (band != Band)
            reasons.Add($"calibrated in band {(int)Band}, now in band {(int)band}");

        if (Math.Abs(videoLoadOhms - VideoLoadOhms) / VideoLoadOhms >= 0.01)
            reasons.Add($"calibrated into {VideoLoadOhms:0.###} ohms, now into {videoLoadOhms:0.###} "
                        + $"ohms — worth {Math.Abs(Detector.LoadLossDb(videoLoadOhms) - Detector.LoadLossDb(VideoLoadOhms)):0.0} dB "
                        + "of sensitivity, so the old fit would be wrong by that much everywhere");

        return reasons.Count == 0
            ? "It does apply."
            : "Re-run the detector calibration: " + string.Join("; ", reasons) + ".";
    }
}
