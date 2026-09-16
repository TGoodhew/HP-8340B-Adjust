using HP8340B.Instruments;
using HP8340B.Instruments.Config;
using Xunit;

namespace HP8340B.Tests;

/// <summary>M0-06, the HP 5351A microwave counter.</summary>
public class Hp5351ADriverTests
{
    private static (Hp5351A Counter, HP8340B.Instruments.Visa.SimulatedInstrumentLink Link) New()
    {
        var link = SimulatedBench.LinkFor("HP 5351A", "SIM::counter::INSTR");
        return (new Hp5351A(link), link);
    }

    [Theory]
    // 5350A/5351A/5352A manual, paragraphs 3-308 to 3-311.
    [InlineData(CounterMode.Automatic, "AUTO")]
    [InlineData(CounterMode.LowZ, "LOWZ")]
    [InlineData(CounterMode.HighZ, "HIGHZ")]
    public void ModeUsesTheManualsWords(CounterMode mode, string expected)
    {
        var (counter, link) = New();
        counter.SetMode(mode);
        Assert.Equal(expected, link.History[^1]);
    }

    [Fact]
    public void ManualModeCarriesItsCentreFrequency()
    {
        // Paragraph 3-309: a Manual Center Frequency may be supplied in Hertz; without one the
        // counter reuses the last measurement.
        var (counter, link) = New();

        counter.SetMode(CounterMode.Manual, 10e9);
        Assert.Equal("MANUAL,10000000000", link.History[^1]);

        counter.SetMode(CounterMode.Manual);
        Assert.Equal("MANUAL", link.History[^1]);
    }

    [Fact]
    public void ResetAndInitAreSentAlone()
    {
        // The manual is explicit: RESET, CLR and INIT clear the input buffers, so they must not
        // be appended to other commands or the rest of the string is discarded.
        var (counter, link) = New();

        counter.Reset();
        Assert.Equal("RESET", link.History[^1]);

        counter.Initialize();
        Assert.Equal("INIT", link.History[^1]);
    }

    [Fact]
    public void FrequencyIsReadAsATalkerNotAQuery()
    {
        var (counter, link) = New();
        var hz = counter.ReadFrequencyHz();

        // The simulated counter reports whatever the shared bench state says the DUT is set to,
        // which defaults to 1 GHz. The value is incidental here — what this test is about is that
        // nothing was written in order to read it.
        Assert.Equal(1e9, hz);

        // Classic HP counters have no query mnemonic — nothing should have been written to read.
        Assert.Empty(link.History);
    }

    [Fact]
    public void ProbeIsHonestThatExternalReferenceCannotBeConfirmed()
    {
        // Paragraph 3-127: the counter switches to an external reference AUTOMATICALLY and lights
        // the EXT REF annunciator, but no command in the 3-305..3-330 list reads that state back.
        // 4-2 is meaningless if the counter is not on the Z3805A, so probe must say it cannot
        // check rather than implying it did.
        var (counter, _) = New();
        var result = counter.Probe();

        Assert.True(result.Responded);
        Assert.Null(result.ExternalReference);
        Assert.Contains("front panel", result.Detail);
    }

    [Fact]
    public void InputLevelWarningAccountsForThePad()
    {
        var (counter, _) = New();

        Assert.NotNull(counter.CheckInputLevel(20.0));
        counter.PadDb = 20.0;
        Assert.Null(counter.CheckInputLevel(20.0));
    }
}

/// <summary>M0-07, the HP 3458A multimeter.</summary>
public class Hp3458ADriverTests
{
    private static (Hp3458A Dmm, HP8340B.Instruments.Visa.SimulatedInstrumentLink Link) New()
    {
        var link = SimulatedBench.LinkFor("HP 3458A", "SIM::dmm::INSTR");
        return (new Hp3458A(link), link);
    }

    [Fact]
    public void PresetSendsDeviceClearFirstAsTheGuideAdvises()
    {
        // The guide: the meter may be busy or the interface held, in which case it will not
        // respond to a remote command; Device Clear it responds to immediately.
        var (dmm, link) = New();
        dmm.Preset();

        var clears = link.Transactions
            .Where(t => t.Operation == HP8340B.Instruments.Visa.BusOperation.Clear)
            .ToList();

        Assert.Single(clears);
        Assert.Contains("PRESET NORM", link.History);
        Assert.Contains("OFORMAT ASCII", link.History);
    }

    [Fact]
    public void RangeIsExplicitWhenGivenAndAutoOnlyWhenAsked()
    {
        // Autorange makes settle time unpredictable, and 5-14 step 7 wants 5 mV to +/-0.5 mV.
        var (dmm, link) = New();

        dmm.SetDcVolts(0.1);
        Assert.Equal("DCV 0.1", link.History[^1]);

        dmm.SetDcVolts(null);
        Assert.Equal("DCV AUTO", link.History[^1]);
    }

    [Fact]
    public void IntegrationTimeAndDigitsUseNplcAndNdig()
    {
        var (dmm, link) = New();

        dmm.SetIntegrationCycles(10);
        Assert.Equal("NPLC 10", link.History[^1]);

        dmm.SetDigits(8);
        Assert.Equal("NDIG 8", link.History[^1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void IntegrationTimeMustBePositive(double nplc)
    {
        var (dmm, _) = New();
        Assert.Throws<ArgumentOutOfRangeException>(() => dmm.SetIntegrationCycles(nplc));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(9)]
    public void DigitsAreRangeChecked(int digits)
    {
        var (dmm, _) = New();
        Assert.Throws<ArgumentOutOfRangeException>(() => dmm.SetDigits(digits));
    }

    [Fact]
    public void MeasureConfiguresThenReads()
    {
        // The 0.5 V/GHz check of 5-14 step 7: 5 mV on a 0.1 V range with a long integration.
        var (dmm, link) = New();
        var volts = dmm.MeasureDcVolts(0.1, nplc: 10);

        Assert.Equal(5.0e-3, volts, 6);
        Assert.Contains("DCV 0.1", link.History);
        Assert.Contains("NPLC 10", link.History);
    }

    [Fact]
    public void ProbeIdentifiesTheMeter()
    {
        var (dmm, _) = New();
        var result = dmm.Probe();

        Assert.True(result.Responded);
        Assert.Contains("3458A", result.Identity);
    }
}

/// <summary>Both drivers reach the bench through the factory.</summary>
public class CounterAndDmmFactoryTests
{
    [Theory]
    [InlineData("counter", "HP 5351A", typeof(Hp5351A))]
    [InlineData("dmm", "HP 3458A", typeof(Hp3458A))]
    [InlineData("spectrum-analyzer", "HP 8563E", typeof(Hp8563E))]
    public void FactoryBuildsTheRealDriverInSimulation(string role, string model, Type expected)
    {
        // Simulated links, real drivers: the command builders are exercised exactly as they would
        // be on hardware (repo rule 4).
        var config = new InstrumentConfig { Role = role, Model = model, Address = "GPIB0::1::INSTR" };

        using var instrument = InstrumentFactory.Create(config, simulate: true);
        Assert.IsType(expected, instrument);
        Assert.True(instrument.Probe().Responded);
    }
}
