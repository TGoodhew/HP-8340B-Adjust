namespace HP8340B.Instruments;

/// <summary>
/// What a setup needs from whichever scope is filling a role, so an unsuitable substitute is
/// refused at config load rather than discovered at the bench with the covers off.
///
/// <para>One scope used to serve S4, S5 and S7. That was always slightly wrong and became clearly
/// wrong once there was more than one on the bench: the right instrument for the tuning view is not
/// the right instrument for 4-12 rise/fall. See M0-25 (#81).</para>
///
/// <para><b>Why the trigger is modelled rather than assumed.</b> The hook-up cards all say "DUT
/// rear sweep trigger to EXT TRIG", which quietly assumes every scope has a usable external trigger
/// input. The TDS3014B does not: its programming manual states that neither EXT nor EXT10 is
/// available on <b>4-channel</b> TDS3000 Series instruments, and the 3014B is four-channel. It can
/// still run these setups, but only by spending one of its four channels on the trigger - which is
/// the difference between "fits S5 exactly" and "cannot do S5 at all". A role therefore declares
/// how many channels carry <i>signal</i> and whether the trigger is a separate signal, and the
/// channel arithmetic follows from the scope.</para>
/// </summary>
/// <param name="Name">The role name, as it appears in <c>bench.json</c>.</param>
/// <param name="SignalChannels">Channels carrying signals that must be captured.</param>
/// <param name="TriggerIsSeparateSignal">
/// True if the trigger is a signal not already on one of the captured channels, so a scope without
/// an external trigger input has to give up a channel for it.
/// </param>
/// <param name="NeedsXy">Whether the role needs an X-against-Y display.</param>
/// <param name="NeedsFiftyOhm">Whether a genuine 50 Ω termination is required.</param>
/// <param name="MinBandwidthHz">Least bandwidth that will do, or zero if bandwidth is irrelevant.</param>
/// <param name="FinestVoltsPerDivision">The most sensitive range the role actually uses.</param>
/// <param name="Why">Why the role needs what it needs, printed with any refusal.</param>
public sealed record ScopeRole(
    string Name,
    int SignalChannels,
    bool TriggerIsSeparateSignal,
    bool NeedsXy,
    bool NeedsFiftyOhm,
    double MinBandwidthHz,
    double FinestVoltsPerDivision,
    string Why)
{
    /// <summary>
    /// S4, the detector XY view - the primary tuning display of 5-14 and 5-16 steps 30-36.
    ///
    /// <para><b>Bandwidth is deliberately not a requirement here.</b> The signal is a detected
    /// envelope at sweep rates, not RF, so every scope on this bench has orders of magnitude more
    /// than it needs. What matters is the XY display, two channels and a link fast enough to watch
    /// while turning a pot - and the last of those is a property of the transport, not the front
    /// end.</para>
    ///
    /// <para>The trigger is not counted as a separate signal: the DUT's sweep ramp is already on a
    /// channel here, so a scope with no external trigger input can trigger on that instead.</para>
    /// </summary>
    public static readonly ScopeRole Tuning = new(
        "tuning-scope",
        SignalChannels: 2,
        TriggerIsSeparateSignal: false,
        NeedsXy: true,
        NeedsFiftyOhm: false,
        MinBandwidthHz: 0,
        FinestVoltsPerDivision: 0.05,
        Why: "S4 puts the 8473C detector on one channel and the DUT sweep ramp on another, in XY. "
             + "The trace is a detected envelope at sweep rates, so bandwidth is irrelevant here.");

    /// <summary>
    /// S5, the internal test points: A26TP3 (MOD LVL), A24TP12 (SRD BIAS) and A25TP2 (DET).
    ///
    /// <para>Three signals at once, and the DUT's sweep trigger is a fourth signal that is not one
    /// of them - so a scope without an external trigger input needs four channels to run this at
    /// all. 5-16 steps 33 and 34 call for 0.005 V/Div, so a scope that stops coarser than that
    /// cannot run the procedure as written.</para>
    /// </summary>
    public static readonly ScopeRole TestPoint = new(
        "testpoint-scope",
        SignalChannels: 3,
        TriggerIsSeparateSignal: true,
        NeedsXy: false,
        NeedsFiftyOhm: false,
        MinBandwidthHz: 0,
        FinestVoltsPerDivision: 0.005,
        Why: "S5 watches three internal test points at once, triggered from the DUT sweep. "
             + "5-16 steps 33 and 34 set the vertical sensitivity to 0.005 V/Div.");

    /// <summary>
    /// S7, 4-12 pulse modulation rise and fall time.
    ///
    /// <para><b>50 Ω is the hard requirement</b>, not bandwidth. The measurement is of a fast edge
    /// on a detected envelope and needs a properly terminated line; S7's own note says the
    /// feedthrough is required there rather than optional, and a scope with a real 50 Ω input
    /// removes that passive part from the chain.</para>
    ///
    /// <para><b>Where the bandwidth figure comes from.</b> Table 4-25 specifies rise and fall times
    /// of <b>under 25 ns</b>. A scope of bandwidth B has a transition time of roughly 0.35/B, and
    /// the measured edge is about the root-sum-square of the signal's and the scope's. Holding the
    /// scope's contribution to 2% of a 25 ns edge needs t_scope no more than
    /// 25 ns × sqrt(1.02² − 1) ≈ 5.0 ns, so B ≥ 0.35 / 5.0 ns ≈ 70 MHz. That is arithmetic from a
    /// cited specification rather than a chosen threshold: change the 2% and recompute, rather than
    /// arguing about the megahertz.</para>
    ///
    /// <para>Which makes the honest position that most of this bench clears the bandwidth bar
    /// comfortably - a 100 MHz scope contributes about 1%. The reasons to prefer the 54845A are the
    /// genuine 50 Ω input and the headroom: at 1.5 GHz the scope stops being a term in the budget
    /// at all, leaving the 8473C's own video bandwidth as the limit. That is where the limit
    /// belongs, and unlike "the scope was 100 MHz" it is one the uncertainty budget can defend.</para>
    /// </summary>
    public static readonly ScopeRole Fast = new(
        "fast-scope",
        SignalChannels: 1,
        TriggerIsSeparateSignal: true,
        NeedsXy: false,
        NeedsFiftyOhm: true,
        MinBandwidthHz: 70e6,
        FinestVoltsPerDivision: 0.05,
        Why: "4-12 measures a pulse envelope specified at under 25 ns (Table 4-25), triggered from "
             + "the 8116A SYNC output, and needs a properly terminated 50 ohm line.");

    /// <summary>All three roles.</summary>
    public static readonly IReadOnlyList<ScopeRole> All = [Tuning, TestPoint, Fast];

    /// <summary>The role of this name, or null.</summary>
    public static ScopeRole? ByName(string name) =>
        All.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// How many channels this role costs on <paramref name="capability"/> — the signal channels,
    /// plus one for the trigger if the scope cannot take it externally.
    /// </summary>
    public int ChannelsNeededOn(ScopeCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);

        var forTrigger = TriggerIsSeparateSignal && !capability.HasExternalTrigger ? 1 : 0;
        return SignalChannels + forTrigger;
    }

    /// <summary>
    /// Everything about <paramref name="capability"/> that stops it filling this role. Empty means
    /// it can.
    /// </summary>
    public IReadOnlyList<string> UnmetBy(ScopeCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);

        var unmet = new List<string>();
        var needed = ChannelsNeededOn(capability);

        if (capability.Channels < needed)
        {
            var because = needed > SignalChannels
                ? $" ({SignalChannels} for signals plus one for the trigger, because the "
                  + $"{capability.Model} has no usable external trigger input)"
                : string.Empty;

            unmet.Add($"needs {needed} channels{because}; the {capability.Model} has "
                      + $"{capability.Channels}");
        }

        if (NeedsXy && !capability.HasXyMode)
            unmet.Add($"needs an XY display, which the {capability.Model} does not have");

        if (NeedsFiftyOhm && !capability.Supports(ScopeInputImpedance.FiftyOhm))
            unmet.Add($"needs a 50 ohm input; the {capability.Model} is 1 Mohm only, and an "
                      + "external feedthrough is not the same thing for a rise-time measurement");

        if (capability.MinVoltsPerDivision > FinestVoltsPerDivision + 1e-12)
            unmet.Add($"needs {FinestVoltsPerDivision * 1e3:0.##} mV/div; the {capability.Model} "
                      + $"stops at {capability.MinVoltsPerDivision * 1e3:0.##} mV/div");

        // Judged at the sensitivity the role actually uses, since several scopes give up bandwidth
        // at their most sensitive ranges.
        var available = capability.BandwidthAt(FinestVoltsPerDivision);

        if (MinBandwidthHz > 0 && available < MinBandwidthHz)
            unmet.Add($"needs {MinBandwidthHz / 1e6:0.#} MHz at {FinestVoltsPerDivision * 1e3:0.##} "
                      + $"mV/div; the {capability.Model} gives {available / 1e6:0.#} MHz there");

        return unmet;
    }

    /// <summary>True if <paramref name="capability"/> can fill this role.</summary>
    public bool CanBeFilledBy(ScopeCapability capability) => UnmetBy(capability).Count == 0;

    /// <summary>
    /// Throws unless <paramref name="capability"/> can fill this role, naming everything wrong at
    /// once rather than one thing per attempt.
    /// </summary>
    public void Require(ScopeCapability capability)
    {
        var unmet = UnmetBy(capability);
        if (unmet.Count == 0) return;

        throw new InvalidOperationException(
            $"The {capability.Model} cannot be the {Name}: {string.Join("; ", unmet)}. {Why}");
    }
}
