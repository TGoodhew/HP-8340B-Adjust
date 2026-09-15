using HP8340B.Instruments;
using HP8340B.Instruments.Config;
using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// Repo rule 4: simulators are first-class and dotnet test must pass with no hardware.
/// These exercise the DUT driver's command builders over a simulated link.
/// </summary>
public class SimulatedBenchTests
{
    private static Hp8340B NewDut()
    {
        var link = SimulatedBench.LinkFor("HP 8340B", "SIM::dut::INSTR");
        return new Hp8340B(link);
    }

    [Fact]
    public void InitializePresetsAndTurnsRfOff()
    {
        var dut = NewDut();
        dut.Initialize();

        // Clear() wipes the simulated history, so only the two writes after it remain.
        Assert.Equal(new[] { "IP", "RF0" }, dut.Link.History);
    }

    [Fact]
    public void CwUsesGigahertzUnits()
    {
        var dut = NewDut();
        dut.SetCwGHz(2.3);
        Assert.Equal("CW 2.3 GZ", dut.Link.History[^1]);
    }

    [Fact]
    public void PowerLevelUsesTheDbTerminator()
    {
        var dut = NewDut();
        dut.SetPowerDbm(-30);

        // Matches the Section III example: IP CW 2.3 GZ PL -30 DB
        Assert.Equal("PL -30 DB", dut.Link.History[^1]);
    }

    [Fact]
    public void SingleSweepAndTakeSweepUseVerifiedCodes()
    {
        var dut = NewDut();
        dut.SingleSweep();
        dut.TakeSweep();
        Assert.Equal(new[] { "S2", "TS" }, dut.Link.History);
    }

    [Fact]
    public void SimulatedDutReportsSettled()
    {
        var dut = NewDut();
        Assert.True(dut.ReadStatus().HasFlag(StatusByte1.RfSettled));
        Assert.True(dut.WaitSettled(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void SyntaxErrorBitAbortsTheSettleWait()
    {
        var link = SimulatedBench.LinkFor("HP 8340B", "SIM::dut::INSTR");
        link.StatusByte = (byte)StatusByte1.SyntaxError;
        var dut = new Hp8340B(link);

        var ex = Assert.Throws<InvalidOperationException>(
            () => dut.WaitSettled(TimeSpan.FromMilliseconds(200)));
        Assert.Contains("syntax error", ex.Message);
    }

    [Fact]
    public void ProbeSucceedsWithoutIdnBecauseTheDutPredates4882()
    {
        var dut = NewDut();
        var result = dut.Probe();

        Assert.True(result.Responded);
        Assert.Contains("status byte", result.Identity);
    }

    [Fact]
    public void ScpiInstrumentsAnswerIdnInSimulation()
    {
        var config = new InstrumentConfig
        {
            Role = "spectrum-analyzer", Model = "HP 8563E", Address = "GPIB0::18::INSTR",
        };

        using var instrument = InstrumentFactory.Create(config, simulate: true);
        var result = instrument.Probe();

        Assert.True(result.Responded);
        Assert.Contains("8563E", result.Identity);
    }

    [Theory]
    // Pre-488.2 instruments cannot answer *IDN?; the 11713A cannot talk at all.
    [InlineData("HP 8902A", false)]
    [InlineData("HP 8673B", false)]
    [InlineData("HP 11713A", true)]
    public void LegacyInstrumentsProbeBySerialPoll(string model, bool listenOnly)
    {
        var config = new InstrumentConfig { Role = "legacy", Model = model, Address = "GPIB0::1::INSTR" };

        Assert.True(SimulatedBench.IsLegacy(model));
        Assert.Equal(listenOnly, SimulatedBench.IsListenOnly(model));

        using var instrument = InstrumentFactory.Create(config, simulate: true);
        Assert.True(instrument.Probe().Responded);
    }

    [Fact]
    public void LiveModeRefusesAnUnknownAddress()
    {
        var config = new InstrumentConfig { Role = "dmm", Model = "HP 3458A", Address = "TBD" };

        var ex = Assert.Throws<InvalidOperationException>(
            () => InstrumentFactory.Create(config, simulate: false));
        Assert.Contains("no address configured", ex.Message);
    }
}
