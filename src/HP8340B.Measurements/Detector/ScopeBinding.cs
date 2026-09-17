using HP8340B.Instruments;

namespace HP8340B.Measurements.Detector;

/// <summary>
/// Which scope, which channel and which termination a detector calibration was taken on.
///
/// <para><b>Why this has to travel with the calibration.</b> The calibration maps detector volts to
/// dB, and that mapping is what makes the 5-14 stop rule measurable — the manual says to peak the
/// power and then back the pot off "until the RF output power is reduced approximately 0.5 dB below
/// the squegging region". Without a calibrated detector, 0.5 dB is not a number anybody can act
/// on.</para>
///
/// <para>Until recently there was one answer, because the DS1104Z is 1 MΩ only. There are now five
/// scopes on this bench, three of them with selectable termination, and the scope itself is a
/// config choice. So a calibration can be invalidated two ways that leave the numbers looking
/// entirely plausible: by changing the scope's input impedance, or by using a different scope.
/// Neither is detectable downstream. See M0-29.</para>
/// </summary>
/// <param name="ScopeModel">The scope the calibration was taken on.</param>
/// <param name="Channel">The channel the detector was connected to.</param>
/// <param name="Termination">The channel's input termination at calibration time.</param>
/// <param name="ExternalLoadOhms">
/// Any external load in parallel with the scope input — the HP 10100C 50 Ω feedthrough in S4 is the
/// case that matters. Null if the detector drove the scope input alone.
/// </param>
public sealed record ScopeBinding(
    string ScopeModel,
    int Channel,
    ScopeInputImpedance Termination,
    double? ExternalLoadOhms = null)
{
    /// <summary>Nominal resistance of a termination setting.</summary>
    public static double OhmsFor(ScopeInputImpedance termination) => termination switch
    {
        ScopeInputImpedance.OneMegaohm => 1e6,
        ScopeInputImpedance.FiftyOhm => 50,
        ScopeInputImpedance.SeventyFiveOhm => 75,
        _ => throw new ArgumentOutOfRangeException(nameof(termination), termination, null),
    };

    /// <summary>
    /// What the detector actually drives: the scope input in parallel with any external load.
    ///
    /// <para>The parallel combination is the point. A 1 MΩ scope input with a 50 Ω feedthrough on
    /// it is a 50 Ω load, not a 1 MΩ one — the feedthrough dominates completely — so the S4
    /// hook-up note about adding the 10100C is not a small adjustment to the detector's working
    /// conditions, it is a different measurement.</para>
    /// </summary>
    public double VideoLoadOhms
    {
        get
        {
            var scope = OhmsFor(Termination);

            if (ExternalLoadOhms is not { } external) return scope;

            if (external <= 0)
                throw new InvalidOperationException(
                    $"An external load of {external} ohms is not a load.");

            return 1.0 / (1.0 / scope + 1.0 / external);
        }
    }

    /// <summary>A line naming the scope, channel and termination, for the session record.</summary>
    public string Describe()
    {
        var load = ExternalLoadOhms is { } external
            ? $"{Termination} with a {external:0.###} ohm external load — {VideoLoadOhms:0.###} ohms in parallel"
            : $"{Termination} — {VideoLoadOhms:0.###} ohms";

        return $"{ScopeModel} CH{Channel}, {load}";
    }

    /// <summary>
    /// Reads the binding off a live scope, so what is recorded is what the instrument reports
    /// rather than what the operator believes.
    /// </summary>
    /// <param name="scope">The scope the detector is connected to.</param>
    /// <param name="channel">The channel it is on.</param>
    /// <param name="externalLoadOhms">Any external feedthrough, which no instrument can report.</param>
    public static ScopeBinding From(IOscilloscope scope, int channel, double? externalLoadOhms = null)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return new ScopeBinding(
            scope.Model, channel, scope.GetInputImpedance(channel), externalLoadOhms);
    }

    /// <summary>
    /// True if <paramref name="other"/> is the same scope, channel and load.
    ///
    /// <para>Model rather than serial number, because nothing on this bench reports a serial the
    /// tool can trust. Two identical models swapped over would not be caught — worth knowing, and
    /// better than the nothing that came before.</para>
    /// </summary>
    public bool Matches(ScopeBinding other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return ScopeModel == other.ScopeModel
               && Channel == other.Channel
               && Math.Abs(VideoLoadOhms - other.VideoLoadOhms) / VideoLoadOhms < 0.01;
    }

    /// <summary>Everything that differs, for a refusal message.</summary>
    public IReadOnlyList<string> Differences(ScopeBinding other)
    {
        ArgumentNullException.ThrowIfNull(other);

        var reasons = new List<string>();

        if (ScopeModel != other.ScopeModel)
            reasons.Add($"calibrated on the {ScopeModel}, now on the {other.ScopeModel}");

        if (Channel != other.Channel)
            reasons.Add($"calibrated on CH{Channel}, now on CH{other.Channel}");

        if (Math.Abs(VideoLoadOhms - other.VideoLoadOhms) / VideoLoadOhms >= 0.01)
            reasons.Add(
                $"calibrated into {VideoLoadOhms:0.###} ohms ({Termination}"
                + (ExternalLoadOhms is { } e ? $" plus a {e:0.###} ohm external load" : "")
                + $"), now into {other.VideoLoadOhms:0.###} ohms ({other.Termination}"
                + (other.ExternalLoadOhms is { } oe ? $" plus a {oe:0.###} ohm external load" : "")
                + ")");

        return reasons;
    }
}
