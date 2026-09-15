using HP8340B.Instruments;
using Spectre.Console;
using Spectre.Console.Cli;

namespace HP8340B.Cli.Commands;

/// <summary>
/// Prints the 8340B HP-IB code table. Repo rule 2: never fabricate an HP-IB code - anything
/// Unverified here throws if a driver tries to send it.
/// </summary>
public sealed class CodesCommand : Command<BenchSettings>
{
    public override int Execute(CommandContext context, BenchSettings settings)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Code");
        table.AddColumn("Front-panel key");
        table.AddColumn("Status");
        table.AddColumn("Source / what to check");

        foreach (var code in Hp8340BCommands.All)
        {
            table.AddRow(
                $"[bold]{code.Code}[/]",
                Markup.Escape(code.FrontPanelKey),
                code.Status == CodeStatus.Verified ? "[green]Verified[/]" : "[yellow]Unverified[/]",
                Markup.Escape(code.Source));
        }

        AnsiConsole.Write(table);

        var unverified = Hp8340BCommands.Unverified.ToList();
        AnsiConsole.MarkupLine(
            $"\n[bold]{Hp8340BCommands.All.Count - unverified.Count}[/] verified, "
            + $"[bold]{unverified.Count}[/] unverified.");

        if (unverified.Count > 0)
        {
            AnsiConsole.MarkupLine(
                "Unverified codes must be checked against Tables 3-1/3-2 of the [bold]operating[/] "
                + "manual before use. They throw rather than reaching the instrument.");
        }

        return 0;
    }
}
