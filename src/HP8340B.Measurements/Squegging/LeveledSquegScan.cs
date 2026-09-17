using HP8340B.Instruments.Model;

namespace HP8340B.Measurements.Squegging;

/// <summary>
/// Where maximum leveled power was found at one carrier, and how it compares with the figure
/// Table 4-9 says the instrument must manage.
/// </summary>
/// <param name="CarrierHz">The carrier the search was run at.</param>
/// <param name="MaxLeveledDbm">The highest requested level at which the ALC still held.</param>
/// <param name="FirstUnleveledDbm">The lowest requested level at which UNLEVELED set.</param>
/// <param name="MaxSpecifiedDbm">Table 4-9's figure for this frequency and option.</param>
/// <param name="ResolutionDb">How finely the two were separated.</param>
public sealed record LeveledPowerLimit(
    double CarrierHz,
    double MaxLeveledDbm,
    double FirstUnleveledDbm,
    double MaxSpecifiedDbm,
    double ResolutionDb)
{
    /// <summary>
    /// True if the instrument levels at least as high as Table 4-9 requires. False is a finding in
    /// its own right — a Section IV failure — quite separate from anything the scan turns up.
    /// </summary>
    public bool MeetsSpecification => MaxLeveledDbm >= MaxSpecifiedDbm - ResolutionDb;

    /// <summary>A line saying where the ALC gave up and whether that is good enough.</summary>
    public string Describe() =>
        $"{CarrierHz / 1e9:0.000} GHz: leveled to {MaxLeveledDbm:+0.0;-0.0} dBm, UNLEVELED at "
        + $"{FirstUnleveledDbm:+0.0;-0.0} dBm (±{ResolutionDb:0.##} dB). Table 4-9 requires "
        + $"{MaxSpecifiedDbm:+0.0;-0.0} dBm — "
        + (MeetsSpecification ? "met." : "NOT met, which is a Section IV failure in itself.");
}

/// <summary>What one leveled scan found.</summary>
/// <param name="Findings">Every response above the warning threshold, at every level.</param>
/// <param name="Limits">Where maximum leveled power was found at each carrier.</param>
/// <param name="PointsScanned">How many carriers were visited.</param>
/// <param name="LevelsScanned">How many carrier-and-level combinations were measured.</param>
/// <param name="Traceability">How far the result can be trusted.</param>
/// <param name="Note">Anything qualifying it.</param>
public sealed record LeveledSquegScanResult(
    IReadOnlyList<SquegFinding> Findings,
    IReadOnlyList<LeveledPowerLimit> Limits,
    int PointsScanned,
    int LevelsScanned,
    TraceabilityClass Traceability,
    string? Note = null)
{
    /// <summary>Findings that need somebody to turn a pot counter-clockwise.</summary>
    public IReadOnlyList<SquegFinding> Actionable =>
        Findings.Where(f => f.IsActionable).ToList();

    /// <summary>
    /// Findings above maximum specified leveled power. Real, recorded, and deliberately kept out
    /// of <see cref="Actionable"/>: 5-16 step 32 says this is the output going unleveled rather
    /// than squegging, and no pot fixes it.
    /// </summary>
    public IReadOnlyList<SquegFinding> AboveMaxLeveled =>
        Findings.Where(f => f.Severity == SpurSeverity.Unleveled).ToList();

    /// <summary>Carriers where the instrument failed to level as high as Table 4-9 requires.</summary>
    public IReadOnlyList<LeveledPowerLimit> BelowSpecification =>
        Limits.Where(l => !l.MeetsSpecification).ToList();

    /// <summary>The leveled bias pots implicated, in the order they should be worked.</summary>
    public IReadOnlyList<string> PotsToAdjust =>
        Findings.Where(f => f.IsActionable && f.Pot is not null)
            .Select(f => f.Pot!)
            .Distinct()
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The lowest ALC level at which each pot was seen to squeg. The adjustment is made at the
    /// worst case, and a pot that squegs 20 dB down is in a different state from one that only
    /// misbehaves at the top of its range.
    /// </summary>
    public IReadOnlyDictionary<string, double> WorstLevelPerPot =>
        Findings.Where(f => f.IsActionable && f.Pot is not null)
            .GroupBy(f => f.Pot!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Min(f => f.RequestedDbm), StringComparer.Ordinal);
}

/// <summary>
/// The leveled squegging test of 5-16 steps 37-50, with the level ladder of steps 42, 46 and 50.
///
/// <para><b>What this does differently from the manual.</b> The manual down-converts the DUT with
/// a second 8340B and a mixer and views a 600 MHz IF on an 8566B. This bench has an 8563E that
/// reaches 26.5 GHz, so the carrier is looked at in place — the same substitution
/// <see cref="SquegScanner"/> makes for the unleveled test, and for the same reason. It also
/// removes the mixing products the manual has to warn about, so its "if the control is adjusted
/// and there is no effect on the signal, the signal is probably a mixing product" caveat does not
/// apply here.</para>
///
/// <para><b>Maximum leveled power is found over the bus, not by eye.</b> Steps 39, 44 and 48 have
/// you turn the rotary knob up until just before the UNLEVELED lamp comes on. Extended status byte
/// #2 bit 6 <i>is</i> that lamp (Table 4-31), so <see cref="FindMaximumLeveledPower"/> brackets it
/// and bisects instead. That matters beyond convenience: the level found is recorded, and the
/// whole test then hangs off a number rather than off somebody's judgement of a lamp.</para>
///
/// <para><b>The line that decides whether there is anything to adjust</b> is maximum
/// <i>specified</i> leveled power — Table 4-9, not the level the instrument actually reached. At
/// or below it a response is a fault and the pot wants backing off; above it, per 5-16 steps
/// 32-34, it is the output going unleveled and cannot be adjusted out. Those two come out as
/// <see cref="SpurSeverity.Warn"/>/<see cref="SpurSeverity.Strong"/> and
/// <see cref="SpurSeverity.Unleveled"/> respectively, and only the first is actionable.</para>
///
/// <para>The thresholds, the carrier exclusion and the harmonic rejection are
/// <see cref="SquegScanner"/>'s. There is one definition of "is that a response" in this project,
/// and the unleveled and leveled tests are looking at the same instrument.</para>
/// </summary>
public static class LeveledSquegScanner
{
    /// <summary>The bands 5-16 steps 41, 45 and 49 walk — the multiplying ones, as 5-14 does.</summary>
    public static readonly IReadOnlyList<BandId> ScannedBands = SquegScanner.ScannedBands;

    /// <summary>Bottom of the level ladder: steps 42, 46 and 50 stop at -20 dBm.</summary>
    public const double LadderFloorDbm = -20.0;

    /// <summary>Ladder spacing. The manual's "5 dB increments".</summary>
    public const double LadderStepDb = 5.0;

    /// <summary>
    /// How far above Table 4-9 the level search is willing to go before it decides the UNLEVELED
    /// bit is not being read correctly and refuses to continue.
    ///
    /// <para>PROVISIONAL: never checked against an instrument. A healthy 8340B levels somewhat
    /// above its own specification — that margin is what the specification is for — but how much
    /// is unknown, and the figure is doing safety work as well as sanity work: everything below
    /// the search's ceiling is about to appear at the DUT output.</para>
    /// </summary>
    public const double SearchHeadroomDb = 6.0;

    /// <summary>
    /// Finest the level search resolves maximum leveled power to. The 8340B takes power in
    /// 0.01 dB steps, so this is a choice about how long the search takes, not a limit.
    /// </summary>
    public const double DefaultResolutionDb = 0.1;

    /// <summary>Coarse step used to bracket UNLEVELED before bisecting.</summary>
    public const double DefaultCoarseStepDb = 1.0;

    /// <summary>
    /// Finds maximum leveled power at one carrier: the highest requested level at which the ALC
    /// still holds, per 5-16 steps 39, 44 and 48.
    /// </summary>
    /// <param name="carrierHz">Where the DUT is set.</param>
    /// <param name="isUnleveled">
    /// Sets the DUT's level and reports extended status byte #2 bit 6. A delegate so the search
    /// can be run against the M0-12 model without owning the instruments.
    /// </param>
    /// <param name="option">Output option, which sets the Table 4-9 figure.</param>
    /// <param name="floorDbm">Level to start from. Must be one the instrument can hold.</param>
    /// <param name="coarseStepDb">Step used to bracket the transition.</param>
    /// <param name="resolutionDb">How finely to separate leveled from unleveled.</param>
    /// <exception cref="InvalidOperationException">
    /// If UNLEVELED is already set at the floor, or still not set
    /// <see cref="SearchHeadroomDb"/> above specification. Both mean the bit is not telling the
    /// truth about the instrument, and returning a number either way would put a level nobody
    /// chose on the output for the rest of the scan.
    /// </exception>
    public static LeveledPowerLimit FindMaximumLeveledPower(
        double carrierHz,
        Func<double, bool> isUnleveled,
        InstrumentOption option = InstrumentOption.Standard,
        double floorDbm = LadderFloorDbm,
        double coarseStepDb = DefaultCoarseStepDb,
        double resolutionDb = DefaultResolutionDb)
    {
        ArgumentNullException.ThrowIfNull(isUnleveled);

        if (resolutionDb <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(resolutionDb), resolutionDb, "Resolution must be positive.");

        if (coarseStepDb < resolutionDb)
            throw new ArgumentOutOfRangeException(
                nameof(coarseStepDb), coarseStepDb,
                "The bracketing step cannot be finer than the resolution it brackets for.");

        var specified = MaxLeveledPower.ForFrequency(option, carrierHz / 1e9);
        var ceiling = specified + SearchHeadroomDb;

        if (floorDbm >= ceiling)
            throw new ArgumentOutOfRangeException(
                nameof(floorDbm), floorDbm,
                $"The floor is at or above the search ceiling of {ceiling:+0.0;-0.0} dBm, so there "
                + "is nothing to search.");

        // Unleveled at the bottom of the ladder is not a low maximum, it is a broken measurement:
        // an open ALC loop, no RF, or the wrong bit being read. A number here would look like an
        // answer.
        if (isUnleveled(floorDbm))
            throw new InvalidOperationException(
                $"UNLEVELED is already set at {floorDbm:+0.0;-0.0} dBm at "
                + $"{carrierHz / 1e9:0.000} GHz, which is {specified - floorDbm:0.#} dB below "
                + "specification. That is not a low maximum leveled power — the ALC loop, the RF "
                + "path or the status read is wrong. Fix that before scanning.");

        var leveled = floorDbm;
        var unleveled = double.NaN;

        // Indexed, not accumulated. Repeatedly adding a step drifts, and here the drift lands on
        // the level the instrument is actually set to — see the same lesson in
        // <see cref="SquegScanner.ScanPoints"/>.
        var steps = (int)Math.Floor((ceiling - floorDbm) / coarseStepDb);

        for (var i = 1; i <= steps; i++)
        {
            var level = floorDbm + i * coarseStepDb;

            if (isUnleveled(level))
            {
                unleveled = level;
                break;
            }

            leveled = level;
        }

        if (double.IsNaN(unleveled))
            throw new InvalidOperationException(
                $"The ALC still held at {ceiling:+0.0;-0.0} dBm at {carrierHz / 1e9:0.000} GHz, "
                + $"which is {SearchHeadroomDb:0.#} dB above the Table 4-9 figure of "
                + $"{specified:+0.0;-0.0} dBm. An 8340B does not do that: extended status byte #2 "
                + "bit 6 is not reaching this code. Refusing to scan rather than run the whole "
                + "test at a level nobody chose.");

        // Bisect the bracket. Every probe in here is at a level between two already visited, so
        // the search never goes higher than the coarse pass already did.
        while (unleveled - leveled > resolutionDb)
        {
            var middle = (leveled + unleveled) / 2.0;

            if (isUnleveled(middle)) unleveled = middle;
            else leveled = middle;
        }

        return new LeveledPowerLimit(carrierHz, leveled, unleveled, specified, resolutionDb);
    }

    /// <summary>
    /// The levels one carrier is tested at: maximum leveled power, then the 5 dB grid below it
    /// down to -20 dBm.
    ///
    /// <para>The grid is the manual's, not a relative ladder. Steps 42, 46 and 50 say to enter
    /// "the maximum ALC power level that will be a 5 dB increment below max leveled power (i.e.,
    /// 15, 10, 5)" — round numbers on the front panel, not maximum-minus-five. So a maximum of
    /// +9.4 dBm is followed by +5, not +4.4.</para>
    /// </summary>
    public static IReadOnlyList<double> LevelLadder(
        double maxLeveledDbm, double floorDbm = LadderFloorDbm, double stepDb = LadderStepDb)
    {
        if (stepDb <= 0)
            throw new ArgumentOutOfRangeException(nameof(stepDb), stepDb, "Step must be positive.");

        if (maxLeveledDbm < floorDbm)
            throw new ArgumentOutOfRangeException(
                nameof(maxLeveledDbm), maxLeveledDbm,
                $"Maximum leveled power is below the {floorDbm:+0.#;-0.#} dBm floor of the ladder, "
                + "so the test has no range to run over.");

        var levels = new List<double> { maxLeveledDbm };

        // The first grid point strictly below the maximum. Ceiling-then-back-off rather than
        // floor, so a maximum that lands exactly on the grid is not visited twice.
        var next = Math.Ceiling(maxLeveledDbm / stepDb) * stepDb;
        if (next >= maxLeveledDbm - 1e-9) next -= stepDb;

        for (; next >= floorDbm - 1e-9; next -= stepDb) levels.Add(next);

        return levels;
    }

    /// <summary>
    /// Classifies one response found with the ALC in circuit.
    ///
    /// <para>The rule is 5-16 steps 32-34's, and it turns on maximum <b>specified</b> leveled
    /// power rather than the level this instrument happened to reach. At or below it, a response
    /// is a fault the leveled bias pot can fix. Above it, the output is going unleveled and no
    /// amount of turning helps.</para>
    /// </summary>
    /// <param name="carrierHz">Where the DUT is set.</param>
    /// <param name="alcDbm">The requested ALC level — the manual's ENTRY DISPLAY reading.</param>
    /// <param name="option">Output option, for the Table 4-9 figure.</param>
    /// <param name="dbc">The response level relative to the carrier.</param>
    /// <param name="noiseFloorDbc">The analyzer's own floor here.</param>
    public static SpurSeverity ClassifyLeveled(
        double carrierHz,
        double alcDbm,
        InstrumentOption option,
        double dbc,
        double noiseFloorDbc)
    {
        var plain = SquegScanner.Classify(dbc, noiseFloorDbc);

        if (plain == SpurSeverity.None) return plain;

        var specified = MaxLeveledPower.ForFrequency(option, carrierHz / 1e9);

        return alcDbm > specified + 1e-9 ? SpurSeverity.Unleveled : plain;
    }

    /// <summary>
    /// Runs the scan: every carrier, at maximum leveled power and every 5 dB step down to -20 dBm.
    /// </summary>
    /// <param name="points">
    /// Carriers to visit. <see cref="SquegScanner.ScanPoints"/> produces the manual's 100 MHz
    /// steps through bands 2, 3 and 4.
    /// </param>
    /// <param name="isUnleveled">
    /// Sets the DUT to a carrier and level and reports extended status byte #2 bit 6.
    /// </param>
    /// <param name="measure">
    /// Sets the DUT to a carrier and level and returns the responses beside it as (offset, dBc)
    /// pairs, with the analyzer's noise floor there.
    /// </param>
    /// <param name="option">Output option, which sets the Table 4-9 figures.</param>
    /// <param name="floorDbm">Bottom of the ladder.</param>
    /// <param name="cancellationToken">Stops a long scan. This one is long: the whole ladder at
    /// every 100 MHz point across three bands.</param>
    public static LeveledSquegScanResult Scan(
        IReadOnlyList<double> points,
        Func<double, double, bool> isUnleveled,
        Func<double, double, (IReadOnlyList<(double OffsetHz, double Dbc)> Responses, double NoiseFloorDbc)> measure,
        InstrumentOption option = InstrumentOption.Standard,
        double floorDbm = LadderFloorDbm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(isUnleveled);
        ArgumentNullException.ThrowIfNull(measure);

        var findings = new List<SquegFinding>();
        var limits = new List<LeveledPowerLimit>();
        var levelsScanned = 0;

        foreach (var carrierHz in points)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var limit = FindMaximumLeveledPower(
                carrierHz, dbm => isUnleveled(carrierHz, dbm), option, floorDbm);

            limits.Add(limit);

            foreach (var alcDbm in LevelLadder(limit.MaxLeveledDbm, floorDbm))
            {
                cancellationToken.ThrowIfCancellationRequested();
                levelsScanned++;

                var (responses, floor) = measure(carrierHz, alcDbm);

                foreach (var (offsetHz, dbc) in responses)
                {
                    if (SquegScanner.IsCarrier(offsetHz)) continue;
                    if (SquegScanner.IsHarmonicallyRelated(carrierHz, offsetHz)) continue;

                    var severity = ClassifyLeveled(carrierHz, alcDbm, option, dbc, floor);
                    if (severity == SpurSeverity.None) continue;

                    findings.Add(new SquegFinding(
                        carrierHz, offsetHz, dbc, severity,
                        Bands.LeveledPotFor(carrierHz / 1e9),
                        floor, alcDbm));
                }
            }
        }

        var actionable = findings.Count(f => f.IsActionable);

        return new LeveledSquegScanResult(
            findings,
            limits,
            points.Count,
            levelsScanned,
            // Spec, but bounded by the analyzer's dynamic range at each point — which is why the
            // floor is recorded against every finding rather than assumed.
            TraceabilityClass.Spec,
            Note(findings.Count, actionable, points.Count, levelsScanned, limits));
    }

    private static string? Note(
        int findings, int actionable, int points, int levels, IReadOnlyList<LeveledPowerLimit> limits)
    {
        var belowSpec = limits.Count(l => !l.MeetsSpecification);

        var lead = findings == 0
            ? $"No responses above {SquegScanner.WarnDbc:0.#} dBc across {points} points and "
              + $"{levels} levels."
            : actionable == 0
                ? $"{findings} response(s), all of them above maximum specified leveled power. "
                  + "Per 5-16 step 32 that is the output going unleveled, not squegging. Nothing "
                  + "to adjust."
                : null;

        if (belowSpec == 0) return lead;

        // Separate finding, and a more serious one than anything the scan itself turns up: the
        // instrument is not meeting Table 4-9.
        return (lead is null ? "" : lead + " ")
               + $"{belowSpec} of {points} carriers failed to level to the Table 4-9 figure at "
               + "all, which is a Section IV failure independent of any squegging.";
    }

    /// <summary>
    /// The warning shown before the run (rule 5). Lower than the unleveled scan's +20 dBm, but the
    /// search deliberately pushes past maximum leveled power to find where it is.
    /// </summary>
    public static string SafetyWarning(InstrumentOption option)
    {
        // Worst case over the bands this scan actually walks, rather than over the whole
        // instrument: band 1 is specified higher but is not visited here.
        var worstDbm = ScannedBands
            .Select(id => MaxLeveledPower.ForFrequency(option, Bands.Get(id).StartGHz + 1e-6))
            .Max() + SearchHeadroomDb;

        return "This scan raises the DUT's level until UNLEVELED sets, so the output reaches "
               + "maximum leveled power for the band and up to "
               + $"{SearchHeadroomDb:0.#} dB beyond it — as much as {worstDbm:+0.#;-0.#} dBm on "
               + "this option. Before starting: confirm the 8563E has input attenuation or a "
               + "characterised pad, that no power sensor is connected without one (the 848x "
               + "series and the 11792A are refused above +17 dBm, the 478A above +7 dBm), and "
               + "that the 5351A is padded.";
    }
}
