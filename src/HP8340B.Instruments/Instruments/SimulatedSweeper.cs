using HP8340B.Instruments.Model;
using HP8340B.Instruments.Visa;

namespace HP8340B.Instruments;

/// <summary>
/// How fast the DUT is sweeping. 5-14 steps 32-58 compare three traces per band, and the fast
/// and single ones are the ones delay compensation has to rescue.
/// </summary>
public enum SweepSpeed
{
    /// <summary>The manual's 200 ms reference sweep. Delay compensation does not affect it.</summary>
    Slow,
    /// <summary>AUTO sweep time — as fast as the instrument will go.</summary>
    Auto,
    /// <summary>Single sweep, triggered.</summary>
    Single,
}

/// <summary>
/// One A24 SRD-bias pot in the model.
///
/// <para>The manual's direction of travel (5-14 steps 11-14): turning the pot <b>clockwise
/// increases bias and moves toward the squegging region</b>, and the adjustment is to find
/// maximum power and then back off about 0.5 dB. So in this model power climbs with bias right up
/// to <see cref="SquegThreshold"/> — the peak and the onset of squegging are the same place,
/// which is exactly why the procedure has to overshoot and come back.</para>
/// </summary>
/// <param name="Name">Pot designation, e.g. "X2A (A24R3)".</param>
public sealed class SrdBiasPot(string name)
{
    public string Name { get; } = name;

    /// <summary>Pot rotation, 0 (fully CCW) to 1 (fully CW). Clockwise increases bias.</summary>
    public double Bias { get; set; } = 0.5;

    /// <summary>
    /// Bias above which the SRD squegs. Varies slightly per pot in a real instrument; settable
    /// here so a test can model an assembly that squegs earlier than it should.
    /// </summary>
    public double SquegThreshold { get; set; } = 0.70;

    /// <summary>How much power the bias is worth, in dB, between fully CCW and the threshold.</summary>
    public double MaxGainDb { get; set; } = 3.5;

    /// <summary>
    /// Power the current bias contributes, in dB. Climbs steeply at first and flattens as it
    /// approaches the threshold, which is what makes the peak hard to find by eye and the reason
    /// 5-14 has you watch A24TP12's slope rather than trusting the peak.
    /// </summary>
    public double GainDb
    {
        get
        {
            var fraction = Math.Clamp(Bias / SquegThreshold, 0, 1);
            return MaxGainDb * (1.0 - Math.Pow(1.0 - fraction, 2));
        }
    }

    /// <summary>How far past the threshold the pot is, as a fraction of the remaining travel.</summary>
    public double Excess => Bias <= SquegThreshold
        ? 0
        : Math.Clamp((Bias - SquegThreshold) / Math.Max(1e-9, 1.0 - SquegThreshold), 0, 1);

    /// <summary>True once the bias is past the threshold.</summary>
    public bool Squegging => Excess > 0;
}

/// <summary>
/// One band's delay compensation — the cal constant that keeps a fast sweep's envelope on top of
/// the slow one (5-14 steps 32-58).
/// </summary>
/// <param name="Constant">Cal constant name, e.g. "CC6".</param>
public sealed class DelayCompensation(string constant, int optimum = 65)
{
    public string Constant { get; } = constant;

    /// <summary>Current value. Delay-compensation constants have range 0-131 (5-14 step 2).</summary>
    public int Value { get; set; } = optimum;

    /// <summary>Where this band actually wants to sit. Unknown to the procedure, which searches.</summary>
    public int Optimum { get; } = optimum;

    /// <summary>Normalised distance from optimum, 0 to 1.</summary>
    public double Error => Math.Clamp(Math.Abs(Value - Optimum) / 131.0, 0, 1);
}

/// <summary>A spurious response, as the analyzer would see it.</summary>
/// <param name="OffsetHz">Offset from the carrier, signed.</param>
/// <param name="Dbc">Level relative to the carrier, negative.</param>
public sealed record SimulatedSpur(double OffsetHz, double Dbc);

/// <summary>One point of a swept envelope.</summary>
public sealed record SweepPoint(double Ghz, double Dbm);

/// <summary>
/// A physical model of the 8340B's RF output: how much power is available at a frequency, what
/// happens when the SRD bias is too high, and how a fast sweep degrades when delay compensation
/// is wrong.
///
/// <para><b>Why this exists.</b> Repo rule 4 says <c>dotnet test</c> must pass with no hardware.
/// Without a model that actually misbehaves, the squegging scanner (M1-04/M1-05), the
/// monotonicity test (M1-06) and the fast-vs-slow comparator (M2-07) have no test coverage at all
/// until the DUT is on the bench — and those are the parts of this project that most need to be
/// right, because a scanner that silently finds nothing looks identical to a healthy
/// instrument.</para>
///
/// <para><b>What squegging is</b>, from the 5-14 footnote: undesired oscillations of the YIG
/// sphere or the step recovery diode, both inside the SYTM. It shows three ways, and the model
/// produces all three: <see cref="OutputPowerDbm"/> reverses (output falls as the request rises),
/// <see cref="SweptEnvelope"/> grows a hole, and <see cref="SpursAt"/> returns a response offset
/// from the carrier.</para>
///
/// <para><b>Band 1 is deliberately different.</b> The manual says its squegging is a function of
/// SYTM input power, happens only at maximum unleveled power, and cannot be adjusted out. There
/// is no A24 pot for it — <see cref="Bands.UnleveledPotFor"/> returns null in bands 0 and 1 — so
/// the model gives it no pot either, and a scanner that reports band 1 as an adjustable fault is
/// wrong about the instrument.</para>
///
/// <para>The model is fully deterministic — the envelope ripple is a function of frequency, not
/// a random draw — so a trace is repeatable and a test that fails fails every time.</para>
/// </summary>
public sealed class SimulatedSweeper
{
    /// <summary>
    /// Headroom above the Table 4-9 leveled limit that a perfectly adjusted instrument has, in
    /// bands with no SRD bias to adjust. 5-14 steps 15/22/30 require the minimum power in a band
    /// to sit more than 1 dB above the leveled spec, so a healthy model has to clear that
    /// comfortably or every test would be balanced on the edge of its own criterion.
    /// </summary>
    public const double UnmultipliedHeadroomDb = 4.0;

    /// <summary>
    /// Headroom in the multiplying bands with the SRD bias fully CCW. The bias is worth up to
    /// <see cref="SrdBiasPot.MaxGainDb"/> on top of this, so a badly set pot drops the band below
    /// the 5-14 minimum-power criterion and a well set one clears it.
    /// </summary>
    public const double MultipliedBaseHeadroomDb = 0.5;

    /// <summary>Output option, which sets the Table 4-9 limits the model is built on.</summary>
    public InstrumentOption Option { get; set; } = InstrumentOption.Standard;

    /// <summary>
    /// The six unleveled SRD-bias pots, keyed by the designation
    /// <see cref="Bands.UnleveledPotFor"/> returns. Bands 0 and 1 have none.
    /// </summary>
    public IReadOnlyDictionary<string, SrdBiasPot> UnleveledBias { get; }

    /// <summary>The three leveled SRD-bias pots (5-16 steps 30-50), one per multiplying band.</summary>
    public IReadOnlyDictionary<string, SrdBiasPot> LeveledBias { get; }

    /// <summary>Delay compensation per band — CC5 to CC8 and their relatives.</summary>
    public IReadOnlyDictionary<BandId, DelayCompensation> Delay { get; }

    /// <summary>
    /// SYTM tracking quality per band, 0 (perfect) to 1 (badly mistracked). Stands for the A28
    /// GAIN/OFST and breakpoint pots and CC73/CC74: it sets the depth of the ripple in the swept
    /// envelope, not the average power.
    /// </summary>
    public Dictionary<BandId, double> TrackingError { get; } = new()
    {
        [BandId.Band0] = 0.05,
        [BandId.Band1] = 0.05,
        [BandId.Band2] = 0.10,
        [BandId.Band3] = 0.10,
        [BandId.Band4] = 0.15,
    };

    public SimulatedSweeper()
    {
        // Pot designations come from the verified Bands mapping rather than being restated, so a
        // correction there cannot leave the model disagreeing with the procedure.
        var unleveled = new Dictionary<string, SrdBiasPot>(StringComparer.Ordinal);
        foreach (var ghz in new[] { 8.0, 12.0, 14.0, 17.0, 21.0, 25.0 })
        {
            var pot = Bands.UnleveledPotFor(ghz)!;
            unleveled[pot] = new SrdBiasPot(pot);
        }

        UnleveledBias = unleveled;

        var leveled = new Dictionary<string, SrdBiasPot>(StringComparer.Ordinal);
        foreach (var ghz in new[] { 10.0, 17.0, 23.3 })
        {
            var pot = Bands.LeveledPotFor(ghz)!;
            leveled[pot] = new SrdBiasPot(pot);
        }

        LeveledBias = leveled;

        // Constants per the brief's reading of 5-14 steps 32-58. The optima differ per band so a
        // search that happens to work for one band is not silently right for all of them.
        Delay = new Dictionary<BandId, DelayCompensation>
        {
            [BandId.Band0] = new("CC5", optimum: 60),
            [BandId.Band1] = new("CC5", optimum: 60),
            [BandId.Band2] = new("CC6", optimum: 72),
            [BandId.Band3] = new("CC7", optimum: 48),
            [BandId.Band4] = new("CC75", optimum: 90),
        };
    }

    // --- Power ------------------------------------------------------------------------------

    /// <summary>The unleveled SRD-bias pot covering <paramref name="ghz"/>, or null below 7 GHz.</summary>
    public SrdBiasPot? UnleveledPotAt(double ghz)
    {
        var name = Bands.UnleveledPotFor(ghz);
        return name is null ? null : UnleveledBias[name];
    }

    /// <summary>The leveled SRD-bias pot covering <paramref name="ghz"/>, or null below 7 GHz.</summary>
    public SrdBiasPot? LeveledPotAt(double ghz)
    {
        var name = Bands.LeveledPotFor(ghz);
        return name is null ? null : LeveledBias[name];
    }

    /// <summary>
    /// Maximum power the instrument can produce at <paramref name="ghz"/> with the ALC out of the
    /// way — the envelope 5-14 is adjusting. Built up from the Table 4-9 leveled limit so the
    /// model cannot drift away from the specification it is checked against.
    /// </summary>
    public double MaxAvailablePowerDbm(double ghz)
    {
        var leveledLimit = MaxLeveledPower.ForFrequency(Option, ghz);
        var band = Bands.ForFrequency(ghz)!;
        var pot = UnleveledPotAt(ghz);

        var headroom = pot is null
            ? UnmultipliedHeadroomDb
            : MultipliedBaseHeadroomDb + pot.GainDb;

        return leveledLimit + headroom - TrackingRippleDb(ghz, band);
    }

    /// <summary>
    /// Ripple from imperfect SYTM-to-YO tracking. Deterministic in frequency: the same frequency
    /// always gives the same ripple, so a trace is repeatable and a minimum-finder can be tested.
    /// </summary>
    private double TrackingRippleDb(double ghz, Band band)
    {
        var error = TrackingError.GetValueOrDefault(band.Id, 0.1);

        // Two incommensurate periods, so the envelope has structure rather than one clean sine a
        // naive peak-finder could exploit.
        var ripple = Math.Sin(ghz * 3.1) * 0.6 + Math.Sin(ghz * 7.7 + 1.3) * 0.4;

        return error * 6.0 * (ripple + 1.0) / 2.0;
    }

    /// <summary>
    /// True if the instrument is squegging at this frequency and requested level.
    ///
    /// <para>Two quite different causes. In the multiplying bands it is the SRD bias being too
    /// far clockwise, and backing the pot off cures it. In band 1 it is a function of SYTM input
    /// power, appears only when maximum unleveled power is asked for, and cannot be tuned out —
    /// the manual is explicit, and 5-14 step 53 works around it by running band 1 at -50 dBm.
    /// </para>
    /// </summary>
    public bool IsSquegging(double ghz, double requestedDbm)
    {
        var band = Bands.ForFrequency(ghz)
                   ?? throw new ArgumentOutOfRangeException(nameof(ghz), ghz, "Outside 0.01-26.5 GHz.");

        if (band.Id == BandId.Band1)
            return requestedDbm >= MaxAvailablePowerDbm(ghz) - 0.5;

        var pot = UnleveledPotAt(ghz);
        if (pot is null || !pot.Squegging) return false;

        // Squegging needs drive as well as bias: a mis-set pot is quiet at low requested power,
        // which is why 5-14's squegging test asks for +20 dBm and 5-16's walks the level down.
        return requestedDbm >= MaxAvailablePowerDbm(ghz) - 6.0;
    }

    /// <summary>
    /// Power actually produced for a requested level.
    ///
    /// <para>Normally the lesser of what was asked for and what is available. When squegging,
    /// <b>output falls as the request rises</b> — the manual's "power reversal", and the symptom
    /// the monotonicity test (M1-06) exists to find.</para>
    /// </summary>
    public double OutputPowerDbm(double ghz, double requestedDbm, SweepSpeed speed = SweepSpeed.Slow)
    {
        // The delay penalty is subtracted after the request is capped, not folded into the
        // available power, so a fast sweep sags at the band start whether or not the ALC has
        // headroom. That is a simplification -- the real ALC loop would claw some of it back --
        // but the loop cannot track at AUTO sweep speed, which is the whole reason 5-14 steps
        // 32-58 exist. Without it the band-1 check at -50 dBm (step 53) would compare two
        // identical traces and prove nothing.
        var output = Math.Min(requestedDbm, MaxAvailablePowerDbm(ghz)) - DelayPenaltyDb(ghz, speed);

        if (!IsSquegging(ghz, requestedDbm)) return output;

        var band = Bands.ForFrequency(ghz)!;
        var excess = band.Id == BandId.Band1 ? 0.4 : UnleveledPotAt(ghz)!.Excess;

        // Past the onset, every further dB asked for costs rather than gains. The slope grows
        // with how far the pot has been taken past the threshold.
        var onset = MaxAvailablePowerDbm(ghz) - 6.0;
        var over = Math.Max(0, requestedDbm - onset);

        return output - over * (0.5 + 2.0 * excess);
    }

    /// <summary>
    /// Loss on a fast or single sweep from delay compensation being off optimum. The slow sweep
    /// is the reference and never takes a penalty.
    ///
    /// <para>Worst at the start of a band's sweep and decaying across it, which is the dropout
    /// 5-14 step 45 uses CC75 to remove. 5-14's limit is 1 dB between the fast and slow traces
    /// (2 dB in band 1), so a correctly set constant has to come in well under that and a badly
    /// set one has to exceed it.</para>
    /// </summary>
    public double DelayPenaltyDb(double ghz, SweepSpeed speed)
    {
        if (speed == SweepSpeed.Slow) return 0;

        var band = Bands.ForFrequency(ghz)!;
        if (!Delay.TryGetValue(band.Id, out var compensation)) return 0;

        // Position in the band, 0 at the start edge.
        var position = (ghz - band.StartGHz) / (band.StopGHz - band.StartGHz);
        var startWeighted = Math.Exp(-3.0 * Math.Clamp(position, 0, 1));

        // A single sweep is slightly worse than AUTO: it has no preceding sweep to have settled.
        var speedFactor = speed == SweepSpeed.Single ? 1.25 : 1.0;

        // Even at optimum a fast sweep does not land exactly on the slow one. Without this floor
        // the two traces are bit-identical at optimum, and a fast-vs-slow comparator that does
        // nothing at all would pass its own test.
        var penalty = (IrreducibleFastSweepLossDb + compensation.Error * 12.0) * startWeighted;

        return penalty * speedFactor;
    }

    /// <summary>
    /// Loss on a fast sweep that no cal constant removes. Well inside the manual's 1 dB limit,
    /// but not zero — see <see cref="DelayPenaltyDb"/>.
    /// </summary>
    public const double IrreducibleFastSweepLossDb = 0.2;

    // --- Envelope and spurs -------------------------------------------------------------------

    /// <summary>
    /// The swept power envelope, as the detector and scope see it in setup S4.
    ///
    /// <para>A squegging half-band grows a <b>power hole</b> — a narrow, deep dip that a
    /// band-wide minimum check will catch but an average will not, which is the whole reason the
    /// envelope is looked at point by point.</para>
    /// </summary>
    public IReadOnlyList<SweepPoint> SweptEnvelope(
        double startGhz, double stopGhz, double requestedDbm,
        int points = 401, SweepSpeed speed = SweepSpeed.Slow)
    {
        if (points < 2) throw new ArgumentOutOfRangeException(nameof(points), points, "Need at least 2 points.");
        if (stopGhz <= startGhz)
            throw new ArgumentException("Stop frequency must be above start.", nameof(stopGhz));

        var step = (stopGhz - startGhz) / (points - 1);
        var trace = new List<SweepPoint>(points);

        for (var i = 0; i < points; i++)
        {
            var ghz = startGhz + i * step;
            var dbm = OutputPowerDbm(ghz, requestedDbm, speed) - PowerHoleDb(ghz, requestedDbm);

            trace.Add(new SweepPoint(ghz, dbm));
        }

        return trace;
    }

    /// <summary>
    /// Depth of the power hole at <paramref name="ghz"/>, in dB. Zero unless squegging. The hole
    /// sits at a fixed place inside the affected half-band so a scanner can be asked to find it
    /// and then checked against where it actually is.
    /// </summary>
    public double PowerHoleDb(double ghz, double requestedDbm)
    {
        if (!IsSquegging(ghz, requestedDbm)) return 0;

        var band = Bands.ForFrequency(ghz)!;
        var excess = band.Id == BandId.Band1 ? 0.4 : UnleveledPotAt(ghz)!.Excess;

        var centre = HoleCentreGHz(ghz);
        var width = 0.08;                                   // GHz, narrow enough to be missed by eye
        var distance = (ghz - centre) / width;

        return (4.0 + 12.0 * excess) * Math.Exp(-distance * distance);
    }

    /// <summary>
    /// Where the hole sits for the half-band containing <paramref name="ghz"/> — one third of the
    /// way in, so it is never at an edge or a midpoint a test might hit by luck.
    /// </summary>
    public static double HoleCentreGHz(double ghz)
    {
        var (low, high) = HalfBand(ghz);
        return low + (high - low) / 3.0;
    }

    /// <summary>The half-band containing <paramref name="ghz"/>, matching the A24 pot split.</summary>
    public static (double LowGHz, double HighGHz) HalfBand(double ghz)
    {
        var band = Bands.ForFrequency(ghz)
                   ?? throw new ArgumentOutOfRangeException(nameof(ghz), ghz, "Outside 0.01-26.5 GHz.");

        // The splits are the ones Bands.UnleveledPotFor uses: band 2 at 10, band 3 at 15,
        // band 4 at 23 (5-14 steps 70-80).
        double? split = band.Id switch
        {
            BandId.Band2 => 10.0,
            BandId.Band3 => 15.0,
            BandId.Band4 => 23.0,
            _ => null,
        };

        if (split is not { } s) return (band.StartGHz, band.StopGHz);

        return ghz < s ? (band.StartGHz, s) : (s, band.StopGHz);
    }

    /// <summary>
    /// Spurious responses on the output at <paramref name="ghz"/>, as the 8563E would see them
    /// looking at the carrier plus or minus 300 MHz (5-14 steps 70-80).
    ///
    /// <para>Empty unless squegging. The relaxation oscillation puts a pair of sidebands on the
    /// carrier whose offset and level both grow with how far the bias has been taken past the
    /// threshold, so a scanner can be tested on "found it" and on "how bad".</para>
    /// </summary>
    public IReadOnlyList<SimulatedSpur> SpursAt(double ghz, double requestedDbm)
    {
        if (!IsSquegging(ghz, requestedDbm)) return [];

        var band = Bands.ForFrequency(ghz)!;
        var excess = band.Id == BandId.Band1 ? 0.4 : UnleveledPotAt(ghz)!.Excess;

        // Kept so that the second harmonic below still lands inside the plus-or-minus 300 MHz the
        // procedure actually scans. A model that emitted a spur outside the window the
        // measurement looks at would make a working scanner look broken.
        var offsetHz = 25e6 + 115e6 * excess;
        var dbc = -35.0 + 25.0 * excess;

        return
        [
            new SimulatedSpur(-offsetHz, dbc),
            new SimulatedSpur(+offsetHz, dbc),
            // The second harmonic of the relaxation oscillation, well down.
            new SimulatedSpur(+2 * offsetHz, dbc - 12.0),
        ];
    }

    // --- Judging, the same way the procedure does ----------------------------------------------

    /// <summary>
    /// The 5-14 minimum-power check (steps 15, 22 and 30): the lowest power anywhere in the band
    /// must sit more than 1 dB above the Table 4-9 leveled limit.
    ///
    /// <para>The manual applies this to the multiplying bands only, and this refuses the others
    /// rather than returning a number that looks like an answer. Band 1 in particular cannot be
    /// examined at maximum unleveled power <b>because it squegs there</b> — that is not a fault
    /// and not something a pot can fix, and it is why 5-14 step 53 runs band 1 at -50 dBm.</para>
    /// </summary>
    public bool MeetsMinimumPower(BandId bandId, SweepSpeed speed = SweepSpeed.Slow)
    {
        if (bandId is not (BandId.Band2 or BandId.Band3 or BandId.Band4))
            throw new ArgumentOutOfRangeException(
                nameof(bandId), bandId,
                "The 5-14 minimum-power check (steps 15, 22, 30) applies to the multiplying bands "
                + "only. Band 1 squegs at maximum unleveled power by design, so its envelope "
                + "cannot be read there at all.");

        var band = Bands.Get(bandId);

        // Ask for more than the instrument can give, so the trace is the available envelope.
        var envelope = SweptEnvelope(band.StartGHz, band.StopGHz - 1e-6, requestedDbm: 25, speed: speed);

        return envelope.All(p => p.Dbm > MaxLeveledPower.MinPowerTargetDbm(Option, p.Ghz));
    }

    /// <summary>
    /// The 5-14 delay-compensation check (steps 32-58): the fast and single traces must sit
    /// within 1 dB of the slow one — 2 dB in band 1.
    /// </summary>
    public double WorstDelayDeviationDb(BandId bandId, SweepSpeed speed)
    {
        var band = Bands.Get(bandId);

        // Band 1 is compared at -50 dBm, per 5-14 step 53, so its unadjustable squegging at
        // maximum unleveled power does not swamp the thing actually being measured. Everywhere
        // else the comparison is made on the available envelope.
        var requested = bandId == BandId.Band1 ? -50.0 : 25.0;

        var slow = SweptEnvelope(band.StartGHz, band.StopGHz - 1e-6, requested, speed: SweepSpeed.Slow);
        var fast = SweptEnvelope(band.StartGHz, band.StopGHz - 1e-6, requested, speed: speed);

        return slow.Zip(fast, (s, f) => Math.Abs(s.Dbm - f.Dbm)).Max();
    }

    /// <summary>The manual's limit for <paramref name="bandId"/>: 1 dB, or 2 dB in band 1.</summary>
    public static double DelayDeviationLimitDb(BandId bandId) =>
        bandId == BandId.Band1 ? 2.0 : 1.0;

    // --- The bus side ---------------------------------------------------------------------------

    /// <summary>
    /// Requested output level, in dBm, as last set over the bus. The link keeps this in step with
    /// what the driver sends so the UNLEVELED bit means what it means on hardware.
    /// </summary>
    public double RequestedDbm { get; set; } = 0.0;

    /// <summary>Current CW frequency, GHz.</summary>
    public double CwGHz { get; set; } = 10.0;

    /// <summary>True when the requested level is above what the ALC can hold — the UNLEVELED lamp.</summary>
    public bool IsUnleveled => RequestedDbm > MaxLeveledPower.ForFrequency(Option, CwGHz);

    /// <summary>
    /// Builds a link driven by this model. The <see cref="Hp8340B"/> driver over it behaves as it
    /// would on hardware: the status bytes reflect the model, so a settle-wait, an UNLEVELED
    /// check and a reference check all exercise their real code paths.
    /// </summary>
    /// <summary>
    /// Shared bench state, so what this DUT is set to is what the counter and analyzer see.
    /// See <see cref="SimulatedBenchState"/>.
    /// </summary>
    public SimulatedBenchState? Bench { get; set; }

    public SimulatedInstrumentLink CreateLink(
        string resourceName = "SIM::dut::INSTR", SimulatedBenchState? bench = null)
    {
        Bench = bench;

        SimulatedInstrumentLink? link = null;
        link = new SimulatedInstrumentLink(resourceName, command => Respond(command, link!))
        {
            StatusByte = (byte)(StatusByte1.RfSettled | StatusByte1.EndOfSweep),
        };

        RefreshStatusBytes(link);
        return link;
    }

    /// <summary>
    /// Updates the two bytes an <c>OS</c> read returns from the model's current state. The
    /// external reference is reported as selected: this bench feeds the DUT from the Z3805A.
    /// </summary>
    public void RefreshStatusBytes(SimulatedInstrumentLink link)
    {
        ArgumentNullException.ThrowIfNull(link);

        var extended = StatusByte2.ExternalFreqRefSelected;
        if (IsUnleveled) extended |= StatusByte2.RfUnleveled;

        link.NextBytes =
        [
            (byte)(StatusByte1.RfSettled | StatusByte1.EndOfSweep),
            (byte)extended,
        ];
    }

    private int _applied;

    private string Respond(string command, SimulatedInstrumentLink link)
    {
        var c = command.TrimStart();

        ApplyWrites(link);

        // OI is the identification code; Table 3-2 specifies 19 ASCII characters.
        if (c.StartsWith("OI", StringComparison.OrdinalIgnoreCase))
            return "HP8340B SIMULATED  "[..19];

        return string.Empty;
    }

    /// <summary>
    /// Mirrors frequency and level settings into the shared bench state, so another simulated
    /// instrument sees what this one was set to. Without it a wiring self-check comparing two
    /// instruments would be comparing a request against a constant.
    /// </summary>
    private void ApplyWrites(SimulatedInstrumentLink link)
    {
        if (Bench is null) return;

        for (; _applied < link.History.Count; _applied++)
        {
            var c = link.History[_applied].Trim();

            if (c.StartsWith("CW", StringComparison.OrdinalIgnoreCase)
                && c.EndsWith("GZ", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(c[2..^2], System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out var ghz))
            {
                Bench.CwHz = ghz * 1e9;
                CwGHz = ghz;
                continue;
            }

            if (c.StartsWith("PL", StringComparison.OrdinalIgnoreCase)
                && c.EndsWith("DB", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(c[2..^2], System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out var dbm))
            {
                Bench.LevelDbm = dbm;
                RequestedDbm = dbm;
            }
        }
    }
}
