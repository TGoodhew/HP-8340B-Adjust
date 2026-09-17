using System.Globalization;
using System.Text;
using HP8340B.Instruments.Visa;

namespace HP8340B.Instruments;

/// <summary>Horizontal timebase mode. DS1000Z Programming Guide, :TIMebase:MODE.</summary>
public enum TimebaseMode
{
    /// <summary>MAIN — normal YT.</summary>
    Normal,
    /// <summary>XY — the manual's A-vs-B display, and the primary tuning view of 5-14.</summary>
    XY,
    /// <summary>ROLL.</summary>
    Roll,
}

/// <summary>Channel coupling.</summary>
public enum ScopeCoupling
{
    Dc,
    Ac,
    Ground,
}

/// <summary>
/// One captured channel, scaled to volts.
/// </summary>
/// <param name="Channel">Channel number, 1-4.</param>
/// <param name="Volts">Sample values in volts.</param>
/// <param name="SecondsPerSample">Time between samples.</param>
/// <param name="StartSeconds">Time of the first sample, relative to the trigger.</param>
public sealed record ScopeWaveform(
    int Channel,
    IReadOnlyList<double> Volts,
    double SecondsPerSample,
    double StartSeconds)
{
    /// <summary>Time of sample <paramref name="index"/>.</summary>
    public double TimeAt(int index) => StartSeconds + index * SecondsPerSample;
}

/// <summary>
/// Driver for the Rigol DS1104Z oscilloscope over LAN.
///
/// <para>Source: <b>MSO1000Z/DS1000Z Programming Guide</b> (`DS1000Z_Programming Guide_EN.pdf` in
/// the local manual library). Standard Rigol SCPI, so unlike the HP instruments this one does
/// answer <c>*IDN?</c>.</para>
///
/// <para>This is the primary tuning view. Setup S4 puts the 8473C detector on CH1 and the DUT's
/// rear-panel SWEEP OUTPUT on CH2 in XY mode, reproducing the manual's A-vs-B display — except
/// that here the trace is read back as numbers rather than eyeballed. <b>Update rate matters more
/// here than anywhere else in the project</b>, because somebody is holding a tuning tool and
/// closing the loop by hand.</para>
///
/// <para>M2-02 originally asked for a 0.5 s refresh. That is 2 Hz, and it is too slow: past about
/// half a second an operator can no longer connect what their hand did to what the trace did, so
/// they overshoot and hunt. The DUT sweeps in 50 ms (5-16 steps 24 and 90), which puts the real
/// ceiling at 20 Hz, so the capture path is built for that instead - see
/// <see cref="ReadWaveformStreaming"/>. What this link actually achieves is unmeasured and is a
/// job for the M0-30 benchmark, not for anybody's estimate.</para>
/// </summary>
public sealed class RigolDs1104Z : IInstrument
{
    private readonly IInstrumentLink _link;

    public string Role { get; }
    public string Model => "Rigol DS1104Z";
    public IInstrumentLink Link => _link;

    public RigolDs1104Z(IInstrumentLink link, string role = "oscilloscope")
    {
        _link = link ?? throw new ArgumentNullException(nameof(link));
        Role = role;
    }

    private static string Num(double v) => v.ToString("0.#########", CultureInfo.InvariantCulture);

    private static void ValidateChannel(int channel)
    {
        if (channel is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(channel), channel, "The DS1104Z has channels 1-4.");
    }

    // --- Display and channels ---------------------------------------------------------------

    /// <summary>
    /// Sets the horizontal timebase mode. <see cref="TimebaseMode.XY"/> is the A-vs-B display
    /// 5-14 is built around. Guide's own example: <c>:TIMebase:MODE XY</c>.
    /// </summary>
    public void SetTimebaseMode(TimebaseMode mode)
    {
        _link.Write($":TIMebase:MODE {mode switch
        {
            TimebaseMode.Normal => "MAIN",
            TimebaseMode.XY => "XY",
            TimebaseMode.Roll => "ROLL",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        }}");

        InvalidateWaveformScaling();
    }

    public void SetChannelEnabled(int channel, bool on)
    {
        ValidateChannel(channel);
        _link.Write($":CHANnel{channel}:DISPlay {(on ? "ON" : "OFF")}");
        InvalidateWaveformScaling();
    }

    /// <summary>Vertical scale, volts per division.</summary>
    public void SetChannelScale(int channel, double voltsPerDivision)
    {
        ValidateChannel(channel);
        _link.Write($":CHANnel{channel}:SCALe {Num(voltsPerDivision)}");
        InvalidateWaveformScaling();
    }

    /// <summary>Vertical offset, volts.</summary>
    public void SetChannelOffset(int channel, double volts)
    {
        ValidateChannel(channel);
        _link.Write($":CHANnel{channel}:OFFSet {Num(volts)}");
        InvalidateWaveformScaling();
    }

    public void SetChannelCoupling(int channel, ScopeCoupling coupling)
    {
        ValidateChannel(channel);
        _link.Write($":CHANnel{channel}:COUPling {coupling switch
        {
            ScopeCoupling.Dc => "DC",
            ScopeCoupling.Ac => "AC",
            ScopeCoupling.Ground => "GND",
            _ => throw new ArgumentOutOfRangeException(nameof(coupling), coupling, null),
        }}");

        InvalidateWaveformScaling();
    }

    /// <summary>
    /// Probe attenuation ratio. Setup S4 uses 1x for the direct BNC connections; S5 uses 10x to
    /// match the 10:1 probes on the internal test points. Getting this wrong scales every reading.
    /// </summary>
    public void SetProbeRatio(int channel, double ratio)
    {
        ValidateChannel(channel);
        if (ratio <= 0)
            throw new ArgumentOutOfRangeException(nameof(ratio), ratio, "Probe ratio must be positive.");

        _link.Write($":CHANnel{channel}:PROBe {Num(ratio)}");
        InvalidateWaveformScaling();
    }

    /// <summary>Horizontal scale, seconds per division.</summary>
    public void SetTimebaseScale(double secondsPerDivision)
    {
        _link.Write($":TIMebase:MAIN:SCALe {Num(secondsPerDivision)}");
        InvalidateWaveformScaling();
    }

    /// <summary>External trigger on a rising edge — the DUT's sweep trigger in S4 and S5.</summary>
    public void SetExternalTrigger(double levelVolts, bool rising = true)
    {
        _link.Write(":TRIGger:MODE EDGE");
        _link.Write(":TRIGger:EDGe:SOURce EXT");
        _link.Write($":TRIGger:EDGe:SLOPe {(rising ? "POSitive" : "NEGative")}");
        _link.Write($":TRIGger:EDGe:LEVel {Num(levelVolts)}");
    }

    // --- Capture ------------------------------------------------------------------------------

    /// <summary>
    /// The preamble fields needed to turn raw BYTE samples into volts and seconds.
    /// </summary>
    /// <param name="Channel">The channel this preamble was read for.</param>
    /// <param name="Points">Preamble <c>points</c> - how many samples a frame carries.</param>
    /// <param name="SecondsPerSample">Preamble <c>xincrement</c>.</param>
    /// <param name="StartSeconds">Preamble <c>xorigin</c>.</param>
    /// <param name="VoltsPerCode">Preamble <c>yincrement</c>.</param>
    /// <param name="CodeOrigin">Preamble <c>yorigin</c>.</param>
    /// <param name="CodeReference">Preamble <c>yreference</c>.</param>
    private sealed record WaveformScaling(
        int Channel,
        int Points,
        double SecondsPerSample,
        double StartSeconds,
        double VoltsPerCode,
        double CodeOrigin,
        double CodeReference);

    private WaveformScaling? _scaling;

    /// <summary>
    /// Discards the cached preamble, forcing the next capture to re-read it.
    ///
    /// <para>Every setting that changes what a raw sample <i>means</i> calls this. That is the
    /// whole risk of caching the preamble: a stale <c>yincrement</c> after a vertical-scale change
    /// gives a trace wrong by a constant factor, which looks completely plausible and is exactly
    /// the kind of error nobody catches at a bench. Re-reading ten bytes is cheaper than that.</para>
    /// </summary>
    public void InvalidateWaveformScaling() => _scaling = null;

    /// <summary>
    /// Reads one channel as volts, re-reading the preamble first.
    ///
    /// <para>This is the one-shot path. <see cref="ReadWaveformStreaming"/> is the live-view one.</para>
    /// </summary>
    public ScopeWaveform ReadWaveform(int channel)
    {
        ValidateChannel(channel);

        _scaling = ReadScaling(channel);
        return ReadFrame(_scaling);
    }

    /// <summary>
    /// Reads one channel as volts reusing the cached preamble - one bus round trip per frame once
    /// warm.
    ///
    /// <para><b>Why this exists.</b> The S4 view is watched while somebody turns a pot, and a human
    /// closing that loop needs the trace to track their hand: under about 100 ms feels coupled, and
    /// past about 500 ms they can no longer correlate the two and start hunting. The DUT sweeps in
    /// 50 ms (5-16 steps 24 and 90), so 20 Hz is the ceiling worth aiming at, and the transport
    /// should not be the thing that stops us reaching it.</para>
    ///
    /// <para>The one-shot path costs five round trips a frame. This costs one.</para>
    /// </summary>
    public ScopeWaveform ReadWaveformStreaming(int channel)
    {
        ValidateChannel(channel);

        if (_scaling is null || _scaling.Channel != channel)
            _scaling = ReadScaling(channel);

        return ReadFrame(_scaling);
    }

    /// <summary>
    /// Sets the transfer up and reads the preamble, which the guide documents as
    /// <c>format,type,points,count,xincrement,xorigin,xreference,yincrement,yorigin,yreference</c>.
    /// </summary>
    private WaveformScaling ReadScaling(int channel)
    {
        _link.Write($":WAVeform:SOURce CHANnel{channel}");
        _link.Write(":WAVeform:MODE NORMal");

        // BYTE, not ASCII: one byte a sample instead of ~14 characters of text, so a 1200-point
        // frame is 1.2 kB rather than ~17 kB, with no decimal parsing at the far end. The scaling
        // that ASCII applied for us comes out of the preamble below instead.
        _link.Write(":WAVeform:FORMat BYTE");

        var preamble = _link.Query(":WAVeform:PREamble?")
            .Split(',', StringSplitOptions.TrimEntries);

        if (preamble.Length < 10)
            throw new InvalidOperationException(
                $"DS1104Z preamble had {preamble.Length} fields, expected 10. Got: "
                + string.Join(",", preamble));

        var points = (int)ParseField(preamble[2], "points");

        if (points <= 0)
            throw new InvalidOperationException(
                $"DS1104Z reported {points} points in its preamble, so there is no frame to read.");

        return new WaveformScaling(
            channel,
            points,
            SecondsPerSample: ParseField(preamble[4], "xincrement"),
            StartSeconds: ParseField(preamble[5], "xorigin"),
            VoltsPerCode: ParseField(preamble[7], "yincrement"),
            CodeOrigin: ParseField(preamble[8], "yorigin"),
            CodeReference: ParseField(preamble[9], "yreference"));
    }

    /// <summary>
    /// Reads one frame and scales it. A single round trip: the read is sized from the preamble's
    /// point count plus room for the block header and terminator.
    /// </summary>
    private ScopeWaveform ReadFrame(WaveformScaling scaling)
    {
        _link.Write(":WAVeform:DATA?");

        var raw = StripBlockHeader(_link.ReadBytes(scaling.Points + BlockOverheadBytes));

        if (raw.Count == 0)
            throw new InvalidOperationException("DS1104Z returned an empty waveform.");

        var volts = new double[raw.Count];

        for (var i = 0; i < raw.Count; i++)
            volts[i] = (raw[i] - scaling.CodeOrigin - scaling.CodeReference) * scaling.VoltsPerCode;

        return new ScopeWaveform(
            scaling.Channel, volts, scaling.SecondsPerSample, scaling.StartSeconds);
    }

    /// <summary>
    /// Room for the IEEE 488.2 definite-length header (<c>#9</c> plus nine digits) and a trailing
    /// terminator, so one read asks for the whole block.
    /// </summary>
    private const int BlockOverheadBytes = 16;

    private static double ParseField(string text, string name) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new InvalidOperationException($"DS1104Z preamble {name} was '{text}'.");

    /// <summary>
    /// Strips the IEEE 488.2 definite-length block header (<c>#9000001200</c> style) and returns
    /// just the payload.
    ///
    /// <para>The byte count in the header is authoritative. The read buffer is deliberately
    /// generous, so what comes back is normally longer than the data - a trailing terminator, and
    /// whatever padding the transport adds - and trusting the buffer length instead would append
    /// phantom samples to the end of every trace.</para>
    /// </summary>
    internal static ArraySegment<byte> StripBlockHeader(byte[] response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.Length < 2 || response[0] != (byte)'#' || !char.IsAsciiDigit((char)response[1]))
            return new ArraySegment<byte>(response);

        var headerDigits = response[1] - '0';

        // '#0' is the indefinite-length form: everything after the header is payload.
        if (headerDigits == 0)
            return new ArraySegment<byte>(response, 2, response.Length - 2);

        var start = 2 + headerDigits;

        if (start > response.Length)
            return new ArraySegment<byte>(response);

        var declared = Encoding.ASCII.GetString(response, 2, headerDigits);

        // A header that will not parse, or that claims more than arrived, falls back to "the rest
        // of the buffer" rather than throwing: a short read is worth reporting as a short trace.
        if (!int.TryParse(declared, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length)
            || length < 0 || start + length > response.Length)
            length = response.Length - start;

        return new ArraySegment<byte>(response, start, length);
    }

    /// <summary>
    /// Captures the screen as a PNG. <c>:DISPlay:DATA?</c> returns a definite-length block; the
    /// caller gets the raw bytes including the header, which the reports project strips.
    /// </summary>
    public byte[] CaptureScreenPng(int maxBytes = 2_000_000)
    {
        _link.Write(":DISPlay:DATA? ON,OFF,PNG");
        return _link.ReadBytes(maxBytes);
    }

    /// <summary>
    /// Configures the S4 XY view: detector on <paramref name="detectorChannel"/> against the DUT
    /// sweep ramp on <paramref name="sweepChannel"/>.
    /// </summary>
    public void ConfigureXyView(
        int detectorChannel, int sweepChannel,
        double detectorVoltsPerDivision, double sweepVoltsPerDivision)
    {
        SetChannelEnabled(detectorChannel, true);
        SetChannelEnabled(sweepChannel, true);

        // Both direct BNC connections in S4, so 1x. The detector output is negative-going and the
        // sweep ramp is 0-10 V, so they need very different scales.
        SetProbeRatio(detectorChannel, 1);
        SetProbeRatio(sweepChannel, 1);
        SetChannelCoupling(detectorChannel, ScopeCoupling.Dc);
        SetChannelCoupling(sweepChannel, ScopeCoupling.Dc);
        SetChannelScale(detectorChannel, detectorVoltsPerDivision);
        SetChannelScale(sweepChannel, sweepVoltsPerDivision);

        SetTimebaseMode(TimebaseMode.XY);
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
            return new ProbeResult(
                Role, Model, _link.ResourceName, Responded: false,
                Detail: $"{ex.Message} (this instrument is on LAN, not GPIB — check the address "
                        + "has not moved off the configured one.)");
        }
    }

    public void Dispose() => _link.Dispose();
}
