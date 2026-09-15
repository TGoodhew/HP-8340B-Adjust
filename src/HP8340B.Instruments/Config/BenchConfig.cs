using System.Text.Json;
using System.Text.Json.Serialization;

namespace HP8340B.Instruments.Config;

/// <summary>One instrument as configured. "TBD" in <see cref="Address"/> means the address is
/// still unknown — <c>probe</c> reports it as unconfigured rather than trying to open it.</summary>
public sealed class InstrumentConfig
{
    /// <summary>Config role, e.g. "dut", "spectrum-analyzer". Unique within the bench.</summary>
    public string Role { get; set; } = "";

    /// <summary>Model designation, e.g. "HP 8563E".</summary>
    public string Model { get; set; } = "";

    /// <summary>VISA resource string, or "TBD" if not yet known.</summary>
    public string Address { get; set; } = "TBD";

    /// <summary>False for borrowed or optional kit that is not on the bench right now.</summary>
    public bool Present { get; set; } = true;

    /// <summary>Free-text note carried into the session record.</summary>
    public string? Note { get; set; }

    /// <summary>
    /// I/O timeout in milliseconds. Null uses <see cref="Visa.VisaInstrumentLink.DefaultTimeout"/>.
    /// Set it where an instrument is genuinely slow: the 8902A averages for up to 10 s, and a 2 s
    /// DUT sweep plus settling is longer again. A flat value across the bench would either time
    /// out on those or leave a dead instrument hanging for ten seconds.
    /// </summary>
    public int? TimeoutMs { get; set; }

    /// <summary>
    /// False for passive kit that is not on the bus at all — power sensors that plug into a
    /// meter, splitters, pads. These are listed so the session records what produced a reading
    /// and so drivers can enforce the right frequency range and power limit, but `probe` reports
    /// them rather than trying to open a VISA session to them.
    /// </summary>
    public bool Probeable { get; set; } = true;

    /// <summary>True when the address has not been filled in yet.</summary>
    [JsonIgnore]
    public bool AddressUnknown =>
        string.IsNullOrWhiteSpace(Address) ||
        Address.Equals("TBD", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The whole bench as data. Loaded from <c>bench.json</c> in the repo, optionally overlaid by a
/// git-ignored <c>bench.local.json</c> so real addresses never have to be committed.
/// </summary>
public sealed class BenchConfig
{
    /// <summary>
    /// DUT output option — sets the max-leveled-power limits (manual Table 4-9).
    /// One of Standard, Opt001, Opt004, Opt005.
    /// </summary>
    public string DutOption { get; set; } = "Standard";

    /// <summary>DUT serial number, for the session record. "TBD" until read off the instrument.</summary>
    public string DutSerial { get; set; } = "TBD";

    /// <summary>Every instrument the project knows about, present or not.</summary>
    public List<InstrumentConfig> Instruments { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Loads <paramref name="path"/>, then overlays <c>bench.local.json</c> beside it if present.
    /// The overlay matches on Role and replaces whole entries, so a local file can fill in
    /// addresses without restating the rest of the bench.
    /// </summary>
    public static BenchConfig Load(string path)
    {
        var config = JsonSerializer.Deserialize<BenchConfig>(File.ReadAllText(path), JsonOptions)
                     ?? throw new InvalidDataException($"{path} did not parse as a bench configuration.");

        var localPath = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", "bench.local.json");

        if (File.Exists(localPath))
        {
            var local = JsonSerializer.Deserialize<BenchConfig>(File.ReadAllText(localPath), JsonOptions);
            if (local is not null) config.Overlay(local);
        }

        return config;
    }

    private void Overlay(BenchConfig other)
    {
        if (!string.IsNullOrWhiteSpace(other.DutOption)) DutOption = other.DutOption;
        if (!string.IsNullOrWhiteSpace(other.DutSerial)) DutSerial = other.DutSerial;

        foreach (var instrument in other.Instruments)
        {
            var index = Instruments.FindIndex(
                i => i.Role.Equals(instrument.Role, StringComparison.OrdinalIgnoreCase));

            if (index >= 0) Instruments[index] = instrument;
            else Instruments.Add(instrument);
        }
    }

    /// <summary>The instrument in <paramref name="role"/>, or null.</summary>
    public InstrumentConfig? ByRole(string role) =>
        Instruments.FirstOrDefault(i => i.Role.Equals(role, StringComparison.OrdinalIgnoreCase));
}
