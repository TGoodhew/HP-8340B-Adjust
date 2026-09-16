using System.Text.Json;
using System.Text.Json.Serialization;
using HP8340B.Instruments;
using HP8340B.Instruments.Model;

namespace HP8340B.Reports;

/// <summary>One constant as it was found.</summary>
/// <param name="Number">CC number.</param>
/// <param name="Value">Its value.</param>
/// <param name="Plausible">
/// False if the value is outside the range the manual gives for this constant. Not an error — the
/// SHHZ read semantics are inferred and unconfirmed — but it is the signal that the read path,
/// not the instrument, may be what is wrong.
/// </param>
public sealed record BackedUpConstant(int Number, int Value, bool Plausible);

/// <summary>
/// Everything needed to put a DUT back the way it was found.
///
/// <para><b>This is the first block of every campaign and the gate on everything that writes.</b>
/// 5-14 step 69 stores to protected memory, which overwrites the protected copy of the
/// calibration. If that copy is lost and the working copy is wrong, the printed card under the
/// top cover is the only remaining source — and it does not carry the delay-compensation
/// constants an adjustment has moved.</para>
/// </summary>
public sealed class CalBackup
{
    /// <summary>When it was taken.</summary>
    public DateTime TakenUtc { get; init; } = DateTime.UtcNow;

    /// <summary>DUT serial, so a backup cannot be restored into the wrong instrument.</summary>
    public string DutSerial { get; set; } = "";

    /// <summary>DUT output option.</summary>
    public InstrumentOption DutOption { get; set; } = InstrumentOption.Standard;

    /// <summary>CC1 to CC99.</summary>
    public List<BackedUpConstant> Constants { get; init; } = [];

    /// <summary>The 123-byte learn string, base64 encoded.</summary>
    public string? LearnStringBase64 { get; set; }

    /// <summary>The instrument's identification string, as a second check on which unit this is.</summary>
    public string? Identity { get; set; }

    /// <summary>
    /// True when every constant read back inside the range the manual gives for it.
    ///
    /// <para>A backup with implausible values is still worth keeping — it is the only record there
    /// is — but it should not be restored without somebody looking at it first, because the most
    /// likely explanation is that the unconfirmed SHHZ read path is returning nonsense rather than
    /// that the instrument holds nonsense.</para>
    /// </summary>
    [JsonIgnore]
    public bool AllPlausible => Constants.All(c => c.Plausible);

    /// <summary>Constants whose value is outside the manual's range.</summary>
    public IReadOnlyList<BackedUpConstant> Implausible() =>
        Constants.Where(c => !c.Plausible).ToList();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Takes a backup from a live DUT. Reads only — nothing here can write.</summary>
    public static CalBackup Capture(
        Hp8340B dut,
        CalConstants constants,
        InstrumentOption option,
        string dutSerial,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dut);
        ArgumentNullException.ThrowIfNull(constants);

        var backup = new CalBackup { DutOption = option, DutSerial = dutSerial };

        foreach (var c in constants.ReadAll(cancellationToken))
            backup.Constants.Add(new BackedUpConstant(c.Number, c.Value, c.Plausible));

        backup.Identity = dut.ReadIdentity();
        backup.LearnStringBase64 = Convert.ToBase64String(dut.ReadLearnString());

        return backup;
    }

    /// <summary>Writes the backup beside a session.</summary>
    public string Write(string folder, string fileName = "cal-backup.json")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, fileName);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));

        return path;
    }

    /// <summary>Reads a backup back.</summary>
    public static CalBackup Read(string path) =>
        JsonSerializer.Deserialize<CalBackup>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"{path} did not parse as a calibration backup.");
}

/// <summary>One constant that differs between two backups.</summary>
/// <param name="Number">CC number.</param>
/// <param name="Before">Value in the earlier backup, or null if it was not present.</param>
/// <param name="After">Value in the later one, or null if it is gone.</param>
/// <param name="Purpose">What this constant does, where the project knows.</param>
public sealed record CalDifference(int Number, int? Before, int? After, string? Purpose)
{
    /// <summary>A line for the console and the session.</summary>
    public string Describe() =>
        $"CC{Number}: {Before?.ToString() ?? "(absent)"} -> {After?.ToString() ?? "(absent)"}"
        + (Purpose is null ? "" : $"   [{Purpose}]");
}

/// <summary>
/// Compares two backups and restores one.
/// </summary>
public static class CalDiff
{
    /// <summary>
    /// What each constant this project knows about does. Only the ones 5-14 and 5-16 actually
    /// touch, from the brief — a diff is far more useful when it says which adjustment moved
    /// something than when it lists bare numbers.
    /// </summary>
    private static readonly Dictionary<int, string> Purposes = new()
    {
        [1] = "A28R13 DRP working value (5-14 steps 68-69)",
        [2] = "Band 2 multiband delay compensation (5-14 step 59+)",
        [3] = "Band 3 multiband delay compensation",
        [4] = "Band 4 multiband delay compensation",
        [5] = "Band 1 delay compensation (5-14 step 53)",
        [6] = "Band 2 delay compensation",
        [7] = "Band 3 delay compensation",
        [8] = "Band 3 fallback delay compensation",
        [48] = "External leveling (5-15)",
        [73] = "Band 3 SYTM tracking peak (5-14 step 20)",
        [74] = "Band 4 SYTM tracking peak (5-14 step 29)",
        [75] = "Band 4 start-of-sweep dropout (5-14 step 45)",
        [77] = "Band-switch point, low side (5-14 steps 59-67)",
        [78] = "Band-switch point, high side",
    };

    /// <summary>What a constant does, or null.</summary>
    public static string? PurposeOf(int constant) =>
        Purposes.TryGetValue(constant, out var purpose) ? purpose : null;

    /// <summary>
    /// Everything that differs between two backups. Only what changed — a list of ninety-nine
    /// unchanged constants hides the three that moved.
    /// </summary>
    public static IReadOnlyList<CalDifference> Compare(CalBackup before, CalBackup after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var b = before.Constants.ToDictionary(c => c.Number, c => c.Value);
        var a = after.Constants.ToDictionary(c => c.Number, c => c.Value);

        return b.Keys.Union(a.Keys)
            .OrderBy(n => n)
            .Where(n => !b.TryGetValue(n, out var bv) || !a.TryGetValue(n, out var av) || bv != av)
            .Select(n => new CalDifference(
                n,
                b.TryGetValue(n, out var bv) ? bv : null,
                a.TryGetValue(n, out var av) ? av : null,
                PurposeOf(n)))
            .ToList();
    }

    /// <summary>
    /// True if the learn string differs. Compared separately because it is one opaque blob: it
    /// either matches or it does not, and there is nothing useful to say about which byte moved.
    /// </summary>
    public static bool LearnStringChanged(CalBackup before, CalBackup after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        return before.LearnStringBase64 != after.LearnStringBase64;
    }

    /// <summary>
    /// Checks a backup may be restored into this instrument, and throws saying why not.
    ///
    /// <para>The serial check is the one that matters. Calibration constants are per-unit: they
    /// describe the YIG tracking and delay of one particular SYTM, and restoring another
    /// instrument's into this one would be worse than leaving it mis-adjusted, because the result
    /// looks like a calibrated instrument.</para>
    /// </summary>
    public static void GuardRestore(CalBackup backup, string dutSerial, string? identity)
    {
        ArgumentNullException.ThrowIfNull(backup);

        if (string.IsNullOrWhiteSpace(backup.DutSerial) || backup.DutSerial == "TBD")
            throw new InvalidOperationException(
                "This backup has no DUT serial recorded, so there is no way to tell which "
                + "instrument it came from. Calibration constants are per-unit — they describe one "
                + "particular SYTM's tracking and delay — and restoring the wrong set produces "
                + "something that looks calibrated and is not. Refusing.");

        if (!string.Equals(backup.DutSerial, dutSerial, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"This backup was taken from serial {backup.DutSerial} and the instrument in front "
                + $"of you is {dutSerial}. Calibration constants do not transfer between "
                + "instruments. Refusing.");

        if (identity is not null && backup.Identity is not null
            && !string.Equals(backup.Identity.Trim(), identity.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"The backup's identity string was '{backup.Identity.Trim()}' and this instrument "
                + $"answers '{identity.Trim()}'. The serials match but the identities do not, "
                + "which usually means one of the two was recorded wrongly. Check before "
                + "restoring.");

        if (!backup.AllPlausible)
            throw new InvalidOperationException(
                $"{backup.Implausible().Count} constant(s) in this backup are outside the ranges "
                + "the manual gives: "
                + string.Join(", ", backup.Implausible().Select(c => $"CC{c.Number}={c.Value}"))
                + ". The likeliest explanation is that the SHHZ read path — which is inferred from "
                + "Table 3-2 and not hardware-confirmed — returned nonsense when this was taken, "
                + "not that the instrument held nonsense. Restoring it would write that nonsense "
                + "in. Look at it before forcing a restore.");
    }
}
