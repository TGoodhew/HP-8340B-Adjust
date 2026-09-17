namespace HP8340B.Instruments;

/// <summary>Oscilloscope channel input termination.</summary>
public enum ScopeInputImpedance
{
    /// <summary>1 MΩ. Every scope here can do this; some can do nothing else.</summary>
    OneMegaohm,

    /// <summary>50 Ω. Needed for 4-12 rise/fall, and it removes the 10100C feedthrough from S4.</summary>
    FiftyOhm,

    /// <summary>75 Ω. Present on the DPO3034 and used by nothing on this bench.</summary>
    SeventyFiveOhm,
}

/// <summary>
/// Bandwidth at or below a vertical sensitivity, for scopes that derate at their most sensitive
/// ranges.
/// </summary>
/// <param name="AtOrBelowVoltsPerDivision">The sensitivity this limit applies at and below.</param>
/// <param name="BandwidthHz">The bandwidth available there.</param>
public sealed record BandwidthDerate(double AtOrBelowVoltsPerDivision, double BandwidthHz);

/// <summary>
/// What an oscilloscope can actually do, as a fact about the model rather than something a driver
/// happens to expose.
///
/// <para><b>Why capability is part of the abstraction.</b> Substituting an instrument on this bench
/// is not only "does the driver work", it is "what may the result claim" (rule 3). A scope that
/// cannot do 50 Ω cannot be the fast-scope for 4-12, and a two-channel scope cannot watch three
/// test points. Those refusals belong at config load, in front of somebody at a keyboard, rather
/// than at the bench with the covers off.</para>
///
/// <para><b>Every figure carries its source</b>, following <c>SwitchModule.RatingForSwitch</c>.
/// This repo keeps a list of figures stated about hardware with no manual to cite
/// (<c>docs/PROVISIONAL.md</c>), and a capability table is exactly the kind of thing that
/// accumulates plausible unsourced numbers if nothing forces the question.</para>
/// </summary>
/// <param name="Model">The instrument these figures describe.</param>
/// <param name="Channels">Analogue channels.</param>
/// <param name="BandwidthHz">Nominal bandwidth, before any derating.</param>
/// <param name="MinVoltsPerDivision">Most sensitive vertical range.</param>
/// <param name="MaxVoltsPerDivision">Least sensitive vertical range.</param>
/// <param name="InputImpedances">Terminations the channels support.</param>
/// <param name="HasExternalTrigger">
/// Whether there is an external trigger input. S4, S5 and S7 all drive it from the 8340B's rear
/// sweep trigger, so a scope without one cannot fill any of those roles.
/// </param>
/// <param name="HasXyMode">Whether the scope can display and capture X against Y.</param>
/// <param name="Derates">Bandwidth limits at the most sensitive ranges, if any.</param>
/// <param name="Source">Where these figures came from.</param>
public sealed record ScopeCapability(
    string Model,
    int Channels,
    double BandwidthHz,
    double MinVoltsPerDivision,
    double MaxVoltsPerDivision,
    IReadOnlyList<ScopeInputImpedance> InputImpedances,
    bool HasExternalTrigger,
    bool HasXyMode,
    IReadOnlyList<BandwidthDerate> Derates,
    string Source)
{
    /// <summary>True if the channels can be terminated in <paramref name="impedance"/>.</summary>
    public bool Supports(ScopeInputImpedance impedance) => InputImpedances.Contains(impedance);

    /// <summary>
    /// Bandwidth at a given vertical sensitivity.
    ///
    /// <para>Several scopes here give up bandwidth at their most sensitive ranges - the TDS3014B
    /// drops from 100 MHz to 90 MHz at 1 mV/div. It never bites on this bench, because the S5 test
    /// points are DC or slow, but the figure should come from the driver rather than from somebody
    /// remembering a footnote.</para>
    /// </summary>
    public double BandwidthAt(double voltsPerDivision)
    {
        var limit = BandwidthHz;

        foreach (var derate in Derates)
            if (voltsPerDivision <= derate.AtOrBelowVoltsPerDivision + 1e-12)
                limit = Math.Min(limit, derate.BandwidthHz);

        return limit;
    }

    /// <summary>True if <paramref name="channel"/> exists on this model.</summary>
    public bool HasChannel(int channel) => channel >= 1 && channel <= Channels;
}
