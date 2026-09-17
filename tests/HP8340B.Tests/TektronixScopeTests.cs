using System.Text;
using HP8340B.Instruments;
using HP8340B.Instruments.Visa;
using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// M0-26 and M0-27, the TDS3014B and DPO3034 drivers.
///
/// <para>Every command asserted here came out of the instrument's own programmer manual. These
/// tests pin them, because the two scopes are both Tektronix and are <b>not</b> command
/// compatible - a driver written by analogy with the other would fail in ways that look like a
/// dead instrument rather than like a wrong command.</para>
/// </summary>
public class TektronixScopeTests
{
    /// <summary>A link that answers preamble queries plausibly and returns a CURVe block.</summary>
    private static SimulatedInstrumentLink Link(int points = 8)
    {
        var link = new SimulatedInstrumentLink("SIM::tek::INSTR", command =>
        {
            if (command.Contains("NR_Pt", StringComparison.OrdinalIgnoreCase)) return points.ToString();
            if (command.Contains("YMUlt", StringComparison.OrdinalIgnoreCase)) return "0.01";
            if (command.Contains("YOFf", StringComparison.OrdinalIgnoreCase)) return "10";
            if (command.Contains("YZEro", StringComparison.OrdinalIgnoreCase)) return "-0.2";
            if (command.Contains("XINcr", StringComparison.OrdinalIgnoreCase)) return "1e-6";
            if (command.Contains("XZEro", StringComparison.OrdinalIgnoreCase)) return "0";
            if (command.Contains("PT_Off", StringComparison.OrdinalIgnoreCase)) return "2";
            if (command.Contains("PRObe", StringComparison.OrdinalIgnoreCase)) return "0.1";
            if (command.Contains("IMPedance", StringComparison.OrdinalIgnoreCase)) return "MEG";
            if (command.Contains("TERmination", StringComparison.OrdinalIgnoreCase)) return "1.0E+6";
            if (command.Contains("*IDN", StringComparison.OrdinalIgnoreCase)) return "TEKTRONIX,SIM";
            return string.Empty;
        });

        // CURVe? answers with a definite-length block of signed bytes.
        var body = new byte[points];
        for (var i = 0; i < points; i++) body[i] = unchecked((byte)(sbyte)(i - 4));

        var header = Encoding.ASCII.GetBytes($"#{points.ToString().Length}{points}");
        link.NextBytes = [.. header, .. body];

        return link;
    }

    // --- The two are not command compatible -------------------------------------------------------

    [Fact]
    public void TheTdsUsesImpedanceAndTheDpoUsesTermination()
    {
        var tdsLink = Link();
        new Tds3014B(tdsLink).SetInputImpedance(1, ScopeInputImpedance.FiftyOhm);
        Assert.Contains("CH1:IMPedance FIFty", tdsLink.History);

        var dpoLink = Link();
        new Dpo3034(dpoLink).SetInputImpedance(1, ScopeInputImpedance.FiftyOhm);
        Assert.Contains("CH1:TERmination FIFty", dpoLink.History);
    }

    [Fact]
    public void TheTdsPutsXyInDisplayFormatAndTheDpoHasItsOwnCommand()
    {
        var tdsLink = Link();
        new Tds3014B(tdsLink).SetTimebaseMode(TimebaseMode.XY);
        Assert.Contains("DISplay:FORMat XY", tdsLink.History);

        var dpoLink = Link();
        new Dpo3034(dpoLink).SetTimebaseMode(TimebaseMode.XY);
        Assert.Contains("DISplay:XY TRIGgered", dpoLink.History);
    }

    [Fact]
    public void TheTdsKeepsMainInItsHorizontalScaleAndTheDpoDoesNot()
    {
        var tdsLink = Link();
        new Tds3014B(tdsLink).SetTimebaseScale(1e-3);
        Assert.Contains("HORizontal:MAIn:SCAle 0.001", tdsLink.History);

        var dpoLink = Link();
        new Dpo3034(dpoLink).SetTimebaseScale(1e-3);
        Assert.Contains("HORizontal:SCAle 0.001", dpoLink.History);
    }

    [Fact]
    public void TheTwoFamiliesUseDifferentPreambleRoots()
    {
        var tdsLink = Link();
        new Tds3014B(tdsLink).ReadWaveform(1);
        Assert.Contains(tdsLink.History, c => c.StartsWith("WFMPre:", StringComparison.Ordinal));

        var dpoLink = Link();
        new Dpo3034(dpoLink).ReadWaveform(1);
        Assert.Contains(dpoLink.History, c => c.StartsWith("WFMOutpre:", StringComparison.Ordinal));
    }

    // --- The trigger restriction -------------------------------------------------------------------

    [Fact]
    public void TheTdsRefusesAnExternalTriggerAndSaysWhatToDoInstead()
    {
        // Neither EXT nor EXT10 is available on 4-channel TDS3000 Series instruments, and the
        // 3014B is four-channel. Silently accepting would leave the scope triggering on something
        // nobody chose.
        var scope = new Tds3014B(Link());

        var ex = Assert.Throws<NotSupportedException>(() => scope.SetExternalTrigger(1.0));

        Assert.Contains("costs a channel", ex.Message);
        Assert.False(scope.Capability.HasExternalTrigger);
    }

    [Fact]
    public void TheTdsCanTriggerOnAChannelInstead()
    {
        var link = Link();
        new Tds3014B(link).SetChannelTrigger(4, 1.0);

        Assert.Contains("TRIGger:A:EDGe:SOUrce CH4", link.History);
        Assert.Contains("TRIGger:A:EDGe:SLOpe RISe", link.History);
        Assert.Contains("TRIGger:A:LEVel 1", link.History);
    }

    [Fact]
    public void TheDpoTriggersFromItsAuxInput()
    {
        var link = Link();
        new Dpo3034(link).SetExternalTrigger(1.0);

        Assert.Contains("TRIGger:A:EDGe:SOUrce AUX", link.History);
    }

    // --- Scaling ------------------------------------------------------------------------------------

    [Fact]
    public void SamplesAreScaledWithTheTektronixConventionNotTheRigolsOne()
    {
        // Tek: volts = YZEro + YMUlt x (raw - YOFf). The Rigol's is
        // (raw - yorigin - yreference) x yincrement. A driver written by analogy would be wrong by
        // an offset -- and an offset error on a detector trace looks like a power error.
        var wave = new Tds3014B(Link(points: 8)).ReadWaveform(1);

        // Raw sample i is (i - 4) as a signed byte, so sample 0 is -4.
        Assert.Equal(-0.2 + 0.01 * (-4 - 10), wave.Volts[0], 9);
        Assert.Equal(8, wave.Volts.Count);
    }

    [Fact]
    public void TheTriggerPointOffsetMovesTheTimeAxis()
    {
        // PT_Off is how far into the record the trigger sits, so the first sample is that far
        // before it. Dropping the term slides the whole trace.
        var wave = new Tds3014B(Link(points: 8)).ReadWaveform(1);

        Assert.Equal(-2e-6, wave.StartSeconds, 12);
        Assert.Equal(1e-6, wave.SecondsPerSample, 12);
    }

    [Fact]
    public void TransferIsOneSignedBytePerSample()
    {
        // Width 1 makes byte order irrelevant, which removes the commonest way to get a binary
        // waveform transfer subtly and plausibly wrong.
        var link = Link();
        new Tds3014B(link).ReadWaveform(1);

        Assert.Contains("DATa:ENCdg RIBinary", link.History);
        Assert.Contains("DATa:WIDth 1", link.History);
    }

    [Fact]
    public void StreamingAFrameReusesThePreamble()
    {
        var link = Link();
        var scope = new Dpo3034(link);

        scope.ReadWaveformStreaming(1);
        var warm = link.Transactions.Count;
        scope.ReadWaveformStreaming(1);

        // CURVe? plus its binary read.
        Assert.Equal(2, link.Transactions.Count - warm);
    }

    [Fact]
    public void ChangingTheVerticalScaleDiscardsTheCachedPreamble()
    {
        var link = Link();
        var scope = new Dpo3034(link);

        scope.ReadWaveformStreaming(1);
        scope.SetChannelScale(1, 0.05);

        var before = link.History.Count;
        scope.ReadWaveformStreaming(1);

        Assert.Contains(link.History.Skip(before), c => c.Contains("WFMOutpre:"));
    }

    // --- Capability guards ---------------------------------------------------------------------------

    [Fact]
    public void AskingForFinerThanTheScopeCanDoIsRefused()
    {
        // The instrument would accept it and quietly coerce, so the reading would not be at the
        // sensitivity the procedure called for.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new Tds3014B(Link()).SetChannelScale(1, 1e-4));
    }

    [Fact]
    public void TheProbeRatioIsVerifiedRatherThanSetBecauseTekProbeDetectsIt()
    {
        // The simulated link reports 0.1, a 10:1 probe. Asking for 10:1 agrees; asking for 1:1
        // must fail loudly, because accepting it would scale every reading by ten.
        var scope = new Tds3014B(Link());

        scope.SetProbeRatio(1, 10);

        var ex = Assert.Throws<InvalidOperationException>(() => scope.SetProbeRatio(1, 1));
        Assert.Contains("detect the probe", ex.Message);
    }

    [Fact]
    public void TheTdsDeratesItsBandwidthAtOneMillivoltPerDivision()
    {
        // 100 MHz from 2 mV/div up, 90 MHz at 1 mV/div. Never bites on this bench, but the figure
        // belongs in the driver rather than in somebody's memory of a footnote.
        var capability = new Tds3014B(Link()).Capability;

        Assert.Equal(90e6, capability.BandwidthAt(1e-3));
        Assert.Equal(100e6, capability.BandwidthAt(5e-3));
    }

    [Fact]
    public void ChannelNumbersAreRangeChecked()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Dpo3034(Link()).SetChannelScale(5, 1.0));
    }

    // --- Roles ------------------------------------------------------------------------------------

    [Fact]
    public void ATektronixScopeWorksEndToEndThroughTheSimulatedBench()
    {
        // Rule 4: a substituted bench still has to pass with no hardware. Built through the real
        // factory rather than by hand, so a model string that reaches the wrong driver is caught.
        var config = new HP8340B.Instruments.Config.InstrumentConfig
        {
            Role = "testpoint-scope",
            Model = "Tektronix TDS3014B",
            Address = "TCPIP0::192.168.1.200::inst0::INSTR",
        };

        using var instrument = SimulatedBench.Create(config);

        var scope = Assert.IsType<Tds3014B>(instrument);
        var wave = scope.ReadWaveform(1);

        Assert.Equal(600, wave.Volts.Count);
        Assert.All(wave.Volts, v => Assert.True(v < 0, "Detector output should be negative-going."));
        Assert.True(wave.Volts.Max() - wave.Volts.Min() > 0.1, "Trace should have real shape.");
    }

    [Fact]
    public void TheTdsFitsTheTestPointRoleExactlyAndTheDpoFitsTheTuningRole()
    {
        var tds = new Tds3014B(Link()).Capability;
        var dpo = new Dpo3034(Link()).Capability;

        // Three test points plus a channel for the trigger is exactly four.
        Assert.True(ScopeRole.TestPoint.CanBeFilledBy(tds));
        Assert.Equal(4, ScopeRole.TestPoint.ChannelsNeededOn(tds));

        Assert.True(ScopeRole.Tuning.CanBeFilledBy(dpo));

        // Both have 50 ohm and clear the 70 MHz bar, so both could serve S7 on capability alone.
        Assert.True(ScopeRole.Fast.CanBeFilledBy(tds));
        Assert.True(ScopeRole.Fast.CanBeFilledBy(dpo));
    }
}
