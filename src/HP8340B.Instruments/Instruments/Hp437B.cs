using System.Globalization;
using HP8340B.Instruments.Model;
using HP8340B.Instruments.Visa;

namespace HP8340B.Instruments;

/// <summary>
/// An 8480-series power sensor: what it covers and what it will stand.
/// </summary>
/// <param name="Name">Model.</param>
/// <param name="MinHz">Lower frequency limit.</param>
/// <param name="MaxHz">Upper frequency limit.</param>
/// <param name="MinDbm">Bottom of the measuring range.</param>
/// <param name="MaxDbm">Top of the measuring range.</param>
/// <param name="Connector">Connector type, because it decides what it can be attached to.</param>
public sealed record PowerSensor(
    string Name,
    double MinHz,
    double MaxHz,
    double MinDbm,
    double MaxDbm,
    string Connector)
{
    /// <summary>
    /// Reference calibration factor, percent, from this sensor's own chart. Null until entered —
    /// the 437B's CAL step wants it and there is no sensible default.
    /// </summary>
    public double? ReferenceCalFactorPercent { get; set; }

    /// <summary>
    /// Frequency-dependent cal factors from this sensor's own chart (issue #6). Empty until
    /// entered: they are per-sensor and guessing them biases every reading.
    /// </summary>
    public List<CalFactor> CalFactors { get; } = [];

    /// <summary>Serial number, for the session record.</summary>
    public string? Serial { get; set; }

    /// <summary>True if this sensor covers <paramref name="hz"/>.</summary>
    public bool Covers(double hz) => hz >= MinHz && hz <= MaxHz;

    /// <summary>
    /// Cal factor at <paramref name="hz"/>, interpolated, as a percentage.
    /// </summary>
    public double CalFactorAt(double hz)
    {
        if (CalFactors.Count == 0)
            throw new InvalidOperationException(
                $"The {Name}'s calibration factors have not been entered. They are printed on the "
                + "sensor's own chart and differ from sensor to sensor — see issue #6. Without "
                + "them every reading is biased by an unknown few percent.");

        if (!Covers(hz))
            throw new ArgumentOutOfRangeException(
                nameof(hz), hz,
                $"The {Name} covers {MinHz / 1e6:0.#} MHz to {MaxHz / 1e9:0.###} GHz.");

        var points = CalFactors.OrderBy(c => c.FrequencyMHz).ToList();
        var mhz = hz / 1e6;

        if (mhz <= points[0].FrequencyMHz) return points[0].PercentCalFactor;
        if (mhz >= points[^1].FrequencyMHz) return points[^1].PercentCalFactor;

        for (var i = 1; i < points.Count; i++)
        {
            if (mhz > points[i].FrequencyMHz) continue;

            var (low, high) = (points[i - 1], points[i]);
            var fraction = (mhz - low.FrequencyMHz) / (high.FrequencyMHz - low.FrequencyMHz);

            return low.PercentCalFactor
                   + fraction * (high.PercentCalFactor - low.PercentCalFactor);
        }

        throw new InvalidOperationException("Unreachable: the frequency is inside the table.");
    }
}

/// <summary>
/// The 8480-series sensors this bench has or might borrow.
///
/// <para><b>These are methods, not static instances, on purpose.</b> A sensor carries per-unit
/// state — its serial number and the cal-factor chart printed on it — and a shared static
/// instance would let two sensors silently end up with one chart. Worse, <c>record</c>'s
/// <c>with</c> copies the reference to the cal-factor list rather than the list, so even an
/// apparent copy would alias the original. Every call here returns a fresh sensor.</para>
/// </summary>
public static class PowerSensors
{
    private const string Source =
        "HP 437B Operating Manual compatible-sensor list, and each sensor's own range.";

    /// <summary>
    /// HP 8481A, 10 MHz to 18 GHz, -30 to +20 dBm, Type-N. The only sensor on this bench that
    /// covers 10 to 50 MHz apart from the 478A thermistor.
    /// </summary>
    public static PowerSensor Hp8481A() => new("HP 8481A", 10e6, 18e9, -30, 20, "Type-N");

    /// <summary>
    /// HP 8485A, to 26.5 GHz, -30 to +20 dBm, 3.5 mm. <b>This is the one that makes band 4
    /// Spec.</b>
    /// </summary>
    public static PowerSensor Hp8485A() => new("HP 8485A", 30e6, 26.5e9, -30, 20, "3.5 mm");

    /// <summary>A fresh instance of every sensor this project knows about.</summary>
    public static IReadOnlyList<PowerSensor> All() => [Hp8481A(), Hp8485A()];

    /// <summary>Where these figures come from.</summary>
    public static string Citation => Source;
}

/// <summary>One power reading, with what it took to make it (repo rule 3).</summary>
/// <param name="Dbm">Power in dBm.</param>
/// <param name="Hz">Frequency the cal factor was taken at.</param>
/// <param name="Sensor">Which sensor produced it.</param>
/// <param name="CalFactorPercent">The cal factor applied.</param>
/// <param name="Traceability">How far it can be trusted.</param>
/// <param name="Note">Anything qualifying it.</param>
public sealed record PowerReading(
    double Dbm,
    double Hz,
    string Sensor,
    double CalFactorPercent,
    TraceabilityClass Traceability,
    string? Note = null)
{
    /// <summary>The same reading in watts.</summary>
    public double Watts => Math.Pow(10, Dbm / 10.0) / 1000.0;
}

/// <summary>
/// Driver for the HP 437B power meter.
///
/// <para>Source: <b>HP 437B Operating Manual</b> (`437B-UM.pdf` in the local manual library).
/// Codes come from <see cref="Hp437BCommands"/>, every one cited to Table 3-5.</para>
///
/// <para><b>The second automatable absolute-power path.</b> Unlike the 432A, which has no HP-IB
/// and is read through the multimeter, this one is on the bus. With an 8481A it covers 10 MHz to
/// 18 GHz and with an 8485A it reaches 26.5 GHz — which is what makes 4-5 band 4 and 5-14 step 30
/// <c>Spec</c> rather than <c>Relative</c>.</para>
/// </summary>
public sealed class Hp437B : IInstrument
{
    /// <summary>
    /// Repo rule 5's refusal threshold with an 848x sensor selected: +17 dBm. The 8481A and 8485A
    /// are specified to +20 dBm, so this leaves 3 dB of margin against a mis-set level.
    /// </summary>
    public const double RefuseAboveDbm = 17.0;

    private readonly IInstrumentLink _link;

    public string Role { get; }
    public string Model => "HP 437B";
    public IInstrumentLink Link => _link;

    /// <summary>Which sensor is plugged in. Its range decides what can be claimed.</summary>
    public PowerSensor Sensor { get; set; } = PowerSensors.Hp8481A();

    /// <summary>Characterised pad in front of the sensor, dB. Declared in config, added back in.</summary>
    public double PadDb { get; set; }

    /// <summary>Settle time after zeroing. The 437B's zero routine takes several seconds.</summary>
    public TimeSpan ZeroSettle { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Settle time after calibrating.</summary>
    public TimeSpan CalibrateSettle { get; set; } = TimeSpan.FromSeconds(5);

    public Hp437B(IInstrumentLink link, string role = "power-meter")
    {
        _link = link ?? throw new ArgumentNullException(nameof(link));
        Role = role;
    }

    private static string Num(double v, string f = "0.##") =>
        v.ToString(f, CultureInfo.InvariantCulture);

    private void Send(string command) => _link.Write(command);

    /// <summary>Device clear then <c>PR</c> preset. Preset leaves the reference oscillator off.</summary>
    public void Preset()
    {
        _link.Clear();
        Send(Hp437BCommands.Require("PR"));
    }

    // --- Zero and calibrate ---------------------------------------------------------------------

    /// <summary>
    /// Zeroes the sensor — <c>ZE</c>, with the reference oscillator explicitly off first.
    ///
    /// <para>The manual's procedure (section 3-9) has the sensor connected to POWER REF before
    /// zeroing, which is only safe because PRESET leaves the oscillator off. Sending <c>OC0</c>
    /// makes that explicit rather than depending on the meter's state: zeroing with power on the
    /// sensor is a silent error that biases everything afterwards.</para>
    /// </summary>
    public void ZeroSensor()
    {
        Send(Hp437BCommands.Require("OC0"));
        Send(Hp437BCommands.Require("ZE"));

        if (ZeroSettle > TimeSpan.Zero) Thread.Sleep(ZeroSettle);
    }

    /// <summary>
    /// Calibrates against the 437B's own 50 MHz 1 mW reference — the manual's section 3-9
    /// procedure: zero, then CAL, then the sensor's REF CAL FACTOR, then ENTER.
    ///
    /// <para>Refuses without the reference cal factor, because the meter would otherwise be
    /// calibrated against whatever was last entered.</para>
    /// </summary>
    public void CalibrateSensor()
    {
        if (Sensor.ReferenceCalFactorPercent is not { } reference)
            throw new InvalidOperationException(
                $"The {Sensor.Name}'s REF CAL FACTOR has not been entered. It is printed on the "
                + "sensor and the manual's calibration procedure (section 3-9, step 4) asks for it "
                + "by name. Calibrating without it would use whatever the meter last had.");

        Send(Hp437BCommands.Require("OC1"));
        Send($"{Hp437BCommands.Require("CL")}{Num(reference)}{Hp437BCommands.Require("EN")}");

        if (CalibrateSettle > TimeSpan.Zero) Thread.Sleep(CalibrateSettle);

        Send(Hp437BCommands.Require("OC0"));
    }

    /// <summary>
    /// Enters the cal factor for <paramref name="hz"/> — <c>KB</c>, value, <c>EN</c>. The 437B
    /// applies it to every reading until it is changed.
    /// </summary>
    public void SetCalFactorFor(double hz)
    {
        var factor = Sensor.CalFactorAt(hz);
        Send($"{Hp437BCommands.Require("KB")}{Num(factor)}{Hp437BCommands.Require("EN")}");
    }

    /// <summary>Tells the meter the measurement frequency — <c>FR</c>, value, units.</summary>
    public void SetFrequencyHz(double hz)
    {
        if (!Sensor.Covers(hz))
            throw new ArgumentOutOfRangeException(
                nameof(hz), hz,
                $"The {Sensor.Name} covers {Sensor.MinHz / 1e6:0.#} MHz to "
                + $"{Sensor.MaxHz / 1e9:0.###} GHz. Reading it outside that is not a measurement.");

        Send($"{Hp437BCommands.Require("FR")}{Num(hz / 1e6, "0.###")}"
             + $"{Hp437BCommands.Require("MZ")}");
    }

    /// <summary>Selects dBm — <c>LG</c>.</summary>
    public void SelectDbm() => Send(Hp437BCommands.Require("LG"));

    /// <summary>Selects watts — <c>LN</c>.</summary>
    public void SelectWatts() => Send(Hp437BCommands.Require("LN"));

    /// <summary>Automatic ranging — <c>RA</c>.</summary>
    public void SetAutoRange() => Send(Hp437BCommands.Require("RA"));

    /// <summary>
    /// Manual filter (averaging) — <c>FM</c>, count, <c>EN</c>. More averages, less noise: the
    /// manual's own table gives 6.0% noise at one average and 0.5% at thirty-two.
    /// </summary>
    public void SetAverages(int count)
    {
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count), count, "At least one average.");

        Send($"{Hp437BCommands.Require("FM")}{count}{Hp437BCommands.Require("EN")}");
    }

    // --- Reading ----------------------------------------------------------------------------------

    /// <summary>Reads the displayed value. The meter is a talker.</summary>
    public double ReadValue()
    {
        var text = _link.Read().Trim();

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new InvalidOperationException(
                $"437B returned '{text}', which is not a number. The meter is a talker and returns "
                + "its reading directly.");

        return value;
    }

    /// <summary>
    /// Sets the frequency and cal factor, reads the power, and records everything needed to
    /// defend the number later.
    /// </summary>
    public PowerReading ReadPower(double hz)
    {
        SetFrequencyHz(hz);
        SetCalFactorFor(hz);
        SelectDbm();
        Send(Hp437BCommands.Require("TR1"));

        var dbm = ReadValue() + PadDb;

        return new PowerReading(
            dbm, hz, Sensor.Name, Sensor.CalFactorAt(hz),
            TraceabilityAt(hz, Sensor),
            PadDb != 0 ? $"Characterised pad {PadDb:0.##} dB added back." : null);
    }

    // --- Guards --------------------------------------------------------------------------------------

    /// <summary>
    /// Refuses a DUT level that would damage the sensor. Repo rule 5: above +17 dBm with an 848x
    /// sensor selected, unless a pad has actually been characterised.
    /// </summary>
    public void GuardSensorLevel(double dutOutputDbm, bool padDeclaredAndCharacterised)
    {
        var atSensor = dutOutputDbm - (padDeclaredAndCharacterised ? PadDb : 0);

        if (atSensor > RefuseAboveDbm)
            throw new InvalidOperationException(
                $"Refusing: {atSensor:+0.0;-0.0} dBm would reach the {Sensor.Name} (DUT at "
                + $"{dutOutputDbm:+0.0;-0.0} dBm"
                + (padDeclaredAndCharacterised ? $", characterised pad {PadDb:0.#} dB" : ", no characterised pad")
                + $"). Repo rule 5 refuses above {RefuseAboveDbm:+0.0} dBm with an 848x sensor. "
                + "Declare a characterised pad, or lower the level.");
    }

    /// <summary>
    /// The traceability class a reading at <paramref name="hz"/> can claim with
    /// <paramref name="sensor"/> fitted.
    ///
    /// <para><b>This is what stops a band-4 result being reported as Spec on an 18 GHz sensor.</b>
    /// An 8481A stops at 18 GHz; reading it at 24 GHz is not a measurement, it is a number.</para>
    /// </summary>
    public static TraceabilityClass TraceabilityAt(double hz, PowerSensor sensor)
    {
        ArgumentNullException.ThrowIfNull(sensor);

        return sensor.Covers(hz) ? TraceabilityClass.Spec : TraceabilityClass.NotPossible;
    }

    /// <summary>
    /// Whether the fitted sensor can support a <c>Spec</c> claim anywhere in
    /// <paramref name="band"/>.
    /// </summary>
    public bool CanClaimSpecIn(BandId band)
    {
        var edges = Bands.Get(band);
        return Sensor.Covers(edges.StartGHz * 1e9) && Sensor.Covers(edges.StopGHz * 1e9);
    }

    public ProbeResult Probe()
    {
        try
        {
            var identity = _link.Query(Hp437BCommands.Require("ID")).Trim();

            var detail = $"Sensor: {Sensor.Name} ({Sensor.MinHz / 1e6:0.#} MHz - "
                         + $"{Sensor.MaxHz / 1e9:0.###} GHz). ";

            detail += Sensor.CalFactors.Count == 0
                ? "Cal factors NOT entered — see issue #6. "
                : $"{Sensor.CalFactors.Count} cal-factor points entered. ";

            detail += CanClaimSpecIn(BandId.Band4)
                ? "Reaches band 4."
                : "Does NOT reach band 4 — a band-4 result on this sensor cannot be Spec.";

            return new ProbeResult(
                Role, Model, _link.ResourceName,
                Responded: !string.IsNullOrWhiteSpace(identity),
                Identity: identity,
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
