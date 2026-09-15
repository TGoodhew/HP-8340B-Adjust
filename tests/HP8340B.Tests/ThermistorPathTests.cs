using HP8340B.Instruments;
using HP8340B.Instruments.Model;
using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// M0-13: the 432A/478A thermistor path read through the 3458A. The independent cross-check
/// below 10 GHz, and the only sensor this bench has for 10-50 MHz.
/// </summary>
public class ThermistorPathTests
{
    /// <summary>
    /// A mount with plausible label data. These are NOT this mount's real figures — those are
    /// printed on it and are decision D-06 — but the shape is right: six points, 10 MHz to
    /// 10 GHz, Effective Efficiency always at or above Calibration Factor.
    /// </summary>
    private static ThermistorMount MountWithPlaceholderLabel()
    {
        var mount = new ThermistorMount { Serial = "PLACEHOLDER" };
        mount.Calibration.AddRange(
        [
            new MountCalibrationPoint(10e6, 99.0, 99.5),
            new MountCalibrationPoint(100e6, 98.5, 99.2),
            new MountCalibrationPoint(1e9, 97.5, 98.6),
            new MountCalibrationPoint(3e9, 96.5, 98.0),
            new MountCalibrationPoint(7e9, 95.0, 97.2),
            new MountCalibrationPoint(10e9, 93.5, 96.5),
        ]);

        return mount;
    }

    private static (Hp432A Meter, SimulatedThermistorPath Path) New(double incidentWatts = 1e-3)
    {
        var path = new SimulatedThermistorPath { IncidentWatts = incidentWatts };
        var dmm = new Hp3458A(path.CreateDmmLink());

        var meter = new Hp432A(dmm, path, MountWithPlaceholderLabel())
        {
            // Tests should not sit through a thermistor's real settling time.
            Settle = TimeSpan.Zero,
        };

        return (meter, path);
    }

    // --- The substitution formula ------------------------------------------------------------

    [Fact]
    public void TheFormulaMatchesTheManualsAlgebra()
    {
        // 432A paragraph 3-30h:  P = (1/4R)[2 V_COMP (V1 - V0) + V0^2 - V1^2] / correction.
        // Worked by hand: R = 200, V_COMP = 3, V0 = 0, V1 = 0.1
        //   bracket = 2*3*0.1 + 0 - 0.01 = 0.59
        //   P = 0.59 / 800 = 737.5 uW
        var watts = Hp432A.SubstitutedWatts(vComp: 3.0, v0: 0.0, v1: 0.1, resistanceOhms: 200);

        Assert.Equal(737.5e-6, watts, 12);
    }

    [Fact]
    public void MoreRfMeansALargerBridgeDifference()
    {
        // RF heats the thermistor, its resistance falls, the bridge drives less DC into it, V_RF
        // drops and so V_COMP - V_RF rises. A model with this backwards would still "work" and
        // report power with the wrong sign of sensitivity, so it is worth pinning down.
        var low = Hp432A.V1ForSubstitutedWatts(0.5e-3, vComp: 3.0, v0: 0, resistanceOhms: 200);
        var high = Hp432A.V1ForSubstitutedWatts(2e-3, vComp: 3.0, v0: 0, resistanceOhms: 200);

        Assert.True(high > low);
    }

    [Theory]
    [InlineData(1e-6)]
    [InlineData(100e-6)]
    [InlineData(1e-3)]
    [InlineData(10e-3)]
    public void TheInverseRoundTripsThroughTheFormula(double watts)
    {
        var v1 = Hp432A.V1ForSubstitutedWatts(watts, vComp: 3.0, v0: 0.0004, resistanceOhms: 200);
        var back = Hp432A.SubstitutedWatts(3.0, 0.0004, v1, 200);

        Assert.Equal(watts, back, 12);
    }

    [Fact]
    public void ThereIsAPowerTheBridgeSimplyCannotSubstitute()
    {
        // The bridge cannot give up more DC power than it started with, and the algebra says so
        // by going imaginary. Better a refusal than a NaN propagating into a session record.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Hp432A.V1ForSubstitutedWatts(1.0, vComp: 3.0, v0: 0, resistanceOhms: 200));
    }

    [Fact]
    public void ANegativeMountResistanceIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Hp432A.SubstitutedWatts(3.0, 0, 0.1, resistanceOhms: 0));

    // --- The measurement, end to end -----------------------------------------------------------

    [Fact]
    public void SubstitutionRecoversTheIncidentPower()
    {
        // The simulator owns the true incident power and produces the voltages that would give
        // it, so this exercises the whole chain rather than comparing against a constant.
        var (meter, path) = New(incidentWatts: 1.234e-3);
        path.CorrectionFraction = meter.Mount.CorrectionAt(1e9, ThermistorCorrection.CalibrationFactor);

        path.RfOn = false;
        meter.ZeroWithRfOff();

        path.RfOn = true;
        var reading = meter.ReadSubstitutionWatts(1e9);

        Assert.Equal(1.234e-3, reading.Watts, 9);
        Assert.Equal(ThermistorMode.Substitution, reading.Mode);
        Assert.Equal(TraceabilityClass.Spec, reading.Traceability);
    }

    [Fact]
    public void ItTakesTheManualsThreeMeasurementsInOrder()
    {
        var (meter, path) = New();

        path.RfOn = false;
        meter.ZeroWithRfOff();
        path.RfOn = true;
        meter.ReadSubstitutionWatts(1e9);

        // 3-30: differential with RF off (V0), differential with RF on (V1), then V_COMP to ground.
        Assert.Equal(
            [
                ThermistorConnection.DifferentialCompMinusRf,
                ThermistorConnection.DifferentialCompMinusRf,
                ThermistorConnection.CompToGround,
            ],
            path.History);
    }

    [Fact]
    public void ReadingWithoutAZeroIsRefused()
    {
        // V0 has no sensible default, and a substitution reading without it is arithmetic on an
        // invented number.
        var (meter, _) = New();

        var ex = Assert.Throws<InvalidOperationException>(() => meter.ReadSubstitutionWatts(1e9));
        Assert.Contains("No zero has been taken", ex.Message);
    }

    [Fact]
    public void AStaleZeroIsRefused()
    {
        // A thermistor bridge drifts with ambient temperature and the accuracy claim rests on the
        // zero being recent, so this expires rather than quietly reusing an old one.
        var (meter, path) = New();
        meter.ZeroValidFor = TimeSpan.Zero;

        path.RfOn = false;
        meter.ZeroWithRfOff();
        path.RfOn = true;

        var ex = Assert.Throws<InvalidOperationException>(() => meter.ReadSubstitutionWatts(1e9));
        Assert.Contains("Re-zero", ex.Message);
    }

    [Fact]
    public void AFreshZeroIsAccepted()
    {
        var (meter, path) = New();

        path.RfOn = false;
        meter.ZeroWithRfOff();

        Assert.True(meter.ZeroIsFresh);
        Assert.NotNull(meter.Zero);
    }

    [Fact]
    public void ACharacterisedPadIsAddedBackSoThePowerIsReportedAtTheDut()
    {
        var (meter, path) = New(incidentWatts: 1e-3);
        path.CorrectionFraction = meter.Mount.CorrectionAt(1e9, ThermistorCorrection.CalibrationFactor);
        meter.Mount.PadDb = 10.0;

        path.RfOn = false;
        meter.ZeroWithRfOff();
        path.RfOn = true;

        // 1 mW at the mount through a 10 dB pad is 10 mW at the DUT.
        var reading = meter.ReadSubstitutionWatts(1e9);

        Assert.Equal(10.0, reading.Dbm, 6);
        Assert.Contains("pad", reading.Note!);
    }

    // --- Correction factor -----------------------------------------------------------------------

    [Fact]
    public void CalibrationFactorIsTheDefaultBecauseThereIsNoTunerOnThisBench()
    {
        // The 432A's formula prints EFFECTIVE EFFICIENCY under the line, but the 478A manual says
        // that applies only with a tuner (paragraph 35) and Calibration Factor applies to "all
        // measurements made without a tuner" (paragraph 33). Effective Efficiency is always the
        // larger, so using it here would report every power low.
        var (meter, _) = New();

        Assert.Equal(ThermistorCorrection.CalibrationFactor, meter.Correction);

        var cf = meter.Mount.CorrectionAt(3e9, ThermistorCorrection.CalibrationFactor);
        var ee = meter.Mount.CorrectionAt(3e9, ThermistorCorrection.EffectiveEfficiency);

        Assert.True(ee > cf);
    }

    [Fact]
    public void UsingEffectiveEfficiencyWithoutATunerWouldReportPowerLow()
    {
        // Worth demonstrating rather than only asserting, because the error is a few percent —
        // small enough to look like a legitimate disagreement between sensors.
        var (meter, path) = New(incidentWatts: 1e-3);
        path.CorrectionFraction = meter.Mount.CorrectionAt(7e9, ThermistorCorrection.CalibrationFactor);

        path.RfOn = false;
        meter.ZeroWithRfOff();
        path.RfOn = true;

        var correct = meter.ReadSubstitutionWatts(7e9);

        meter.Correction = ThermistorCorrection.EffectiveEfficiency;
        var wrong = meter.ReadSubstitutionWatts(7e9);

        Assert.True(wrong.Watts < correct.Watts);
        Assert.Equal(ThermistorCorrection.EffectiveEfficiency, wrong.CorrectionUsed);
    }

    [Fact]
    public void TheReadingRecordsWhichFactorWasUsedAndItsValue()
    {
        // Repo rule 3: a measurement carries what it was corrected by, or it cannot be defended
        // six months later.
        var (meter, path) = New();

        path.RfOn = false;
        meter.ZeroWithRfOff();
        path.RfOn = true;

        var reading = meter.ReadSubstitutionWatts(1e9);

        Assert.Equal(ThermistorCorrection.CalibrationFactor, reading.CorrectionUsed);
        Assert.Equal(0.975, reading.CorrectionFraction, 6);
        Assert.Equal(1e9, reading.Hz);
    }

    [Fact]
    public void CalibrationFactorInterpolatesBetweenTheLabelsPoints()
    {
        // 478A paragraph 31: the mounts are swept-tested specifically so interpolation between
        // the printed points is valid, so this is the manual's own method rather than a guess.
        var mount = MountWithPlaceholderLabel();

        // Halfway between the 1 GHz (97.5%) and 3 GHz (96.5%) points.
        Assert.Equal(0.97, mount.CorrectionAt(2e9, ThermistorCorrection.CalibrationFactor), 6);

        // Exactly on a point.
        Assert.Equal(0.975, mount.CorrectionAt(1e9, ThermistorCorrection.CalibrationFactor), 6);
    }

    [Fact]
    public void AMountWithNoLabelDataRefusesToGuess()
    {
        // The figures are per-mount and printed on the mount. Defaulting to anything would bias
        // every reading by an unknown few percent, silently.
        var meter = new Hp432A(
            new Hp3458A(new SimulatedThermistorPath().CreateDmmLink()),
            new SimulatedThermistorPath());

        var ex = Assert.Throws<InvalidOperationException>(
            () => meter.Mount.CorrectionAt(1e9, ThermistorCorrection.CalibrationFactor));

        Assert.Contains("D-06", ex.Message);
    }

    [Theory]
    [InlineData(1e6)]        // below 10 MHz
    [InlineData(11e9)]       // above 10 GHz
    public void FrequenciesOutsideTheMountsRangeAreRefused(double hz)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => MountWithPlaceholderLabel().CorrectionAt(hz, ThermistorCorrection.CalibrationFactor));

        Assert.Contains("8485A", ex.Message);
    }

    [Theory]
    [InlineData(10e6, TraceabilityClass.Spec)]
    [InlineData(50e6, TraceabilityClass.Spec)]
    [InlineData(10e9, TraceabilityClass.Spec)]
    [InlineData(12e9, TraceabilityClass.NotPossible)]
    [InlineData(1e6, TraceabilityClass.NotPossible)]
    public void TraceabilityFollowsTheMountsRange(double hz, TraceabilityClass expected) =>
        Assert.Equal(expected, Hp432A.TraceabilityAt(hz));

    // --- Meter mode and its unconfirmed scaling -----------------------------------------------------

    [Fact]
    public void MeterModeIsTypicalUntilTheScalingHasBeenConfirmed()
    {
        // The acceptance criterion: the recorder-output scaling carries a marker until it is
        // confirmed against a substitution reading at the same power.
        var (meter, path) = New();
        path.CorrectionFraction = meter.Mount.CorrectionAt(1e9, ThermistorCorrection.CalibrationFactor);

        var reading = meter.ReadMeterWatts(1e9);

        Assert.Equal(TraceabilityClass.Typical, reading.Traceability);
        Assert.Contains("UNCONFIRMED", reading.Note!);
        Assert.False(meter.MeterScalingConfirmed);
    }

    [Fact]
    public void ConfirmingTheScalingAgainstSubstitutionPromotesItToSpec()
    {
        var (meter, path) = New(incidentWatts: 1e-3);
        path.CorrectionFraction = meter.Mount.CorrectionAt(1e9, ThermistorCorrection.CalibrationFactor);

        var beforeMeter = meter.ReadMeterWatts(1e9);

        path.RfOn = false;
        meter.ZeroWithRfOff();
        path.RfOn = true;
        var substitution = meter.ReadSubstitutionWatts(1e9);

        Assert.True(meter.ConfirmMeterScaling(beforeMeter, substitution));

        var after = meter.ReadMeterWatts(1e9);
        Assert.Equal(TraceabilityClass.Spec, after.Traceability);
        Assert.Contains("confirmed", after.Note!);
    }

    [Fact]
    public void AScalingErrorIsCaughtRatherThanConfirmed()
    {
        // The other half: if the two methods disagree, confirmation has to fail. Here the RANGE
        // switch is declared one position too high, which is the most likely real mistake since
        // the switch cannot be read over the bus.
        var (meter, path) = New(incidentWatts: 1e-3);
        path.CorrectionFraction = meter.Mount.CorrectionAt(1e9, ThermistorCorrection.CalibrationFactor);

        meter.Range = ThermistorRange.All[5];   // declared +5 dBm; the meter is actually on 0 dBm
        var wrongMeter = meter.ReadMeterWatts(1e9);

        path.RfOn = false;
        meter.ZeroWithRfOff();
        path.RfOn = true;
        var substitution = meter.ReadSubstitutionWatts(1e9);

        Assert.False(meter.ConfirmMeterScaling(wrongMeter, substitution));
        Assert.False(meter.MeterScalingConfirmed);
    }

    [Fact]
    public void ConfirmationRefusesTwoReadingsOfTheSameKind()
    {
        var (meter, path) = New();
        path.CorrectionFraction = meter.Mount.CorrectionAt(1e9, ThermistorCorrection.CalibrationFactor);

        var a = meter.ReadMeterWatts(1e9);
        var b = meter.ReadMeterWatts(1e9);

        Assert.Throws<ArgumentException>(() => meter.ConfirmMeterScaling(a, b));
    }

    [Fact]
    public void MeterModeReadsTheRecorderOutput()
    {
        var (meter, path) = New();
        path.CorrectionFraction = meter.Mount.CorrectionAt(1e9, ThermistorCorrection.CalibrationFactor);

        meter.ReadMeterWatts(1e9);

        Assert.Equal(ThermistorConnection.RecorderToGround, path.History[^1]);
    }

    [Fact]
    public void TheReadingSaysWhichModeProducedIt()
    {
        // The session has to record which of the two methods was used, because they have very
        // different accuracy: 0.2% of reading for substitution, 1% of full scale for the meter.
        var (meter, path) = New();
        path.CorrectionFraction = meter.Mount.CorrectionAt(1e9, ThermistorCorrection.CalibrationFactor);

        Assert.Equal(ThermistorMode.Meter, meter.ReadMeterWatts(1e9).Mode);

        path.RfOn = false;
        meter.ZeroWithRfOff();
        path.RfOn = true;
        Assert.Equal(ThermistorMode.Substitution, meter.ReadSubstitutionWatts(1e9).Mode);
    }

    // --- Ranges ------------------------------------------------------------------------------------

    [Fact]
    public void TheSevenRangesAreExactFiveDbSteps()
    {
        // Table 1-1: 10, 30, 100, 300 uW, 1, 3, 10 mW, also -20 dBm to +10 dBm in 5 dB steps.
        Assert.Equal(7, ThermistorRange.All.Count);

        Assert.Equal(
            [-20.0, -15.0, -10.0, -5.0, 0.0, 5.0, 10.0],
            ThermistorRange.All.Select(r => r.FullScaleDbm));
    }

    [Fact]
    public void TheRoundedPanelLegendsAreNotUsedForScaling()
    {
        // "30 uW" is really 31.62 uW, "300 uW" is 316.2 and "3 mW" is 3.162. Taking the printed
        // legend literally would put a 5% (0.23 dB) error into meter mode on three of the seven
        // ranges -- larger than the meter's own 1%-of-full-scale accuracy, and it would look like
        // a disagreement between sensors rather than a bug.
        Assert.Equal(31.6228e-6, ThermistorRange.All[1].FullScaleWatts, 9);
        Assert.Equal(316.228e-6, ThermistorRange.All[3].FullScaleWatts, 9);
        Assert.Equal(3.16228e-3, ThermistorRange.All[5].FullScaleWatts, 8);

        // And the round ones really are round.
        Assert.Equal(10e-6, ThermistorRange.All[0].FullScaleWatts, 12);
        Assert.Equal(1e-3, ThermistorRange.All[4].FullScaleWatts, 12);
        Assert.Equal(10e-3, ThermistorRange.All[6].FullScaleWatts, 12);
    }

    [Theory]
    [InlineData(5e-6, -20.0)]
    [InlineData(10e-6, -20.0)]
    [InlineData(11e-6, -15.0)]
    [InlineData(2e-3, 5.0)]
    [InlineData(10e-3, 10.0)]
    public void RangeSelectionPicksTheLowestThatFits(double watts, double expectedFullScaleDbm) =>
        Assert.Equal(expectedFullScaleDbm, ThermistorRange.ForPower(watts).FullScaleDbm);

    [Fact]
    public void AboveFullScaleThereIsNoRangeAndTheMessageSaysWhy()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => ThermistorRange.ForPower(20e-3));
        Assert.Contains("30 mW", ex.Message);
    }

    // --- Rule 5 ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(7.1)]
    [InlineData(10.0)]
    [InlineData(20.0)]
    public void AnyDutLevelAboveSevenDbmIsRefusedOutright(double dutDbm)
    {
        // Repo rule 5, the hardest refusal in the project: the 478A is a 10 mW mount and its own
        // manual says never to apply more than 30 mW. This one destroys hardware, so it throws
        // rather than warning.
        var mount = new ThermistorMount();

        var ex = Assert.Throws<InvalidOperationException>(
            () => mount.GuardMountLevel(dutDbm, padDeclaredAndCharacterised: false));

        Assert.Contains("rule 5", ex.Message);
        Assert.Contains("478A", ex.Message);
    }

    [Theory]
    [InlineData(7.0)]
    [InlineData(0.0)]
    [InlineData(-20.0)]
    public void LevelsAtOrBelowSevenDbmPass(double dutDbm) =>
        new ThermistorMount().GuardMountLevel(dutDbm, padDeclaredAndCharacterised: false);

    [Fact]
    public void OnlyACharacterisedPadLiftsTheRefusal()
    {
        // A pad assumed from its label is not a measurement. The flag is separate from PadDb on
        // purpose, exactly as it is for the 8902A.
        var mount = new ThermistorMount { PadDb = 20.0 };

        Assert.Throws<InvalidOperationException>(
            () => mount.GuardMountLevel(20.0, padDeclaredAndCharacterised: false));

        mount.GuardMountLevel(20.0, padDeclaredAndCharacterised: true);
    }

    [Fact]
    public void EvenWithAPadTheLevelAtTheMountIsWhatCounts()
    {
        var mount = new ThermistorMount { PadDb = 10.0 };

        // +20 dBm through 10 dB is +10 dBm at the mount, still above the threshold.
        Assert.Throws<InvalidOperationException>(
            () => mount.GuardMountLevel(20.0, padDeclaredAndCharacterised: true));

        mount.GuardMountLevel(17.0, padDeclaredAndCharacterised: true);
    }

    // --- Connections ------------------------------------------------------------------------------------

    [Fact]
    public void TheManualSelectorTellsAPersonExactlyWhatToConnect()
    {
        var instructions = new List<string>();
        var selector = new ManualThermistorConnections(instructions.Add);

        selector.Select(ThermistorConnection.DifferentialCompMinusRf);

        Assert.False(selector.IsAutomatic);
        Assert.Single(instructions);

        // The floating-input warning matters: the 432A manual is explicit about it, and getting
        // it wrong puts a ground loop across the bridge.
        Assert.Contains("float", instructions[0]);
        Assert.Contains("V COMP", instructions[0]);
    }

    [Fact]
    public void TheSimulatedPathIsAutomatic() =>
        Assert.True(new SimulatedThermistorPath().IsAutomatic);

    [Fact]
    public void EveryConnectionHasAnInstruction() =>
        Assert.All(
            Enum.GetValues<ThermistorConnection>(),
            c => Assert.False(string.IsNullOrWhiteSpace(ManualThermistorConnections.Describe(c))));
}
