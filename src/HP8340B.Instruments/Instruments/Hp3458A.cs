using System.Globalization;
using HP8340B.Instruments.Visa;

namespace HP8340B.Instruments;

/// <summary>
/// Driver for the HP 3458A digital multimeter.
///
/// <para>Source: <b>HP 3458A Multimeter User's Guide</b> (`3458A/3458A Multimeter User's Guide
/// .pdf` in the local manual library). Commands are word-based: <c>DCV</c>, <c>NPLC</c>,
/// <c>NDIG</c>, <c>AZERO</c>, <c>OFORMAT</c>, <c>PRESET NORM</c> — the guide's Table 10 lists the
/// PRESET NORM state and is the citation for all of them.</para>
///
/// <para>Used for the internal test points of 5-14 and 5-16, the rear-panel 0.5 V/GHz output, and
/// reading the 432A's recorder output or its V_RF/V_COMP terminals for DC substitution.</para>
///
/// <para><b>Ranging is explicit, not autorange-and-hope.</b> A settle time that depends on which
/// range the meter happened to pick is a settle time nobody can predict, and 5-14 step 7 wants
/// 5 mV read to ±0.5 mV.</para>
/// </summary>
public sealed class Hp3458A : IVoltmeter
{
    private readonly IInstrumentLink _link;

    public string Role { get; }
    public string Model => "HP 3458A";
    public IInstrumentLink Link => _link;

    /// <summary>
    /// 1-year DC voltage specification, from Appendix A of the User's Guide. The guide states it
    /// in ppm rather than percent: the 100 mV range is "9 + 3" ppm, which is 0.0009% of reading
    /// plus 0.0003% of range. Ranges carry 20% overrange, so full scale is 120 mV, 1.2 V, 12 V.
    /// </summary>
    public static readonly VoltmeterAccuracy Specification = new(
        "HP 3458A",
        [
            new VoltmeterRange(0.12, 9e-4, 3e-4),
            new VoltmeterRange(1.2, 8e-4, 0.3e-4),
            new VoltmeterRange(12.0, 8e-4, 0.05e-4),
            new VoltmeterRange(120.0, 10e-4, 0.3e-4),
            new VoltmeterRange(1050.0, 10e-4, 0.1e-4),
        ],
        DcCommonModeRejectionDb: 140,
        MaxNplc: 1000,
        Source: "3458A User's Guide, Appendix A, DC Voltage: 1 Year accuracy in "
                + "(ppm of Reading + ppm of Range). DC ECMR 140 dB from the Noise Rejection "
                + "table.");

    public VoltmeterAccuracy Accuracy => Specification;

    public Hp3458A(IInstrumentLink link, string role = "dmm")
    {
        _link = link ?? throw new ArgumentNullException(nameof(link));
        Role = role;
    }

    private static string Num(double v) => v.ToString("0.#########", CultureInfo.InvariantCulture);

    /// <summary>
    /// Presets to the known state of the guide's Table 10.
    ///
    /// <para>Sends a GPIB device clear first, as the guide advises: "it is possible that the
    /// multimeter is busy or the GPIB interface is being held. In either case, the multimeter will
    /// not respond to a remote command ... send the GPIB Device Clear command prior to
    /// presetting."</para>
    /// </summary>
    public void Preset()
    {
        _link.Clear();
        _link.Write("PRESET NORM");
        _link.Write("OFORMAT ASCII");
    }

    /// <summary>
    /// Selects DC volts on an explicit range, in volts. Pass null for autorange, but prefer a
    /// stated range wherever the expected level is known — see the class remarks.
    /// </summary>
    public void SetDcVolts(double? rangeVolts)
    {
        _link.Write(rangeVolts is { } r
            ? $"DCV {Num(r)}"
            : "DCV AUTO");
    }

    /// <summary>
    /// Integration time in power line cycles. Higher is slower and quieter: NPLC 1 is the preset,
    /// NPLC 10 or 100 is worth it for a small DC reading such as the 5 mV of 5-14 step 7.
    /// </summary>
    public void SetIntegrationCycles(double nplc)
    {
        if (nplc <= 0)
            throw new ArgumentOutOfRangeException(nameof(nplc), nplc, "NPLC must be positive.");

        _link.Write($"NPLC {Num(nplc)}");
    }

    /// <summary>Displayed digits — <c>NDIG</c>. The preset is 6 (6.5 digits).</summary>
    public void SetDigits(int digits)
    {
        if (digits is < 3 or > 8)
            throw new ArgumentOutOfRangeException(
                nameof(digits), digits, "NDIG is 3 to 8 on the 3458A.");

        _link.Write($"NDIG {digits}");
    }

    /// <summary>
    /// Autozero on or off. On is the preset and is what a slow, accurate DC reading wants; off is
    /// faster but drifts.
    /// </summary>
    public void SetAutoZero(bool on) => _link.Write(on ? "AZERO ON" : "AZERO OFF");

    /// <summary>Reads one value. The meter is a talker, so this is a bare read.</summary>
    public double ReadVolts()
    {
        var text = _link.Read().Trim();

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var volts))
            throw new InvalidOperationException($"3458A returned '{text}', which is not a number.");

        return volts;
    }

    /// <summary>
    /// Configures for a DC measurement and reads it, in one call. <paramref name="rangeVolts"/> is
    /// the expected full scale — 0.1 V for the 5 mV of 5-14 step 7, for instance.
    /// </summary>
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
            // The 3458A does answer ID?, which returns e.g. "HP3458A".
            var identity = _link.Query("ID?").Trim();

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
