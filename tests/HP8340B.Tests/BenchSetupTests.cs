using HP8340B.Bench;
using HP8340B.Bench.Model;
using HP8340B.Instruments.Config;
using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// The setup catalogue and bench config are data files (D.4). These prove they parse and that
/// every setup carries what a hook-up card needs.
/// </summary>
public class BenchSetupTests
{
    private static SetupCatalogue LoadCatalogue() =>
        SetupCatalogue.Load(Path.Combine(AppContext.BaseDirectory, "Data", "setups.json"));

    [Fact]
    public void CatalogueContainsS0ThroughS10()
    {
        var catalogue = LoadCatalogue();
        var ids = catalogue.Setups.Select(s => s.Id).ToList();

        foreach (var expected in Enumerable.Range(0, 11).Select(n => $"S{n}"))
            Assert.Contains(expected, ids);
    }

    [Fact]
    public void EverySetupHasAName_UsedBy_AndSelfCheck()
    {
        foreach (var setup in LoadCatalogue().Setups)
        {
            Assert.False(string.IsNullOrWhiteSpace(setup.Name), $"{setup.Id} has no name.");
            Assert.False(string.IsNullOrWhiteSpace(setup.UsedBy), $"{setup.Id} says nothing about what uses it.");
            Assert.NotNull(setup.SelfCheck);
            Assert.False(string.IsNullOrWhiteSpace(setup.SelfCheck!.Expected),
                $"{setup.Id} self-check does not say what to expect.");
        }
    }

    [Fact]
    public void EverySetupThatCarriesRfDeclaresSafetyLimits()
    {
        foreach (var setup in LoadCatalogue().Setups)
        {
            Assert.NotEmpty(setup.SafetyLimits);
        }
    }

    [Fact]
    public void HookUpCardListsConnectionsInSignalOrder()
    {
        var setup = LoadCatalogue().ById("S4")!;
        var card = HookUpCard.Render(setup);

        Assert.Contains("S4", card);
        Assert.Contains("CONNECTIONS", card);
        Assert.Contains("8473C", card);
        Assert.Contains("DS1104Z CH1", card);
        Assert.Contains("WIRING SELF-CHECK", card);
        Assert.Contains("SAFETY LIMITS", card);
    }

    [Fact]
    public void UnverifiedConnectorNamesAreFlaggedOnTheCard()
    {
        var setup = LoadCatalogue().ById("S1")!;

        Assert.True(setup.HasUnverifiedConnectors);
        Assert.Contains("verify-connector", HookUpCard.Render(setup));
    }

    [Fact]
    public void BenchConfigParsesAndKnowsTheKnownAddresses()
    {
        var bench = BenchConfig.Load(Path.Combine(AppContext.BaseDirectory, "bench.json"));

        Assert.Equal("Standard", bench.DutOption);
        Assert.Equal("GPIB0::18::INSTR", bench.ByRole("spectrum-analyzer")!.Address);
        Assert.Equal("GPIB0::14::INSTR", bench.ByRole("counter")!.Address);
        Assert.Equal("GPIB0::10::INSTR", bench.ByRole("function-generator")!.Address);
        Assert.Equal("GPIB0::6::INSTR", bench.ByRole("plotter")!.Address);
        Assert.Contains("192.168.1.145", bench.ByRole("oscilloscope")!.Address);
    }

    [Fact]
    public void UnknownAddressesAreReportedAsSuch()
    {
        var bench = BenchConfig.Load(Path.Combine(AppContext.BaseDirectory, "bench.json"));

        Assert.True(bench.ByRole("dmm")!.AddressUnknown);
        Assert.False(bench.ByRole("spectrum-analyzer")!.AddressUnknown);
    }

    [Fact]
    public void BorrowedKitIsAbsentUntilItIsOnTheBench()
    {
        var bench = BenchConfig.Load(Path.Combine(AppContext.BaseDirectory, "bench.json"));
        Assert.False(bench.ByRole("usb-power-sensor")!.Present);
        Assert.False(bench.ByRole("splitter-26g")!.Present);
    }

    [Fact]
    public void The437BIsAStandingInstrumentNotBorrowedKit()
    {
        // It reads the 848x sensors, which the 432A cannot, and it is on HP-IB. That makes it the
        // absolute-power path rather than something that has to arrive with a borrowed sensor.
        var meter = BenchConfig.Load(Path.Combine(AppContext.BaseDirectory, "bench.json"))
            .ByRole("power-meter")!;

        Assert.Equal("HP 437B", meter.Model);
        Assert.True(meter.Present);
        Assert.True(meter.Probeable);
    }

    [Fact]
    public void BothPowerSensorsArePresentAndSpanTheWholeInstrument()
    {
        // 8481A 10 MHz - 18 GHz and 8485A to 26.5 GHz, both on the 437B. Together they cover the
        // 8340B's entire 10 MHz - 26.5 GHz range, so band-4 absolute power is Spec with nothing
        // borrowed. Sensor cal-factor ranges: 8481A manual Table 1-3.
        var bench = BenchConfig.Load(Path.Combine(AppContext.BaseDirectory, "bench.json"));

        var low = bench.ByRole("power-sensor-low")!;
        var high = bench.ByRole("power-sensor-high")!;

        Assert.Equal("HP 8481A", low.Model);
        Assert.Equal("HP 8485A", high.Model);
        Assert.True(low.Present);
        Assert.True(high.Present);
    }

    [Fact]
    public void PassiveKitIsNotProbed()
    {
        // Sensors plug into the meter and splitters are passive: neither is on the bus, so probe
        // must report them rather than trying to open a VISA session to them.
        var bench = BenchConfig.Load(Path.Combine(AppContext.BaseDirectory, "bench.json"));

        foreach (var role in new[] { "power-sensor-low", "power-sensor-high", "splitter-26g" })
            Assert.False(bench.ByRole(role)!.Probeable, $"'{role}' should not be probed.");
    }
}
