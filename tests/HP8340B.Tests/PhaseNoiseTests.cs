using HP8340B.Instruments.Model;
using HP8340B.Measurements.PhaseNoise;
using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// M0-16: importing KE5FX PN.EXE traces and judging them against the 4-9 limits.
/// </summary>
public class PhaseNoiseTests
{
    private static PnTrace Trace(params (double Offset, double Dbc)[] points) =>
        new(points.Select(p => new PnPoint(p.Offset, p.Dbc)).ToList(),
            CarrierHz: 10e9, CarrierDbm: 0, AnalyzerSettings: null, SourceFile: null,
            ImportedAt: DateTime.UtcNow);

    // --- Import, and the format trap --------------------------------------------------------

    [Fact]
    public void ThePnCsvExportIsOneLongLineNotOneRowPerPoint()
    {
        // Straight from PN's own source (pn.cpp, CMD_export): the .csv branch writes
        // "%.20lf,%.20lf" per point with a bare "," between points and NO line breaks. A parser
        // written to the usual CSV shape would read the whole trace as a single row of 2N values
        // and either fail or, worse, pair offsets with the wrong amplitudes.
        var oneLine = "10.00000,-80.00000,100.00000,-95.00000,1000.00000,-110.00000";

        var points = PnImport.Parse(oneLine);

        Assert.Equal(3, points.Count);
        Assert.Equal(10.0, points[0].OffsetHz);
        Assert.Equal(-80.0, points[0].DbcPerHz);
        Assert.Equal(1000.0, points[2].OffsetHz);
        Assert.Equal(-110.0, points[2].DbcPerHz);
    }

    [Fact]
    public void ThePnTxtExportIsOnePairPerLine()
    {
        // The .txt branch writes "%.20lf, %.20lf\n" — note the space after the comma.
        var lines = "10.00000, -80.00000\n100.00000, -95.00000\n1000.00000, -110.00000\n";

        var points = PnImport.Parse(lines);

        Assert.Equal(3, points.Count);
        Assert.Equal(-95.0, points[1].DbcPerHz);
    }

    [Fact]
    public void BothFormatsParseToTheSameTrace()
    {
        var csv = PnImport.Parse("10.0,-80.0,100.0,-95.0");
        var txt = PnImport.Parse("10.0, -80.0\n100.0, -95.0\n");

        Assert.Equal(csv.Select(p => p.OffsetHz), txt.Select(p => p.OffsetHz));
        Assert.Equal(csv.Select(p => p.DbcPerHz), txt.Select(p => p.DbcPerHz));
    }

    [Fact]
    public void ATruncatedExportIsRefusedRatherThanSilentlyDroppingAPoint()
    {
        // An odd count means the file was cut short. Dropping the orphan would quietly shorten
        // the trace.
        var ex = Assert.Throws<InvalidOperationException>(
            () => PnImport.Parse("10.0,-80.0,100.0"));

        Assert.Contains("odd", ex.Message);
    }

    [Fact]
    public void AFileThatIsNotAPnExportSaysSo()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => PnImport.Parse("offset,dbc\n10,-80\n"));

        Assert.Contains("not a number", ex.Message);
    }

    [Fact]
    public void AnEmptyExportIsRefused() =>
        Assert.Throws<InvalidOperationException>(() => PnImport.Parse("   "));

    // --- Interpolation -------------------------------------------------------------------------

    [Fact]
    public void TheTraceInterpolatesInLogOffsetNotLinear()
    {
        // Phase-noise plots are log-log. Between 100 Hz (-95) and 1 kHz (-110), the geometric
        // midpoint 316 Hz should read about half way in dB; a linear interpolation would put
        // -95 - 15*(316-100)/900 = -98.6 there instead of -102.5.
        var trace = Trace((100, -95), (1000, -110));

        Assert.Equal(-102.5, trace.At(316.23), 1);
    }

    [Fact]
    public void OutsideTheTraceTheEndpointsAreHeldRatherThanExtrapolated()
    {
        var trace = Trace((100, -95), (1000, -110));

        Assert.Equal(-95, trace.At(10));
        Assert.Equal(-110, trace.At(100000));
    }

    // --- The conservative pass rule ---------------------------------------------------------------

    [Fact]
    public void ACompositeBelowTheLimitIsAnUnambiguousPass()
    {
        // The measurement is DUT plus analyzer. Adding the analyzer can only have made it worse,
        // so if the composite is already inside the limit the DUT alone must be.
        var judgement = PnPassRule.Judge(10e3, compositeDbcPerHz: -100, limitDbcPerHz: -90);

        Assert.Equal(PnVerdict.Pass, judgement.Verdict);
        Assert.Equal(TraceabilityClass.Spec, judgement.Traceability);
        Assert.Contains("can only have made it worse", judgement.Reason);
    }

    [Fact]
    public void ACompositeAboveTheLimitWithNoBaselineIsInconclusiveNotAFailure()
    {
        // The excess could be the analyzer. Calling it a failure would condemn a good DUT.
        var judgement = PnPassRule.Judge(10e3, compositeDbcPerHz: -80, limitDbcPerHz: -90);

        Assert.Equal(PnVerdict.Inconclusive, judgement.Verdict);
        Assert.Equal(TraceabilityClass.NotPossible, judgement.Traceability);
        Assert.Contains("no analyzer baseline", judgement.Reason);
    }

    [Fact]
    public void ABaselineTooCloseToTheCompositeIsStillInconclusive()
    {
        // Within 10 dB the analyzer is a large enough part of the measurement that subtracting
        // it amplifies its own uncertainty faster than it removes the error.
        var judgement = PnPassRule.Judge(
            10e3, compositeDbcPerHz: -80, limitDbcPerHz: -90, baselineDbcPerHz: -85);

        Assert.Equal(PnVerdict.Inconclusive, judgement.Verdict);
        Assert.Contains("5.0 dB below", judgement.Reason);
    }

    [Fact]
    public void ABaselineTenDbDownLetsTheSubtractionStand()
    {
        // Composite -80, baseline -95 (15 dB down). In power the DUT alone is about -80.1.
        var judgement = PnPassRule.Judge(
            10e3, compositeDbcPerHz: -80, limitDbcPerHz: -90, baselineDbcPerHz: -95);

        Assert.Equal(PnVerdict.Fail, judgement.Verdict);
        Assert.NotNull(judgement.DutOnlyDbcPerHz);
        Assert.Equal(-80.1, judgement.DutOnlyDbcPerHz!.Value, 1);

        // A derived result, not a direct one.
        Assert.Equal(TraceabilityClass.Relative, judgement.Traceability);
    }

    [Fact]
    public void SubtractingABaselineCanTurnAMarginalFailIntoAPass()
    {
        // Composite -89.7 against a -90 limit, with the baseline 10 dB down at -99.7. Removing
        // the analyzer's share leaves the DUT at about -90.2, inside the limit.
        var judgement = PnPassRule.Judge(
            10e3, compositeDbcPerHz: -89.7, limitDbcPerHz: -90, baselineDbcPerHz: -99.7);

        Assert.Equal(PnVerdict.Pass, judgement.Verdict);
        Assert.NotNull(judgement.DutOnlyDbcPerHz);
        Assert.True(judgement.DutOnlyDbcPerHz!.Value < -90);
    }

    [Fact]
    public void TheSubtractionCanOnlyEverRescueAMarginalCase()
    {
        // Worth knowing before anyone hopes a baseline will rescue a bad result. The rule needs
        // the baseline at least 10 dB below the composite; removing a contribution 10 dB down
        // leaves 90% of the power, which is 0.46 dB. That is the MOST the subtraction can ever
        // buy, so anything more than about half a dB over the limit is over it, baseline or no
        // baseline.
        //
        // (Not to be confused with the 0.41 dB such a contribution ADDS — 10*log10(1.1) going in
        // against 10*log10(0.9) coming out. They are not the same number and I used the wrong one
        // first.)
        var mostItCanEverBuy = -PnPassRule.SubtractInPower(0, -PnPassRule.BaselineMarginDb);

        Assert.Equal(0.46, mostItCanEverBuy, 2);

        // And a composite a full dB over the limit stays a failure however good the baseline is.
        var judgement = PnPassRule.Judge(
            10e3, compositeDbcPerHz: -89.0, limitDbcPerHz: -90, baselineDbcPerHz: -120);

        Assert.Equal(PnVerdict.Fail, judgement.Verdict);
    }

    [Fact]
    public void NoiseAddsInPowerNotInDb()
    {
        // Two equal contributions make 3 dB, not 6. Getting this wrong by treating dB as linear
        // would misjudge every marginal case.
        Assert.Equal(-103.0, PnPassRule.SubtractInPower(-100, -103), 1);
    }

    [Fact]
    public void ABaselineAtOrAboveTheCompositeIsRefused()
    {
        // There is nothing left after subtracting it, which means the measurement is the
        // analyzer rather than the DUT.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PnPassRule.SubtractInPower(-100, -100));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => PnPassRule.SubtractInPower(-100, -95));
    }

    [Fact]
    public void AWholeTraceCanBeJudgedAtTheManualsOffsets()
    {
        // 4-9's offsets. The analyzer LO degrades by 20 log N in its higher harmonic bands, so
        // bands 3 and 4 at 10 kHz are expected to come out inconclusive — a known limit, not a
        // bug.
        var trace = Trace((100, -70), (1e3, -85), (10e3, -95), (100e3, -110));
        var baseline = Trace((100, -90), (1e3, -100), (10e3, -105), (100e3, -125));

        var judgements = PnPassRule.JudgeTrace(
            trace, [100, 1e3, 10e3, 100e3], _ => -90, baseline);

        Assert.Equal(4, judgements.Count);
        Assert.All(judgements, j => Assert.False(string.IsNullOrWhiteSpace(j.Reason)));

        // 100 Hz: composite -70 against a -90 limit, baseline 20 dB down, so a real failure.
        Assert.Equal(PnVerdict.Fail, judgements[0].Verdict);

        // 10 kHz: composite -95 is already inside the limit.
        Assert.Equal(PnVerdict.Pass, judgements[2].Verdict);
    }

    [Fact]
    public void TheJudgementCarriesEveryNumberItUsed()
    {
        // Repo rule 3: a verdict with no record of the composite, the baseline and the limit is
        // not defensible later.
        var judgement = PnPassRule.Judge(
            10e3, compositeDbcPerHz: -80, limitDbcPerHz: -90, baselineDbcPerHz: -95);

        Assert.Equal(10e3, judgement.OffsetHz);
        Assert.Equal(-80, judgement.CompositeDbcPerHz);
        Assert.Equal(-95, judgement.BaselineDbcPerHz);
        Assert.Equal(-90, judgement.LimitDbcPerHz);
    }

    [Fact]
    public void AnImportedTraceRecordsTheDutStateAndAnalyzerSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pn-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, "10.0,-80.0,100.0,-95.0");

        try
        {
            var trace = PnImport.Read(path, carrierHz: 10e9, carrierDbm: 0,
                analyzerSettings: "RBW 300 Hz, VBW 100 Hz, ref -10 dBm");

            Assert.Equal(10e9, trace.CarrierHz);
            Assert.Equal(0, trace.CarrierDbm);
            Assert.Contains("RBW", trace.AnalyzerSettings!);
            Assert.Equal(path, trace.SourceFile);
            Assert.Equal(2, trace.Points.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
