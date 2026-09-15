using HP8340B.Instruments;
using HP8340B.Instruments.Visa;
using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// M0-09: the 8902A in RF-power mode with the 11792A, plus the 11713A. Codes carried from
/// HP-Attenuator's hardware-proven driver.
/// </summary>
public class Hp8902ADriverTests
{
    private static (Hp8902A Rx, SimulatedInstrumentLink Link) New()
    {
        var link = SimulatedBench.LinkFor("HP 8902A", "SIM::measuring-receiver::INSTR");
        var rx = new Hp8902A(link)
        {
            // Tests should not actually sleep for eight seconds.
            ZeroSettle = TimeSpan.Zero,
            CalibrateSettle = TimeSpan.Zero,
        };
        return (rx, link);
    }

    [Fact]
    public void ZeroingTurnsTheCalibratorOffFirst()
    {
        // There must be no RF on the sensor while zeroing, so C0 has to precede ZR.
        var (rx, link) = New();
        rx.ZeroSensor();

        var c0 = link.History.ToList().IndexOf("C0");
        var zr = link.History.ToList().IndexOf("ZR");

        Assert.True(c0 >= 0 && zr >= 0);
        Assert.True(c0 < zr, "Calibrator must be off before zeroing.");
    }

    [Fact]
    public void CalibrationUsesThe50MhzReferenceAndLeavesItOff()
    {
        var (rx, link) = New();
        var dbm = rx.CalibrateSensor();

        Assert.Equal(0.0, dbm, 1);            // 1 mW is 0 dBm
        Assert.Contains("C1", link.History);
        Assert.Equal("C0", link.History[^1]); // calibrator left off afterwards
    }

    [Fact]
    public void CalibrationRefusesIfTheSensorIsNotOnTheCalibrator()
    {
        // Carried from HP-Attenuator: saving here with the sensor elsewhere corrupts the sensor
        // calibration and poisons every later reading. A silent error is the worst outcome.
        var link = new SimulatedInstrumentLink("SIM::rx::INSTR", _ => "1.0E-9");  // -60 dBm
        var rx = new Hp8902A(link) { CalibrateSettle = TimeSpan.Zero };

        var ex = Assert.Throws<InvalidOperationException>(() => rx.CalibrateSensor());

        Assert.Contains("CALIBRATION RF POWER OUTPUT", ex.Message);
        Assert.Contains("Refusing to save", ex.Message);
        Assert.Equal("C0", link.History[^1]);   // and it turns the calibrator back off
    }

    [Fact]
    public void CalFactorsSetTheReferenceBeforeThePairs()
    {
        // The reference cal factor is a SEPARATE store, entered value-only with no MZ, and it is
        // what clears the instrument's Error 15 ("no cal factors stored").
        var (rx, link) = New();

        rx.LoadCalFactors(97.4, [new CalFactor(1000, 96.5), new CalFactor(10000, 92.1)]);

        Assert.Contains("37.9SP", link.History);                        // clear the table first
        Assert.Contains("37.3SP97.40CF", link.History);                 // reference: no MZ
        Assert.Contains("37.3SP1000MZ96.50CF", link.History);           // pair: freq MZ cf CF
        Assert.Contains("37.3SP10000MZ92.10CF", link.History);

        var clear = link.History.ToList().IndexOf("37.9SP");
        var reference = link.History.ToList().IndexOf("37.3SP97.40CF");
        Assert.True(clear < reference, "Table must be cleared before the reference is set.");
    }

    [Fact]
    public void SensorLevelGuardRefusesAbove17Dbm()
    {
        // Repo rule 5: refuse above +17 dBm with the 11792A or an 848x sensor.
        var (rx, _) = New();

        var ex = Assert.Throws<InvalidOperationException>(
            () => rx.GuardSensorLevel(20.0, padDeclaredAndCharacterised: false));
        Assert.Contains("rule 5", ex.Message);
        Assert.Contains("no characterised pad", ex.Message);

        // Safe levels pass.
        rx.GuardSensorLevel(10.0, padDeclaredAndCharacterised: false);
    }

    [Fact]
    public void GuardOnlyCreditsAPadThatHasActuallyBeenCharacterised()
    {
        // A pad assumed from its label is not a measurement. The flag is separate from PadDb on
        // purpose, so "I have a 20 dB pad somewhere" cannot lift the refusal.
        var (rx, _) = New();
        rx.PadDb = 20.0;

        Assert.Throws<InvalidOperationException>(
            () => rx.GuardSensorLevel(30.0, padDeclaredAndCharacterised: false));

        rx.GuardSensorLevel(30.0, padDeclaredAndCharacterised: true);
    }

    [Fact]
    public void PadIsAddedBackSoPowerIsReportedAtTheDut()
    {
        var (rx, _) = New();
        rx.PadDb = 10.0;

        // 1 mW at the sensor through a 10 dB pad is +10 dBm at the DUT.
        Assert.Equal(10.0, rx.ReadRfPowerDbm(), 1);
    }

    [Theory]
    // The 11792A covers 50 MHz to 18 GHz. Above that the 11793A tuned-RF-level path is relative.
    [InlineData(1e9, "Spec")]
    [InlineData(17.9e9, "Spec")]
    [InlineData(50e6, "Spec")]
    [InlineData(18.1e9, "Relative")]
    [InlineData(25e9, "Relative")]
    [InlineData(10e6, "Relative")]
    public void TraceabilityFollowsTheSensorModulesRange(double hz, string expected) =>
        Assert.Equal(expected, Hp8902A.TraceabilityAt(hz));

    [Theory]
    [InlineData(1e-3, 0.0)]
    [InlineData(1.0, 30.0)]
    [InlineData(1e-6, -30.0)]
    public void WattsToDbmIsCorrect(double watts, double dbm) =>
        Assert.Equal(dbm, Hp8902A.WattsToDbm(watts), 6);

    [Fact]
    public void ZeroWattsHasNoDbmValue() =>
        Assert.Equal(double.NegativeInfinity, Hp8902A.WattsToDbm(0));

    [Fact]
    public void ProbeSaysItHasNoIdnBecauseItPredates4882()
    {
        var (rx, _) = New();
        var result = rx.Probe();

        Assert.True(result.Responded);
        Assert.Contains("Pre-488.2", result.Detail);
    }
}

/// <summary>The 11713A, which cannot talk.</summary>
public class Hp11713ADriverTests
{
    [Fact]
    public void ProbeIsHonestThatAListenOnlyDeviceCannotBeConfirmed()
    {
        var link = SimulatedBench.LinkFor("HP 11713A", "SIM::switch-driver::INSTR");
        var result = new Hp11713A(link).Probe();

        // It reports Responded so it does not fail the bench, but the detail says plainly that
        // nothing was actually confirmed.
        Assert.True(result.Responded);
        Assert.Contains("cannot be confirmed over the bus", result.Detail);
        Assert.Contains("front panel", result.Detail);
    }

    [Fact]
    public void RelaysAreSetByChannelString()
    {
        var link = SimulatedBench.LinkFor("HP 11713A", "SIM::switch-driver::INSTR");
        new Hp11713A(link).SetRelays("A1B2");

        Assert.Equal("A1B2", link.History[^1]);
    }
}
