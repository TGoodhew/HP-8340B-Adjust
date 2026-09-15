namespace HP8340B.Instruments;

/// <summary>
/// How far an HP-IB code has been confirmed against a printed source.
/// Repo rule 2: never fabricate an HP-IB code.
/// </summary>
public enum CodeStatus
{
    /// <summary>Attested by a cited manual table/page, or proven on this bench.</summary>
    Verified,

    /// <summary>
    /// Believed correct but not yet confirmed against Tables 3-1/3-2 of the OPERATING manual.
    /// <see cref="Hp8340BCommands.Require"/> throws on these, so nothing unverified can reach
    /// the instrument until someone checks it and moves it to <see cref="Verified"/>.
    /// </summary>
    Unverified,
}

/// <summary>One HP-IB mnemonic and where it came from.</summary>
/// <param name="Code">The mnemonic as sent, e.g. "IP".</param>
/// <param name="FrontPanelKey">The front-panel key it stands for.</param>
/// <param name="Status">Verified or Unverified.</param>
/// <param name="Source">Citation for a verified code, or what still needs checking.</param>
public sealed record HpibCode(string Code, string FrontPanelKey, CodeStatus Status, string Source);

/// <summary>
/// The 8340B HP-IB code table. Codes are front-panel key mnemonics concatenated; lower case is
/// upshifted and spaces ignored, e.g. <c>IP CW 2.3 GZ PL -30 DB</c>.
///
/// The full code set lives in Tables 3-1/3-2 of the OPERATING manual, which is not yet on this
/// machine (see the "Source Section III" decision issue). Everything listed Unverified here must
/// be checked against those tables before use; <see cref="Require"/> enforces that at runtime and
/// a unit test asserts the table's shape.
/// </summary>
public static class Hp8340BCommands
{
    /// <summary>Every code the project knows about, verified or not.</summary>
    public static readonly IReadOnlyList<HpibCode> All =
    [
        // --- Verified ------------------------------------------------------------------
        new("IP",  "INSTR PRESET", CodeStatus.Verified,
            "Service manual 4-17 p. 4-83: \"the program outputs an IP (INSTR PRESET)\"; also the "
            + "Section III example \"IP CW 2.3 GZ PL -30 DB\". Proven on this bench in HP-Attenuator."),
        new("S2",  "SINGLE sweep", CodeStatus.Verified,
            "Service manual 4-17 p. 4-83: \"S2 (Single sweep)\"; program listing Table 4-32."),
        new("TS",  "TAKE SWEEP", CodeStatus.Verified,
            "Service manual 4-17 p. 4-83: \"two TS (Take Sweep) commands\"; program listing Table 4-32."),
        new("CW",  "CW", CodeStatus.Verified,
            "Section III example \"IP CW 2.3 GZ PL -30 DB\". Proven on this bench in HP-Attenuator."),
        new("PL",  "POWER LEVEL", CodeStatus.Verified,
            "Section III example \"IP CW 2.3 GZ PL -30 DB\". Proven on this bench in HP-Attenuator."),
        new("RF1", "RF ON", CodeStatus.Verified,  "Proven on this bench in HP-Attenuator (Hp8340B.cs)."),
        new("RF0", "RF OFF", CodeStatus.Verified, "Proven on this bench in HP-Attenuator (Hp8340B.cs)."),

        // --- Units (verified) ----------------------------------------------------------
        new("GZ",  "GHz",  CodeStatus.Verified, "Section III example \"CW 2.3 GZ\"."),
        new("MZ",  "MHz",  CodeStatus.Verified, "Proven on this bench in HP-Attenuator (Hp8340B.cs)."),
        new("DB",  "dB(m)", CodeStatus.Verified, "Section III example \"PL -30 DB\"."),

        // --- Unverified: believed correct, NOT yet checked against Tables 3-1/3-2 -------
        new("FA",  "START FREQ", CodeStatus.Unverified, "Check Table 3-2 of the operating manual."),
        new("FB",  "STOP FREQ",  CodeStatus.Unverified, "Check Table 3-2 of the operating manual."),
        new("CF",  "CENTER FREQ", CodeStatus.Unverified, "Check Table 3-2 of the operating manual."),
        new("DF",  "DELTA FREQ / FREQ SPAN", CodeStatus.Unverified,
            "Check Table 3-2; the mnemonic and whether it is span or delta both need confirming."),
        new("ST",  "SWEEP TIME", CodeStatus.Unverified,
            "Check Table 3-2, including the time-unit suffixes (SC/MS/US?)."),
        new("S1",  "CONTINUOUS sweep", CodeStatus.Unverified,
            "Check Table 3-2. S2 is confirmed as single sweep, so S1 is very likely continuous."),
        new("S3",  "MANUAL sweep", CodeStatus.Unverified, "Check Table 3-2."),
        new("KZ",  "kHz", CodeStatus.Unverified, "Check Table 3-1 unit terminators."),
        new("HZ",  "Hz",  CodeStatus.Unverified, "Check Table 3-1 unit terminators."),
    ];

    private static readonly Dictionary<string, HpibCode> Index =
        All.ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);

    /// <summary>Looks a code up, or null if the project has never heard of it.</summary>
    public static HpibCode? Find(string code) =>
        Index.TryGetValue(code, out var found) ? found : null;

    /// <summary>Codes still needing a Section III check.</summary>
    public static IEnumerable<HpibCode> Unverified =>
        All.Where(c => c.Status == CodeStatus.Unverified);

    /// <summary>
    /// Returns <paramref name="code"/> if it is Verified. Throws otherwise — an unknown code is
    /// a fabrication (rule 2) and an Unverified one must be checked before it reaches an
    /// instrument. Every command builder runs its mnemonics through this.
    /// </summary>
    public static string Require(string code)
    {
        var found = Find(code)
            ?? throw new InvalidOperationException(
                $"HP-IB code '{code}' is not in the 8340B code table. Repo rule 2: never fabricate "
                + "an HP-IB code. Add it to Hp8340BCommands.All with its Section III citation first.");

        if (found.Status != CodeStatus.Verified)
            throw new InvalidOperationException(
                $"HP-IB code '{code}' ({found.FrontPanelKey}) is Unverified and must not be sent. "
                + $"{found.Source} Then move it to CodeStatus.Verified with the citation.");

        return found.Code;
    }
}
