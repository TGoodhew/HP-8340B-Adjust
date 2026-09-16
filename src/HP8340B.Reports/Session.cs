using System.Text.Json;
using System.Text.Json.Serialization;
using HP8340B.Instruments.Model;

namespace HP8340B.Reports;

/// <summary>An instrument as it was found at the start of a run.</summary>
/// <param name="Role">Config role.</param>
/// <param name="Model">Model.</param>
/// <param name="Address">Address actually used.</param>
/// <param name="Responded">Whether it answered.</param>
/// <param name="Identity">What it said it was.</param>
/// <param name="ExternalReference">Reference state, where it could be read.</param>
/// <param name="Serial">Serial number, where known. Required for borrowed kit (D-10).</param>
/// <param name="Borrowed">True for kit that is not this bench's.</param>
public sealed record SessionInstrument(
    string Role,
    string Model,
    string Address,
    bool Responded,
    string? Identity,
    bool? ExternalReference,
    string? Serial,
    bool Borrowed);

/// <summary>
/// One measurement, with everything needed to defend it a year later (repo rule 3).
/// </summary>
/// <param name="What">What was measured.</param>
/// <param name="Hz">Frequency.</param>
/// <param name="Value">The number.</param>
/// <param name="Unit">Its unit.</param>
/// <param name="Traceability">How far it can be trusted.</param>
/// <param name="Instrument">Which instrument produced it.</param>
/// <param name="Settings">
/// The instrument settings in force. Free text, but it has to be there: a reading with no record
/// of the resolution bandwidth or the cal factor is not defensible.
/// </param>
/// <param name="RoutedLeg">Which routed leg it came through, if any — an 18 GHz leg caps the claim.</param>
/// <param name="At">When.</param>
/// <param name="Note">Anything qualifying it.</param>
public sealed record SessionMeasurement(
    string What,
    double Hz,
    double Value,
    string Unit,
    TraceabilityClass Traceability,
    string Instrument,
    string Settings,
    string? RoutedLeg,
    DateTime At,
    string? Note = null);

/// <summary>
/// A write to the DUT. Repo rule 1: every one logged with its timestamp, old and new value.
/// </summary>
/// <param name="What">What was written.</param>
/// <param name="OldValue">What it was before, where that could be read.</param>
/// <param name="NewValue">What it became.</param>
/// <param name="ConfirmedBy">Who authorised this specific action.</param>
/// <param name="At">When.</param>
public sealed record SessionWrite(
    string What,
    string? OldValue,
    string NewValue,
    string ConfirmedBy,
    DateTime At);

/// <summary>
/// Conditions the results depend on and nothing in the instruments records.
/// </summary>
/// <param name="AmbientCelsius">Ambient temperature.</param>
/// <param name="WarmUpElapsed">How long the DUT had been on.</param>
/// <param name="Notes">Anything else worth knowing.</param>
public sealed record SessionEnvironment(
    double? AmbientCelsius,
    TimeSpan? WarmUpElapsed,
    string? Notes)
{
    /// <summary>
    /// Whether the warm-up satisfies what the manual asks for. Section IV specifies performance
    /// after a warm-up, and a result taken before it is a result about a cold instrument.
    /// </summary>
    public bool WarmedUp(TimeSpan required) => WarmUpElapsed is { } elapsed && elapsed >= required;
}

/// <summary>
/// Everything one run produced.
///
/// <para><b>The session is the evidence.</b> A year from now the only way to know whether a number
/// was taken through an 18 GHz leg at 22 GHz, or on a borrowed sensor, or before the oven was
/// warm, is because this said so. Nothing here is reconstructable afterwards.</para>
/// </summary>
public sealed class Session
{
    /// <summary>When the run started. Also names the folder.</summary>
    public DateTime StartedUtc { get; init; } = DateTime.UtcNow;

    /// <summary>What this run was for, e.g. "5-14 unleveled RF output".</summary>
    public string Purpose { get; set; } = "";

    /// <summary>Who ran it.</summary>
    public string Operator { get; set; } = "";

    /// <summary>DUT output option, which sets the Table 4-9 limits.</summary>
    public InstrumentOption DutOption { get; set; } = InstrumentOption.Standard;

    /// <summary>DUT serial number.</summary>
    public string DutSerial { get; set; } = "TBD";

    /// <summary>The bench as probed.</summary>
    public List<SessionInstrument> Instruments { get; init; } = [];

    /// <summary>Hook-up cards acknowledged, as "S1 by Tony at ...".</summary>
    public List<string> Acknowledgements { get; init; } = [];

    /// <summary>Wiring self-check results.</summary>
    public List<string> SelfChecks { get; init; } = [];

    /// <summary>Every measurement.</summary>
    public List<SessionMeasurement> Measurements { get; init; } = [];

    /// <summary>Every write to the DUT (rule 1).</summary>
    public List<SessionWrite> Writes { get; init; } = [];

    /// <summary>Conditions.</summary>
    public SessionEnvironment Environment { get; set; } = new(null, null, null);

    /// <summary>Whether a calibration backup was taken before anything could write.</summary>
    public string? CalBackupFile { get; set; }

    /// <summary>
    /// Records a measurement, refusing one that cannot be defended.
    ///
    /// <para>Rule 3 is enforced here rather than trusted: a measurement with no settings recorded
    /// is not evidence, and the moment to notice that is when it is taken, not a year later when
    /// somebody asks what the resolution bandwidth was.</para>
    /// </summary>
    public void Record(SessionMeasurement measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);

        if (string.IsNullOrWhiteSpace(measurement.Settings))
            throw new ArgumentException(
                $"The measurement '{measurement.What}' has no instrument settings recorded. Repo "
                + "rule 3: every measurement carries the settings used. A reading with no record "
                + "of the resolution bandwidth, cal factor or averaging cannot be defended later, "
                + "and by then nobody will remember.", nameof(measurement));

        if (string.IsNullOrWhiteSpace(measurement.Instrument))
            throw new ArgumentException(
                $"The measurement '{measurement.What}' does not say which instrument produced it.",
                nameof(measurement));

        Measurements.Add(measurement);
    }

    /// <summary>
    /// Records a write to the DUT, refusing one that is not attributable.
    ///
    /// <para>Rule 1 wants the old value as well as the new. Where the old one genuinely could not
    /// be read that is recorded as such, but it has to be a deliberate statement rather than an
    /// omission.</para>
    /// </summary>
    public void RecordWrite(SessionWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);

        if (string.IsNullOrWhiteSpace(write.ConfirmedBy))
            throw new ArgumentException(
                $"The write '{write.What}' has no confirmation recorded. Repo rule 1: never write "
                + "to the 8340B without explicit per-action confirmation, and log who gave it.",
                nameof(write));

        Writes.Add(write);
    }

    /// <summary>Borrowed instruments whose serial number was not recorded. D-10 wants none.</summary>
    public IReadOnlyList<SessionInstrument> BorrowedWithoutSerial() =>
        Instruments.Where(i => i.Borrowed && string.IsNullOrWhiteSpace(i.Serial)).ToList();

    /// <summary>
    /// Instruments whose reference state could not be confirmed. Not an error — the 5351A never
    /// can — but it belongs in the record next to any frequency result.
    /// </summary>
    public IReadOnlyList<SessionInstrument> ReferenceUnconfirmed() =>
        Instruments.Where(i => i is { Responded: true, ExternalReference: null }).ToList();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Writes the session folder: the raw JSON and a Markdown summary readable without the tool.
    /// </summary>
    /// <param name="root">The sessions directory. Git-ignored (rule 7).</param>
    /// <returns>The folder written.</returns>
    public string Write(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var folder = Path.Combine(root, StartedUtc.ToString("yyyy-MM-dd-HHmmss"));
        Directory.CreateDirectory(folder);

        File.WriteAllText(
            Path.Combine(folder, "session.json"),
            JsonSerializer.Serialize(this, JsonOptions));

        File.WriteAllText(Path.Combine(folder, "summary.md"), RenderMarkdown());

        return folder;
    }

    /// <summary>
    /// The summary, in Markdown.
    ///
    /// <para>Readable without this tool on purpose: the evidence should outlive the program that
    /// produced it, and a JSON file nobody can open is not evidence.</para>
    /// </summary>
    public string RenderMarkdown()
    {
        var md = new System.Text.StringBuilder();

        md.AppendLine($"# HP 8340B session — {StartedUtc:yyyy-MM-dd HH:mm} UTC");
        md.AppendLine();
        md.AppendLine($"**Purpose:** {Purpose}  ");
        md.AppendLine($"**Operator:** {Operator}  ");
        md.AppendLine($"**DUT:** 8340B {DutOption}, serial {DutSerial}  ");

        if (CalBackupFile is { } backup)
            md.AppendLine($"**Calibration backup:** `{backup}`  ");
        else
            md.AppendLine("**Calibration backup:** NONE TAKEN  ");

        md.AppendLine();
        md.AppendLine("## Conditions");
        md.AppendLine();
        md.AppendLine($"- Ambient: {Describe(Environment.AmbientCelsius, "°C")}");
        md.AppendLine($"- Warm-up elapsed: {Environment.WarmUpElapsed?.ToString() ?? "not recorded"}");
        if (!string.IsNullOrWhiteSpace(Environment.Notes)) md.AppendLine($"- {Environment.Notes}");

        md.AppendLine();
        md.AppendLine("## Bench");
        md.AppendLine();
        md.AppendLine("| Role | Model | Address | Responded | Ref | Serial |");
        md.AppendLine("|---|---|---|---|---|---|");

        foreach (var i in Instruments)
        {
            var reference = i.ExternalReference switch
            {
                true => "EXT",
                false => "**INT**",
                null => "?",
            };

            md.AppendLine(
                $"| {i.Role} | {i.Model}{(i.Borrowed ? " *(borrowed)*" : "")} | {i.Address} | "
                + $"{(i.Responded ? "yes" : "**no**")} | {reference} | {i.Serial ?? ""} |");
        }

        Section(md, "Hook-up cards acknowledged", Acknowledgements);
        Section(md, "Wiring self-checks", SelfChecks);

        if (Measurements.Count > 0)
        {
            md.AppendLine();
            md.AppendLine("## Measurements");
            md.AppendLine();
            md.AppendLine("| What | Frequency | Value | Traceability | Instrument | Leg | Settings |");
            md.AppendLine("|---|---|---|---|---|---|---|");

            foreach (var m in Measurements)
                md.AppendLine(
                    $"| {m.What} | {m.Hz / 1e9:0.###} GHz | {m.Value:0.###} {m.Unit} | "
                    + $"{m.Traceability} | {m.Instrument} | {m.RoutedLeg ?? "direct"} | {m.Settings} |");
        }

        if (Writes.Count > 0)
        {
            md.AppendLine();
            md.AppendLine("## Writes to the DUT");
            md.AppendLine();
            md.AppendLine("| When | What | Old | New | Confirmed by |");
            md.AppendLine("|---|---|---|---|---|");

            foreach (var w in Writes)
                md.AppendLine(
                    $"| {w.At:HH:mm:ss} | {w.What} | {w.OldValue ?? "*(could not be read)*"} | "
                    + $"{w.NewValue} | {w.ConfirmedBy} |");
        }

        var unconfirmed = ReferenceUnconfirmed();

        if (unconfirmed.Count > 0)
        {
            md.AppendLine();
            md.AppendLine("## Caveats");
            md.AppendLine();
            md.AppendLine(
                "- Reference state could not be read from: "
                + string.Join(", ", unconfirmed.Select(i => i.Model))
                + ". These have no command that reports it, so any frequency result here rests on "
                + "a front-panel check rather than a bus reading.");
        }

        var noSerial = BorrowedWithoutSerial();

        if (noSerial.Count > 0)
        {
            if (unconfirmed.Count == 0) { md.AppendLine(); md.AppendLine("## Caveats"); md.AppendLine(); }

            md.AppendLine(
                "- Borrowed kit with NO serial recorded: "
                + string.Join(", ", noSerial.Select(i => i.Model))
                + ". A borrowed instrument's calibration history is not this bench's, so a result "
                + "that depends on one cannot be traced without its serial (D-10).");
        }

        return md.ToString();
    }

    private static string Describe(double? value, string unit) =>
        value is { } v ? $"{v:0.#} {unit}" : "not recorded";

    private static void Section(System.Text.StringBuilder md, string title, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return;

        md.AppendLine();
        md.AppendLine($"## {title}");
        md.AppendLine();
        foreach (var line in lines) md.AppendLine($"- {line}");
    }
}
