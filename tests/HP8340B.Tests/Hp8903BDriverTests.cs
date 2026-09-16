using HP8340B.Instruments;
using HP8340B.Instruments.Config;
using HP8340B.Instruments.Visa;
using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// M0-18: the HP 8903B, which closes the 5-5 step 48 active-probe gap.
/// </summary>
public class Hp8903BDriverTests
{
    private static (Hp8903B Analyzer, SimulatedInstrumentLink Link) New()
    {
        var link = SimulatedBench.LinkFor("HP 8903B", "SIM::audio-analyzer::INSTR");
        return (new Hp8903B(link), link);
    }

    [Fact]
    public void AcLevelAndDetectorUseTheCodesFromTableThreeSix()
    {
        var (analyzer, link) = New();

        analyzer.SelectAcLevel();
        Assert.Equal("M1", link.History[^1]);

        analyzer.SelectDcLevel();
        Assert.Equal("S1", link.History[^1]);

        analyzer.SelectRmsDetector();
        Assert.Equal("AO", link.History[^1]);

        analyzer.SelectAverageDetector();
        Assert.Equal("A1", link.History[^1]);
    }

    [Fact]
    public void LogAndLinearAreTheManualsCodes()
    {
        var (analyzer, link) = New();

        analyzer.SelectLog();
        Assert.Equal("LG", link.History[^1]);

        analyzer.SelectLinear();
        Assert.Equal("LN", link.History[^1]);
    }

    [Fact]
    public void StoringAReferenceSwitchesToRatioInDb()
    {
        // The reason this instrument suits step 48: the step wants a level 65 dB below a
        // reference, and the analyzer does the subtraction rather than leaving two absolute
        // readings to be differenced by hand.
        var (analyzer, link) = New();
        analyzer.StoreReferenceAndRatio();

        Assert.Contains("R1", link.History);
        Assert.Equal("LG", link.History[^1]);
    }

    [Fact]
    public void RatioCanBeTurnedOffAgain()
    {
        var (analyzer, link) = New();
        analyzer.RatioOff();
        Assert.Equal("R0", link.History[^1]);
    }

    [Fact]
    public void AnExplicitReferenceCarriesItsUnits()
    {
        var (analyzer, link) = New();
        analyzer.SetReferenceVolts(1.0);

        Assert.Contains("R11VL", link.History);
    }

    [Fact]
    public void AZeroReferenceIsRefused()
    {
        var (analyzer, _) = New();
        Assert.Throws<ArgumentOutOfRangeException>(() => analyzer.SetReferenceVolts(0));
    }

    // --- Filters ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(AudioLowPass.Off, "L0")]
    [InlineData(AudioLowPass.Khz30, "L1")]
    [InlineData(AudioLowPass.Khz80, "L2")]
    public void LowPassFiltersMatchTheManual(AudioLowPass filter, string expected)
    {
        var (analyzer, link) = New();
        analyzer.SelectLowPass(filter);
        Assert.Equal(expected, link.History[^1]);
    }

    [Theory]
    [InlineData(AudioHighPass.Off, "H0")]
    [InlineData(AudioHighPass.LeftPlugIn, "H1")]
    [InlineData(AudioHighPass.RightPlugIn, "H2")]
    public void HighPassCodesAddressThePlugInPosition(AudioHighPass filter, string expected)
    {
        var (analyzer, link) = New();
        analyzer.SelectHighPass(filter);
        Assert.Equal(expected, link.History[^1]);
    }

    [Fact]
    public void StepFortyEightUsesTheEightyKilohertzLowPassAndNotTheThirty()
    {
        // The step reads 20 kHz and 50 kHz tones. The 30 kHz low-pass would reject the 50 kHz one
        // outright and the measurement would read as an excellent null — exactly the wrong way
        // for a mistake to fail.
        var (analyzer, link) = New();
        analyzer.ConfigureForStep48();

        Assert.Contains("L2", link.History);
        Assert.DoesNotContain("L1", link.History);
        Assert.Contains("H0", link.History);
        Assert.Contains("M1", link.History);
        Assert.Contains("AO", link.History);
    }

    [Fact]
    public void The400HzHighPassCannotBeSelectedUntilItsPositionIsDeclared()
    {
        // H1 and H2 address the plug-in POSITION, not the filter. On a differently fitted 8903B
        // the left position holds a CCITT or A-weighting bandpass, and quietly weighting a level
        // measurement changes the answer without changing its appearance.
        var (analyzer, _) = New();

        var ex = Assert.Throws<InvalidOperationException>(() => analyzer.SelectHighPass400());

        Assert.Contains("POSITION", ex.Message);
        Assert.Contains("010", ex.Message);
    }

    [Fact]
    public void OnceDeclaredThe400HzHighPassSelectsThatPosition()
    {
        var (analyzer, link) = New();
        analyzer.HighPass400Position = AudioHighPass.RightPlugIn;

        analyzer.SelectHighPass400();

        Assert.Equal("H2", link.History[^1]);
    }

    // --- Source ------------------------------------------------------------------------------------

    [Fact]
    public void SourceFrequencyAndAmplitudeCarryUnits()
    {
        var (analyzer, link) = New();

        analyzer.SetSourceFrequencyHz(50000);
        Assert.Equal("FA50000HZ", link.History[^1]);

        analyzer.SetSourceAmplitudeVolts(1.0);
        Assert.Equal("AP1VL", link.History[^1]);

        analyzer.SetSourceAmplitudeMillivolts(560);
        Assert.Equal("AP560MV", link.History[^1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveSourceFrequencyIsRefused(double hz)
    {
        var (analyzer, _) = New();
        Assert.Throws<ArgumentOutOfRangeException>(() => analyzer.SetSourceFrequencyHz(hz));
    }

    // --- Reading and probe -------------------------------------------------------------------------

    [Fact]
    public void AMeasurementIsReadAsATalker()
    {
        var (analyzer, _) = New();
        Assert.Equal(5.0e-3, analyzer.ReadMeasurement(), 6);
    }

    [Fact]
    public void ANonNumericReadingSaysWhatItUsuallyMeans()
    {
        var link = new SimulatedInstrumentLink("SIM::aa::INSTR", _ => "----");
        var analyzer = new Hp8903B(link);

        var ex = Assert.Throws<InvalidOperationException>(() => analyzer.ReadMeasurement());
        Assert.Contains("stop band", ex.Message);
    }

    [Fact]
    public void ProbeFlagsTheUndeclaredFilterPosition()
    {
        var (analyzer, _) = New();
        var result = analyzer.Probe();

        Assert.True(result.Responded);
        Assert.Contains("NOT declared", result.Detail);

        analyzer.HighPass400Position = AudioHighPass.LeftPlugIn;
        Assert.Contains("LeftPlugIn", analyzer.Probe().Detail);
    }

    [Fact]
    public void InitializePutsItInAcLevelWithFreeRunTriggering()
    {
        var (analyzer, link) = New();
        analyzer.Initialize();

        Assert.Contains("M1", link.History);
        Assert.Contains("T0", link.History);
        Assert.Contains("AO", link.History);
    }

    [Fact]
    public void FactoryBuildsTheRealDriverInSimulation()
    {
        var config = new InstrumentConfig
        {
            Role = "audio-analyzer", Model = "HP 8903B", Address = "GPIB0::28::INSTR",
        };

        using var instrument = InstrumentFactory.Create(config, simulate: true);

        Assert.IsType<Hp8903B>(instrument);
        Assert.True(instrument.Probe().Responded);
    }
}
