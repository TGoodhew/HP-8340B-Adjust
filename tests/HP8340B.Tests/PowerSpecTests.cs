using HP8340B.Measurements.Model;
using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// Maximum leveled output power, manual Table 4-9 (1 of 2), p. 4-24. Transcribed from the
/// service manual text layer and checked against it on 15 Sep 2026.
/// </summary>
public class MaxLeveledPowerTests
{
    [Theory]
    // Standard: front panel output WITH attenuator.
    [InlineData(InstrumentOption.Standard, 1.0, +10.0)]
    [InlineData(InstrumentOption.Standard, 5.0, +12.0)]
    [InlineData(InstrumentOption.Standard, 10.0, +10.0)]
    [InlineData(InstrumentOption.Standard, 17.0, +9.0)]
    [InlineData(InstrumentOption.Standard, 21.0, +3.0)]
    [InlineData(InstrumentOption.Standard, 25.0, +1.0)]
    // Option 001: front panel output, no attenuator.
    [InlineData(InstrumentOption.Opt001, 5.0, +13.0)]
    [InlineData(InstrumentOption.Opt001, 25.0, +4.0)]
    // Option 004: rear panel output with attenuator - the only option with a negative limit.
    [InlineData(InstrumentOption.Opt004, 17.0, +7.0)]
    [InlineData(InstrumentOption.Opt004, 25.0, -1.0)]
    // Option 005: rear panel output, no attenuator.
    [InlineData(InstrumentOption.Opt005, 10.0, +11.0)]
    [InlineData(InstrumentOption.Opt005, 25.0, +2.0)]
    public void MatchesTable49(InstrumentOption option, double ghz, double expectedDbm) =>
        Assert.Equal(expectedDbm, MaxLeveledPower.ForFrequency(option, ghz), 3);

    [Fact]
    public void Band4SplitsAt23GHz()
    {
        Assert.Equal(+3.0, MaxLeveledPower.ForFrequency(InstrumentOption.Standard, 22.999), 3);
        Assert.Equal(+1.0, MaxLeveledPower.ForFrequency(InstrumentOption.Standard, 23.0), 3);
    }

    [Fact]
    public void TopOfBand4IsInRange() =>
        Assert.Equal(+1.0, MaxLeveledPower.ForFrequency(InstrumentOption.Standard, 26.5), 3);

    [Theory]
    [InlineData(0.005)]
    [InlineData(30.0)]
    public void OutOfRangeThrows(double ghz) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MaxLeveledPower.ForFrequency(InstrumentOption.Standard, ghz));

    /// <summary>
    /// 5-14 steps 15, 22 and 30: the minimum power in a band must be more than 1 dB above the
    /// maximum-leveled-power spec.
    /// </summary>
    [Fact]
    public void MinPowerTargetIsOneDbAboveSpec() =>
        Assert.Equal(+11.0, MaxLeveledPower.MinPowerTargetDbm(InstrumentOption.Standard, 10.0), 3);
}
