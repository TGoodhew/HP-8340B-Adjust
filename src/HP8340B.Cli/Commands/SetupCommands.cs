using HP8340B.Bench;
using HP8340B.Bench.Model;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace HP8340B.Cli.Commands;

/// <summary>Finds the setup catalogue shipped beside the executable.</summary>
internal static class Catalogue
{
    public static SetupCatalogue Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "setups.json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Setup catalogue not found at {path}.");
        return SetupCatalogue.Load(path);
    }
}

/// <summary>Lists every bench setup (M0-19).</summary>
public sealed class SetupListCommand : Command<BenchSettings>
{
    public override int Execute(CommandContext context, BenchSettings settings)
    {
        var catalogue = Catalogue.Load();

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("ID");
        table.AddColumn("Setup");
        table.AddColumn("Conns");
        table.AddColumn("Used by");

        foreach (var setup in catalogue.Setups)
        {
            table.AddRow(setup.Id, Markup.Escape(setup.Name),
                setup.Connections.Count.ToString(),
                Markup.Escape(Truncate(setup.UsedBy, 60)));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("\nRun [bold]hp8340b setup show <id>[/] for a hook-up card.");
        return 0;
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "...";
}

/// <summary>Arguments for <c>setup show</c>.</summary>
public sealed class SetupShowSettings : BenchSettings
{
    [CommandArgument(0, "<ID>")]
    [Description("Setup identifier, e.g. S4.")]
    public string Id { get; init; } = "";
}

/// <summary>Prints the hook-up card for one setup (M0-19).</summary>
public sealed class SetupShowCommand : Command<SetupShowSettings>
{
    public override int Execute(CommandContext context, SetupShowSettings settings)
    {
        var catalogue = Catalogue.Load();
        var setup = catalogue.ById(settings.Id);

        if (setup is null)
        {
            AnsiConsole.MarkupLine($"[red]No setup {Markup.Escape(settings.Id)}.[/] "
                + $"Known: {string.Join(", ", catalogue.Setups.Select(s => s.Id))}");
            return 1;
        }

        AnsiConsole.Write(new Panel(Markup.Escape(HookUpCard.Render(setup)))
            .Header($"[bold]Hook-up card {setup.Id}[/]")
            .Border(BoxBorder.Rounded)
            .Expand());

        if (setup.SelfCheck is not null)
            AnsiConsole.MarkupLine(
                SelfChecks.Implemented.Contains(setup.Id)
                    ? "\n[green]This setup has an executable wiring self-check.[/] A block will "
                      + "not start until the card is acknowledged and the check passes."
                    : "\n[yellow]This setup's self-check is prose only - there is no executable "
                      + "check for it yet.[/] That is reported as NotRun, not as a pass, so a "
                      + "block gated on it will refuse to start.");
        return 0;
    }
}
