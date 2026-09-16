namespace HP8340B.Instruments;

/// <summary>
/// One 437B HP-IB code, with the extra thing its manual makes a point of.
/// </summary>
/// <param name="Code">The mnemonic.</param>
/// <param name="FrontPanelKey">What it does.</param>
/// <param name="Status">Verified or not.</param>
/// <param name="NeedsEnter">
/// True for codes footnoted 1: "requires a numeric entry followed by the program code EN".
/// </param>
/// <param name="CompatibleWith438A">
/// True for codes footnoted 4. The manual warns that "use of the other available HP 437B HP-IB
/// command codes may inhibit the operation of an HP 438A", so this is recorded per code rather
/// than assumed — a 438A-compatible subset is not the same thing as the 437B's command set.
/// </param>
/// <param name="Source">Citation.</param>
public sealed record PowerMeterCode(
    string Code,
    string FrontPanelKey,
    CodeStatus Status,
    bool NeedsEnter,
    bool CompatibleWith438A,
    string Source);

/// <summary>
/// HP 437B command table.
///
/// <para>Source: <b>HP 437B Power Meter Operating Manual</b> (`437B-UM.pdf` in the local manual
/// library), <b>Table 3-5, HP-IB Codes Summary, pp. 3-41 and 3-42</b>. The table's four footnotes
/// carry real information and two of them are modelled here: footnote 1 marks codes needing a
/// numeric entry terminated by <c>EN</c>, and footnote 4 marks the codes that are 438A
/// compatible.</para>
///
/// <para>Repo rule 2, as for the DUT and the analyzer: nothing is sent that is not cited.</para>
/// </summary>
public static class Hp437BCommands
{
    private const string Table = "HP 437B Operating Manual, Table 3-5 HP-IB Codes Summary, pp. 3-41/3-42";

    /// <summary>Every code the project sends to the 437B.</summary>
    public static readonly IReadOnlyList<PowerMeterCode> All =
    [
        new("PR", "PRESET", CodeStatus.Verified, false, true, $"{Table}."),
        new("ID", "identification query", CodeStatus.Verified, false, false,
            $"{Table}. Response format, from the HP-IB Syntax summary p. 3-40: "
            + "\"HEWLETT-PACKARD, 437B,, X.X.\". Note it is NOT footnoted 4, so it is one of the "
            + "codes that can inhibit a 438A."),
        new("ERR?", "device error query", CodeStatus.Verified, false, true, $"{Table}."),
        new("SM", "status message", CodeStatus.Verified, false, true,
            $"{Table}. Output format p. 3-40: 26 ASCII characters."),

        // Zero and calibrate.
        new("ZE", "ZERO", CodeStatus.Verified, false, true,
            $"{Table}. Section 3-9: connect the sensor to POWER REF, ZERO, then CAL."),
        new("CL", "CAL", CodeStatus.Verified, true, true,
            $"{Table}. Section 3-9 steps 3-5: press CAL, \"Key the REF CAL FACTOR of the power "
            + "sensor into the power meter\", press ENTER."),
        new("OC0", "reference oscillator off", CodeStatus.Verified, false, true,
            $"{Table}. PRESET leaves the reference oscillator Off (section 3-10 table), which is "
            + "what makes the manual's zero-then-cal order safe."),
        new("OC1", "reference oscillator on", CodeStatus.Verified, false, true, $"{Table}."),

        // Sensor and frequency.
        new("KB", "CAL FAC", CodeStatus.Verified, true, true, $"{Table}."),
        new("FR", "FREQ", CodeStatus.Verified, true, false,
            $"{Table}. NOT 438A compatible — no footnote 4."),
        new("SE", "SENSOR", CodeStatus.Verified, true, false, $"{Table}. NOT 438A compatible."),

        // Units and display.
        new("LG", "log display (dBm)", CodeStatus.Verified, false, true, $"{Table}."),
        new("LN", "linear display (watts)", CodeStatus.Verified, false, true, $"{Table}."),
        new("GZ", "GHz", CodeStatus.Verified, false, false, $"{Table}."),
        new("MZ", "MHz", CodeStatus.Verified, false, false, $"{Table}."),
        new("KZ", "kHz", CodeStatus.Verified, false, false, $"{Table}."),
        new("HZ", "Hz", CodeStatus.Verified, false, false, $"{Table}."),
        new("PCT", "percent", CodeStatus.Verified, false, false,
            $"{Table}: \"Percent (can terminate DUTY CYCLE, CAL FAC, and REF CF)\"."),
        new("EN", "ENTER", CodeStatus.Verified, false, true, $"{Table}."),

        // Ranging.
        new("RA", "AUTO RNG", CodeStatus.Verified, false, true, $"{Table}."),
        new("RH", "range hold", CodeStatus.Verified, false, true, $"{Table}."),
        new("RM", "SET RANGE", CodeStatus.Verified, true, true, $"{Table}."),
        new("RE", "RESOLN", CodeStatus.Verified, true, false, $"{Table}. NOT 438A compatible."),

        // Filtering — the averaging that sets the noise floor.
        new("FA", "automatic filter selection", CodeStatus.Verified, false, true, $"{Table}."),
        new("FH", "filter hold", CodeStatus.Verified, false, true, $"{Table}."),
        new("FM", "manual filter selection", CodeStatus.Verified, true, true, $"{Table}."),

        // Triggering.
        new("TR0", "trigger hold", CodeStatus.Verified, false, true, $"{Table}."),
        new("TR1", "trigger immediate", CodeStatus.Verified, false, true, $"{Table}."),
        new("TR2", "trigger with delay", CodeStatus.Verified, false, true, $"{Table}."),
        new("TR3", "trigger free run", CodeStatus.Verified, false, true, $"{Table}."),
    ];

    private static readonly Dictionary<string, PowerMeterCode> Index =
        All.ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);

    public static PowerMeterCode? Find(string code) =>
        Index.TryGetValue(code, out var found) ? found : null;

    public static IEnumerable<PowerMeterCode> Unverified =>
        All.Where(c => c.Status == CodeStatus.Unverified);

    /// <summary>
    /// Codes this project uses that would inhibit a 438A. Kept visible because the manual makes a
    /// point of it, and because this bench may one day have a 438A on the same bus.
    /// </summary>
    public static IEnumerable<PowerMeterCode> NotCompatibleWith438A =>
        All.Where(c => !c.CompatibleWith438A);

    /// <summary>Returns <paramref name="code"/> if Verified; throws otherwise.</summary>
    public static string Require(string code)
    {
        var found = Find(code)
            ?? throw new InvalidOperationException(
                $"437B code '{code}' is not in the command table. Repo rule 2: never fabricate an "
                + "HP-IB code. Add it to Hp437BCommands.All with its Table 3-5 citation.");

        if (found.Status != CodeStatus.Verified)
            throw new InvalidOperationException(
                $"437B code '{code}' ({found.FrontPanelKey}) is Unverified. {found.Source}");

        return found.Code;
    }
}
