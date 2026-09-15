using System.Text.Json;
using System.Text.Json.Serialization;

namespace HP8340B.Bench.Model;

/// <summary>
/// One physical connection in signal order, DUT port to instrument port.
/// Connector names carry a <see cref="VerifyConnector"/> flag until they have been checked
/// against Section III of the operating manual and the instrument itself.
/// </summary>
public sealed class Connection
{
    /// <summary>Source port, e.g. "8340B RF OUTPUT (3.5 mm m)".</summary>
    public string From { get; set; } = "";

    /// <summary>Destination port, e.g. "8563E RF INPUT (3.5 mm f)".</summary>
    public string To { get; set; } = "";

    /// <summary>Adapters, pads and cable labels between the two, in order.</summary>
    public List<string> Via { get; set; } = new();

    /// <summary>True while the connector names here are still unverified (label: verify-connector).</summary>
    public bool VerifyConnector { get; set; } = true;

    /// <summary>Renders the connection as one hook-up card line.</summary>
    public override string ToString()
    {
        var parts = new List<string> { From };
        parts.AddRange(Via);
        parts.Add(To);
        return string.Join(" -> ", parts);
    }
}

/// <summary>
/// A check run before a block starts, proving the wiring is what the card says. States what it
/// expects and what it saw, and stops the block on failure (M0-20).
/// </summary>
public sealed class WiringSelfCheck
{
    /// <summary>What the check does, e.g. "DUT CW 1 GHz at 0 dBm; read the 8563E marker".</summary>
    public string Description { get; set; } = "";

    /// <summary>The passing condition in words, e.g. "marker within 0 dBm +/- 2 dB (plus pad)".</summary>
    public string Expected { get; set; } = "";
}

/// <summary>
/// One bench configuration. Setups are data, editable without code (D.4). Every measurement,
/// test and guided procedure declares the setup it needs.
/// </summary>
public sealed class BenchSetup
{
    /// <summary>Setup identifier, S0 through S10, or a routed setup SR/SV.</summary>
    public string Id { get; set; } = "";

    /// <summary>Short name, e.g. "DUT RF OUTPUT to 8563E".</summary>
    public string Name { get; set; } = "";

    /// <summary>The manual figure this corresponds to, where there is one.</summary>
    public string? ManualFigure { get; set; }

    /// <summary>What uses this setup.</summary>
    public string UsedBy { get; set; } = "";

    /// <summary>Connections in signal order.</summary>
    public List<Connection> Connections { get; set; } = new();

    /// <summary>Settings that cannot be made over the bus — switches, jumpers, dials, feedthroughs.</summary>
    public List<string> PhysicalSettings { get; set; } = new();

    /// <summary>Maximum inputs, required pads, and the warnings from docs/SAFETY.md that apply here.</summary>
    public List<string> SafetyLimits { get; set; } = new();

    /// <summary>The check that proves this wiring before any measurement runs.</summary>
    public WiringSelfCheck? SelfCheck { get; set; }

    /// <summary>True if any connector name in this setup is still unverified.</summary>
    [JsonIgnore]
    public bool HasUnverifiedConnectors => Connections.Any(c => c.VerifyConnector);
}

/// <summary>The setup catalogue, loaded from setups.json.</summary>
public sealed class SetupCatalogue
{
    public List<BenchSetup> Setups { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static SetupCatalogue Load(string path) =>
        JsonSerializer.Deserialize<SetupCatalogue>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"{path} did not parse as a setup catalogue.");

    public BenchSetup? ById(string id) =>
        Setups.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
