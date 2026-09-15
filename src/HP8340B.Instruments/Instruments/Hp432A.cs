using System.Globalization;
using HP8340B.Instruments.Model;
using HP8340B.Instruments.Visa;

namespace HP8340B.Instruments;

/// <summary>
/// One of the 432A's seven power ranges. Manual Table 1-1, p. 1-1: "7 ranges with full-scale
/// readings of 10, 30, 100, and 300 uW, 1, 3 and 10 mW: also calibrated in dBm from -20 dBm to
/// +10 dBm full scale in 5-dB steps."
///
/// <para><b>The milliwatt figures on the panel are rounded and the dBm ones are exact.</b> Five
/// dB steps from -20 dBm give 10, 31.6, 100, 316, 1000, 3162 and 10000 uW — the panel prints
/// "30", "300" and "3" for the three that are not round numbers. Taking those legends literally
/// would put a 5% (0.23 dB) scaling error into meter mode on three of the seven ranges, which is
/// larger than the meter's own 1%-of-full-scale accuracy and would look like a sensor
/// disagreement rather than a bug.</para>
///
/// <para>The service section corroborates the exact steps: the range amplifier's gain is
/// "variable from 1 to 31.6", which is 30 dB across the seven ranges, matching -20 to +10 dBm
/// exactly. So full scale is derived from dBm here, and the label keeps the panel's legend.</para>
/// </summary>
/// <param name="FullScaleDbm">Full scale in dBm — the exact value.</param>
/// <param name="Label">How the range reads on the front panel, rounded legend and all.</param>
public sealed record ThermistorRange(double FullScaleDbm, string Label)
{
    /// <summary>Full scale in watts, derived from the exact dBm figure.</summary>
    public double FullScaleWatts => Math.Pow(10, FullScaleDbm / 10.0) / 1000.0;

    /// <summary>The seven ranges, lowest first.</summary>
    public static readonly IReadOnlyList<ThermistorRange> All =
    [
        new(-20, "10 uW (-20 dBm)"),
        new(-15, "30 uW (-15 dBm)"),      // 31.62 uW
        new(-10, "100 uW (-10 dBm)"),
        new(-5, "300 uW (-5 dBm)"),       // 316.2 uW
        new(0, "1 mW (0 dBm)"),
        new(5, "3 mW (+5 dBm)"),          // 3.162 mW
        new(10, "10 mW (+10 dBm)"),
    ];

    /// <summary>The lowest range whose full scale is at or above <paramref name="watts"/>.</summary>
    public static ThermistorRange ForPower(double watts) =>
        All.FirstOrDefault(r => watts <= r.FullScaleWatts)
        ?? throw new ArgumentOutOfRangeException(
            nameof(watts), watts,
            "Above the 432A's 10 mW full scale. The 478A's own manual: \"UNDER NO CIRCUMSTANCES "
            + "APPLY MORE THAN 30 mW AVERAGE TO THE MOUNT.\"");
}

/// <summary>
/// Which of the 432A's rear-panel outputs the 3458A is connected to.
///
/// <para>The 432A has no HP-IB at all, so every reading comes through the multimeter. The manual
/// (3-30) takes three separate measurements, and these are exactly those three.</para>
/// </summary>
public enum ThermistorConnection
{
    /// <summary>
    /// RECORDER output to ground — meter mode. 1.000 V open circuit equals full-scale deflection
    /// (Table 1-1), and paragraph 3-10 confirms that holds for a voltmeter above 1 Mohm, which
    /// the 3458A comfortably is on its 1 V and 10 V ranges.
    /// </summary>
    RecorderToGround,

    /// <summary>
    /// V_COMP and V_RF across the meter's input, giving the difference directly. The manual is
    /// emphatic that the voltmeter input must be isolated from chassis ground for this, which is
    /// why it takes two banks of the 44472A rather than one.
    /// </summary>
    DifferentialCompMinusRf,

    /// <summary>V_COMP to ground — step g of the procedure.</summary>
    CompToGround,
}

/// <summary>
/// Puts the 3458A across the right pair of 432A terminals.
///
/// <para>On this bench that is the 44472A dual 4-channel VHF switch in the 3499A: one bank picks
/// the meter's HI, the other its LO, which is how a genuinely differential connection is made
/// from single-ended relays. That driver is M0-21, so until it lands the only implementation is
/// the one that asks a person to move the lead.</para>
/// </summary>
public interface IThermistorConnectionSelector
{
    /// <summary>Connects the multimeter to <paramref name="connection"/>.</summary>
    void Select(ThermistorConnection connection);

    /// <summary>True if this happens over the bus rather than by hand.</summary>
    bool IsAutomatic { get; }
}

/// <summary>
/// The selector for a bench with no 3499A: it hands the instruction to a person and waits.
/// Slow, but it makes the substitution measurement possible before M0-21 lands rather than after.
/// </summary>
public sealed class ManualThermistorConnections(Action<string> instruct) : IThermistorConnectionSelector
{
    private readonly Action<string> _instruct =
        instruct ?? throw new ArgumentNullException(nameof(instruct));

    public bool IsAutomatic => false;

    /// <summary>Every connection asked for, in order — carried into the session record.</summary>
    public List<ThermistorConnection> History { get; } = [];

    public void Select(ThermistorConnection connection)
    {
        History.Add(connection);
        _instruct(Describe(connection));
    }

    /// <summary>What a person has to do to make <paramref name="connection"/>.</summary>
    public static string Describe(ThermistorConnection connection) => connection switch
    {
        ThermistorConnection.RecorderToGround =>
            "Connect the 3458A across the 432A's rear-panel RECORDER output (HI to centre, LO to shell).",
        ThermistorConnection.DifferentialCompMinusRf =>
            "Connect the 3458A HI to the 432A's rear-panel V COMP jack and LO to V RF. The meter "
            + "input must float — the 432A manual (3-30a) is explicit that it has to be isolated "
            + "from chassis ground.",
        ThermistorConnection.CompToGround =>
            "Connect the 3458A HI to the 432A's rear-panel V COMP jack and LO to chassis ground.",
        _ => throw new ArgumentOutOfRangeException(nameof(connection), connection, null),
    };
}

/// <summary>
/// The calibration data printed on a 478A's own label: Calibration Factor and Effective
/// Efficiency at six frequencies between 10 MHz and 10 GHz (478A manual, paragraph 31).
/// </summary>
/// <param name="Hz">Calibration frequency.</param>
/// <param name="CalibrationFactorPercent">Calibration Factor, as a percentage.</param>
/// <param name="EffectiveEfficiencyPercent">Effective Efficiency, as a percentage.</param>
public sealed record MountCalibrationPoint(
    double Hz,
    double CalibrationFactorPercent,
    double EffectiveEfficiencyPercent);

/// <summary>
/// Which correction factor to divide a substituted-power reading by.
///
/// <para><b>This is not a preference, and getting it wrong biases every reading.</b> The 432A's
/// substitution formula (3-30h) is printed with EFFECTIVE EFFICIENCY underneath it, because the
/// precision procedure it belongs to assumes a tuner. The 478A's own manual is explicit about
/// when each applies: Calibration Factor "is applied as a correction factor to all measurements
/// made without a tuner" (paragraph 33), and Effective Efficiency "is applied as a correction
/// factor when a tuner is used to match the thermistor mount to the transmission line"
/// (paragraph 35).</para>
///
/// <para>There is no tuner on this bench, so <see cref="CalibrationFactor"/> is the right divisor
/// here despite what the 432A's formula says — and since Effective Efficiency is always the
/// larger of the two, using it without a tuner would report every power low.</para>
/// </summary>
public enum ThermistorCorrection
{
    /// <summary>No tuner in the line — what this bench does.</summary>
    CalibrationFactor,

    /// <summary>A tuner is matching the mount, so mismatch loss is already gone.</summary>
    EffectiveEfficiency,
}

/// <summary>
/// An HP 478A thermistor mount and the calibration data from its label.
///
/// <para>478A manual, Table 1: 10 MHz to 10 GHz, 200 ohms +/-1%, 1 uW to 10 mW.</para>
/// </summary>
public sealed class ThermistorMount
{
    /// <summary>Frequency range, 478A manual p. 1: 10 MHz to 10 GHz.</summary>
    public const double MinHz = 10e6;
    public const double MaxHz = 10e9;

    /// <summary>
    /// Operating resistance, 478A manual p. 1: 200 ohms +/-1%. This is the R in the substitution
    /// formula, and the 432A's MOUNT RESISTANCE switch has to agree — the 478A manual warns in
    /// capitals that running a 200 ohm mount on a meter set for 100 ohms damages the thermistor.
    /// </summary>
    public const double ResistanceOhms = 200.0;

    /// <summary>
    /// The measuring limit, 478A manual paragraph 13: "can measure average power up to 10 mW".
    /// The destruction limit is higher — "UNDER NO CIRCUMSTANCES APPLY MORE THAN 30 mW AVERAGE TO
    /// THE MOUNT" — but repo rule 5 refuses well below either.
    /// </summary>
    public const double MaxMeasurableWatts = 10e-3;

    /// <summary>
    /// Repo rule 5's refusal threshold with the 478A selected: +7 dBm, half the measuring limit.
    /// </summary>
    public const double RefuseAboveDbm = 7.0;

    /// <summary>
    /// The mount's own calibration points, from its label. <b>Empty until somebody reads them off
    /// this particular mount</b> — that is decision D-06, and the values are per-mount, so there
    /// is nothing to default to and guessing them would quietly bias every reading.
    /// </summary>
    public List<MountCalibrationPoint> Calibration { get; } = [];

    /// <summary>Serial number, for the session record.</summary>
    public string? Serial { get; set; }

    /// <summary>Characterised pad in front of the mount, dB. Declared in config, added back in.</summary>
    public double PadDb { get; set; }

    /// <summary>
    /// The correction factor at <paramref name="hz"/>, as a fraction, interpolated linearly
    /// between the label's points. The 478A manual (paragraph 31) says the mounts are tested on a
    /// swept-frequency basis specifically so that interpolation between the printed points is
    /// valid, so this is the manual's own sanctioned method rather than an assumption.
    /// </summary>
    public double CorrectionAt(double hz, ThermistorCorrection which)
    {
        if (Calibration.Count == 0)
            throw new InvalidOperationException(
                "The 478A's calibration points have not been entered. They are printed on this "
                + "mount's own label at six frequencies between 10 MHz and 10 GHz and differ from "
                + "mount to mount, so there is nothing sensible to default to — see decision D-06. "
                + "Without them every reading would be biased by an unknown few percent.");

        if (hz < MinHz || hz > MaxHz)
            throw new ArgumentOutOfRangeException(
                nameof(hz), hz,
                $"The 478A covers {MinHz / 1e6:0.#} MHz to {MaxHz / 1e9:0.#} GHz. Above that, use "
                + "the 8485A on the 437B.");

        double Value(MountCalibrationPoint p) => which == ThermistorCorrection.CalibrationFactor
            ? p.CalibrationFactorPercent
            : p.EffectiveEfficiencyPercent;

        var points = Calibration.OrderBy(p => p.Hz).ToList();

        if (hz <= points[0].Hz) return Value(points[0]) / 100.0;
        if (hz >= points[^1].Hz) return Value(points[^1]) / 100.0;

        for (var i = 1; i < points.Count; i++)
        {
            if (hz > points[i].Hz) continue;

            var (low, high) = (points[i - 1], points[i]);
            var fraction = (hz - low.Hz) / (high.Hz - low.Hz);

            return (Value(low) + fraction * (Value(high) - Value(low))) / 100.0;
        }

        throw new InvalidOperationException("Unreachable: the frequency is inside the table.");
    }

    /// <summary>
    /// Refuses a DUT level that would reach the mount above repo rule 5's threshold.
    /// </summary>
    /// <param name="dutOutputDbm">What the DUT is about to be set to.</param>
    /// <param name="padDeclaredAndCharacterised">
    /// True only when a pad has been measured at this frequency, not merely assumed from its label.
    /// </param>
    public void GuardMountLevel(double dutOutputDbm, bool padDeclaredAndCharacterised)
    {
        var atMount = dutOutputDbm - (padDeclaredAndCharacterised ? PadDb : 0);

        if (atMount > RefuseAboveDbm)
            throw new InvalidOperationException(
                $"Refusing: {atMount:+0.0;-0.0} dBm would reach the 478A (DUT at "
                + $"{dutOutputDbm:+0.0;-0.0} dBm"
                + (padDeclaredAndCharacterised ? $", characterised pad {PadDb:0.#} dB" : ", no characterised pad")
                + $"). Repo rule 5 refuses above {RefuseAboveDbm:+0.0} dBm with the 478A selected: "
                + "it is a 10 mW mount and its own manual says never to apply more than 30 mW. "
                + "Declare a characterised pad, or lower the level.");
    }
}

/// <summary>How a thermistor power reading was taken.</summary>
public enum ThermistorMode
{
    /// <summary>From the RECORDER output — quick, and only as good as the meter's own 1% of full scale.</summary>
    Meter,

    /// <summary>The DC-substitution procedure of 432A paragraph 3-30 — 0.2% of reading + 0.5 uW.</summary>
    Substitution,
}

/// <summary>One power reading and everything needed to defend it later (repo rule 3).</summary>
/// <param name="Watts">Incident RF power at the mount.</param>
/// <param name="Mode">Which of the two methods produced it.</param>
/// <param name="Hz">Frequency the correction factor was taken at.</param>
/// <param name="CorrectionUsed">Which factor was divided out, and its value.</param>
/// <param name="CorrectionFraction">The factor actually applied.</param>
/// <param name="Traceability">How far the reading can be trusted.</param>
/// <param name="Note">Anything qualifying the reading.</param>
public sealed record ThermistorReading(
    double Watts,
    ThermistorMode Mode,
    double Hz,
    ThermistorCorrection CorrectionUsed,
    double CorrectionFraction,
    TraceabilityClass Traceability,
    string? Note = null)
{
    /// <summary>The same reading in dBm. Zero or negative power has no dBm value.</summary>
    public double Dbm => Watts <= 0 ? double.NegativeInfinity : 10.0 * Math.Log10(Watts * 1000.0);
}

/// <summary>
/// The HP 432A power meter with a 478A thermistor mount, read through the 3458A.
///
/// <para>Source: <b>HP 432A Operating and Service manual</b> (`432A-OSM.pdf`) and the
/// <b>478A Operating Note</b> (`478A-ON.pdf`), both in the local manual library. Page and
/// paragraph references below are theirs.</para>
///
/// <para><b>The 432A has no HP-IB.</b> Everything here is the 3458A reading one of three
/// rear-panel connections, selected by an <see cref="IThermistorConnectionSelector"/>. The front
/// panel — RANGE, CALIBRATION FACTOR, MOUNT RESISTANCE, the ZERO controls — cannot be read or set
/// over the bus at all, so those are declared in config and the readings carry them.</para>
///
/// <para><b>Why bother, with a 437B on the bench.</b> This is an independent thermistor
/// cross-check below 10 GHz that shares nothing with the diode sensors: different physics,
/// different traceability chain. And it is the only sensor for <b>10 to 50 MHz</b>, below the
/// 11792A's range, which 4-5 needs.</para>
/// </summary>
public sealed class Hp432A
{
    private readonly Hp3458A _dmm;
    private readonly IThermistorConnectionSelector _selector;

    /// <summary>The mount, and the calibration data off its label.</summary>
    public ThermistorMount Mount { get; }

    /// <summary>
    /// Which correction factor to divide out. Defaults to Calibration Factor because there is no
    /// tuner on this bench — see <see cref="ThermistorCorrection"/> for why that differs from the
    /// 432A formula's own printed divisor.
    /// </summary>
    public ThermistorCorrection Correction { get; set; } = ThermistorCorrection.CalibrationFactor;

    /// <summary>
    /// Where the front-panel RANGE switch is set. Not readable over the bus, so it is declared;
    /// meter mode is meaningless without it.
    /// </summary>
    public ThermistorRange Range { get; set; } = ThermistorRange.All[4];   // 1 mW

    /// <summary>
    /// Where the front-panel CALIBRATION FACTOR switch is set, as a percentage. The switch has 13
    /// positions, 100% down to 88% in 1% steps (Table 1-1), so it often cannot be set to the
    /// mount's exact figure — which is why the reading is corrected here from the mount's real
    /// value rather than trusted to the switch.
    /// </summary>
    public double CalFactorSwitchPercent { get; set; } = 100.0;

    /// <summary>
    /// Settle time after the RF is switched on or off. A thermistor is a thermal device and takes
    /// seconds, not milliseconds; reading too early reports a power that is still rising.
    /// </summary>
    public TimeSpan Settle { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a zero stays good before <see cref="ReadSubstitutionWatts"/> insists on a new one.
    /// Thermistor bridges drift with ambient temperature, and the whole accuracy claim rests on
    /// the zero being recent.
    /// </summary>
    public TimeSpan ZeroValidFor { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Whether the recorder-output scaling has been confirmed against a substitution reading at
    /// the same power. Until it has, meter-mode readings are <see cref="TraceabilityClass.Typical"/>
    /// rather than Spec — see <see cref="ConfirmMeterScaling"/>.
    /// </summary>
    public bool MeterScalingConfirmed { get; private set; }

    /// <summary>The zero-offset voltage V0, and when it was taken. Null until zeroed.</summary>
    public (double Volts, DateTime At)? Zero { get; private set; }

    public Hp432A(Hp3458A dmm, IThermistorConnectionSelector selector, ThermistorMount? mount = null)
    {
        _dmm = dmm ?? throw new ArgumentNullException(nameof(dmm));
        _selector = selector ?? throw new ArgumentNullException(nameof(selector));
        Mount = mount ?? new ThermistorMount();
    }

    // --- Zeroing ----------------------------------------------------------------------------

    /// <summary>
    /// Takes the zero-offset voltage V0 with the RF off — steps b to d of paragraph 3-30.
    ///
    /// <para>The caller is responsible for the RF actually being off; this cannot check it, and a
    /// zero taken with power applied is silently wrong in the direction that reports less power
    /// than there is.</para>
    /// </summary>
    public double ZeroWithRfOff()
    {
        _selector.Select(ThermistorConnection.DifferentialCompMinusRf);
        if (Settle > TimeSpan.Zero) Thread.Sleep(Settle);

        // Millivolts on a bridge that has been nulled by the FINE ZERO toggle, so the 0.1 V range
        // with a long integration rather than autorange.
        var volts = _dmm.MeasureDcVolts(rangeVolts: 0.1, nplc: 10);

        Zero = (volts, DateTime.UtcNow);
        return volts;
    }

    /// <summary>True if a zero has been taken and is still inside <see cref="ZeroValidFor"/>.</summary>
    public bool ZeroIsFresh =>
        Zero is { } z && DateTime.UtcNow - z.At < ZeroValidFor;

    // --- Substitution mode --------------------------------------------------------------------

    /// <summary>
    /// The DC-substitution measurement of 432A paragraph 3-30, with the RF already on and settled.
    ///
    /// <para>The manual's equation, 3-30h:</para>
    /// <code>
    ///           (1/4R) [ 2 V_COMP (V1 - V0) + V0^2 - V1^2 ]
    /// P_RF  =  ---------------------------------------------
    ///                     EFFECTIVE EFFICIENCY
    /// </code>
    /// <para>where R is the mount resistance, V0 is V_COMP - V_RF with the RF off and V1 the same
    /// difference with it on. The divisor is <see cref="Correction"/> rather than always Effective
    /// Efficiency, for the reason set out on <see cref="ThermistorCorrection"/>.</para>
    /// </summary>
    /// <param name="hz">Frequency, for the correction factor.</param>
    public ThermistorReading ReadSubstitutionWatts(double hz)
    {
        if (Zero is not { } zero)
            throw new InvalidOperationException(
                "No zero has been taken. Call ZeroWithRfOff with the DUT's RF output off first — "
                + "the substitution formula needs V0, and there is no sensible default for it.");

        if (!ZeroIsFresh)
            throw new InvalidOperationException(
                $"The zero is {(DateTime.UtcNow - zero.At).TotalMinutes:0.#} minutes old and this "
                + $"meter is set to re-zero every {ZeroValidFor.TotalMinutes:0.#}. A thermistor "
                + "bridge drifts with ambient temperature, and the accuracy claim rests on the "
                + "zero being recent. Re-zero with the RF off.");

        _selector.Select(ThermistorConnection.DifferentialCompMinusRf);
        if (Settle > TimeSpan.Zero) Thread.Sleep(Settle);
        var v1 = _dmm.MeasureDcVolts(rangeVolts: 0.1, nplc: 10);

        _selector.Select(ThermistorConnection.CompToGround);
        var vComp = _dmm.MeasureDcVolts(rangeVolts: 10.0, nplc: 10);

        var substituted = SubstitutedWatts(vComp, zero.Volts, v1, ThermistorMount.ResistanceOhms);
        var correction = Mount.CorrectionAt(hz, Correction);

        // The pad is added back in power terms, so the reported figure is the level at the DUT.
        var incident = substituted / correction * Math.Pow(10, Mount.PadDb / 10.0);

        return new ThermistorReading(
            incident, ThermistorMode.Substitution, hz, Correction, correction,
            TraceabilityClass.Spec,
            Mount.PadDb != 0 ? $"Characterised pad {Mount.PadDb:0.##} dB added back." : null);
    }

    /// <summary>
    /// The bracketed part of the manual's equation — DC power substituted in the mount, before
    /// any correction factor. Public and static so it can be checked against a worked example
    /// without a meter or a mount.
    /// </summary>
    /// <param name="vComp">V_COMP measured to ground.</param>
    /// <param name="v0">V_COMP - V_RF with the RF off.</param>
    /// <param name="v1">V_COMP - V_RF with the RF on.</param>
    /// <param name="resistanceOhms">Mount resistance R — 200 ohms for the 478A.</param>
    public static double SubstitutedWatts(double vComp, double v0, double v1, double resistanceOhms)
    {
        if (resistanceOhms <= 0)
            throw new ArgumentOutOfRangeException(nameof(resistanceOhms), resistanceOhms, "R must be positive.");

        return (2 * vComp * (v1 - v0) + v0 * v0 - v1 * v1) / (4 * resistanceOhms);
    }

    /// <summary>
    /// The inverse of <see cref="SubstitutedWatts"/>: the V1 that would produce
    /// <paramref name="watts"/>. Used by the simulator, and by anyone checking the algebra.
    /// </summary>
    public static double V1ForSubstitutedWatts(
        double watts, double vComp, double v0, double resistanceOhms)
    {
        // Writing d = V1 - V0, the bracket becomes 2d(V_COMP - V0) - d^2, so d solves
        // d^2 - 2d(V_COMP - V0) + 4R*P = 0 and the physical root is the smaller one.
        var b = vComp - v0;
        var discriminant = b * b - 4 * resistanceOhms * watts;

        if (discriminant < 0)
            throw new ArgumentOutOfRangeException(
                nameof(watts), watts,
                $"No bridge voltage produces {watts * 1000:0.###} mW with V_COMP {vComp:0.###} V. "
                + "The bridge cannot substitute more DC power than it started with.");

        return v0 + b - Math.Sqrt(discriminant);
    }

    // --- Meter mode ---------------------------------------------------------------------------

    /// <summary>
    /// Reads the RECORDER output and scales it to power.
    ///
    /// <para>Table 1-1: "1.000 volt into open circuit corresponds to full-scale meter deflection
    /// (1.0 on 0-1 scale), +/-0.5%, 1000-ohm output impedance", and paragraph 3-10 confirms that
    /// holds for a voltmeter above 1 Mohm. The reading is then un-corrected by the front-panel
    /// CALIBRATION FACTOR switch and re-corrected by the mount's real figure, because the switch
    /// only moves in 1% steps and frequently cannot be set to the mount's exact value.</para>
    ///
    /// <para><b>Marked Typical until cross-checked.</b> Nothing here has been confirmed against
    /// hardware: the scaling, the switch arithmetic and the range declaration are all inferred
    /// from the manual. <see cref="ConfirmMeterScaling"/> upgrades it to Spec once a meter reading
    /// and a substitution reading at the same power have been compared.</para>
    /// </summary>
    public ThermistorReading ReadMeterWatts(double hz)
    {
        _selector.Select(ThermistorConnection.RecorderToGround);
        if (Settle > TimeSpan.Zero) Thread.Sleep(Settle);

        // 1 V is full scale, so the 1 V range covers the whole span with no autoranging.
        var volts = _dmm.MeasureDcVolts(rangeVolts: 1.0, nplc: 10);

        var indicated = volts * Range.FullScaleWatts;
        var correction = Mount.CorrectionAt(hz, Correction);

        // The meter has already divided by whatever the switch is set to; undo that and apply the
        // mount's actual figure.
        var incident = indicated * (CalFactorSwitchPercent / 100.0) / correction
                       * Math.Pow(10, Mount.PadDb / 10.0);

        return new ThermistorReading(
            incident, ThermistorMode.Meter, hz, Correction, correction,
            MeterScalingConfirmed ? TraceabilityClass.Spec : TraceabilityClass.Typical,
            MeterScalingConfirmed
                ? $"Recorder scaling confirmed against a substitution reading. Range {Range.Label}."
                : "UNCONFIRMED recorder-output scaling: 1.000 V = full scale is taken from the "
                  + "manual and has never been checked on this meter. Run ConfirmMeterScaling "
                  + "against a substitution reading at the same power before treating this as Spec. "
                  + $"Range {Range.Label}.");
    }

    /// <summary>
    /// Compares a meter-mode reading against a substitution reading taken at the same power, and
    /// promotes meter mode to Spec if they agree.
    ///
    /// <para>This is the check the recorder-output scaling is waiting on. Substitution is the
    /// reference: the 432A manual puts it at 0.2% of reading plus 0.5 uW, against 1% of full
    /// scale for the meter.</para>
    /// </summary>
    /// <param name="toleranceDb">
    /// How far apart the two may be. The default allows the meter's own 1%-of-full-scale
    /// instrumentation error plus the recorder output's 0.5%, which together are about 0.07 dB at
    /// full scale — 0.2 dB leaves room for the reading not being at full scale without letting a
    /// scaling error of any real size through.
    /// </param>
    public bool ConfirmMeterScaling(
        ThermistorReading meter, ThermistorReading substitution, double toleranceDb = 0.2)
    {
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentNullException.ThrowIfNull(substitution);

        if (meter.Mode != ThermistorMode.Meter || substitution.Mode != ThermistorMode.Substitution)
            throw new ArgumentException(
                "ConfirmMeterScaling compares a meter reading against a substitution reading. "
                + $"Got {meter.Mode} and {substitution.Mode}.");

        if (meter.Watts <= 0 || substitution.Watts <= 0)
            throw new ArgumentException("Both readings must be positive power.");

        var differenceDb = Math.Abs(meter.Dbm - substitution.Dbm);

        MeterScalingConfirmed = differenceDb <= toleranceDb;
        return MeterScalingConfirmed;
    }

    /// <summary>
    /// The traceability class a reading at <paramref name="hz"/> can claim through this path.
    /// The 478A covers 10 MHz to 10 GHz; outside that the mount simply cannot be used.
    /// </summary>
    public static TraceabilityClass TraceabilityAt(double hz) =>
        hz >= ThermistorMount.MinHz && hz <= ThermistorMount.MaxHz
            ? TraceabilityClass.Spec
            : TraceabilityClass.NotPossible;
}

/// <summary>
/// A simulated 432A/478A/3458A path. Owns the true incident power and produces the bridge
/// voltages that would give it, so the substitution arithmetic is exercised end to end rather
/// than against a constant.
/// </summary>
public sealed class SimulatedThermistorPath : IThermistorConnectionSelector
{
    private ThermistorConnection _connection = ThermistorConnection.RecorderToGround;

    /// <summary>True incident RF power at the mount, in watts. What a correct reading recovers.</summary>
    public double IncidentWatts { get; set; } = 1e-3;

    /// <summary>The mount's correction factor as a fraction, at the frequency being modelled.</summary>
    public double CorrectionFraction { get; set; } = 0.97;

    /// <summary>Compensation-bridge voltage, V_COMP to ground. A few volts on a real 432A.</summary>
    public double VCompVolts { get; set; } = 3.0;

    /// <summary>Residual zero offset V0 after the FINE ZERO toggle — small, and not exactly zero.</summary>
    public double ZeroOffsetVolts { get; set; } = 0.0004;

    /// <summary>Front-panel RANGE, for the recorder-output model.</summary>
    public ThermistorRange Range { get; set; } = ThermistorRange.All[4];

    /// <summary>Front-panel CALIBRATION FACTOR switch, percent.</summary>
    public double CalFactorSwitchPercent { get; set; } = 100.0;

    /// <summary>Whether the DUT's RF is on. The voltages differ, which is the whole measurement.</summary>
    public bool RfOn { get; set; } = true;

    public bool IsAutomatic => true;

    /// <summary>Every connection made, in order.</summary>
    public List<ThermistorConnection> History { get; } = [];

    public void Select(ThermistorConnection connection)
    {
        _connection = connection;
        History.Add(connection);
    }

    /// <summary>DC power the bridge substitutes for the RF — incident power times the correction.</summary>
    public double SubstitutedWatts => IncidentWatts * CorrectionFraction;

    /// <summary>Builds the 3458A link. It answers whatever the currently selected connection reads.</summary>
    public SimulatedInstrumentLink CreateDmmLink(string resourceName = "SIM::dmm::INSTR")
    {
        var link = new SimulatedInstrumentLink(resourceName, Respond)
        {
            StatusByte = 0,
        };

        return link;
    }

    private string Respond(string command)
    {
        var c = command.Trim();

        if (c.StartsWith("ID?", StringComparison.OrdinalIgnoreCase)) return "HP3458A";

        return VoltsFor(_connection).ToString("0.000000E+00", CultureInfo.InvariantCulture);
    }

    /// <summary>What the meter would read on <paramref name="connection"/>.</summary>
    public double VoltsFor(ThermistorConnection connection) => connection switch
    {
        ThermistorConnection.CompToGround => VCompVolts,

        ThermistorConnection.DifferentialCompMinusRf => RfOn
            ? Hp432A.V1ForSubstitutedWatts(
                SubstitutedWatts, VCompVolts, ZeroOffsetVolts, ThermistorMount.ResistanceOhms)
            : ZeroOffsetVolts,

        // The meter displays substituted power divided by the switch setting, and the recorder
        // output is that as a fraction of full scale.
        ThermistorConnection.RecorderToGround =>
            SubstitutedWatts / (CalFactorSwitchPercent / 100.0) / Range.FullScaleWatts,

        _ => throw new ArgumentOutOfRangeException(nameof(connection), connection, null),
    };
}
