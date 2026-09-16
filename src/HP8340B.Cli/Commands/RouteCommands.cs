using HP8340B.Bench;
using HP8340B.Bench.Model;
using HP8340B.Instruments.Model;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace HP8340B.Cli.Commands;

/// <summary>Settings for the route commands.</summary>
public sealed class RouteSettings : BenchSettings
{
    [CommandArgument(0, "[leg]")]
    [Description("Leg to route, e.g. SR-A.")]
    public string? Leg { get; set; }

    [CommandOption("--campaign")]
    [Description("Adjustment or Verification. Filters legs wired for the other one.")]
    public string? Campaign { get; set; }
}

/// <summary>Shows every routed leg and what it can carry (M0-22).</summary>
public sealed class RouteStatusCommand : Command<RouteSettings>
{
    public override int Execute(CommandContext context, RouteSettings settings)
    {
        var map = RoutingMap.LoadDefault();

        AnsiConsole.Write(new Rule("[bold]Routing[/]").LeftJustified());

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Leg");
        table.AddColumn("Path");
        table.AddColumn("Switch");
        table.AddColumn("Slot/ch");
        table.AddColumn("To");
        table.AddColumn("Campaign");

        foreach (var leg in map.AllLegs)
        {
            var limit = leg.MaxHz > 0
                ? leg.Covers(BandId.Band4)
                    ? $"[green]{leg.MaxHz / 1e9:0.#} GHz[/]"
                    : $"[yellow]{leg.MaxHz / 1e9:0.#} GHz[/]"
                : "[red]UNRATED[/]";

            table.AddRow(
                leg.Id,
                Markup.Escape(leg.Name),
                Markup.Escape(leg.SwitchModel),
                $"{leg.Slot}/{string.Join(",", leg.Channels)}",
                limit,
                leg.Campaign == RoutingCampaign.Any ? "" : leg.Campaign.ToString());
        }

        AnsiConsole.Write(table);

        var bandFour = map.BandFourCapableLegs().ToList();

        AnsiConsole.MarkupLine(
            $"\n[bold]{bandFour.Count}[/] leg(s) can carry band 4: "
            + Markup.Escape(string.Join(", ", bandFour.Select(l => l.Id))) + ". "
            + "Everything else is limited to 18 GHz and cannot report a band-4 result as Spec.");

        if (map.DcLimitNote is { } dc) AnsiConsole.MarkupLine($"\n[yellow]DC:[/] {Markup.Escape(dc)}");
        if (map.PowerRatingNote is { } pw) AnsiConsole.MarkupLine($"[yellow]Power:[/] {Markup.Escape(pw)}");

        var placeholders = map.AllLegs.Count(l => l.Note?.Contains("PLACEHOLDER") == true);

        if (placeholders > 0)
            AnsiConsole.MarkupLine(
                $"\n[red]{placeholders} leg(s) still carry PLACEHOLDER slot and channel numbers.[/] "
                + "That is decision D-11, and it cannot be read off the bus: the 3499A reports "
                + "\"GP RELAY 44471\" for every 44471/44476/44477, and its guide says to check the "
                + "modules physically. Trace the cables and edit Data/routing.json.");

        return 0;
    }
}

/// <summary>Prints the routed hook-up card for a campaign (M0-19 and M0-22).</summary>
public sealed class RouteCardCommand : Command<RouteSettings>
{
    public override int Execute(CommandContext context, RouteSettings settings)
    {
        var campaign = RoutingCampaign.Any;

        if (!string.IsNullOrWhiteSpace(settings.Campaign)
            && !Enum.TryParse(settings.Campaign, ignoreCase: true, out campaign))
        {
            AnsiConsole.MarkupLine(
                $"[red]Unknown campaign '{Markup.Escape(settings.Campaign)}'.[/] "
                + "Use Adjustment or Verification.");
            return 1;
        }

        var map = RoutingMap.LoadDefault();

        AnsiConsole.Write(new Panel(Markup.Escape(HookUpCard.RenderRouted(map, campaign)))
            .Header($"[bold]Hook-up card SR / SV — {campaign}[/]")
            .Border(BoxBorder.Rounded)
            .Expand());

        return 0;
    }
}

/// <summary>Shows one leg in full, as a hook-up card would (M0-22).</summary>
public sealed class RouteShowCommand : Command<RouteSettings>
{
    public override int Execute(CommandContext context, RouteSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Leg))
        {
            AnsiConsole.MarkupLine("[red]Name a leg, e.g. [bold]hp8340b route SR-A[/].[/]");
            return 1;
        }

        var map = RoutingMap.LoadDefault();

        RoutedLeg leg;
        try
        {
            leg = map.Require(settings.Leg);
        }
        catch (ArgumentException ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        AnsiConsole.Write(new Rule($"[bold]{Markup.Escape(leg.Id)}[/]").LeftJustified());
        AnsiConsole.MarkupLine(Markup.Escape(leg.Describe()));

        AnsiConsole.MarkupLine(
            $"\nBand coverage: " + string.Join("  ", Bands.All.Select(b =>
                leg.Covers(b.Id) ? $"[green]B{(int)b.Id} ok[/]" : $"[red]B{(int)b.Id} no[/]")));

        if (map.DcLimitNote is { } dc) AnsiConsole.MarkupLine($"\n[yellow]DC:[/] {Markup.Escape(dc)}");
        if (map.PowerRatingNote is { } pw) AnsiConsole.MarkupLine($"[yellow]Power:[/] {Markup.Escape(pw)}");

        return 0;
    }
}
