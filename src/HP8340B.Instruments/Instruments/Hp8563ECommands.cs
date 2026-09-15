namespace HP8340B.Instruments;

/// <summary>
/// HP 8563E command table. The 8563E is part of the 8560 E-series and uses the <b>8560-series
/// command language, not SCPI</b>.
///
/// <para>Source: <b>HP/Agilent 8560 E-Series Programming Guide</b> (`8560E Programming Guide.pdf`
/// in the local manual library, 716 pages, text layer). Page numbers below are the guide's own.
/// A subset is additionally hardware-proven on this bench through GpibMcp's instrument database,
/// which cites the same guide.</para>
///
/// <para>Repo rule 2 applies here as it does to the DUT: nothing is sent that is not cited.</para>
/// </summary>
public static class Hp8563ECommands
{
    private const string Guide = "HP/Agilent 8560 E-Series Programming Guide";
    private const string Bench = " Hardware-proven on this bench via GpibMcp's 8563E database.";

    /// <summary>Every code the project sends to the analyzer.</summary>
    public static readonly IReadOnlyList<HpibCode> All =
    [
        new("IP",    "INSTR PRESET", CodeStatus.Verified, $"{Guide}.{Bench}"),
        new("ID",    "identify (ID?)", CodeStatus.Verified, $"{Guide}.{Bench}"),
        new("FREF",  "10 MHz EXT INT", CodeStatus.Verified,
            $"{Guide}, p. 477 (FREF Frequency Reference). Parameters INT and EXT; query form "
            + "FREF? returns which is selected. \"An external reference must be 10 MHz (+/-100 Hz) "
            + "at a minimum amplitude of 0 dBm... When the external mode is selected, an \"X\" "
            + "appears on the left edge of the display.\" Preset state is Internal."),

        // Frequency.
        new("CF",    "CENTER FREQ", CodeStatus.Verified, $"{Guide}.{Bench}"),
        new("SP",    "SPAN", CodeStatus.Verified, $"{Guide}.{Bench}"),
        new("FA",    "START FREQ", CodeStatus.Verified, $"{Guide}.{Bench}"),
        new("FB",    "STOP FREQ", CodeStatus.Verified, $"{Guide}.{Bench}"),
        new("SS",    "CF step size", CodeStatus.Verified, $"{Guide}.{Bench}"),

        // Amplitude.
        new("RL",    "REFERENCE LEVEL", CodeStatus.Verified, $"{Guide}.{Bench}"),
        new("AT",    "INPUT ATTENUATION", CodeStatus.Verified, $"{Guide}, p. 424 (AT Input Attenuation)."),

        // Bandwidth and sweep.
        new("RB",    "RESOLUTION BW", CodeStatus.Verified, $"{Guide}.{Bench}"),
        new("VB",    "VIDEO BW", CodeStatus.Verified, $"{Guide}.{Bench}"),
        new("ST",    "SWEEP TIME", CodeStatus.Verified, $"{Guide}.{Bench}"),
        new("TS",    "TAKE SWEEP", CodeStatus.Verified, $"{Guide}.{Bench}"),
        new("SNGLS", "SINGLE SWEEP", CodeStatus.Verified, $"{Guide}, p. 594 (SNGLS Single Sweep).{Bench}"),
        new("CONTS", "CONTINUOUS SWEEP", CodeStatus.Verified, $"{Guide}, p. 443 (CONTS Continuous Sweep).{Bench}"),
        new("DONE",  "operation complete", CodeStatus.Verified, $"{Guide}.{Bench}"),

        // Detector.
        new("DET",   "DETECTION MODE", CodeStatus.Verified,
            $"{Guide}, p. 453 (DET Detection Modes). Parameters NEG, NRM, POS, SMP."),

        // Traces.
        new("MXMH",  "MAX HOLD", CodeStatus.Verified,
            $"{Guide}, p. 543 (MXMH Maximum Hold). Takes a trace: MXMH TRA. Guide's own example: "
            + "\"BLANK TRA;CLRW TRB;MXMH TRB;\". Employs the positive peak detector."),
        new("CLRW",  "CLEAR WRITE", CodeStatus.Verified,
            $"{Guide}, p. 440 (CLRW Clear Write). Takes a trace: CLRW TRA."),
        new("BLANK", "BLANK TRACE", CodeStatus.Verified, $"{Guide}, same example as MXMH."),
        new("VIEW",  "VIEW TRACE", CodeStatus.Verified, $"{Guide}, p. 638 example \"IP;TDF P;VIEW TRA;\"."),
        new("TRA",   "TRACE A data", CodeStatus.Verified,
            $"{Guide}, p. 638 (TRA/TRB Trace Data Input/Output). Query form TRA?."),
        new("TDF",   "TRACE DATA FORMAT", CodeStatus.Verified,
            $"{Guide}, p. 630 (TDF Trace Data Format). \"TDF P\" returns real values in the "
            + "fundamental units of Table 5-1 — dBm for amplitude — rather than measurement units. "
            + "Guide's own example: \"TDF P;TRA?\"."),

        // Markers.
        new("MKPK",  "MARKER PEAK SEARCH", CodeStatus.Verified,
            $"{Guide}. \"MKPK HI\" finds the highest peak.{Bench}"),
        new("MKF",   "MARKER FREQUENCY", CodeStatus.Verified, $"{Guide}.{Bench}"),
        new("MKA",   "MARKER AMPLITUDE", CodeStatus.Verified, $"{Guide}.{Bench}"),
        new("MKCF",  "MARKER TO CENTER FREQ", CodeStatus.Verified, $"{Guide}.{Bench}"),

        // Screen capture. The analyzer plots itself to the bus as HP-GL, which GpibMcp already
        // uses for its 8563E capture and 7090A forwarding workflows.
        new("PLOT",  "PLOT (HP-GL output)", CodeStatus.Verified,
            $"{Guide}. GpibMcp's 8563E database uses \"PLOT 550,279,9750,7479;\" with "
            + "\"SNGLS;TS;\" before and \"CONTS;\" after — hardware-proven on this bench."),
    ];

    private static readonly Dictionary<string, HpibCode> Index =
        All.ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);

    /// <summary>Looks a code up, or null if the project has never heard of it.</summary>
    public static HpibCode? Find(string code) =>
        Index.TryGetValue(code, out var found) ? found : null;

    /// <summary>Codes still needing a check against the programming guide.</summary>
    public static IEnumerable<HpibCode> Unverified =>
        All.Where(c => c.Status == CodeStatus.Unverified);

    /// <summary>
    /// Returns <paramref name="code"/> if Verified; throws otherwise. Same discipline as the
    /// DUT's table — an invented analyzer code produces a plausible wrong measurement, which is
    /// the failure mode this project exists to avoid.
    /// </summary>
    public static string Require(string code)
    {
        var found = Find(code)
            ?? throw new InvalidOperationException(
                $"8563E code '{code}' is not in the command table. Repo rule 2: never fabricate an "
                + "HP-IB code. Add it to Hp8563ECommands.All with its programming-guide citation.");

        if (found.Status != CodeStatus.Verified)
            throw new InvalidOperationException(
                $"8563E code '{code}' ({found.FrontPanelKey}) is Unverified. {found.Source}");

        return found.Code;
    }
}
