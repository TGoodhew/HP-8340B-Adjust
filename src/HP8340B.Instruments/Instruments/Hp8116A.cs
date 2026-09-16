using System.Globalization;
using HP8340B.Instruments.Visa;

namespace HP8340B.Instruments;

/// <summary>
/// Driver for the HP 8116A Programmable Pulse/Function Generator.
///
/// <para>Source: <b>HP 8116A Operating and Service Manual</b> (`8116A-OSM-002.pdf` in the local
/// manual library). The command language is mnemonic-plus-value-plus-unit, e.g.
/// <c>FRQ 1 KHZ</c>, <c>WID 100 NS</c>, <c>AMP 1 V</c>, with <c>M1</c> and <c>M2</c> selecting
/// mode and <c>D0</c>/<c>D1</c> enabling and disabling the output.</para>
///
/// <para>This is the pulse source for 4-11 to 4-14 and 5-18, standing in for the 8012B of
/// Table 4-2. <b>A pulse-fidelity test is only as good as the source's edges</b>, which is why
/// <see cref="MeetsTable42PulseRequirement"/> exists and is checked rather than assumed.</para>
/// </summary>
public sealed class Hp8116A : IInstrument
{
    // --- Specifications, all from the manual --------------------------------------------------

    /// <summary>
    /// Transition time, 10% to 90%. Section 4-15 Pulse Characteristics, SPECIFICATION:
    /// "Transition times (10 % to 90 %): &lt; 6 ns". The adjustment procedure tightens this to
    /// &lt; 5.6 ns, so a correctly adjusted instrument has margin on its own specification.
    /// </summary>
    public const double TransitionTimeSeconds = 6e-9;

    /// <summary>Preshoot/overshoot/ringing, same specification block: within 5%.</summary>
    public const double OvershootPercent = 5.0;

    /// <summary>Minimum programmable pulse width. Manual: "Pulse Width (pulse mode) Range: 10.0 ns to 999 ms".</summary>
    public const double MinWidthSeconds = 10e-9;
    public const double MaxWidthSeconds = 0.999;

    /// <summary>
    /// The manual's own constraint on width against period: "Max. Width: Period - 10 ns". Asking
    /// for more produces a pulse the generator cannot make.
    /// </summary>
    public const double WidthPeriodMarginSeconds = 10e-9;

    /// <summary>Frequency range: 1 mHz to 50 MHz.</summary>
    public const double MinHz = 1e-3;
    public const double MaxHz = 50e6;

    /// <summary>
    /// Amplitude range, from the two level windows: 10.0 mVpp to 16.0 Vpp into 50 ohms.
    /// </summary>
    public const double MinAmplitudeVpp = 10e-3;
    public const double MaxAmplitudeVpp = 16.0;

    /// <summary>Output impedance: "50 Ohm +/- 5 %".</summary>
    public const double OutputImpedanceOhms = 50.0;

    private readonly IInstrumentLink _link;

    public string Role { get; }
    public string Model => "HP 8116A";
    public IInstrumentLink Link => _link;

    public Hp8116A(IInstrumentLink link, string role = "pulse-generator")
    {
        _link = link ?? throw new ArgumentNullException(nameof(link));
        Role = role;
    }

    private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// Whether this generator satisfies what Table 4-2 asks of a pulse source for 4-11 to 4-14:
    /// a width of 100 ns or less and a rise time of 10 ns or less.
    ///
    /// <para>The issue for this driver asked for that to be verified against the 8116A's own
    /// manual before the pulse tests rely on it, rather than assumed. It is verified, and it
    /// passes with margin: 10 ns minimum width against the 100 ns asked for, and a specified
    /// &lt; 6 ns transition time against the 10 ns asked for.</para>
    /// </summary>
    public static bool MeetsTable42PulseRequirement(
        double requiredWidthSeconds = 100e-9, double requiredRiseSeconds = 10e-9) =>
        MinWidthSeconds <= requiredWidthSeconds && TransitionTimeSeconds <= requiredRiseSeconds;

    /// <summary>Device clear, then pulse mode with the output off.</summary>
    public void Initialize()
    {
        _link.Clear();
        SetOutputEnabled(false);
        SelectPulseMode();
    }

    /// <summary>Normal (continuous) mode — <c>M1</c>.</summary>
    public void SelectPulseMode() => _link.Write("M1");

    /// <summary>Triggered mode — <c>M2</c>. 5-18 uses this.</summary>
    public void SelectTriggeredMode() => _link.Write("M2");

    /// <summary>
    /// Repetition frequency — <c>FRQ</c>. The manual programs period as frequency; for a pulse
    /// train the period is its reciprocal.
    /// </summary>
    public void SetFrequencyHz(double hz)
    {
        if (hz is < MinHz or > MaxHz)
            throw new ArgumentOutOfRangeException(
                nameof(hz), hz, $"The 8116A covers {MinHz} Hz to {MaxHz / 1e6:0.#} MHz.");

        _link.Write($"FRQ {Num(hz)} HZ");
    }

    /// <summary>Repetition period, as the pulse tests state it. Sent as its reciprocal frequency.</summary>
    public void SetPeriodSeconds(double seconds)
    {
        if (seconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "Period must be positive.");

        SetFrequencyHz(1.0 / seconds);
    }

    /// <summary>
    /// Pulse width — <c>WID</c>. Refuses a width the generator cannot produce, including the
    /// manual's own "Max. Width: Period - 10 ns" constraint when a period is supplied.
    /// </summary>
    public void SetWidthSeconds(double seconds, double? periodSeconds = null)
    {
        if (seconds is < MinWidthSeconds or > MaxWidthSeconds)
            throw new ArgumentOutOfRangeException(
                nameof(seconds), seconds,
                $"The 8116A's pulse width range is {MinWidthSeconds * 1e9:0.#} ns to "
                + $"{MaxWidthSeconds * 1e3:0.#} ms.");

        if (periodSeconds is { } period && seconds > period - WidthPeriodMarginSeconds)
            throw new ArgumentOutOfRangeException(
                nameof(seconds), seconds,
                $"The manual's constraint is \"Max. Width: Period - 10 ns\". With a "
                + $"{period * 1e9:0.#} ns period the widest pulse is "
                + $"{(period - WidthPeriodMarginSeconds) * 1e9:0.#} ns.");

        // Nanoseconds keep three significant digits available across the whole range, which is
        // the generator's own resolution.
        _link.Write($"WID {Num(seconds * 1e9)} NS");
    }

    /// <summary>Amplitude, volts peak to peak — <c>AMP</c>.</summary>
    public void SetAmplitudeVpp(double volts)
    {
        if (volts is < MinAmplitudeVpp or > MaxAmplitudeVpp)
            throw new ArgumentOutOfRangeException(
                nameof(volts), volts,
                $"The 8116A's amplitude range is {MinAmplitudeVpp * 1e3:0.#} mVpp to "
                + $"{MaxAmplitudeVpp:0.#} Vpp.");

        _link.Write($"AMP {Num(volts)} V");
    }

    /// <summary>Offset — <c>OFS</c>.</summary>
    public void SetOffsetVolts(double volts) => _link.Write($"OFS {Num(volts)} V");

    /// <summary>
    /// High and low levels — <c>HIL</c> and <c>LOL</c>. The manual notes the generator
    /// recalculates amplitude and offset from these, so the two ways of stating a level are
    /// alternatives, not a pair to be set together.
    /// </summary>
    public void SetLevels(double highVolts, double lowVolts)
    {
        if (highVolts <= lowVolts)
            throw new ArgumentException("High level must be above low level.", nameof(highVolts));

        _link.Write($"HIL {Num(highVolts)} V");
        _link.Write($"LOL {Num(lowVolts)} V");
    }

    /// <summary>
    /// A TTL-level pulse, which is what the 8340B's pulse input wants: low below 0.4 V, high
    /// above 2.4 V.
    /// </summary>
    public void SetTtlLevels() => SetLevels(highVolts: 4.0, lowVolts: 0.0);

    /// <summary>Duty cycle in percent — <c>DTY</c>. Sine, triangle and square only, not pulse mode.</summary>
    public void SetDutyCyclePercent(double percent)
    {
        if (percent is < 10 or > 90)
            throw new ArgumentOutOfRangeException(
                nameof(percent), percent,
                "Duty cycle range is 10% to 90% below 1 MHz, narrowing to 20-80% above it.");

        _link.Write($"DTY {Num(percent)} %");
    }

    /// <summary>Output on or off — <c>D0</c> enables, <c>D1</c> disables.</summary>
    public void SetOutputEnabled(bool on) => _link.Write(on ? "D0" : "D1");

    /// <summary>
    /// Sets up a pulse for the 4-11 to 4-14 family in one call, with the output enabled last.
    /// </summary>
    public void ConfigurePulse(double periodSeconds, double widthSeconds, bool ttlLevels = true)
    {
        SetOutputEnabled(false);
        SelectPulseMode();
        SetPeriodSeconds(periodSeconds);
        SetWidthSeconds(widthSeconds, periodSeconds);

        if (ttlLevels) SetTtlLevels();

        SetOutputEnabled(true);
    }

    public ProbeResult Probe()
    {
        try
        {
            var status = _link.SerialPoll();

            return new ProbeResult(
                Role, Model, _link.ResourceName,
                Responded: true,
                Identity: $"status byte 0x{status:X2}",
                ExternalReference: null,
                Detail: $"Pre-488.2, so no *IDN?. Specified transition time < "
                        + $"{TransitionTimeSeconds * 1e9:0.#} ns and minimum width "
                        + $"{MinWidthSeconds * 1e9:0.#} ns, both verified against its own manual "
                        + "and both inside what Table 4-2 asks for.");
        }
        catch (Exception ex)
        {
            return new ProbeResult(Role, Model, _link.ResourceName, Responded: false, Detail: ex.Message);
        }
    }

    public void Dispose() => _link.Dispose();
}
