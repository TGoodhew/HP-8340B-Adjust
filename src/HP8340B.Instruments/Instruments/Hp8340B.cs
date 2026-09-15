using System.Globalization;
using HP8340B.Instruments.Visa;

namespace HP8340B.Instruments;

/// <summary>Sweep mode. Codes S1/S2/S3, Table 3-2.</summary>
public enum SweepMode
{
    /// <summary>Continuous — [CONT].</summary>
    Continuous,
    /// <summary>Single — [SINGLE].</summary>
    Single,
    /// <summary>Manual — [MANUAL].</summary>
    Manual,
}

/// <summary>Leveling source. Codes A1/A2/A3, Table 3-2.</summary>
public enum LevelingMode
{
    /// <summary>Internal — [INT].</summary>
    Internal,
    /// <summary>
    /// External crystal detector — [XTAL]. With nothing connected to the external-leveling input
    /// this is effectively UNLEVELED, which is how 5-14's SYTM tracking adjustments are run
    /// (step 8 selects [XTAL] with no detector attached).
    /// </summary>
    External,
    /// <summary>Power meter — [METER].</summary>
    PowerMeter,
}

/// <summary>
/// Driver for the HP 8340B Synthesized Sweeper — the DUT.
///
/// <para>Every write goes through <see cref="Hp8340BCommands.Require"/>, so an unverified or
/// invented code throws rather than reaching the instrument (repo rule 2).</para>
///
/// <para>The instrument predates IEEE 488.2: no <c>*IDN?</c>, no <c>*OPC?</c>. Identification is
/// <c>OI</c>, and settling is the status byte — see <see cref="WaitSettled"/> and
/// <see cref="StatusPoller"/>.</para>
///
/// <para><b>Nothing here writes a calibration constant.</b> Cal-constant access is M0-04 and is
/// guarded separately (repo rule 1).</para>
/// </summary>
public sealed class Hp8340B : IInstrument
{
    /// <summary>Bytes in a learn string. Table 3-2: <c>OL (123b)</c> and <c>IL 123b</c>.</summary>
    public const int LearnStringLength = 123;

    private readonly IInstrumentLink _link;

    public string Role => "dut";
    public string Model => "HP 8340B";
    public IInstrumentLink Link => _link;

    public Hp8340B(IInstrumentLink link) => _link = link ?? throw new ArgumentNullException(nameof(link));

    private void Send(params string[] parts) => _link.Write(string.Join(" ", parts));

    private static string Num(double value, string format = "0.#########") =>
        value.ToString(format, CultureInfo.InvariantCulture);

    // --- Instrument state -----------------------------------------------------------------

    /// <summary>Device clear, preset, RF off. Leaves the DUT in a known posture.</summary>
    public void Initialize()
    {
        _link.Clear();
        _link.Write(Hp8340BCommands.Require("IP"));
        _link.Write(Hp8340BCommands.Require("RF0"));
    }

    public void Preset() => _link.Write(Hp8340BCommands.Require("IP"));

    public void RfOn() => _link.Write(Hp8340BCommands.Require("RF1"));

    public void RfOff() => _link.Write(Hp8340BCommands.Require("RF0"));

    // --- Frequency ------------------------------------------------------------------------

    public void SetCwGHz(double ghz) =>
        Send(Hp8340BCommands.Require("CW"), Num(ghz), Hp8340BCommands.Require("GZ"));

    /// <summary>Sets the swept range. 5-14 uses this constantly, e.g. 6.9 to 13.5 GHz for band 2.</summary>
    public void SetSweepGHz(double startGHz, double stopGHz)
    {
        if (stopGHz <= startGHz)
            throw new ArgumentException(
                $"Stop frequency ({stopGHz} GHz) must be above start ({startGHz} GHz).",
                nameof(stopGHz));

        Send(Hp8340BCommands.Require("FA"), Num(startGHz), Hp8340BCommands.Require("GZ"));
        Send(Hp8340BCommands.Require("FB"), Num(stopGHz), Hp8340BCommands.Require("GZ"));
    }

    /// <summary>
    /// Sets centre frequency and span. <c>DF</c> is DELTA frequency in Table 3-2, which on this
    /// instrument is the span about the centre.
    /// </summary>
    public void SetCentreSpanGHz(double centreGHz, double spanGHz)
    {
        if (spanGHz < 0)
            throw new ArgumentOutOfRangeException(nameof(spanGHz), spanGHz, "Span cannot be negative.");

        Send(Hp8340BCommands.Require("CF"), Num(centreGHz), Hp8340BCommands.Require("GZ"));
        Send(Hp8340BCommands.Require("DF"), Num(spanGHz), Hp8340BCommands.Require("GZ"));
    }

    /// <summary>Sets the frequency step size used by the step keys — <c>SHCF</c>.</summary>
    public void SetFrequencyStepGHz(double stepGHz) =>
        Send(Hp8340BCommands.Require("SHCF"), Num(stepGHz), Hp8340BCommands.Require("GZ"));

    // --- Power ----------------------------------------------------------------------------

    public void SetPowerDbm(double dbm) =>
        Send(Hp8340BCommands.Require("PL"), Num(dbm, "0.###"), Hp8340BCommands.Require("DB"));

    /// <summary>Power sweep on or off — <c>PS1</c>/<c>PS0</c>. Used throughout 5-16.</summary>
    public void SetPowerSweep(bool on) =>
        _link.Write(Hp8340BCommands.Require(on ? "PS1" : "PS0"));

    /// <summary>Amplitude modulation on or off — <c>AM1</c>/<c>AM0</c>.</summary>
    public void SetAmplitudeModulation(bool on) =>
        _link.Write(Hp8340BCommands.Require(on ? "AM1" : "AM0"));

    /// <summary>
    /// Selects the leveling source. <see cref="LevelingMode.External"/> with no detector
    /// connected is how 5-14 runs the SYTM tracking adjustments unleveled.
    /// </summary>
    public void SetLeveling(LevelingMode mode) => _link.Write(Hp8340BCommands.Require(mode switch
    {
        LevelingMode.Internal => "A1",
        LevelingMode.External => "A2",
        LevelingMode.PowerMeter => "A3",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    }));

    // --- Sweep ----------------------------------------------------------------------------

    /// <summary>
    /// Sets sweep time. Sent in milliseconds (<c>MS</c>) below 1 s and seconds (<c>SC</c>) above,
    /// which keeps the numbers close to how the manual writes them — 5-14 asks for 200 ms sweeps
    /// and 1 s multiband sweeps.
    /// </summary>
    public void SetSweepTime(TimeSpan sweepTime)
    {
        if (sweepTime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(sweepTime), sweepTime, "Sweep time must be positive.");

        if (sweepTime < TimeSpan.FromSeconds(1))
            Send(Hp8340BCommands.Require("ST"), Num(sweepTime.TotalMilliseconds),
                 Hp8340BCommands.Require("MS"));
        else
            Send(Hp8340BCommands.Require("ST"), Num(sweepTime.TotalSeconds),
                 Hp8340BCommands.Require("SC"));
    }

    public void SetSweepMode(SweepMode mode) => _link.Write(Hp8340BCommands.Require(mode switch
    {
        SweepMode.Continuous => "S1",
        SweepMode.Single => "S2",
        SweepMode.Manual => "S3",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    }));

    public void SingleSweep() => _link.Write(Hp8340BCommands.Require("S2"));

    public void TakeSweep() => _link.Write(Hp8340BCommands.Require("TS"));

    // --- State storage --------------------------------------------------------------------

    /// <summary>
    /// Saves the current state to register <paramref name="register"/>. Table 3-2 gives
    /// <c>SVn</c> with n a single digit 1-9. 5-14 steps 32-58 use registers 1, 2 and 3 for the
    /// slow, AUTO and single-sweep states it compares.
    /// </summary>
    public void SaveState(int register)
    {
        if (register is < 1 or > 9)
            throw new ArgumentOutOfRangeException(
                nameof(register), register, "SAVE registers are 1-9 (Table 3-2, SVn).");

        _link.Write(Hp8340BCommands.Require("SV") + register.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Recalls register <paramref name="register"/>. Table 3-2 gives <c>RCn</c> with n 0-9 — a
    /// wider range than SAVE, because register 0 holds the pre-recall state.
    /// </summary>
    public void RecallState(int register)
    {
        if (register is < 0 or > 9)
            throw new ArgumentOutOfRangeException(
                nameof(register), register, "RECALL registers are 0-9 (Table 3-2, RCn).");

        _link.Write(Hp8340BCommands.Require("RC") + register.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Reads the learn string — the instrument's whole front-panel state as 123 binary bytes
    /// (<c>OL (123b)</c>). Captured by `backup-cal` (M1-01) before anything is adjusted.
    /// </summary>
    public byte[] ReadLearnString()
    {
        _link.Write(Hp8340BCommands.Require("OL"));
        var data = _link.ReadBytes(LearnStringLength);

        if (data.Length != LearnStringLength)
            throw new InvalidOperationException(
                $"Expected a {LearnStringLength}-byte learn string, got {data.Length}.");

        return data;
    }

    /// <summary>
    /// Restores a learn string (<c>IL 123b</c>).
    ///
    /// <para><b>This is a write to the instrument.</b> Repo rule 1: the caller must have obtained
    /// explicit confirmation for this specific action. The driver enforces the length, because a
    /// short payload would leave the instrument consuming whatever came next as state.</para>
    /// </summary>
    public void WriteLearnString(byte[] learnString)
    {
        ArgumentNullException.ThrowIfNull(learnString);

        if (learnString.Length != LearnStringLength)
            throw new ArgumentException(
                $"A learn string is exactly {LearnStringLength} bytes (Table 3-2, IL 123b); "
                + $"got {learnString.Length}. Refusing to send a partial state.",
                nameof(learnString));

        _link.Write(Hp8340BCommands.Require("IL"));
        _link.WriteBytes(learnString);
    }

    // --- Status and identity ---------------------------------------------------------------

    /// <summary>Reads status byte #1 by serial poll (Table 4-31).</summary>
    public StatusByte1 ReadStatus() => (StatusByte1)_link.SerialPoll();

    /// <summary>
    /// Reads BOTH status bytes with <c>OS (2b)</c>. This is the only way to see extended status
    /// byte #2, which carries the two bits this project depends on: bit 6 RF unleveled (M1-05
    /// finds maximum leveled power with it) and bit 3 external frequency reference selected
    /// (<c>probe</c> confirms the Z3805A lock with it).
    /// </summary>
    public (StatusByte1 Primary, StatusByte2 Extended) ReadBothStatusBytes()
    {
        _link.Write(Hp8340BCommands.Require("OS"));
        var data = _link.ReadBytes(2);

        if (data.Length != 2)
            throw new InvalidOperationException($"Expected 2 status bytes, got {data.Length}.");

        return ((StatusByte1)data[0], (StatusByte2)data[1]);
    }

    /// <summary>Clears both status bytes — <c>CS</c>.</summary>
    public void ClearStatus() => _link.Write(Hp8340BCommands.Require("CS"));

    /// <summary>
    /// Reads the instrument's identification — <c>OI (19a)</c>, 19 ASCII characters. The 8340B's
    /// equivalent of <c>*IDN?</c>, which it predates.
    /// </summary>
    public string ReadIdentity() => _link.Query(Hp8340BCommands.Require("OI")).Trim();

    /// <summary>True if the DUT reports the external 10 MHz reference selected (byte #2 bit 3).</summary>
    public bool IsExternalReferenceSelected() =>
        ReadBothStatusBytes().Extended.HasFlag(StatusByte2.ExternalFreqRefSelected);

    /// <summary>
    /// True if the UNLEVELED lamp is on (byte #2 bit 6). This is how maximum leveled power is
    /// found over the bus rather than by watching the front panel (M1-05).
    /// </summary>
    public bool IsUnleveled() => ReadBothStatusBytes().Extended.HasFlag(StatusByte2.RfUnleveled);

    // --- Waiting ---------------------------------------------------------------------------

    /// <summary>
    /// Waits for <see cref="StatusByte1.RfSettled"/>. The 8340B's stand-in for *OPC?, which it
    /// does not have. Returns false on timeout rather than throwing, so a caller can decide
    /// whether a slow settle is fatal.
    ///
    /// Aborts immediately on <see cref="StatusByte1.SyntaxError"/>: waiting out the full timeout
    /// after a malformed command would hide the real fault behind a timeout message.
    /// See <see cref="StatusPoller"/> for why this polls rather than using SRQ.
    /// </summary>
    public bool WaitSettled(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        StatusPoller.WaitFor(
            _link,
            isReady: s => ((StatusByte1)s).HasFlag(StatusByte1.RfSettled),
            timeout: timeout,
            abort: s => ((StatusByte1)s).HasFlag(StatusByte1.SyntaxError),
            abortMessage: _ => SyntaxErrorMessage("settling"),
            cancellationToken: cancellationToken) is not null;

    /// <summary>
    /// Waits for <see cref="StatusByte1.EndOfSweep"/> after a <see cref="TakeSweep"/>. Same
    /// polling and abort behaviour as <see cref="WaitSettled"/>.
    /// </summary>
    public bool WaitEndOfSweep(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        StatusPoller.WaitFor(
            _link,
            isReady: s => ((StatusByte1)s).HasFlag(StatusByte1.EndOfSweep),
            timeout: timeout,
            abort: s => ((StatusByte1)s).HasFlag(StatusByte1.SyntaxError),
            abortMessage: _ => SyntaxErrorMessage("waiting for end of sweep"),
            cancellationToken: cancellationToken) is not null;

    private string SyntaxErrorMessage(string what) =>
        $"The 8340B reported an HP-IB syntax error (status byte #1 bit 5) while {what}. The last "
        + $"command sent was: {_link.History.LastOrDefault() ?? "(none)"}";

    // --- Probe -----------------------------------------------------------------------------

    public ProbeResult Probe()
    {
        try
        {
            // OI is the 8340B's identification query; it predates *IDN?.
            var identity = ReadIdentity();
            var (primary, extended) = ReadBothStatusBytes();

            var detail = extended.HasFlag(StatusByte2.FaultIndicatorOn) ? "FAULT indicator is ON. "
                       : extended.HasFlag(StatusByte2.RfUnlocked) ? "RF UNLOCKED. "
                       : extended.HasFlag(StatusByte2.OvenCold) ? "Oven cold — still warming up. "
                       : null;

            return new ProbeResult(
                Role, Model, _link.ResourceName,
                Responded: true,
                Identity: string.IsNullOrWhiteSpace(identity)
                    ? $"status #1 0x{(byte)primary:X2}, #2 0x{(byte)extended:X2}"
                    : identity,
                ExternalReference: extended.HasFlag(StatusByte2.ExternalFreqRefSelected),
                Detail: detail);
        }
        catch (Exception ex)
        {
            return new ProbeResult(Role, Model, _link.ResourceName, Responded: false, Detail: ex.Message);
        }
    }

    public void Dispose() => _link.Dispose();
}
