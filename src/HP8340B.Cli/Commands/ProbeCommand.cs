using HP8340B.Instruments;
using HP8340B.Instruments.Config;
using HP8340B.Measurements.Model;
using Spectre.Console;
using Spectre.Console.Cli;

namespace HP8340B.Cli.Commands;

/// <summary>
/// M0-11: walks the configured bench, confirms each instrument responds, prints addresses and
/// identities, and checks the external-reference state where the instrument can report it.
/// Every hardware run is gated on this succeeding (repo rule 4).
/// </summary>
public sealed class ProbeCommand : Command<BenchSettings>
{
    public override int Execute(CommandContext context, BenchSettings settings)
    {
        var bench = BenchConfig.Load(settings.ResolveBenchPath());

        AnsiConsole.Write(new Rule(
            $"[bold]HP 8340B bench[/] - {(settings.Simulate ? "SIMULATED" : "HARDWARE")}").LeftJustified());

        var option = Enum.TryParse<InstrumentOption>(bench.DutOption, ignoreCase: true, out var parsed)
            ? parsed
            : InstrumentOption.Standard;

        AnsiConsole.MarkupLine($"DUT option: [bold]{option}[/]   serial: [bold]{bench.DutSerial}[/]");
        AnsiConsole.MarkupLine("Max leveled power: " + string.Join("  ", Bands.All.Select(b =>
            $"B{(int)b.Id} {MaxLeveledPower.ForFrequency(option, b.StartGHz + 0.001):+0.0;-0.0} dBm")));
        AnsiConsole.WriteLine();

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Role");
        table.AddColumn("Model");
        table.AddColumn("Address");
        table.AddColumn("Status");
        table.AddColumn("Identity / detail");

        var failures = 0;

        foreach (var instrumentConfig in bench.Instruments)
        {
            if (!instrumentConfig.Present)
            {
                table.AddRow(instrumentConfig.Role, instrumentConfig.Model,
                    Markup.Escape(instrumentConfig.Address), "[grey]not on bench[/]",
                    Markup.Escape(instrumentConfig.Note ?? ""));
                continue;
            }

            if (!instrumentConfig.Probeable)
            {
                table.AddRow(instrumentConfig.Role, instrumentConfig.Model,
                    Markup.Escape(instrumentConfig.Address), "[blue]passive[/]",
                    Markup.Escape(instrumentConfig.Note ?? "Not on the bus."));
                continue;
            }

            if (instrumentConfig.AddressUnknown && !settings.Simulate)
            {
                table.AddRow(instrumentConfig.Role, instrumentConfig.Model, "[yellow]TBD[/]",
                    "[yellow]unconfigured[/]", "Address not set - see the decision issues.");
                failures++;
                continue;
            }

            ProbeResult result;
            try
            {
                using var instrument = InstrumentFactory.Create(instrumentConfig, settings.Simulate);
                result = instrument.Probe();
            }
            catch (Exception ex)
            {
                result = new ProbeResult(instrumentConfig.Role, instrumentConfig.Model,
                    instrumentConfig.Address, Responded: false, Detail: ex.Message);
            }

            if (!result.Responded) failures++;

            table.AddRow(result.Role, result.Model, Markup.Escape(result.Address),
                result.Responded ? "[green]ok[/]" : "[red]no response[/]",
                Markup.Escape(result.Identity ?? result.Detail ?? ""));
        }

        AnsiConsole.Write(table);

        var unverified = Hp8340BCommands.Unverified.Count();
        if (unverified > 0)
        {
            AnsiConsole.MarkupLine(
                $"\n[yellow]{unverified} HP-IB code(s) are still Unverified[/] and will throw if used. "
                + "Run [bold]hp8340b codes[/] to list them.");
        }

        if (failures > 0)
        {
            AnsiConsole.MarkupLine($"\n[red]{failures} instrument(s) did not respond.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine("\n[green]Every configured instrument responded.[/]");
        return 0;
    }
}
