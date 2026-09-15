using System.Globalization;
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
/// here than anywhere else in the project</b>: M2-02 targets a 0.5 s refresh while somebody is
/// holding a tuning tool.</para>
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
    public void SetTimebaseMode(TimebaseMode mode) => _link.Write($":TIMebase:MODE {mode switch
    {
        TimebaseMode.Normal => "MAIN",
        TimebaseMode.XY => "XY",
        TimebaseMode.Roll => "ROLL",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    }}");

    public void SetChannelEnabled(int channel, bool on)
    {
        ValidateChannel(channel);
        _link.Write($":CHANnel{channel}:DISPlay {(on ? "ON" : "OFF")}");
    }

    /// <summary>Vertical scale, volts per division.</summary>
    public void SetChannelScale(int channel, double voltsPerDivision)
    {
        ValidateChannel(channel);
        _link.Write($":CHANnel{channel}:SCALe {Num(voltsPerDivision)}");
    }

    /// <summary>Vertical offset, volts.</summary>
    public void SetChannelOffset(int channel, double volts)
    {
        ValidateChannel(channel);
        _link.Write($":CHANnel{channel}:OFFSet {Num(volts)}");
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
    }

    /// <summary>Horizontal scale, seconds per division.</summary>
    public void SetTimebaseScale(double secondsPerDivision) =>
        _link.Write($":TIMebase:MAIN:SCALe {Num(secondsPerDivision)}");

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
    /// Reads one channel as volts.
    ///
    /// <para>Uses ASCII format, which returns the samples already scaled to volts. BYTE format is
    /// faster on the wire but needs the preamble's yincrement, yorigin and yreference applied by
    /// hand, and at the few-hundred-point screen depth this view uses, the difference does not
    /// reach the 0.5 s budget. Revisit only if M2-02 turns out to miss its target.</para>
    /// </summary>
    public ScopeWaveform ReadWaveform(int channel)
    {
        ValidateChannel(channel);

        _link.Write($":WAVeform:SOURce CHANnel{channel}");
        _link.Write(":WAVeform:MODE NORMal");
        _link.Write(":WAVeform:FORMat ASCii");

        // Preamble, so the samples can be placed in time:
        // <format>,<type>,<points>,<count>,<xincrement>,<xorigin>,<xreference>,
        // <yincrement>,<yorigin>,<yreference>
        var preamble = _link.Query(":WAVeform:PREamble?")
            .Split(',', StringSplitOptions.TrimEntries);

        if (preamble.Length < 10)
            throw new InvalidOperationException(
                $"DS1104Z preamble had {preamble.Length} fields, expected 10. Got: "
                + string.Join(",", preamble));

        var xIncrement = ParseField(preamble[4], "xincrement");
        var xOrigin = ParseField(preamble[5], "xorigin");

        var data = _link.Query(":WAVeform:DATA?");
        var volts = ParseAsciiBlock(data);

        return new ScopeWaveform(channel, volts, xIncrement, xOrigin);
    }

    private static double ParseField(string text, string name) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new InvalidOperationException($"DS1104Z preamble {name} was '{text}'.");

    /// <summary>
    /// Strips the IEEE 488.2 definite-length block header if present (<c>#9000001200</c> style)
    /// and parses the comma-separated values.
    /// </summary>
    internal static List<double> ParseAsciiBlock(string response)
    {
        var body = response.AsSpan().TrimStart();

        if (body.Length > 2 && body[0] == '#' && char.IsDigit(body[1]))
        {
            var headerDigits = body[1] - '0';
            var skip = 2 + headerDigits;
            if (body.Length > skip) body = body[skip..];
        }

        var values = new List<double>();

        foreach (var range in body.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var text = range.Trim();
            if (text.Length == 0) continue;

            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                throw new InvalidOperationException($"DS1104Z waveform contained '{text}'.");

            values.Add(v);
        }

        if (values.Count == 0)
            throw new InvalidOperationException("DS1104Z returned an empty waveform.");

        return values;
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
