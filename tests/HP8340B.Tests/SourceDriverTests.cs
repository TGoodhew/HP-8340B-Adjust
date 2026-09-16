using HP8340B.Instruments;
using HP8340B.Instruments.Config;
using HP8340B.Instruments.Visa;
using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// M0-15: the 8116A pulse generator, the DG1032Z and the E4438C.
/// </summary>
public class Hp8116ADriverTests
{
    private static (Hp8116A Generator, SimulatedInstrumentLink Link) New()
    {
        var link = SimulatedBench.LinkFor("HP 8116A", "SIM::pulse-generator::INSTR");
        return (new Hp8116A(link), link);
    }

    // --- The specification check the issue asked for ---------------------------------------------

    [Fact]
    public void TheEdgeAndWidthFiguresClearWhatTableFourTwoAsksFor()
    {
        // The issue was explicit: verify these against the 8116A's own manual before 4-12 relies
        // on them, because a pulse-fidelity test is only as good as the source's edges. They are
        // verified and they pass with margin.
        //
        // Manual, section 4-15 Pulse Characteristics: "Transition times (10 % to 90 %): < 6 ns".
        // Manual, Pulse Width (pulse mode): "Range: 10.0 ns to 999 ms".
        // Table 4-2 asks for <= 100 ns width and <= 10 ns rise.
        Assert.True(Hp8116A.MeetsTable42PulseRequirement());

        Assert.Equal(6e-9, Hp8116A.TransitionTimeSeconds);
        Assert.Equal(10e-9, Hp8116A.MinWidthSeconds);

        // Ten times the margin on width, and nearly two on the edge.
        Assert.True(Hp8116A.MinWidthSeconds <= 100e-9 / 10);
        Assert.True(Hp8116A.TransitionTimeSeconds < 10e-9);
    }

    [Fact]
    public void AToughenedRequirementWouldFailTheCheckRatherThanPassingQuietly()
    {
        // The check has to be capable of saying no, or passing it proves nothing. A 2 ns rise
        // requirement is beyond this generator.
        Assert.False(Hp8116A.MeetsTable42PulseRequirement(requiredRiseSeconds: 2e-9));
        Assert.False(Hp8116A.MeetsTable42PulseRequirement(requiredWidthSeconds: 5e-9));
    }

    // --- Commands ---------------------------------------------------------------------------------

    [Fact]
    public void PulseSetupUsesTheManualsMnemonics()
    {
        var (generator, link) = New();
        generator.ConfigurePulse(periodSeconds: 1e-6, widthSeconds: 100e-9);

        Assert.Contains("M1", link.History);                 // pulse mode
        Assert.Contains("FRQ 1000000 HZ", link.History);     // 1 us period
        Assert.Contains("WID 100 NS", link.History);

        // Output enabled last, so nothing downstream sees an unintended pulse first.
        Assert.Equal("D0", link.History[^1]);
        Assert.Equal("D1", link.History[0]);
    }

    [Fact]
    public void WidthIsRefusedAgainstTheManualsPeriodConstraint()
    {
        // "Max. Width: Period - 10 ns". A 1 us period cannot carry a 1 us pulse.
        var (generator, _) = New();

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => generator.SetWidthSeconds(1e-6, periodSeconds: 1e-6));

        Assert.Contains("Period - 10 ns", ex.Message);

        // And 990 ns in a 1 us period is fine.
        generator.SetWidthSeconds(990e-9, periodSeconds: 1e-6);
    }

    [Theory]
    [InlineData(5e-9)]      // below the 10 ns minimum
    [InlineData(2.0)]       // above the 999 ms maximum
    public void WidthsOutsideTheGeneratorsRangeAreRefused(double seconds)
    {
        var (generator, _) = New();
        Assert.Throws<ArgumentOutOfRangeException>(() => generator.SetWidthSeconds(seconds));
    }

    [Theory]
    [InlineData(0.0005)]    // below 1 mHz
    [InlineData(60e6)]      // above 50 MHz
    public void FrequenciesOutsideTheRangeAreRefused(double hz)
    {
        var (generator, _) = New();
        Assert.Throws<ArgumentOutOfRangeException>(() => generator.SetFrequencyHz(hz));
    }

    [Fact]
    public void TtlLevelsAreWhatTheDutsPulseInputWants()
    {
        // The 8340B's pulse input is TTL: low below 0.4 V, high above 2.4 V.
        var (generator, link) = New();
        generator.SetTtlLevels();

        Assert.Contains("HIL 4 V", link.History);
        Assert.Contains("LOL 0 V", link.History);
    }

    [Fact]
    public void LevelsMustBeTheRightWayRound()
    {
        var (generator, _) = New();
        Assert.Throws<ArgumentException>(() => generator.SetLevels(highVolts: 0, lowVolts: 4));
    }

    [Theory]
    [InlineData(5e-3)]
    [InlineData(20.0)]
    public void AmplitudesOutsideTheLevelWindowsAreRefused(double vpp)
    {
        var (generator, _) = New();
        Assert.Throws<ArgumentOutOfRangeException>(() => generator.SetAmplitudeVpp(vpp));
    }

    [Fact]
    public void ProbeReportsTheEdgeSpecificationItWasVerifiedAgainst()
    {
        var (generator, _) = New();
        var result = generator.Probe();

        Assert.True(result.Responded);
        Assert.Contains("transition time", result.Detail);
    }
}

/// <summary>The DG1032Z: second pulse source and DC supply.</summary>
public class RigolDg1032ZDriverTests
{
    private static (RigolDg1032Z Generator, SimulatedInstrumentLink Link) New()
    {
        var link = SimulatedBench.LinkFor("Rigol DG1032Z", "SIM::arb-generator::INSTR");
        return (new RigolDg1032Z(link), link);
    }

    [Fact]
    public void PulseFollowsTheGuidesOwnExampleShape()
    {
        // Guide: ":SOUR1:APPL:PULS 100,3,2,1" is frequency, amplitude, offset, phase.
        var (generator, link) = New();
        generator.ApplyPulse(1, 100, amplitudeVpp: 3, offsetVolts: 2);

        Assert.Equal(":SOURce1:APPLy:PULSe 100,3,2,0", link.History[^1]);
    }

    [Fact]
    public void DcCarriesThePlaceholdersTheGuideInsistsOn()
    {
        // The guide: frequency and amplitude "are not applicable to the DC function but they must
        // be specified as a placeholder". Its example for 2 V DC is ":SOUR1:APPL:DC 1,1,2".
        var (generator, link) = New();
        generator.ApplyDc(1, 5.0);

        Assert.Equal(":SOURce1:APPLy:DC 1,1,5", link.History[^1]);
    }

    [Fact]
    public void HighImpedanceLoadIsSettableBecauseOtherwiseDcReadsDouble()
    {
        // The generator's level arithmetic assumes 50 ohms unless told otherwise, so a DC level
        // feeding a DVM or a test point comes out twice what was asked for.
        var (generator, link) = New();
        generator.SetOutputLoadHighZ(1);

        Assert.Equal(":OUTPut1:LOAD INFinity", link.History[^1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void ChannelNumbersAreRangeChecked(int channel)
    {
        var (generator, _) = New();
        Assert.Throws<ArgumentOutOfRangeException>(() => generator.SetOutputEnabled(channel, true));
    }

    [Fact]
    public void PulseFrequencyIsRangeCheckedAgainstTheGuidesTable()
    {
        var (generator, _) = New();
        Assert.Throws<ArgumentOutOfRangeException>(() => generator.ApplyPulse(1, 30e6, 1));
    }
}

/// <summary>The E4438C: low-band LO, 5-5 injection source, and the 4-9 phase-noise baseline.</summary>
public class AgilentE4438CDriverTests
{
    private static (AgilentE4438C Generator, SimulatedInstrumentLink Link) New()
    {
        var link = SimulatedBench.LinkFor("Agilent E4438C", "SIM::vector-generator::INSTR");
        return (new AgilentE4438C(link), link);
    }

    [Fact]
    public void CwSetupPutsTheOutputOnLast()
    {
        var (generator, link) = New();
        generator.ConfigureCw(350e6, -10);

        Assert.Equal(":OUTPut:STATe OFF", link.History[0]);
        Assert.Contains(":SOURce:FREQuency:FIXed 350000000 HZ", link.History);
        Assert.Contains(":SOURce:POWer:LEVel:IMMediate:AMPLitude -10 DBM", link.History);
        Assert.Equal(":OUTPut:STATe ON", link.History[^1]);
    }

    [Fact]
    public void TheFrequencyOptionIsRespectedRatherThanAssumed()
    {
        // The upper limit depends on which frequency option is fitted (1, 2, 3, 4 or 6 GHz) and
        // that is not recorded for this unit. The default is the most conservative, so a
        // frequency the unit may not reach is refused rather than silently clipped.
        var (generator, _) = New();

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => generator.SetCwFrequencyHz(3e9));
        Assert.Contains("frequency option", ex.Message);

        generator.MaxHz = 6e9;
        generator.SetCwFrequencyHz(3e9);   // now fine
    }

    [Fact]
    public void ItCanProveItIsOnTheHouseReference()
    {
        // ":ROSC:SOUR? ... returns either INT (internal) or EXT (external)". Unlike the 8673B's
        // two status bits this is a single query, and unlike the 5351A it exists at all.
        var (generator, _) = New();

        Assert.True(generator.IsExternalReferenceSelected());

        var result = generator.Probe();
        Assert.True(result.ExternalReference);
        Assert.True(result.ReferenceApplies);
    }

    [Fact]
    public void AnInternalReferenceSaysWhyItMattersForFourNine()
    {
        var link = new SimulatedInstrumentLink("SIM::e4438c::INSTR", c =>
            c.StartsWith(":SOURce:ROSCillator", StringComparison.OrdinalIgnoreCase)
                ? "INT"
                : "Agilent Technologies,E4438C,SIM,1.0");

        var result = new AgilentE4438C(link).Probe();

        Assert.False(result.ExternalReference);
        Assert.Contains("phase-noise baseline", result.Detail);
    }

    [Fact]
    public void AnUnrecognisedReferenceAnswerIsUnknownNotGuessed()
    {
        var generator = new AgilentE4438C(new SimulatedInstrumentLink("SIM::x::INSTR", _ => "?"));
        Assert.Null(generator.IsExternalReferenceSelected());
    }
}

/// <summary>All three reach the bench through the factory.</summary>
public class SourceFactoryTests
{
    [Theory]
    [InlineData("pulse-generator", "HP 8116A", typeof(Hp8116A))]
    [InlineData("arb-generator", "Rigol DG1032Z", typeof(RigolDg1032Z))]
    [InlineData("vector-generator", "Agilent E4438C", typeof(AgilentE4438C))]
    public void FactoryBuildsTheRealDriverInSimulation(string role, string model, Type expected)
    {
        var config = new InstrumentConfig { Role = role, Model = model, Address = "GPIB0::5::INSTR" };

        using var instrument = InstrumentFactory.Create(config, simulate: true);

        Assert.IsType(expected, instrument);
        Assert.True(instrument.Probe().Responded);
    }
}
