namespace HP8340B.Measurements.Detector;

/// <summary>
/// A crystal detector, modelled well enough to develop and test the calibration against.
///
/// <para><b>The model.</b> Output magnitude follows</para>
/// <code>
///   |V| = k * P / (1 + sqrt(P / Pc))
/// </code>
/// <para>which has the two behaviours a crystal detector actually has and a smooth transition
/// between them: for P far below Pc it reduces to <c>k*P</c>, output proportional to input
/// <b>power</b>, which is square law; for P far above Pc it reduces to
/// <c>k*sqrt(Pc)*sqrt(P)</c>, output proportional to input <b>voltage</b>, which is the linear
/// region. In log-log terms the slope runs from 1 to 0.5 and Pc is where it is halfway.</para>
///
/// <para><b>It is not invented: both constants come from the 8470B's own specification.</b>
/// "Low level: > 0.5 mVdc/uW CW" fixes k at 0.5 V/mW. "High level: &lt; 0.35 mW produces 100 mV
/// output" then fixes Pc: solving 0.5*0.35/(1+sqrt(0.35/Pc)) = 0.1 gives Pc = 0.622 mW. So a
/// model with two free parameters is pinned by the two figures the manual publishes, and anything
/// it predicts in between is at least consistent with them.</para>
///
/// <para>The output is <b>negative</b>, as every detector on this bench is.</para>
/// </summary>
public sealed class SimulatedDetector
{
    /// <summary>
    /// Square-law sensitivity into a high load, volts per milliwatt. 8470B: "Low level:
    /// &gt; 0.5 mVdc/uW CW", which is 0.5 V/mW.
    /// </summary>
    public double SensitivityVoltsPerMilliwatt { get; set; } = 0.5;

    /// <summary>
    /// Transition power, milliwatts — where the square-law and linear asymptotes cross over.
    /// Derived from the 8470B's high-level figure, as set out in the class remarks.
    /// </summary>
    public double TransitionMilliwatts { get; set; } = 0.622;

    /// <summary>What the detector is driving. The divider with its output impedance is applied.</summary>
    public double VideoLoadOhms { get; set; } = 1e6;

    /// <summary>The detector being modelled, for its output impedance and limits.</summary>
    public DetectorModel Model { get; set; } = Detectors.Hp8470B;

    /// <summary>
    /// Peak-to-peak noise on the output, volts. 8470B: "Noise: &lt; 50 uVpp with CW applied to
    /// produce 100 mV output". Deterministic here — a function of the level, not a random draw —
    /// so a fit that fails, fails every time.
    /// </summary>
    public double NoiseVoltsPeakToPeak { get; set; } = 50e-6;

    /// <summary>Detector output in volts for an input of <paramref name="dbm"/>. Negative.</summary>
    public double VoltsFor(double dbm)
    {
        var milliwatts = Math.Pow(10, dbm / 10.0);

        if (milliwatts > Model.MaxInputMilliwatts)
            throw new ArgumentOutOfRangeException(
                nameof(dbm), dbm,
                $"{milliwatts:0.#} mW is above the {Model.Name}'s {Model.MaxInputMilliwatts:0} mW "
                + "maximum operating input power.");

        var openCircuit = SensitivityVoltsPerMilliwatt * milliwatts
                          / (1.0 + Math.Sqrt(milliwatts / TransitionMilliwatts));

        var loaded = openCircuit * Model.LoadFactor(VideoLoadOhms);

        // A deterministic wobble standing in for the detector's noise, so the bottom of a sweep
        // into a low load genuinely disappears into it rather than staying artificially clean.
        var noise = NoiseVoltsPeakToPeak / 2.0 * Math.Sin(dbm * 2.7);

        return -(loaded + noise);
    }

    /// <summary>
    /// The log-log slope at <paramref name="dbm"/>: 1 in the square-law region, 0.5 in the linear
    /// one. Public so a test can confirm the model really does span both rather than assuming it.
    /// </summary>
    public double SlopeAt(double dbm)
    {
        const double step = 0.01;

        var low = Math.Log10(Math.Abs(VoltsFor(dbm - step)));
        var high = Math.Log10(Math.Abs(VoltsFor(dbm + step)));

        // d(log10 V) / d(log10 P), and log10 P is dBm/10.
        return (high - low) / (2 * step / 10.0);
    }
}
