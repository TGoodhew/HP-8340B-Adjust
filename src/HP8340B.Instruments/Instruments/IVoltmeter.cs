namespace HP8340B.Instruments;

/// <summary>
/// One DC range of a multimeter, with its accuracy as the manufacturers all state it:
/// +/-(% of reading + % of range).
/// </summary>
/// <param name="FullScaleVolts">
/// Full scale, <b>including any overrange</b>. The 3458A's "100 mV" range reads to 120 mV and its
/// range term is specified against that, so using 0.1 here would understate the floor.
/// </param>
/// <param name="PercentOfReading">Proportional term, percent.</param>
/// <param name="PercentOfRange">Additive term, percent of full scale — the noise floor.</param>
public sealed record VoltmeterRange(
    double FullScaleVolts,
    double PercentOfReading,
    double PercentOfRange)
{
    /// <summary>Uncertainty for a reading of <paramref name="volts"/> on this range.</summary>
    public double UncertaintyVolts(double volts) =>
        PercentOfReading / 100.0 * Math.Abs(volts) + PercentOfRange / 100.0 * FullScaleVolts;
}

/// <summary>
/// What a multimeter can actually do, as data rather than as a comment.
///
/// <para><b>Why this is modelled at all.</b> Three different meters could sit in the DMM role on
/// this bench, and the question "is this one good enough for that measurement?" has a numeric
/// answer that depends on the level being measured. Encoding the specification lets the
/// measurement compute it — and downgrade its own traceability class when the answer is no —
/// instead of somebody remembering a rule of thumb.</para>
/// </summary>
/// <param name="Model">Model designation.</param>
/// <param name="DcRanges">DC voltage ranges, lowest first.</param>
/// <param name="DcCommonModeRejectionDb">
/// DC common-mode rejection. This matters here more than it usually does: the 432A's substitution
/// measurement reads a difference of a few millivolts riding on about 3 V of common mode.
/// </param>
/// <param name="MaxNplc">Longest integration the meter offers, in power line cycles.</param>
/// <param name="Source">Where these figures come from.</param>
public sealed record VoltmeterAccuracy(
    string Model,
    IReadOnlyList<VoltmeterRange> DcRanges,
    double DcCommonModeRejectionDb,
    double MaxNplc,
    string Source)
{
    /// <summary>
    /// The range the meter would use for <paramref name="volts"/> — the lowest one that fits.
    /// </summary>
    public VoltmeterRange RangeFor(double volts)
    {
        var magnitude = Math.Abs(volts);

        return DcRanges.FirstOrDefault(r => magnitude <= r.FullScaleVolts)
               ?? throw new ArgumentOutOfRangeException(
                   nameof(volts), volts, $"Above the {Model}'s highest DC range.");
    }

    /// <summary>
    /// Total uncertainty for a reading, including the error a common-mode voltage leaks through
    /// finite rejection.
    /// </summary>
    /// <param name="volts">The reading.</param>
    /// <param name="commonModeVolts">Common-mode voltage present, if any.</param>
    public double UncertaintyVolts(double volts, double commonModeVolts = 0)
    {
        var range = RangeFor(volts);
        var commonMode = Math.Abs(commonModeVolts) / Math.Pow(10, DcCommonModeRejectionDb / 20.0);

        return range.UncertaintyVolts(volts) + commonMode;
    }
}

/// <summary>
/// The DMM role on this bench: whatever meter is reading the internal test points, the 0.5 V/GHz
/// output and the 432A's terminals.
///
/// <para>Three instruments can fill it — see <see cref="Hp3458A"/>, <see cref="Hp34401A"/> and
/// <see cref="RigolDm3058"/>. They speak three different command languages, so the measurements
/// talk to this rather than to any one of them, and each carries its own
/// <see cref="Accuracy"/> so a measurement can work out whether the meter in the rack is good
/// enough for what is being asked of it.</para>
/// </summary>
public interface IVoltmeter : IInstrument
{
    /// <summary>Puts the meter in a known state.</summary>
    void Preset();

    /// <summary>
    /// Configures for a DC measurement and reads it.
    /// </summary>
    /// <param name="rangeVolts">
    /// Expected full scale. Null autoranges, but prefer a stated range: a settle time that depends
    /// on which range the meter happened to pick is a settle time nobody can predict.
    /// </param>
    /// <param name="nplc">Integration time in power line cycles.</param>
    double MeasureDcVolts(double? rangeVolts, double nplc = 10);

    /// <summary>This meter's specification, as data.</summary>
    VoltmeterAccuracy Accuracy { get; }
}
