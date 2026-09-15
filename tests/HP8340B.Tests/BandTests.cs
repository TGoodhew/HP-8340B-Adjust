using HP8340B.Instruments.Model;
using Xunit;

namespace HP8340B.Tests;

/// <summary>Band structure and the pot each half-band belongs to (manual 5-14 / 5-16).</summary>
public class BandTests
{
    [Theory]
    [InlineData(0.01, BandId.Band0)]
    [InlineData(2.0, BandId.Band0)]
    [InlineData(2.3, BandId.Band1)]
    [InlineData(6.9, BandId.Band1)]
    [InlineData(7.0, BandId.Band2)]
    [InlineData(13.4, BandId.Band2)]
    [InlineData(13.5, BandId.Band3)]
    [InlineData(19.9, BandId.Band3)]
    [InlineData(20.0, BandId.Band4)]
    [InlineData(26.5, BandId.Band4)]
    public void FrequencyMapsToBand(double ghz, BandId expected) =>
        Assert.Equal(expected, Bands.ForFrequency(ghz)!.Id);

    [Fact]
    public void OutOfRangeHasNoBand()
    {
        Assert.Null(Bands.ForFrequency(0.005));
        Assert.Null(Bands.ForFrequency(27.0));
    }

    [Theory]
    // 5-14 steps 70-80: below 10 GHz adjust X2A, above 10 GHz X2B; band 3 splits at 15 GHz;
    // band 4 splits at 23 GHz.
    [InlineData(8.0, "X2A (A24R3)")]
    [InlineData(12.0, "X2B (A24R4)")]
    [InlineData(14.0, "X3A (A24R6)")]
    [InlineData(18.0, "X3B (A24R7)")]
    [InlineData(21.0, "X4A (A24R9)")]
    [InlineData(25.0, "X4B (A24R10)")]
    public void UnleveledPotIsNamedForTheHalfBand(double ghz, string expected) =>
        Assert.Equal(expected, Bands.UnleveledPotFor(ghz));

    [Theory]
    [InlineData(8.0, "X2C (A24R5)")]
    [InlineData(17.5, "X3C (A24R8)")]
    [InlineData(23.3, "X4C (A24R11)")]
    public void LeveledPotIsOnePerBand(double ghz, string expected) =>
        Assert.Equal(expected, Bands.LeveledPotFor(ghz));

    [Theory]
    // Bands 0 and 1 have no SRD bias pot: band 1 squegging is a function of SYTM input power
    // and cannot be adjusted out (manual 5-14 footnote).
    [InlineData(1.0)]
    [InlineData(5.0)]
    public void NonMultiplyingBandsHaveNoSrdBiasPot(double ghz)
    {
        Assert.Null(Bands.UnleveledPotFor(ghz));
        Assert.Null(Bands.LeveledPotFor(ghz));
    }
}
