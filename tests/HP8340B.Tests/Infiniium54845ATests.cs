using System.Text;
using HP8340B.Instruments;
using HP8340B.Instruments.Visa;
using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// M0-28, the Agilent 54845A Infiniium driver — the fast-scope for 4-12 rise and fall time.
///
/// <para>Commands came from the Infiniium Programmer's Quick Reference Guide, read off pages
/// rendered to images because its command reference is vector-drawn and does not extract as text.
/// These tests pin them, since anyone re-checking by grepping the PDF will find nothing.</para>
/// </summary>
public class Infiniium54845ATests
{
    private static SimulatedInstrumentLink Link(int points = 8, double yReference = 128)
    {
        var link = new SimulatedInstrumentLink("SIM::inf::INSTR", command =>
        {
            if (command.Contains("POINts", StringComparison.OrdinalIgnoreCase)) return points.ToString();
            if (command.Contains("XINCrement", StringComparison.OrdinalIgnoreCase)) return "1e-9";
            if (command.Contains("XORigin", StringComparison.OrdinalIgnoreCase)) return "0";
            if (command.Contains("XREFerence", StringComparison.OrdinalIgnoreCase)) return "2";
            if (command.Contains("YINCrement", StringComparison.OrdinalIgnoreCase)) return "0.01";
            if (command.Contains("YORigin", StringComparison.OrdinalIgnoreCase)) return "-0.3";
            if (command.Contains("YREFerence", StringComparison.OrdinalIgnoreCase))
                return yReference.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (command.Contains("INPut?", StringComparison.OrdinalIgnoreCase)) return "DC50";
            if (command.Contains("*IDN", StringComparison.OrdinalIgnoreCase)) return "AGILENT,54845A,SIM";
            return string.Empty;
        });

        var body = new byte[points];
        for (var i = 0; i < points; i++) body[i] = (byte)(120 + i);

        var header = Encoding.ASCII.GetBytes($"#{points.ToString().Length}{points}");
        link.NextBytes = [.. header, .. body];

        return link;
    }

    // --- The trap: coupling and termination are one command ----------------------------------------

    [Fact]
    public void SettingCouplingDoesNotSilentlyMoveTheChannelOffFiftyOhms()
    {
        // :CHANnel<N>:INPut sets both together. A driver that mapped SetChannelCoupling straight
        // onto it would knock a 50 ohm channel back to 1 Mohm, which invalidates any detector
        // calibration taken at the other one and gives a trace wrong by a constant factor.
        var link = Link();
        var scope = new Infiniium54845A(link);

        scope.SetInputImpedance(1, ScopeInputImpedance.FiftyOhm);
        Assert.Contains(":CHANnel1:INPut DC50", link.History);

        scope.SetChannelCoupling(1, ScopeCoupling.Dc);

        // Still DC50, not plain DC.
        Assert.Equal(":CHANnel1:INPut DC50", link.History[^1]);
    }

    [Fact]
    public void SettingTerminationKeepsTheCouplingThatWasAlreadyChosen()
    {
        var link = Link();
        var scope = new Infiniium54845A(link);

        scope.SetChannelCoupling(1, ScopeCoupling.Ac);
        Assert.Equal(":CHANnel1:INPut AC", link.History[^1]);

        scope.SetInputImpedance(1, ScopeInputImpedance.OneMegaohm);
        Assert.Equal(":CHANnel1:INPut AC", link.History[^1]);
    }

    [Fact]
    public void ThereIsNoAcCoupledFiftyOhmModeAndAskingForOneIsRefused()
    {
        var scope = new Infiniium54845A(Link());
        scope.SetChannelCoupling(1, ScopeCoupling.Ac);

        var ex = Assert.Throws<NotSupportedException>(
            () => scope.SetInputImpedance(1, ScopeInputImpedance.FiftyOhm));

        Assert.Contains("AC-coupled 50 ohm", ex.Message);
    }

    [Fact]
    public void GroundCouplingIsNotOneOfTheInputCommandsArguments()
    {
        Assert.Throws<NotSupportedException>(
            () => new Infiniium54845A(Link()).SetChannelCoupling(1, ScopeCoupling.Ground));
    }

    [Fact]
    public void TerminationIsReadBackFromTheInstrument()
    {
        Assert.Equal(
            ScopeInputImpedance.FiftyOhm,
            new Infiniium54845A(Link()).GetInputImpedance(1));
    }

    // --- Trigger ------------------------------------------------------------------------------------

    [Fact]
    public void TheExternalTriggerIsAuxOnThisModel()
    {
        // The quick reference notes AUX is available on the 54815/25/35/45/46 and EXTernal only on
        // the 54810/20. Sending EXTernal here would be rejected by the instrument.
        var link = Link();
        new Infiniium54845A(link).SetExternalTrigger(1.0);

        Assert.Contains(":TRIGger:MODE EDGE", link.History);
        Assert.Contains(":TRIGger:EDGE:SOURce AUX", link.History);
        Assert.Contains(":TRIGger:EDGE:SLOPe POSitive", link.History);
        Assert.DoesNotContain(link.History, c => c.Contains("EXTernal"));
    }

    [Fact]
    public void TheTriggerLevelCarriesItsSource()
    {
        // :TRIGger:LEVel takes source and level together, unlike the Tektronix scopes' bare level.
        var link = Link();
        new Infiniium54845A(link).SetExternalTrigger(-0.5);

        Assert.Contains(":TRIGger:LEVel AUX,-0.5", link.History);
    }

    [Fact]
    public void ItCanAlsoTriggerOnAChannel()
    {
        var link = Link();
        new Infiniium54845A(link).SetChannelTrigger(2, 1.5);

        Assert.Contains(":TRIGger:EDGE:SOURce CHANnel2", link.History);
        Assert.Contains(":TRIGger:LEVel CHANnel2,1.5", link.History);
    }

    // --- Scaling: a third convention ------------------------------------------------------------------

    [Fact]
    public void SamplesUseTheAgilentConventionNotTheRigolsOrTektronixs()
    {
        // Agilent:    volts = (raw - YREFerence) x YINCrement + YORigin
        // Rigol:      volts = (raw - yorigin - yreference) x yincrement
        // Tektronix:  volts = YZEro + YMUlt x (raw - YOFf)
        // Three scopes, three formulae, each of which yields a plausible trace on the wrong one.
        var wave = new Infiniium54845A(Link(points: 8)).ReadWaveform(1);

        // Raw sample 0 is 120, YREFerence 128, YINCrement 0.01, YORigin -0.3.
        Assert.Equal((120 - 128) * 0.01 + -0.3, wave.Volts[0], 9);
        Assert.Equal(8, wave.Volts.Count);
    }

    [Fact]
    public void TheTimeAxisUsesXReferenceRatherThanATriggerPointOffset()
    {
        var wave = new Infiniium54845A(Link(points: 8)).ReadWaveform(1);

        // (0 - XREFerence) x XINCrement + XORigin, with XREFerence 2 and XINCrement 1 ns.
        Assert.Equal(-2e-9, wave.StartSeconds, 15);
        Assert.Equal(1e-9, wave.SecondsPerSample, 15);
    }

    [Fact]
    public void TransferIsByteFormatWithByteOrderPinned()
    {
        var link = Link();
        new Infiniium54845A(link).ReadWaveform(1);

        Assert.Contains(":WAVeform:FORMat BYTE", link.History);
        Assert.Contains(":WAVeform:BYTeorder MSBFirst", link.History);
        Assert.Contains(":WAVeform:SOURce CHANnel1", link.History);
    }

    [Fact]
    public void StreamingAFrameReusesTheScaling()
    {
        var link = Link();
        var scope = new Infiniium54845A(link);

        scope.ReadWaveformStreaming(1);
        var warm = link.Transactions.Count;
        scope.ReadWaveformStreaming(1);

        Assert.Equal(2, link.Transactions.Count - warm);
    }

    [Fact]
    public void ChangingTheTerminationDiscardsTheCachedScaling()
    {
        // Termination changes what the detector does as well as what a code means, so a stale
        // scaling here would be wrong twice over.
        var link = Link();
        var scope = new Infiniium54845A(link);

        scope.ReadWaveformStreaming(1);
        scope.SetInputImpedance(1, ScopeInputImpedance.FiftyOhm);

        var before = link.History.Count;
        scope.ReadWaveformStreaming(1);

        Assert.Contains(link.History.Skip(before), c => c.Contains(":WAVeform:YINCrement?"));
    }

    // --- Capability and roles -------------------------------------------------------------------------

    [Fact]
    public void ItIsTheFastScopeAndNotTheTuningScope()
    {
        var capability = new Infiniium54845A(Link()).Capability;

        Assert.True(ScopeRole.Fast.CanBeFilledBy(capability));

        // Refused at config load rather than failing at the bench: this driver cannot drive the
        // instrument's X-versus-Y display, because that command is not in the only Infiniium
        // programming document in the library.
        Assert.False(ScopeRole.Tuning.CanBeFilledBy(capability));
        Assert.Contains(ScopeRole.Tuning.UnmetBy(capability), u => u.Contains("XY"));
    }

    [Fact]
    public void AskingForXyIsRefusedWithTheReason()
    {
        var ex = Assert.Throws<NotSupportedException>(
            () => new Infiniium54845A(Link()).SetTimebaseMode(TimebaseMode.XY));

        Assert.Contains("X-versus-Y", ex.Message);
    }

    [Fact]
    public void VerticalSensitivityStopsAtTwoMillivoltsPerDivision()
    {
        // From the 54845A Service Manual: 2 mV/div to 2 V/div on 1 Mohm. Not 1 mV/div, which is
        // what most of this bench does and what would have been assumed.
        var scope = new Infiniium54845A(Link());

        Assert.Equal(2e-3, scope.Capability.MinVoltsPerDivision);
        Assert.Throws<ArgumentOutOfRangeException>(() => scope.SetChannelScale(1, 1e-3));

        scope.SetChannelScale(1, 2e-3);
    }

    [Fact]
    public void ItStillClearsTheTestPointRoleSensitivity()
    {
        // 5-16 steps 33 and 34 want 0.005 V/Div, and 2 mV/div is finer than that.
        Assert.True(ScopeRole.TestPoint.CanBeFilledBy(new Infiniium54845A(Link()).Capability));
    }

    [Fact]
    public void ChannelNumbersAreRangeChecked()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new Infiniium54845A(Link()).SetChannelScale(5, 0.1));
    }

    [Fact]
    public void ScreenCaptureIsRefusedRatherThanGuessed()
    {
        // A guessed HARDcopy sequence would appear to work and write a corrupt file into the
        // session record, which is worse than having no picture.
        Assert.Throws<NotSupportedException>(
            () => new Infiniium54845A(Link()).CaptureScreenPng());
    }
}
