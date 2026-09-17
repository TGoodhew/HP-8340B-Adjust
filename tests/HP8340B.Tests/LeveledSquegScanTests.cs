using HP8340B.Instruments;
using HP8340B.Instruments.Model;
using HP8340B.Measurements.Squegging;
using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// M1-05, the leveled squegging test of 5-16 steps 37-50, against the M0-12 model.
///
/// <para>Same discipline as <see cref="SquegScanTests"/>: every behaviour asserted twice, once on
/// a model whose leveled bias is set correctly and once on one where it is not. A scanner that
/// silently finds nothing looks exactly like a healthy instrument.</para>
/// </summary>
public class LeveledSquegScanTests
{
    private const double Band2Low = 8e9;
    private const double Band3Mid = 17e9;

    /// <summary>Extended status byte #2 bit 6, as the model reports it.</summary>
    private static Func<double, double, bool> Unleveled(SimulatedSweeper model) =>
        (carrierHz, dbm) =>
        {
            model.CwGHz = carrierHz / 1e9;
            model.RequestedDbm = dbm;
            return model.IsUnleveled;
        };

    private static Func<double, double, (IReadOnlyList<(double, double)>, double)> Measuring(
        SimulatedSweeper model, double noiseFloorDbc = -70) =>
        (carrierHz, alcDbm) =>
        {
            var spurs = model.SpursAtLeveled(carrierHz / 1e9, alcDbm)
                .Select(s => (s.OffsetHz, s.Dbc))
                .ToList();

            return (spurs, noiseFloorDbc);
        };

    // --- Finding maximum leveled power ----------------------------------------------------------

    [Fact]
    public void MaximumLeveledPowerIsBracketedAndBisectedRatherThanEyeballed()
    {
        // Steps 39, 44 and 48 say to turn the knob up until just before UNLEVELED lights. The bit
        // is readable (Table 4-31), so the level is a recorded number rather than a judgement.
        var limit = LeveledSquegScanner.FindMaximumLeveledPower(
            Band2Low, dbm => dbm > 9.4, resolutionDb: 0.1);

        Assert.InRange(limit.MaxLeveledDbm, 9.3, 9.4);
        Assert.InRange(limit.FirstUnleveledDbm, 9.4, 9.5);
        Assert.True(limit.FirstUnleveledDbm - limit.MaxLeveledDbm <= 0.1);
    }

    [Fact]
    public void TheSearchNeverProbesAboveWhereTheBracketAlreadyWent()
    {
        var probed = new List<double>();

        var limit = LeveledSquegScanner.FindMaximumLeveledPower(
            Band2Low,
            dbm =>
            {
                probed.Add(dbm);
                return dbm > 9.4;
            });

        // The coarse pass stops at the first 1 dB rung that trips UNLEVELED -- +10 dBm here --
        // and every bisection probe lies inside the bracket below it. So refining the answer from
        // "somewhere under +10" to +9.4 never puts a higher level on the DUT's output than simply
        // bracketing it did.
        Assert.Equal(10.0, probed.Max(), 3);
        Assert.True(limit.FirstUnleveledDbm <= probed.Max(),
            $"The reported unleveled level of {limit.FirstUnleveledDbm:0.00} dBm was never "
            + $"actually probed -- the search only reached {probed.Max():0.00} dBm.");
    }

    [Fact]
    public void AgainstTheModelTheAnswerIsTheTableFourNineFigure()
    {
        var model = new SimulatedSweeper();

        var limit = LeveledSquegScanner.FindMaximumLeveledPower(
            Band2Low, dbm => Unleveled(model)(Band2Low, dbm));

        Assert.Equal(MaxLeveledPower.ForFrequency(model.Option, 8.0), limit.MaxSpecifiedDbm, 3);
        Assert.True(limit.MeetsSpecification);
    }

    [Fact]
    public void AnInstrumentThatWillNotLevelToSpecificationIsAFindingInItsOwnRight()
    {
        // Nothing to do with squegging: it is a Section IV failure, and it has to be visible
        // rather than silently lowering the ladder the scan then runs over.
        var limit = LeveledSquegScanner.FindMaximumLeveledPower(Band2Low, dbm => dbm > 4.0);

        Assert.False(limit.MeetsSpecification);
        Assert.Contains("NOT met", limit.Describe());
    }

    [Fact]
    public void UnleveledAtTheBottomOfTheLadderIsRefusedRatherThanReported()
    {
        // An open ALC loop, no RF, or the wrong bit being read. Returning a number here would
        // look like an answer and would set the whole scan's top rung to -20 dBm.
        var ex = Assert.Throws<InvalidOperationException>(
            () => LeveledSquegScanner.FindMaximumLeveledPower(Band2Low, _ => true));

        Assert.Contains("ALC loop", ex.Message);
    }

    [Fact]
    public void StillLevelWellAboveSpecificationMeansTheBitIsNotBeingRead()
    {
        // An 8340B does not level 6 dB above its own specification. Far more likely the status
        // read is wrong -- and continuing would run the whole test at the ceiling.
        var ex = Assert.Throws<InvalidOperationException>(
            () => LeveledSquegScanner.FindMaximumLeveledPower(Band2Low, _ => false));

        Assert.Contains("bit 6", ex.Message);
    }

    // --- The level ladder -----------------------------------------------------------------------

    [Fact]
    public void TheLadderStartsAtMaximumLeveledPowerThenFallsOntoTheFiveDecibelGrid()
    {
        // Steps 42, 46 and 50: "the maximum ALC power level that will be a 5 dB increment below
        // max leveled power (i.e., 15, 10, 5)" -- round numbers on the front panel, not
        // maximum-minus-five.
        var ladder = LeveledSquegScanner.LevelLadder(9.4);

        Assert.Equal(9.4, ladder[0], 3);
        Assert.Equal(5.0, ladder[1], 3);
        Assert.Equal(-20.0, ladder[^1], 3);
    }

    [Fact]
    public void AMaximumAlreadyOnTheGridIsNotVisitedTwice()
    {
        var ladder = LeveledSquegScanner.LevelLadder(10.0);

        Assert.Equal(10.0, ladder[0], 3);
        Assert.Equal(5.0, ladder[1], 3);
        Assert.Equal(ladder.Count, ladder.Distinct().Count());
    }

    [Fact]
    public void TheLadderStopsAtMinusTwentyDbm()
    {
        Assert.All(LeveledSquegScanner.LevelLadder(10.0), dbm => Assert.True(dbm >= -20.0));
        Assert.Contains(-20.0, LeveledSquegScanner.LevelLadder(10.0));
    }

    [Fact]
    public void AMaximumBelowTheFloorLeavesNoTestToRun()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LeveledSquegScanner.LevelLadder(-25.0));
    }

    // --- Classification -------------------------------------------------------------------------

    [Fact]
    public void AtOrBelowMaximumSpecifiedLeveledPowerAResponseIsAFault()
    {
        // 5-16 step 32: "If the ALC level is at or below the maximum specified leveled power,
        // adjust A24R5 (X2C) counter-clockwise to eliminate the squegging."
        var specified = MaxLeveledPower.ForFrequency(InstrumentOption.Standard, 8.0);

        var severity = LeveledSquegScanner.ClassifyLeveled(
            Band2Low, specified, InstrumentOption.Standard, dbc: -20, noiseFloorDbc: -70);

        Assert.Equal(SpurSeverity.Strong, severity);
    }

    [Fact]
    public void AboveMaximumSpecifiedLeveledPowerItIsTheOutputGoingUnleveled()
    {
        // Same step: "If the ALC level is above maximum leveled power, what you are seeing is
        // probably the RF output going unleveled and cannot be adjusted out." Turning the pot to
        // chase it leaves the instrument worse than it started.
        var specified = MaxLeveledPower.ForFrequency(InstrumentOption.Standard, 8.0);

        var severity = LeveledSquegScanner.ClassifyLeveled(
            Band2Low, specified + 0.5, InstrumentOption.Standard, dbc: -20, noiseFloorDbc: -70);

        Assert.Equal(SpurSeverity.Unleveled, severity);
        Assert.False(new SquegFinding(Band2Low, 50e6, -20, severity, null, -70, specified + 0.5)
            .IsActionable);
    }

    [Fact]
    public void TheLineIsDrawnAtTheSpecificationNotAtWhatThisInstrumentReaches()
    {
        // Option 001 is specified 2 dB higher in band 2, so the same level is a fault on one
        // option and not on the other. Judging against the instrument's own measured maximum
        // instead would make a weak instrument look compliant.
        var severity = LeveledSquegScanner.ClassifyLeveled(
            Band2Low, 11.0, InstrumentOption.Opt001, dbc: -20, noiseFloorDbc: -70);

        Assert.Equal(SpurSeverity.Strong, severity);

        Assert.Equal(SpurSeverity.Unleveled, LeveledSquegScanner.ClassifyLeveled(
            Band2Low, 11.0, InstrumentOption.Standard, dbc: -20, noiseFloorDbc: -70));
    }

    [Fact]
    public void AResponseAtTheAnalyzersOwnFloorIsStillNotAFinding()
    {
        Assert.Equal(SpurSeverity.None, LeveledSquegScanner.ClassifyLeveled(
            Band2Low, 0, InstrumentOption.Standard, dbc: -30, noiseFloorDbc: -25));
    }

    // --- Against the model, both ways -----------------------------------------------------------

    [Fact]
    public void AWellAdjustedInstrumentProducesNoFindings()
    {
        var model = new SimulatedSweeper();

        var result = LeveledSquegScanner.Scan(
            SquegScanner.ScanPoints([BandId.Band2]), Unleveled(model), Measuring(model));

        Assert.Empty(result.Findings);
        Assert.Empty(result.BelowSpecification);
        Assert.Contains("No responses above", result.Note!);
    }

    [Fact]
    public void AnOverBiasedLeveledPotIsFoundAndTheLeveledPotIsNamed()
    {
        // The leveled bias is a different control from the unleveled one on the same assembly, so
        // 5-16 must name X2C even though 5-14's scan names X2A and X2B.
        var model = new SimulatedSweeper();
        model.LeveledPotAt(8.0)!.Bias = 0.95;

        var result = LeveledSquegScanner.Scan(
            SquegScanner.ScanPoints([BandId.Band2]), Unleveled(model), Measuring(model));

        Assert.NotEmpty(result.Actionable);
        Assert.Equal(["X2C (A24R5)"], result.PotsToAdjust);
        Assert.DoesNotContain("X2A (A24R3)", result.PotsToAdjust);
    }

    [Fact]
    public void TheUnleveledPotsDoNotDecideTheLeveledResult()
    {
        // And the other way round. An instrument can pass 5-14 and fail 5-16.
        var model = new SimulatedSweeper();
        model.UnleveledPotAt(8.0)!.Bias = 1.0;

        var result = LeveledSquegScanner.Scan(
            SquegScanner.ScanPoints([BandId.Band2]), Unleveled(model), Measuring(model));

        Assert.Empty(result.Findings);
    }

    [Fact]
    public void FindingsAreConfinedToTheBandWhosePotIsWrong()
    {
        var model = new SimulatedSweeper();
        model.LeveledPotAt(Band3Mid / 1e9)!.Bias = 0.95;

        var band2 = LeveledSquegScanner.Scan(
            SquegScanner.ScanPoints([BandId.Band2]), Unleveled(model), Measuring(model));

        var band3 = LeveledSquegScanner.Scan(
            SquegScanner.ScanPoints([BandId.Band3]), Unleveled(model), Measuring(model));

        Assert.Empty(band2.Findings);
        Assert.Equal(["X3C (A24R8)"], band3.PotsToAdjust);
    }

    [Fact]
    public void AWorsePotSquegsFurtherDownTheLadder()
    {
        // The whole reason steps 42, 46 and 50 walk the level down. Testing only at maximum
        // leveled power would find both of these and rank them the same.
        var mild = new SimulatedSweeper();
        mild.LeveledPotAt(8.0)!.Bias = 0.78;

        var bad = new SimulatedSweeper();
        bad.LeveledPotAt(8.0)!.Bias = 0.95;

        var mildResult = LeveledSquegScanner.Scan([Band2Low], Unleveled(mild), Measuring(mild));
        var badResult = LeveledSquegScanner.Scan([Band2Low], Unleveled(bad), Measuring(bad));

        Assert.True(
            badResult.WorstLevelPerPot["X2C (A24R5)"] < mildResult.WorstLevelPerPot["X2C (A24R5)"],
            "A pot further past its threshold has to squeg at lower ALC levels, or severity "
            + "carries no information about how badly it is set.");
    }

    [Fact]
    public void OneCarrierIsTestedAtEveryRungOfTheLadder()
    {
        var model = new SimulatedSweeper();

        var result = LeveledSquegScanner.Scan([Band2Low], Unleveled(model), Measuring(model));

        // Maximum leveled power (+10 dBm on the standard option in band 2) plus +5 down to -20.
        Assert.Equal(1, result.PointsScanned);
        Assert.Equal(LeveledSquegScanner.LevelLadder(10.0).Count, result.LevelsScanned);
        Assert.Single(result.Limits);
    }

    [Fact]
    public void EveryFindingRecordsTheLevelItWasFoundAt()
    {
        // 5-16 step 32 turns on exactly this: "examine the ENTRY DISPLAY to determine the
        // requested ALC level". A finding without its level cannot be classified, let alone acted
        // on.
        var model = new SimulatedSweeper();
        model.LeveledPotAt(8.0)!.Bias = 0.95;

        var result = LeveledSquegScanner.Scan([Band2Low], Unleveled(model), Measuring(model));

        Assert.All(result.Findings, f => Assert.True(f.RequestedDbm is >= -20 and <= 10));
        Assert.True(result.Findings.Select(f => f.RequestedDbm).Distinct().Count() > 1,
            "The ladder was walked, so the findings should not all be at one level.");
    }

    [Fact]
    public void AFindingNamesTheFrequencyTheLevelAndWhatToTurn()
    {
        var model = new SimulatedSweeper();
        model.LeveledPotAt(8.0)!.Bias = 0.95;

        var line = LeveledSquegScanner.Scan([Band2Low], Unleveled(model), Measuring(model))
            .Findings[0].Describe();

        Assert.Contains("8.000 GHz", line);
        Assert.Contains("dBm", line);
        Assert.Contains("X2C (A24R5)", line);
        Assert.Contains("counter-clockwise", line);
    }

    [Fact]
    public void AResponseAboveSpecificationIsRecordedButNotActedOn()
    {
        // An instrument that levels above its own specification, which is what a healthy one
        // does. The top rung of its ladder is then above Table 4-9, and a response there is the
        // output going unleveled -- reported, kept out of the list of things to turn.
        const double actualMax = 14.0;

        var result = LeveledSquegScanner.Scan(
            [Band2Low],
            (_, dbm) => dbm > actualMax,
            (_, dbm) => (dbm > 12.0 ? [(60e6, -20.0)] : [], -70.0));

        Assert.NotEmpty(result.AboveMaxLeveled);
        Assert.Empty(result.Actionable);
        Assert.Empty(result.PotsToAdjust);
        Assert.Contains("going unleveled", result.Note!);
    }

    [Fact]
    public void ASquegAtTheSpecifiedMaximumIsActedOnEvenThoughTheInstrumentLevelsHigher()
    {
        // The other half of the same instrument: above the specification it is unleveled, at it
        // the pot is wrong. Both appear in one scan and only one is actionable.
        var result = LeveledSquegScanner.Scan(
            [Band2Low],
            (_, dbm) => dbm > 14.0,
            (_, dbm) => (dbm >= 10.0 ? [(60e6, -20.0)] : [], -70.0));

        Assert.NotEmpty(result.AboveMaxLeveled);
        Assert.NotEmpty(result.Actionable);
        Assert.All(result.Actionable, f => Assert.True(f.RequestedDbm <= 10.0));
        Assert.Equal(["X2C (A24R5)"], result.PotsToAdjust);
    }

    [Fact]
    public void ACarrierThatCannotReachSpecificationIsCalledOutInTheNote()
    {
        var result = LeveledSquegScanner.Scan(
            [Band2Low], (_, dbm) => dbm > 4.0, (_, _) => ([], -70.0));

        Assert.Single(result.BelowSpecification);
        Assert.Contains("Section IV failure", result.Note!);
    }

    [Fact]
    public void ALongScanCanBeStopped()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var model = new SimulatedSweeper();

        Assert.Throws<OperationCanceledException>(() => LeveledSquegScanner.Scan(
            SquegScanner.ScanPoints(), Unleveled(model), Measuring(model),
            cancellationToken: cancelled.Token));
    }

    [Fact]
    public void TheSafetyWarningSaysHowHighTheSearchWillGo()
    {
        // Rule 5. The search deliberately pushes past maximum leveled power to find where it is,
        // so "leveled" is not by itself a statement that the output is low.
        var warning = LeveledSquegScanner.SafetyWarning(InstrumentOption.Standard);

        Assert.Contains("UNLEVELED", warning);
        Assert.Contains("+17 dBm", warning);
        Assert.Contains("478A", warning);
    }
}
