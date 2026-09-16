using HP8340B.Instruments;
using HP8340B.Instruments.Config;
using HP8340B.Instruments.Model;
using HP8340B.Instruments.Visa;
using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// M0-23: the HP 437B, the second <b>automatable</b> absolute-power path. With an 8485A it is
/// what makes band 4 Spec rather than Relative.
/// </summary>
public class Hp437BDriverTests
{
    private static PowerSensor SensorWithChart(PowerSensor sensor)
    {
        // Plausible shape, NOT this sensor's real chart — those are per-sensor and are issue #6.
        sensor.ReferenceCalFactorPercent = 99.8;
        sensor.CalFactors.AddRange(
        [
            new CalFactor(50, 99.5),
            new CalFactor(1000, 98.0),
            new CalFactor(10000, 95.0),
            new CalFactor(18000, 92.0),
            new CalFactor(26500, 88.0),
        ]);

        return sensor;
    }

    private static (Hp437B Meter, SimulatedInstrumentLink Link) New(PowerSensor? sensor = null)
    {
        var link = SimulatedBench.LinkFor("HP 437B", "SIM::power-meter::INSTR");

        var meter = new Hp437B(link)
        {
            Sensor = sensor ?? SensorWithChart(PowerSensors.Hp8481A()),
            ZeroSettle = TimeSpan.Zero,
            CalibrateSettle = TimeSpan.Zero,
        };

        return (meter, link);
    }

    // --- Codes -----------------------------------------------------------------------------------

    [Fact]
    public void EveryCodeIsVerifiedAndCitedToTableThreeFive()
    {
        Assert.Empty(Hp437BCommands.Unverified);
        Assert.All(Hp437BCommands.All, c => Assert.Contains("Table 3-5", c.Source));
    }

    [Fact]
    public void AnUncitedCodeCannotBeSent()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Hp437BCommands.Require("QQ"));
        Assert.Contains("never fabricate", ex.Message);
    }

    [Fact]
    public void TheFourThirtyEightACompatibilityTrapIsRecordedPerCode()
    {
        // The manual warns that "use of the other available HP 437B HP-IB command codes may
        // inhibit the operation of an HP 438A", so which codes carry footnote 4 is information,
        // not decoration. ID, FR, SE and RE are among those that do not.
        Assert.False(Hp437BCommands.Find("ID")!.CompatibleWith438A);
        Assert.False(Hp437BCommands.Find("FR")!.CompatibleWith438A);
        Assert.True(Hp437BCommands.Find("ZE")!.CompatibleWith438A);
        Assert.True(Hp437BCommands.Find("CL")!.CompatibleWith438A);

        Assert.NotEmpty(Hp437BCommands.NotCompatibleWith438A);
    }

    [Fact]
    public void CodesNeedingAnEnterTerminatorAreMarked()
    {
        // Footnote 1: "requires a numeric entry followed by the program code EN".
        Assert.True(Hp437BCommands.Find("CL")!.NeedsEnter);
        Assert.True(Hp437BCommands.Find("KB")!.NeedsEnter);
        Assert.False(Hp437BCommands.Find("ZE")!.NeedsEnter);
    }

    // --- Zero and calibrate -------------------------------------------------------------------------

    [Fact]
    public void ZeroingTurnsTheReferenceOscillatorOffFirst()
    {
        // The manual's procedure has the sensor already on POWER REF when you zero, which is only
        // safe because PRESET leaves the oscillator off. Sending OC0 makes that explicit instead
        // of depending on the meter's state — zeroing with power on the sensor biases everything
        // afterwards, silently.
        var (meter, link) = New();
        meter.ZeroSensor();

        var oc0 = link.History.ToList().IndexOf("OC0");
        var ze = link.History.ToList().IndexOf("ZE");

        Assert.True(oc0 >= 0 && ze >= 0);
        Assert.True(oc0 < ze, "The reference must be off before zeroing.");
    }

    [Fact]
    public void CalibrationFollowsTheManualsOwnStepsAndLeavesTheReferenceOff()
    {
        // Section 3-9: CAL, key the REF CAL FACTOR, ENTER.
        var (meter, link) = New();
        meter.CalibrateSensor();

        Assert.Contains("OC1", link.History);
        Assert.Contains("CL99.8EN", link.History);
        Assert.Equal("OC0", link.History[^1]);
    }

    [Fact]
    public void CalibrationRefusesWithoutTheSensorsReferenceCalFactor()
    {
        // Step 4 of the procedure asks for it by name. Without it the meter would calibrate
        // against whatever it last had.
        var sensor = PowerSensors.Hp8481A();
        var (meter, _) = New(sensor);

        var ex = Assert.Throws<InvalidOperationException>(() => meter.CalibrateSensor());
        Assert.Contains("REF CAL FACTOR", ex.Message);
    }

    // --- Cal factors --------------------------------------------------------------------------------

    [Fact]
    public void CalFactorsAreEnteredWithKbAndAnEnterTerminator()
    {
        var (meter, link) = New();
        meter.SetCalFactorFor(1e9);

        Assert.Contains("KB98EN", link.History);
    }

    [Fact]
    public void CalFactorsInterpolateBetweenTheChartsPoints()
    {
        var sensor = SensorWithChart(PowerSensors.Hp8481A());

        Assert.Equal(98.0, sensor.CalFactorAt(1e9), 6);

        // Halfway between 1 GHz (98.0) and 10 GHz (95.0) in frequency is 5.5 GHz.
        Assert.Equal(96.5, sensor.CalFactorAt(5.5e9), 6);
    }

    [Fact]
    public void TwoSensorsNeverShareAChart()
    {
        // PowerSensors exposes methods rather than static instances because a sensor carries
        // per-unit state. This test exists because the first version used static instances, and
        // `record`'s `with` copies the reference to the cal-factor list rather than the list —
        // so entering one sensor's chart quietly rewrote the other's.
        var first = PowerSensors.Hp8481A();
        var second = PowerSensors.Hp8481A();

        first.CalFactors.Add(new CalFactor(1000, 98.0));
        first.Serial = "FIRST";

        Assert.Empty(second.CalFactors);
        Assert.Null(second.Serial);
    }

    [Fact]
    public void ASensorWithNoChartRefusesToGuess()
    {
        // Per-sensor data, printed on the sensor. Defaulting would bias every reading.
        var sensor = PowerSensors.Hp8485A();

        var ex = Assert.Throws<InvalidOperationException>(() => sensor.CalFactorAt(10e9));
        Assert.Contains("#6", ex.Message);
    }

    // --- The band-4 rule, which is the point of this driver ----------------------------------------

    [Theory]
    [InlineData(10e6, TraceabilityClass.Spec)]
    [InlineData(1e9, TraceabilityClass.Spec)]
    [InlineData(18e9, TraceabilityClass.Spec)]
    [InlineData(20e9, TraceabilityClass.NotPossible)]
    [InlineData(24e9, TraceabilityClass.NotPossible)]
    public void An8481AAboveEighteenGigahertzIsNotAMeasurement(double hz, TraceabilityClass expected) =>
        Assert.Equal(expected, Hp437B.TraceabilityAt(hz, PowerSensors.Hp8481A()));

    [Theory]
    [InlineData(1e9, TraceabilityClass.Spec)]
    [InlineData(24e9, TraceabilityClass.Spec)]
    [InlineData(26.5e9, TraceabilityClass.Spec)]
    [InlineData(10e6, TraceabilityClass.NotPossible)]   // the 8485A starts at 30 MHz
    public void The8485AReachesBandFourButNotTheVeryBottom(double hz, TraceabilityClass expected) =>
        Assert.Equal(expected, Hp437B.TraceabilityAt(hz, PowerSensors.Hp8485A()));

    [Fact]
    public void OnlyTheSensorThatReachesTwentySixPointFiveCanClaimBandFour()
    {
        var (withLowSensor, _) = New(SensorWithChart(PowerSensors.Hp8481A()));
        var (withHighSensor, _) = New(SensorWithChart(PowerSensors.Hp8485A()));

        Assert.False(withLowSensor.CanClaimSpecIn(BandId.Band4));
        Assert.True(withHighSensor.CanClaimSpecIn(BandId.Band4));

        // Both cover band 2.
        Assert.True(withLowSensor.CanClaimSpecIn(BandId.Band2));
        Assert.True(withHighSensor.CanClaimSpecIn(BandId.Band2));
    }

    [Fact]
    public void ReadingOutsideTheSensorsRangeIsRefusedRatherThanReturningANumber()
    {
        var (meter, _) = New(SensorWithChart(PowerSensors.Hp8481A()));

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => meter.ReadPower(24e9));
        Assert.Contains("not a measurement", ex.Message);
    }

    [Fact]
    public void TheReadingRecordsTheSensorAndCalFactorUsed()
    {
        // Repo rule 3. A power figure with no record of which sensor and which cal factor is not
        // defensible six months later.
        var (meter, _) = New();
        var reading = meter.ReadPower(1e9);

        Assert.Equal("HP 8481A", reading.Sensor);
        Assert.Equal(98.0, reading.CalFactorPercent, 6);
        Assert.Equal(TraceabilityClass.Spec, reading.Traceability);
        Assert.Equal(1e9, reading.Hz);
    }

    [Fact]
    public void APadIsAddedBackSoPowerIsReportedAtTheDut()
    {
        var (meter, _) = New();
        meter.PadDb = 10.0;

        // The simulator returns 0.00 dBm; through a 10 dB pad that is +10 dBm at the DUT.
        var reading = meter.ReadPower(1e9);

        Assert.Equal(10.0, reading.Dbm, 6);
        Assert.Contains("pad", reading.Note!);
    }

    // --- Rule 5 ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(18.0)]
    [InlineData(20.0)]
    [InlineData(25.0)]
    public void LevelsAboveSeventeenDbmIntoAnEightFourEightXSensorAreRefused(double dutDbm)
    {
        var (meter, _) = New();

        var ex = Assert.Throws<InvalidOperationException>(
            () => meter.GuardSensorLevel(dutDbm, padDeclaredAndCharacterised: false));

        Assert.Contains("rule 5", ex.Message);
        Assert.Contains("848x", ex.Message);
    }

    [Fact]
    public void SeventeenDbmItselfPasses()
    {
        var (meter, _) = New();
        meter.GuardSensorLevel(17.0, padDeclaredAndCharacterised: false);
    }

    [Fact]
    public void OnlyACharacterisedPadLiftsTheRefusal()
    {
        var (meter, _) = New();
        meter.PadDb = 20.0;

        Assert.Throws<InvalidOperationException>(
            () => meter.GuardSensorLevel(30.0, padDeclaredAndCharacterised: false));

        meter.GuardSensorLevel(30.0, padDeclaredAndCharacterised: true);
    }

    // --- Probe and factory --------------------------------------------------------------------------------

    [Fact]
    public void ProbeSaysWhetherTheFittedSensorReachesBandFour()
    {
        var (low, _) = New(SensorWithChart(PowerSensors.Hp8481A()));
        Assert.Contains("Does NOT reach band 4", low.Probe().Detail);

        var (high, _) = New(SensorWithChart(PowerSensors.Hp8485A()));
        Assert.Contains("Reaches band 4", high.Probe().Detail);
    }

    [Fact]
    public void ProbeFlagsMissingCalFactors()
    {
        var (meter, _) = New(PowerSensors.Hp8485A());
        Assert.Contains("Cal factors NOT entered", meter.Probe().Detail);
    }

    [Fact]
    public void ProbeUsesTheManualsIdentificationFormat()
    {
        var (meter, _) = New();
        var result = meter.Probe();

        Assert.True(result.Responded);
        Assert.Contains("437B", result.Identity);
    }

    [Fact]
    public void FactoryBuildsTheRealDriverInSimulation()
    {
        var config = new InstrumentConfig
        {
            Role = "power-meter", Model = "HP 437B", Address = "GPIB0::13::INSTR",
        };

        using var instrument = InstrumentFactory.Create(config, simulate: true);

        Assert.IsType<Hp437B>(instrument);
        Assert.True(instrument.Probe().Responded);
    }
}
