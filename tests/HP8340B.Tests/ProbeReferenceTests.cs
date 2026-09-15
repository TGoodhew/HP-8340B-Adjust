using HP8340B.Instruments;
using HP8340B.Instruments.Config;
using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// M0-11: the reference check `probe` reports per instrument.
///
/// <para>The point of these tests is the distinction between "I cannot tell you" and "the
/// question does not apply". Without it `probe` warns about the reference state of a power meter
/// and a switch driver, which buries the 5351A — the one instrument on this bench whose reference
/// genuinely cannot be read back and on which 4-2 depends.</para>
/// </summary>
public class ProbeReferenceTests
{
    private static ProbeResult ProbeOf(string role, string model)
    {
        var config = new InstrumentConfig { Role = role, Model = model, Address = "GPIB0::1::INSTR" };
        using var instrument = InstrumentFactory.Create(config, simulate: true);
        return instrument.Probe();
    }

    [Theory]
    // The instruments locked to the Z3805A, where being on the wrong reference changes results.
    [InlineData("dut", "HP 8340B")]
    [InlineData("spectrum-analyzer", "HP 8563E")]
    [InlineData("counter", "HP 5351A")]
    [InlineData("lo-source", "HP 8673B")]
    public void InstrumentsOnTheHouseReferenceSayTheReferenceApplies(string role, string model) =>
        Assert.True(ProbeOf(role, model).ReferenceApplies);

    [Theory]
    // These have no 10 MHz reference worth reporting. A blank column, not a question mark.
    [InlineData("oscilloscope", "Rigol DS1104Z")]
    [InlineData("dmm", "HP 3458A")]
    [InlineData("measuring-receiver", "HP 8902A")]
    [InlineData("switch-driver", "HP 11713A")]
    public void InstrumentsWithNoReferenceDoNotClaimOne(string role, string model)
    {
        var result = ProbeOf(role, model);

        Assert.False(result.ReferenceApplies);
        Assert.False(result.ReferenceUnconfirmed);
    }

    [Fact]
    public void TheCounterIsTheOneThatCannotBeConfirmed()
    {
        // It switches to an external reference automatically but has no command that reads that
        // state back, so it is reported as applying-but-unknown rather than either answer.
        var result = ProbeOf("counter", "HP 5351A");

        Assert.True(result.ReferenceApplies);
        Assert.Null(result.ExternalReference);
        Assert.True(result.ReferenceUnconfirmed);
    }

    [Theory]
    [InlineData("dut", "HP 8340B")]
    [InlineData("spectrum-analyzer", "HP 8563E")]
    [InlineData("lo-source", "HP 8673B")]
    public void TheRestGiveARealAnswer(string role, string model)
    {
        var result = ProbeOf(role, model);

        Assert.NotNull(result.ExternalReference);
        Assert.False(result.ReferenceUnconfirmed);
    }

    [Fact]
    public void AnAnswerIsNeverTreatedAsUnconfirmed()
    {
        var known = new ProbeResult("r", "m", "a", Responded: true,
            ExternalReference: false, ReferenceApplies: true);

        Assert.False(known.ReferenceUnconfirmed);
    }
}

/// <summary>Bench configuration reaching `probe`.</summary>
public class BorrowedKitTests
{
    private static BenchConfig LoadBench() =>
        BenchConfig.Load(Path.Combine(AppContext.BaseDirectory, "bench.json"));

    [Fact]
    public void BorrowedKitIsFlaggedAsSuchInTheShippedBench()
    {
        // The failure mode is a `present` flag left true after the instrument has gone back:
        // probe then reports a plain "no response" that reads as a fault. The flag lets it say
        // what actually happened.
        var bench = LoadBench();

        Assert.Contains(bench.Instruments, i => i.Borrowed);
        Assert.All(bench.Instruments.Where(i => i.Borrowed), i =>
            Assert.Contains("Borrowed", i.Note ?? "", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OwnedKitIsNotFlaggedBorrowed()
    {
        var bench = LoadBench();

        Assert.False(bench.ByRole("dut")!.Borrowed);
        Assert.False(bench.ByRole("spectrum-analyzer")!.Borrowed);
    }

    [Fact]
    public void BorrowedDefaultsToFalseSoAnUnmarkedEntryIsOwned() =>
        Assert.False(new InstrumentConfig().Borrowed);
}
