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
    /// Believed correct but not yet confirmed. <see cref="Hp8340BCommands.Require"/> throws on
    /// these, so nothing unverified can reach the instrument until someone checks it.
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
/// The 8340B HP-IB code table, from <b>Table 3-2, HP 8340B/41B Programming Codes</b>, Operating
/// Manual Section III, pp. 3-59 to 3-62.
///
/// <para>Codes are front-panel key mnemonics concatenated. Lower case is upshifted and spaces are
/// ignored, so <c>IP CW 2.3 GZ PL -30 DB</c> and <c>ipcw2.3gzpl-30db</c> are the same command.</para>
///
/// <para><b>How these were verified.</b> Table 3-2 must be read with <c>pdftotext -raw</c>, not
/// <c>-layout</c>: in layout mode the Code and Operation columns drift by a row wherever a cell
/// wraps, which silently mis-pairs every entry after it. In raw mode the two columns come out as
/// equal-length lists (49 and 49 on page 2 of 4) that pair correctly. Three independent checks
/// confirm the raw pairing:</para>
/// <list type="bullet">
/// <item>The prose following the table cites codes directly, e.g. "[SHIFT] [ENTRY OFF] (HP-IB:
/// SHEF)" and "[SHIFT] [PEAK] (HP-IB: SHRP)".</item>
/// <item>The OM (output mode data) parameter list independently names parameters 40, 41 and 42 as
/// I/O channel [SHIFT][GHz], I/O subchannel [SHIFT][MHz] and I/O write [SHIFT][kHz].</item>
/// <item>The SH-prefixed codes track their unprefixed forms exactly: A1/A2/A3 are INT/XTAL/METER,
/// so SHA1/SHA2/SHA3 are SHIFT plus those.</item>
/// </list>
///
/// <para>Data suffixes in the manual's own notation: <c>d</c> decimal data, <c>m</c> a 1 or 0
/// suffix (1 turns a function on), <c>n</c> a single digit register number, <c>t</c> a required
/// terminator. Terminators double as unit scalars: GZ, MZ, KZ, HZ, DB, SC and MS. A comma or an
/// ASCII LF also terminates, scaling to Hz, seconds or dB(m).</para>
/// </summary>
public static class Hp8340BCommands
{
    private const string T32 = "Operating manual Table 3-2, HP 8340B/41B Programming Codes, "
                               + "pp. 3-59 to 3-62 (read in raw column order).";

    /// <summary>Every code the project knows about, verified or not.</summary>
    public static readonly IReadOnlyList<HpibCode> All =
    [
        // --- Frequency ------------------------------------------------------------------
        new("CW",  "CW", CodeStatus.Verified,
            T32 + " Also the Section III example \"IP CW 2.3 GZ PL -30 DB\", and proven on this "
            + "bench in HP-Attenuator."),
        new("FA",  "START FREQ", CodeStatus.Verified, T32),
        new("FB",  "STOP FREQ", CodeStatus.Verified, T32),
        new("CF",  "CENTER FREQ", CodeStatus.Verified, T32),
        new("DF",  "DELTA FREQ", CodeStatus.Verified,
            T32 + " NOTE: this is delta frequency, NOT frequency span. The project brief was "
            + "unsure which; Table 3-2 settles it."),
        new("SF",  "STEP FREQ SIZE", CodeStatus.Verified, T32),
        new("BC",  "CHANGE FREQUENCY BAND", CodeStatus.Verified, T32),

        // --- Power ----------------------------------------------------------------------
        new("PL",  "POWER LEVEL", CodeStatus.Verified,
            T32 + " Also the Section III example \"PL -30 DB\", and proven on this bench."),
        new("PS1", "PWR SWP on", CodeStatus.Verified,
            T32 + " Prose: \"[PWR SWEEP] (HP-IB: PS1 turns on power sweep, PS0 turns off)\"."),
        new("PS0", "PWR SWP off", CodeStatus.Verified, T32 + " See PS1."),
        new("AT",  "ATTENUATOR", CodeStatus.Verified, T32),
        new("SL",  "POWER SLOPE", CodeStatus.Verified, T32),
        new("RF1", "RF ON", CodeStatus.Verified,
            T32 + " (listed as RFm, RF output on/off.) Proven on this bench in HP-Attenuator."),
        new("RF0", "RF OFF", CodeStatus.Verified, T32 + " See RF1."),

        // --- Leveling mode --------------------------------------------------------------
        // A1/A2/A3 are what 5-14 step 8 and 5-16 select. [XTAL] with nothing connected is
        // effectively unleveled, which is how the 5-14 SYTM tracking adjustments are made.
        new("A1",  "INT (leveling, internal)", CodeStatus.Verified, T32),
        new("A2",  "XTAL (leveling, external)", CodeStatus.Verified, T32),
        new("A3",  "METER (leveling, power meter)", CodeStatus.Verified, T32),

        // --- Sweep ----------------------------------------------------------------------
        new("S1",  "CONT (sweep, continuous)", CodeStatus.Verified, T32),
        new("S2",  "SINGLE (sweep, single)", CodeStatus.Verified,
            T32 + " Also service manual 4-17 p. 4-83: \"S2 (Single sweep)\"."),
        new("S3",  "MANUAL (sweep, manual)", CodeStatus.Verified, T32),
        new("ST",  "SWEEP TIME", CodeStatus.Verified, T32),
        new("TS",  "TAKE SWEEP", CodeStatus.Verified,
            T32 + " Also service manual 4-17 p. 4-83: \"two TS (Take Sweep) commands\"."),
        new("RS",  "RESET SWEEP", CodeStatus.Verified, T32),
        new("T1",  "FREE RUN (trigger, free run)", CodeStatus.Verified, T32),
        new("T2",  "LINE (trigger, line)", CodeStatus.Verified, T32),
        new("T3",  "EXT (trigger, external)", CodeStatus.Verified, T32),

        // --- Modulation -----------------------------------------------------------------
        new("AM1", "AM on", CodeStatus.Verified, T32 + " (listed as AMm.)"),
        new("AM0", "AM off", CodeStatus.Verified, T32 + " See AM1."),
        new("FM1", "FM on", CodeStatus.Verified, T32 + " (listed as FMm.)"),
        new("FM0", "FM off", CodeStatus.Verified, T32 + " See FM1."),
        new("PM1", "PULSE on", CodeStatus.Verified, T32 + " (listed as PMm.)"),
        new("PM0", "PULSE off", CodeStatus.Verified, T32 + " See PM1."),

        // --- State storage --------------------------------------------------------------
        // 5-14 steps 32-58 save three states per band: slow sweep, AUTO, single sweep.
        new("SV",  "SAVE (save instrument state)", CodeStatus.Verified,
            T32 + " Listed as SVn, where n is a single digit 1-9 giving the register."),
        new("RC",  "RECALL (recall instrument state)", CodeStatus.Verified,
            T32 + " Listed as RCn, where n is 0-9."),
        new("IL",  "INPUT LEARN DATA", CodeStatus.Verified,
            T32 + " Listed as IL 123b: 123 binary bytes follow."),
        new("OL",  "OUTPUT LEARN DATA", CodeStatus.Verified,
            T32 + " Listed as OL (123b): read back 123 binary bytes."),

        // --- Output / query -------------------------------------------------------------
        // The 8340B predates IEEE 488.2 and has no *IDN?, but OI is an identification query.
        new("OI",  "OUTPUT IDENTIFICATION", CodeStatus.Verified,
            T32 + " Listed as OI (19a): read back 19 ASCII characters."),
        new("OS",  "OUTPUT STATUS BYTES", CodeStatus.Verified,
            T32 + " Listed as OS (2b): read back BOTH status bytes as 2 binary bytes. This is how "
            + "extended status byte #2 is read - bit 6 RF unleveled, bit 3 external reference "
            + "selected (service manual Table 4-31, p. 4-85)."),
        new("OA",  "OUTPUT ACTIVE PARAMETER", CodeStatus.Verified, T32 + " Listed as OA (d)."),
        new("OM",  "OUTPUT MODE DATA", CodeStatus.Verified, T32 + " Listed as OM (8b)."),
        new("OP",  "OUTPUT INTERROGATED PARAMETER", CodeStatus.Verified, T32 + " Listed as OP (d)."),
        new("OR",  "OUTPUT POWER LEVEL", CodeStatus.Verified, T32 + " Listed as OR (d)."),
        new("OF",  "OUTPUT FAULT VALUES", CodeStatus.Verified, T32 + " Listed as OF (d)."),
        new("CS",  "CLEAR BOTH STATUS BYTES", CodeStatus.Verified, T32),
        new("RM",  "STATUS BYTE MASK", CodeStatus.Verified, T32 + " Listed as RM 1 b."),
        new("RE",  "EXTENDED STATUS BYTE MASK", CodeStatus.Verified, T32 + " Listed as RE 1 b."),

        // --- Instrument -----------------------------------------------------------------
        new("IP",  "INSTR PRESET", CodeStatus.Verified,
            T32 + " Also service manual 4-17 p. 4-83, and proven on this bench."),
        new("EF",  "ENTRY OFF (entry display off)", CodeStatus.Verified, T32),
        new("EK",  "ENABLE ROTARY KNOB", CodeStatus.Verified, T32),
        new("KR",  "KEYBOARD RELEASE", CodeStatus.Verified, T32),
        new("SH",  "SHIFT (shift prefix)", CodeStatus.Verified,
            T32 + " The prefix the guided procedures need: SH plus the unshifted code."),

        // --- Shifted functions the adjustment procedures use ----------------------------
        new("SHRP", "SHIFT PEAK (tracking calibration)", CodeStatus.Verified,
            T32 + " Prose, p. 3-39: \"[SHIFT] [PEAK] (HP-IB: SHRP) ... aligns all of the YTM "
            + "tracking calibration constants and requires 5-10 seconds to implement ... AUTO "
            + "TRACKING will disappear after 5-10 seconds.\" This is 5-14 step 31."),
        new("RP1", "PEAK (RF peaking) on", CodeStatus.Verified,
            T32 + " (listed as RPm.) Peaks the present CW frequency only; SHRP is the full "
            + "tracking calibration."),
        new("RP0", "PEAK off", CodeStatus.Verified, T32 + " See RP1."),
        new("SHA3", "SHIFT METER (access linear modulator)", CodeStatus.Verified,
            T32 + " Cross-confirmed by the OM parameter list, item 37 \"Bypassed ALC ([SHIFT] "
            + "[METER])\". This is the ALC bypass used by 5-16 step 1 and by 5-14 step 55's "
            + "\"[SHIFT][METER] -50 dBm\"."),
        new("SHPS", "SHIFT PWR SWP (decouple ATN, ALC)", CodeStatus.Verified,
            T32 + " Cross-confirmed by the OM parameter list, item 35 \"Decoupled ATN/ALC "
            + "([SHIFT] [PWR SWP])\". Used throughout 5-16."),
        new("SHCF", "SHIFT CF (set frequency step size)", CodeStatus.Verified, T32),
        new("SHPL", "SHIFT POWER LEVEL (set power level step)", CodeStatus.Verified, T32),
        new("SHAK", "SHIFT AMTD MKR (immediate YTM peak)", CodeStatus.Verified,
            T32 + " Fine alignment only, faster than PEAK but with less range (prose p. 3-39)."),
        new("SHS3", "SHIFT MANUAL (fault diagnostic)", CodeStatus.Verified,
            T32 + " Prose p. 3-29: displays \"FAULT: CAL KICK ADC PEAK TRK\"."),

        // --- Calibration-constant I/O ---------------------------------------------------
        // DANGER: these reach the protected calibration. Repo rule 1 - no write without an
        // explicit per-action confirmation. The codes are verified; the exact access SEQUENCE
        // is still M0-04's job, and must be worked out before anything writes.
        new("SHGZ", "SHIFT GHz (I/O channel)", CodeStatus.Verified,
            T32 + " Cross-confirmed by the OM parameter list, item 40 \"I/O channel ([SHIFT] "
            + "[GHz/dB(m)])\". Selects the cal constant in 5-14 step 2's key sequence."),
        new("SHMZ", "SHIFT MHz (I/O subchannel)", CodeStatus.Verified,
            T32 + " Cross-confirmed by the OM parameter list, item 41 \"I/O subchannel ([SHIFT] "
            + "[MHz/sec])\"."),
        new("SHKZ", "SHIFT kHz (I/O write)", CodeStatus.Verified,
            T32 + " Cross-confirmed by the OM parameter list, item 42 \"I/O write ([SHIFT] "
            + "[kHz/msec])\"."),
        new("SHHZ", "SHIFT Hz (read from I/O)", CodeStatus.Verified,
            T32 + " Table 3-2 only - the OM parameter list has no matching entry, so the READ "
            + "semantics must be confirmed on hardware before M0-04 relies on them. Very likely "
            + "the mechanism behind 08340-10009's \"display cal data\" utility."),
        new("SHEF", "SHIFT ENTRY OFF (restore cal constant access)", CodeStatus.Verified,
            T32 + " Prose: \"[SHIFT] [ENTRY OFF] (HP-IB: SHEF) recalls the Calibration Constant "
            + "Access Function ... used when one wishes to re-enter the calibration constant mode "
            + "after just exiting it.\" Matches 5-14 step 2's note."),

        // --- Terminators / unit scalars -------------------------------------------------
        new("GZ",  "GHz", CodeStatus.Verified, T32 + " Also the Section III example \"CW 2.3 GZ\"."),
        new("MZ",  "MHz", CodeStatus.Verified, T32 + " Proven on this bench in HP-Attenuator."),
        new("KZ",  "kHz", CodeStatus.Verified, T32),
        new("HZ",  "Hz", CodeStatus.Verified, T32),
        new("DB",  "dB(m)", CodeStatus.Verified, T32 + " Also the example \"PL -30 DB\"."),
        new("SC",  "seconds", CodeStatus.Verified, T32),
        new("MS",  "milliseconds", CodeStatus.Verified, T32),
    ];

    private static readonly Dictionary<string, HpibCode> Index =
        All.ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);

    /// <summary>Looks a code up, or null if the project has never heard of it.</summary>
    public static HpibCode? Find(string code) =>
        Index.TryGetValue(code, out var found) ? found : null;

    /// <summary>Codes still needing a check against a printed source.</summary>
    public static IEnumerable<HpibCode> Unverified =>
        All.Where(c => c.Status == CodeStatus.Unverified);

    /// <summary>
    /// Returns <paramref name="code"/> if it is Verified. Throws otherwise - an unknown code is
    /// a fabrication (rule 2) and an Unverified one must be checked before it reaches an
    /// instrument. Every command builder runs its mnemonics through this.
    /// </summary>
    public static string Require(string code)
    {
        var found = Find(code)
            ?? throw new InvalidOperationException(
                $"HP-IB code '{code}' is not in the 8340B code table. Repo rule 2: never fabricate "
                + "an HP-IB code. Add it to Hp8340BCommands.All with its Table 3-2 citation first.");

        if (found.Status != CodeStatus.Verified)
            throw new InvalidOperationException(
                $"HP-IB code '{code}' ({found.FrontPanelKey}) is Unverified and must not be sent. "
                + $"{found.Source}");

        return found.Code;
    }
}
