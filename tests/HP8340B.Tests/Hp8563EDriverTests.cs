using HP8340B.Instruments;
using HP8340B.Instruments.Visa;
using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// M0-05: the 8563E driver. 8560-series command language, not SCPI. Every code cited in
/// Hp8563ECommands from the 8560 E-Series Programming Guide.
/// </summary>
public class Hp8563EDriverTests
{
    private static (Hp8563E Sa, SimulatedInstrumentLink Link, SimulatedAnalyzer Model) NewSa()
    {
        var model = new SimulatedAnalyzer();
        var link = model.CreateLink();
        return (new Hp8563E(link), link, model);
    }

    [Fact]
    public void FrequencyAndBandwidthUseThe8560Mnemonics()
    {
        var (sa, link, _) = NewSa();

        sa.SetCenterSpanHz(10e9, 600e6);
        sa.SetResolutionBandwidthHz(300e3);
        sa.SetVideoBandwidthHz(100e3);

        Assert.Equal("CF 10000000000 HZ", link.History[0]);
        Assert.Equal("SP 600000000 HZ", link.History[1]);
        Assert.Equal("RB 300000 HZ", link.History[2]);
        Assert.Equal("VB 100000 HZ", link.History[3]);
    }

    [Fact]
    public void StartStopRefusesAnInvertedSpan()
    {
        var (sa, _, _) = NewSa();
        Assert.Throws<ArgumentException>(() => sa.SetStartStopHz(20e9, 10e9));
    }

    [Theory]
    [InlineData(AnalyzerDetector.Positive, "DET POS")]
    [InlineData(AnalyzerDetector.Negative, "DET NEG")]
    [InlineData(AnalyzerDetector.Normal, "DET NRM")]
    [InlineData(AnalyzerDetector.Sample, "DET SMP")]
    public void DetectorModesMatchTheGuide(AnalyzerDetector detector, string expected)
    {
        // 8560E Programming Guide p. 453: parameters NEG, NRM, POS, SMP.
        var (sa, link, _) = NewSa();
        sa.SetDetector(detector);
        Assert.Equal(expected, link.History[^1]);
    }

    [Fact]
    public void MaxHoldAndClearWriteNameTheTrace()
    {
        // The guide's own example: "BLANK TRA;CLRW TRB;MXMH TRB;" — these commands take a trace.
        var (sa, link, _) = NewSa();
        sa.ClearWrite();
        sa.MaxHold();

        Assert.Equal("CLRW TRA", link.History[0]);
        Assert.Equal("MXMH TRA", link.History[1]);
    }

    [Fact]
    public void TraceIsReadInParameterUnitsSoItIsAlreadyDbm()
    {
        // TDF P returns real values in the fundamental units of the guide's Table 5-1 — dBm for
        // amplitude. Without it the analyzer returns measurement units that would need scaling.
        var (sa, link, _) = NewSa();
        var trace = sa.ReadTrace(9.7e9, 10.3e9, "test");

        Assert.Contains("TDF P", link.History);
        Assert.Equal("TRA?", link.History[^1]);
        Assert.Equal(Hp8563E.TracePoints, trace.Amplitudes.Count);
    }

    [Fact]
    public void TraceCarriesItsFrequencyAxisAndFindsItsExtremes()
    {
        var (sa, _, model) = NewSa();
        model.CarrierDbm = -5.0;

        var trace = sa.ReadTrace(9.7e9, 10.3e9, "test");

        Assert.Equal(9.7e9, trace.FrequencyAt(0));
        Assert.Equal(10.3e9, trace.FrequencyAt(Hp8563E.TracePoints - 1));

        // The carrier sits at the centre of the simulated span.
        var (peakHz, peakDbm) = trace.Maximum();
        Assert.Equal(10.0e9, peakHz, 0);
        Assert.Equal(-5.0, peakDbm, 1);

        // And the minimum is down in the noise, not equal to the peak — a flat trace would let a
        // broken minimum-finder pass.
        var (_, minDbm) = trace.Minimum();
        Assert.True(minDbm < peakDbm - 50);
    }

    [Fact]
    public void AnInjectedSpurShowsUpInTheTrace()
    {
        // This is what the squegging scanners look for, so it has to be reproducible offline.
        var (sa, _, model) = NewSa();
        model.CarrierDbm = 0.0;
        model.Spurs.Add((OffsetPoints: 120, Dbm: -30.0));

        var trace = sa.ReadTrace(9.7e9, 10.3e9, "test");
        var spur = trace.Amplitudes[Hp8563E.TracePoints / 2 + 120];

        Assert.Equal(-30.0, spur, 1);
    }

    [Fact]
    public void PadIsAddedBackSoReadingsAreAtTheDutNotTheAnalyzer()
    {
        var (sa, _, model) = NewSa();
        model.CarrierDbm = -10.0;
        sa.PadDb = 20.0;

        var trace = sa.ReadTrace(9.7e9, 10.3e9, "test");
        var (_, peak) = trace.Maximum();

        // -10 dBm at the analyzer through a 20 dB pad is +10 dBm at the DUT.
        Assert.Equal(10.0, peak, 1);
    }

    [Fact]
    public void ZeroDbAttenuationIsRefusedWithACarrierPresent()
    {
        // Repo rule 5 and the S1 hook-up card: never 0 dB with a carrier this size. The squegging
        // scans drive the DUT to +20 dBm, far above this.
        var (sa, _, _) = NewSa();

        var ex = Assert.Throws<InvalidOperationException>(() => sa.SetAttenuationDb(0, 10.0));
        Assert.Contains("rule 5", ex.Message);

        // Attenuation is fine once there is some, and 0 dB is allowed when nothing is present.
        sa.SetAttenuationDb(10, 10.0);
        sa.SetAttenuationDb(0, -40.0);
    }

    [Fact]
    public void InputLevelCheckAccountsForTheDeclaredPad()
    {
        var (sa, _, _) = NewSa();

        // +20 dBm unleveled, as 5-14 steps 70-80 request, with no pad: unsafe.
        Assert.NotNull(sa.CheckInputLevel(20.0));
        Assert.Contains("Add or declare a pad", sa.CheckInputLevel(20.0));

        // The same level through a 20 dB pad lands at 0 dBm: fine.
        sa.PadDb = 20.0;
        Assert.Null(sa.CheckInputLevel(20.0));

        // 0 dBm is safe either way.
        sa.PadDb = 0;
        Assert.Null(sa.CheckInputLevel(0.0));
    }

    [Fact]
    public void SpurScanPresetMatchesTheManualsSettings()
    {
        // 5-14 steps 70-80: carrier +/- 300 MHz, RBW 300 kHz, VBW 100 kHz, positive peak.
        var (sa, link, _) = NewSa();
        sa.SpurScanPreset(10e9);

        Assert.Contains("SP 600000000 HZ", link.History);
        Assert.Contains("RB 300000 HZ", link.History);
        Assert.Contains("VB 100000 HZ", link.History);
        Assert.Contains("DET POS", link.History);
    }

    [Fact]
    public void EnvelopePresetClearsBeforeAccumulatingMaxHold()
    {
        // Order matters: max hold over a stale accumulation would show the previous band.
        var (sa, link, _) = NewSa();
        sa.EnvelopePreset(7e9, 13.5e9, 10.0);

        var clear = link.History.ToList().IndexOf("CLRW TRA");
        var hold = link.History.ToList().IndexOf("MXMH TRA");

        Assert.True(clear >= 0 && hold >= 0);
        Assert.True(clear < hold, "Clear-write must precede max hold.");
    }

    [Fact]
    public void ZeroSpanPresetUsesWideBandwidthsSoTheEnvelopeSurvives()
    {
        // M1-07 looks for a periodic amplitude collapse. Narrow VBW would filter it away.
        var (sa, link, _) = NewSa();
        sa.ZeroSpanPreset(10e9, TimeSpan.FromMilliseconds(20));

        Assert.Contains("SP 0 HZ", link.History);
        Assert.Contains("RB 3000000 HZ", link.History);
        Assert.Contains("VB 3000000 HZ", link.History);
        Assert.Contains("ST 0.02 S", link.History);
    }

    [Fact]
    public void PeakSearchReturnsFrequencyAndAmplitude()
    {
        var (sa, link, model) = NewSa();
        model.CarrierDbm = -12.0;

        var (_, dbm) = sa.PeakSearch();

        Assert.Contains("MKPK HI", link.History);
        Assert.Equal(-12.0, dbm, 1);
    }

    [Fact]
    public void ProbeIdentifiesTheAnalyzer()
    {
        var (sa, _, _) = NewSa();
        var result = sa.Probe();

        Assert.True(result.Responded);
        Assert.Contains("8563E", result.Identity);
    }

    [Fact]
    public void EveryCodeInTheTableIsCitedAndVerified()
    {
        Assert.Empty(Hp8563ECommands.Unverified);
        Assert.All(Hp8563ECommands.All, c =>
            Assert.Contains("8560 E-Series Programming Guide", c.Source));
    }

    [Fact]
    public void UnknownAnalyzerCodeIsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Hp8563ECommands.Require("ZZZ"));
        Assert.Contains("never fabricate", ex.Message);
    }

    [Fact]
    public void FrequencyReferenceIsReadableSoProbeCanReportARealAnswer()
    {
        // FREF? (guide p. 477). The analyzer's own reference sets the frequency axis that every
        // spur and envelope measurement is judged against, so this is worth confirming rather
        // than assuming from the cabinet.
        var (sa, _, model) = NewSa();
        Assert.True(sa.IsExternalReferenceSelected());
        Assert.True(sa.Probe().ExternalReference);

        model.ExternalReference = false;
        Assert.False(sa.IsExternalReferenceSelected());

        var result = sa.Probe();
        Assert.False(result.ExternalReference);
        // IP presets FREF back to INT, which is the way this gets lost in practice.
        Assert.Contains("IP presets FREF back to INT", result.Detail);
    }

    [Fact]
    public void FrequencyReferenceIsSetWithTheGuidesParameters()
    {
        var (sa, link, _) = NewSa();

        sa.SetFrequencyReference(external: true);
        Assert.Equal("FREF EXT;", link.History[^1]);

        sa.SetFrequencyReference(external: false);
        Assert.Equal("FREF INT;", link.History[^1]);
    }

    [Fact]
    public void AnUnrecognisedReferenceAnswerIsReportedAsUnknownNotGuessed()
    {
        var link = new SimulatedInstrumentLink("SIM::sa::INSTR", _ => "?????");
        var sa = new Hp8563E(link);

        Assert.Null(sa.IsExternalReferenceSelected());
    }
}
