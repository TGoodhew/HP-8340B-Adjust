using System.Globalization;
using HP8340B.Instruments.Visa;

namespace HP8340B.Instruments;

/// <summary>
/// Agilent 54845A Infiniium — 1.5 GHz, four channels. The <c>fast-scope</c> for S7, 4-12 pulse
/// modulation rise and fall time.
///
/// <para>Sources: <b>54845A Service Manual</b> for the specifications, and the <b>Infiniium
/// Programmer's Quick Reference Guide</b> (pub. 54810-97065) for every command below.</para>
///
/// <para><b>A note on how those commands were obtained.</b> The quick reference is a vector-drawn
/// syntax-diagram book: its text layer holds the front matter and parameter tables and none of the
/// command reference, so searching it returns nothing. The commands here were read off pages
/// rendered to images. That matters for anyone maintaining this driver — grepping the PDF will
/// appear to prove these commands do not exist.</para>
///
/// <para><b>The quick reference gives syntax, not semantics.</b> It shows what may be sent, not
/// what the instrument does with it. Anything below that depends on meaning rather than form — the
/// sample scaling in particular — is the documented Agilent convention and is marked as needing
/// confirmation against the instrument.</para>
/// </summary>
public sealed class Infiniium54845A : IOscilloscope
{
    private readonly IInstrumentLink _link;

    /// <summary>
    /// Coupling and termination per channel, because the instrument sets both with one command and
    /// the caller sets them separately. See <see cref="WriteInput"/>.
    /// </summary>
    private readonly Dictionary<int, (ScopeCoupling Coupling, ScopeInputImpedance Impedance)> _input = new();

    private WaveformScaling? _scaling;

    public string Role { get; }
    public string Model => "Agilent 54845A";
    public IInstrumentLink Link => _link;

    /// <summary>
    /// Specifications from the 54845A Service Manual: 1.5 GHz bandwidth at 4 GSa/s in four-channel
    /// mode, and vertical sensitivity of <b>2 mV/div</b> to 2 V/div on 1 MΩ, 2 mV/div to 1 V/div
    /// on 50 Ω.
    ///
    /// <para><b>XY is declared false deliberately.</b> The instrument has an X-versus-Y display
    /// function — the user manual documents it, and it is why searching these manuals for "XY"
    /// finds nothing. But the command that selects it is not in the quick reference, so this driver
    /// cannot drive it. Claiming the capability would let the scope be accepted as the
    /// <c>tuning-scope</c> at config load and then fail at the bench, which is the exact failure
    /// <see cref="ScopeRole"/> exists to prevent. It is the driver that cannot do XY, not the
    /// instrument; fix the driver and flip this.</para>
    ///
    /// <para><see cref="ScopeCapability.MaxVoltsPerDivision"/> carries the 1 MΩ figure. The coarsest
    /// range is one step lower on 50 Ω, which no procedure here goes near.</para>
    /// </summary>
    public ScopeCapability Capability { get; } = new(
        Model: "Agilent 54845A",
        Channels: 4,
        BandwidthHz: 1.5e9,
        MinVoltsPerDivision: 2e-3,
        MaxVoltsPerDivision: 2.0,
        InputImpedances: [ScopeInputImpedance.OneMegaohm, ScopeInputImpedance.FiftyOhm],
        HasExternalTrigger: true,
        HasXyMode: false,
        Derates: [],
        Source: "54845A Service Manual; Infiniium Programmer's Quick Reference Guide 54810-97065");

    public Infiniium54845A(IInstrumentLink link, string role = "fast-scope")
    {
        _link = link ?? throw new ArgumentNullException(nameof(link));
        Role = role;
    }

    private sealed record WaveformScaling(
        int Channel, int Points,
        double XIncrement, double XOrigin, double XReference,
        double YIncrement, double YOrigin, double YReference);

    private static string Num(double v) => v.ToString("0.#########", CultureInfo.InvariantCulture);

    private void ValidateChannel(int channel)
    {
        if (!Capability.HasChannel(channel))
            throw new ArgumentOutOfRangeException(
                nameof(channel), channel, $"The 54845A has channels 1-{Capability.Channels}.");
    }

    // --- Channels -------------------------------------------------------------------------------

    public void SetChannelEnabled(int channel, bool on)
    {
        ValidateChannel(channel);
        _link.Write($":CHANnel{channel}:DISPlay {(on ? "ON" : "OFF")}");
        InvalidateWaveformScaling();
    }

    public void SetChannelScale(int channel, double voltsPerDivision)
    {
        ValidateChannel(channel);

        if (voltsPerDivision < Capability.MinVoltsPerDivision - 1e-12)
            throw new ArgumentOutOfRangeException(
                nameof(voltsPerDivision), voltsPerDivision,
                $"The 54845A stops at {Capability.MinVoltsPerDivision * 1e3:0.##} mV/div. The "
                + "instrument would accept a finer request and quietly coerce it, so the reading "
                + "would not be at the sensitivity the procedure called for.");

        _link.Write($":CHANnel{channel}:SCALe {Num(voltsPerDivision)}");
        InvalidateWaveformScaling();
    }

    public void SetChannelOffset(int channel, double volts)
    {
        ValidateChannel(channel);
        _link.Write($":CHANnel{channel}:OFFSet {Num(volts)}");
        InvalidateWaveformScaling();
    }

    /// <summary>
    /// Sets coupling, keeping the channel's current termination.
    ///
    /// <para>See <see cref="WriteInput"/> for why these two cannot be set independently.</para>
    /// </summary>
    public void SetChannelCoupling(int channel, ScopeCoupling coupling)
    {
        ValidateChannel(channel);
        WriteInput(channel, coupling, CurrentInput(channel).Impedance);
    }

    /// <summary>Sets termination, keeping the channel's current coupling.</summary>
    public void SetInputImpedance(int channel, ScopeInputImpedance impedance)
    {
        ValidateChannel(channel);

        if (!Capability.Supports(impedance))
            throw new NotSupportedException($"The 54845A does not support {impedance}.");

        WriteInput(channel, CurrentInput(channel).Coupling, impedance);
    }

    private (ScopeCoupling Coupling, ScopeInputImpedance Impedance) CurrentInput(int channel) =>
        _input.TryGetValue(channel, out var state)
            ? state
            // The instrument powers up DC-coupled at 1 MΩ, and this is only the starting point for
            // the first combined write; every later one is built from what this driver last set.
            : (ScopeCoupling.Dc, ScopeInputImpedance.OneMegaohm);

    /// <summary>
    /// Writes <c>:CHANnel&lt;N&gt;:INPut</c>, which sets coupling and termination <b>together</b>.
    ///
    /// <para>This is the trap in this instrument, and the reason the driver keeps per-channel state.
    /// The documented arguments are <c>AC</c>, <c>DC</c>, <c>LFR1</c>, <c>LFR2</c> and
    /// <c>DC50</c>/<c>DCFifty</c> — so there is no way to change coupling without also asserting a
    /// termination, or the other way round. A driver that mapped
    /// <see cref="SetChannelCoupling"/> straight onto this command would silently move a channel
    /// from 50 Ω back to 1 MΩ, which invalidates any detector calibration taken at the other one
    /// (M0-29) and produces a trace that is wrong by a constant and looks entirely plausible.</para>
    ///
    /// <para>Ground coupling and the LFR1/LFR2 low-frequency-reject modes are not offered: GND is
    /// not one of this command's arguments, and LFR needs an 1153A probe attached.</para>
    /// </summary>
    private void WriteInput(int channel, ScopeCoupling coupling, ScopeInputImpedance impedance)
    {
        var argument = (coupling, impedance) switch
        {
            (ScopeCoupling.Dc, ScopeInputImpedance.OneMegaohm) => "DC",
            (ScopeCoupling.Ac, ScopeInputImpedance.OneMegaohm) => "AC",
            (ScopeCoupling.Dc, ScopeInputImpedance.FiftyOhm) => "DC50",

            (ScopeCoupling.Ac, ScopeInputImpedance.FiftyOhm) => throw new NotSupportedException(
                "The 54845A has no AC-coupled 50 ohm input mode: :CHANnel<N>:INPut offers AC and "
                + "DC at 1 Mohm, and DC50 at 50 ohm. 4-12 measures a detected envelope and wants "
                + "DC anyway, so this combination should not be needed."),

            (ScopeCoupling.Ground, _) => throw new NotSupportedException(
                "Ground coupling is not one of :CHANnel<N>:INPut's arguments on the 54845A."),

            _ => throw new ArgumentOutOfRangeException(nameof(impedance), impedance, null),
        };

        _link.Write($":CHANnel{channel}:INPut {argument}");
        _input[channel] = (coupling, impedance);

        InvalidateWaveformScaling();
    }

    public ScopeInputImpedance GetInputImpedance(int channel)
    {
        ValidateChannel(channel);

        var reported = _link.Query($":CHANnel{channel}:INPut?").Trim();

        // DC50 and DCFifty are the same setting; everything else is a 1 MΩ mode.
        return reported.StartsWith("DC5", StringComparison.OrdinalIgnoreCase)
               || reported.StartsWith("DCF", StringComparison.OrdinalIgnoreCase)
            ? ScopeInputImpedance.FiftyOhm
            : ScopeInputImpedance.OneMegaohm;
    }

    /// <summary>
    /// Probe attenuation factor. Unlike the Tektronix scopes, this one is told rather than
    /// detecting it.
    /// </summary>
    public void SetProbeRatio(int channel, double ratio)
    {
        ValidateChannel(channel);

        if (ratio <= 0)
            throw new ArgumentOutOfRangeException(nameof(ratio), ratio, "Probe ratio must be positive.");

        // The parameter table gives attenuation_factor as 0.0001 to 1,000,000, and RATio selects
        // the plain ratio form rather than decibels.
        _link.Write($":CHANnel{channel}:PROBe {Num(ratio)},RATio");
        InvalidateWaveformScaling();
    }

    // --- Horizontal and trigger -------------------------------------------------------------------

    public void SetTimebaseScale(double secondsPerDivision)
    {
        _link.Write($":TIMebase:SCALe {Num(secondsPerDivision)}");
        InvalidateWaveformScaling();
    }

    /// <summary>
    /// Horizontal mode. Only <see cref="TimebaseMode.Normal"/> is available through this driver -
    /// see the note on <see cref="Capability"/> about X-versus-Y.
    /// </summary>
    public void SetTimebaseMode(TimebaseMode mode)
    {
        if (mode != TimebaseMode.Normal)
            throw new NotSupportedException(
                $"This driver cannot put the 54845A into {mode}. The instrument has an X-versus-Y "
                + "display function, but the command that selects it is not in the Programmer's "
                + "Quick Reference Guide, which is the only Infiniium programming document in the "
                + "library. Capability.HasXyMode is false to match, so the scope is refused as the "
                + "tuning-scope at config load rather than failing at the bench.");

        InvalidateWaveformScaling();
    }

    /// <summary>
    /// External trigger, on the AUX input.
    ///
    /// <para>The quick reference notes that <c>AUX</c> is available on the 54815/25/35/45/46 and
    /// that <c>EXTernal</c> is only on the 54810/20 — so for this model the external trigger is
    /// AUX, and asking for EXTernal would be rejected by the instrument.</para>
    /// </summary>
    public void SetExternalTrigger(double levelVolts, bool rising = true)
    {
        _link.Write(":TRIGger:MODE EDGE");
        _link.Write(":TRIGger:EDGE:SOURce AUX");
        _link.Write($":TRIGger:EDGE:SLOPe {(rising ? "POSitive" : "NEGative")}");

        // :TRIGger:LEVel takes the source and the level together.
        _link.Write($":TRIGger:LEVel AUX,{Num(levelVolts)}");
    }

    /// <summary>Triggers on an input channel instead of AUX.</summary>
    public void SetChannelTrigger(int channel, double levelVolts, bool rising = true)
    {
        ValidateChannel(channel);

        _link.Write(":TRIGger:MODE EDGE");
        _link.Write($":TRIGger:EDGE:SOURce CHANnel{channel}");
        _link.Write($":TRIGger:EDGE:SLOPe {(rising ? "POSitive" : "NEGative")}");
        _link.Write($":TRIGger:LEVel CHANnel{channel},{Num(levelVolts)}");
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

    private WaveformScaling ReadScaling(int channel)
    {
        _link.Write($":WAVeform:SOURce CHANnel{channel}");
        _link.Write(":WAVeform:FORMat BYTE");

        // One byte a sample makes byte order irrelevant, but it is set explicitly so the transfer
        // does not depend on whatever the instrument was last left in.
        _link.Write(":WAVeform:BYTeorder MSBFirst");

        var points = (int)Field(":WAVeform:POINts?");

        if (points <= 0)
            throw new InvalidOperationException(
                $"The 54845A reported {points} points, so there is no frame to read.");

        return new WaveformScaling(
            channel,
            points,
            Field(":WAVeform:XINCrement?"),
            Field(":WAVeform:XORigin?"),
            Field(":WAVeform:XREFerence?"),
            Field(":WAVeform:YINCrement?"),
            Field(":WAVeform:YORigin?"),
            Field(":WAVeform:YREFerence?"));

        double Field(string query)
        {
            var text = _link.Query(query).Trim();

            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? v
                : throw new InvalidOperationException(
                    $"The 54845A answered {query} with '{text}'.");
        }
    }

    /// <summary>
    /// Reads one frame and scales it.
    ///
    /// <para><b>Scaling convention, and what is assumed.</b> Agilent's is
    /// <c>volts = (raw − YREFerence) × YINCrement + YORigin</c>, and the time axis is
    /// <c>seconds = (n − XREFerence) × XINCrement + XORigin</c>. Note this is a <b>third</b>
    /// convention: it is not the Rigol's <c>(raw − yorigin − yreference) × yincrement</c>, and it is
    /// not Tektronix's <c>YZEro + YMUlt × (raw − YOFf)</c>. Three scopes, three formulae, all of
    /// which produce a plausible trace when applied to the wrong instrument.</para>
    ///
    /// <para>Raw samples are treated as <b>unsigned</b> 0-255, with the instrument's own
    /// <c>YREFerence</c> carrying the mid-scale offset. The quick reference documents syntax only,
    /// so signedness is inferred from Agilent's convention rather than read from a page.
    /// <b>Confirm against the instrument before trusting this</b> — getting it wrong inverts and
    /// offsets the trace without making it look broken, and the detector output is negative-going,
    /// so a wrong trace still looks like a detector trace.</para>
    /// </summary>
    private ScopeWaveform ReadFrame(WaveformScaling scaling)
    {
        _link.Write(":WAVeform:DATA?");

        var raw = IeeeBlock.StripHeader(
            _link.ReadBytes(scaling.Points + IeeeBlock.OverheadBytes));

        if (raw.Count == 0)
            throw new InvalidOperationException("The 54845A returned an empty waveform.");

        var volts = new double[raw.Count];

        for (var i = 0; i < raw.Count; i++)
            volts[i] = (raw[i] - scaling.YReference) * scaling.YIncrement + scaling.YOrigin;

        var start = (0 - scaling.XReference) * scaling.XIncrement + scaling.XOrigin;

        return new ScopeWaveform(scaling.Channel, volts, scaling.XIncrement, start);
    }

    /// <summary>
    /// Screen capture is not implemented: the HARDcopy subsystem's commands are in the same
    /// unextractable part of the quick reference, and a guessed sequence would write a corrupt file
    /// into the session record, which is worse than having no picture. Waveform capture, which is
    /// what the measurements use, works.
    /// </summary>
    public byte[] CaptureScreenPng(int maxBytes = 2_000_000) =>
        throw new NotSupportedException(
            "Screen capture for the 54845A is not implemented - the HARDcopy command sequence has "
            + "not been sourced. Waveform capture works.");

    public ProbeResult Probe()
    {
        try
        {
            var identity = _link.Query("*IDN?").Trim();

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
