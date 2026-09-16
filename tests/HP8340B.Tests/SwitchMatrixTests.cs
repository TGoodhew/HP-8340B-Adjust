using HP8340B.Instruments;
using HP8340B.Instruments.Config;
using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// M0-21: the Agilent 3499A. One physical build serves S1-S4 with switch changes instead of
/// re-cabling, which only works if a relay that did not move is caught.
/// </summary>
public class SwitchMatrixTests
{
    private static (Agilent3499A Matrix, SimulatedSwitchMatrix Model) New(bool rfOff = true)
    {
        var model = new SimulatedSwitchMatrix();
        var matrix = new Agilent3499A(model.CreateLink()) { RfIsOff = () => rfOff };

        return (matrix, model);
    }

    // --- Channel numbering ---------------------------------------------------------------------

    [Theory]
    [InlineData(101)]
    [InlineData(312)]
    [InlineData(512)]
    [InlineData(5)]        // slot 0, channel 05 — the guide gives a 3499A slots 0 through 5
    [InlineData(99)]
    public void ValidChannelNumbersAreAccepted(int channel) => Agilent3499A.ValidateChannel(channel);

    [Theory]
    [InlineData(600)]      // slot 6, and a 3499A stops at slot 5
    [InlineData(999)]
    [InlineData(1234)]
    [InlineData(-1)]
    public void ChannelNumbersOutsideTheMainframesSlotsAreRefused(int channel)
    {
        // The guide: "The channel number is in the form of snn", and "3499A slots 0 through 5".
        // Anything implying slot 6 or above belongs to a C mainframe, not this one.
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Agilent3499A.ValidateChannel(channel));
        Assert.Contains("snn", ex.Message);
    }

    [Fact]
    public void ChannelListsAreFormattedTheWayTheGuideWritesThem()
    {
        Assert.Equal("(@101)", Agilent3499A.ChannelList(101));
        Assert.Equal("(@101,203)", Agilent3499A.ChannelList(101, 203));
        Assert.Throws<ArgumentException>(() => Agilent3499A.ChannelList());
    }

    // --- Hot switching, which is the safety requirement ------------------------------------------

    [Fact]
    public void SwitchingWithTheRfOnIsRefused()
    {
        // Repo rule 5. Coaxial relays switched hot arc, and the damage accumulates invisibly
        // until the insertion loss has drifted — so this is structural, not advisory.
        var (matrix, _) = New(rfOff: false);

        var ex = Assert.Throws<InvalidOperationException>(() => matrix.Close(101));
        Assert.Contains("rule 5", ex.Message);
        Assert.Contains("arc", ex.Message);

        Assert.Throws<InvalidOperationException>(() => matrix.Open(101));
        Assert.Throws<InvalidOperationException>(() => matrix.OpenAll());
        Assert.Throws<InvalidOperationException>(() => matrix.ApplySafeState());
    }

    [Fact]
    public void SwitchingWithTheRfOffIsAllowed()
    {
        var (matrix, _) = New(rfOff: true);
        matrix.Close(101);
        matrix.Open(101);
    }

    [Fact]
    public void AnUnwiredGuardSwitchesAnywayAndProbeSaysSo()
    {
        // Left null the driver genuinely cannot tell, and pretending otherwise would be worse
        // than admitting it. probe says the guard is not wired so it can be noticed before a
        // routed run rather than after.
        var model = new SimulatedSwitchMatrix();
        var matrix = new Agilent3499A(model.CreateLink());

        matrix.Close(101);   // no throw

        Assert.Contains("Hot-switch guard is NOT wired", matrix.Probe().Detail);
    }

    // --- State readback -------------------------------------------------------------------------

    [Fact]
    public void ClosingIsConfirmedAgainstTheMainframe()
    {
        var (matrix, _) = New();
        matrix.CloseVerified(101, 102);

        Assert.All(matrix.IsClosed(101, 102), Assert.True);
    }

    [Fact]
    public void ARelayThatDidNotMoveIsCaught()
    {
        // The whole point of the readback. A relay that stayed open sends the measurement down
        // the wrong path, which looks like a plausible wrong number rather than a failure — the
        // worst outcome in this project.
        var (matrix, model) = New();
        model.StuckOpen.Add(102);

        var ex = Assert.Throws<InvalidOperationException>(() => matrix.CloseVerified(101, 102));

        Assert.Contains("102", ex.Message);
        Assert.Contains("plausible wrong number", ex.Message);
    }

    [Fact]
    public void OpeningActuallyOpens()
    {
        var (matrix, _) = New();

        matrix.CloseVerified(101);
        matrix.Open(101);

        Assert.False(matrix.IsClosed(101)[0]);
    }

    [Fact]
    public void OpenAllClearsEverything()
    {
        var (matrix, _) = New();
        matrix.CloseVerified(101, 102, 103);

        matrix.OpenAll();

        Assert.All(matrix.IsClosed(101, 102, 103), Assert.False);
    }

    [Fact]
    public void AMismatchedAnswerCountIsRefusedRatherThanMisread()
    {
        // If the mainframe answers a different number of channels than were asked about, lining
        // the answers up positionally would silently attribute one channel's state to another.
        var model = new SimulatedSwitchMatrix();
        var link = new HP8340B.Instruments.Visa.SimulatedInstrumentLink("SIM::sw::INSTR", _ => "1");
        var matrix = new Agilent3499A(link);

        Assert.Throws<InvalidOperationException>(() => matrix.IsClosed(101, 102));
    }

    // --- Module identification, which is what D-11 needs -------------------------------------------

    [Fact]
    public void EachPopulatedSlotIdentifiesItself()
    {
        var (matrix, _) = New();

        var slot1 = matrix.IdentifySlot(1);

        Assert.NotNull(slot1);
        Assert.Equal(1, slot1.Slot);
        Assert.Equal("GP RELAY 44471", slot1.ReportedType);
        Assert.NotEmpty(slot1.Serial);
    }

    [Fact]
    public void AnEmptySlotReportsNothingRatherThanAnEmptyModule()
    {
        var (matrix, _) = New();
        Assert.Null(matrix.IdentifySlot(4));
    }

    [Fact]
    public void TheWholeMapCanBeReadInOneGoForDecisionElevenSlot()
    {
        var (matrix, _) = New();
        var modules = matrix.IdentifyAllSlots();

        Assert.Equal(3, modules.Count);
        Assert.Contains(modules, m => m.ReportedType.Contains("VHF SW 44472"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    public void SlotNumbersAreRangeChecked(int slot)
    {
        var (matrix, _) = New();
        Assert.Throws<ArgumentOutOfRangeException>(() => matrix.IdentifySlot(slot));
    }

    [Fact]
    public void ProbeSaysTheSlotMapIsStillADecisionWhenNothingIsFound()
    {
        var model = new SimulatedSwitchMatrix();
        model.Slots.Clear();

        var matrix = new Agilent3499A(model.CreateLink()) { RfIsOff = () => true };

        Assert.Contains("D-11", matrix.Probe().Detail);
    }

    // --- Relay wear --------------------------------------------------------------------------------

    [Fact]
    public void RelayCyclesAreCountedSoWearIsVisible()
    {
        // Coaxial relays wear out, and a leg whose count is climbing is worth knowing about
        // before its insertion loss drifts.
        var (matrix, _) = New();

        Assert.Equal(0, matrix.RelayCycles(101)[0]);

        matrix.CloseVerified(101);
        matrix.Open(101);
        matrix.CloseVerified(101);

        Assert.Equal(3, matrix.RelayCycles(101)[0]);
    }

    [Fact]
    public void TheWorstRelayOnAModuleCanBeAskedForDirectly()
    {
        var (matrix, _) = New();

        matrix.CloseVerified(101);
        matrix.CloseVerified(102);
        matrix.Open(102);

        Assert.Equal(2, matrix.MaxRelayCycles(1));
    }

    // --- Safe state ------------------------------------------------------------------------------------

    [Fact]
    public void TheSafeStateOpensEverything()
    {
        // Nothing connected to anything, and the DVM multiplexer not sitting across a test point.
        var (matrix, _) = New();
        matrix.CloseVerified(101, 203);

        matrix.ApplySafeState();

        Assert.All(matrix.IsClosed(101, 203), Assert.False);
    }

    [Fact]
    public void TheSafeStateIsExplicitRatherThanRelyingOnThePowerOnState()
    {
        var model = new SimulatedSwitchMatrix();
        var link = model.CreateLink();
        var matrix = new Agilent3499A(link) { RfIsOff = () => true };

        matrix.ApplySafeState();

        Assert.Contains("SYSTem:CPON ALL", link.History);
        Assert.Contains("ROUTe:OPEN ALL", link.History);
    }

    // --- Frequency limits per switch type ------------------------------------------------------------------

    [Theory]
    [InlineData("33311C", 26.5e9)]
    [InlineData("33311B", 18e9)]
    [InlineData("44472A", 100e6)]
    [InlineData("something else", 0)]
    public void EachSwitchTypeKnowsHowHighItGoes(string model, double expected) =>
        Assert.Equal(expected, SwitchModule.MaxHzForSwitch(model));

    // --- What the mainframe cannot tell you -----------------------------------------------------

    [Fact]
    public void TheDriverModulesOnThisBenchCannotBeIdentifiedOverTheBus()
    {
        // The guide's SYSTem:CTYPE? table, footnote b: all of the 44471A/D, 44476A/B and 44477A
        // return "GP RELAY 44471", and "you must physically check the modules to determine which
        // one is present". This bench drives its coaxial switches from 44476A/B modules, so the
        // slot map D-11 wants cannot be closed from the bus however thoroughly it is queried.
        var (matrix, _) = New();

        var slot1 = matrix.IdentifySlot(1)!;

        Assert.True(slot1.IsAmbiguous);
        Assert.Contains("44476A", slot1.AmbiguousWith);
        Assert.Contains("44476B", slot1.AmbiguousWith);
        Assert.Contains("44471A", slot1.AmbiguousWith);
    }

    [Fact]
    public void TheAmbiguousSlotsCanBeListedForAPhysicalCheck()
    {
        var (matrix, _) = New();
        var needChecking = matrix.SlotMapNeedsPhysicalCheck();

        // All three of the simulated bench's modules are in ambiguous families.
        Assert.Equal(3, needChecking.Count);
    }

    [Fact]
    public void ProbeSaysTheSlotMapCannotBeClosedFromTheBus()
    {
        var (matrix, _) = New();
        Assert.Contains("physically check", matrix.Probe().Detail);
    }

    [Fact]
    public void AnUnambiguousModuleIsNotFlagged()
    {
        var model = new SimulatedSwitchMatrix();
        model.Slots.Clear();
        model.Slots[0] = ("MATRIX SW 44473", "SIM9");

        var matrix = new Agilent3499A(model.CreateLink()) { RfIsOff = () => true };

        Assert.False(matrix.IdentifySlot(0)!.IsAmbiguous);
        Assert.Empty(matrix.SlotMapNeedsPhysicalCheck());
    }

    [Fact]
    public void SlotZeroIsValidOnAThreeFourNineNineA()
    {
        // "3499A slots 0 through 5", so channel 012 is a real channel and slot 0 is not an error.
        Agilent3499A.ValidateChannel(12);

        var (matrix, _) = New();
        matrix.IdentifySlot(0);   // must not throw
    }

    [Fact]
    public void FactoryBuildsTheRealDriverInSimulation()
    {
        var config = new InstrumentConfig
        {
            Role = "switch-matrix", Model = "Agilent 3499A", Address = "GPIB0::9::INSTR",
        };

        using var instrument = InstrumentFactory.Create(config, simulate: true);

        Assert.IsType<Agilent3499A>(instrument);
        Assert.True(instrument.Probe().Responded);
    }
}
