using HP8340B.Instruments.Model;
using HP8340B.Reports;
using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// M1-01: the calibration backup, and the guards on putting one back.
///
/// <para>This is the first block of every campaign. 5-14 step 69 overwrites the <b>protected</b>
/// copy of the calibration, so if that is lost and the working copy is wrong, the printed card
/// under the top cover is the only remaining source — and it does not carry the
/// delay-compensation constants an adjustment has moved.</para>
/// </summary>
public class CalBackupTests
{
    private static CalBackup Backup(
        string serial = "2530A01234",
        string? identity = "HP8340B SIMULATED",
        params (int Number, int Value)[] constants)
    {
        var backup = new CalBackup { DutSerial = serial, Identity = identity };

        foreach (var (number, value) in constants)
            backup.Constants.Add(new BackedUpConstant(number, value, Plausible: true));

        return backup;
    }

    // --- Diffing ---------------------------------------------------------------------------------

    [Fact]
    public void OnlyWhatChangedIsReported()
    {
        // A list of ninety-nine unchanged constants hides the three that moved.
        var before = Backup(constants: [(1, 50), (73, 1024), (74, 1024)]);
        var after = Backup(constants: [(1, 50), (73, 1031), (74, 1024)]);

        var diff = CalDiff.Compare(before, after);

        Assert.Single(diff);
        Assert.Equal(73, diff[0].Number);
        Assert.Equal(1024, diff[0].Before);
        Assert.Equal(1031, diff[0].After);
    }

    [Fact]
    public void ADiffSaysWhatTheConstantDoesWhereTheProjectKnows()
    {
        // Far more useful than a bare number: it names which adjustment moved it.
        var diff = CalDiff.Compare(
            Backup(constants: [(73, 1024)]),
            Backup(constants: [(73, 1031)]));

        Assert.Contains("SYTM tracking", diff[0].Purpose!);
        Assert.Contains("CC73", diff[0].Describe());
        Assert.Contains("5-14 step 20", diff[0].Describe());
    }

    [Fact]
    public void AConstantWithNoKnownPurposeStillDiffsCleanly()
    {
        var diff = CalDiff.Compare(
            Backup(constants: [(42, 100)]),
            Backup(constants: [(42, 101)]));

        Assert.Single(diff);
        Assert.Null(diff[0].Purpose);
        Assert.DoesNotContain("[", diff[0].Describe());
    }

    [Fact]
    public void AConstantThatAppearedOrVanishedIsShownRatherThanSkipped()
    {
        // A backup missing a constant the other has is a difference worth seeing — it usually
        // means one of the two reads did not complete.
        var diff = CalDiff.Compare(
            Backup(constants: [(1, 50)]),
            Backup(constants: [(1, 50), (2, 100)]));

        Assert.Single(diff);
        Assert.Null(diff[0].Before);
        Assert.Equal(100, diff[0].After);
        Assert.Contains("(absent)", diff[0].Describe());
    }

    [Fact]
    public void IdenticalBackupsDiffToNothing() =>
        Assert.Empty(CalDiff.Compare(
            Backup(constants: [(1, 50), (73, 1024)]),
            Backup(constants: [(1, 50), (73, 1024)])));

    [Fact]
    public void TheLearnStringIsComparedAsOneBlob()
    {
        // It either matches or it does not; there is nothing useful to say about which byte moved.
        var before = Backup();
        var after = Backup();

        before.LearnStringBase64 = Convert.ToBase64String(new byte[123]);
        after.LearnStringBase64 = Convert.ToBase64String(new byte[123]);

        Assert.False(CalDiff.LearnStringChanged(before, after));

        after.LearnStringBase64 = Convert.ToBase64String(Enumerable.Repeat((byte)1, 123).ToArray());
        Assert.True(CalDiff.LearnStringChanged(before, after));
    }

    // --- The restore guards, which are the point ---------------------------------------------------

    [Fact]
    public void ABackupFromAnotherInstrumentIsRefused()
    {
        // The guard that matters. Calibration constants are per-unit — they describe one
        // particular SYTM's tracking and delay — so restoring another instrument's produces
        // something that LOOKS calibrated and is not. That is worse than leaving it mis-adjusted.
        var backup = Backup(serial: "2530A01234");

        var ex = Assert.Throws<InvalidOperationException>(
            () => CalDiff.GuardRestore(backup, "2530A09999", null));

        Assert.Contains("do not transfer between instruments", ex.Message);
    }

    [Fact]
    public void ABackupWithNoSerialIsRefused()
    {
        // No serial means no way to tell which instrument it came from, which is the same risk
        // with none of the evidence.
        foreach (var serial in new[] { "", "TBD" })
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => CalDiff.GuardRestore(Backup(serial: serial), "2530A01234", null));

            Assert.Contains("no DUT serial recorded", ex.Message);
        }
    }

    [Fact]
    public void AMatchingSerialAndIdentityPasses() =>
        CalDiff.GuardRestore(Backup(), "2530A01234", "HP8340B SIMULATED");

    [Fact]
    public void AMatchingSerialWithAMismatchedIdentityIsRefused()
    {
        // Usually means one of the two was recorded wrongly, which is worth stopping for rather
        // than silently trusting the serial.
        var ex = Assert.Throws<InvalidOperationException>(
            () => CalDiff.GuardRestore(Backup(), "2530A01234", "HP8341B SOMETHING"));

        Assert.Contains("identities do not", ex.Message);
    }

    [Fact]
    public void ABackupWithImplausibleValuesIsRefused()
    {
        // The likeliest explanation is that the unconfirmed SHHZ read path returned nonsense when
        // the backup was taken, not that the instrument held nonsense. Restoring would write that
        // nonsense in.
        var backup = Backup();
        backup.Constants.Add(new BackedUpConstant(73, 99999, Plausible: false));

        var ex = Assert.Throws<InvalidOperationException>(
            () => CalDiff.GuardRestore(backup, "2530A01234", null));

        Assert.Contains("SHHZ", ex.Message);
        Assert.Contains("CC73=99999", ex.Message);
        Assert.False(backup.AllPlausible);
    }

    [Fact]
    public void ImplausibleValuesAreListedSoTheyCanBeLookedAt()
    {
        var backup = Backup();
        backup.Constants.Add(new BackedUpConstant(73, 99999, Plausible: false));
        backup.Constants.Add(new BackedUpConstant(1, 50, Plausible: true));

        Assert.Single(backup.Implausible());
        Assert.Equal(73, backup.Implausible()[0].Number);
    }

    // --- Round trip ----------------------------------------------------------------------------------

    [Fact]
    public void ABackupSurvivesBeingWrittenAndReadBack()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"hp8340b-cal-{Guid.NewGuid():N}");

        try
        {
            var backup = Backup(constants: [(1, 50), (73, 1024)]);
            backup.LearnStringBase64 = Convert.ToBase64String(Enumerable.Repeat((byte)0x5A, 123).ToArray());

            var path = backup.Write(folder);
            var restored = CalBackup.Read(path);

            Assert.Equal(backup.DutSerial, restored.DutSerial);
            Assert.Equal(backup.LearnStringBase64, restored.LearnStringBase64);
            Assert.Equal(2, restored.Constants.Count);

            // And the round-tripped copy diffs to nothing against the original.
            Assert.Empty(CalDiff.Compare(backup, restored));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void TheOptionIsRecordedByNameSoTheFileReadsOnItsOwnTerms()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"hp8340b-cal-{Guid.NewGuid():N}");

        try
        {
            var backup = Backup();
            backup.DutOption = InstrumentOption.Opt001;

            var json = File.ReadAllText(backup.Write(folder));

            Assert.Contains("\"Opt001\"", json);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    // --- Capture from the simulated DUT ------------------------------------------------------------------

    [Fact]
    public void CaptureReadsEveryConstantTheLearnStringAndTheIdentity()
    {
        var model = new HP8340B.Instruments.SimulatedSweeper();
        var link = model.CreateLink();

        // The simulated DUT answers reads with an empty string, so the cal-constant read path
        // throws rather than inventing values — which is the correct behaviour and is why this
        // asserts the refusal rather than a captured backup.
        var dut = new HP8340B.Instruments.Hp8340B(link);
        var constants = new HP8340B.Instruments.CalConstants(dut);

        var ex = Assert.Throws<InvalidOperationException>(() => CalBackup.Capture(
            dut, constants, InstrumentOption.Standard, "2530A01234"));

        Assert.Contains("SHHZ read semantics are inferred", ex.Message);
    }
}
