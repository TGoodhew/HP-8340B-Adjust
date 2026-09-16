using HP8340B.Instruments;
using HP8340B.Instruments.Model;
using HP8340B.Measurements.Squegging;
using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// M1-04 and M1-06, tested against the M0-12 model that actually misbehaves.
///
/// <para>These are the tests #24 existed for. A scanner that silently finds nothing looks exactly
/// like a healthy instrument, so each behaviour is asserted in both directions: nothing found on a
/// well-adjusted model, and the right thing found on a badly-adjusted one.</para>
/// </summary>
public class SquegScanTests
{
    private const double Band2Low = 8e9;     // X2A's half
    private const double Band2High = 12e9;   // X2B's half

    /// <summary>Wires the M0-12 model up as the scan's measurement delegate.</summary>
    private static Func<double, (IReadOnlyList<(double, double)>, double)> Measuring(
        SimulatedSweeper model, double requestedDbm = 20, double noiseFloorDbc = -70)
    {
        return carrierHz =>
        {
            var spurs = model.SpursAt(carrierHz / 1e9, requestedDbm)
                .Select(s => (s.OffsetHz, s.Dbc))
                .ToList();

            return (spurs, noiseFloorDbc);
        };
    }

    // --- Scan points ------------------------------------------------------------------------

    [Fact]
    public void TheScanWalksBandsTwoThreeAndFourInHundredMegahertzSteps()
    {
        var points = SquegScanner.ScanPoints();

        Assert.All(points, hz =>
        {
            var band = Bands.ForFrequency(hz / 1e9);
            Assert.NotNull(band);
            Assert.Contains(band!.Id, SquegScanner.ScannedBands);
        });

        // Band 2 is 7.0 to 13.5 GHz: 65 steps of 100 MHz.
        Assert.Equal(65, points.Count(hz => Bands.ForFrequency(hz / 1e9)!.Id == BandId.Band2));
    }

    [Fact]
    public void AFinerStepIsAllowed()
    {
        var coarse = SquegScanner.ScanPoints([BandId.Band2]);
        var fine = SquegScanner.ScanPoints([BandId.Band2], stepHz: 50e6);

        Assert.True(fine.Count > coarse.Count);
    }

    [Fact]
    public void BandZeroCannotSquegAtAllSoScanningItIsRefused()
    {
        // Heterodyne: the YO is mixed with the A8 oscillator, so there is no step recovery diode
        // and no multiplication. Scanning it is meaningless rather than merely unhelpful.
        var ex = Assert.Throws<ArgumentException>(() => SquegScanner.ScanPoints([BandId.Band0]));

        Assert.Contains("heterodyne", ex.Message);
    }

    [Fact]
    public void BandOneIsScannableButNotInTheDefaultSet()
    {
        // It is not part of steps 70-80, but refusing it outright would be wrong: its squegging is
        // real, just expected and unadjustable. Scanning it deliberately gives the right answer
        // rather than an error (M1-08).
        Assert.DoesNotContain(BandId.Band1, SquegScanner.ScannedBands);
        Assert.NotEmpty(SquegScanner.ScanPoints([BandId.Band1]));
    }

    // --- Classification ----------------------------------------------------------------------

    [Theory]
    [InlineData(-20, SpurSeverity.Strong)]
    [InlineData(-25, SpurSeverity.Strong)]
    [InlineData(-30, SpurSeverity.Warn)]
    [InlineData(-40, SpurSeverity.Warn)]
    [InlineData(-50, SpurSeverity.None)]
    public void SeverityFollowsTheThresholds(double dbc, SpurSeverity expected) =>
        Assert.Equal(expected, SquegScanner.Classify(dbc, noiseFloorDbc: -70));

    [Fact]
    public void AResponseAtOrBelowTheAnalyzersOwnFloorIsNotAFinding()
    {
        // It is not a measurement of anything. Reporting it would have the scanner chasing the
        // 8563E's noise around the band.
        Assert.Equal(SpurSeverity.None, SquegScanner.Classify(-30, noiseFloorDbc: -30));
        Assert.Equal(SpurSeverity.None, SquegScanner.Classify(-30, noiseFloorDbc: -25));
    }

    [Fact]
    public void TheCarrierIsNotASpuriousResponse()
    {
        // A peak search would find the carrier every time without this.
        Assert.True(SquegScanner.IsCarrier(0));
        Assert.True(SquegScanner.IsCarrier(4e6));
        Assert.False(SquegScanner.IsCarrier(20e6));
    }

    [Fact]
    public void HarmonicsAreNotBlamedOnTheSrdBias()
    {
        // The 8340B's harmonics are specified and expected. Finding one and naming a pot would
        // send somebody to turn something unrelated.
        Assert.True(SquegScanner.IsHarmonicallyRelated(10e9, offsetHz: 10e9));   // 2nd harmonic
        Assert.True(SquegScanner.IsHarmonicallyRelated(10e9, offsetHz: -5e9));   // subharmonic
        Assert.False(SquegScanner.IsHarmonicallyRelated(10e9, offsetHz: 60e6));  // a real spur
    }

    // --- Against the model, both ways ----------------------------------------------------------

    [Fact]
    public void AWellAdjustedInstrumentProducesNoFindings()
    {
        var model = new SimulatedSweeper();
        var result = SquegScanner.Scan(SquegScanner.ScanPoints([BandId.Band2]), Measuring(model));

        Assert.Empty(result.Findings);
        Assert.Contains("No responses above", result.Note!);
    }

    [Fact]
    public void AnOverBiasedPotIsFoundAndTheRightPotIsNamed()
    {
        // The other half, and the whole point of the scanner. X2A owns 7-10 GHz.
        var model = new SimulatedSweeper();
        model.UnleveledPotAt(8.0)!.Bias = 0.95;

        var result = SquegScanner.Scan(SquegScanner.ScanPoints([BandId.Band2]), Measuring(model));

        Assert.NotEmpty(result.Findings);
        Assert.Contains("X2A (A24R3)", result.PotsToAdjust);

        // And not the other half's pot, which is correctly set.
        Assert.DoesNotContain("X2B (A24R4)", result.PotsToAdjust);
    }

    [Fact]
    public void FindingsAreConfinedToTheHalfBandWhosePotIsWrong()
    {
        var model = new SimulatedSweeper();
        model.UnleveledPotAt(Band2High / 1e9)!.Bias = 0.95;

        var result = SquegScanner.Scan(SquegScanner.ScanPoints([BandId.Band2]), Measuring(model));

        Assert.NotEmpty(result.Findings);
        Assert.All(result.Findings, f => Assert.True(f.CarrierHz >= 10e9,
            $"A finding at {f.CarrierHz / 1e9:0.0} GHz is in X2A's half, whose pot is correct."));
    }

    [Fact]
    public void AWorseBiasProducesStrongerFindings()
    {
        // Severity has to track how bad it is, or the scanner can only say "something".
        var mild = new SimulatedSweeper();
        mild.UnleveledPotAt(8.0)!.Bias = 0.78;

        var bad = new SimulatedSweeper();
        bad.UnleveledPotAt(8.0)!.Bias = 1.0;

        var mildResult = SquegScanner.Scan([Band2Low], Measuring(mild));
        var badResult = SquegScanner.Scan([Band2Low], Measuring(bad));

        Assert.True(badResult.Findings.Max(f => f.Dbc) > mildResult.Findings.Max(f => f.Dbc));
        Assert.NotEmpty(badResult.Strong);
    }

    [Fact]
    public void EveryFindingRecordsTheAnalyzerFloorItWasJudgedAgainst()
    {
        // Rule 3, and what makes the Spec claim defensible: the result is only as good as the
        // dynamic range available at that point.
        var model = new SimulatedSweeper();
        model.UnleveledPotAt(8.0)!.Bias = 0.95;

        var result = SquegScanner.Scan([Band2Low], Measuring(model, noiseFloorDbc: -65));

        Assert.All(result.Findings, f => Assert.Equal(-65, f.AnalyzerNoiseFloorDbc));
    }

    [Fact]
    public void AFindingNamesTheFrequencyTheLevelAndWhatToTurn()
    {
        var model = new SimulatedSweeper();
        model.UnleveledPotAt(8.0)!.Bias = 0.95;

        var line = SquegScanner.Scan([Band2Low], Measuring(model)).Findings[0].Describe();

        Assert.Contains("8.000 GHz", line);
        Assert.Contains("dBc", line);
        Assert.Contains("counter-clockwise", line);
    }

    [Fact]
    public void TheSafetyWarningNamesTheSensorLimitsBeforeAnythingRuns()
    {
        // Rule 5: +20 dBm unleveled is about to appear at the DUT output.
        var warning = SquegScanner.SafetyWarning(20);

        Assert.Contains("UNLEVELED", warning);
        Assert.Contains("+17 dBm", warning);
        Assert.Contains("+7 dBm", warning);
    }

    [Fact]
    public void AScanCanBeCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => SquegScanner.Scan(
            SquegScanner.ScanPoints(), Measuring(new SimulatedSweeper()),
            cancellationToken: cts.Token));
    }
}

/// <summary>M1-06, the power-reversal test.</summary>
public class MonotonicityTests
{
    private static IReadOnlyList<RampPoint> Ramp(SimulatedSweeper model, double hz)
    {
        return MonotonicityTest.DefaultRamp(15)
            .Select(dbm =>
            {
                model.CwGHz = hz / 1e9;
                model.RequestedDbm = dbm;

                return new RampPoint(dbm, model.OutputPowerDbm(hz / 1e9, dbm), model.IsUnleveled);
            })
            .ToList();
    }

    [Fact]
    public void AWellAdjustedInstrumentIsMonotonicOrSimplyOutOfRange()
    {
        // At the top of the ramp the ALC runs out, which is expected. What must NOT appear is a
        // reversal.
        var model = new SimulatedSweeper();
        var result = MonotonicityTest.Examine(8e9, Ramp(model, 8e9));

        Assert.NotEqual(ReversalKind.Reversal, result.Kind);
    }

    [Fact]
    public void AnOverBiasedPotProducesReversalAndNamesIt()
    {
        var model = new SimulatedSweeper();
        model.UnleveledPotAt(8.0)!.Bias = 0.95;

        var result = MonotonicityTest.Examine(8e9, Ramp(model, 8e9));

        Assert.Equal(ReversalKind.Reversal, result.Kind);
        Assert.NotNull(result.OnsetDbm);
        Assert.True(result.WorstDropDb > 0);
        Assert.Equal("X2A (A24R3)", result.Pot);
        Assert.Contains("POWER REVERSAL", result.Describe());
    }

    [Fact]
    public void RunningOutOfAlcRangeIsNotReportedAsSquegging()
    {
        // The distinction that makes this test usable. Without consulting the UNLEVELED bit,
        // every band's top end would be reported as a fault.
        var points = new List<RampPoint>
        {
            new(0, 0, false),
            new(1, 1, false),
            new(2, 2, false),
            new(3, 2.0, true),      // stopped rising, UNLEVELED set
            new(4, 2.0, true),
        };

        var result = MonotonicityTest.Examine(8e9, points);

        Assert.Equal(ReversalKind.OutOfRange, result.Kind);
        Assert.Contains("not squegging", result.Describe());
    }

    [Fact]
    public void APlateauWithoutTheUnleveledBitIsNotCalledOutOfRange()
    {
        // If the ALC has NOT run out, an output that stops following the request is not explained
        // by the ALC, and saying so would hide it.
        var points = new List<RampPoint>
        {
            new(0, 0, false),
            new(1, 1, false),
            new(2, 1.0, false),
            new(3, 1.0, false),
        };

        Assert.Equal(ReversalKind.None, MonotonicityTest.Examine(8e9, points).Kind);
    }

    [Fact]
    public void SmallWobblesAreNotCalledReversals()
    {
        // Measurement noise between two 1 dB steps must not read as a fault.
        var points = new List<RampPoint>
        {
            new(0, 0.00, false),
            new(1, 1.05, false),
            new(2, 1.95, false),      // 0.1 dB below a perfect step, not a reversal
            new(3, 3.00, false),
        };

        Assert.Equal(ReversalKind.None, MonotonicityTest.Examine(8e9, points).Kind);
    }

    [Fact]
    public void TheOnsetIsTheLevelTheFallStartedFrom()
    {
        var points = new List<RampPoint>
        {
            new(-2, -2, false),
            new(-1, -1, false),
            new(0, 0, false),
            new(1, -3, false),        // falls between 0 and +1
        };

        var result = MonotonicityTest.Examine(8e9, points);

        Assert.Equal(ReversalKind.Reversal, result.Kind);
        Assert.Equal(0, result.OnsetDbm);
        Assert.Equal(3, result.WorstDropDb, 6);
    }

    [Fact]
    public void ARampOfOnePointIsRefused()
    {
        // One level proves nothing about which way the output moves.
        var ex = Assert.Throws<ArgumentException>(
            () => MonotonicityTest.Examine(8e9, [new RampPoint(0, 0, false)]));

        Assert.Contains("at least two points", ex.Message);
    }

    [Fact]
    public void TheDefaultFrequenciesIncludeTheHalfBandSplits()
    {
        // A pot set wrongly shows worst at the edge of its own half, and those are exactly the
        // frequencies a 100 MHz scan is least likely to land on.
        var frequencies = MonotonicityTest.DefaultFrequencies();

        Assert.Contains(10e9, frequencies);
        Assert.Contains(15e9, frequencies);
        Assert.Contains(23e9, frequencies);
    }

    [Fact]
    public void TheDefaultRampStartsAtMinusTwentyInOneDbSteps()
    {
        var ramp = MonotonicityTest.DefaultRamp(10);

        Assert.Equal(-20, ramp[0]);
        Assert.Equal(10, ramp[^1]);
        Assert.Equal(31, ramp.Count);
    }

    [Fact]
    public void ThisIsARelativeCheckNotAnAbsoluteOne()
    {
        var result = MonotonicityTest.Examine(8e9, [new RampPoint(0, 0, false), new RampPoint(1, 1, false)]);

        Assert.Equal(TraceabilityClass.Relative, result.Traceability);
    }

    [Fact]
    public void RunWalksEveryFrequency()
    {
        var model = new SimulatedSweeper();

        var results = MonotonicityTest.Run(
            [8e9, 12e9],
            MonotonicityTest.DefaultRamp(10),
            (hz, dbm) =>
            {
                model.CwGHz = hz / 1e9;
                model.RequestedDbm = dbm;
                return (model.OutputPowerDbm(hz / 1e9, dbm), model.IsUnleveled);
            });

        Assert.Equal(2, results.Count);
    }
}

/// <summary>
/// M1-08: band-1 squegging is real, expected, and not adjustable.
///
/// <para>The manual is explicit — it is a function of SYTM input power, occurs only at maximum
/// unleveled power, and cannot be adjusted out. Without this the unleveled scanner would report
/// band 1 as failing every time and send somebody hunting for a pot that does not exist.</para>
/// </summary>
public class BandOneSqueggingTests
{
    private static Func<double, (IReadOnlyList<(double, double)>, double)> Responses(double dbc) =>
        _ => ([(60e6, dbc)], -70.0);

    [Fact]
    public void BandOneAboveMaximumSpecifiedPowerIsExpectedNotAFault()
    {
        // Band 1's Standard-option limit is +12 dBm, so +20 dBm is well past it.
        var severity = SquegScanner.ClassifyInBand(
            carrierHz: 5e9, requestedDbm: 20, InstrumentOption.Standard,
            dbc: -20, noiseFloorDbc: -70);

        Assert.Equal(SpurSeverity.Expected, severity);
    }

    [Fact]
    public void BandOneAtOrBelowMaximumSpecifiedPowerIsStillWorthKnowingAbout()
    {
        // The manual says it only happens at maximum UNLEVELED power. Below the specified leveled
        // limit it should not squeg at all, so a response there is a real finding — even though
        // there is still no pot to turn.
        var severity = SquegScanner.ClassifyInBand(
            carrierHz: 5e9, requestedDbm: 5, InstrumentOption.Standard,
            dbc: -20, noiseFloorDbc: -70);

        Assert.Equal(SpurSeverity.Strong, severity);
    }

    [Fact]
    public void TheMultiplyingBandsAreNotGivenTheBandOneExcuse()
    {
        // The exemption is band 1's alone. Band 2 at +20 dBm is exactly what steps 70-80 test.
        var severity = SquegScanner.ClassifyInBand(
            carrierHz: 8e9, requestedDbm: 20, InstrumentOption.Standard,
            dbc: -20, noiseFloorDbc: -70);

        Assert.Equal(SpurSeverity.Strong, severity);
    }

    [Fact]
    public void AnExpectedFindingExplainsItselfAndNamesNoPot()
    {
        var result = SquegScanner.Scan(
            [5e9], Responses(-20), requestedDbm: 20, option: InstrumentOption.Standard);

        var finding = Assert.Single(result.Findings);

        Assert.Equal(SpurSeverity.Expected, finding.Severity);
        Assert.Null(finding.Pot);
        Assert.Contains("EXPECTED", finding.Describe());
        Assert.Contains("cannot be adjusted out", finding.Describe());
        Assert.Contains("do not go looking for a control", finding.Describe());
    }

    [Fact]
    public void AnExpectedFindingIsRecordedButNotActionable()
    {
        // Reported so the record is complete; kept out of the work list so nobody chases it.
        var result = SquegScanner.Scan(
            [5e9], Responses(-20), requestedDbm: 20, option: InstrumentOption.Standard);

        Assert.Single(result.Expected);
        Assert.Empty(result.Actionable);
        Assert.Empty(result.PotsToAdjust);
    }

    [Fact]
    public void AScanFindingOnlyTheExpectedCaseSaysThereIsNothingToAdjust()
    {
        var result = SquegScanner.Scan(
            SquegScanner.ScanPoints([BandId.Band1]), Responses(-20),
            requestedDbm: 20, option: InstrumentOption.Standard);

        Assert.NotEmpty(result.Findings);
        Assert.Contains("Nothing to adjust", result.Note!);
    }

    [Fact]
    public void TheOptionMovesTheThresholdBecauseTable49Does()
    {
        // Opt001's band-1 limit is +13 dBm against Standard's +12, so a request of +12.5 dBm is
        // above the limit on one and below it on the other.
        var standard = SquegScanner.ClassifyInBand(
            5e9, 12.5, InstrumentOption.Standard, -20, -70);

        var opt001 = SquegScanner.ClassifyInBand(
            5e9, 12.5, InstrumentOption.Opt001, -20, -70);

        Assert.Equal(SpurSeverity.Expected, standard);
        Assert.Equal(SpurSeverity.Strong, opt001);
    }

    [Fact]
    public void NeitherPotMapOffersAControlForBandOne()
    {
        // The reason the whole exemption exists, and it comes from the verified map rather than
        // being restated here.
        Assert.Null(Bands.UnleveledPotFor(5.0));
        Assert.Null(Bands.LeveledPotFor(5.0));
    }
}

/// <summary>
/// The scan points themselves. Found by a band-1 test that failed for a reason nothing to do with
/// band 1.
/// </summary>
public class ScanPointBoundaryTests
{
    [Theory]
    [InlineData(BandId.Band1)]
    [InlineData(BandId.Band2)]
    [InlineData(BandId.Band3)]
    [InlineData(BandId.Band4)]
    public void EveryPointLiesInTheBandThatGeneratedIt(BandId band)
    {
        // Accumulating 0.1 GHz from 2.3 drifts enough that the last point of band 1 rounded to
        // exactly 7.000 GHz, which is band 2's first frequency -- so it would have been handed
        // band 2's pot. The loop condition never caught it because the drift went the other way
        // from the comparison.
        foreach (var hz in SquegScanner.ScanPoints([band]))
        {
            var actual = Bands.ForFrequency(hz / 1e9);

            Assert.NotNull(actual);
            Assert.True(actual!.Id == band,
                $"{hz / 1e9:0.000} GHz was generated for band {(int)band} but belongs to "
                + $"band {(int)actual.Id}.");
        }
    }

    [Fact]
    public void TheLastPointOfABandIsBelowTheNextBandsEdge()
    {
        var band1 = SquegScanner.ScanPoints([BandId.Band1]);

        Assert.True(band1[^1] < 7e9, $"Last band-1 point was {band1[^1] / 1e9:0.000} GHz.");
    }

    [Fact]
    public void PointsAreStillEvenlySpacedAndStartAtTheBandEdge()
    {
        var points = SquegScanner.ScanPoints([BandId.Band2]);

        Assert.Equal(7e9, points[0]);
        Assert.Equal(100e6, points[1] - points[0]);
    }
}
