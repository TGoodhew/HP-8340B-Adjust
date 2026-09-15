namespace HP8340B.Instruments;

/// <summary>
/// HP 8673B command table. Like the DUT, the 8673B predates IEEE 488.2: there is no
/// <c>*IDN?</c> and no <c>*OPC?</c>, and settling is detected by serial-polling the status byte.
///
/// <para>Source: <b>HP 8673B Synthesized Signal Generator Operating and Service manual</b>
/// (`8673B User.pdf` in the local manual library), Section III. The authority for the code list
/// is <b>Table 3-6, HP-IB Program Codes, pp. 3-43 to 3-44</b>; the syntax and read-back
/// behaviour of each code come from the Detailed Operating Instructions cited per entry.</para>
///
/// <para>Repo rule 2 applies here as it does to the DUT and the analyzer: nothing is sent that is
/// not cited. Table 3-6 is printed in two columns and OCRs imperfectly — the <c>DM</c> entry, for
/// instance, comes out as "dB" with the "m" lost — so every code below is additionally confirmed
/// against prose in the Detailed Operating Instructions rather than taken from the table alone.
/// </para>
/// </summary>
public static class Hp8673BCommands
{
    private const string Table = "HP 8673B Operating manual, Table 3-6 HP-IB Program Codes, pp. 3-43/3-44";

    /// <summary>Every code the project sends to the 8673B.</summary>
    public static readonly IReadOnlyList<HpibCode> All =
    [
        new("IP",   "INSTRUMENT PRESET", CodeStatus.Verified, $"{Table}."),
        new("CS",   "Clear Status", CodeStatus.Verified,
            $"{Table}. Detailed Operating Instructions, Status Byte and Polling, p. 3-135: "
            + "\"To read the current instrument extended status, the program string \"CSOS\" "
            + "should be sent to clear both status bytes and to update the extended status byte.\""),
        new("OS",   "Output Status", CodeStatus.Verified,
            $"{Table}. Section 3-33 Sending the Data Message, p. 3-39: after OS the generator "
            + "\"sends two binary bytes, each 8 bits wide. The first byte is identical to the "
            + "Status Byte of the Serial Poll. The second byte is the Extended Status Byte\". "
            + "Table of data-message formats, p. 3-38: \"Output Status  OS  2 Bytes [EOI]\"."),

        // --- Frequency -------------------------------------------------------------------------
        new("FR",   "FREQUENCY (CW)", CodeStatus.Verified,
            $"{Table}. Frequency (CW), p. 3-71: \"the program code FR or CW is sent followed by "
            + "the desired frequency and the units (GZ, MZ, KZ, or HZ)\". Manual's own example "
            + "strings: \"FR16.232334GZ\", \"FR16232.334MZ\", \"FR16232334KZ\", "
            + "\"FR16232334000HZ\"."),
        new("CW",   "CW Frequency", CodeStatus.Verified,
            $"{Table}. Frequency (CW), p. 3-71: same as FR, and additionally \"turns off sweep if "
            + "any sweep mode is active\"."),
        new("OK",   "Output Lock Frequency", CodeStatus.Verified,
            $"{Table}. Sending the Data Message, p. 3-38: \"the Signal Generator sends the value "
            + "of the frequency at which it is currently phase locked... FR [Numeric Value] HZ "
            + "[LF and EOI]\". Frequency (CW), p. 3-71 warns this must not be used while sweeping."),
        new("GZ",   "GHz", CodeStatus.Verified, $"{Table}. Frequency (CW), p. 3-71 example."),
        new("MZ",   "MHz", CodeStatus.Verified, $"{Table}. Frequency (CW), p. 3-71 example."),
        new("KZ",   "kHz", CodeStatus.Verified, $"{Table}. Frequency (CW), p. 3-71 example."),
        new("HZ",   "Hz", CodeStatus.Verified, $"{Table}. Frequency (CW), p. 3-71 example."),

        // --- Output level ----------------------------------------------------------------------
        new("LE",   "RF Output Level", CodeStatus.Verified,
            $"{Table}. Range (Output Level), p. 3-117: \"The RF output level can be programmed "
            + "directly using the program code LE, AP, or PL.\" The same page marks LE the "
            + "*preferred* code and says the generator \"will always send the program code LE when "
            + "the RF output level is read as a string\". Manual's own example: \"LE-56DM\"."),
        new("DM",   "dBm (units terminator)", CodeStatus.Verified,
            $"{Table} (OCRs as \"dB\" — the \"m\" is lost in the scan). Range (Output Level), "
            + "p. 3-117 is unambiguous: \"The units terminator for the output level is dBm which "
            + "corresponds to the program code DM.\" Example string \"LE-56DM\"."),
        new("OA",   "Output Active Parameter (read-back suffix)", CodeStatus.Verified,
            $"{Table}. Range (Output Level), p. 3-117: \"To read the RANGE setting, send the "
            + "program codes RAOA and then read the RANGE setting.\" Frequency (CW), p. 3-71: "
            + "\"send the program codes FROA and then read the frequency\"."),

        // --- RF on/off -------------------------------------------------------------------------
        new("RF1",  "RF ON", CodeStatus.Verified,
            $"{Table}. RF Output On/Off, p. 3-123: \"The program code to turn the RF output on is "
            + "RFl or Rl.\" Note it starts an Auto Peak, so a settle wait must follow."),
        new("RF0",  "RF OFF", CodeStatus.Verified,
            $"{Table}. RF Output On/Off, p. 3-123: \"The program code to turn the RF output off is "
            + "RF0 or RO.\""),

        // --- ALC -------------------------------------------------------------------------------
        new("C1",   "ALC INTERNAL", CodeStatus.Verified,
            $"{Table}. Also Range (Output Level), p. 3-117 step 1: \"Press the ALC INT key to "
            + "place the Signal Generator into internal ALC mode.\""),
        new("C2",   "ALC DIODE", CodeStatus.Verified, $"{Table}."),
        new("C3",   "ALC POWER METER", CodeStatus.Verified, $"{Table}."),

        // --- Auto peak -------------------------------------------------------------------------
        // Section 3-13, p. 3-6: "Major power and pulse modulation specifications are not warranted
        // unless an AUTO PEAK operation has been performed."
        new("K0",   "AUTO PEAK OFF", CodeStatus.Verified, $"{Table}."),
        new("K1",   "AUTO PEAK ON", CodeStatus.Verified,
            $"{Table}. Section 3-13, p. 3-6: power specifications are not warranted without an "
            + "AUTO PEAK, which happens automatically on a frequency change above 50 MHz while "
            + "AUTO PEAK is enabled."),
    ];

    private static readonly Dictionary<string, HpibCode> Index =
        All.ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);

    /// <summary>Looks a code up, or null if the project has never heard of it.</summary>
    public static HpibCode? Find(string code) =>
        Index.TryGetValue(code, out var found) ? found : null;

    /// <summary>Codes still needing a check against the operating manual.</summary>
    public static IEnumerable<HpibCode> Unverified =>
        All.Where(c => c.Status == CodeStatus.Unverified);

    /// <summary>
    /// Returns <paramref name="code"/> if Verified; throws otherwise. Same discipline as the DUT
    /// and the analyzer.
    /// </summary>
    public static string Require(string code)
    {
        var found = Find(code)
            ?? throw new InvalidOperationException(
                $"8673B code '{code}' is not in the command table. Repo rule 2: never fabricate an "
                + "HP-IB code. Add it to Hp8673BCommands.All with its manual citation.");

        if (found.Status != CodeStatus.Verified)
            throw new InvalidOperationException(
                $"8673B code '{code}' ({found.FrontPanelKey}) is Unverified. {found.Source}");

        return found.Code;
    }
}
