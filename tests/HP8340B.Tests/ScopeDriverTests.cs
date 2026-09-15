using HP8340B.Instruments;
using HP8340B.Instruments.Config;
using HP8340B.Instruments.Visa;
using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// M0-08: the Rigol DS1104Z. The primary tuning view — setup S4 puts the 8473C detector on CH1
/// and the DUT sweep ramp on CH2 in XY mode, reproducing the manual's A-vs-B display.
/// </summary>
public class ScopeDriverTests
{
    private static (RigolDs1104Z Scope, SimulatedInstrumentLink Link) New()
    {
        var link = SimulatedBench.LinkFor("Rigol DS1104Z", "TCPIP0::192.168.1.145::inst0::INSTR");
        return (new RigolDs1104Z(link), link);
    }

    [Theory]
    [InlineData(TimebaseMode.XY, ":TIMebase:MODE XY")]
    [InlineData(TimebaseMode.Normal, ":TIMebase:MODE MAIN")]
    [InlineData(TimebaseMode.Roll, ":TIMebase:MODE ROLL")]
    public void TimebaseModeMatchesTheGuide(TimebaseMode mode, string expected)
    {
        // DS1000Z Programming Guide, :TIMebase:MODE <mode>, {MAIN|XY|ROLL}.
        var (scope, link) = New();
        scope.SetTimebaseMode(mode);
        Assert.Equal(expected, link.History[^1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void ChannelNumbersAreRangeChecked(int channel)
    {
        var (scope, _) = New();
        Assert.Throws<ArgumentOutOfRangeException>(() => scope.SetChannelScale(channel, 1.0));
    }

    [Fact]
    public void ProbeRatioIsSettableBecauseGettingItWrongScalesEveryReading()
    {
        // S4 uses 1x for direct BNC; S5 uses 10x to match the probes on the internal test points.
        var (scope, link) = New();

        scope.SetProbeRatio(1, 10);
        Assert.Equal(":CHANnel1:PROBe 10", link.History[^1]);

        Assert.Throws<ArgumentOutOfRangeException>(() => scope.SetProbeRatio(1, 0));
    }

    [Fact]
    public void ExternalTriggerIsConfiguredForTheDutSweep()
    {
        var (scope, link) = New();
        scope.SetExternalTrigger(1.0);

        Assert.Contains(":TRIGger:MODE EDGE", link.History);
        Assert.Contains(":TRIGger:EDGe:SOURce EXT", link.History);
        Assert.Contains(":TRIGger:EDGe:SLOPe POSitive", link.History);
        Assert.Contains(":TRIGger:EDGe:LEVel 1", link.History);
    }

    [Fact]
    public void WaveformIsReadInAsciiSoSamplesArriveAlreadyInVolts()
    {
        // BYTE format is faster on the wire but needs yincrement/yorigin/yreference applied by
        // hand. At screen depth the difference does not reach M2-02's 0.5 s budget.
        var (scope, link) = New();
        var wave = scope.ReadWaveform(1);

        Assert.Contains(":WAVeform:SOURce CHANnel1", link.History);
        Assert.Contains(":WAVeform:FORMat ASCii", link.History);
        Assert.Equal(600, wave.Volts.Count);
        Assert.Equal(1, wave.Channel);
    }

    [Fact]
    public void WaveformCarriesItsTimeAxisFromThePreamble()
    {
        var (scope, _) = New();
        var wave = scope.ReadWaveform(1);

        // Preamble field 4 is xincrement, field 5 xorigin.
        Assert.Equal(1e-6, wave.SecondsPerSample);
        Assert.Equal(0.0, wave.StartSeconds);
        Assert.Equal(0.0, wave.TimeAt(0));
        Assert.Equal(1e-6, wave.TimeAt(1), 12);
    }

    [Fact]
    public void SimulatedDetectorTraceIsNegativeGoingAndNotFlat()
    {
        // Every crystal detector on this bench is negative polarity, and a flat trace would let a
        // broken minimum-finder pass.
        var (scope, _) = New();
        var wave = scope.ReadWaveform(1);

        Assert.All(wave.Volts, v => Assert.True(v < 0, "Detector output should be negative-going."));
        Assert.True(wave.Volts.Max() - wave.Volts.Min() > 0.1, "Trace should have real shape.");
    }

    [Theory]
    // Rigol returns a definite-length block: '#' then one digit giving the header width, then
    // that many digits of byte count, then the payload.
    [InlineData("#9000001200-0.5,-0.4,-0.3", new[] { -0.5, -0.4, -0.3 })]
    [InlineData("-0.5,-0.4,-0.3", new[] { -0.5, -0.4, -0.3 })]
    [InlineData("  -1.0, -2.0 ", new[] { -1.0, -2.0 })]
    public void BlockHeaderIsStrippedBeforeParsing(string response, double[] expected) =>
        Assert.Equal(expected, RigolDs1104Z.ParseAsciiBlock(response));

    [Fact]
    public void EmptyOrCorruptWaveformFailsLoudly()
    {
        Assert.Throws<InvalidOperationException>(() => RigolDs1104Z.ParseAsciiBlock(""));
        Assert.Throws<InvalidOperationException>(() => RigolDs1104Z.ParseAsciiBlock("1.0,oops,3.0"));
    }

    [Fact]
    public void XyViewConfiguresBothChannelsAndSwitchesToXy()
    {
        var (scope, link) = New();
        scope.ConfigureXyView(detectorChannel: 1, sweepChannel: 2,
            detectorVoltsPerDivision: 0.1, sweepVoltsPerDivision: 2.0);

        Assert.Contains(":CHANnel1:DISPlay ON", link.History);
        Assert.Contains(":CHANnel2:DISPlay ON", link.History);
        Assert.Contains(":CHANnel1:SCALe 0.1", link.History);
        Assert.Contains(":CHANnel2:SCALe 2", link.History);

        // XY last: setting it before the channels are configured shows a half-set-up display.
        Assert.Equal(":TIMebase:MODE XY", link.History[^1]);
    }

    [Fact]
    public void ProbeFailureMentionsTheLanAddressBecauseThatIsTheUsualCause()
    {
        // This is the one instrument on LAN rather than GPIB, so a DHCP change silently breaks
        // the most important display in the project.
        var config = new InstrumentConfig
        {
            Role = "oscilloscope", Model = "Rigol DS1104Z", Address = "TCPIP0::10.0.0.1::inst0::INSTR",
        };

        var scope = new RigolDs1104Z(new ThrowingLink(config.Address));
        var result = scope.Probe();

        Assert.False(result.Responded);
        Assert.Contains("LAN", result.Detail);
    }

    private sealed class ThrowingLink(string resource) : IInstrumentLink
    {
        public string ResourceName { get; } = resource;
        public bool IsSimulated => true;
        public TimeSpan Timeout { get; set; }
        public IReadOnlyList<string> History => Array.Empty<string>();
        public IReadOnlyList<InstrumentTransaction> Transactions => Array.Empty<InstrumentTransaction>();

        public void Clear() { }
        public void Write(string command) { }
        public string Read() => throw new TimeoutException("no response");
        public string Query(string command) => throw new TimeoutException("no response");
        public byte[] ReadBytes(int count) => throw new TimeoutException("no response");
        public void WriteBytes(byte[] data) { }
        public byte SerialPoll() => 0;
        public void Dispose() { }
    }
}
