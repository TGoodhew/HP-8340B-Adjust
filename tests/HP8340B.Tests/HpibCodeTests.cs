using HP8340B.Instruments;
using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// Repo rule 2: never fabricate an HP-IB code. These tests document where every Verified code
/// came from and prove the Unverified ones cannot reach an instrument.
/// </summary>
public class HpibCodeTests
{
    [Theory]
    // Service manual 4-17, p. 4-83: "the program outputs an IP (INSTR PRESET), S2 (Single
    // sweep), and two TS (Take Sweep) commands." Program listing in Table 4-32.
    [InlineData("IP")]
    [InlineData("S2")]
    [InlineData("TS")]
    // Section III example, quoted in the brief: "IP CW 2.3 GZ PL -30 DB".
    [InlineData("CW")]
    [InlineData("PL")]
    [InlineData("GZ")]
    [InlineData("DB")]
    // Proven on this bench in HP-Attenuator (src/HP-Attenuator.Core/Instruments/Hp8340B.cs).
    [InlineData("MZ")]
    [InlineData("RF1")]
    [InlineData("RF0")]
    public void VerifiedCodesAreSendable(string code)
    {
        Assert.Equal(code, Hp8340BCommands.Require(code));
        Assert.Equal(CodeStatus.Verified, Hp8340BCommands.Find(code)!.Status);
    }

    [Theory]
    // Every one of these needs checking against Tables 3-1/3-2 of the OPERATING manual, which
    // is not on this machine yet. Each must fail loudly until then.
    [InlineData("FA")]
    [InlineData("FB")]
    [InlineData("CF")]
    [InlineData("DF")]
    [InlineData("ST")]
    [InlineData("S1")]
    [InlineData("S3")]
    [InlineData("KZ")]
    [InlineData("HZ")]
    public void UnverifiedCodesRefuseToBeSent(string code)
    {
        var found = Hp8340BCommands.Find(code);
        Assert.NotNull(found);
        Assert.Equal(CodeStatus.Unverified, found!.Status);

        var ex = Assert.Throws<InvalidOperationException>(() => Hp8340BCommands.Require(code));
        Assert.Contains("Unverified", ex.Message);
    }

    [Fact]
    public void UnknownCodeIsRefusedAsAFabrication()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Hp8340BCommands.Require("ZZ"));
        Assert.Contains("never fabricate", ex.Message);
    }

    [Fact]
    public void EveryVerifiedCodeCitesASource()
    {
        foreach (var code in Hp8340BCommands.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(code.Source),
                $"Code '{code.Code}' has no source citation.");
        }
    }
}
