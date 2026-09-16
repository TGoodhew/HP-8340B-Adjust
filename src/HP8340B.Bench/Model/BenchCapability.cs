using HP8340B.Instruments;
using HP8340B.Instruments.Config;
using HP8340B.Instruments.Model;

namespace HP8340B.Bench.Model;

/// <summary>
/// What this bench can claim for absolute power in a band, and what would change it.
/// </summary>
/// <param name="Band">The band.</param>
/// <param name="Best">The best traceability any path on this bench supports here.</param>
/// <param name="Path">Which instrument and sensor gives it, if any.</param>
/// <param name="WouldUpgrade">What arriving would improve it, if anything.</param>
public sealed record BandCapability(
    BandId Band,
    TraceabilityClass Best,
    string? Path,
    string? WouldUpgrade);

/// <summary>
/// Works out what the bench as configured can actually claim, rather than what it is hoped to.
///
/// <para><b>Why this is computed.</b> Band 4 is the case that matters: the 8481A and the 11792A
/// both stop at 18 GHz, so with those alone a 20-26.5 GHz power measurement is not a measurement
/// at all. Whether the bench can claim <c>Spec</c> up there depends on which sensors are actually
/// present, and "present" is a config flag that changes between campaigns. A rule kept in
/// somebody's head would be wrong the first time a sensor went back.</para>
///
/// <para>The asymmetry is deliberate and is the point of the issue this implements: the presence
/// of a 26.5 GHz sensor <b>upgrades</b> band 4 automatically, and its absence means a band-4
/// result <b>can never</b> be reported as Spec however it was taken.</para>
/// </summary>
public static class BenchCapability
{
    /// <summary>Roles that carry an absolute-power sensor.</summary>
    private static readonly string[] PowerRoles =
        ["power-sensor-low", "power-sensor-high", "usb-power-sensor", "measuring-receiver",
         "thermistor-mount"];

    /// <summary>
    /// The frequency each sensor model reaches, from its own specification. A model this project
    /// has never heard of contributes nothing rather than being assumed adequate.
    /// </summary>
    public static double MaxHzFor(string model) => model switch
    {
        var m when m.Contains("8481A", StringComparison.OrdinalIgnoreCase) => 18e9,
        var m when m.Contains("8485A", StringComparison.OrdinalIgnoreCase) => 26.5e9,
        var m when m.Contains("U8485A", StringComparison.OrdinalIgnoreCase) => 33e9,
        var m when m.Contains("11792A", StringComparison.OrdinalIgnoreCase) => 18e9,
        var m when m.Contains("8902A", StringComparison.OrdinalIgnoreCase) => 18e9,
        var m when m.Contains("478A", StringComparison.OrdinalIgnoreCase) => 10e9,
        _ => 0,
    };

    /// <summary>The lowest frequency each sensor reaches.</summary>
    public static double MinHzFor(string model) => model switch
    {
        var m when m.Contains("8481A", StringComparison.OrdinalIgnoreCase) => 10e6,
        var m when m.Contains("8485A", StringComparison.OrdinalIgnoreCase) => 30e6,
        var m when m.Contains("U8485A", StringComparison.OrdinalIgnoreCase) => 10e6,
        var m when m.Contains("11792A", StringComparison.OrdinalIgnoreCase) => 50e6,
        var m when m.Contains("8902A", StringComparison.OrdinalIgnoreCase) => 50e6,
        var m when m.Contains("478A", StringComparison.OrdinalIgnoreCase) => 10e6,
        _ => double.MaxValue,
    };

    /// <summary>Power sensors present on the bench, by model.</summary>
    public static IReadOnlyList<InstrumentConfig> PresentSensors(BenchConfig bench)
    {
        ArgumentNullException.ThrowIfNull(bench);

        return bench.Instruments
            .Where(i => i.Present)
            .Where(i => PowerRoles.Contains(i.Role, StringComparer.OrdinalIgnoreCase))
            .Where(i => MaxHzFor(i.Model) > 0)
            .ToList();
    }

    /// <summary>
    /// What the bench can claim for absolute power across <paramref name="band"/>.
    ///
    /// <para>A sensor has to cover the <b>whole</b> band, not part of it: a claim that holds at
    /// one end and not the other is not a claim about the band.</para>
    /// </summary>
    public static BandCapability For(BenchConfig bench, BandId band)
    {
        ArgumentNullException.ThrowIfNull(bench);

        var edges = Bands.Get(band);
        var startHz = edges.StartGHz * 1e9;
        var stopHz = edges.StopGHz * 1e9;

        var covering = PresentSensors(bench)
            .Where(s => MinHzFor(s.Model) <= startHz && MaxHzFor(s.Model) >= stopHz)
            .ToList();

        if (covering.Count > 0)
        {
            var best = covering.OrderByDescending(s => MaxHzFor(s.Model)).First();

            return new BandCapability(band, TraceabilityClass.Spec, best.Model, null);
        }

        // Nothing covers it. Say what would.
        var absent = bench.Instruments
            .Where(i => !i.Present)
            .Where(i => PowerRoles.Contains(i.Role, StringComparer.OrdinalIgnoreCase))
            .Where(i => MinHzFor(i.Model) <= startHz && MaxHzFor(i.Model) >= stopHz)
            .Select(i => i.Model)
            .ToList();

        var partial = PresentSensors(bench)
            .Where(s => MaxHzFor(s.Model) > startHz)
            .OrderByDescending(s => MaxHzFor(s.Model))
            .FirstOrDefault();

        var upgrade = absent.Count > 0
            ? $"{string.Join(" or ", absent)} arriving would make this band Spec."
            : "No sensor this project knows of, present or not, covers this band.";

        return new BandCapability(
            band,
            // A sensor that covers part of the band still gives valid deltas within its range.
            partial is null ? TraceabilityClass.NotPossible : TraceabilityClass.Relative,
            partial?.Model,
            upgrade);
    }

    /// <summary>Every band, for a capability summary.</summary>
    public static IReadOnlyList<BandCapability> All(BenchConfig bench) =>
        Bands.All.Select(b => For(bench, b.Id)).ToList();

    /// <summary>
    /// Refuses a <c>Spec</c> claim the bench cannot support.
    ///
    /// <para>The acceptance criterion this implements, stated as a rule rather than a hope: a
    /// band-4 result taken without a sensor that reaches 26.5 GHz can never be reported as Spec.
    /// </para>
    /// </summary>
    public static TruncateResult Constrain(
        BenchConfig bench, BandId band, TraceabilityClass claimed)
    {
        var capability = For(bench, band);

        // The enum runs best to worst, so the worse of the two wins.
        var allowed = (TraceabilityClass)Math.Max((int)claimed, (int)capability.Best);

        return allowed == claimed
            ? new TruncateResult(allowed, false, null)
            : new TruncateResult(
                allowed, true,
                $"A {claimed} claim in band {(int)band} was reduced to {allowed}: "
                + (capability.Path is null
                    ? "no sensor on this bench covers the band. "
                    : $"the best sensor present is the {capability.Path}, which does not cover the "
                      + "whole band. ")
                + capability.WouldUpgrade);
    }
}

/// <summary>The outcome of constraining a claim to what the bench supports.</summary>
/// <param name="Allowed">What may actually be claimed.</param>
/// <param name="WasReduced">True if the claim was cut down.</param>
/// <param name="Reason">Why, if it was.</param>
public sealed record TruncateResult(TraceabilityClass Allowed, bool WasReduced, string? Reason);
