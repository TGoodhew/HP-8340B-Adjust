using System.Text;
using HP8340B.Instruments.Model;
using HP8340B.Bench.Model;

namespace HP8340B.Bench;

/// <summary>
/// Renders a <see cref="BenchSetup"/> as the hook-up card shown on every setup transition and
/// on demand via <c>setup show &lt;id&gt;</c> (M0-19). Plain text here; the Spectre.Console
/// presentation lives in the CLI so this stays testable and reusable by the report writers.
/// </summary>
public static class HookUpCard
{
    /// <summary>Renders the full card.</summary>
    public static string Render(BenchSetup setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        var card = new StringBuilder();

        card.AppendLine($"{setup.Id} — {setup.Name}");
        if (!string.IsNullOrWhiteSpace(setup.ManualFigure))
            card.AppendLine($"Manual: {setup.ManualFigure}");
        card.AppendLine($"Used by: {setup.UsedBy}");

        Section(card, "CONNECTIONS (in signal order)",
            setup.Connections.Select(c => c + (c.VerifyConnector ? "   [verify-connector]" : "")));

        Section(card, "PHYSICAL SETTINGS (cannot be set over the bus)", setup.PhysicalSettings);
        Section(card, "SAFETY LIMITS", setup.SafetyLimits);

        if (setup.SelfCheck is { } check)
        {
            card.AppendLine();
            card.AppendLine("WIRING SELF-CHECK");
            card.AppendLine($"  {check.Description}");
            card.AppendLine($"  Expect: {check.Expected}");
        }

        if (setup.HasUnverifiedConnectors)
        {
            card.AppendLine();
            card.AppendLine(
                "NOTE: connector names marked [verify-connector] are placeholders taken from the "
                + "service manual's figures. Check them against Section III of the operating manual "
                + "and against the instrument itself before trusting this card.");
        }

        return card.ToString();
    }

    /// <summary>
    /// The card for the routed setups SR and SV: one physical build with switch changes instead
    /// of re-cabling.
    ///
    /// <para>Every leg lists its cable label, slot and channel, and <b>whether it is limited to
    /// 18 GHz</b> — the one thing on this card that changes what a measurement may claim. The
    /// switches' DC and power limits are on every card because both are easy to forget and
    /// neither is recoverable.</para>
    /// </summary>
    /// <param name="map">The routing map.</param>
    /// <param name="campaign">
    /// Which campaign is being run. The second 33311C feeds the detector for adjustment work and
    /// the sensor for verification, so the legs offered differ.
    /// </param>
    public static string RenderRouted(RoutingMap map, RoutingCampaign campaign)
    {
        ArgumentNullException.ThrowIfNull(map);

        var card = new StringBuilder();

        card.AppendLine($"SR / SV — routed bench, {campaign} campaign");
        card.AppendLine(
            "One physical build serves S1, S2, S3 and the detector leg of S4. Setup changes are "
            + "switch transitions, logged, not re-cabling.");

        var legs = map.LegsFor(campaign).ToList();

        Section(card, "RF LEGS (SR)", legs.Where(l => map.Sr.Contains(l)).Select(DescribeLeg));
        Section(card, "LF LEGS (SV)", legs.Where(l => map.Sv.Contains(l)).Select(DescribeLeg));

        var limited = legs.Where(l => !l.Covers(BandId.Band4) && map.Sr.Contains(l)).ToList();

        if (limited.Count > 0)
        {
            card.AppendLine();
            card.AppendLine("BAND LIMITS");
            card.AppendLine(
                $"  - 18 GHz legs: {string.Join(", ", limited.Select(l => l.Id))}. A band-4 result "
                + "through one of these cannot be reported as Spec, and band 3 runs to 20 GHz so "
                + "it does not cover all of that either.");

            var bandFour = legs.Where(l => l.Covers(BandId.Band4)).Select(l => l.Id).ToList();

            card.AppendLine(bandFour.Count > 0
                ? $"  - Full 26.5 GHz legs: {string.Join(", ", bandFour)}."
                : "  - NO leg on this campaign reaches 26.5 GHz. Band 4 needs direct cabling.");
        }

        var unsourced = legs.Where(l => l.MaxHz > 0 && !l.LimitConfirmed).ToList();

        if (unsourced.Count > 0)
        {
            card.AppendLine();
            card.AppendLine("UNCONFIRMED RATINGS");
            card.AppendLine(
                "  - The frequency limit on " + string.Join(", ", unsourced.Select(l => l.Id))
                + " comes from a switch model whose data sheet is not in the local manual library. "
                + "The figure is very probably right, but it decides whether a band-4 result may "
                + "be reported as Spec, so it is shown as unconfirmed until somebody checks it "
                + "against a data sheet or the switch body.");
        }

        var safety = new List<string>();
        if (map.DcLimitNote is { } dc) safety.Add(dc);
        if (map.PowerRatingNote is { } power) safety.Add(power);
        safety.Add("Never switch with the RF on. Set RF off, switch, confirm the readback, then "
                   + "RF on. Coaxial relays switched hot arc, and the damage accumulates "
                   + "invisibly until the insertion loss has drifted.");

        Section(card, "SAFETY LIMITS", safety);

        var placeholders = legs.Where(l => l.Note?.Contains("PLACEHOLDER") == true).ToList();

        if (placeholders.Count > 0)
        {
            card.AppendLine();
            card.AppendLine(
                $"NOTE: {placeholders.Count} leg(s) still carry PLACEHOLDER slot and channel "
                + "numbers — decision D-11. These cannot be read off the bus: the 3499A reports "
                + "\"GP RELAY 44471\" for every 44471/44476/44477 and its guide says to check the "
                + "modules physically. Trace the cables before routing a real measurement.");
        }

        return card.ToString();
    }

    private static string DescribeLeg(RoutedLeg leg)
    {
        var limit = leg.MaxHz > 0
            ? $"to {leg.MaxHz / 1e9:0.#} GHz{(leg.LimitConfirmed ? "" : " [UNCONFIRMED rating]")}"
            : "UNRATED";
        var cables = leg.CableLabels.Count > 0 ? $" [{string.Join(", ", leg.CableLabels)}]" : "";

        return $"{leg.Id}: {leg.Name}{cables} — {leg.SwitchModel}, slot {leg.Slot}, "
               + $"channel(s) {string.Join(",", leg.Channels)}, {limit}";
    }

    private static void Section(StringBuilder card, string title, IEnumerable<string> lines)
    {
        var items = lines.ToList();
        if (items.Count == 0) return;

        card.AppendLine();
        card.AppendLine(title);
        foreach (var line in items) card.AppendLine($"  - {line}");
    }
}
