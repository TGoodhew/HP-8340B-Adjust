using System.ComponentModel;
using Spectre.Console.Cli;

namespace HP8340B.Cli.Commands;

/// <summary>Options every command shares.</summary>
public class BenchSettings : CommandSettings
{
    [CommandOption("--sim")]
    [Description("Run against simulated instruments. No hardware required (repo rule 4).")]
    public bool Simulate { get; init; }

    [CommandOption("--bench <PATH>")]
    [Description("Path to bench.json. Defaults to bench.json beside the executable.")]
    public string? BenchPath { get; init; }

    /// <summary>Resolves the bench file path, falling back to the copy beside the executable.</summary>
    public string ResolveBenchPath()
    {
        if (!string.IsNullOrWhiteSpace(BenchPath)) return BenchPath;

        var beside = Path.Combine(AppContext.BaseDirectory, "bench.json");
        if (File.Exists(beside)) return beside;

        throw new FileNotFoundException(
            "No bench configuration found. Pass --bench <path> or put bench.json beside the executable.");
    }
}
