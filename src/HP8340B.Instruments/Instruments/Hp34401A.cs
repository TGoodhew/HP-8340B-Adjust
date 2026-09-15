using System.Globalization;
using HP8340B.Instruments.Visa;

namespace HP8340B.Instruments;

/// <summary>
/// Driver for the HP/Agilent 34401A digital multimeter.
///
/// <para>Source: <b>34401A User's Guide</b> (`34401A User.pdf` in the local manual library).
/// Unlike the 3458A this is a plain SCPI instrument — <c>*RST</c>, <c>CONFigure:VOLTage:DC</c>,
/// <c>SENSe:VOLTage:DC:NPLCycles</c>, <c>READ?</c> — so the driver is short.</para>
///
/// <para><b>Standing in for the 3458A.</b> Everything this bench asks of a meter is slow DC: the
/// 5 mV of 5-14 step 7 to +/-0.5 mV, the null of 5-16 step 3, and the 432A's bridge voltages. The
/// 34401A is comfortably inside all of them — see <see cref="Accuracy"/> and the numbers on
/// <see cref="Hp432A"/>. Its DC common-mode rejection is 140 dB, the same figure the 3458A
/// quotes, which is what the 432A's differential measurement actually leans on.</para>
/// </summary>
public sealed class Hp34401A : IVoltmeter
{
    private readonly IInstrumentLink _link;

    public string Role { get; }
    public string Model => "HP 34401A";
    public IInstrumentLink Link => _link;

    /// <summary>
    /// 1-year DC voltage specification, 23 C +/- 5 C, from the guide's DC Characteristics table.
    /// Ranges carry 20% overrange, so full scale is 120 mV, 1.2 V and 12 V.
    /// </summary>
    public static readonly VoltmeterAccuracy Specification = new(
        "HP 34401A",
        [
            new VoltmeterRange(0.12, 0.0050, 0.0035),
            new VoltmeterRange(1.2, 0.0040, 0.0007),
            new VoltmeterRange(12.0, 0.0035, 0.0005),
            new VoltmeterRange(120.0, 0.0045, 0.0006),
            new VoltmeterRange(1000.0, 0.0045, 0.0010),
        ],
        DcCommonModeRejectionDb: 140,
        MaxNplc: 100,
        Source: "34401A User's Guide, DC Characteristics: 1 Year 23 C +/- 5 C, "
                + "(% of reading + % of range). DC CMRR 140 dB from the Measurement Noise "
                + "Rejection table.");

    public VoltmeterAccuracy Accuracy => Specification;

    public Hp34401A(IInstrumentLink link, string role = "dmm")
    {
        _link = link ?? throw new ArgumentNullException(nameof(link));
        Role = role;
    }

    private static string Num(double v) => v.ToString("0.#########", CultureInfo.InvariantCulture);

    /// <summary>Device clear then <c>*RST</c>, for the same reason the 3459A driver clears first.</summary>
    public void Preset()
    {
        _link.Clear();
        _link.Write("*RST");
        _link.Write("*CLS");
    }

    /// <summary>
    /// Selects DC volts on an explicit range. <c>CONFigure</c> takes the expected value rather than
    /// a range name and picks the range that holds it; <c>AUTO</c> autoranges.
    /// </summary>
    public void SetDcVolts(double? rangeVolts) =>
        _link.Write(rangeVolts is { } r
            ? $"CONFigure:VOLTage:DC {Num(r)}"
            : "CONFigure:VOLTage:DC AUTO");

    /// <summary>
    /// Integration time in power line cycles. The 34401A accepts 0.02, 0.2, 1, 10 and 100 and
    /// rounds anything else up to the next of those, so this refuses a value above its maximum
    /// rather than letting the meter silently integrate for less time than was asked for.
    /// </summary>
    public void SetIntegrationCycles(double nplc)
    {
        if (nplc <= 0)
            throw new ArgumentOutOfRangeException(nameof(nplc), nplc, "NPLC must be positive.");

        if (nplc > Specification.MaxNplc)
            throw new ArgumentOutOfRangeException(
                nameof(nplc), nplc,
                $"The 34401A integrates for at most {Specification.MaxNplc} PLC. The 3458A goes to "
                + "1000, so a measurement carried over from it may be asking for more averaging "
                + "than this meter can give.");

        _link.Write($"SENSe:VOLTage:DC:NPLCycles {Num(nplc)}");
    }

    /// <summary>
    /// Input impedance on the 100 mV, 1 V and 10 V ranges: 10 Mohm, or >10 Gohm with AUTO ON.
    ///
    /// <para>Worth setting deliberately for the 432A's RECORDER output, which is specified as
    /// 1.000 V into an <b>open circuit</b> from a 1 kohm source. 10 Mohm loads that by about
    /// 0.01%, which is harmless — but the default matters enough to be explicit about.</para>
    /// </summary>
    public void SetHighInputImpedance(bool on) =>
        _link.Write($"INPut:IMPedance:AUTO {(on ? "ON" : "OFF")}");

    /// <summary>Triggers and reads one value — <c>READ?</c>.</summary>
    public double ReadVolts() => Parse(_link.Query("READ?"));

    private static double Parse(string text)
    {
        var trimmed = text.Trim();

        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new InvalidOperationException($"34401A returned '{trimmed}', which is not a number.");
    }

    public double MeasureDcVolts(double? rangeVolts, double nplc = 10)
    {
        SetDcVolts(rangeVolts);
        SetIntegrationCycles(nplc);
        return ReadVolts();
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
/// Driver for the Rigol DM3058 digital multimeter.
///
/// <para>Source: <b>DM3058/DM3058E User's Guide</b> and <b>Programming Guide</b> (both in the
/// local manual library). SCPI, close enough to the 34401A that the command builders look alike.
/// </para>
///
/// <para><b>The weakest of the three, and still good enough here.</b> Its 200 mV range floor is
/// 8 uV against the 34401A's 3.5 uV and the 3458A's 0.36 uV, and its DC CMRR is 120 dB rather than
/// 140. Both only start to matter below about -25 dBm at the thermistor mount, and there the
/// 432A's own 0.5 uW additive term is already contributing six times more error than the meter is
/// — see <see cref="Hp432A.MeterIsAdequate"/>, which computes that rather than asserting it.</para>
///
/// <para>Two practical notes. The DM3058<b>E</b> has no GPIB, only USB and RS-232, so on a bench
/// where everything else is on the bus it lands on a separate interface. And its "slow" rate is
/// what the specification below is quoted at, so a faster rate is not covered by these
/// figures.</para>
/// </summary>
public sealed class RigolDm3058 : IVoltmeter
{
    private readonly IInstrumentLink _link;

    public string Role { get; }
    public string Model => "Rigol DM3058";
    public IInstrumentLink Link => _link;

    /// <summary>
    /// 1-year DC voltage specification, 23 C +/- 5 C, from the user guide's accuracy table. The
    /// guide notes these apply at the "slow" measurement rate after 30 minutes of warm-up.
    /// Ranges carry 20% overrange except 1000 V.
    /// </summary>
    public static readonly VoltmeterAccuracy Specification = new(
        "Rigol DM3058",
        [
            new VoltmeterRange(0.24, 0.015, 0.004),
            new VoltmeterRange(2.4, 0.015, 0.003),
            new VoltmeterRange(24.0, 0.015, 0.004),
            new VoltmeterRange(240.0, 0.015, 0.003),
            new VoltmeterRange(1000.0, 0.015, 0.003),
        ],
        DcCommonModeRejectionDb: 120,
        MaxNplc: 10,
        Source: "DM3058/DM3058E User's Guide, DC accuracy table: 1 Year 23 C +/- 5 C, "
                + "(% of reading + % of range), at the \"slow\" measurement rate. "
                + "DC CMRR 120 dB for 1 kohm unbalance in the LO lead.");

    public VoltmeterAccuracy Accuracy => Specification;

    public RigolDm3058(IInstrumentLink link, string role = "dmm")
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

    /// <summary>
    /// Selects DC volts. The Rigol takes a range index or AUTO; passing the expected value lets
    /// the driver pick, keeping the call shape identical to the other two meters.
    /// </summary>
    public void SetDcVolts(double? rangeVolts)
    {
        if (rangeVolts is not { } r)
        {
            _link.Write(":MEASure:VOLTage:DC AUTO");
            return;
        }

        // Range index, per the programming guide: 0 = 200 mV, 1 = 2 V, 2 = 20 V, 3 = 200 V,
        // 4 = 1000 V.
        var index = Specification.DcRanges
            .Select((range, i) => (range, i))
            .First(x => Math.Abs(r) <= x.range.FullScaleVolts).i;

        _link.Write($":MEASure:VOLTage:DC {index}");
    }

    /// <summary>
    /// Measurement rate. The DM3058 has three — Slow, Medium and Fast — rather than an NPLC
    /// setting, and <b>only Slow is covered by the accuracy specification</b>, so anything asking
    /// for real integration gets Slow.
    /// </summary>
    public void SetIntegrationCycles(double nplc)
    {
        if (nplc <= 0)
            throw new ArgumentOutOfRangeException(nameof(nplc), nplc, "NPLC must be positive.");

        _link.Write(nplc >= 1
            ? ":RATE:VOLTage:DC S"
            : ":RATE:VOLTage:DC F");
    }

    /// <summary>
    /// High input impedance on the 200 mV and 2 V ranges — >10 Gohm rather than 10 Mohm. Above
    /// 2 V the input is 10 Mohm and not selectable.
    /// </summary>
    public void SetHighInputImpedance(bool on) =>
        _link.Write($":MEASure:VOLTage:DC:IMPedance {(on ? "10G" : "10M")}");

    public double ReadVolts()
    {
        var text = _link.Query(":MEASure:VOLTage:DC?").Trim();

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new InvalidOperationException($"DM3058 returned '{text}', which is not a number.");
    }

    public double MeasureDcVolts(double? rangeVolts, double nplc = 10)
    {
        SetDcVolts(rangeVolts);
        SetIntegrationCycles(nplc);
        return ReadVolts();
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
                Detail: "DM3058E has no GPIB — if this is the E variant it is on USB or RS-232, "
                        + "not the bus with everything else.");
        }
        catch (Exception ex)
        {
            return new ProbeResult(Role, Model, _link.ResourceName, Responded: false, Detail: ex.Message);
        }
    }

    public void Dispose() => _link.Dispose();
}
