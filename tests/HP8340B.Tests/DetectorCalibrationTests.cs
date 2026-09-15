using HP8340B.Instruments.Model;
using HP8340B.Measurements.Detector;
using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// M0-14: fitting detector volts to dB, so the XY tuning view of M2-02 can be read in dB instead
/// of arbitrary volts. 5-14 step 10 does this by eye; this does it numerically.
/// </summary>
public class DetectorCalibrationTests
{
    private static SimulatedDetector Detector(double loadOhms = 1e6) =>
        new() { VideoLoadOhms = loadOhms };

    private static DetectorCalibration CalibrateAgainst(
        SimulatedDetector model, IReadOnlyList<double>? levels = null, int degree = 3) =>
        DetectorCalibrator.Calibrate(
            model.Model, model.VideoLoadOhms, BandId.Band2, 10e9,
            levels ?? DetectorCalibrator.DefaultLevelsDbm,
            model.VoltsFor, degree);

    // --- The model is anchored to the manual ---------------------------------------------------

    [Fact]
    public void TheDetectorModelReproducesBothOfThe8470BsPublishedFigures()
    {
        // The model has two free parameters and the manual publishes two numbers, so it is pinned
        // rather than invented. "Low level: > 0.5 mVdc/uW CW" and "High level: < 0.35 mW produces
        // 100 mV output".
        var detector = Detector();

        // 0.35 mW is -4.56 dBm.
        var highLevel = Math.Abs(detector.VoltsFor(10 * Math.Log10(0.35)));
        Assert.Equal(0.100, highLevel, 3);

        // Deep in the square-law region, output should follow 0.5 V/mW. Noise off: this is a
        // test of the transfer function, and at -40 dBm the detector's own 50 uVpp is the same
        // size as the signal — which is itself why the default sweep stops well above here.
        var quiet = new SimulatedDetector { NoiseVoltsPeakToPeak = 0 };
        Assert.Equal(0.5 * Math.Pow(10, -4.0), Math.Abs(quiet.VoltsFor(-40)), 5);
    }

    [Fact]
    public void TheModelSpansBothRegionsWhichIsTheWholePoint()
    {
        // A crystal detector is square-law at low level (slope 1 in log-log: volts follow power)
        // and linear at high level (slope 0.5: volts follow voltage). If the model did not
        // actually bend, a straight-line fit would pass and prove nothing.
        var detector = Detector();

        Assert.True(detector.SlopeAt(-40) > 0.97, "Should be square law at -40 dBm.");
        Assert.True(detector.SlopeAt(10) < 0.70, "Should be well into the linear region at +10 dBm.");
    }

    [Fact]
    public void TheDetectorOutputIsNegative()
    {
        // Every detector on this bench is negative polarity, and 5-15 step 26 is skipped precisely
        // because no positive one is on hand.
        var detector = Detector();

        Assert.All(new[] { 7.0, 0.0, -20.0, -40.0 },
            dbm => Assert.True(detector.VoltsFor(dbm) < 0));
    }

    [Fact]
    public void AnInputAboveTheDetectorsRatingIsRefused()
    {
        // 200 mW maximum operating input, from its own manual.
        var detector = Detector();
        Assert.Throws<ArgumentOutOfRangeException>(() => detector.VoltsFor(30));
    }

    // --- The fit --------------------------------------------------------------------------------

    [Fact]
    public void TheFitRecoversTheLevelAcrossTheWholeSweep()
    {
        var detector = Detector();
        var calibration = CalibrateAgainst(detector);

        // Levels deliberately between the calibration points, so this tests the fit rather
        // than the points it was given.
        foreach (var dbm in new[] { 6.0, 0.0, -9.0, -16.0 })
        {
            var recovered = calibration.Fit.DbmFor(detector.VoltsFor(dbm));

            Assert.True(Math.Abs(recovered - dbm) < 0.05,
                $"Asked for {dbm:+0.0;-0.0} dBm, fit recovered {recovered:+0.00;-0.00}.");
        }

        // The bottom of the sweep is looser, and it is the detector that sets the floor rather
        // than the polynomial: at -24 dBm the output is about 1.6 mV and the 8470B's 50 uVpp of
        // noise is +/-1.6% of that, which is +/-0.14 dB. No fit can do better than the data.
        var bottom = calibration.Fit.DbmFor(detector.VoltsFor(-24));
        Assert.True(Math.Abs(bottom + 24.0) < 0.2,
            $"Asked for -24.0 dBm, fit recovered {bottom:+0.00;-0.00}.");
    }

    [Fact]
    public void AStraightLineCannotDoTheJobAndACurveCan()
    {
        // This is the acceptance criterion that matters: "fits a curve valid across the
        // square-law and linear regions, not a single straight line". A first-order fit is what
        // somebody would reach for, and it is wrong at both ends and least wrong in the middle —
        // the worst failure shape, because it looks plausible.
        var detector = Detector();
        var points = DetectorCalibrator.DefaultLevelsDbm
            .Select(d => new DetectorPoint(d, detector.VoltsFor(d)))
            .ToList();

        var line = DetectorCalibrator.Fit(points, degree: 1);
        var curve = DetectorCalibrator.Fit(points, degree: 3);

        Assert.True(line.RmsResidualDb > DetectorCalibrator.AcceptableRmsResidualDb,
            $"A straight line should fail the acceptance threshold; it managed "
            + $"{line.RmsResidualDb:0.000} dB RMS.");
        Assert.True(curve.RmsResidualDb < DetectorCalibrator.AcceptableRmsResidualDb);

        // Not marginally better — two orders of magnitude better.
        Assert.True(curve.RmsResidualDb < line.RmsResidualDb / 50,
            $"line {line.RmsResidualDb:0.000} dB vs curve {curve.RmsResidualDb:0.0000} dB");
    }

    [Fact]
    public void TheResidualIsReportedSoABadCalibrationIsVisible()
    {
        var calibration = CalibrateAgainst(Detector());

        Assert.True(calibration.Fit.RmsResidualDb >= 0);
        Assert.True(calibration.Fit.WorstResidualDb >= calibration.Fit.RmsResidualDb);
        Assert.True(calibration.Fit.RmsResidualDb < DetectorCalibrator.AcceptableRmsResidualDb);
    }

    [Fact]
    public void ABadFitWarnsRatherThanBeingStoredQuietly()
    {
        // Fitting nonsense should say so. Here the "detector" returns a value unrelated to level.
        var levels = DetectorCalibrator.DefaultLevelsDbm;
        var scramble = new Random(1);

        var calibration = DetectorCalibrator.Calibrate(
            Detectors.Hp8470B, 1e6, BandId.Band2, 10e9, levels,
            _ => -(0.001 + scramble.NextDouble() * 0.5));

        Assert.Contains(calibration.Warnings, w => w.Contains("residual"));
    }

    [Fact]
    public void TheFitKnowsWhatRangeItActuallyCovers()
    {
        // A polynomial extrapolates confidently and wrongly, so the calibrated span is recorded.
        var detector = Detector();
        var calibration = CalibrateAgainst(detector, [7.0, 3.0, -1.0, -5.0, -9.0]);

        Assert.True(calibration.Fit.Covers(detector.VoltsFor(0)));
        Assert.False(calibration.Fit.Covers(detector.VoltsFor(-30)));
    }

    [Fact]
    public void AZeroReadingHasNoLevelAndSaysWhyNot()
    {
        var calibration = CalibrateAgainst(Detector());

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => calibration.Fit.DbmFor(0));
        Assert.Contains("disconnected", ex.Message);
    }

    [Fact]
    public void AFitNeedsMorePointsThanItsDegree()
    {
        var detector = Detector();

        Assert.Throws<ArgumentException>(
            () => CalibrateAgainst(detector, [0.0, -5.0, -10.0], degree: 3));
    }

    [Fact]
    public void AFlatSweepIsRefusedRatherThanFitted()
    {
        // Every point the same voltage means the detector is disconnected or the level never
        // changed. A degenerate system would otherwise produce coefficients of infinity.
        var ex = Assert.Throws<InvalidOperationException>(() => DetectorCalibrator.Calibrate(
            Detectors.Hp8470B, 1e6, BandId.Band2, 10e9,
            DetectorCalibrator.DefaultLevelsDbm, _ => -0.05));

        Assert.Contains("do not determine a fit", ex.Message);
    }

    // --- Squegging during the calibration --------------------------------------------------------

    [Fact]
    public void PowerReversalDuringTheSweepIsCalledOutAsPossibleSquegging()
    {
        // If the DUT squegs during the calibration sweep the detector output falls as the level
        // rises. Fitting a smooth curve through that would bake the fault into the calibration
        // and hide it, which is the opposite of what this project is for.
        var detector = Detector();

        double Reversing(double dbm) => dbm > 0
            ? detector.VoltsFor(0) * (1 - (dbm / 20.0))   // output collapses above 0 dBm
            : detector.VoltsFor(dbm);

        var calibration = DetectorCalibrator.Calibrate(
            Detectors.Hp8470B, 1e6, BandId.Band2, 10e9,
            DetectorCalibrator.DefaultLevelsDbm, Reversing);

        Assert.Contains(calibration.Warnings, w => w.Contains("squegging"));
        Assert.Contains(calibration.Warnings, w => w.Contains("FELL"));
    }

    [Fact]
    public void AWellBehavedSweepRaisesNoMonotonicityWarning()
    {
        var calibration = CalibrateAgainst(Detector());
        Assert.DoesNotContain(calibration.Warnings, w => w.Contains("squegging"));
    }

    // --- The video load, which is the thing people forget ---------------------------------------

    [Fact]
    public void AFiftyOhmFeedthroughIsWarnedAboutWithTheNumber()
    {
        // The 8470B's output impedance is 1.3 kohm typical and its sensitivity is specified into
        // >50 kohm. Loading it with 50 ohms throws away about 29 dB. The DS1104Z's 1 Mohm input
        // is BETTER for this than a 50 ohm one would be.
        var warnings = DetectorCalibrator.CheckSetup(Detectors.Hp8470B, 50);

        Assert.Contains(warnings, w => w.Contains("28.6 dB") || w.Contains("29") || w.Contains("28"));
        Assert.Contains(warnings, w => w.Contains("square-law"));
    }

    [Fact]
    public void AMegohmLoadRaisesNoLoadWarning() =>
        Assert.Empty(DetectorCalibrator.CheckSetup(Detectors.Hp8470B, 1e6));

    [Fact]
    public void TheLoadLossIsComputedFromTheDetectorsOwnOutputImpedance()
    {
        // 50 / (1300 + 50) is -28.6 dB.
        Assert.Equal(28.6, Detectors.Hp8470B.LoadLossDb(50), 1);

        // Into a megohm it is nothing at all.
        Assert.True(Detectors.Hp8470B.LoadLossDb(1e6) < 0.02);
    }

    [Fact]
    public void IntoFiftyOhmsTheBottomOfTheSweepDisappearsIntoTheDetectorsOwnNoise()
    {
        // Demonstrating the consequence rather than only warning about it: at -30 dBm the signal
        // into 50 ohms is smaller than the detector's specified 50 uVpp noise.
        var loaded = Detector(50);
        var signal = Math.Abs(loaded.VoltsFor(-30));

        Assert.True(signal < loaded.NoiseVoltsPeakToPeak,
            $"{signal * 1e6:0.#} uV against {loaded.NoiseVoltsPeakToPeak * 1e6:0.#} uVpp of noise.");

        // Into a megohm the same level is comfortably clear of it.
        Assert.True(Math.Abs(Detector().VoltsFor(-30)) > 5 * Detector().NoiseVoltsPeakToPeak);
    }

    // --- Storage and invalidation ------------------------------------------------------------------

    [Fact]
    public void ACalibrationOnlyAppliesToTheSetupItWasTakenOn()
    {
        var calibration = CalibrateAgainst(Detector());

        Assert.True(calibration.AppliesTo(Detectors.Hp8470B, 1e6, BandId.Band2));

        Assert.False(calibration.AppliesTo(Detectors.Hp8473C, 1e6, BandId.Band2));   // other detector
        Assert.False(calibration.AppliesTo(Detectors.Hp8470B, 50, BandId.Band2));    // load changed
        Assert.False(calibration.AppliesTo(Detectors.Hp8470B, 1e6, BandId.Band3));   // other band
    }

    [Fact]
    public void ChangingTheLoadExplainsItselfInDb()
    {
        // The acceptance criterion asks for a prompt to re-run when the load changes. Saying how
        // many dB the old fit would now be wrong by is what makes that prompt worth obeying.
        var calibration = CalibrateAgainst(Detector());
        var reason = calibration.ExplainMismatch(Detectors.Hp8470B, 50, BandId.Band2);

        Assert.Contains("Re-run", reason);
        Assert.Contains("ohms", reason);
        Assert.Contains("dB", reason);
    }

    [Fact]
    public void TheStoreKeepsOneCalibrationPerSetup()
    {
        var store = new DetectorCalibrationStore();

        store.Store(CalibrateAgainst(Detector()));
        store.Store(CalibrateAgainst(Detector()));          // same setup, replaces
        store.Store(DetectorCalibrator.Calibrate(
            Detectors.Hp8470B, 1e6, BandId.Band3, 15e9,
            DetectorCalibrator.DefaultLevelsDbm, Detector().VoltsFor));

        Assert.Equal(2, store.All.Count);
        Assert.NotNull(store.For(Detectors.Hp8470B, 1e6, BandId.Band2));
        Assert.NotNull(store.For(Detectors.Hp8470B, 1e6, BandId.Band3));
        Assert.Null(store.For(Detectors.Hp8470B, 50, BandId.Band2));
    }

    [Fact]
    public void RequiringAMissingCalibrationSaysWhatToRun()
    {
        var store = new DetectorCalibrationStore();

        var ex = Assert.Throws<InvalidOperationException>(
            () => store.Require(Detectors.Hp8470B, 1e6, BandId.Band2));

        Assert.Contains("Run the calibration first", ex.Message);
    }

    [Fact]
    public void RequiringAfterTheSetupChangedExplainsTheChange()
    {
        var store = new DetectorCalibrationStore();
        store.Store(CalibrateAgainst(Detector()));

        var ex = Assert.Throws<InvalidOperationException>(
            () => store.Require(Detectors.Hp8470B, 50, BandId.Band2));

        Assert.Contains("Re-run", ex.Message);
    }

    // --- Traceability and provenance ------------------------------------------------------------------

    [Fact]
    public void ADetectorCalibrationIsRelativeNotAbsolute()
    {
        // It says how many dB apart two points on a trace are, not what either is absolutely.
        Assert.Equal(TraceabilityClass.Relative, CalibrateAgainst(Detector()).Traceability);
    }

    [Fact]
    public void The8470BsFiguresAreCitedAndTheOthersAdmitTheyAreEstimates()
    {
        // The 8473C is the manual's own choice and the only detector here that reaches band 4,
        // but its manual is not in the local library. Saying so is better than quoting the
        // 8470B's numbers as if they were its own.
        Assert.Contains("8470B", Detectors.Hp8470B.Source);
        Assert.DoesNotContain("NOT", Detectors.Hp8470B.Source);

        Assert.StartsWith("NOT", Detectors.Hp8473C.Source);
        Assert.StartsWith("NOT", Detectors.Hp8472B.Source);
    }

    [Fact]
    public void CalibratingWithAnEstimatedDetectorSaysSo()
    {
        var warnings = DetectorCalibrator.CheckSetup(Detectors.Hp8473C, 1e6);

        Assert.Contains(warnings, w => w.Contains("estimates"));
    }

    [Fact]
    public void TheRawPointsAreKeptSoASuspectFitCanBeReExamined()
    {
        var calibration = CalibrateAgainst(Detector());

        Assert.Equal(DetectorCalibrator.DefaultLevelsDbm.Count, calibration.Points.Count);
        Assert.All(calibration.Points, p => Assert.True(p.Volts < 0));
    }

    [Fact]
    public void AFrequencyOutsideTheDetectorsRangeIsRefused()
    {
        // The 8470B stops at 18 GHz, so it cannot calibrate band 4 at all — that is what the
        // 8473C is for.
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => DetectorCalibrator.Calibrate(
            Detectors.Hp8470B, 1e6, BandId.Band4, 24e9,
            DetectorCalibrator.DefaultLevelsDbm, Detector().VoltsFor));

        Assert.Contains("18", ex.Message);
    }

    [Fact]
    public void The8473CReachesBandFour()
    {
        var detector = new SimulatedDetector { Model = Detectors.Hp8473C };

        var calibration = DetectorCalibrator.Calibrate(
            Detectors.Hp8473C, 1e6, BandId.Band4, 24e9,
            DetectorCalibrator.DefaultLevelsDbm, detector.VoltsFor);

        Assert.Equal(BandId.Band4, calibration.Band);
    }
}
