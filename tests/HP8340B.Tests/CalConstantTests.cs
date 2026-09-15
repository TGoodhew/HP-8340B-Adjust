using HP8340B.Instruments;
using HP8340B.Instruments.Visa;
using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// M0-04: calibration-constant access. The most dangerous code in the project — the
/// store-to-protected sequence overwrites the instrument's protected calibration, and the printed
/// card under the top cover is the only remaining copy if it is lost.
///
/// These tests are mostly about what the code REFUSES to do.
/// </summary>
public class CalConstantTests
{
    private static (CalConstants Cal, SimulatedInstrumentLink Link) NewCal(string readsBack = "100")
    {
        var link = new SimulatedInstrumentLink("SIM::dut::INSTR", _ => readsBack)
        {
            StatusByte = (byte)StatusByte1.RfSettled,
        };
        return (new CalConstants(new Hp8340B(link)), link);
    }

    [Fact]
    public void ReadSendsTheManualsAccessSequence()
    {
        // 5-14 step 2, after INSTR PRESET:
        //   [SHIFT][GHz] n [Hz], [SHIFT][MHz] 1 2 [Hz], [SHIFT][kHz] 2 2 [Hz]
        var (cal, link) = NewCal();
        cal.Read(2, afterPreset: true);

        Assert.Equal("SHGZ 2 HZ", link.History[0]);
        Assert.Equal("SHMZ 12 HZ", link.History[1]);
        Assert.Equal("SHKZ 22 HZ", link.History[2]);
        Assert.Equal("SHHZ", link.History[3]);
    }

    [Fact]
    public void SubsequentReadsUseTheShortFormWithShef()
    {
        // The manual's note: after the first access, [SHIFT][GHz] n [Hz] [SHIFT][ENTRY OFF].
        var (cal, link) = NewCal();
        cal.Read(7);

        Assert.Equal("SHGZ 7 HZ", link.History[0]);
        Assert.Equal("SHEF", link.History[1]);
        Assert.DoesNotContain("SHKZ 22 HZ", link.History);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(-1)]
    public void ConstantNumberIsRangeChecked(int constant)
    {
        var (cal, _) = NewCal();
        Assert.Throws<ArgumentOutOfRangeException>(() => cal.Read(constant));
    }

    [Fact]
    public void ReadAllCoversCc1ToCc99AndWritesNothing()
    {
        var (cal, link) = NewCal();
        var all = cal.ReadAll();

        Assert.Equal(99, all.Count);
        Assert.Equal(1, all[0].Number);
        Assert.Equal(99, all[^1].Number);

        // Selecting and reading is all it may do. Nothing that sets a value.
        Assert.All(link.History, sent =>
            Assert.True(
                sent.StartsWith("SHGZ") || sent.StartsWith("SHMZ") || sent.StartsWith("SHKZ")
                || sent == "SHEF" || sent == "SHHZ",
                $"ReadAll sent '{sent}', which is not part of reading."));
    }

    [Fact]
    public void ImplausibleValuesAreFlaggedRatherThanTrusted()
    {
        // CC2-CC8 are delay compensation, range 0-131 per 5-14 step 2. The SHHZ read path is not
        // hardware-confirmed, so a value outside that more likely means the read is wrong.
        var (cal, _) = NewCal(readsBack: "9999");
        Assert.False(cal.Read(5).Plausible);

        var (ok, _) = NewCal(readsBack: "100");
        Assert.True(ok.Read(5).Plausible);
    }

    [Fact]
    public void NonNumericReadFailsLoudlyAndPointsAtTheCaveat()
    {
        var (cal, _) = NewCal(readsBack: "???");
        var ex = Assert.Throws<InvalidOperationException>(() => cal.Read(2));

        Assert.Contains("not yet hardware-confirmed", ex.Message);
    }

    [Fact]
    public void WriteRefusesAgainstASimulatedInstrument()
    {
        // A simulated write would look like success in a log and prove nothing.
        var (cal, _) = NewCal();
        var auth = new CalWriteAuthorization(5, 100, 110, "Tony", "5-14 step 3 preset");

        var ex = Assert.Throws<InvalidOperationException>(() => cal.Write(auth));
        Assert.Contains("SIMULATED", ex.Message);
    }

    [Fact]
    public void WriteRequiresAConfirmingParty()
    {
        var (cal, _) = NewCal();
        var auth = new CalWriteAuthorization(5, 100, 110, "  ", "no one confirmed this");

        var ex = Assert.Throws<ArgumentException>(() => cal.Write(auth));
        Assert.Contains("who confirmed it", ex.Message);
    }

    [Fact]
    public void AuthorizationNamesOneConstantSoLoopingIsNotExpressible()
    {
        // Rule 1 says never in a background loop. The authorisation is a value naming a single
        // constant and its old value, so "confirm once, write many" cannot be written down.
        var auth = new CalWriteAuthorization(73, 1024, 1030, "Tony", "SYTM tracking, band 3");

        Assert.Equal(73, auth.Constant);
        Assert.Equal(1024, auth.OldValue);
        Assert.Equal(1030, auth.NewValue);

        // Reusing it for a different constant means constructing a different authorisation.
        var other = auth with { Constant = 74 };
        Assert.NotEqual(auth.Constant, other.Constant);
    }

    [Fact]
    public void DryRunDescribesTheWriteWithoutTouchingTheInstrument()
    {
        var (cal, link) = NewCal();
        var auth = new CalWriteAuthorization(73, 1024, 1030, "Tony", "SYTM tracking, band 3");

        var description = CalConstants.DescribeWrite(auth);

        Assert.Contains("DRY RUN", description);
        Assert.Contains("CC73", description);
        Assert.Contains("1024", description);
        Assert.Contains("1030", description);
        Assert.Empty(link.History);
    }

    [Fact]
    public void StoreToProtectedNeedsItsOwnExactAcknowledgement()
    {
        var (cal, _) = NewCal();

        foreach (var wrong in new[] { "yes", "OK", "confirm", "overwrite protected calibration" })
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                cal.StoreToProtectedMemory(new CalStoreAuthorization("Tony", "backup.json", wrong)));
            Assert.Contains(CalConstants.StoreAcknowledgement, ex.Message);
        }
    }

    [Fact]
    public void StoreToProtectedRefusesWithoutABackupThatActuallyExists()
    {
        var (cal, _) = NewCal();
        var auth = new CalStoreAuthorization(
            "Tony", "does-not-exist.json", CalConstants.StoreAcknowledgement);

        var ex = Assert.Throws<InvalidOperationException>(() => cal.StoreToProtectedMemory(auth));
        Assert.Contains("backup-cal", ex.Message);
    }

    [Fact]
    public void StoreToProtectedRefusesAgainstASimulatedInstrument()
    {
        var (cal, _) = NewCal();
        var backup = Path.GetTempFileName();

        try
        {
            var auth = new CalStoreAuthorization(
                "Tony", backup, CalConstants.StoreAcknowledgement);

            var ex = Assert.Throws<InvalidOperationException>(() => cal.StoreToProtectedMemory(auth));
            Assert.Contains("SIMULATED", ex.Message);
        }
        finally
        {
            File.Delete(backup);
        }
    }

    [Fact]
    public void PresetTableMatchesTheManual()
    {
        // 5-14 steps 2-3, confirmed against the manual in raw reading order. The layout-mode
        // extraction of that two-column table interleaves its rows and cannot be trusted.
        var p = CalConstants.Adjustment514Presets;

        foreach (var n in Enumerable.Range(2, 7)) Assert.Equal(100, p[n]);       // CC2-CC8
        foreach (var n in Enumerable.Range(9, 4)) Assert.Equal(1024, p[n]);      // CC9-CC12
        foreach (var n in Enumerable.Range(50, 4)) Assert.Equal(0, p[n]);        // CC50-CC53
        foreach (var n in Enumerable.Range(71, 4)) Assert.Equal(1024, p[n]);     // CC71-CC74

        Assert.Equal(25, p[75]);
        Assert.Equal(1000, p[76]);
        Assert.Equal(-25, p[77]);
        Assert.Equal(25, p[78]);
        Assert.Equal(0, p[80]);

        // CC1 is NOT preset here: 5-14 step 68 sets it to 500 for the DRP adjustment and back to
        // 50 afterwards, which is a different step with different values.
        Assert.False(p.ContainsKey(1));
    }

    [Fact]
    public void EveryPresetValueIsWithinItsKnownRange()
    {
        // Cross-check: the delay-compensation presets must sit inside the 0-131 the manual states,
        // and the band-switch presets inside -25 to +25. A transcription slip would break this.
        var p = CalConstants.Adjustment514Presets;

        foreach (var n in Enumerable.Range(2, 7))
            Assert.InRange(p[n], 0, 131);

        Assert.InRange(p[77], -25, 25);
        Assert.InRange(p[78], -25, 25);
        Assert.InRange(p[75], 0, 500);
    }
}
