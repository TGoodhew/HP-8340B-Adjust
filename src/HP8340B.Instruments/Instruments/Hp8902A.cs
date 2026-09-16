using System.Globalization;
using HP8340B.Instruments.Visa;

namespace HP8340B.Instruments;

/// <summary>A sensor cal factor: percentage at a frequency.</summary>
/// <param name="FrequencyMHz">Frequency in MHz.</param>
/// <param name="PercentCalFactor">Cal factor as a percentage, e.g. 97.4.</param>
public sealed record CalFactor(double FrequencyMHz, double PercentCalFactor);

/// <summary>
/// Driver for the HP 8902A Measuring Receiver, in RF-power mode with the 11792A sensor module,
/// and as a modulation analyzer for 4-15 and 4-16.
///
/// <para><b>Source: HP-Attenuator's `Hp8902A.cs`</b>, which is hardware-proven on this bench — it
/// is the 8902A driver Tony already uses for tuned-RF-level attenuator measurements. The codes
/// below are carried across from it with the same citations: `IP` preset, `M4` RF power, `C0`/`C1`
/// calibrator off/on, `ZR` zero, `T0`/`T3` trigger modes, `SP` special function, `MZ`/`CF`
/// terminators, `M1`/`M2` AM/FM. The 8902A Operation and Calibration manual is also in the local
/// library.</para>
///
/// <para>This project uses a narrower slice than HP-Attenuator does: absolute RF power to 18 GHz
/// via the 11792A, tuned RF level above 18 GHz through the 11793A (Relative only), and AM/FM
/// demodulation. The attenuation-measurement machinery stays in HP-Attenuator.</para>
/// </summary>
public sealed class Hp8902A : IInstrument
{
    /// <summary>
    /// Refusal threshold with a thermocouple sensor selected. Repo rule 5: refuse above +17 dBm
    /// with the 11792A or an 848x sensor unless a characterised pad is declared.
    /// </summary>
    public const double RefuseAboveDbm = 17.0;

    /// <summary>The 11792A sensor module's range. Above this, readings are Relative at best.</summary>
    public const double SensorModuleMinHz = 50e6;
    public const double SensorModuleMaxHz = 18e9;

    private readonly IInstrumentLink _link;

    public string Role { get; }
    public string Model => "HP 8902A";
    public IInstrumentLink Link => _link;

    /// <summary>Settle time after zeroing. Carried from HP-Attenuator's proven value.</summary>
    public TimeSpan ZeroSettle { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Settle time after switching the calibrator on.</summary>
    public TimeSpan CalibrateSettle { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Characterised pad in front of the sensor, dB. Declared in config, added back in.</summary>
    public double PadDb { get; set; }

    public Hp8902A(IInstrumentLink link, string role = "measuring-receiver")
    {
        _link = link ?? throw new ArgumentNullException(nameof(link));
        Role = role;
    }

    private static string Num(double v, string f = "0.####") =>
        v.ToString(f, CultureInfo.InvariantCulture);

    private void Send(string command) => _link.Write(command);

    /// <summary>
    /// Device clear then preset. HP-Attenuator's note: device clear drops pending I/O and
    /// deasserts a latched SRQ; IP presets to free-run trigger with the SRQ mask set to HP-IB
    /// code errors only.
    /// </summary>
    public void Initialize()
    {
        _link.Clear();
        Send("IP");
    }

    /// <summary>Selects RF POWER mode — <c>M4</c>.</summary>
    public void SelectRfPower() => Send("M4");

    /// <summary>Selects AM demodulation — <c>M1</c>. Used by 4-15.</summary>
    public void SelectAm() => Send("M1");

    /// <summary>Selects FM demodulation — <c>M2</c>. Used by 4-16.</summary>
    public void SelectFm() => Send("M2");

    /// <summary>
    /// Zeroes the sensor with the calibrator OFF. There must be no RF on the sensor while
    /// zeroing, which is why <c>C0</c> comes first.
    /// </summary>
    public double ZeroSensor()
    {
        Send("M4");
        Send("C0");
        Send("ZR");

        if (ZeroSettle > TimeSpan.Zero) Thread.Sleep(ZeroSettle);
        return ReadWatts();
    }

    /// <summary>
    /// Calibrates against the 8902A's own 50 MHz 1 mW reference.
    ///
    /// <para><b>Refuses to complete if the reference does not read about 0 dBm.</b> Carried from
    /// HP-Attenuator: if the sensor is not actually on the CALIBRATION RF POWER OUTPUT, saving
    /// here would corrupt the sensor calibration — a silent error that poisons every later
    /// reading.</para>
    /// </summary>
    public double CalibrateSensor()
    {
        Send("M4");
        Send("C1");

        if (CalibrateSettle > TimeSpan.Zero) Thread.Sleep(CalibrateSettle);

        var watts = ReadWatts();
        var dbm = WattsToDbm(watts);

        if (dbm < -10.0)
        {
            Send("C0");
            throw new InvalidOperationException(
                $"8902A calibrator reference reads {dbm:0.0} dBm, not about 0 dBm. The sensor is "
                + "probably not on the CALIBRATION RF POWER OUTPUT. Refusing to save — saving now "
                + "would corrupt the sensor calibration and poison every later reading.");
        }

        Send("C0");
        return dbm;
    }

    /// <summary>
    /// Loads the cal-factor table for the sensor in use.
    ///
    /// <para>Carried from HP-Attenuator, including the detail that actually matters: the
    /// <b>reference cal factor</b> is a separate store entered value-only as
    /// <c>37.3SP{cf}CF</c> with no <c>MZ</c>, and it is what clears the instrument's Error 15
    /// ("no cal factors stored"). Each frequency/cal-factor pair then goes in as
    /// <c>37.3SP{freqMHz}MZ{cf}CF</c>. The table must be cleared first with <c>37.9SP</c>, and
    /// <c>T0</c> free-run is required for entries to commit.</para>
    /// </summary>
    public void LoadCalFactors(double referenceCalFactor, IReadOnlyList<CalFactor> table)
    {
        ArgumentNullException.ThrowIfNull(table);

        Send("M4T0");
        Send("27.0SP");                                  // select the normal (direct) table
        Send("37.9SP");                                  // clear it
        Send($"37.3SP{Num(referenceCalFactor, "0.00")}CF");  // reference CF — clears Error 15

        foreach (var entry in table)
            Send($"37.3SP{Num(entry.FrequencyMHz)}MZ{Num(entry.PercentCalFactor, "0.00")}CF");
    }

    /// <summary>
    /// Refuses a DUT level that would damage the sensor. Repo rule 5.
    /// </summary>
    /// <param name="dutOutputDbm">What the DUT is about to be set to.</param>
    /// <param name="padDeclaredAndCharacterised">
    /// True only when a pad has been measured at this frequency, not merely assumed from its label.
    /// </param>
    public void GuardSensorLevel(double dutOutputDbm, bool padDeclaredAndCharacterised)
    {
        var atSensor = dutOutputDbm - (padDeclaredAndCharacterised ? PadDb : 0);

        if (atSensor > RefuseAboveDbm)
            throw new InvalidOperationException(
                $"Refusing: {atSensor:+0.0;-0.0} dBm would reach the sensor (DUT at "
                + $"{dutOutputDbm:+0.0;-0.0} dBm"
                + (padDeclaredAndCharacterised ? $", characterised pad {PadDb:0.#} dB" : ", no characterised pad")
                + $"). Repo rule 5 refuses above {RefuseAboveDbm:+0.0} dBm with the 11792A or an "
                + "848x sensor. Declare a characterised pad, or lower the level.");
    }

    /// <summary>Reads the measurement in watts. The 8902A is a talker.</summary>
    public double ReadWatts()
    {
        var text = _link.Read().Trim();

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var watts))
            throw new InvalidOperationException($"8902A returned '{text}', which is not a number.");

        return watts;
    }

    /// <summary>
    /// Reads RF power in dBm, with any characterised pad added back so the value is the level at
    /// the DUT rather than at the sensor.
    /// </summary>
    public double ReadRfPowerDbm() => WattsToDbm(ReadWatts()) + PadDb;

    /// <summary>
    /// The traceability class a power reading at <paramref name="hz"/> can claim through this
    /// instrument. The 11792A covers 50 MHz to 18 GHz; above that the 11793A tuned-RF-level path
    /// is a relative measurement, not an absolute one.
    /// </summary>
    public static string TraceabilityAt(double hz) =>
        hz >= SensorModuleMinHz && hz <= SensorModuleMaxHz
            ? "Spec"
            : "Relative";

    /// <summary>Reads AM depth as a percentage — 4-15.</summary>
    public double ReadAmDepthPercent()
    {
        Send("M1");
        return ReadNumber();
    }

    /// <summary>Reads FM deviation in Hz — 4-16.</summary>
    public double ReadFmDeviationHz()
    {
        Send("M2");
        return ReadNumber();
    }

    private double ReadNumber()
    {
        var text = _link.Read().Trim();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new InvalidOperationException($"8902A returned '{text}', which is not a number.");
    }

    /// <summary>Watts to dBm. Zero or negative power has no dBm value; returns negative infinity.</summary>
    public static double WattsToDbm(double watts) =>
        watts <= 0 ? double.NegativeInfinity : 10.0 * Math.Log10(watts * 1000.0);

    public ProbeResult Probe()
    {
        try
        {
            // Pre-488.2: no *IDN?. A serial poll is the cheapest non-intrusive proof of life,
            // as it is for the DUT.
            var status = _link.SerialPoll();

            return new ProbeResult(
                Role, Model, _link.ResourceName,
                Responded: true,
                Identity: $"status byte 0x{status:X2}",
                ExternalReference: null,
                Detail: "Pre-488.2, so no *IDN?. Zero and calibrate against the 50 MHz reference "
                        + "before a measurement block, not once a day.");
        }
        catch (Exception ex)
        {
            return new ProbeResult(Role, Model, _link.ResourceName, Responded: false, Detail: ex.Message);
        }
    }

    public void Dispose() => _link.Dispose();
}

/// <summary>
/// Driver for the HP 11713A attenuator/switch driver.
///
/// <para>Source: HP-Attenuator's `Hp11713A.cs`, hardware-proven on this bench, and `11713A-OSM.pdf`
/// in the local library. <b>Listen-only</b>: it accepts commands but cannot talk, so its state can
/// never be confirmed over the bus — `probe` says so rather than implying it checked.</para>
///
/// <para>Optional in this project: it drives the 8494G/8496G programmable pads, which stop at
/// 18 GHz and so cannot serve band 4.</para>
/// </summary>
public sealed class Hp11713A : IInstrument
{
    private readonly IInstrumentLink _link;

    public string Role { get; }
    public string Model => "HP 11713A";
    public IInstrumentLink Link => _link;

    public Hp11713A(IInstrumentLink link, string role = "switch-driver")
    {
        _link = link ?? throw new ArgumentNullException(nameof(link));
        Role = role;
    }

    /// <summary>
    /// The relay setting last successfully sent, or null if none has been, or if the last attempt
    /// failed.
    ///
    /// <para><b>This is the only record there is.</b> A listen-only device cannot be asked what
    /// state it is in, so unlike every other instrument here there is no readback to fall back
    /// on — see <see cref="StateIsKnown"/>.</para>
    /// </summary>
    public string? LastRelaySetting { get; private set; }

    /// <summary>
    /// False once a relay command has failed to go out.
    ///
    /// <para>A failed write to a device that cannot talk is the worst combination on this bench:
    /// the pads may or may not have moved, and nothing can be asked. Anything depending on the
    /// attenuation being what it was told to be is now depending on a guess, so this says so
    /// rather than leaving the last successful setting standing as if it were still true.</para>
    /// </summary>
    public bool StateIsKnown { get; private set; } = true;

    /// <summary>
    /// Closes the given relays (A1-A0, B1-B0), opening the rest of the same bank.
    ///
    /// <para>If the write fails the exception propagates, and <see cref="StateIsKnown"/> goes
    /// false and stays false until a later command succeeds.</para>
    /// </summary>
    public void SetRelays(string closeChannels)
    {
        ArgumentNullException.ThrowIfNull(closeChannels);

        try
        {
            _link.Write(closeChannels);
        }
        catch
        {
            StateIsKnown = false;
            LastRelaySetting = null;
            throw;
        }

        LastRelaySetting = closeChannels;
        StateIsKnown = true;
    }

    public ProbeResult Probe() => new(
        Role, Model, _link.ResourceName,
        Responded: true,
        Identity: "listen-only",
        ExternalReference: null,
        Detail: StateIsKnown
            ? "Listen-only device: it accepts commands but cannot talk, so its presence and "
              + "state cannot be confirmed over the bus. Confirm at the front panel."
              + (LastRelaySetting is null ? "" : $" Last setting sent: {LastRelaySetting}.")
            : "Listen-only device, and a relay command FAILED to go out. The pads may or may not "
              + "have moved and there is no way to ask. Set the relays again, and confirm at the "
              + "front panel before trusting any attenuation figure.");

    public void Dispose() => _link.Dispose();
}
