using HP8340B.Instruments;

namespace HP8340B.Bench.Model;

/// <summary>What a wiring self-check found.</summary>
/// <param name="SetupId">Setup checked.</param>
/// <param name="Status">Whether it passed, failed, or could not run.</param>
/// <param name="Expected">What the card said should happen.</param>
/// <param name="Observed">What actually happened.</param>
/// <param name="At">When.</param>
public sealed record SelfCheckOutcome(
    string SetupId,
    SelfCheckStatus Status,
    string Expected,
    string Observed,
    DateTime At)
{
    /// <summary>A line for the console and the session record.</summary>
    public string Describe() =>
        $"{SetupId}: {Status}. Expected {Expected}. Observed {Observed}.";
}

/// <summary>How a self-check ended.</summary>
public enum SelfCheckStatus
{
    /// <summary>The wiring is what the card says.</summary>
    Passed,

    /// <summary>It is not. The block must not start.</summary>
    Failed,

    /// <summary>
    /// The check could not be made — an instrument is missing, or this setup has no executable
    /// check yet.
    ///
    /// <para><b>Deliberately not the same as Passed.</b> A check that silently does nothing is
    /// worse than no check at all, because it produces the reassurance without the evidence.</para>
    /// </summary>
    NotRun,
}

/// <summary>
/// The instruments a self-check can reach, by role.
/// </summary>
public sealed class SelfCheckContext
{
    private readonly Dictionary<string, IInstrument> _byRole;

    /// <summary>Pad in front of whatever is being measured, dB. Declared in config.</summary>
    public double PadDb { get; init; }

    /// <summary>Frequency the checks use. 1 GHz suits every instrument on this bench.</summary>
    public double CheckHz { get; init; } = 1e9;

    /// <summary>Level the checks use.</summary>
    public double CheckDbm { get; init; }

    public SelfCheckContext(IEnumerable<IInstrument> instruments)
    {
        ArgumentNullException.ThrowIfNull(instruments);

        _byRole = instruments.ToDictionary(i => i.Role, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The instrument in <paramref name="role"/> as <typeparamref name="T"/>, or null.</summary>
    public T? Get<T>(string role) where T : class, IInstrument =>
        _byRole.TryGetValue(role, out var instrument) ? instrument as T : null;

    /// <summary>Every instrument, for checks that walk the whole bench.</summary>
    public IReadOnlyCollection<IInstrument> All => _byRole.Values;
}

/// <summary>
/// The executable wiring checks (M0-20).
///
/// <para><b>Why these exist.</b> A hook-up card tells you what to connect; a self-check knows
/// whether you did. A mis-cabled bench produces plausible-looking wrong numbers, which is the
/// worst failure mode in this project — worse than an obvious break, because nothing about the
/// result looks wrong.</para>
///
/// <para>Each check states what it expected and what it saw, so a failure is diagnosable rather
/// than just a refusal.</para>
/// </summary>
public static class SelfChecks
{
    /// <summary>
    /// How close a level check has to be, in dB. Generous: this proves cabling, not accuracy.
    ///
    /// <para>PROVISIONAL: never checked against an instrument. Chosen to be loose enough that
    /// cable loss and a mis-declared pad are distinguishable, and tight enough that a wrong cable
    /// is not. The real figure depends on what the bench's cables actually lose.</para>
    /// </summary>
    public const double LevelToleranceDb = 2.0;

    /// <summary>
    /// How close a frequency check has to be, as a fraction.
    ///
    /// <para>PROVISIONAL: never checked against an instrument. A part in a million is far inside
    /// what two Z3805A-locked instruments should manage, but nobody has measured what they do
    /// manage.</para>
    /// </summary>
    public const double FrequencyTolerance = 1e-6;

    private static readonly Dictionary<string, Func<SelfCheckContext, (SelfCheckStatus, string)>> Registry =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["S0"] = CheckStandingConfiguration,
            ["S1"] = CheckAnalyzerPath,
            ["S2"] = CheckCounterPath,
            ["S3"] = CheckPowerPath,
        };

    /// <summary>Setup ids that have an executable check.</summary>
    public static IReadOnlyCollection<string> Implemented => Registry.Keys;

    /// <summary>
    /// Runs the check for <paramref name="setup"/>.
    ///
    /// <para>A setup with no executable check returns <see cref="SelfCheckStatus.NotRun"/> and
    /// says so, rather than passing.</para>
    /// </summary>
    public static SelfCheckOutcome Run(BenchSetup setup, SelfCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(context);

        var expected = setup.SelfCheck?.Expected ?? "(no self-check declared for this setup)";

        if (!Registry.TryGetValue(setup.Id, out var check))
            return new SelfCheckOutcome(
                setup.Id, SelfCheckStatus.NotRun, expected,
                "No executable check for this setup yet — the card's expectation is prose only. "
                + "This is NOT a pass: the wiring has not been proven.",
                DateTime.UtcNow);

        try
        {
            var (status, observed) = check(context);
            return new SelfCheckOutcome(setup.Id, status, expected, observed, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            // NotRun, not Failed. A check that threw did not find the wiring wrong -- it did not
            // find anything, because it never completed. Reporting it as Failed prints the wiring
            // diagnosis for what is usually a bus fault, and sends somebody to check cables that
            // are fine. Both statuses block the gate, so nothing is lost by being accurate.
            return new SelfCheckOutcome(
                setup.Id, SelfCheckStatus.NotRun, expected,
                $"The check could not be completed: {ex.Message} That is not a statement about "
                + "the wiring — the check never got far enough to make one. Fix this first, then "
                + "run it again.",
                DateTime.UtcNow);
        }
    }

    // --- The checks ------------------------------------------------------------------------------

    /// <summary>
    /// S0: every present instrument responds, and the DUT is on the external reference.
    /// </summary>
    private static (SelfCheckStatus, string) CheckStandingConfiguration(SelfCheckContext context)
    {
        var results = context.All.Select(i => i.Probe()).ToList();
        var silent = results.Where(r => !r.Responded).Select(r => r.Model).ToList();

        if (silent.Count > 0)
            return (SelfCheckStatus.Failed, $"{string.Join(", ", silent)} did not respond.");

        var dut = context.Get<Hp8340B>("dut");

        if (dut is null)
            return (SelfCheckStatus.NotRun, "No DUT in the bench, so the reference bit cannot be read.");

        return dut.IsExternalReferenceSelected()
            ? (SelfCheckStatus.Passed,
                $"{results.Count} instrument(s) responded; the DUT reports external reference selected.")
            : (SelfCheckStatus.Failed,
                "The DUT reports its INTERNAL reference. Extended status byte #2 bit 3 is clear, so "
                + "it is not on the Z3805A and every frequency result would be against its own "
                + "crystal.");
    }

    /// <summary>
    /// S1: the DUT reaches the analyzer. CW at a known level, peak-search, compare.
    /// </summary>
    private static (SelfCheckStatus, string) CheckAnalyzerPath(SelfCheckContext context)
    {
        var dut = context.Get<Hp8340B>("dut");
        var analyzer = context.Get<Hp8563E>("spectrum-analyzer");

        if (dut is null || analyzer is null)
            return (SelfCheckStatus.NotRun, "The DUT or the analyzer is not on the bench.");

        dut.SetCwGHz(context.CheckHz / 1e9);
        dut.SetPowerDbm(context.CheckDbm);
        dut.WaitSettled(TimeSpan.FromSeconds(5));

        analyzer.SetCenterSpanHz(context.CheckHz, 10e6);
        var observed = analyzer.PeakSearch().Dbm + context.PadDb;
        var error = observed - context.CheckDbm;

        return Math.Abs(error) <= LevelToleranceDb
            ? (SelfCheckStatus.Passed,
                $"{observed:+0.0;-0.0} dBm at the analyzer against {context.CheckDbm:+0.0;-0.0} dBm "
                + $"requested ({error:+0.0;-0.0} dB).")
            : (SelfCheckStatus.Failed,
                $"{observed:+0.0;-0.0} dBm at the analyzer against {context.CheckDbm:+0.0;-0.0} dBm "
                + $"requested — {error:+0.0;-0.0} dB out, beyond the {LevelToleranceDb:0.#} dB this "
                + "check allows. Check the cable, the pad, and that the pad value in config matches "
                + "the one actually fitted.");
    }

    /// <summary>S2: the DUT reaches the counter, and both are on the same reference.</summary>
    private static (SelfCheckStatus, string) CheckCounterPath(SelfCheckContext context)
    {
        var dut = context.Get<Hp8340B>("dut");
        var counter = context.Get<Hp5351A>("counter");

        if (dut is null || counter is null)
            return (SelfCheckStatus.NotRun, "The DUT or the counter is not on the bench.");

        dut.SetCwGHz(context.CheckHz / 1e9);
        dut.WaitSettled(TimeSpan.FromSeconds(5));

        counter.SetMode(CounterMode.Automatic);
        var observed = counter.ReadFrequencyHz();
        var error = Math.Abs(observed - context.CheckHz) / context.CheckHz;

        return error <= FrequencyTolerance
            ? (SelfCheckStatus.Passed,
                $"{observed / 1e9:0.000000000} GHz against {context.CheckHz / 1e9:0.000000000} GHz "
                + "requested.")
            : (SelfCheckStatus.Failed,
                $"{observed / 1e9:0.000000000} GHz against {context.CheckHz / 1e9:0.000000000} GHz "
                + $"requested — {error * 1e6:0.#} ppm out. Either the cable is wrong or the two are "
                + "not on the same 10 MHz. The counter cannot report its own reference state, so "
                + "check its EXT REF annunciator.");
    }

    /// <summary>S3: the DUT reaches the selected power sensor.</summary>
    private static (SelfCheckStatus, string) CheckPowerPath(SelfCheckContext context)
    {
        var dut = context.Get<Hp8340B>("dut");
        var meter = context.Get<Hp437B>("power-meter");

        if (dut is null || meter is null)
            return (SelfCheckStatus.NotRun, "The DUT or the power meter is not on the bench.");

        if (!meter.Sensor.Covers(context.CheckHz))
            return (SelfCheckStatus.NotRun,
                $"The {meter.Sensor.Name} does not cover {context.CheckHz / 1e9:0.###} GHz, so this "
                + "check cannot be made at that frequency.");

        dut.SetCwGHz(context.CheckHz / 1e9);
        dut.SetPowerDbm(context.CheckDbm);
        dut.WaitSettled(TimeSpan.FromSeconds(5));

        var reading = meter.ReadPower(context.CheckHz);
        var error = reading.Dbm - context.CheckDbm;

        return Math.Abs(error) <= LevelToleranceDb
            ? (SelfCheckStatus.Passed,
                $"{reading.Dbm:+0.0;-0.0} dBm on the {reading.Sensor} against "
                + $"{context.CheckDbm:+0.0;-0.0} dBm requested ({error:+0.0;-0.0} dB).")
            : (SelfCheckStatus.Failed,
                $"{reading.Dbm:+0.0;-0.0} dBm on the {reading.Sensor} against "
                + $"{context.CheckDbm:+0.0;-0.0} dBm requested — {error:+0.0;-0.0} dB out. Check the "
                + "cable and the pad, and that the meter was zeroed and calibrated for this sensor.");
    }
}
