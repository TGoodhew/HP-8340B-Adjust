using HP8340B.Instruments.Model;
using HP8340B.Reports;
using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// M1-02: the session. A year from now the only way to know whether a number was taken through an
/// 18 GHz leg at 22 GHz, on a borrowed sensor, or before the oven was warm, is because this said
/// so. None of it is reconstructable afterwards.
/// </summary>
public class SessionTests
{
    private static Session New() => new()
    {
        Purpose = "5-14 unleveled RF output",
        Operator = "Tony",
        DutSerial = "2530A01234",
    };

    private static SessionMeasurement Measurement(
        string what = "max leveled power",
        TraceabilityClass traceability = TraceabilityClass.Spec,
        string settings = "437B, 8485A, cal factor 92.0%, 16 averages",
        string? leg = null) =>
        new(what, 24e9, 3.2, "dBm", traceability, "HP 437B", settings, leg, DateTime.UtcNow);

    // --- Rule 3, enforced rather than trusted -------------------------------------------------

    [Fact]
    public void AMeasurementWithNoSettingsIsRefused()
    {
        // The moment to notice is when it is taken, not a year later when somebody asks what the
        // resolution bandwidth was.
        var session = New();

        var ex = Assert.Throws<ArgumentException>(
            () => session.Record(Measurement(settings: "")));

        Assert.Contains("rule 3", ex.Message);
        Assert.Empty(session.Measurements);
    }

    [Fact]
    public void AMeasurementWithNoInstrumentIsRefused()
    {
        var session = New();

        Assert.Throws<ArgumentException>(() => session.Record(
            Measurement() with { Instrument = "" }));
    }

    [Fact]
    public void AProperlyRecordedMeasurementIsKept()
    {
        var session = New();
        session.Record(Measurement());

        Assert.Single(session.Measurements);
        Assert.Equal(TraceabilityClass.Spec, session.Measurements[0].Traceability);
    }

    [Fact]
    public void TheRoutedLegIsCarriedBecauseItCapsTheClaim()
    {
        // The specific thing the issue names: knowing a number came through an 18 GHz leg at
        // 22 GHz is only possible if the leg was written down at the time.
        var session = New();
        session.Record(Measurement(leg: "SR-D"));

        Assert.Equal("SR-D", session.Measurements[0].RoutedLeg);
        Assert.Contains("SR-D", session.RenderMarkdown());
    }

    // --- Rule 1 ------------------------------------------------------------------------------------

    [Fact]
    public void AWriteWithNoConfirmationIsRefused()
    {
        var session = New();

        var ex = Assert.Throws<ArgumentException>(() => session.RecordWrite(
            new SessionWrite("CC73", "1024", "1031", ConfirmedBy: "", DateTime.UtcNow)));

        Assert.Contains("rule 1", ex.Message);
        Assert.Empty(session.Writes);
    }

    [Fact]
    public void AWriteCarriesTheOldValueAndTheNewOne()
    {
        var session = New();
        session.RecordWrite(new SessionWrite("CC73", "1024", "1031", "Tony", DateTime.UtcNow));

        var markdown = session.RenderMarkdown();

        Assert.Contains("CC73", markdown);
        Assert.Contains("1024", markdown);
        Assert.Contains("1031", markdown);
        Assert.Contains("Tony", markdown);
    }

    [Fact]
    public void AnUnreadableOldValueIsStatedRatherThanOmitted()
    {
        // Rule 1 wants the old value. Where it genuinely could not be read that has to be a
        // deliberate statement, not a blank that reads as an oversight.
        var session = New();
        session.RecordWrite(new SessionWrite("learn string", null, "<123 bytes>", "Tony", DateTime.UtcNow));

        Assert.Contains("could not be read", session.RenderMarkdown());
    }

    // --- Borrowed kit, D-10 ------------------------------------------------------------------------

    [Fact]
    public void BorrowedKitWithNoSerialIsFlagged()
    {
        // A borrowed instrument's calibration history is not this bench's, so a result depending
        // on one cannot be traced without its serial.
        var session = New();
        session.Instruments.Add(new SessionInstrument(
            "usb-power-sensor", "Keysight U8485A", "USB0::INSTR", true, "Keysight,U8485A",
            null, Serial: null, Borrowed: true));

        Assert.Single(session.BorrowedWithoutSerial());
        Assert.Contains("NO serial recorded", session.RenderMarkdown());
    }

    [Fact]
    public void BorrowedKitWithASerialIsNotFlagged()
    {
        var session = New();
        session.Instruments.Add(new SessionInstrument(
            "usb-power-sensor", "Keysight U8485A", "USB0::INSTR", true, "Keysight,U8485A",
            null, Serial: "MY12345678", Borrowed: true));

        Assert.Empty(session.BorrowedWithoutSerial());
    }

    [Fact]
    public void OwnedKitWithNoSerialIsNotFlagged()
    {
        // This bench's own instruments have a known calibration history; the serial is nice to
        // have, not load-bearing.
        var session = New();
        session.Instruments.Add(new SessionInstrument(
            "power-meter", "HP 437B", "GPIB0::13::INSTR", true, "HEWLETT-PACKARD, 437B",
            null, Serial: null, Borrowed: false));

        Assert.Empty(session.BorrowedWithoutSerial());
    }

    // --- Reference state ---------------------------------------------------------------------------

    [Fact]
    public void AnInstrumentThatCannotReportItsReferenceIsListedAsACaveat()
    {
        // Not an error — the 5351A never can — but it belongs next to any frequency result.
        var session = New();
        session.Instruments.Add(new SessionInstrument(
            "counter", "HP 5351A", "GPIB0::14::INSTR", true, "status byte 0x08",
            ExternalReference: null, Serial: null, Borrowed: false));

        Assert.Single(session.ReferenceUnconfirmed());
        Assert.Contains("front-panel check", session.RenderMarkdown());
    }

    [Fact]
    public void AnInstrumentOnItsInternalReferenceIsShownInBold()
    {
        // It should be hard to skim past in a summary somebody reads a year later.
        var session = New();
        session.Instruments.Add(new SessionInstrument(
            "spectrum-analyzer", "HP 8563E", "GPIB0::18::INSTR", true, "HP8563E",
            ExternalReference: false, Serial: null, Borrowed: false));

        Assert.Contains("**INT**", session.RenderMarkdown());
    }

    [Fact]
    public void AnInstrumentThatDidNotRespondIsShownInBold()
    {
        var session = New();
        session.Instruments.Add(new SessionInstrument(
            "dmm", "HP 3458A", "GPIB0::22::INSTR", Responded: false, null, null, null, false));

        Assert.Contains("**no**", session.RenderMarkdown());
    }

    // --- Environment --------------------------------------------------------------------------------

    [Fact]
    public void AMissingWarmUpTimeIsSaidRatherThanLeftBlank()
    {
        var session = New();

        Assert.Contains("Warm-up elapsed: not recorded", session.RenderMarkdown());
        Assert.Contains("Ambient: not recorded", session.RenderMarkdown());
    }

    [Fact]
    public void WarmUpIsJudgedAgainstWhatTheManualAsksFor()
    {
        // Section IV specifies performance after a warm-up, so a result taken before it is a
        // result about a cold instrument.
        var cold = new SessionEnvironment(23.0, TimeSpan.FromMinutes(10), null);
        var warm = new SessionEnvironment(23.0, TimeSpan.FromHours(2), null);

        Assert.False(cold.WarmedUp(TimeSpan.FromHours(1)));
        Assert.True(warm.WarmedUp(TimeSpan.FromHours(1)));

        // Unrecorded is not warmed up.
        Assert.False(new SessionEnvironment(null, null, null).WarmedUp(TimeSpan.FromHours(1)));
    }

    // --- The backup gate ------------------------------------------------------------------------------

    [Fact]
    public void ASessionWithNoCalibrationBackupSaysSoProminently()
    {
        // M1-01: nothing that can write to the instrument may run until a backup has succeeded.
        // The summary has to make its absence obvious rather than just omitting the line.
        Assert.Contains("**Calibration backup:** NONE TAKEN", New().RenderMarkdown());
    }

    [Fact]
    public void ABackupIsNamedWhenThereIsOne()
    {
        var session = New();
        session.CalBackupFile = "cal-backup.json";

        Assert.Contains("cal-backup.json", session.RenderMarkdown());
        Assert.DoesNotContain("NONE TAKEN", session.RenderMarkdown());
    }

    // --- Writing the folder ------------------------------------------------------------------------------

    [Fact]
    public void TheSessionFolderCarriesBothTheRawDataAndAReadableSummary()
    {
        // The evidence should outlive the program that produced it: a JSON file nobody can open
        // is not evidence.
        var root = Path.Combine(Path.GetTempPath(), $"hp8340b-{Guid.NewGuid():N}");

        try
        {
            var session = New();
            session.Record(Measurement());

            var folder = session.Write(root);

            Assert.True(File.Exists(Path.Combine(folder, "session.json")));
            Assert.True(File.Exists(Path.Combine(folder, "summary.md")));

            var markdown = File.ReadAllText(Path.Combine(folder, "summary.md"));
            Assert.Contains("5-14 unleveled RF output", markdown);
            Assert.Contains("2530A01234", markdown);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TheFolderIsNamedByTheStartTimeSoRunsSortInOrder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hp8340b-{Guid.NewGuid():N}");

        try
        {
            var folder = New().Write(root);
            var name = Path.GetFileName(folder);

            Assert.Matches(@"^\d{4}-\d{2}-\d{2}-\d{6}$", name);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TheJsonRoundTripsSoTheRawDataIsUsable()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hp8340b-{Guid.NewGuid():N}");

        try
        {
            var session = New();
            session.Record(Measurement());
            session.RecordWrite(new SessionWrite("CC73", "1024", "1031", "Tony", DateTime.UtcNow));

            var folder = session.Write(root);
            var json = File.ReadAllText(Path.Combine(folder, "session.json"));

            // Traceability is written by name, not as an integer, so the file is readable on its
            // own terms.
            Assert.Contains("\"Spec\"", json);
            Assert.Contains("CC73", json);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
