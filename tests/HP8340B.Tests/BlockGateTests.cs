using HP8340B.Bench.Model;
using HP8340B.Instruments;
using HP8340B.Instruments.Config;
using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// M0-19's acknowledgement gate and M0-20's wiring self-checks.
///
/// <para>A hook-up card tells you what to connect; a self-check knows whether you did. A
/// mis-cabled bench produces plausible-looking wrong numbers, which is the worst failure mode in
/// this project — so the block does not start until both are satisfied.</para>
/// </summary>
public class BlockGateTests
{
    private static SetupCatalogue Catalogue() =>
        SetupCatalogue.Load(Path.Combine(AppContext.BaseDirectory, "Data", "setups.json"));

    private static BenchSetup Setup(string id) => Catalogue().ById(id)!;

    private static SelfCheckContext SimulatedBenchContext(double padDb = 0)
    {
        var bench = BenchConfig.Load(Path.Combine(AppContext.BaseDirectory, "bench.json"));

        // One shared state, so the simulated instruments agree with each other — which is the
        // only way a check that compares two of them proves anything.
        var state = new SimulatedBenchState();

        var instruments = bench.Instruments
            .Where(i => i is { Present: true, Probeable: true })
            .Select(i => InstrumentFactory.Create(i, simulate: true, state))
            .ToList();

        return new SelfCheckContext(instruments) { PadDb = padDb, CheckDbm = 0 };
    }

    // --- The gate ---------------------------------------------------------------------------------

    [Fact]
    public void ABlockDoesNotStartWithoutAnAcknowledgement()
    {
        var gate = new BlockGate();

        var ex = Assert.Throws<InvalidOperationException>(() => gate.RequireReady("S1"));

        Assert.Contains("has not been acknowledged", ex.Message);
        Assert.Contains("setup show S1", ex.Message);
    }

    [Fact]
    public void ABlockDoesNotStartWithoutASelfCheckEither()
    {
        // An acknowledgement is a promise. The check is the evidence.
        var gate = new BlockGate();
        gate.Acknowledge(Setup("S1"), "Tony");

        var ex = Assert.Throws<InvalidOperationException>(() => gate.RequireReady("S1"));
        Assert.Contains("has not been run", ex.Message);
    }

    [Fact]
    public void BothTogetherOpenTheGate()
    {
        var gate = new BlockGate();
        var setup = Setup("S0");

        gate.Acknowledge(setup, "Tony");
        var outcome = gate.RunSelfCheck(setup, SimulatedBenchContext());

        Assert.Equal(SelfCheckStatus.Passed, outcome.Status);
        Assert.True(gate.IsReady("S0"));

        gate.RequireReady("S0");   // must not throw
    }

    [Fact]
    public void AnAcknowledgementExpires()
    {
        // Cables get moved. An acknowledgement from three hours ago is not evidence about the
        // bench as it is now.
        var gate = new BlockGate { AcknowledgementValidFor = TimeSpan.Zero };
        var setup = Setup("S0");

        gate.Acknowledge(setup, "Tony");
        gate.RunSelfCheck(setup, SimulatedBenchContext());

        var ex = Assert.Throws<InvalidOperationException>(() => gate.RequireReady("S0"));
        Assert.Contains("re-acknowledge", ex.Message);
    }

    [Fact]
    public void AnAnonymousAcknowledgementIsRefused()
    {
        // It goes into the session record as evidence about who checked the bench, and an
        // anonymous one is not evidence.
        var gate = new BlockGate();

        Assert.Throws<ArgumentException>(() => gate.Acknowledge(Setup("S1"), "  "));
    }

    [Fact]
    public void TheAcknowledgementRecordsWhetherTheCardStillHadUnverifiedConnectors()
    {
        // It changes how much the acknowledgement is worth: somebody confirming connections
        // against names nobody has checked is confirming less than it appears.
        var gate = new BlockGate();
        var setup = Setup("S1");

        var acknowledgement = gate.Acknowledge(setup, "Tony");

        Assert.Equal(setup.HasUnverifiedConnectors, acknowledgement.UnverifiedConnectors);
        Assert.Equal("Tony", acknowledgement.By);
        Assert.Equal("S1", acknowledgement.SetupId);
    }

    [Fact]
    public void EverythingIsLoggedForTheSession()
    {
        var gate = new BlockGate();
        var setup = Setup("S0");

        gate.Acknowledge(setup, "Tony");
        gate.RunSelfCheck(setup, SimulatedBenchContext());

        Assert.Single(gate.Acknowledgements);
        Assert.Single(gate.SelfCheckResults);
        Assert.All(gate.Acknowledgements, a => Assert.NotEqual(default, a.At));
    }

    // --- A check that could not run is not a pass ---------------------------------------------------

    [Fact]
    public void ASetupWithNoExecutableCheckReportsNotRunRatherThanPassing()
    {
        // The whole reason the third state exists. A check that silently does nothing produces
        // the reassurance without the evidence.
        var setup = Setup("S5");
        var outcome = SelfChecks.Run(setup, SimulatedBenchContext());

        Assert.Equal(SelfCheckStatus.NotRun, outcome.Status);
        Assert.Contains("NOT a pass", outcome.Observed);
    }

    [Fact]
    public void ANotRunCheckDoesNotOpenTheGate()
    {
        var gate = new BlockGate();
        var setup = Setup("S5");

        gate.Acknowledge(setup, "Tony");
        gate.RunSelfCheck(setup, SimulatedBenchContext());

        var ex = Assert.Throws<InvalidOperationException>(() => gate.RequireReady("S5"));
        Assert.Contains("could not run", ex.Message);
        Assert.False(gate.IsReady("S5"));
    }

    [Fact]
    public void AMissingInstrumentIsNotRunNotFailed()
    {
        // "The counter is not on the bench" is a different statement from "the wiring is wrong",
        // and conflating them would send somebody looking for a cable fault that is not there.
        var context = new SelfCheckContext([]);
        var outcome = SelfChecks.Run(Setup("S2"), context);

        Assert.Equal(SelfCheckStatus.NotRun, outcome.Status);
        Assert.Contains("not on the bench", outcome.Observed);
    }

    // --- The checks themselves -----------------------------------------------------------------------

    [Fact]
    public void TheStandingConfigurationCheckReadsTheDutsReferenceBit()
    {
        var outcome = SelfChecks.Run(Setup("S0"), SimulatedBenchContext());

        Assert.Equal(SelfCheckStatus.Passed, outcome.Status);
        Assert.Contains("external reference selected", outcome.Observed);
    }

    [Fact]
    public void TheAnalyzerPathCheckComparesLevelAgainstWhatWasRequested()
    {
        var outcome = SelfChecks.Run(Setup("S1"), SimulatedBenchContext());

        Assert.Equal(SelfCheckStatus.Passed, outcome.Status);
        Assert.Contains("dBm at the analyzer", outcome.Observed);
    }

    [Fact]
    public void AWrongPadValueFailsTheAnalyzerCheckWithBothNumbers()
    {
        // The most likely real mis-configuration: a pad declared in config that is not the one
        // fitted. The failure has to name what it expected and what it saw, or it is just a
        // refusal.
        var outcome = SelfChecks.Run(Setup("S1"), SimulatedBenchContext(padDb: 30));

        Assert.Equal(SelfCheckStatus.Failed, outcome.Status);
        Assert.Contains("dB out", outcome.Observed);
        Assert.Contains("pad", outcome.Observed);
    }

    [Fact]
    public void TheCounterCheckComparesFrequency()
    {
        var outcome = SelfChecks.Run(Setup("S2"), SimulatedBenchContext());

        Assert.Equal(SelfCheckStatus.Passed, outcome.Status);
        Assert.Contains("GHz", outcome.Observed);
    }

    [Fact]
    public void EveryImplementedCheckMatchesASetupInTheCatalogue()
    {
        // Catches a check registered against a setup id that does not exist — it would never run
        // and nothing would say so.
        var catalogue = Catalogue();

        Assert.All(SelfChecks.Implemented, id => Assert.NotNull(catalogue.ById(id)));
    }

    [Fact]
    public void EverySetupWithAWrittenExpectationIsEitherImplementedOrHonestlyNotRun()
    {
        // Every setup in the catalogue must give an answer of some kind; none may silently do
        // nothing.
        var context = SimulatedBenchContext();

        foreach (var setup in Catalogue().Setups)
        {
            var outcome = SelfChecks.Run(setup, context);

            Assert.False(string.IsNullOrWhiteSpace(outcome.Observed));
            Assert.Equal(setup.Id, outcome.SetupId);
        }
    }

    [Fact]
    public void AnOutcomeDescribesItselfForTheSessionRecord()
    {
        var outcome = SelfChecks.Run(Setup("S0"), SimulatedBenchContext());
        var line = outcome.Describe();

        Assert.Contains("S0", line);
        Assert.Contains("Expected", line);
        Assert.Contains("Observed", line);
    }
}
