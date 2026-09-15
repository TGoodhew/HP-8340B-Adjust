using HP8340B.Instruments;
using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// Repo rule 2: never fabricate an HP-IB code. These tests document where every code came from
/// and prove that an unknown one is refused.
///
/// Source for all of them: Table 3-2, HP 8340B/41B Programming Codes, Operating Manual Section
/// III, pp. 3-59 to 3-62, read in raw column order. See the remarks on Hp8340BCommands for why
/// layout-mode extraction of that table is unsafe.
/// </summary>
public class HpibCodeTests
{
    [Theory]
    // Frequency and power.
    [InlineData("CW", "CW")]
    [InlineData("FA", "START FREQ")]
    [InlineData("FB", "STOP FREQ")]
    [InlineData("CF", "CENTER FREQ")]
    [InlineData("PL", "POWER LEVEL")]
    // Delta frequency, NOT span - the project brief was unsure and Table 3-2 settles it.
    [InlineData("DF", "DELTA FREQ")]
    // Leveling mode. A2 ([XTAL]) with nothing connected is how 5-14 runs unleveled.
    [InlineData("A1", "INT (leveling, internal)")]
    [InlineData("A2", "XTAL (leveling, external)")]
    [InlineData("A3", "METER (leveling, power meter)")]
    // Sweep.
    [InlineData("S1", "CONT (sweep, continuous)")]
    [InlineData("S2", "SINGLE (sweep, single)")]
    [InlineData("S3", "MANUAL (sweep, manual)")]
    [InlineData("ST", "SWEEP TIME")]
    [InlineData("TS", "TAKE SWEEP")]
    // State storage for 5-14's slow/AUTO/single register trio, and the learn string.
    [InlineData("SV", "SAVE (save instrument state)")]
    [InlineData("RC", "RECALL (recall instrument state)")]
    [InlineData("IL", "INPUT LEARN DATA")]
    [InlineData("OL", "OUTPUT LEARN DATA")]
    // Terminators and unit scalars.
    [InlineData("GZ", "GHz")]
    [InlineData("MZ", "MHz")]
    [InlineData("KZ", "kHz")]
    [InlineData("HZ", "Hz")]
    [InlineData("DB", "dB(m)")]
    [InlineData("SC", "seconds")]
    [InlineData("MS", "milliseconds")]
    public void VerifiedCodeIsSendableAndNamesItsKey(string code, string key)
    {
        Assert.Equal(code, Hp8340BCommands.Require(code));

        var found = Hp8340BCommands.Find(code)!;
        Assert.Equal(CodeStatus.Verified, found.Status);
        Assert.Equal(key, found.FrontPanelKey);
    }

    [Fact]
    public void OsReadsBothStatusBytes()
    {
        // Table 3-2 lists OS (2b). This is how extended status byte #2 is read - bit 6 RF
        // unleveled (M1-05 finds max leveled power with it) and bit 3 external reference
        // selected (probe confirms the DUT is on the Z3805A with it).
        var os = Hp8340BCommands.Find("OS")!;
        Assert.Equal(CodeStatus.Verified, os.Status);
        Assert.Contains("BOTH status bytes", os.Source);
    }

    [Fact]
    public void ShrpIsTheTrackingCalibrationOf514Step31()
    {
        // Prose, operating manual p. 3-39: "[SHIFT] [PEAK] (HP-IB: SHRP) ... aligns all of the
        // YTM tracking calibration constants and requires 5-10 seconds to implement."
        var shrp = Hp8340BCommands.Find("SHRP")!;
        Assert.Equal(CodeStatus.Verified, shrp.Status);
        Assert.Contains("SHRP", shrp.Source);
        Assert.Contains("5-14 step 31", shrp.Source);

        // RP is only a single-frequency peak, not the full tracking calibration. Keeping the two
        // apart matters: sending RP where SHRP was meant would silently do far less.
        Assert.NotEqual(shrp.Code, Hp8340BCommands.Find("RP1")!.Code);
    }

    [Theory]
    // The calibration-constant I/O codes, cross-confirmed by the OM parameter list items 40, 41
    // and 42. These reach the protected calibration, so rule 1 applies to every write.
    [InlineData("SHGZ", "I/O channel")]
    [InlineData("SHMZ", "I/O subchannel")]
    [InlineData("SHKZ", "I/O write")]
    [InlineData("SHHZ", "read from I/O")]
    [InlineData("SHEF", "restore cal constant access")]
    public void CalConstantIoCodesAreVerified(string code, string expectedInKey)
    {
        var found = Hp8340BCommands.Find(code)!;
        Assert.Equal(CodeStatus.Verified, found.Status);
        Assert.Contains(expectedInKey, found.FrontPanelKey, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShhzReadSemanticsAreFlaggedForHardwareConfirmation()
    {
        // SHHZ appears in Table 3-2 but has no entry in the OM parameter list, unlike its three
        // siblings. M0-04 must confirm the read semantics on hardware before relying on them.
        Assert.Contains("confirmed on hardware", Hp8340BCommands.Find("SHHZ")!.Source);
    }

    [Fact]
    public void UnknownCodeIsRefusedAsAFabrication()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Hp8340BCommands.Require("ZZ"));
        Assert.Contains("never fabricate", ex.Message);
    }

    [Fact]
    public void AnyUnverifiedCodeRefusesToBeSent()
    {
        // Table 3-2 verified everything the project currently knows about, so this list is empty
        // today. The guard still has to work for whatever gets added next.
        foreach (var code in Hp8340BCommands.Unverified)
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => Hp8340BCommands.Require(code.Code));
            Assert.Contains("Unverified", ex.Message);
        }
    }

    [Fact]
    public void EveryCodeCitesASource()
    {
        foreach (var code in Hp8340BCommands.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(code.Source),
                $"Code '{code.Code}' has no source citation.");
        }
    }

    [Fact]
    public void CodeTableHasNoDuplicates()
    {
        var duplicates = Hp8340BCommands.All
            .GroupBy(c => c.Code, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(duplicates);
    }
}
