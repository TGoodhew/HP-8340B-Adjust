using System.Globalization;
using HP8340B.Instruments.Visa;

namespace HP8340B.Instruments;

/// <summary>
/// 8673B status byte #1 — the one a serial poll returns. Operating manual, Figure 3-11,
/// "Status Byte Information", p. 3-42.
/// </summary>
[Flags]
public enum SourceStatusByte1 : byte
{
    None = 0,
    /// <summary>Bit 0 (1): front panel key pressed.</summary>
    FrontPanelKeyPressed = 1,
    /// <summary>Bit 1 (2): front panel entry complete.</summary>
    FrontPanelEntryComplete = 2,
    /// <summary>Bit 2 (4): change in the extended status byte.</summary>
    ExtendedStatusChanged = 4,
    /// <summary>
    /// Bit 3 (8): source settled. The 8673B's stand-in for *OPC? — the manual's own BASIC example
    /// for RF ON polls exactly this bit (<c>IF NOT BIT(V,3) THEN GOTO Wait_settle</c>, p. 3-124).
    /// </summary>
    SourceSettled = 8,
    /// <summary>Bit 4 (16): end of sweep.</summary>
    EndOfSweep = 16,
    /// <summary>Bit 5 (32): entry error — an invalid keystroke or program command.</summary>
    EntryError = 32,
    /// <summary>Bit 6 (64): request service (RQS).</summary>
    RequestService = 64,
    /// <summary>Bit 7 (128): change in sweep parameters.</summary>
    SweepParametersChanged = 128,
}

/// <summary>
/// 8673B extended status byte #2, readable only through <c>OS</c>. Operating manual, Figure 3-11
/// p. 3-42, with the per-bit descriptions on pp. 3-135 to 3-137.
/// </summary>
[Flags]
public enum SourceStatusByte2 : byte
{
    None = 0,
    /// <summary>Bit 0 (1): self test failed at turn-on.</summary>
    SelfTestFailed = 1,
    /// <summary>Bit 1 (2): FM overmodulated.</summary>
    FmOvermodulated = 2,
    // Bit 2 (4) is always zero — "BIT 3 This bit is always set to zero", p. 3-136.
    /// <summary>
    /// Bit 3 (8): external reference. Set when the rear-panel FREQ STANDARD INT/EXT switch is in
    /// EXT, and the front-panel EXT REF annunciator is lit.
    ///
    /// <para><b>On its own this only says the switch is set, not that a reference is present.</b>
    /// See <see cref="Hp8673B.ReadReferenceState"/>.</para>
    /// </summary>
    ExternalReference = 8,
    /// <summary>
    /// Bit 4 (16): not phase locked — the front-panel ɸ UNLOCKED annunciator. Set on malfunction,
    /// severe FM overmodulation, <b>the switch in EXT with no external reference</b>, or RF off.
    /// Not valid after a frequency change until <see cref="SourceStatusByte1.SourceSettled"/> is
    /// set (p. 3-136).
    /// </summary>
    NotPhaseLocked = 16,
    /// <summary>Bit 5 (32): power failure/on — mains was interrupted and restored.</summary>
    PowerFailure = 32,
    /// <summary>
    /// Bit 6 (64): ALC unleveled. Also how the manual says to detect AM overmodulation
    /// (p. 3-46).
    /// </summary>
    AlcUnleveled = 64,
    // Bit 7 (128) is always zero.
}

/// <summary>Which detector the 8673B's levelling loop uses. Table 3-6 codes C1/C2/C3.</summary>
public enum SourceAlcMode
{
    /// <summary>ALC INTERNAL — <c>C1</c>. The normal mode, and what the level specs assume.</summary>
    Internal,
    /// <summary>ALC DIODE — <c>C2</c>. External diode detector.</summary>
    Diode,
    /// <summary>ALC POWER METER — <c>C3</c>. External power meter.</summary>
    PowerMeter,
}

/// <summary>
/// What <see cref="Hp8673B.ReadReferenceState"/> found. Two bits have to agree before the
/// generator can be said to be running on the house reference.
/// </summary>
/// <param name="SwitchInExternal">Extended status bit 3: the INT/EXT switch is in EXT.</param>
/// <param name="PhaseLocked">Extended status bit 4 inverted: the generator is phase locked.</param>
public sealed record SourceReferenceState(bool SwitchInExternal, bool PhaseLocked)
{
    /// <summary>
    /// True only when the switch is in EXT <b>and</b> the generator is locked — the switch alone
    /// is set even when the external reference is missing entirely.
    /// </summary>
    public bool LockedToExternalReference => SwitchInExternal && PhaseLocked;

    /// <summary>A sentence for <c>probe</c> output and session records.</summary>
    public string Describe() => (SwitchInExternal, PhaseLocked) switch
    {
        (true, true) => "FREQ STANDARD switch in EXT and phase locked.",
        (true, false) => "FREQ STANDARD switch in EXT but NOT PHASE LOCKED — the external "
                         + "reference is missing, out of tolerance or too low in level.",
        (false, true) => "FREQ STANDARD switch in INT: running on the generator's own crystal, "
                         + "not the house reference.",
        (false, false) => "FREQ STANDARD switch in INT and NOT PHASE LOCKED — check the "
                          + "generator before using it.",
    };
}

/// <summary>
/// Driver for the HP 8673B Synthesized Signal Generator, 2.0 to 26.0 GHz.
///
/// <para>Source: <b>HP 8673B Operating and Service manual</b> (`8673B User.pdf` in the local
/// manual library), Section III. Codes come from <see cref="Hp8673BCommands"/>, each cited to
/// Table 3-6 and to the Detailed Operating Instruction that gives its syntax.</para>
///
/// <para>Role in this project: CW frequency and level only. It is the LO for the 11793A in setup
/// S6, and the optional LO for phase-noise down-conversion above the E4438C's frequency limit.
/// It also stands in for the second 8340B that Table 4-2 of the service manual calls for.</para>
///
/// <para><b>Why the reference check matters more than anything else here.</b> When this
/// generator is the LO for a phase-noise measurement, its own phase noise sits directly on the
/// result. If it is running on its internal crystal while the DUT is on the Z3805A, 4-9 measures
/// the difference between two oscillators rather than the DUT. Unlike the 5351A, the 8673B does
/// report its reference state over the bus, so <see cref="Probe"/> can check it rather than
/// asking somebody to look at the front panel — see <see cref="ReadReferenceState"/>.</para>
/// </summary>
public sealed class Hp8673B : IInstrument
{
    /// <summary>Specified frequency range, manual Table 1-1 p. 1-5: 2.0 to 26.0 GHz.</summary>
    public const double MinSpecifiedHz = 2.0e9;
    public const double MaxSpecifiedHz = 26.0e9;

    /// <summary>Overrange the instrument will still accept, same table: 1.95 to 26.5 GHz.</summary>
    public const double MinOverrangeHz = 1.95e9;
    public const double MaxOverrangeHz = 26.5e9;

    /// <summary>
    /// Level range the generator accepts, Range (Output Level) p. 3-117: "The Signal Generator
    /// accepts any RF output level between -101.9 and +13 dBm." Accepting is not the same as
    /// levelling — see <see cref="MaxLeveledDbm"/>.
    /// </summary>
    public const double MinLevelDbm = -101.9;
    public const double MaxLevelDbm = 13.0;

    private readonly IInstrumentLink _link;

    public string Role { get; }
    public string Model => "HP 8673B";
    public IInstrumentLink Link => _link;

    public Hp8673B(IInstrumentLink link, string role = "lo-source")
    {
        _link = link ?? throw new ArgumentNullException(nameof(link));
        Role = role;
    }

    private static string Num(double v, string format = "0.#########") =>
        v.ToString(format, CultureInfo.InvariantCulture);

    private void Send(string command) => _link.Write(command);

    // --- Setup ---------------------------------------------------------------------------------

    /// <summary>
    /// Device clear then <c>IP</c> instrument preset. Per Table 3-3 (Message Reference Table,
    /// p. 3-34) a Clear message "Sets output to 3000.000 MHz at -70 dBm with sweep and modulation
    /// off", which is a safe state to hand to anything on this bench.
    /// </summary>
    public void Initialize()
    {
        _link.Clear();
        Send(Hp8673BCommands.Require("IP"));
    }

    /// <summary>
    /// Sets the CW frequency. Sent as <c>FR{value}HZ</c> in fundamental units so there is no
    /// decimal-point ambiguity between GZ/MZ/KZ.
    ///
    /// <para>Above 6.6 GHz the generator rounds to its 2, 3 or 4 kHz resolution; read
    /// <see cref="ReadOutputFrequencyHz"/> back if the rounded value matters.</para>
    /// </summary>
    public void SetCwFrequencyHz(double hz)
    {
        GuardFrequency(hz);
        Send($"{Hp8673BCommands.Require("FR")}{Num(hz, "0")}{Hp8673BCommands.Require("HZ")}");
    }

    /// <summary>Convenience wrapper for the GHz figures the procedures are written in.</summary>
    public void SetCwFrequencyGHz(double ghz) => SetCwFrequencyHz(ghz * 1e9);

    /// <summary>
    /// Refuses a frequency the generator cannot produce. The overrange limits are used rather
    /// than the specified ones: 1.95-26.5 GHz is still settable, just not warranted, and the
    /// traceability class already records that distinction.
    /// </summary>
    public static void GuardFrequency(double hz)
    {
        if (hz is < MinOverrangeHz or > MaxOverrangeHz)
            throw new ArgumentOutOfRangeException(
                nameof(hz), hz,
                $"The 8673B covers {MinSpecifiedHz / 1e9:0.##}-{MaxSpecifiedHz / 1e9:0.##} GHz "
                + $"({MinOverrangeHz / 1e9:0.##}-{MaxOverrangeHz / 1e9:0.##} GHz overrange). It "
                + "cannot reach the 8340B's low band — use the E4438C below 2 GHz.");
    }

    /// <summary>
    /// Sets the RF output level, <c>LE{value}DM</c>. LE is the manual's preferred code over AP
    /// and PL, and DM (dBm) its preferred terminator.
    /// </summary>
    public void SetOutputLevelDbm(double dbm)
    {
        if (dbm is < MinLevelDbm or > MaxLevelDbm)
            throw new ArgumentOutOfRangeException(
                nameof(dbm), dbm,
                $"The 8673B accepts {MinLevelDbm:0.#} to {MaxLevelDbm:+0.#} dBm.");

        Send($"{Hp8673BCommands.Require("LE")}{Num(dbm, "0.0")}{Hp8673BCommands.Require("DM")}");
    }

    /// <summary>
    /// Maximum <b>leveled</b> output for a standard-option 8673B at <paramref name="hz"/>,
    /// manual Table 1-1 p. 1-7: +8 dBm to 18 GHz, +4 dBm to 22 GHz, 0 dBm to 26 GHz.
    ///
    /// <para>The generator accepts settings above this and simply lights UNLEVELED, so this is
    /// the figure to check a request against rather than <see cref="MaxLevelDbm"/>. Options 001,
    /// 004, 005 and 008 shift these; the option of this unit is not recorded yet, so the standard
    /// figures are used and a real run should confirm with <see cref="ReadExtendedStatus"/>.</para>
    /// </summary>
    public static double MaxLeveledDbm(double hz) => hz switch
    {
        <= 18.0e9 => 8.0,
        <= 22.0e9 => 4.0,
        _ => 0.0,
    };

    /// <summary>Turns the RF output on (<c>RF1</c>) or off (<c>RF0</c>).</summary>
    /// <remarks>
    /// Turning the output on starts an Auto Peak, so the caller should
    /// <see cref="WaitSettled"/> before measuring. With the output off, both UNLEVELED and
    /// ɸ UNLOCKED light and <see cref="SourceStatusByte2.NotPhaseLocked"/> is set — expected, not
    /// a fault (p. 3-123).
    /// </remarks>
    public void SetRfOutput(bool on) =>
        Send(Hp8673BCommands.Require(on ? "RF1" : "RF0"));

    /// <summary>Selects the levelling loop's detector.</summary>
    public void SetAlcMode(SourceAlcMode mode) => Send(Hp8673BCommands.Require(mode switch
    {
        SourceAlcMode.Internal => "C1",
        SourceAlcMode.Diode => "C2",
        SourceAlcMode.PowerMeter => "C3",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    }));

    /// <summary>
    /// Enables or disables AUTO PEAK. Section 3-13 p. 3-6: "Major power and pulse modulation
    /// specifications are not warranted unless an AUTO PEAK operation has been performed", so
    /// this stays on for any measurement whose level matters.
    /// </summary>
    public void SetAutoPeak(bool on) => Send(Hp8673BCommands.Require(on ? "K1" : "K0"));

    /// <summary>
    /// Sets the generator up as a CW local oscillator: internal ALC, auto peak on, frequency,
    /// level, RF on, then a settle wait. The order matters — level and frequency are set before
    /// the output is enabled so nothing downstream sees an unintended level first.
    /// </summary>
    public bool ConfigureCw(double hz, double dbm, TimeSpan settleTimeout)
    {
        SetRfOutput(false);
        SetAlcMode(SourceAlcMode.Internal);
        SetAutoPeak(true);
        SetCwFrequencyHz(hz);
        SetOutputLevelDbm(dbm);
        SetRfOutput(true);

        return WaitSettled(settleTimeout);
    }

    // --- Read-back -----------------------------------------------------------------------------

    /// <summary>
    /// Reads the programmed CW frequency in Hz — <c>FROA</c>. This is the requested frequency,
    /// not the rounded one the generator is actually producing; see
    /// <see cref="ReadOutputFrequencyHz"/> for that.
    /// </summary>
    /// <remarks>
    /// The manual notes the response is prefixed with <c>CF</c> rather than <c>FR</c>
    /// (p. 3-71), which is why the parser strips whatever letters it finds rather than expecting
    /// a particular prefix.
    /// </remarks>
    public double ReadCwFrequencyHz() =>
        ParseNumeric(
            _link.Query($"{Hp8673BCommands.Require("FR")}{Hp8673BCommands.Require("OA")}"),
            "CW frequency");

    /// <summary>
    /// Reads the frequency the generator is currently phase locked to — <c>OK</c>. This is the
    /// rounded, actual output frequency. The manual warns it is not correct during a sweep; this
    /// project only ever uses the 8673B in CW.
    /// </summary>
    public double ReadOutputFrequencyHz() =>
        ParseNumeric(_link.Query(Hp8673BCommands.Require("OK")), "output frequency");

    /// <summary>Reads the RF output level in dBm — <c>LEOA</c>.</summary>
    public double ReadOutputLevelDbm() =>
        ParseNumeric(
            _link.Query($"{Hp8673BCommands.Require("LE")}{Hp8673BCommands.Require("OA")}"),
            "output level");

    /// <summary>
    /// Strips the program-code prefix and units terminator the 8673B wraps its numbers in, e.g.
    /// "CF16232334000HZ" or "LE-56.0DM".
    /// </summary>
    internal static double ParseNumeric(string response, string what)
    {
        ArgumentNullException.ThrowIfNull(response);

        var span = response.Trim();
        var start = 0;
        while (start < span.Length && !char.IsDigit(span[start]) && span[start] is not ('-' or '+' or '.'))
            start++;

        var end = start;
        while (end < span.Length && (char.IsDigit(span[end]) || span[end] is '-' or '+' or '.' or 'e' or 'E'))
            end++;

        var number = span[start..end];

        if (number.Length > 0
            && double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return value;

        throw new InvalidOperationException(
            $"8673B returned '{response}' for {what}, which contains no number. The generator is a "
            + "talker addressed after a read-back code; an empty response usually means the code "
            + "was rejected.");
    }

    // --- Status ---------------------------------------------------------------------------------

    /// <summary>
    /// Reads both status bytes with <c>CSOS</c> and returns them.
    ///
    /// <para><c>CS</c> first is the manual's own instruction (p. 3-135): the extended status bits
    /// stay set until read, so without clearing first, a condition that has since gone away still
    /// reads as present. <b>This also clears status byte #1</b>, including a latched
    /// <see cref="SourceStatusByte1.SourceSettled"/> — which is why
    /// <see cref="WaitSettled"/> serial-polls rather than using this.</para>
    /// </summary>
    public (SourceStatusByte1 Primary, SourceStatusByte2 Extended) ReadExtendedStatus()
    {
        Send($"{Hp8673BCommands.Require("CS")}{Hp8673BCommands.Require("OS")}");
        var data = _link.ReadBytes(2);

        if (data.Length != 2)
            throw new InvalidOperationException(
                $"Expected 2 status bytes from the 8673B's OS, got {data.Length}.");

        return ((SourceStatusByte1)data[0], (SourceStatusByte2)data[1]);
    }

    /// <summary>
    /// Reads the reference state as the two bits that together mean something.
    ///
    /// <para>Bit 3 alone says only that the rear-panel switch is in EXT. The manual (p. 3-136) is
    /// explicit that NOT PHASE LOCKED is what is set when the switch is in EXT "with no external
    /// frequency reference", so both bits are needed before claiming the generator is running on
    /// the house 10 MHz.</para>
    ///
    /// <para><b>Even this is not airtight.</b> Paragraph 3-11 warns that an external reference
    /// whose level is below the specified 0.1 to 1 Vrms "may be sufficient to turn off the ɸ
    /// UNLOCKED status annunciator, giving a false indication of normal operation. In fact, the
    /// phase noise of the Signal Generator may be degraded." A phase-noise run that depends on
    /// this LO should still have the reference level confirmed once at the rear panel.</para>
    /// </summary>
    public SourceReferenceState ReadReferenceState()
    {
        var (_, extended) = ReadExtendedStatus();

        return new SourceReferenceState(
            SwitchInExternal: extended.HasFlag(SourceStatusByte2.ExternalReference),
            PhaseLocked: !extended.HasFlag(SourceStatusByte2.NotPhaseLocked));
    }

    /// <summary>True if the ALC is unleveled — extended status bit 6.</summary>
    public bool IsUnleveled() =>
        ReadExtendedStatus().Extended.HasFlag(SourceStatusByte2.AlcUnleveled);

    /// <summary>
    /// Waits for <see cref="SourceStatusByte1.SourceSettled"/> by serial poll — the 8673B has no
    /// *OPC?. Returns false on timeout rather than throwing.
    ///
    /// <para>Aborts immediately on <see cref="SourceStatusByte1.EntryError"/>: a rejected command
    /// never settles, and waiting out the timeout would report a slow instrument instead of a bad
    /// command.</para>
    /// </summary>
    public bool WaitSettled(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        StatusPoller.WaitFor(
            _link,
            isReady: s => ((SourceStatusByte1)s).HasFlag(SourceStatusByte1.SourceSettled),
            timeout: timeout,
            abort: s => ((SourceStatusByte1)s).HasFlag(SourceStatusByte1.EntryError),
            abortMessage: _ =>
                "The 8673B reported an entry error (status byte #1 bit 5) while settling. The last "
                + $"command sent was: {_link.History.LastOrDefault() ?? "(none)"}",
            cancellationToken: cancellationToken) is not null;

    // --- Probe -----------------------------------------------------------------------------------

    public ProbeResult Probe()
    {
        try
        {
            // Pre-488.2: no *IDN?, and unlike the DUT's OI there is no identification code in
            // Table 3-6 either. A serial poll is the only non-intrusive proof of life.
            var status = (SourceStatusByte1)_link.SerialPoll();
            var reference = ReadReferenceState();

            var detail = reference.Describe();

            if (!reference.LockedToExternalReference)
                detail += " As the LO for a phase-noise measurement its own noise lands on the "
                          + "result, so it must share the Z3805A with the DUT.";

            return new ProbeResult(
                Role, Model, _link.ResourceName,
                Responded: true,
                Identity: $"status byte 0x{(byte)status:X2} (pre-488.2: no *IDN?, and no "
                          + "identification code in Table 3-6)",
                ExternalReference: reference.LockedToExternalReference,
                Detail: detail,
                ReferenceApplies: true);
        }
        catch (Exception ex)
        {
            return new ProbeResult(Role, Model, _link.ResourceName, Responded: false, Detail: ex.Message);
        }
    }

    public void Dispose() => _link.Dispose();
}
