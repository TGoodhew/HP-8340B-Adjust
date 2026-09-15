using HP8340B.Instruments;
using HP8340B.Instruments.Config;
using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// M0-10: the HP 8673B, the LO for the 11793A in setup S6 and the optional phase-noise
/// down-conversion LO. CW frequency and level only.
/// </summary>
public class Hp8673BDriverTests
{
    private static (Hp8673B Source, SimulatedSource Model) New()
    {
        var model = new SimulatedSource();
        return (new Hp8673B(model.CreateLink()), model);
    }

    // --- Command table --------------------------------------------------------------------------

    [Fact]
    public void EveryCodeTheProjectSendsIsVerified()
    {
        // Repo rule 2. The table is small and entirely cited, so an Unverified entry appearing
        // later is a deliberate act that should fail here until it is checked.
        Assert.Empty(Hp8673BCommands.Unverified);
    }

    [Fact]
    public void AnUncitedCodeCannotBeSent()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Hp8673BCommands.Require("ZZ"));
        Assert.Contains("never fabricate", ex.Message);
    }

    [Fact]
    public void EveryCodeCitesTheManual()
    {
        Assert.All(Hp8673BCommands.All, code =>
            Assert.Contains("8673B", code.Source));
    }

    // --- Frequency ------------------------------------------------------------------------------

    [Fact]
    public void CwFrequencyIsSentInHertzSoThereIsNoDecimalPointAmbiguity()
    {
        // The manual accepts GZ/MZ/KZ/HZ; sending fundamental units means the same string shape
        // for 2 GHz and 26 GHz, and no rounding introduced on our side of the bus.
        var (source, _) = New();
        source.SetCwFrequencyGHz(16.232334);

        Assert.Equal("FR16232334000HZ", source.Link.History[^1]);
    }

    [Theory]
    [InlineData(1.0e9)]      // below the 8340B's low band — the 8673B cannot reach it at all
    [InlineData(1.9e9)]      // below even the overrange
    [InlineData(26.6e9)]
    public void FrequenciesOutsideTheGeneratorsRangeAreRefused(double hz)
    {
        var (source, _) = New();
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => source.SetCwFrequencyHz(hz));

        // The message has to say what to use instead: the 8673B starts at 2 GHz, so anything
        // below that is the E4438C's job, not a broken driver.
        Assert.Contains("E4438C", ex.Message);
    }

    [Theory]
    [InlineData(1.95e9)]     // the overrange limits are settable, just not warranted
    [InlineData(2.0e9)]
    [InlineData(26.5e9)]
    public void OverrangeFrequenciesAreAllowed(double hz)
    {
        var (source, _) = New();
        source.SetCwFrequencyHz(hz);   // must not throw
    }

    [Fact]
    public void FrequencyReadBackRoundTripsThroughTheSimulator()
    {
        // Proves the command builder and the response parser agree, rather than comparing the
        // driver against a constant it could never disagree with.
        var (source, _) = New();
        source.SetCwFrequencyGHz(12.4);

        Assert.Equal(12.4e9, source.ReadCwFrequencyHz());
        Assert.Equal(12.4e9, source.ReadOutputFrequencyHz());
    }

    [Fact]
    public void ReadBackUsesTheOaSuffixNotAQuery()
    {
        var (source, _) = New();
        source.ReadCwFrequencyHz();
        Assert.Contains("FROA", source.Link.History);

        source.ReadOutputLevelDbm();
        Assert.Contains("LEOA", source.Link.History);
    }

    // --- Level ----------------------------------------------------------------------------------

    [Fact]
    public void LevelUsesThePreferredCodeAndDbmTerminator()
    {
        // p. 3-117 marks LE preferred over AP and PL, and DM the units terminator for dBm.
        var (source, _) = New();
        source.SetOutputLevelDbm(-56);

        Assert.Equal("LE-56.0DM", source.Link.History[^1]);
    }

    [Fact]
    public void LevelReadBackRoundTrips()
    {
        var (source, _) = New();
        source.SetOutputLevelDbm(-12.5);

        Assert.Equal(-12.5, source.ReadOutputLevelDbm(), 1);
    }

    [Theory]
    [InlineData(-102.0)]
    [InlineData(14.0)]
    public void LevelsTheGeneratorCannotAcceptAreRefused(double dbm)
    {
        var (source, _) = New();
        Assert.Throws<ArgumentOutOfRangeException>(() => source.SetOutputLevelDbm(dbm));
    }

    [Theory]
    // Table 1-1 p. 1-7, standard option: +8 dBm to 18 GHz, +4 to 22 GHz, 0 dBm to 26 GHz.
    [InlineData(2.0e9, 8.0)]
    [InlineData(18.0e9, 8.0)]
    [InlineData(20.0e9, 4.0)]
    [InlineData(22.0e9, 4.0)]
    [InlineData(26.0e9, 0.0)]
    public void MaxLeveledPowerFollowsTheSpecificationTable(double hz, double expected) =>
        Assert.Equal(expected, Hp8673B.MaxLeveledDbm(hz));

    [Fact]
    public void ALevelAboveTheLeveledLimitIsStillAcceptedBecauseTheGeneratorAcceptsIt()
    {
        // +13 dBm at 24 GHz is well above the 0 dBm leveled limit. The generator takes it and
        // lights UNLEVELED, so the driver must not pretend otherwise — the caller checks
        // IsUnleveled. Refusing here would make it impossible to find the leveled limit.
        var (source, _) = New();
        source.SetCwFrequencyGHz(24);
        source.SetOutputLevelDbm(13);

        Assert.Equal("LE13.0DM", source.Link.History[^1]);
    }

    // --- Output and ALC --------------------------------------------------------------------------

    [Fact]
    public void RfOnAndOffUseTheRfCodes()
    {
        var (source, model) = New();

        source.SetRfOutput(true);
        Assert.Equal("RF1", source.Link.History[^1]);
        Assert.True(model.RfOn);

        source.SetRfOutput(false);
        Assert.Equal("RF0", source.Link.History[^1]);
        Assert.False(model.RfOn);
    }

    [Theory]
    [InlineData(SourceAlcMode.Internal, "C1")]
    [InlineData(SourceAlcMode.Diode, "C2")]
    [InlineData(SourceAlcMode.PowerMeter, "C3")]
    public void AlcModeMatchesTableThreeSix(SourceAlcMode mode, string expected)
    {
        var (source, _) = New();
        source.SetAlcMode(mode);
        Assert.Equal(expected, source.Link.History[^1]);
    }

    [Fact]
    public void ConfigureCwSetsTheLevelBeforeEnablingTheOutput()
    {
        // Otherwise whatever is connected sees the previous level first — which for an LO
        // feeding a mixer or the 11793A is the difference between a measurement and a repair.
        var (source, _) = New();
        Assert.True(source.ConfigureCw(12e9, -5, TimeSpan.Zero));

        var history = source.Link.History.ToList();
        var level = history.FindIndex(h => h.StartsWith("LE") && h.EndsWith("DM"));
        var rfOn = history.IndexOf("RF1");

        Assert.True(level >= 0 && rfOn >= 0);
        Assert.True(level < rfOn, "Level must be set before the RF output is enabled.");

        // Auto peak matters: section 3-13 says power specs are not warranted without it.
        Assert.Contains("K1", history);
    }

    [Fact]
    public void ConfigureCwTurnsTheOutputOffFirst()
    {
        var (source, _) = New();
        source.ConfigureCw(12e9, -5, TimeSpan.Zero);

        Assert.Equal("RF0", source.Link.History[0]);
    }

    // --- Status ------------------------------------------------------------------------------------

    [Fact]
    public void ExtendedStatusIsReadWithClearStatusFirst()
    {
        // p. 3-135: extended bits stay set until read, so without CS first a condition that has
        // since cleared still reads as present.
        var (source, _) = New();
        source.ReadExtendedStatus();

        Assert.Equal("CSOS", source.Link.History[^1]);
    }

    [Fact]
    public void ReferenceStateNeedsBothBitsBeforeItClaimsTheHouseReference()
    {
        var (source, model) = New();

        // Switch in EXT and locked: genuinely on the Z3805A.
        var good = source.ReadReferenceState();
        Assert.True(good.LockedToExternalReference);

        // Switch in EXT but not locked — the reference is missing or out of tolerance. Bit 3
        // alone would have reported this as "external reference selected" and been wrong.
        model.PhaseLocked = false;
        model.RefreshStatusBytes();

        var broken = source.ReadReferenceState();
        Assert.True(broken.SwitchInExternal);
        Assert.False(broken.LockedToExternalReference);
        Assert.Contains("NOT PHASE LOCKED", broken.Describe());
    }

    [Fact]
    public void SwitchInInternalIsReportedPlainly()
    {
        var (source, model) = New();
        model.ExternalReferenceSelected = false;
        model.RefreshStatusBytes();

        var state = source.ReadReferenceState();

        Assert.False(state.LockedToExternalReference);
        Assert.Contains("own crystal", state.Describe());
    }

    [Fact]
    public void UnleveledIsReadableFromTheExtendedStatusByte()
    {
        var (source, model) = New();
        Assert.False(source.IsUnleveled());

        model.Unleveled = true;
        model.RefreshStatusBytes();
        Assert.True(source.IsUnleveled());
    }

    [Fact]
    public void SettleWaitPollsRatherThanQueryingBecauseThereIsNoOpc()
    {
        var (source, _) = New();
        var link = (HP8340B.Instruments.Visa.SimulatedInstrumentLink)source.Link;

        Assert.True(source.WaitSettled(TimeSpan.Zero));
        Assert.True(link.PollCount > 0);
    }

    [Fact]
    public void SettleWaitAbortsOnAnEntryErrorRatherThanTimingOut()
    {
        // A rejected command never settles. Waiting out the timeout would report a slow
        // instrument instead of the bad command that is actually the fault.
        var model = new SimulatedSource();
        var link = model.CreateLink();
        link.StatusByte = (byte)SourceStatusByte1.EntryError;

        var source = new Hp8673B(link);
        source.SetCwFrequencyGHz(12);

        var ex = Assert.Throws<InvalidOperationException>(
            () => source.WaitSettled(TimeSpan.FromMilliseconds(50)));

        Assert.Contains("entry error", ex.Message);
        Assert.Contains("FR12000000000HZ", ex.Message);   // names the command that was rejected
    }

    // --- Parsing ------------------------------------------------------------------------------------

    [Theory]
    // The generator wraps its numbers in a program code and a units terminator, and for FROA it
    // answers with CF rather than FR — hence a parser that strips letters rather than a prefix.
    [InlineData("CF16232334000HZ", 16232334000.0)]
    [InlineData("FR2000000000HZ", 2000000000.0)]
    [InlineData("LE-56.0DM", -56.0)]
    [InlineData("LE13.0DM", 13.0)]
    [InlineData("  RA-50DB  ", -50.0)]
    public void ResponsesAreParsedWithTheirPrefixAndUnitsStripped(string response, double expected) =>
        Assert.Equal(expected, Hp8673B.ParseNumeric(response, "test"));

    [Theory]
    [InlineData("")]
    [InlineData("HZ")]
    public void AResponseWithNoNumberFailsLoudly(string response)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Hp8673B.ParseNumeric(response, "output level"));

        Assert.Contains("output level", ex.Message);
    }

    // --- Probe ---------------------------------------------------------------------------------------

    [Fact]
    public void ProbeConfirmsTheExternalReferenceBecauseThisGeneratorActuallyCan()
    {
        // Unlike the 5351A, the 8673B reports its reference state over the bus, so probe reports
        // a real answer rather than null with an instruction to look at the front panel.
        var (source, _) = New();
        var result = source.Probe();

        Assert.True(result.Responded);
        Assert.True(result.ExternalReference);
        Assert.Contains("no *IDN?", result.Identity);
    }

    [Fact]
    public void ProbeSaysWhyItMattersWhenTheGeneratorIsOnItsOwnCrystal()
    {
        var (source, model) = New();
        model.ExternalReferenceSelected = false;
        model.RefreshStatusBytes();

        var result = source.Probe();

        Assert.False(result.ExternalReference);
        Assert.Contains("phase-noise", result.Detail);
    }

    [Fact]
    public void FactoryBuildsTheRealDriverInSimulation()
    {
        var config = new InstrumentConfig
        {
            Role = "lo-source", Model = "HP 8673B", Address = "GPIB0::19::INSTR",
        };

        using var instrument = InstrumentFactory.Create(config, simulate: true);

        Assert.IsType<Hp8673B>(instrument);
        Assert.True(instrument.Probe().Responded);
    }
}
