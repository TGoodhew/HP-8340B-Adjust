using HP8340B.Instruments;
using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// M0-25: the scope role model. The point of it is that an unsuitable substitute is refused at
/// config load, in front of somebody at a keyboard, rather than discovered at the bench with the
/// covers off and the DUT live.
/// </summary>
public class ScopeRoleTests
{
    /// <summary>A 1 MΩ-only, four-channel, 100 MHz scope with a real external trigger.</summary>
    private static ScopeCapability Rigol() => new RigolDs1104Z(
        new HP8340B.Instruments.Visa.SimulatedInstrumentLink("SIM::s::INSTR")).Capability;

    private static ScopeCapability Fake(
        int channels = 4,
        double bandwidthHz = 100e6,
        double minVoltsPerDiv = 1e-3,
        bool fiftyOhm = false,
        bool externalTrigger = true,
        bool xy = true,
        IReadOnlyList<BandwidthDerate>? derates = null) =>
        new(
            "Fake scope",
            channels,
            bandwidthHz,
            minVoltsPerDiv,
            10.0,
            fiftyOhm
                ? [ScopeInputImpedance.OneMegaohm, ScopeInputImpedance.FiftyOhm]
                : [ScopeInputImpedance.OneMegaohm],
            externalTrigger,
            xy,
            derates ?? [],
            "test");

    // --- The trigger arithmetic ------------------------------------------------------------------

    [Fact]
    public void AScopeWithNoExternalTriggerSpendsAChannelOnIt()
    {
        // The TDS3014B case. Its programming manual says neither EXT nor EXT10 is available on
        // 4-channel TDS3000 Series instruments, and the 3014B is four-channel — so the DUT sweep
        // trigger has to occupy one of the four.
        var withExt = Fake(channels: 4, externalTrigger: true);
        var withoutExt = Fake(channels: 4, externalTrigger: false);

        Assert.Equal(3, ScopeRole.TestPoint.ChannelsNeededOn(withExt));
        Assert.Equal(4, ScopeRole.TestPoint.ChannelsNeededOn(withoutExt));
    }

    [Fact]
    public void AFourChannelScopeWithNoExternalTriggerStillFitsTheTestPointRoleExactly()
    {
        // Three test points plus the trigger is exactly four. Worth pinning: it is the difference
        // between "fits" and "cannot run the procedure", and it turns on a footnote in a manual.
        Assert.True(ScopeRole.TestPoint.CanBeFilledBy(Fake(channels: 4, externalTrigger: false)));
        Assert.False(ScopeRole.TestPoint.CanBeFilledBy(Fake(channels: 3, externalTrigger: false)));
    }

    [Fact]
    public void TheTuningRoleDoesNotSpendAChannelOnTheTrigger()
    {
        // The DUT's sweep ramp is already on a channel in S4, so a scope with no external trigger
        // input can trigger on that instead of giving up a third channel.
        Assert.Equal(2, ScopeRole.Tuning.ChannelsNeededOn(Fake(externalTrigger: false)));
        Assert.True(ScopeRole.Tuning.CanBeFilledBy(Fake(channels: 2, externalTrigger: false)));
    }

    [Fact]
    public void TheRefusalSaysWhyTheExtraChannelIsNeeded()
    {
        var unmet = ScopeRole.TestPoint.UnmetBy(Fake(channels: 3, externalTrigger: false));

        Assert.Contains(unmet, u => u.Contains("external trigger"));
    }

    // --- Capability gates -------------------------------------------------------------------------

    [Fact]
    public void ATwoChannelScopeCannotWatchThreeTestPoints()
    {
        Assert.False(ScopeRole.TestPoint.CanBeFilledBy(Fake(channels: 2)));
    }

    [Fact]
    public void AOneMegaohmOnlyScopeCannotBeTheFastScope()
    {
        // 4-12 needs a terminated line. An external feedthrough is not the same thing for a
        // rise-time measurement, and the refusal says so rather than leaving it to be rediscovered.
        var unmet = ScopeRole.Fast.UnmetBy(Fake(fiftyOhm: false, bandwidthHz: 1.5e9));

        Assert.Contains(unmet, u => u.Contains("50 ohm"));
        Assert.Contains(unmet, u => u.Contains("feedthrough"));
    }

    [Fact]
    public void AScopeTooCoarseForFiveMillivoltsPerDivisionCannotRunFiveSixteen()
    {
        // 5-16 steps 33 and 34 set 0.005 V/Div.
        Assert.False(ScopeRole.TestPoint.CanBeFilledBy(Fake(minVoltsPerDiv: 0.01)));
        Assert.True(ScopeRole.TestPoint.CanBeFilledBy(Fake(minVoltsPerDiv: 0.005)));
    }

    [Fact]
    public void BandwidthIsJudgedAtTheSensitivityTheRoleActuallyUses()
    {
        // Several scopes give up bandwidth at their most sensitive ranges. Judging at the nominal
        // figure would accept a scope that cannot deliver it where the measurement happens.
        var derated = Fake(
            bandwidthHz: 100e6,
            fiftyOhm: true,
            derates: [new BandwidthDerate(0.05, 40e6)]);

        Assert.Equal(40e6, derated.BandwidthAt(0.05));
        Assert.Equal(100e6, derated.BandwidthAt(1.0));
        Assert.False(ScopeRole.Fast.CanBeFilledBy(derated));
    }

    [Fact]
    public void AHundredMegahertzScopeClearsTheRiseTimeBar()
    {
        // The honest consequence of the arithmetic: Table 4-25 specifies under 25 ns, 0.35/B gives
        // a 100 MHz scope 3.5 ns, and that contributes about 1%. Bandwidth is not what rules
        // scopes out of 4-12 on this bench — the 50 ohm input is.
        Assert.True(ScopeRole.Fast.CanBeFilledBy(Fake(bandwidthHz: 100e6, fiftyOhm: true)));
        Assert.False(ScopeRole.Fast.CanBeFilledBy(Fake(bandwidthHz: 50e6, fiftyOhm: true)));
    }

    [Fact]
    public void EverythingWrongIsReportedAtOnce()
    {
        // One thing per attempt would mean three trips to the bench.
        var hopeless = Fake(channels: 1, bandwidthHz: 10e6, minVoltsPerDiv: 0.1, fiftyOhm: false);

        Assert.True(ScopeRole.Fast.UnmetBy(hopeless).Count >= 2);

        var ex = Assert.Throws<InvalidOperationException>(() => ScopeRole.Fast.Require(hopeless));
        Assert.Contains("fast-scope", ex.Message);
    }

    // --- The real bench --------------------------------------------------------------------------

    [Fact]
    public void TheRigolCanTuneAndWatchTestPointsButCannotBeTheFastScope()
    {
        var rigol = Rigol();

        Assert.True(ScopeRole.Tuning.CanBeFilledBy(rigol));
        Assert.True(ScopeRole.TestPoint.CanBeFilledBy(rigol));
        Assert.False(ScopeRole.Fast.CanBeFilledBy(rigol));
    }

    [Fact]
    public void TheRigolRefusesFiftyOhmRatherThanIgnoringTheRequest()
    {
        // Silently staying at 1 Mohm would leave the detector calibration describing a termination
        // that is not in use (M0-29), and the trace would be wrong by a constant factor.
        var scope = new RigolDs1104Z(
            new HP8340B.Instruments.Visa.SimulatedInstrumentLink("SIM::s::INSTR"));

        Assert.Throws<NotSupportedException>(
            () => scope.SetInputImpedance(1, ScopeInputImpedance.FiftyOhm));

        Assert.Equal(ScopeInputImpedance.OneMegaohm, scope.GetInputImpedance(1));
    }

    [Fact]
    public void RolesAreFoundByTheNameConfigUses()
    {
        Assert.Equal(ScopeRole.Tuning, ScopeRole.ByName("tuning-scope"));
        Assert.Equal(ScopeRole.Fast, ScopeRole.ByName("FAST-SCOPE"));
        Assert.Null(ScopeRole.ByName("nonsense"));
    }
}
