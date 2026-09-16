using System.Globalization;
using HP8340B.Instruments.Visa;

namespace HP8340B.Instruments;

/// <summary>Which low-pass filter is in circuit. 8903B Table 3-6.</summary>
public enum AudioLowPass
{
    /// <summary>All off — <c>L0</c>. The manual: measurement bandwidth is then about 10 Hz to 750 kHz.</summary>
    Off,
    /// <summary>30 kHz — <c>L1</c>.</summary>
    Khz30,
    /// <summary>80 kHz — <c>L2</c>.</summary>
    Khz80,
}

/// <summary>
/// Which plug-in high-pass or bandpass filter is in circuit.
///
/// <para>The 8903B has two plug-in positions and the codes address the <b>position</b>, not the
/// filter: <c>H1</c> is whatever is in the left one and <c>H2</c> whatever is in the right. A
/// 400 Hz high-pass is option 010 in the left position or 050 in the right, so which code selects
/// it depends on how this particular unit is fitted.</para>
/// </summary>
public enum AudioHighPass
{
    /// <summary>All off — <c>H0</c>.</summary>
    Off,
    /// <summary>Whatever is in the left plug-in position — <c>H1</c>.</summary>
    LeftPlugIn,
    /// <summary>Whatever is in the right plug-in position — <c>H2</c>.</summary>
    RightPlugIn,
}

/// <summary>
/// Driver for the HP 8903B Audio Analyzer.
///
/// <para>Source: <b>HP 8903B Operation and Calibration Manual</b> (`8903b.pdf` in the local
/// manual library), <b>Table 3-6, Parameter to HP-IB Code Summary, p. 3-37</b>, and the Detailed
/// Operating Instructions for each function.</para>
///
/// <para><b>What this closes.</b> 5-5 step 48 wants a 100 kohm-in / 50 ohm-out active probe
/// (1121A class) to read the output of the A36 40 kHz low-pass at 20 kHz and 50 kHz while nulling
/// A36L7/L8 to at least 65 dB down. The 8903B's analyzer input is 100 kohm like the 1121A, it
/// reads true RMS level in dB relative to a stored reference, and it is on HP-IB. The manual
/// applies the two tones one at a time, so a broadband level meter is a valid stand-in and reading
/// below the −65 dB target proves the null conservatively.</para>
/// </summary>
public sealed class Hp8903B : IInstrument
{
    private readonly IInstrumentLink _link;

    public string Role { get; }
    public string Model => "HP 8903B";
    public IInstrumentLink Link => _link;

    /// <summary>
    /// Which plug-in position the 400 Hz high-pass is fitted in, if it is fitted at all.
    ///
    /// <para>Null means not fitted or not known. It cannot be discovered over the bus — the codes
    /// address the position, not the filter — so it is declared, and <see cref="SelectHighPass400"/>
    /// refuses rather than guessing.</para>
    /// </summary>
    public AudioHighPass? HighPass400Position { get; set; }

    public Hp8903B(IInstrumentLink link, string role = "audio-analyzer")
    {
        _link = link ?? throw new ArgumentNullException(nameof(link));
        Role = role;
    }

    private static string Num(double v) => v.ToString("0.#####", CultureInfo.InvariantCulture);

    private void Send(string command) => _link.Write(command);

    /// <summary>Device clear, then AC level with free-run triggering.</summary>
    public void Initialize()
    {
        _link.Clear();
        Send("M1");     // AC Level
        Send("T0");     // Free Run
        Send("AO");     // RMS detector — true RMS is the point of using this instrument
    }

    // --- Measurement mode -----------------------------------------------------------------------

    /// <summary>AC level — <c>M1</c>. True RMS between the HIGH and LOW inputs.</summary>
    public void SelectAcLevel() => Send("M1");

    /// <summary>DC level — <c>S1</c>.</summary>
    public void SelectDcLevel() => Send("S1");

    /// <summary>RMS detector — <c>AO</c>. The default, and what step 48 wants.</summary>
    public void SelectRmsDetector() => Send("AO");

    /// <summary>Average-responding detector — <c>A1</c>.</summary>
    public void SelectAverageDetector() => Send("A1");

    /// <summary>Logarithmic display — <c>LG</c>. dB, or dBm into 600 ohms when not in ratio.</summary>
    public void SelectLog() => Send("LG");

    /// <summary>Linear display — <c>LN</c>. Volts.</summary>
    public void SelectLinear() => Send("LN");

    // --- Ratio, which is the measurement step 48 actually wants -----------------------------------

    /// <summary>
    /// Stores the level now present as the 0 dB reference and switches to ratio in dB —
    /// <c>R1</c> then <c>LG</c>.
    ///
    /// <para>This is the whole reason the 8903B suits step 48: the step asks for a level
    /// <b>65 dB below a reference</b>, and the instrument does the subtraction itself rather than
    /// leaving two absolute readings to be differenced by hand.</para>
    /// </summary>
    public void StoreReferenceAndRatio()
    {
        Send("R1");
        Send("LG");
    }

    /// <summary>Ratio off — <c>R0</c>. Back to absolute level.</summary>
    public void RatioOff() => Send("R0");

    /// <summary>
    /// Enters an explicit ratio reference in volts rather than measuring one, then switches to dB.
    /// </summary>
    public void SetReferenceVolts(double volts)
    {
        if (volts <= 0)
            throw new ArgumentOutOfRangeException(nameof(volts), volts, "A reference must be positive.");

        Send($"R1{Num(volts)}VL");
        Send("LG");
    }

    // --- Filters ---------------------------------------------------------------------------------

    /// <summary>Selects a low-pass filter — <c>L0</c>, <c>L1</c> or <c>L2</c>.</summary>
    public void SelectLowPass(AudioLowPass filter) => Send(filter switch
    {
        AudioLowPass.Off => "L0",
        AudioLowPass.Khz30 => "L1",
        AudioLowPass.Khz80 => "L2",
        _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, null),
    });

    /// <summary>Selects a plug-in high-pass or bandpass filter — <c>H0</c>, <c>H1</c> or <c>H2</c>.</summary>
    public void SelectHighPass(AudioHighPass filter) => Send(filter switch
    {
        AudioHighPass.Off => "H0",
        AudioHighPass.LeftPlugIn => "H1",
        AudioHighPass.RightPlugIn => "H2",
        _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, null),
    });

    /// <summary>
    /// Selects the 400 Hz high-pass, if this unit has one and its position has been declared.
    ///
    /// <para>Refuses otherwise. <c>H1</c> selects whatever is in the left plug-in position, which
    /// on a differently fitted 8903B could be a CCITT or A-weighting bandpass — and quietly
    /// weighting a level measurement would change the answer without changing its appearance.</para>
    /// </summary>
    public void SelectHighPass400()
    {
        if (HighPass400Position is not { } position)
            throw new InvalidOperationException(
                "The 400 Hz high-pass position has not been declared, so it cannot be selected. "
                + "The 8903B's H1 and H2 codes address the plug-in POSITION, not the filter — "
                + "option 010 puts the 400 Hz high-pass in the left position and option 050 in the "
                + "right, and other units carry weighting bandpass filters in those same slots. "
                + "Set HighPass400Position once this unit's options are known, or leave the "
                + "high-pass out.");

        SelectHighPass(position);
    }

    /// <summary>
    /// The filter set 5-5 step 48 wants: the 80 kHz low-pass in, the 30 kHz one out.
    ///
    /// <para>The step reads 20 kHz and 50 kHz tones. The 30 kHz low-pass would reject the 50 kHz
    /// one outright and the measurement would read as a very good null, which is exactly the wrong
    /// way for a mistake to fail.</para>
    /// </summary>
    public void ConfigureForStep48()
    {
        SelectAcLevel();
        SelectRmsDetector();
        SelectLowPass(AudioLowPass.Khz80);
        SelectHighPass(AudioHighPass.Off);
    }

    // --- Source -----------------------------------------------------------------------------------

    /// <summary>Source frequency — <c>FA</c> with a units terminator.</summary>
    public void SetSourceFrequencyHz(double hz)
    {
        if (hz <= 0)
            throw new ArgumentOutOfRangeException(nameof(hz), hz, "Frequency must be positive.");

        Send($"FA{Num(hz)}HZ");
    }

    /// <summary>Source amplitude in volts — <c>AP</c>.</summary>
    public void SetSourceAmplitudeVolts(double volts)
    {
        if (volts < 0)
            throw new ArgumentOutOfRangeException(nameof(volts), volts, "Amplitude cannot be negative.");

        Send($"AP{Num(volts)}VL");
    }

    /// <summary>Source amplitude in millivolts — <c>AP</c> with <c>MV</c>.</summary>
    public void SetSourceAmplitudeMillivolts(double millivolts) =>
        Send($"AP{Num(millivolts)}MV");

    // --- Reading ------------------------------------------------------------------------------------

    /// <summary>
    /// Reads the right display — the measurement. The analyzer is a talker, so a bare read
    /// returns it; <c>RR</c> asks for it explicitly where the mode is ambiguous.
    /// </summary>
    public double ReadMeasurement()
    {
        var text = _link.Read().Trim();

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new InvalidOperationException(
                $"8903B returned '{text}', which is not a number. A reading of 0.000 kHz on the "
                + "left display usually means the input is in the stop band of a filter.");

        return value;
    }

    /// <summary>Reads the left display — the measured input frequency — with <c>RL</c>.</summary>
    public double ReadFrequencyHz()
    {
        var text = _link.Query("RL").Trim();

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var hz)
            ? hz
            : throw new InvalidOperationException($"8903B returned '{text}' for the left display.");
    }

    public ProbeResult Probe()
    {
        try
        {
            // Pre-488.2: no *IDN?. A serial poll is the only non-intrusive proof of life.
            var status = _link.SerialPoll();

            var detail = "Pre-488.2, so no *IDN?. ";

            detail += HighPass400Position is { } position
                ? $"400 Hz high-pass declared in the {position} position."
                : "400 Hz high-pass position NOT declared — H1/H2 address the plug-in position, "
                  + "not the filter, so it cannot be selected until this unit's options are known.";

            return new ProbeResult(
                Role, Model, _link.ResourceName,
                Responded: true,
                Identity: $"status byte 0x{status:X2}",
                ExternalReference: null,
                Detail: detail);
        }
        catch (Exception ex)
        {
            return new ProbeResult(Role, Model, _link.ResourceName, Responded: false, Detail: ex.Message);
        }
    }

    public void Dispose() => _link.Dispose();
}
