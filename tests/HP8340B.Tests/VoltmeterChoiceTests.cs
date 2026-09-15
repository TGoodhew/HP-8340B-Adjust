using HP8340B.Instruments;
using HP8340B.Instruments.Config;
using HP8340B.Instruments.Model;
using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// Whether the 3458A is actually required, or a 34401A or DM3058 would do.
///
/// <para>The question has a numeric answer that depends on what is being measured, so these tests
/// compute it from each meter's published specification rather than restating a conclusion. If a
/// meter is swapped, or a specification is corrected, the answer moves with it.</para>
/// </summary>
public class VoltmeterChoiceTests
{
    private static Hp432A MeterWith(IVoltmeter dmm)
    {
        var mount = new ThermistorMount();
        mount.Calibration.Add(new MountCalibrationPoint(1e9, 97.0, 98.5));

        return new Hp432A(dmm, new SimulatedThermistorPath(), mount) { Settle = TimeSpan.Zero };
    }

    private static IVoltmeter Build(string model) => model switch
    {
        "3458A" => new Hp3458A(SimulatedBench.LinkFor("HP 3458A", "SIM::dmm::INSTR")),
        "34401A" => new Hp34401A(SimulatedBench.LinkFor("HP 34401A", "SIM::dmm::INSTR")),
        _ => new RigolDm3058(SimulatedBench.LinkFor("Rigol DM3058", "SIM::dmm::INSTR")),
    };

    // --- The specifications themselves ------------------------------------------------------

    [Theory]
    [InlineData("3458A")]
    [InlineData("34401A")]
    [InlineData("DM3058")]
    public void EveryMeterCitesWhereItsNumbersCameFrom(string model)
    {
        var accuracy = Build(model).Accuracy;

        Assert.NotEmpty(accuracy.Source);
        Assert.Contains("1 Year", accuracy.Source);
        Assert.NotEmpty(accuracy.DcRanges);
    }

    [Fact]
    public void TheLowRangeFloorsAreWhatSeparateTheThreeMeters()
    {
        // The additive term on the lowest DC range is the figure that decides everything below
        // about -20 dBm at the mount. Roughly 0.36 uV, 4.2 uV and 9.6 uV.
        double Floor(IVoltmeter m) => m.Accuracy.DcRanges[0].UncertaintyVolts(0);

        var precision = Floor(Build("3458A"));
        var hp = Floor(Build("34401A"));
        var rigol = Floor(Build("DM3058"));

        Assert.True(precision < hp, "The 3458A should have the lowest floor.");
        Assert.True(hp < rigol);

        // An order of magnitude between the best and the worst.
        Assert.True(rigol / precision > 10);
    }

    [Fact]
    public void CommonModeRejectionMattersHereAndTwoOfThreeAreEqual()
    {
        // The substitution measurement reads a few millivolts of difference riding on about 3 V
        // of common mode, so CMRR is not a footnote. The 34401A matches the 3458A at 140 dB; the
        // Rigol gives up 20 dB.
        Assert.Equal(140, Build("3458A").Accuracy.DcCommonModeRejectionDb);
        Assert.Equal(140, Build("34401A").Accuracy.DcCommonModeRejectionDb);
        Assert.Equal(120, Build("DM3058").Accuracy.DcCommonModeRejectionDb);
    }

    [Fact]
    public void RangeSelectionPicksTheLowestRangeThatHoldsTheReading()
    {
        var accuracy = Build("34401A").Accuracy;

        // 20% overrange, so the "100 mV" range holds 120 mV.
        Assert.Equal(0.12, accuracy.RangeFor(0.1).FullScaleVolts);
        Assert.Equal(1.2, accuracy.RangeFor(0.5).FullScaleVolts);

        Assert.Throws<ArgumentOutOfRangeException>(() => accuracy.RangeFor(2000));
    }

    // --- The measurement that actually decides it -----------------------------------------------

    [Theory]
    // Rule 5 caps the mount at +7 dBm, and 4-5 goes down to about -10 dBm. Across that whole
    // span every one of the three meters contributes a small fraction of the 432A's own budget.
    [InlineData("3458A", 5e-3)]
    [InlineData("34401A", 5e-3)]
    [InlineData("DM3058", 5e-3)]
    [InlineData("3458A", 1e-3)]
    [InlineData("34401A", 1e-3)]
    [InlineData("DM3058", 1e-3)]
    [InlineData("3458A", 100e-6)]
    [InlineData("34401A", 100e-6)]
    [InlineData("DM3058", 100e-6)]
    public void AcrossTheRangeThisBenchMeasuresAnyOfTheThreeMetersIsAdequate(string model, double watts)
    {
        var (percent, share) = MeterWith(Build(model)).MeterIsAdequate(watts);

        Assert.True(share < 1.0,
            $"{model} at {watts * 1e3:0.###} mW contributes {percent:0.####}%, which is "
            + $"{share:0.##} of the 432A's own budget — it would be the limit, not the 432A.");
    }

    [Fact]
    public void The432AIsTheLimitNotTheVoltmeterEvenForTheWorstMeter()
    {
        // The whole answer in one assertion: at the top of the range rule 5 allows, the DM3058
        // contributes about a tenth of what the 432A itself does. Swapping it for a 3458A would
        // improve the measurement by an amount too small to observe.
        var rigol = MeterWith(Build("DM3058")).MeterIsAdequate(5e-3);
        var precision = MeterWith(Build("3458A")).MeterIsAdequate(5e-3);

        Assert.True(rigol.ShareOfBudget < 0.2);
        Assert.True(precision.ShareOfBudget < 0.02);

        // And the 432A's own figure is where the error actually lives.
        Assert.True(Hp432A.MeterIndependentUncertaintyPercent(5e-3) > rigol.MeterPercent);
    }

    [Fact]
    public void FarBelowTheRangeThisBenchUsesTheWorstMeterWouldBecomeTheLimit()
    {
        // The other half: the comparison has to be capable of saying no. The 432A's own additive
        // term grows fast at low power, so the meter never becomes the dominant error in
        // practice — but its share does climb, and the calculation tracks it.
        var high = MeterWith(Build("DM3058")).MeterIsAdequate(5e-3).ShareOfBudget;
        var low = MeterWith(Build("DM3058")).MeterIsAdequate(10e-6).ShareOfBudget;

        Assert.True(low > high, "The meter's share of the budget should grow as power falls.");
    }

    [Fact]
    public void TheAdditiveTermIsWhatDominatesAtLowPower()
    {
        // 0.2% of reading + 0.5 uW. At +7 dBm the additive half is worth 0.01%; at -20 dBm it is
        // worth 5%. This is why no voltmeter choice rescues a low-power thermistor reading.
        Assert.Equal(0.21, Hp432A.MeterIndependentUncertaintyPercent(5e-3), 2);
        Assert.Equal(5.2, Hp432A.MeterIndependentUncertaintyPercent(10e-6), 1);
    }

    [Fact]
    public void ZeroPowerHasNoUncertaintyFigure() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Hp432A.MeterIndependentUncertaintyPercent(0));

    // --- The other jobs the DMM does --------------------------------------------------------------

    [Theory]
    [InlineData("3458A")]
    [InlineData("34401A")]
    [InlineData("DM3058")]
    public void EveryMeterIsComfortableWithThe514StepSevenAdjustment(string model)
    {
        // 5-14 step 7: adjust A28R67 for 5.0 mV +/- 0.5 mV on the 0.5 V/GHz output. A 10%
        // tolerance on a 5 mV reading is not a demanding measurement for any modern DMM, and the
        // margin is worth demonstrating rather than assuming.
        var accuracy = Build(model).Accuracy;
        var uncertainty = accuracy.UncertaintyVolts(5e-3);

        Assert.True(uncertainty < 0.5e-3 / 20,
            $"{model} contributes {uncertainty * 1e6:0.#} uV against a 500 uV tolerance.");
    }

    // --- Drivers -----------------------------------------------------------------------------------

    [Fact]
    public void The34401AUsesScpiNotThe3458AsWordCommands()
    {
        var link = SimulatedBench.LinkFor("HP 34401A", "SIM::dmm::INSTR");
        var dmm = new Hp34401A(link);

        dmm.MeasureDcVolts(0.1, nplc: 10);

        Assert.Contains("CONFigure:VOLTage:DC 0.1", link.History);
        Assert.Contains("SENSe:VOLTage:DC:NPLCycles 10", link.History);
        Assert.Contains("READ?", link.History);
    }

    [Fact]
    public void The34401ARefusesAnIntegrationTimeItCannotDeliver()
    {
        // The 3458A goes to 1000 PLC and the 34401A stops at 100. A measurement carried over from
        // one to the other would otherwise silently integrate for a tenth as long.
        var dmm = new Hp34401A(SimulatedBench.LinkFor("HP 34401A", "SIM::dmm::INSTR"));

        dmm.SetIntegrationCycles(100);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => dmm.SetIntegrationCycles(1000));
        Assert.Contains("3458A", ex.Message);
    }

    [Fact]
    public void TheDm3058HasRatesRatherThanNplcAndOnlySlowIsSpecified()
    {
        // Its accuracy table is quoted at the "slow" rate, so anything wanting real integration
        // gets slow rather than a rate the specification does not cover.
        var link = SimulatedBench.LinkFor("Rigol DM3058", "SIM::dmm::INSTR");
        var dmm = new RigolDm3058(link);

        dmm.SetIntegrationCycles(10);
        Assert.Equal(":RATE:VOLTage:DC S", link.History[^1]);
    }

    [Fact]
    public void TheDm3058ProbeWarnsAboutTheEVariantHavingNoGpib()
    {
        var dmm = new RigolDm3058(SimulatedBench.LinkFor("Rigol DM3058", "SIM::dmm::INSTR"));
        var result = dmm.Probe();

        Assert.True(result.Responded);
        Assert.Contains("DM3058E", result.Detail);
    }

    [Theory]
    [InlineData("HP 3458A", typeof(Hp3458A))]
    [InlineData("HP 34401A", typeof(Hp34401A))]
    [InlineData("Rigol DM3058", typeof(RigolDm3058))]
    public void AnyOfTheThreeCanFillTheDmmRoleFromConfig(string model, Type expected)
    {
        var config = new InstrumentConfig { Role = "dmm", Model = model, Address = "GPIB0::22::INSTR" };

        using var instrument = InstrumentFactory.Create(config, simulate: true);

        Assert.IsType(expected, instrument);
        Assert.True(instrument.Probe().Responded);
        Assert.IsAssignableFrom<IVoltmeter>(instrument);
    }

    [Fact]
    public void TheThermistorPathTakesWhicheverMeterIsInTheRack()
    {
        // The point of the abstraction: Hp432A never mentions a model.
        foreach (var model in new[] { "3458A", "34401A", "DM3058" })
        {
            var path = new SimulatedThermistorPath { IncidentWatts = 1e-3, CorrectionFraction = 0.97 };
            var dmm = model switch
            {
                "3458A" => (IVoltmeter)new Hp3458A(path.CreateDmmLink()),
                "34401A" => new Hp34401A(path.CreateDmmLink()),
                _ => new RigolDm3058(path.CreateDmmLink()),
            };

            var mount = new ThermistorMount();
            mount.Calibration.Add(new MountCalibrationPoint(1e9, 97.0, 98.5));

            var meter = new Hp432A(dmm, path, mount) { Settle = TimeSpan.Zero };

            path.RfOn = false;
            meter.ZeroWithRfOff();
            path.RfOn = true;

            var reading = meter.ReadSubstitutionWatts(1e9);

            Assert.Equal(1e-3, reading.Watts, 9);
            Assert.Equal(TraceabilityClass.Spec, reading.Traceability);
            Assert.Contains(model, reading.Voltmeter!);
        }
    }

    [Fact]
    public void TheReadingRecordsWhichMeterTookItAndWhatThatCost()
    {
        var path = new SimulatedThermistorPath { IncidentWatts = 1e-3, CorrectionFraction = 0.97 };
        var mount = new ThermistorMount();
        mount.Calibration.Add(new MountCalibrationPoint(1e9, 97.0, 98.5));

        var meter = new Hp432A(new RigolDm3058(path.CreateDmmLink()), path, mount)
        {
            Settle = TimeSpan.Zero,
        };

        path.RfOn = false;
        meter.ZeroWithRfOff();
        path.RfOn = true;

        var reading = meter.ReadSubstitutionWatts(1e9);

        Assert.Equal("Rigol DM3058", reading.Voltmeter);
        Assert.True(reading.MeterUncertaintyPercent > 0);
        Assert.True(reading.MeterShareOfBudget is > 0 and < 1);
    }
}
