using System.Globalization;

namespace HP8340B.Instruments;

/// <summary>
/// Authorisation for one calibration-constant write. Repo rule 1: no write to the 8340B without
/// explicit confirmation for that specific action.
///
/// <para>This is a value, not a flag, and it names the constant and both values. That is
/// deliberate: it makes "confirm once, then write a hundred constants in a loop" impossible to
/// express, because each write needs an authorisation naming its own constant and its own old
/// value, and <see cref="CalConstants"/> re-reads the constant and refuses if the old value has
/// moved since the authorisation was granted.</para>
/// </summary>
/// <param name="Constant">Which constant, CC1-CC99.</param>
/// <param name="OldValue">The value read immediately before asking for confirmation.</param>
/// <param name="NewValue">The value to write.</param>
/// <param name="ConfirmedBy">Who or what confirmed it — for the session record.</param>
/// <param name="Reason">Why, e.g. "5-14 step 3 preset" — for the session record.</param>
public sealed record CalWriteAuthorization(
    int Constant,
    int OldValue,
    int NewValue,
    string ConfirmedBy,
    string Reason);

/// <summary>Authorisation for the store-to-protected-memory sequence. Separately worded on purpose.</summary>
/// <param name="ConfirmedBy">Who confirmed it.</param>
/// <param name="BackupPath">The cal backup taken in this session. Required.</param>
/// <param name="Acknowledgement">
/// Must be exactly <see cref="CalConstants.StoreAcknowledgement"/>. A distinct phrase from any
/// other confirmation in the project, so it cannot be satisfied by a reflexive "yes".
/// </param>
public sealed record CalStoreAuthorization(
    string ConfirmedBy,
    string BackupPath,
    string Acknowledgement);

/// <summary>One constant's value and what is known about it.</summary>
/// <param name="Number">CC number, 1-99.</param>
/// <param name="Value">Value as read.</param>
/// <param name="Plausible">
/// False if the value falls outside what the manual's own preset table implies for this constant.
/// The SHHZ read path is not yet hardware-confirmed, so an implausible value more likely means the
/// read is wrong than that the instrument is.
/// </param>
public sealed record CalConstant(int Number, int Value, bool Plausible = true);

/// <summary>
/// Calibration-constant access over HP-IB.
///
/// <para><b>This is the most dangerous code in the project.</b> The store-to-protected sequence
/// overwrites the protected copy of the instrument's calibration; if that is lost and the working
/// copy is wrong, the printed card under the top cover is the only remaining source.</para>
///
/// <para>Codes, all verified against Table 3-2 (see docs/HPIB-8340B.md):
/// <c>SHGZ</c> I/O channel, <c>SHMZ</c> I/O subchannel, <c>SHKZ</c> I/O write, <c>SHHZ</c> read
/// from I/O, <c>SHEF</c> restore cal-constant access.</para>
///
/// <para><b>Hardware caveat.</b> <c>SHHZ</c> appears in Table 3-2 but, unlike its three siblings,
/// has no corroborating entry in the OM parameter list. The read semantics are therefore inferred
/// and must be confirmed on the bench before any decision is based on a read value — which is why
/// <see cref="ReadAll"/> flags implausible values rather than trusting them.</para>
/// </summary>
public sealed class CalConstants
{
    /// <summary>Lowest and highest calibration constant.</summary>
    public const int First = 1;
    public const int Last = 99;

    /// <summary>
    /// The phrase <see cref="CalStoreAuthorization.Acknowledgement"/> must carry. Deliberately
    /// specific: it cannot be produced by agreeing to something else.
    /// </summary>
    public const string StoreAcknowledgement = "OVERWRITE PROTECTED CALIBRATION";

    // The fixed operands in the manual's access sequence (5-14 step 2):
    //   [SHIFT][GHz] n [Hz]     -> SHGZ n HZ      select the constant
    //   [SHIFT][MHz] 1 2 [Hz]   -> SHMZ 12 HZ     I/O subchannel
    //   [SHIFT][kHz] 2 2 [Hz]   -> SHKZ 22 HZ     I/O write
    private const int AccessSubchannel = 12;
    private const int AccessWriteKey = 22;

    private readonly Hp8340B _dut;

    public CalConstants(Hp8340B dut) => _dut = dut ?? throw new ArgumentNullException(nameof(dut));

    /// <summary>
    /// Values 5-14 steps 2-3 preset before the unleveled adjustments. Confirmed against the manual
    /// in raw reading order on 15 Sep 2026; the layout-mode extraction of that two-column table is
    /// unreliable. CC2 is set in step 2, CC3-CC80 in step 3.
    /// </summary>
    public static readonly IReadOnlyDictionary<int, int> Adjustment514Presets = new Dictionary<int, int>
    {
        [2] = 100, [3] = 100, [4] = 100, [5] = 100, [6] = 100, [7] = 100, [8] = 100,
        [9] = 1024, [10] = 1024, [11] = 1024, [12] = 1024,
        [50] = 0, [51] = 0, [52] = 0, [53] = 0,
        [71] = 1024, [72] = 1024, [73] = 1024, [74] = 1024,
        [75] = 25, [76] = 1000, [77] = -25, [78] = 25, [80] = 0,
    };

    /// <summary>
    /// Known ranges, from the manual's own text. Used only to flag an implausible READ, given the
    /// SHHZ caveat — not to constrain what may be written, because an adjustment legitimately
    /// moves a constant away from its preset.
    /// </summary>
    private static readonly Dictionary<int, (int Min, int Max)> KnownRanges = new()
    {
        // 5-14 steps 2-3: delay compensation constants have range 0-131.
        [2] = (0, 131), [3] = (0, 131), [4] = (0, 131), [5] = (0, 131),
        [6] = (0, 131), [7] = (0, 131), [8] = (0, 131),
        // 5-14 steps 59-67: band-switch points run -25 to +25.
        [77] = (-25, 25), [78] = (-25, 25),
        // 5-14 steps 23-30: CC75 fixes the band-4 start dropout, range 0-500.
        [75] = (0, 500),
    };

    private static void ValidateNumber(int constant)
    {
        if (constant is < First or > Last)
            throw new ArgumentOutOfRangeException(
                nameof(constant), constant, $"Calibration constants are CC{First}-CC{Last}.");
    }

    private static string Digits(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Selects a constant for access. Sends the full sequence after a preset, or the short form
    /// otherwise, exactly as 5-14 step 2's note describes.
    /// </summary>
    private void Select(int constant, bool afterPreset)
    {
        var link = _dut.Link;
        var hz = Hp8340BCommands.Require("HZ");

        link.Write($"{Hp8340BCommands.Require("SHGZ")} {Digits(constant)} {hz}");

        if (afterPreset)
        {
            link.Write($"{Hp8340BCommands.Require("SHMZ")} {Digits(AccessSubchannel)} {hz}");
            link.Write($"{Hp8340BCommands.Require("SHKZ")} {Digits(AccessWriteKey)} {hz}");
        }
        else
        {
            link.Write(Hp8340BCommands.Require("SHEF"));
        }
    }

    /// <summary>
    /// Reads one constant. A read is always safe (rule 1 governs writes only), but see the class
    /// remarks on the SHHZ caveat before trusting the value.
    /// </summary>
    public CalConstant Read(int constant, bool afterPreset = false)
    {
        ValidateNumber(constant);
        Select(constant, afterPreset);

        var text = _dut.Link.Query(Hp8340BCommands.Require("SHHZ")).Trim();

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new InvalidOperationException(
                $"CC{constant} read returned '{text}', which is not an integer. The SHHZ read "
                + "semantics are inferred from Table 3-2 and not yet hardware-confirmed — see "
                + "docs/HPIB-8340B.md before trusting this path.");

        return new CalConstant(constant, value, IsPlausible(constant, value));
    }

    private static bool IsPlausible(int constant, int value) =>
        !KnownRanges.TryGetValue(constant, out var range) || (value >= range.Min && value <= range.Max);

    /// <summary>
    /// Reads every constant, CC1-CC99. Writes nothing. This is what `backup-cal` (M1-01) captures
    /// before anything is adjusted.
    /// </summary>
    public IReadOnlyList<CalConstant> ReadAll(CancellationToken cancellationToken = default)
    {
        var all = new List<CalConstant>(Last);

        for (var n = First; n <= Last; n++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The full access sequence on the first constant, the short form thereafter.
            all.Add(Read(n, afterPreset: n == First));
        }

        return all;
    }

    /// <summary>
    /// Writes one constant. Requires a <see cref="CalWriteAuthorization"/> naming this constant
    /// and the value it held when confirmation was given.
    ///
    /// <para>Re-reads the constant first and refuses if it no longer matches
    /// <see cref="CalWriteAuthorization.OldValue"/>. That closes the window where something else
    /// changed the instrument between the operator being asked and agreeing.</para>
    ///
    /// <para>Returns the log line the session must record (rule 1: timestamp, old and new value).</para>
    /// </summary>
    public string Write(CalWriteAuthorization authorization, bool afterPreset = false)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ValidateNumber(authorization.Constant);

        if (string.IsNullOrWhiteSpace(authorization.ConfirmedBy))
            throw new ArgumentException(
                "A cal-constant write must record who confirmed it (repo rule 1).",
                nameof(authorization));

        if (_dut.Link.IsSimulated)
            throw new InvalidOperationException(
                $"Refusing to write CC{authorization.Constant} against a SIMULATED instrument. A "
                + "simulated write would look like success in a log and prove nothing. Run against "
                + "the real DUT, or call the dry-run path explicitly.");

        var current = Read(authorization.Constant, afterPreset);

        if (current.Value != authorization.OldValue)
            throw new InvalidOperationException(
                $"CC{authorization.Constant} now reads {current.Value}, but the confirmation was "
                + $"given for {authorization.OldValue}. Something changed it in between. Refusing "
                + "the write; re-read and confirm again.");

        if (current.Value == authorization.NewValue)
            return Log(authorization, "no change — already at the requested value");

        // The constant is already selected by the Read above, so only the value and terminator
        // are needed. 5-14 step 3: "Be sure to terminate each entry by pressing [Hz]."
        _dut.Link.Write($"{Digits(authorization.NewValue)} {Hp8340BCommands.Require("HZ")}");

        return Log(authorization, "written");
    }

    private static string Log(CalWriteAuthorization a, string outcome) =>
        $"{DateTime.UtcNow:O} CC{a.Constant} {a.OldValue} -> {a.NewValue} ({outcome}); "
        + $"confirmed by {a.ConfirmedBy}; reason: {a.Reason}";

    /// <summary>
    /// Stores the working calibration constants to protected memory.
    /// <c>[SHIFT][MHz] 1 4 [Hz]</c>, <c>[SHIFT][kHz] 5 3 4 9 [Hz]</c>, wait for
    /// "CALIBRATION STORED" (5-14 step 69).
    ///
    /// <para><b>This overwrites the protected copy.</b> It requires its own authorisation with a
    /// distinct acknowledgement phrase and the path of a cal backup taken in this session, and it
    /// refuses against a simulated instrument.</para>
    /// </summary>
    public string StoreToProtectedMemory(CalStoreAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);

        if (authorization.Acknowledgement != StoreAcknowledgement)
            throw new InvalidOperationException(
                $"Store to protected memory needs the exact acknowledgement '{StoreAcknowledgement}'. "
                + "This phrase is distinct from every other confirmation in the project so it "
                + "cannot be satisfied by agreeing to something else.");

        if (string.IsNullOrWhiteSpace(authorization.BackupPath) || !File.Exists(authorization.BackupPath))
            throw new InvalidOperationException(
                "Store to protected memory requires a calibration backup taken in this session. "
                + $"'{authorization.BackupPath}' does not exist. Run `backup-cal` first — the "
                + "printed card under the top cover is otherwise the only remaining copy.");

        if (_dut.Link.IsSimulated)
            throw new InvalidOperationException(
                "Refusing to store to protected memory against a SIMULATED instrument.");

        var hz = Hp8340BCommands.Require("HZ");
        _dut.Link.Write($"{Hp8340BCommands.Require("SHMZ")} 14 {hz}");
        _dut.Link.Write($"{Hp8340BCommands.Require("SHKZ")} 5349 {hz}");

        return $"{DateTime.UtcNow:O} STORE TO PROTECTED MEMORY; confirmed by "
               + $"{authorization.ConfirmedBy}; backup at {authorization.BackupPath}";
    }

    /// <summary>
    /// What <see cref="Write"/> would do, without touching the instrument. Safe in simulation and
    /// the only cal-constant operation that is.
    /// </summary>
    public static string DescribeWrite(CalWriteAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        return $"DRY RUN: would set CC{authorization.Constant} from {authorization.OldValue} to "
               + $"{authorization.NewValue} ({authorization.Reason})";
    }
}
