using System.Globalization;
using HP8340B.Instruments.Visa;

namespace HP8340B.Instruments;

/// <summary>
/// Driver for the Rigol DG1032Z arbitrary waveform generator.
///
/// <para>Source: <b>DG1000Z Programming Guide</b> (in the local manual library). Standard Rigol
/// SCPI: <c>[:SOURce[n]]:APPLy:PULSe</c>, <c>[:SOURce[n]]:APPLy:DC</c>,
/// <c>:OUTPut[n][:STATe]</c>.</para>
///
/// <para>Two jobs here: a second pulse source alongside the 8116A, and a DC supply for the 5.00 V
/// and +/-0.3 V points that Table 4-2 wanted a 6294A for.</para>
/// </summary>
public sealed class RigolDg1032Z : IInstrument
{
    /// <summary>Pulse frequency range, from the APPLy:PULSe parameter table: 1 uHz to 25 MHz.</summary>
    public const double MinPulseHz = 1e-6;
    public const double MaxPulseHz = 25e6;

    private readonly IInstrumentLink _link;

    public string Role { get; }
    public string Model => "Rigol DG1032Z";
    public IInstrumentLink Link => _link;

    public RigolDg1032Z(IInstrumentLink link, string role = "arb-generator")
    {
        _link = link ?? throw new ArgumentNullException(nameof(link));
        Role = role;
    }

    private static string Num(double v) => v.ToString("0.#########", CultureInfo.InvariantCulture);

    private static void ValidateChannel(int channel)
    {
        if (channel is < 1 or > 2)
            throw new ArgumentOutOfRangeException(nameof(channel), channel, "The DG1032Z has channels 1 and 2.");
    }

    public void Preset()
    {
        _link.Clear();
        _link.Write("*RST");
    }

    /// <summary>
    /// Sets a channel to pulse. The guide's own example is
    /// <c>:SOUR1:APPL:PULS 100,3,2,1</c> — frequency, amplitude, offset, phase.
    /// </summary>
    public void ApplyPulse(int channel, double hz, double amplitudeVpp, double offsetVolts = 0)
    {
        ValidateChannel(channel);

        if (hz is < MinPulseHz or > MaxPulseHz)
            throw new ArgumentOutOfRangeException(
                nameof(hz), hz, $"Pulse frequency range is {MinPulseHz} Hz to {MaxPulseHz / 1e6:0.#} MHz.");

        _link.Write($":SOURce{channel}:APPLy:PULSe {Num(hz)},{Num(amplitudeVpp)},{Num(offsetVolts)},0");
    }

    /// <summary>Pulse width, seconds — separate from APPLy, which does not take one.</summary>
    public void SetPulseWidthSeconds(int channel, double seconds)
    {
        ValidateChannel(channel);

        if (seconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "Width must be positive.");

        _link.Write($":SOURce{channel}:FUNCtion:PULSe:WIDTh {Num(seconds)}");
    }

    /// <summary>
    /// Sets a channel to DC at <paramref name="volts"/>.
    ///
    /// <para>The guide is explicit that frequency and amplitude "are not applicable to the DC
    /// function but they must be specified as a placeholder", hence the two leading ones — its own
    /// example is <c>:SOUR1:APPL:DC 1,1,2</c> for 2 V DC.</para>
    /// </summary>
    public void ApplyDc(int channel, double volts)
    {
        ValidateChannel(channel);
        _link.Write($":SOURce{channel}:APPLy:DC 1,1,{Num(volts)}");
    }

    /// <summary>
    /// Output load. Matters for DC: the generator's amplitude arithmetic assumes 50 ohms unless
    /// told otherwise, so a DC level feeding a high-impedance node reads double what was asked
    /// for if this is left alone.
    /// </summary>
    public void SetOutputLoad(int channel, double ohms)
    {
        ValidateChannel(channel);
        _link.Write($":OUTPut{channel}:LOAD {Num(ohms)}");
    }

    /// <summary>Output load set to high impedance, which is what a DVM or a test point wants.</summary>
    public void SetOutputLoadHighZ(int channel)
    {
        ValidateChannel(channel);
        _link.Write($":OUTPut{channel}:LOAD INFinity");
    }

    public void SetOutputEnabled(int channel, bool on)
    {
        ValidateChannel(channel);
        _link.Write($":OUTPut{channel}:STATe {(on ? "ON" : "OFF")}");
    }

    public ProbeResult Probe()
    {
        try
        {
            var identity = _link.Query("*IDN?").Trim();

            return new ProbeResult(
                Role, Model, _link.ResourceName,
                Responded: !string.IsNullOrWhiteSpace(identity),
                Identity: identity,
                ExternalReference: null,
                Detail: null);
        }
        catch (Exception ex)
        {
            return new ProbeResult(Role, Model, _link.ResourceName, Responded: false, Detail: ex.Message);
        }
    }

    public void Dispose() => _link.Dispose();
}

/// <summary>
/// Driver for the Agilent E4438C ESG vector signal generator.
///
/// <para>Source: <b>E4438C SCPI Command Reference</b> (in the local manual library):
/// <c>[:SOURce]:FREQuency:FIXed</c>, <c>[:SOURce]:POWer[:LEVel]</c>,
/// <c>:OUTPut[:STATe]</c>, <c>[:SOURce]:ROSCillator:SOURce?</c>.</para>
///
/// <para>Three jobs: CW for the 5-5 300-400 MHz injection, the clean low-band LO where the 8673B
/// cannot reach (it starts at 2 GHz), and the phase-noise baseline source for 4-9 — it is far
/// quieter than the DUT, which is what makes an 8563E baseline meaningful.</para>
///
/// <para>Like the 8673B, it can prove it is on the house reference: <c>:ROSC:SOUR?</c> returns
/// INT or EXT. Unlike the 8673B it is a modern SCPI instrument, so that is a single query rather
/// than two status bits that have to agree.</para>
/// </summary>
public sealed class AgilentE4438C : IInstrument
{
    /// <summary>
    /// Lower frequency limit. The upper one depends on the frequency option fitted (1, 2, 3, 4 or
    /// 6 GHz), which is not recorded for this unit yet — see the bench note.
    /// </summary>
    public const double MinHz = 250e3;

    private readonly IInstrumentLink _link;

    public string Role { get; }
    public string Model => "Agilent E4438C";
    public IInstrumentLink Link => _link;

    /// <summary>
    /// Upper frequency limit, from the option fitted. Defaults to the most conservative of the
    /// range so a frequency this unit may not reach is refused rather than silently clipped.
    /// </summary>
    public double MaxHz { get; set; } = 1e9;

    public AgilentE4438C(IInstrumentLink link, string role = "vector-generator")
    {
        _link = link ?? throw new ArgumentNullException(nameof(link));
        Role = role;
    }

    private static string Num(double v) => v.ToString("0.#########", CultureInfo.InvariantCulture);

    public void Preset()
    {
        _link.Clear();
        _link.Write("*RST");
        _link.Write("*CLS");
    }

    /// <summary>CW frequency — <c>:FREQuency:FIXed</c>.</summary>
    public void SetCwFrequencyHz(double hz)
    {
        if (hz < MinHz || hz > MaxHz)
            throw new ArgumentOutOfRangeException(
                nameof(hz), hz,
                $"This E4438C is configured for {MinHz / 1e3:0.#} kHz to {MaxHz / 1e9:0.###} GHz. "
                + "If the unit has a higher frequency option, set MaxHz to match it — the default "
                + "is the most conservative option so an unreachable frequency is refused rather "
                + "than silently clipped.");

        _link.Write($":SOURce:FREQuency:FIXed {Num(hz)} HZ");
    }

    /// <summary>Output level in dBm — <c>:POWer</c>.</summary>
    public void SetOutputLevelDbm(double dbm) =>
        _link.Write($":SOURce:POWer:LEVel:IMMediate:AMPLitude {Num(dbm)} DBM");

    public void SetRfOutput(bool on) => _link.Write($":OUTPut:STATe {(on ? "ON" : "OFF")}");

    /// <summary>
    /// True if the generator is running on an external reference — <c>:ROSC:SOUR?</c>, which
    /// "returns either INT (internal) or EXT (external)". Null if the answer is neither.
    /// </summary>
    public bool? IsExternalReferenceSelected()
    {
        var response = _link.Query(":SOURce:ROSCillator:SOURce?").Trim();

        if (response.StartsWith("EXT", StringComparison.OrdinalIgnoreCase)) return true;
        if (response.StartsWith("INT", StringComparison.OrdinalIgnoreCase)) return false;

        return null;
    }

    /// <summary>Sets up as a CW source, output last.</summary>
    public void ConfigureCw(double hz, double dbm)
    {
        SetRfOutput(false);
        SetCwFrequencyHz(hz);
        SetOutputLevelDbm(dbm);
        SetRfOutput(true);
    }

    public ProbeResult Probe()
    {
        try
        {
            var identity = _link.Query("*IDN?").Trim();
            var external = IsExternalReferenceSelected();

            var detail = external switch
            {
                true => "Reference: EXT.",
                false => "Reference: INT — on its own crystal, not the Z3805A. As the phase-noise "
                         + "baseline source for 4-9 its own noise sets the floor, so this matters.",
                null => ":ROSC:SOUR? gave no recognisable answer.",
            };

            return new ProbeResult(
                Role, Model, _link.ResourceName,
                Responded: !string.IsNullOrWhiteSpace(identity),
                Identity: identity,
                ExternalReference: external,
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
