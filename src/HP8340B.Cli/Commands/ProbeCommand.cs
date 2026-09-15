using HP8340B.Instruments;
using HP8340B.Instruments.Config;
using HP8340B.Instruments.Model;
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
        table.AddColumn("Ref");
        table.AddColumn("Identity / detail");

        var failures = 0;
        var missingBorrowed = new List<InstrumentConfig>();
        var onInternalReference = new List<string>();
        var referenceUnconfirmed = new List<string>();

        foreach (var instrumentConfig in bench.Instruments)
        {
            if (!instrumentConfig.Present)
            {
                table.AddRow(instrumentConfig.Role, instrumentConfig.Model,
                    Markup.Escape(instrumentConfig.Address), "[grey]not on bench[/]", "",
                    Markup.Escape(instrumentConfig.Note ?? ""));
                continue;
            }

            if (!instrumentConfig.Probeable)
            {
                table.AddRow(instrumentConfig.Role, instrumentConfig.Model,
                    Markup.Escape(instrumentConfig.Address), "[blue]passive[/]", "",
                    Markup.Escape(instrumentConfig.Note ?? "Not on the bus."));
                continue;
            }

            if (instrumentConfig.AddressUnknown && !settings.Simulate)
            {
                table.AddRow(instrumentConfig.Role, instrumentConfig.Model, "[yellow]TBD[/]",
                    "[yellow]unconfigured[/]", "",
                    "Address not set - see the decision issues.");
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

            if (!result.Responded)
            {
                failures++;
                if (instrumentConfig.Borrowed) missingBorrowed.Add(instrumentConfig);
            }
            else
            {
                // Only meaningful for an instrument that answered: a dead instrument reports null
                // for everything, which is not the same as "cannot tell me".
                if (result.ExternalReference == false) onInternalReference.Add(result.Model);
                else if (result.ReferenceUnconfirmed) referenceUnconfirmed.Add(result.Model);
            }

            table.AddRow(result.Role, result.Model, Markup.Escape(result.Address),
                result.Responded ? "[green]ok[/]" : "[red]no response[/]",
                ReferenceCell(result),
                Markup.Escape(DescribeResult(result)));
        }

        AnsiConsole.Write(table);

        var unverified = Hp8340BCommands.Unverified.Count();
        if (unverified > 0)
        {
            AnsiConsole.MarkupLine(
                $"\n[yellow]{unverified} HP-IB code(s) are still Unverified[/] and will throw if used. "
                + "Run [bold]hp8340b codes[/] to list them.");
        }

        ReportReference(onInternalReference, referenceUnconfirmed);

        foreach (var borrowed in missingBorrowed)
        {
            AnsiConsole.MarkupLine(
                $"\n[yellow]{Markup.Escape(borrowed.Model)} ({Markup.Escape(borrowed.Role)}) is "
                + "borrowed kit marked present, and did not answer.[/] If it has gone back, set "
                + "[bold]present: false[/] in bench.json rather than leaving it as a failure - the "
                + "measurements that depend on it will then say so by name.");
        }

        if (failures > 0)
        {
            AnsiConsole.MarkupLine($"\n[red]{failures} instrument(s) did not respond.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine("\n[green]Every configured instrument responded.[/]");
        return 0;
    }

    /// <summary>
    /// The reference column. Three states, kept visually distinct because they mean different
    /// things: on the house standard, demonstrably not, and cannot be asked.
    /// </summary>
    internal static string ReferenceCell(ProbeResult result) => result switch
    {
        { Responded: false } => "",
        { ExternalReference: true } => "[green]EXT[/]",
        { ExternalReference: false } => "[red]INT[/]",
        { ReferenceApplies: true } => "[yellow]?[/]",
        // A scope, a DMM or a switch driver has no 10 MHz reference worth reporting. Blank, not
        // "?", so the question marks that remain are the ones worth walking over to look at.
        _ => "",
    };

    /// <summary>
    /// Identity and detail together. The earlier version showed detail only when there was no
    /// identity, which hid exactly the sentences worth reading - the 8563E reports which
    /// reference it is on in its detail, and its identity is only a model string.
    /// </summary>
    internal static string DescribeResult(ProbeResult result) =>
        string.Join(" - ", new[] { result.Identity, result.Detail }
            .Where(p => !string.IsNullOrWhiteSpace(p)));

    private static void ReportReference(
        IReadOnlyList<string> onInternal, IReadOnlyList<string> unconfirmed)
    {
        // Deliberately a warning and not a failure. Being off the house standard invalidates 4-2
        // and every frequency-accuracy number, but it does not stop a power or squegging run, and
        // failing probe here would block work that has no reason to care (repo rule 4 gates
        // hardware runs on probe succeeding).
        if (onInternal.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"\n[red]On an internal reference: {Markup.Escape(string.Join(", ", onInternal))}.[/] "
                + "Frequency-accuracy results (4-2, 4-4) are not valid until these share the "
                + "Z3805A with the DUT.");
        }

        if (unconfirmed.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"\n[yellow]Reference unconfirmed: {Markup.Escape(string.Join(", ", unconfirmed))}.[/] "
                + "These have no command that reads the reference state back - check the front "
                + "panel before trusting a frequency measurement.");
        }
    }
}
