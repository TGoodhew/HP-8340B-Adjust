using HP8340B.Bench;
using HP8340B.Bench.Model;
using HP8340B.Instruments.Config;
using HP8340B.Instruments.Model;
using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// M0-17's capability rule: what this bench can claim for absolute power, and what would change
/// it.
///
/// <para>Band 4 is the case that matters. The 8481A and the 11792A both stop at 18 GHz, so with
/// those alone a 20-26.5 GHz power measurement is not a measurement. Whether the bench can claim
/// Spec up there depends on which sensors are present — and "present" is a config flag that
/// changes when borrowed kit goes back, so a rule kept in somebody's head would be wrong the
/// first time it did.</para>
/// </summary>
public class BenchCapabilityTests
{
    private static BenchConfig Bench(params InstrumentConfig[] sensors)
    {
        var bench = new BenchConfig();
        bench.Instruments.AddRange(sensors);
        return bench;
    }

    private static InstrumentConfig Sensor(string role, string model, bool present = true) =>
        new() { Role = role, Model = model, Address = "n/a", Present = present, Probeable = false };

    private static BenchConfig ShippedBench() =>
        BenchConfig.Load(Path.Combine(AppContext.BaseDirectory, "bench.json"));

    // --- The band-4 rule ------------------------------------------------------------------------

    [Fact]
    public void An8481AAloneCannotMakeBandFourSpec()
    {
        // It stops at 18 GHz and band 4 runs 20 to 26.5.
        var bench = Bench(Sensor("power-sensor-low", "HP 8481A"));

        var capability = BenchCapability.For(bench, BandId.Band4);

        Assert.NotEqual(TraceabilityClass.Spec, capability.Best);
    }

    [Fact]
    public void An8485AMakesBandFourSpec()
    {
        // Its cal-factor data reaches 26.5 GHz, which is the whole reason it matters here.
        var bench = Bench(Sensor("power-sensor-high", "HP 8485A"));

        var capability = BenchCapability.For(bench, BandId.Band4);

        Assert.Equal(TraceabilityClass.Spec, capability.Best);
        Assert.Contains("8485A", capability.Path!);
    }

    [Fact]
    public void ASensorThatIsNotPresentDoesNotCount()
    {
        // The flag is the whole mechanism: a sensor that has gone back cannot support a claim.
        var bench = Bench(Sensor("power-sensor-high", "HP 8485A", present: false));

        Assert.NotEqual(TraceabilityClass.Spec, BenchCapability.For(bench, BandId.Band4).Best);
    }

    [Fact]
    public void AnAbsentSensorThatWouldHelpIsNamedAsTheUpgrade()
    {
        // The other half of the mechanism: saying what would fix it is more useful than saying it
        // is broken.
        var bench = Bench(
            Sensor("power-sensor-low", "HP 8481A"),
            Sensor("usb-power-sensor", "Keysight U8485A", present: false));

        var capability = BenchCapability.For(bench, BandId.Band4);

        Assert.Contains("U8485A", capability.WouldUpgrade!);
        Assert.Contains("would make this band Spec", capability.WouldUpgrade!);
    }

    [Fact]
    public void TheUpgradeIsAutomaticWhenTheFlagFlips()
    {
        // The acceptance criterion: band-4 power upgrades from Relative to Spec automatically
        // when a capable sensor is marked present. No code change, no remembered rule.
        var sensor = Sensor("usb-power-sensor", "Keysight U8485A", present: false);
        var bench = Bench(Sensor("power-sensor-low", "HP 8481A"), sensor);

        Assert.NotEqual(TraceabilityClass.Spec, BenchCapability.For(bench, BandId.Band4).Best);

        sensor.Present = true;

        Assert.Equal(TraceabilityClass.Spec, BenchCapability.For(bench, BandId.Band4).Best);
    }

    [Fact]
    public void ASensorCoveringPartOfTheBandGivesRelativeNotNothing()
    {
        // Band 3 runs 13.5 to 20 GHz and the 8481A reaches 18, so it covers most of it. Deltas
        // taken entirely inside that overlap are still valid, and Relative is the honest answer.
        var bench = Bench(Sensor("power-sensor-low", "HP 8481A"));

        Assert.Equal(TraceabilityClass.Relative, BenchCapability.For(bench, BandId.Band3).Best);
    }

    [Fact]
    public void ASensorWhoseRangeDoesNotTouchTheBandAtAllGivesNothing()
    {
        // An 8481A stops at 18 GHz and band 4 starts at 20, so there is no overlap whatever --
        // not even a delta. That is NotPossible, not Relative, and the distinction is worth
        // keeping: Relative means "valid within itself", and there is no within here.
        var bench = Bench(Sensor("power-sensor-low", "HP 8481A"));

        Assert.Equal(TraceabilityClass.NotPossible, BenchCapability.For(bench, BandId.Band4).Best);
    }

    [Fact]
    public void ABenchWithNoSensorAtAllCanClaimNothing()
    {
        Assert.Equal(TraceabilityClass.NotPossible,
            BenchCapability.For(Bench(), BandId.Band4).Best);
    }

    [Fact]
    public void ASensorModelThisProjectHasNeverHeardOfContributesNothing()
    {
        // Silence about a range must not read as "covers everything".
        var bench = Bench(Sensor("power-sensor-high", "Acme Wondersensor 9000"));

        Assert.Equal(TraceabilityClass.NotPossible,
            BenchCapability.For(bench, BandId.Band4).Best);
    }

    // --- A sensor must cover the WHOLE band ------------------------------------------------------

    [Fact]
    public void The8485ACannotCoverBandZeroBecauseItStartsAt30Megahertz()
    {
        // Band 0 starts at 10 MHz. A claim that holds at one end of a band and not the other is
        // not a claim about the band.
        var bench = Bench(Sensor("power-sensor-high", "HP 8485A"));

        Assert.NotEqual(TraceabilityClass.Spec, BenchCapability.For(bench, BandId.Band0).Best);
    }

    [Fact]
    public void The8481AIsWhatCoversTheBottomOfBandZero()
    {
        var bench = Bench(Sensor("power-sensor-low", "HP 8481A"));

        Assert.Equal(TraceabilityClass.Spec, BenchCapability.For(bench, BandId.Band0).Best);
    }

    // --- Constraining a claim -----------------------------------------------------------------------

    [Fact]
    public void ASpecClaimInBandFourIsCutDownWithoutACapableSensor()
    {
        var bench = Bench(Sensor("power-sensor-low", "HP 8481A"));

        var result = BenchCapability.Constrain(bench, BandId.Band4, TraceabilityClass.Spec);

        Assert.True(result.WasReduced);
        Assert.NotEqual(TraceabilityClass.Spec, result.Allowed);

        // The 8481A's range does not reach band 4 at all, so the reason is that nothing covers
        // it rather than that the best sensor falls short.
        Assert.Contains("no sensor on this bench covers the band", result.Reason!);
    }

    [Fact]
    public void ASpecClaimStandsWhenTheBenchSupportsIt()
    {
        var bench = Bench(Sensor("power-sensor-high", "HP 8485A"));

        var result = BenchCapability.Constrain(bench, BandId.Band4, TraceabilityClass.Spec);

        Assert.False(result.WasReduced);
        Assert.Equal(TraceabilityClass.Spec, result.Allowed);
    }

    [Fact]
    public void TheBenchCanOnlyEverMakeAClaimWorseNotBetter()
    {
        // A Relative measurement does not become Spec because a good sensor happens to be plugged
        // in — the constraint is a ceiling, not a floor.
        var bench = Bench(Sensor("power-sensor-high", "HP 8485A"));

        var result = BenchCapability.Constrain(bench, BandId.Band4, TraceabilityClass.Relative);

        Assert.Equal(TraceabilityClass.Relative, result.Allowed);
        Assert.False(result.WasReduced);
    }

    // --- The bench as shipped -----------------------------------------------------------------------

    [Fact]
    public void TheShippedBenchCoversEveryBandForPower()
    {
        // The 8481A and 8485A together span 10 MHz to 26.5 GHz, which is the whole instrument.
        // This is the claim GEAR-MAP makes in prose; here it is checked against the config.
        var bench = ShippedBench();

        Assert.All(BenchCapability.All(bench), c =>
            Assert.Equal(TraceabilityClass.Spec, c.Best));
    }

    [Fact]
    public void RemovingThe8485AFromTheShippedBenchCostsBandFour()
    {
        // The dependency, demonstrated rather than asserted: this is what happens if that sensor
        // is unavailable.
        var bench = ShippedBench();
        bench.ByRole("power-sensor-high")!.Present = false;

        Assert.NotEqual(TraceabilityClass.Spec, BenchCapability.For(bench, BandId.Band4).Best);

        // And bands 0 to 3 are unaffected, because the 8481A covers them.
        Assert.Equal(TraceabilityClass.Spec, BenchCapability.For(bench, BandId.Band2).Best);
    }

    [Fact]
    public void BorrowedSensorsAreListedSoTheirSerialsCanBeRecorded()
    {
        // D-10: the session has to record which borrowed unit produced a result, because a
        // borrowed sensor's calibration history is not this bench's.
        var bench = ShippedBench();
        var borrowed = bench.Instruments.Where(i => i.Borrowed).ToList();

        Assert.NotEmpty(borrowed);
        Assert.All(borrowed, i => Assert.False(string.IsNullOrWhiteSpace(i.Note)));
    }
}
