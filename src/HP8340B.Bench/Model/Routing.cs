using System.Text.Json;
using System.Text.Json.Serialization;
using HP8340B.Instruments;
using HP8340B.Instruments.Model;

namespace HP8340B.Bench.Model;

/// <summary>
/// Which campaign a routed leg belongs to.
///
/// <para>The second 33311C is shared: for adjustment work it feeds the crystal detector, for
/// verification work it feeds the power sensor. It cannot do both at once, so which one is
/// wired is declared and the planner knows.</para>
/// </summary>
public enum RoutingCampaign
{
    /// <summary>Wired for either campaign.</summary>
    Any,

    /// <summary>5-14 and 5-16 — the detector leg.</summary>
    Adjustment,

    /// <summary>Section IV — the sensor leg.</summary>
    Verification,
}

/// <summary>
/// One routed path through the 3499A: a leg of the RF tree (SR) or the LF multiplexers (SV).
///
/// <para>The point of routing is that one physical build serves S1, S2, S3 and the detector leg
/// of S4, so a setup change becomes a logged switch transition rather than re-cabling. That only
/// works if each leg carries what it is actually capable of.</para>
/// </summary>
public sealed class RoutedLeg
{
    /// <summary>Leg identifier, e.g. "SR-A".</summary>
    public string Id { get; set; } = "";

    /// <summary>Where this leg goes, e.g. "DUT RF OUT to 8563E".</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// The coaxial switch this leg passes through, e.g. "33311C" or "33311B".
    ///
    /// <para><b>This is declared, not discovered.</b> The 3499A cannot tell which switch is on
    /// which driver channel — see <see cref="SwitchModule.IsAmbiguous"/> — so it has to be read
    /// off the bench and written down.</para>
    /// </summary>
    public string SwitchModel { get; set; } = "";

    /// <summary>Mainframe slot the driver module is in. Part of decision D-11.</summary>
    public int Slot { get; set; }

    /// <summary>Channels to close for this leg.</summary>
    public List<int> Channels { get; set; } = new();

    /// <summary>Cable labels along the leg, for the hook-up card.</summary>
    public List<string> CableLabels { get; set; } = new();

    /// <summary>Which campaign this leg is wired for.</summary>
    public RoutingCampaign Campaign { get; set; } = RoutingCampaign.Any;

    /// <summary>Anything else worth knowing at the bench.</summary>
    public string? Note { get; set; }

    /// <summary>
    /// The highest frequency this leg is good for, from the switch it passes through.
    ///
    /// <para>Legs through a 33311B stop at 18 GHz; only a 33311C reaches 26.5 GHz.</para>
    /// </summary>
    [JsonIgnore]
    public double MaxHz => SwitchModule.MaxHzForSwitch(SwitchModel);

    /// <summary>
    /// False when this leg's frequency limit has no source in the local manual library.
    ///
    /// <para>It still constrains what the leg may claim — an unsourced 18 GHz is better than no
    /// limit at all — but it is shown as unconfirmed rather than printed as a fact, because this
    /// number decides whether a band-4 result may be reported as Spec.</para>
    /// </summary>
    [JsonIgnore]
    public bool LimitConfirmed => SwitchModule.RatingForSwitch(SwitchModel).Confirmed;

    /// <summary>Where this leg's frequency limit comes from, or why it has no source.</summary>
    [JsonIgnore]
    public string LimitSource => SwitchModule.RatingForSwitch(SwitchModel).Source;

    /// <summary>
    /// The traceability class a measurement at <paramref name="hz"/> can claim through this leg.
    ///
    /// <para><b>This is the rule that stops a band-4 result being reported as Spec through an
    /// 18 GHz leg.</b> The switch does not stop passing signal above its rating — it passes it
    /// with an insertion loss and a match nobody has characterised, which produces a plausible
    /// number rather than an obvious failure.</para>
    /// </summary>
    public TraceabilityClass TraceabilityAt(double hz) =>
        MaxHz > 0 && hz <= MaxHz ? TraceabilityClass.Spec : TraceabilityClass.NotPossible;

    /// <summary>True if this leg can carry every frequency in <paramref name="band"/>.</summary>
    public bool Covers(BandId band)
    {
        var edges = Bands.Get(band);
        return TraceabilityAt(edges.StopGHz * 1e9) == TraceabilityClass.Spec;
    }

    /// <summary>A hook-up card line for this leg.</summary>
    public string Describe()
    {
        var limit = MaxHz > 0
            ? $"{MaxHz / 1e9:0.#} GHz{(LimitConfirmed ? "" : " [unconfirmed rating]")}"
            : "UNRATED";
        var cables = CableLabels.Count > 0 ? $" [{string.Join(", ", CableLabels)}]" : "";
        var channels = Channels.Count > 0 ? string.Join(",", Channels) : "none";

        return $"{Id}  {Name}{cables} — {SwitchModel}, slot {Slot}, ch {channels}, to {limit}"
               + (Campaign == RoutingCampaign.Any ? "" : $", {Campaign} campaign only")
               + (Note is null ? "" : $". {Note}");
    }
}

/// <summary>
/// The routed bench: the RF tree and the LF multiplexers, as data.
/// </summary>
public sealed class RoutingMap
{
    /// <summary>
    /// The switches' DC limit. Carried here because it belongs on every hook-up card: the
    /// coaxial switches are not DC-blocking and the DUT's sweep ramp is not RF.
    /// </summary>
    public string? DcLimitNote { get; set; }

    /// <summary>The switches' average power rating, likewise for the card.</summary>
    public string? PowerRatingNote { get; set; }

    /// <summary>RF tree legs.</summary>
    public List<RoutedLeg> Sr { get; set; } = new();

    /// <summary>LF multiplexer legs.</summary>
    public List<RoutedLeg> Sv { get; set; } = new();

    /// <summary>Every leg, RF and LF.</summary>
    [JsonIgnore]
    public IEnumerable<RoutedLeg> AllLegs => Sr.Concat(Sv);

    /// <summary>The leg with this id, or null.</summary>
    public RoutedLeg? Leg(string id) =>
        AllLegs.FirstOrDefault(l => l.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The leg with this id, or an exception naming what is available. Used where a typo would
    /// otherwise silently route nothing.
    /// </summary>
    public RoutedLeg Require(string id) =>
        Leg(id) ?? throw new ArgumentException(
            $"No routed leg '{id}'. Known legs: {string.Join(", ", AllLegs.Select(l => l.Id))}.",
            nameof(id));

    /// <summary>
    /// Legs that are usable for <paramref name="campaign"/> — those declared for it, plus those
    /// wired for either.
    /// </summary>
    public IEnumerable<RoutedLeg> LegsFor(RoutingCampaign campaign) =>
        AllLegs.Where(l => l.Campaign == RoutingCampaign.Any || l.Campaign == campaign);

    /// <summary>
    /// Legs that can carry band 4. On this bench there are only two 33311C switches, so this is
    /// the scarce resource the whole routing plan is built around.
    /// </summary>
    public IEnumerable<RoutedLeg> BandFourCapableLegs() =>
        AllLegs.Where(l => l.Covers(BandId.Band4));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Loads the routing map from its data file.</summary>
    public static RoutingMap Load(string path) =>
        JsonSerializer.Deserialize<RoutingMap>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"{path} did not parse as a routing map.");

    /// <summary>Loads the map that ships with the tool.</summary>
    public static RoutingMap LoadDefault()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "routing.json");

        return File.Exists(path)
            ? Load(path)
            : throw new FileNotFoundException(
                "routing.json was not found beside the tool. It carries the slot and channel map, "
                + "which is decision D-11 and has to be read off the bench.", path);
    }
}

/// <summary>
/// Decides what a routed measurement may claim, and refuses the combinations that would produce
/// a plausible wrong number.
/// </summary>
public static class RoutingRules
{
    /// <summary>
    /// The traceability a measurement at <paramref name="hz"/> can claim through
    /// <paramref name="leg"/>, given what the instrument itself could claim.
    ///
    /// <para>The leg can only ever make it worse. A <c>Spec</c> sensor reading routed through an
    /// 18 GHz leg at 24 GHz is not <c>Spec</c>.</para>
    /// </summary>
    public static TraceabilityClass Combine(TraceabilityClass instrument, RoutedLeg leg, double hz)
    {
        ArgumentNullException.ThrowIfNull(leg);

        var legClass = leg.TraceabilityAt(hz);

        // Worse of the two. The enum is ordered best to worst.
        return (TraceabilityClass)Math.Max((int)instrument, (int)legClass);
    }

    /// <summary>
    /// Refuses a routed measurement the leg cannot support, with a message that says why rather
    /// than just failing.
    /// </summary>
    public static void Guard(RoutedLeg leg, double hz)
    {
        ArgumentNullException.ThrowIfNull(leg);

        if (leg.TraceabilityAt(hz) == TraceabilityClass.Spec) return;

        var limit = leg.MaxHz > 0
            ? $"{leg.MaxHz / 1e9:0.#} GHz"
            : $"unrated — '{leg.SwitchModel}' is not a switch this project knows the rating of";

        throw new InvalidOperationException(
            $"Leg {leg.Id} ({leg.SwitchModel}) is good to {limit}, and this measurement is at "
            + $"{hz / 1e9:0.###} GHz. The switch does not stop passing signal above its rating — it "
            + "passes it with an insertion loss and a match nobody has characterised, which gives a "
            + "plausible number rather than an obvious failure. Route through a 33311C leg, or "
            + "cable the measurement directly.");
    }
}
