using HP8340B.Instruments.Model;

namespace HP8340B.Measurements.Detector;

/// <summary>
/// Runs the detector calibration of 5-14 step 10, numerically.
///
/// <para>The manual calibrates the scope's vertical scale by eye: set a known level, note where
/// the trace sits, repeat. This does the same sweep over the bus and fits a curve, so the XY
/// traces of M2-02 can be shown in dB instead of arbitrary volts — which is what makes "back off
/// 0.5 dB from the squegging region" a thing the tool can measure rather than a thing somebody
/// estimates from a graticule.</para>
/// </summary>
public static class DetectorCalibrator
{
    /// <summary>Default polynomial degree. Three is enough for the square-law-to-linear bend.</summary>
    public const int DefaultDegree = 3;

    /// <summary>
    /// Residual above which the calibration is called bad. A detector curve is smooth, so a fit
    /// that cannot get inside a few tenths of a dB is telling you something is wrong with the
    /// measurement, not with the polynomial.
    ///
    /// <para>PROVISIONAL: never checked against an instrument. The simulated detector fits to
    /// 0.006 dB, so 0.3 is fifty times the model's own residual — but a real detector's noise and
    /// the DUT's own level accuracy both land in this number, and neither has been measured.</para>
    /// </summary>
    public const double AcceptableRmsResidualDb = 0.3;

    /// <summary>
    /// The default level sweep: +7 dBm down to -25 dBm in 2 dB steps.
    ///
    /// <para><b>It stops at -25 dBm because of the detector, not the DUT.</b> The 8470B's noise is
    /// specified at "&lt; 50 uVpp with CW applied to produce 100 mV output", and at -25 dBm into a
    /// megohm its output is about 1.5 mV — thirty times that. Two more steps down and the margin
    /// is twelve times; by -39 dBm the signal and the noise are the same size and the fit residual
    /// grows fortyfold, which looks like a bad calibration but is really just the bottom of the
    /// sweep being below what the detector can see.</para>
    ///
    /// <para>The top is +7 dBm because rule 5 refuses more into a sensor, and because the range
    /// that matters for the tuning view is the top twenty-odd dB of the envelope anyway.</para>
    /// </summary>
    public static IReadOnlyList<double> DefaultLevelsDbm { get; } =
        Enumerable.Range(0, 17).Select(i => 7.0 - 2.0 * i).ToList();

    /// <summary>
    /// Steps the DUT's level and fits detector volts to dB.
    /// </summary>
    /// <param name="detector">Which detector is connected.</param>
    /// <param name="videoLoadOhms">What the detector is driving — see the remarks on the result.</param>
    /// <param name="band">Band being calibrated.</param>
    /// <param name="calibrationHz">Fixed CW frequency for the sweep.</param>
    /// <param name="levelsDbm">Levels to step through.</param>
    /// <param name="measure">
    /// Sets the DUT to a level and returns the detector reading in volts. Kept as a delegate so
    /// the calibration is testable against a model and does not have to own the instruments.
    /// </param>
    /// <param name="degree">Polynomial degree.</param>
    public static DetectorCalibration Calibrate(
        DetectorModel detector,
        double videoLoadOhms,
        BandId band,
        double calibrationHz,
        IReadOnlyList<double> levelsDbm,
        Func<double, double> measure,
        int degree = DefaultDegree)
    {
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(levelsDbm);
        ArgumentNullException.ThrowIfNull(measure);

        if (levelsDbm.Count < degree + 1)
            throw new ArgumentException(
                $"A degree-{degree} fit needs at least {degree + 1} points; got {levelsDbm.Count}.",
                nameof(levelsDbm));

        if (calibrationHz < detector.MinHz || calibrationHz > detector.MaxHz)
            throw new ArgumentOutOfRangeException(
                nameof(calibrationHz), calibrationHz,
                $"The {detector.Name} covers {detector.MinHz / 1e6:0.#} MHz to "
                + $"{detector.MaxHz / 1e9:0.#} GHz.");

        // Highest level first, so that if the sweep is abandoned the points that exist are the
        // ones well clear of the noise.
        var ordered = levelsDbm.OrderByDescending(v => v).ToList();
        var points = ordered.Select(dbm => new DetectorPoint(dbm, measure(dbm))).ToList();

        var warnings = new List<string>();
        warnings.AddRange(CheckSetup(detector, videoLoadOhms));
        warnings.AddRange(CheckMonotonic(points));

        var usable = points.Where(p => Math.Abs(p.Volts) > 0).ToList();

        if (usable.Count < degree + 1)
            throw new InvalidOperationException(
                $"Only {usable.Count} of {points.Count} levels produced any detector output. Check "
                + "the detector is connected to the scope channel the reader is using, that the "
                + "DUT's RF output is on, and that the level range is inside the detector's span.");

        var fit = Fit(usable, degree);

        if (fit.RmsResidualDb > AcceptableRmsResidualDb)
            warnings.Add(
                $"Fit residual is {fit.RmsResidualDb:0.00} dB RMS ({fit.WorstResidualDb:0.00} dB "
                + $"worst), above the {AcceptableRmsResidualDb:0.0} dB this project treats as "
                + "acceptable. A detector curve is smooth, so a residual this large usually means "
                + "the measurement misbehaved — a loose connection, the DUT squegging during the "
                + "sweep, or the bottom of the range buried in noise — rather than the polynomial "
                + "being the wrong shape.");

        return new DetectorCalibration(
            detector, videoLoadOhms, band, calibrationHz, fit, DateTime.UtcNow, points, warnings);
    }

    /// <summary>
    /// Things about the setup that make a calibration less trustworthy than it looks.
    ///
    /// <para>The load check is the one that matters. A 50 ohm feedthrough in front of a detector
    /// whose output impedance is 1.3 kohm throws away about 29 dB, which pushes the bottom of the
    /// sweep into the detector's own noise and voids the square-law specification. The DS1104Z's
    /// 1 Mohm input is <b>better</b> for this measurement than a 50 ohm one would be.</para>
    /// </summary>
    public static IReadOnlyList<string> CheckSetup(DetectorModel detector, double videoLoadOhms)
    {
        var warnings = new List<string>();

        if (videoLoadOhms < detector.MinLoadForSensitivitySpecOhms)
        {
            var loss = detector.LoadLossDb(videoLoadOhms);

            warnings.Add(
                $"The detector is driving {videoLoadOhms:0.###} ohms, below the "
                + $"{detector.MinLoadForSensitivitySpecOhms / 1000:0.#} kohm its sensitivity is "
                + $"specified into. Against its {detector.OutputImpedanceOhms:0} ohm output "
                + $"impedance that costs {loss:0.0} dB, so the published sensitivity does not "
                + "apply and the bottom of the sweep may be in the noise.");
        }

        if (videoLoadOhms < detector.MinLoadForSquareLawOhms)
            warnings.Add(
                $"Below {detector.MinLoadForSquareLawOhms / 1000:0.#} kohm the square-law "
                + "specification does not apply either, so the low-level end of the curve is not "
                + "the shape the fit assumes.");

        if (detector.Source.StartsWith("NOT", StringComparison.Ordinal))
            warnings.Add(
                $"The figures for the {detector.Name} are estimates carried across from the 8470B, "
                + "not its own manual. " + detector.Source);

        return warnings;
    }

    /// <summary>
    /// Checks the detector output grows with level.
    ///
    /// <para>It should, monotonically. If it does not, the interesting possibility is that the DUT
    /// was <b>squegging</b> during the sweep — power reversal is one of the three symptoms — and
    /// fitting a smooth curve through that would bake the fault into the calibration and hide it.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> CheckMonotonic(IReadOnlyList<DetectorPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        var ascending = points.OrderBy(p => p.Dbm).ToList();
        var reversals = new List<string>();

        for (var i = 1; i < ascending.Count; i++)
        {
            var previous = Math.Abs(ascending[i - 1].Volts);
            var current = Math.Abs(ascending[i].Volts);

            if (current < previous)
                reversals.Add($"{ascending[i - 1].Dbm:+0.#;-0.#} to {ascending[i].Dbm:+0.#;-0.#} dBm");
        }

        if (reversals.Count == 0) return [];

        return
        [
            $"Detector output FELL as the level rose, between {string.Join(", ", reversals)}. "
            + "That is power reversal — one of the three symptoms of squegging — so the DUT may "
            + "have been squegging during this sweep. Fitting through it would bake the fault "
            + "into the calibration and hide it. Check the level range and the SRD bias before "
            + "trusting this fit.",
        ];
    }

    /// <summary>
    /// Least-squares fit of dBm against log10 of the detector magnitude.
    /// </summary>
    public static DetectorFit Fit(IReadOnlyList<DetectorPoint> points, int degree = DefaultDegree)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (degree < 1)
            throw new ArgumentOutOfRangeException(nameof(degree), degree, "Degree must be at least 1.");

        if (points.Count < degree + 1)
            throw new ArgumentException(
                $"A degree-{degree} fit needs at least {degree + 1} points.", nameof(points));

        var logs = points.Select(p => Math.Log10(Math.Abs(p.Volts))).ToList();
        var centre = logs.Average();

        var x = logs.Select(l => l - centre).ToList();
        var y = points.Select(p => p.Dbm).ToList();

        var coefficients = LeastSquares(x, y, degree);

        var residuals = new List<double>();
        for (var i = 0; i < points.Count; i++)
        {
            var predicted = 0.0;
            var power = 1.0;

            foreach (var c in coefficients)
            {
                predicted += c * power;
                power *= x[i];
            }

            residuals.Add(predicted - y[i]);
        }

        var magnitudes = points.Select(p => Math.Abs(p.Volts)).ToList();

        return new DetectorFit(
            coefficients,
            centre,
            magnitudes.Min(),
            magnitudes.Max(),
            RmsResidualDb: Math.Sqrt(residuals.Average(r => r * r)),
            WorstResidualDb: residuals.Max(Math.Abs));
    }

    /// <summary>
    /// Polynomial least squares by normal equations, solved with Gaussian elimination and partial
    /// pivoting. The system is tiny — four by four at the default degree — and the abscissa is
    /// centred before the powers are taken, which is what keeps it conditioned.
    /// </summary>
    private static double[] LeastSquares(IReadOnlyList<double> x, IReadOnlyList<double> y, int degree)
    {
        var terms = degree + 1;
        var a = new double[terms, terms + 1];

        for (var row = 0; row < terms; row++)
        {
            for (var column = 0; column < terms; column++)
                a[row, column] = x.Sum(v => Math.Pow(v, row + column));

            a[row, terms] = x.Select((v, i) => Math.Pow(v, row) * y[i]).Sum();
        }

        for (var pivot = 0; pivot < terms; pivot++)
        {
            var best = pivot;
            for (var row = pivot + 1; row < terms; row++)
                if (Math.Abs(a[row, pivot]) > Math.Abs(a[best, pivot])) best = row;

            if (Math.Abs(a[best, pivot]) < 1e-15)
                throw new InvalidOperationException(
                    "The detector calibration points do not determine a fit. This happens when "
                    + "every point returned the same voltage — usually the detector is "
                    + "disconnected, or the DUT's level was not actually changing.");

            if (best != pivot)
                for (var column = 0; column <= terms; column++)
                    (a[pivot, column], a[best, column]) = (a[best, column], a[pivot, column]);

            for (var row = 0; row < terms; row++)
            {
                if (row == pivot) continue;

                var factor = a[row, pivot] / a[pivot, pivot];
                for (var column = pivot; column <= terms; column++)
                    a[row, column] -= factor * a[pivot, column];
            }
        }

        var result = new double[terms];
        for (var i = 0; i < terms; i++) result[i] = a[i, terms] / a[i, i];

        return result;
    }
}

/// <summary>
/// The calibrations taken so far, one per detector, load and band.
///
/// <para>Keyed on all three because all three change the answer, and the one people forget is the
/// load — see <see cref="DetectorCalibration.VideoLoadOhms"/>.</para>
/// </summary>
public sealed class DetectorCalibrationStore
{
    private readonly List<DetectorCalibration> _calibrations = [];

    /// <summary>Everything stored, newest first.</summary>
    public IReadOnlyList<DetectorCalibration> All =>
        _calibrations.OrderByDescending(c => c.At).ToList();

    /// <summary>Stores a calibration, replacing any earlier one for the same setup.</summary>
    public void Store(DetectorCalibration calibration)
    {
        ArgumentNullException.ThrowIfNull(calibration);

        _calibrations.RemoveAll(c =>
            c.AppliesTo(calibration.Detector, calibration.VideoLoadOhms, calibration.Band));

        _calibrations.Add(calibration);
    }

    /// <summary>The calibration for this setup, or null if there is not one.</summary>
    public DetectorCalibration? For(DetectorModel detector, double videoLoadOhms, BandId band) =>
        _calibrations
            .Where(c => c.AppliesTo(detector, videoLoadOhms, band))
            .OrderByDescending(c => c.At)
            .FirstOrDefault();

    /// <summary>
    /// The calibration for this setup, or an exception explaining what to re-run. Used where a
    /// missing calibration would otherwise show a trace in volts that looks like dB.
    /// </summary>
    public DetectorCalibration Require(DetectorModel detector, double videoLoadOhms, BandId band)
    {
        if (For(detector, videoLoadOhms, band) is { } found) return found;

        var nearest = _calibrations.FirstOrDefault();

        throw new InvalidOperationException(
            nearest is null
                ? $"No detector calibration for the {detector.Name} into {videoLoadOhms:0.###} ohms "
                  + $"in band {(int)band}. Run the calibration first — without it a trace can only "
                  + "be shown in volts."
                : nearest.ExplainMismatch(detector, videoLoadOhms, band));
    }
}
