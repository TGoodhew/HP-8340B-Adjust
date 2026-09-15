using HP8340B.Instruments;
using HP8340B.Instruments.Visa;
using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// M0-03: the DUT driver. Every command is checked against the mnemonic Table 3-2 gives, because
/// a wrong code here reaches a 26.5 GHz synthesiser.
/// </summary>
public class Hp8340BDriverTests
{
    private static (Hp8340B Dut, SimulatedInstrumentLink Link) NewDut()
    {
        var link = SimulatedBench.LinkFor("HP 8340B", "SIM::dut::INSTR");
        return (new Hp8340B(link), link);
    }

    [Fact]
    public void SweepRangeUsesFaAndFb()
    {
        // 5-14 step 8: START 6.9 GHz, STOP 13.5 GHz for the band-2 adjustments.
        var (dut, link) = NewDut();
        dut.SetSweepGHz(6.9, 13.5);

        Assert.Equal(new[] { "FA 6.9 GZ", "FB 13.5 GZ" }, link.History);
    }

    [Fact]
    public void SweepRangeRefusesAnInvertedSpan()
    {
        var (dut, _) = NewDut();
        Assert.Throws<ArgumentException>(() => dut.SetSweepGHz(13.5, 6.9));
    }

    [Fact]
    public void CentreSpanUsesCfAndDf()
    {
        // DF is DELTA frequency in Table 3-2, not "span" as the project brief first guessed.
        var (dut, link) = NewDut();
        dut.SetCentreSpanGHz(10.0, 1.0);

        Assert.Equal(new[] { "CF 10 GZ", "DF 1 GZ" }, link.History);
    }

    [Theory]
    // Below 1 s the manual thinks in milliseconds (5-14 asks for 200 ms sweeps); above, seconds
    // (the multiband sweeps of steps 59-67 are 1 s).
    [InlineData(200, "ST 200 MS")]
    [InlineData(50, "ST 50 MS")]
    [InlineData(1000, "ST 1 SC")]
    [InlineData(2000, "ST 2 SC")]
    public void SweepTimePicksTheUnitTheManualUses(int milliseconds, string expected)
    {
        var (dut, link) = NewDut();
        dut.SetSweepTime(TimeSpan.FromMilliseconds(milliseconds));

        Assert.Equal(expected, link.History[^1]);
    }

    [Fact]
    public void SweepTimeRefusesZeroOrNegative()
    {
        var (dut, _) = NewDut();
        Assert.Throws<ArgumentOutOfRangeException>(() => dut.SetSweepTime(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => dut.SetSweepTime(TimeSpan.FromSeconds(-1)));
    }

    [Theory]
    [InlineData(SweepMode.Continuous, "S1")]
    [InlineData(SweepMode.Single, "S2")]
    [InlineData(SweepMode.Manual, "S3")]
    public void SweepModeMapsToS1S2S3(SweepMode mode, string expected)
    {
        var (dut, link) = NewDut();
        dut.SetSweepMode(mode);
        Assert.Equal(expected, link.History[^1]);
    }

    [Theory]
    // A1/A2/A3 = [INT]/[XTAL]/[METER], Table 3-2.
    [InlineData(LevelingMode.Internal, "A1")]
    [InlineData(LevelingMode.External, "A2")]
    [InlineData(LevelingMode.PowerMeter, "A3")]
    public void LevelingModeMapsToA1A2A3(LevelingMode mode, string expected)
    {
        var (dut, link) = NewDut();
        dut.SetLeveling(mode);
        Assert.Equal(expected, link.History[^1]);
    }

    [Fact]
    public void PowerSweepAndAmUseTheirOnOffSuffixes()
    {
        var (dut, link) = NewDut();
        dut.SetPowerSweep(true);
        dut.SetAmplitudeModulation(true);
        dut.SetPowerSweep(false);
        dut.SetAmplitudeModulation(false);

        Assert.Equal(new[] { "PS1", "AM1", "PS0", "AM0" }, link.History);
    }

    [Theory]
    [InlineData(1, "SV1")]
    [InlineData(3, "SV3")]
    [InlineData(9, "SV9")]
    public void SaveUsesRegisters1To9(int register, string expected)
    {
        var (dut, link) = NewDut();
        dut.SaveState(register);
        Assert.Equal(expected, link.History[^1]);
    }

    [Theory]
    [InlineData(0, "RC0")]
    [InlineData(3, "RC3")]
    [InlineData(9, "RC9")]
    public void RecallUsesRegisters0To9(int register, string expected)
    {
        var (dut, link) = NewDut();
        dut.RecallState(register);
        Assert.Equal(expected, link.History[^1]);
    }

    [Fact]
    public void SaveAndRecallHaveDifferentRegisterRanges()
    {
        // Table 3-2: SVn is 1-9 but RCn is 0-9. Register 0 holds the pre-recall state, so it can
        // be recalled but not saved to.
        var (dut, _) = NewDut();

        Assert.Throws<ArgumentOutOfRangeException>(() => dut.SaveState(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => dut.SaveState(10));
        Assert.Throws<ArgumentOutOfRangeException>(() => dut.RecallState(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => dut.RecallState(10));
    }

    [Fact]
    public void FiveFourteenRegisterTrioRoundTrips()
    {
        // 5-14 steps 32-58 save the slow, AUTO and single-sweep states into registers 1, 2 and 3
        // and compare their envelopes.
        var (dut, link) = NewDut();

        foreach (var r in new[] { 1, 2, 3 }) dut.SaveState(r);
        foreach (var r in new[] { 1, 2, 3 }) dut.RecallState(r);

        Assert.Equal(new[] { "SV1", "SV2", "SV3", "RC1", "RC2", "RC3" }, link.History);
    }

    [Fact]
    public void LearnStringIsReadAsBinaryNotText()
    {
        var (dut, link) = NewDut();

        // Include bytes that are not valid text; a string round-trip would mangle them.
        var state = Enumerable.Range(0, Hp8340B.LearnStringLength)
            .Select(i => (byte)(i * 3)).ToArray();
        link.NextBytes = state;

        var read = dut.ReadLearnString();

        Assert.Equal(Hp8340B.LearnStringLength, read.Length);
        Assert.Equal(state, read);
        Assert.Equal("OL", link.History[^1]);
    }

    [Fact]
    public void LearnStringWriteSendsTheCommandThenExactly123Bytes()
    {
        var (dut, link) = NewDut();
        var state = new byte[Hp8340B.LearnStringLength];

        dut.WriteLearnString(state);

        Assert.Equal("IL", link.History[^1]);
        Assert.Equal(Hp8340B.LearnStringLength, link.LastBytesWritten.Length);
    }

    [Fact]
    public void LearnStringWriteRefusesAPartialState()
    {
        // A short payload would leave the instrument consuming whatever came next as state.
        var (dut, _) = NewDut();

        var ex = Assert.Throws<ArgumentException>(() => dut.WriteLearnString(new byte[100]));
        Assert.Contains("Refusing to send a partial state", ex.Message);
    }

    [Fact]
    public void BothStatusBytesAreReadWithOs()
    {
        var (dut, link) = NewDut();
        link.NextBytes =
        [
            (byte)(StatusByte1.RfSettled | StatusByte1.EndOfSweep),
            (byte)(StatusByte2.RfUnleveled | StatusByte2.ExternalFreqRefSelected),
        ];

        var (primary, extended) = dut.ReadBothStatusBytes();

        Assert.Equal("OS", link.History[^1]);
        Assert.True(primary.HasFlag(StatusByte1.RfSettled));
        Assert.True(primary.HasFlag(StatusByte1.EndOfSweep));
        Assert.True(extended.HasFlag(StatusByte2.RfUnleveled));
        Assert.True(extended.HasFlag(StatusByte2.ExternalFreqRefSelected));
    }

    [Fact]
    public void UnleveledAndExternalReferenceComeFromExtendedStatus()
    {
        // These two bits carry real weight: M1-05 finds maximum leveled power with the first,
        // and probe confirms the Z3805A lock with the second.
        var (dut, link) = NewDut();

        link.NextBytes = [0x00, (byte)StatusByte2.RfUnleveled];
        Assert.True(dut.IsUnleveled());
        Assert.False(dut.IsExternalReferenceSelected());

        link.NextBytes = [0x00, (byte)StatusByte2.ExternalFreqRefSelected];
        Assert.False(dut.IsUnleveled());
        Assert.True(dut.IsExternalReferenceSelected());
    }

    [Fact]
    public void ProbeReportsAFaultIndicatorRatherThanClaimingHealth()
    {
        var (dut, link) = NewDut();
        link.NextBytes = [0x00, (byte)StatusByte2.FaultIndicatorOn];

        var result = dut.Probe();

        Assert.True(result.Responded);
        Assert.Contains("FAULT", result.Detail);
    }

    [Fact]
    public void EveryCommandTheDriverSendsIsAVerifiedCode()
    {
        // Belt and braces on rule 2: drive the whole surface and confirm every mnemonic the
        // driver emitted is in the verified table.
        var (dut, link) = NewDut();

        dut.Preset();
        dut.SetCwGHz(2.3);
        dut.SetSweepGHz(6.9, 13.5);
        dut.SetCentreSpanGHz(10, 1);
        dut.SetFrequencyStepGHz(0.1);
        dut.SetPowerDbm(-30);
        dut.SetPowerSweep(true);
        dut.SetAmplitudeModulation(true);
        dut.SetLeveling(LevelingMode.External);
        dut.SetSweepTime(TimeSpan.FromMilliseconds(200));
        dut.SetSweepMode(SweepMode.Single);
        dut.TakeSweep();
        dut.SaveState(1);
        dut.RecallState(1);
        dut.ClearStatus();
        dut.RfOn();
        dut.RfOff();

        Assert.NotEmpty(link.History);

        foreach (var sent in link.History)
        {
            var token = sent.Split(' ')[0];

            // SVn and RCn carry a register digit; everything else is the bare mnemonic.
            var verified = IsVerified(token) || IsVerified(token.TrimEnd(
                '0', '1', '2', '3', '4', '5', '6', '7', '8', '9'));

            Assert.True(verified, $"Driver sent '{sent}', whose mnemonic is not a verified code.");
        }

        static bool IsVerified(string code) =>
            code.Length > 0 && Hp8340BCommands.Find(code) is { Status: CodeStatus.Verified };
    }
}
