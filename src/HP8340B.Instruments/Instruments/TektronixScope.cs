using System.Globalization;
using HP8340B.Instruments.Visa;

namespace HP8340B.Instruments;

/// <summary>
/// Shared behaviour for the Tektronix scopes on this bench.
///
/// <para>The TDS3014B and the DPO3034 share a command family but are not command-compatible, and
/// the differences are exactly the ones that matter here: the waveform preamble is
/// <c>WFMPre</c> on one and <c>WFMOutpre</c> on the other, input termination is
/// <c>CH&lt;x&gt;:IMPedance</c> against <c>CH&lt;x&gt;:TERmination</c>, the horizontal scale is
/// <c>HORizontal:MAIn:SCAle</c> against <c>HORizontal:SCAle</c>, and XY is a mode of
/// <c>DISplay:FORMat</c> against a separate <c>DISplay:XY</c>. Anything assuming "it is a Tek, it
/// will be the same" would produce a driver that silently failed on one of them.</para>
///
/// <para>Scaling is common to both, and is the Tek convention:
/// <c>volts = YZEro + YMUlt × (raw − YOFf)</c> and
/// <c>seconds = XZEro + XINcr × (n − PT_Off)</c>. Note that this is not the Rigol's convention —
/// a driver written by analogy with the DS1104Z would be wrong by an offset.</para>
/// </summary>
public abstract class TektronixScope : IOscilloscope
{
    private readonly IInstrumentLink _link;

    /// <summary>Cached preamble, so a streamed frame is one round trip.</summary>
    private TekScaling? _scaling;

    public string Role { get; }
    public abstract string Model { get; }
    public abstract ScopeCapability Capability { get; }
    public IInstrumentLink Link => _link;

    /// <summary><c>WFMPre</c> on the TDS3000 family, <c>WFMOutpre</c> on the DPO3000 family.</summary>
    protected abstract string PreambleRoot { get; }

    /// <summary><c>HORizontal:MAIn:SCAle</c> or <c>HORizontal:SCAle</c>.</summary>
    protected abstract string HorizontalScaleCommand { get; }

    protected TektronixScope(IInstrumentLink link, string role)
    {
        _link = link ?? throw new ArgumentNullException(nameof(link));
        Role = role;
    }

    /// <summary>The preamble fields needed to turn raw samples into volts and seconds.</summary>
    private sealed record TekScaling(
        int Channel, int Points, double YMult, double YOff, double YZero,
        double XIncr, double XZero, double PtOff);

    protected static string Num(double v) => v.ToString("0.#########", CultureInfo.InvariantCulture);

    protected void Write(string command) => _link.Write(command);

    protected string Query(string command) => _link.Query(command);

    protected void ValidateChannel(int channel)
    {
        if (!Capability.HasChannel(channel))
            throw new ArgumentOutOfRangeException(
                nameof(channel), channel,
                $"The {Model} has channels 1-{Capability.Channels}.");
    }

    // --- Channels -------------------------------------------------------------------------------

    public void SetChannelEnabled(int channel, bool on)
    {
        ValidateChannel(channel);
        Write($"SELect:CH{channel} {(on ? "ON" : "OFF")}");
        InvalidateWaveformScaling();
    }

    public void SetChannelScale(int channel, double voltsPerDivision)
    {
        ValidateChannel(channel);

        if (voltsPerDivision < Capability.MinVoltsPerDivision - 1e-12)
            throw new ArgumentOutOfRangeException(
                nameof(voltsPerDivision), voltsPerDivision,
                $"The {Model} stops at {Capability.MinVoltsPerDivision * 1e3:0.##} mV/div. "
                + "Asking for finer would be accepted by the instrument and quietly coerced, so "
                + "the reading would not be at the sensitivity the procedure called for.");

        Write($"CH{channel}:SCAle {Num(voltsPerDivision)}");
        InvalidateWaveformScaling();
    }

    public void SetChannelOffset(int channel, double volts)
    {
        ValidateChannel(channel);
        Write($"CH{channel}:OFFSet {Num(volts)}");
        InvalidateWaveformScaling();
    }

    public void SetChannelCoupling(int channel, ScopeCoupling coupling)
    {
        ValidateChannel(channel);

        Write($"CH{channel}:COUPling {coupling switch
        {
            ScopeCoupling.Dc => "DC",
            ScopeCoupling.Ac => "AC",
            ScopeCoupling.Ground => "GND",
            _ => throw new ArgumentOutOfRangeException(nameof(coupling), coupling, null),
        }}");

        InvalidateWaveformScaling();
    }

    /// <summary>The model's own termination command, which differs across the two families.</summary>
    protected abstract void WriteInputImpedance(int channel, ScopeInputImpedance impedance);

    /// <summary>Reads the termination back, as the instrument reports it.</summary>
    protected abstract ScopeInputImpedance ReadInputImpedance(int channel);

    public void SetInputImpedance(int channel, ScopeInputImpedance impedance)
    {
        ValidateChannel(channel);

        if (!Capability.Supports(impedance))
            throw new NotSupportedException(
                $"The {Model} does not support {impedance}.");

        WriteInputImpedance(channel, impedance);

        // Termination changes what the 8473C detector does, so it invalidates any detector
        // calibration taken at the other one (M0-29) as well as the preamble.
        InvalidateWaveformScaling();
    }

    public ScopeInputImpedance GetInputImpedance(int channel)
    {
        ValidateChannel(channel);
        return ReadInputImpedance(channel);
    }

    /// <summary>
    /// Probe attenuation.
    ///
    /// <para>Tektronix scopes with TekProbe inputs <b>detect</b> the probe rather than being told
    /// about it, so this verifies rather than sets. A driver that silently accepted a ratio it
    /// could not apply would scale every reading wrongly — the exact failure the Rigol's version of
    /// this method exists to prevent.</para>
    /// </summary>
    public void SetProbeRatio(int channel, double ratio)
    {
        ValidateChannel(channel);

        if (ratio <= 0)
            throw new ArgumentOutOfRangeException(nameof(ratio), ratio, "Probe ratio must be positive.");

        var reported = Query($"CH{channel}:PRObe?").Trim();

        if (!double.TryParse(reported, NumberStyles.Float, CultureInfo.InvariantCulture, out var actual))
            throw new InvalidOperationException(
                $"The {Model} reported its CH{channel} probe as '{reported}', which is not a number.");

        // Tek reports attenuation as a gain factor: a 10:1 probe reads 0.1.
        var asRatio = actual > 0 && actual < 1 ? 1.0 / actual : actual;

        if (Math.Abs(asRatio - ratio) > 0.01 * ratio)
            throw new InvalidOperationException(
                $"The {Model} has a {asRatio:0.##}:1 probe on CH{channel} but {ratio:0.##}:1 was "
                + "requested. TekProbe inputs detect the probe rather than being told about it, so "
                + "this cannot be set over the bus — change the probe, or the reading will be "
                + $"wrong by {asRatio / ratio:0.##}x.");
    }

    // --- Horizontal and trigger -------------------------------------------------------------------

    public void SetTimebaseScale(double secondsPerDivision)
    {
        Write($"{HorizontalScaleCommand} {Num(secondsPerDivision)}");
        InvalidateWaveformScaling();
    }

    /// <summary>The model's own XY control.</summary>
    protected abstract void WriteTimebaseMode(TimebaseMode mode);

    public void SetTimebaseMode(TimebaseMode mode)
    {
        WriteTimebaseMode(mode);
        InvalidateWaveformScaling();
    }

    /// <summary>
    /// The trigger source this model uses for the external input, or null if it has none usable.
    /// </summary>
    protected abstract string? ExternalTriggerSource { get; }

    public void SetExternalTrigger(double levelVolts, bool rising = true)
    {
        if (ExternalTriggerSource is null)
            throw new NotSupportedException(
                $"The {Model} has no usable external trigger input, so the DUT's sweep trigger has "
                + "to go to one of its channels and be selected with SetChannelTrigger instead. "
                + "That costs a channel, which is why the scope roles count it.");

        Write("TRIGger:A:TYPe EDGe");
        Write($"TRIGger:A:EDGe:SOUrce {ExternalTriggerSource}");
        Write($"TRIGger:A:EDGe:SLOpe {(rising ? "RISe" : "FALL")}");
        Write($"TRIGger:A:LEVel {Num(levelVolts)}");
    }

    /// <summary>
    /// Triggers on one of the input channels — how a scope with no external trigger input takes
    /// the DUT's sweep trigger.
    /// </summary>
    public void SetChannelTrigger(int channel, double levelVolts, bool rising = true)
    {
        ValidateChannel(channel);

        Write("TRIGger:A:TYPe EDGe");
        Write($"TRIGger:A:EDGe:SOUrce CH{channel}");
        Write($"TRIGger:A:EDGe:SLOpe {(rising ? "RISe" : "FALL")}");
        Write($"TRIGger:A:LEVel {Num(levelVolts)}");
    }

    // --- Capture ----------------------------------------------------------------------------------

    public void InvalidateWaveformScaling() => _scaling = null;

    public ScopeWaveform ReadWaveform(int channel)
    {
        ValidateChannel(channel);
        _scaling = ReadScaling(channel);
        return ReadFrame(_scaling);
    }

    public ScopeWaveform ReadWaveformStreaming(int channel)
    {
        ValidateChannel(channel);

        if (_scaling is null || _scaling.Channel != channel)
            _scaling = ReadScaling(channel);

        return ReadFrame(_scaling);
    }

    /// <summary>
    /// Sets the transfer up and reads the preamble fields.
    ///
    /// <para>One byte a sample, signed. Width 1 makes byte order irrelevant, which removes the
    /// commonest way to get a binary waveform transfer subtly wrong.</para>
    /// </summary>
    private TekScaling ReadScaling(int channel)
    {
        Write($"DATa:SOUrce CH{channel}");
        Write("DATa:ENCdg RIBinary");
        Write("DATa:WIDth 1");

        var points = (int)Field("NR_Pt");

        if (points <= 0)
            throw new InvalidOperationException(
                $"The {Model} reported {points} points, so there is no frame to read.");

        Write("DATa:STARt 1");
        Write($"DATa:STOP {points}");

        return new TekScaling(
            channel,
            points,
            Field("YMUlt"),
            Field("YOFf"),
            Field("YZEro"),
            Field("XINcr"),
            Field("XZEro"),
            Field("PT_Off"));

        double Field(string name)
        {
            var text = Query($"{PreambleRoot}:{name}?").Trim();

            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? v
                : throw new InvalidOperationException(
                    $"The {Model} reported {PreambleRoot}:{name} as '{text}'.");
        }
    }

    private ScopeWaveform ReadFrame(TekScaling scaling)
    {
        Write("CURVe?");

        var raw = IeeeBlock.StripHeader(
            _link.ReadBytes(scaling.Points + IeeeBlock.OverheadBytes));

        if (raw.Count == 0)
            throw new InvalidOperationException($"The {Model} returned an empty waveform.");

        var volts = new double[raw.Count];

        for (var i = 0; i < raw.Count; i++)
        {
            // RIBinary at width 1 is a signed byte.
            var code = (sbyte)raw[i];
            volts[i] = scaling.YZero + scaling.YMult * (code - scaling.YOff);
        }

        // Tek puts the trigger at PT_Off samples into the record, so the first sample is that far
        // before it — usually negative, and dropping the term would slide the whole trace.
        var start = scaling.XZero - scaling.XIncr * scaling.PtOff;

        return new ScopeWaveform(scaling.Channel, volts, scaling.XIncr, start);
    }

    /// <summary>Captures the screen. Tek hardcopy differs per family, so each model supplies it.</summary>
    public abstract byte[] CaptureScreenPng(int maxBytes = 2_000_000);

    public virtual ProbeResult Probe()
    {
        try
        {
            var identity = Query("*IDN?").Trim();

            return new ProbeResult(
                Role, Model, _link.ResourceName,
                Responded: !string.IsNullOrWhiteSpace(identity),
                Identity: identity);
        }
        catch (Exception ex)
        {
            return new ProbeResult(Role, Model, _link.ResourceName, Responded: false, Detail: ex.Message);
        }
    }

    public void Dispose() => _link.Dispose();
}
