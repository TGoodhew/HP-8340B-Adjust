using HP8340B.Instruments;
using HP8340B.Instruments.Config;
using HP8340B.Instruments.Model;
using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// M0-12: the 8340B model that misbehaves the way the real instrument does.
///
/// <para>These tests matter more than most. A squegging scanner that finds nothing looks exactly
/// like a healthy instrument, so the scanners in M1-04, M1-05, M1-06 and M2-07 can only be
/// trusted if there is something offline that genuinely squegs for them to find. Each behaviour
/// below is therefore asserted twice: absent when the model is adjusted, present when it is
/// not.</para>
/// </summary>
public class SimulatedSweeperTests
{
    private const double Band2Low = 8.0;     // X2A's half of band 2
    private const double Band2High = 12.0;   // X2B's half

    // --- The power curve ------------------------------------------------------------------------

    [Theory]
    [InlineData(0.05)]
    [InlineData(1.0)]
    [InlineData(5.0)]
    [InlineData(8.0)]
    [InlineData(12.0)]
    [InlineData(17.0)]
    [InlineData(22.0)]
    [InlineData(25.0)]
    public void AvailablePowerClearsTheTableFourNineLimitAcrossTheRange(double ghz)
    {
        // The model is built up FROM the Table 4-9 limit rather than tabulated separately, so it
        // cannot drift away from the specification it is later judged against.
        var model = new SimulatedSweeper();

        Assert.True(
            model.MaxAvailablePowerDbm(ghz) > MaxLeveledPower.ForFrequency(model.Option, ghz),
            $"Unleveled power at {ghz} GHz should exceed the leveled limit.");
    }

    [Theory]
    [InlineData(InstrumentOption.Standard)]
    [InlineData(InstrumentOption.Opt001)]
    [InlineData(InstrumentOption.Opt004)]
    [InlineData(InstrumentOption.Opt005)]
    public void ThePowerCurveFollowsTheConfiguredOption(InstrumentOption option)
    {
        // Opt001 has 2 dB more in band 2 than Standard; the model has to move with it rather than
        // carrying one hard-coded curve.
        var model = new SimulatedSweeper { Option = option };

        var expected = MaxLeveledPower.ForFrequency(option, Band2Low);
        var available = model.MaxAvailablePowerDbm(Band2Low);

        Assert.InRange(available - expected, 0.5, 5.0);
    }

    [Fact]
    public void TheEnvelopeHasRippleRatherThanBeingFlat()
    {
        // A flat envelope would let a broken minimum-finder or peak-finder pass. The ripple is a
        // function of frequency, so the same sweep always returns the same trace.
        var model = new SimulatedSweeper();
        var trace = model.SweptEnvelope(7.0, 13.4, requestedDbm: 25);
        var again = model.SweptEnvelope(7.0, 13.4, requestedDbm: 25);

        Assert.True(trace.Max(p => p.Dbm) - trace.Min(p => p.Dbm) > 0.5, "Envelope should have shape.");
        Assert.Equal(trace.Select(p => p.Dbm), again.Select(p => p.Dbm));
    }

    [Theory]
    [InlineData(BandId.Band2)]
    [InlineData(BandId.Band3)]
    [InlineData(BandId.Band4)]
    public void AWellSetBiasMeetsThe514MinimumPowerCriterion(BandId band) =>
        Assert.True(new SimulatedSweeper().MeetsMinimumPower(band));

    [Theory]
    [InlineData(BandId.Band2)]
    [InlineData(BandId.Band3)]
    [InlineData(BandId.Band4)]
    public void ABiasLeftFullyCounterClockwiseFailsIt(BandId band)
    {
        // The other half of the test: the criterion has to be capable of failing, or passing it
        // proves nothing. Fully CCW is the pot preset of 5-14 step 4.
        var model = new SimulatedSweeper();
        foreach (var pot in model.UnleveledBias.Values) pot.Bias = 0;

        Assert.False(model.MeetsMinimumPower(band));
    }

    [Theory]
    [InlineData(BandId.Band0)]
    [InlineData(BandId.Band1)]
    public void TheMinimumPowerCheckRefusesTheBandsTheManualDoesNotApplyItTo(BandId band)
    {
        // Steps 15, 22 and 30 are the multiplying bands. Band 1's envelope cannot be read at
        // maximum unleveled power at all, because it squegs there by design.
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new SimulatedSweeper().MeetsMinimumPower(band));

        Assert.Contains("multiplying bands", ex.Message);
    }

    // --- SRD bias and squegging ------------------------------------------------------------------

    [Fact]
    public void PowerRisesWithBiasRightUpToTheSqueggingThreshold()
    {
        // 5-14 steps 11-14: clockwise increases bias and moves toward squegging, and the
        // adjustment is to find maximum power then back off 0.5 dB. Peak and onset are the same
        // place, which is exactly why the procedure has to overshoot and return.
        var model = new SimulatedSweeper();
        var pot = model.UnleveledPotAt(Band2Low)!;

        var readings = new List<double>();
        for (var bias = 0.0; bias <= pot.SquegThreshold; bias += 0.05)
        {
            pot.Bias = bias;
            readings.Add(model.MaxAvailablePowerDbm(Band2Low));
        }

        Assert.Equal(readings.OrderBy(v => v), readings);
        Assert.True(readings[^1] - readings[0] > 3.0, "Bias should be worth real power.");
    }

    [Fact]
    public void BackingOffHalfADbFromThePeakLeavesTheSqueggingRegion()
    {
        // The manual's actual instruction. If this were not achievable the whole adjustment would
        // be impossible, so it is worth asserting rather than assuming.
        var model = new SimulatedSweeper();
        var pot = model.UnleveledPotAt(Band2Low)!;

        pot.Bias = pot.SquegThreshold;
        var peak = model.MaxAvailablePowerDbm(Band2Low);
        Assert.False(pot.Squegging, "At the threshold itself it is not yet squegging.");

        // Walk back until 0.5 dB down.
        while (model.MaxAvailablePowerDbm(Band2Low) > peak - 0.5) pot.Bias -= 0.005;

        Assert.False(pot.Squegging);
        Assert.False(model.IsSquegging(Band2Low, requestedDbm: 20));
    }

    [Fact]
    public void PastTheThresholdTheOutputReversesAgainstTheRequest()
    {
        // "Power reversal": actual output falls as requested output rises. This is the symptom
        // the monotonicity test (M1-06) exists to find.
        var model = new SimulatedSweeper();
        model.UnleveledPotAt(Band2Low)!.Bias = 0.95;

        var low = model.OutputPowerDbm(Band2Low, requestedDbm: 14);
        var high = model.OutputPowerDbm(Band2Low, requestedDbm: 20);

        Assert.True(high < low,
            $"Asking for 20 dBm gave {high:0.0} dBm, asking for 14 gave {low:0.0} — expected reversal.");
    }

    [Fact]
    public void AWellAdjustedInstrumentIsMonotonic()
    {
        var model = new SimulatedSweeper();

        var outputs = Enumerable.Range(0, 15)
            .Select(i => model.OutputPowerDbm(Band2Low, requestedDbm: i))
            .ToList();

        Assert.Equal(outputs.OrderBy(v => v), outputs);
    }

    [Fact]
    public void SqueggingPutsAHoleInTheSweptEnvelope()
    {
        var model = new SimulatedSweeper();
        var clean = model.SweptEnvelope(7.0, 10.0, requestedDbm: 20);

        model.UnleveledPotAt(Band2Low)!.Bias = 0.95;
        var squegging = model.SweptEnvelope(7.0, 10.0, requestedDbm: 20);

        var cleanRange = clean.Max(p => p.Dbm) - clean.Min(p => p.Dbm);
        var squeggingRange = squegging.Max(p => p.Dbm) - squegging.Min(p => p.Dbm);

        Assert.True(squeggingRange > cleanRange + 3.0, "The hole should dominate the envelope.");

        // And it is where the model says it is, so a scanner can be checked on position and not
        // just on "something was wrong somewhere".
        var deepest = squegging.MinBy(p => p.Dbm)!;
        Assert.Equal(SimulatedSweeper.HoleCentreGHz(Band2Low), deepest.Ghz, 1);
    }

    [Fact]
    public void SqueggingProducesSpursOffsetFromTheCarrier()
    {
        // 5-14 steps 70-80 look at the carrier plus or minus 300 MHz for exactly this.
        var model = new SimulatedSweeper();
        Assert.Empty(model.SpursAt(Band2Low, requestedDbm: 20));

        model.UnleveledPotAt(Band2Low)!.Bias = 0.95;
        var spurs = model.SpursAt(Band2Low, requestedDbm: 20);

        Assert.NotEmpty(spurs);
        Assert.All(spurs, s => Assert.True(Math.Abs(s.OffsetHz) <= 300e6,
            "Spurs should fall inside the span the procedure actually looks at."));
        Assert.All(spurs, s => Assert.True(s.Dbc < 0, "A spur is below the carrier."));

        // Worse bias, worse spur — so a scanner can be tested on severity, not only presence.
        var mild = spurs.Max(s => s.Dbc);
        model.UnleveledPotAt(Band2Low)!.Bias = 1.0;
        Assert.True(model.SpursAt(Band2Low, 20).Max(s => s.Dbc) > mild);
    }

    [Fact]
    public void SqueggingNeedsDriveAsWellAsBias()
    {
        // A mis-set pot is quiet at low requested power, which is why 5-14's squegging test asks
        // for +20 dBm and 5-16's walks the level back down in 5 dB steps.
        var model = new SimulatedSweeper();
        model.UnleveledPotAt(Band2Low)!.Bias = 0.95;

        Assert.False(model.IsSquegging(Band2Low, requestedDbm: -20));
        Assert.True(model.IsSquegging(Band2Low, requestedDbm: 20));
    }

    [Fact]
    public void EachPotOwnsOnlyItsOwnHalfBand()
    {
        // Getting this wrong would send somebody to the wrong pot. The halves are the ones
        // Bands.UnleveledPotFor defines: band 2 splits at 10 GHz.
        var model = new SimulatedSweeper();
        model.UnleveledPotAt(Band2Low)!.Bias = 0.95;

        Assert.True(model.IsSquegging(Band2Low, 20));
        Assert.False(model.IsSquegging(Band2High, 20));
    }

    [Theory]
    [InlineData(8.0, "X2A (A24R3)")]
    [InlineData(12.0, "X2B (A24R4)")]
    [InlineData(14.0, "X3A (A24R6)")]
    [InlineData(17.0, "X3B (A24R7)")]
    [InlineData(21.0, "X4A (A24R9)")]
    [InlineData(25.0, "X4B (A24R10)")]
    public void TheModelUsesTheSamePotMappingAsTheProcedure(double ghz, string expected) =>
        Assert.Equal(expected, new SimulatedSweeper().UnleveledPotAt(ghz)!.Name);

    // --- Band 1 ------------------------------------------------------------------------------------

    [Fact]
    public void BandOneSquegsOnlyAtMaximumUnleveledPower()
    {
        // The manual: band 1 squegging is a function of SYTM input power and occurs only when
        // maximum unleveled power is requested.
        var model = new SimulatedSweeper();
        var maximum = model.MaxAvailablePowerDbm(5.0);

        Assert.True(model.IsSquegging(5.0, maximum));
        Assert.False(model.IsSquegging(5.0, maximum - 3));
        Assert.False(model.IsSquegging(5.0, -50));   // 5-14 step 53 runs band 1 here for that reason
    }

    [Fact]
    public void BandOneSqueggingCannotBeTunedOut()
    {
        // There is no A24 pot for band 1, and a scanner that reports it as an adjustable fault is
        // wrong about the instrument.
        var model = new SimulatedSweeper();

        Assert.Null(model.UnleveledPotAt(5.0));
        Assert.Null(Bands.UnleveledPotFor(5.0));

        // Nothing that can be adjusted changes it.
        foreach (var pot in model.UnleveledBias.Values) pot.Bias = 0;
        Assert.True(model.IsSquegging(5.0, model.MaxAvailablePowerDbm(5.0)));
    }

    [Fact]
    public void BandZeroDoesNotSquegAtAll()
    {
        // Band 0 is heterodyne — the YO mixed with the A8 oscillator — so there is no SRD and no
        // multiplication to squeg.
        var model = new SimulatedSweeper();

        Assert.Null(model.UnleveledPotAt(1.0));
        Assert.False(model.IsSquegging(1.0, 20));
    }

    // --- Delay compensation --------------------------------------------------------------------------

    [Theory]
    [InlineData(BandId.Band0)]
    [InlineData(BandId.Band1)]
    [InlineData(BandId.Band2)]
    [InlineData(BandId.Band3)]
    [InlineData(BandId.Band4)]
    public void AtOptimumTheFastAndSingleTracesAreInsideTheManualsLimit(BandId band)
    {
        var model = new SimulatedSweeper();
        var limit = SimulatedSweeper.DelayDeviationLimitDb(band);

        Assert.True(model.WorstDelayDeviationDb(band, SweepSpeed.Auto) < limit);
        Assert.True(model.WorstDelayDeviationDb(band, SweepSpeed.Single) < limit);
    }

    [Theory]
    [InlineData(BandId.Band2)]
    [InlineData(BandId.Band3)]
    [InlineData(BandId.Band4)]
    public void AConstantAtTheWrongEndOfItsRangeBreaksTheLimit(BandId band)
    {
        var model = new SimulatedSweeper();
        var compensation = model.Delay[band];

        // 0-131 is the delay-compensation range (5-14 step 2). Pick whichever end is further
        // from this band's optimum.
        compensation.Value = compensation.Optimum < 66 ? 131 : 0;

        Assert.True(model.WorstDelayDeviationDb(band, SweepSpeed.Auto)
                    > SimulatedSweeper.DelayDeviationLimitDb(band));
    }

    [Fact]
    public void TheFastSweepNeverExactlyMatchesTheSlowOne()
    {
        // Even at optimum there is an irreducible loss. Without it the two traces would be
        // bit-identical and a comparator that did nothing at all would pass its own test.
        var model = new SimulatedSweeper();

        Assert.True(model.WorstDelayDeviationDb(BandId.Band2, SweepSpeed.Auto) > 0);
        Assert.Equal(0, model.WorstDelayDeviationDb(BandId.Band2, SweepSpeed.Slow));
    }

    [Fact]
    public void ASingleSweepIsWorseThanAutoWhichIsWorseThanSlow()
    {
        var model = new SimulatedSweeper();
        model.Delay[BandId.Band2].Value = 0;

        var slow = model.OutputPowerDbm(7.2, 25, SweepSpeed.Slow);
        var auto = model.OutputPowerDbm(7.2, 25, SweepSpeed.Auto);
        var single = model.OutputPowerDbm(7.2, 25, SweepSpeed.Single);

        Assert.True(auto < slow);
        Assert.True(single < auto);
    }

    [Fact]
    public void TheFastSweepLossIsWorstAtTheStartOfTheBand()
    {
        // The dropout 5-14 step 45 uses CC75 to remove is at the start of the sweep.
        var model = new SimulatedSweeper();
        model.Delay[BandId.Band2].Value = 0;

        var atStart = model.DelayPenaltyDb(7.1, SweepSpeed.Auto);
        var atEnd = model.DelayPenaltyDb(13.4, SweepSpeed.Auto);

        Assert.True(atStart > atEnd * 3, "The loss should be concentrated at the band start.");
    }

    [Fact]
    public void BandOneIsJudgedAgainstATwoDbLimitNotOne()
    {
        Assert.Equal(2.0, SimulatedSweeper.DelayDeviationLimitDb(BandId.Band1));
        Assert.Equal(1.0, SimulatedSweeper.DelayDeviationLimitDb(BandId.Band3));
    }

    // --- Half-band geometry ---------------------------------------------------------------------------

    [Theory]
    [InlineData(8.0, 7.0, 10.0)]
    [InlineData(12.0, 10.0, 13.5)]
    [InlineData(14.0, 13.5, 15.0)]
    [InlineData(17.0, 15.0, 20.0)]
    [InlineData(21.0, 20.0, 23.0)]
    [InlineData(25.0, 23.0, 26.5)]
    public void HalfBandsMatchTheSplitsTheProcedureUses(double ghz, double low, double high)
    {
        var (actualLow, actualHigh) = SimulatedSweeper.HalfBand(ghz);

        Assert.Equal(low, actualLow);
        Assert.Equal(high, actualHigh);
    }

    [Fact]
    public void BandsWithNoSplitReturnTheWholeBand()
    {
        var (low, high) = SimulatedSweeper.HalfBand(5.0);

        Assert.Equal(2.3, low);
        Assert.Equal(7.0, high);
    }

    [Fact]
    public void FrequenciesOutsideTheInstrumentAreRefused()
    {
        var model = new SimulatedSweeper();

        Assert.Throws<ArgumentOutOfRangeException>(() => model.IsSquegging(30.0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SimulatedSweeper.HalfBand(0.001));
    }

    // --- The bus side ------------------------------------------------------------------------------------

    [Fact]
    public void TheUnleveledBitFollowsTheModelRatherThanBeingHardCoded()
    {
        // This is how M1-05 finds maximum leveled power over the bus instead of by watching the
        // front panel, so the bit has to move with the requested level.
        var model = new SimulatedSweeper { CwGHz = 8.0 };
        var link = model.CreateLink();
        var dut = new Hp8340B(link);

        model.RequestedDbm = 0;
        model.RefreshStatusBytes(link);
        Assert.False(dut.IsUnleveled());

        // +15 dBm at 8 GHz is above the Standard option's +10 dBm leveled limit.
        model.RequestedDbm = 15;
        model.RefreshStatusBytes(link);
        Assert.True(dut.IsUnleveled());
    }

    [Fact]
    public void TheModelledDutStillAnswersItsIdentificationCode()
    {
        var model = new SimulatedSweeper();
        var dut = new Hp8340B(model.CreateLink());

        Assert.Contains("8340B", dut.ReadIdentity());
    }

    [Fact]
    public void TheModelledDutReportsTheExternalReference()
    {
        // The DUT is on the Z3805A, and probe --sim should exercise the same path a bench run
        // does rather than a link that always says yes.
        var model = new SimulatedSweeper();
        var dut = new Hp8340B(model.CreateLink());

        Assert.True(dut.IsExternalReferenceSelected());
    }

    [Fact]
    public void TheSimulatedBenchBuildsTheDutFromTheModel()
    {
        var config = new InstrumentConfig { Role = "dut", Model = "HP 8340B", Address = "GPIB0::19::INSTR" };

        using var instrument = InstrumentFactory.Create(config, simulate: true);
        var result = instrument.Probe();

        Assert.IsType<Hp8340B>(instrument);
        Assert.True(result.Responded);
        Assert.True(result.ExternalReference);
    }
}
