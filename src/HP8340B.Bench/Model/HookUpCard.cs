using System.Text;
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

    private static void Section(StringBuilder card, string title, IEnumerable<string> lines)
    {
        var items = lines.ToList();
        if (items.Count == 0) return;

        card.AppendLine();
        card.AppendLine(title);
        foreach (var line in items) card.AppendLine($"  - {line}");
    }
}
