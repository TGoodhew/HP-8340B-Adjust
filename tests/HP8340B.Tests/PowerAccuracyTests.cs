using HP8340B.Measurements.Model;
using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// Output power accuracy and flatness, manual Table 4-9, pp. 4-25 and 4-26. Transcribed from the
/// rendered page images because both pdftotext modes shift the Band 0 column up by a row — the
/// "+18 to +10 dBm" row has a dash there, and extraction drops it.
/// </summary>
public class PowerAccuracyTests
{
    [Theory]
    // STANDARD INSTRUMENT, accuracy. Band 0 / Bands 1-3 / Band 4.
    [InlineData(+5.0, 1.0, 0.9)]      // +10 to -9.95, band 0
    [InlineData(+5.0, 5.0, 1.5)]      // +10 to -9.95, bands 1-3
    [InlineData(+5.0, 25.0, 2.0)]     // +10 to -9.95, band 4
    [InlineData(-15.0, 1.0, 1.2)]     // -10 to -19.95, band 0
    [InlineData(-15.0, 5.0, 2.0)]
    [InlineData(-15.0, 25.0, 2.5)]
    [InlineData(-30.0, 1.0, 1.5)]     // -20 to -49.95
    [InlineData(-60.0, 1.0, 1.8)]     // -50 to -79.95
    [InlineData(-90.0, 1.0, 2.1)]     // -80 to -100
    [InlineData(-90.0, 5.0, 2.9)]
    [InlineData(-90.0, 25.0, 3.4)]
    public void StandardAccuracyMatchesTable49(double levelDbm, double ghz, double expectedDb) =>
        Assert.Equal(expectedDb, PowerAccuracy.AccuracyDb(InstrumentOption.Standard, levelDbm, ghz));

    [Fact]
    public void Band0HasNoAccuracySpecAbovePlus10dBm()
    {
        // The manual prints a dash: band 0's maximum leveled power is +10 dBm, so there is no
        // +18 to +10 dBm range to specify. This dash is exactly what breaks text extraction.
        Assert.Null(PowerAccuracy.AccuracyDb(InstrumentOption.Standard, +15.0, 1.0));
        Assert.Null(PowerAccuracy.FlatnessDb(InstrumentOption.Standard, +15.0, 1.0));

        // Bands 1-3 and band 4 do have a spec there.
        Assert.Equal(1.8, PowerAccuracy.AccuracyDb(InstrumentOption.Standard, +15.0, 5.0));
        Assert.Equal(2.3, PowerAccuracy.AccuracyDb(InstrumentOption.Standard, +15.0, 25.0));
    }

    [Theory]
    // STANDARD INSTRUMENT, flatness — the second half of Table 4-9, p. 4-26.
    [InlineData(+5.0, 1.0, 0.6)]
    [InlineData(+5.0, 5.0, 1.1)]
    [InlineData(+5.0, 25.0, 1.6)]
    [InlineData(-90.0, 1.0, 1.7)]
    [InlineData(-90.0, 5.0, 2.5)]
    [InlineData(-90.0, 25.0, 3.0)]
    public void StandardFlatnessMatchesTable49(double levelDbm, double ghz, double expectedDb) =>
        Assert.Equal(expectedDb, PowerAccuracy.FlatnessDb(InstrumentOption.Standard, levelDbm, ghz));

    [Fact]
    public void FlatnessIsAlwaysTighterThanAccuracyAtTheSamePoint()
    {
        // Accuracy is absolute level error; flatness is only the spread across frequency, so it
        // must be the smaller number. A transcription slip between the two tables would break this.
        foreach (var option in Enum.GetValues<InstrumentOption>())
        foreach (var row in PowerAccuracy.AccuracyRows(option))
        {
            var level = row.LowerDbm;
            foreach (var ghz in new[] { 1.0, 5.0, 25.0 })
            {
                var accuracy = PowerAccuracy.AccuracyDb(option, level, ghz);
                var flatness = PowerAccuracy.FlatnessDb(option, level, ghz);

                if (accuracy is null || flatness is null) continue;
                Assert.True(flatness < accuracy,
                    $"{option} at {level} dBm, {ghz} GHz: flatness {flatness} should be tighter "
                    + $"than accuracy {accuracy}.");
            }
        }
    }

    [Theory]
    // The level ranges differ per option. Standard and 004 have six rows, 001 and 005 have three.
    [InlineData(InstrumentOption.Standard, 6)]
    [InlineData(InstrumentOption.Opt004, 6)]
    [InlineData(InstrumentOption.Opt001, 3)]
    [InlineData(InstrumentOption.Opt005, 3)]
    public void RowCountMatchesTheOption(InstrumentOption option, int expected)
    {
        Assert.Equal(expected, PowerAccuracy.AccuracyRows(option).Count);
        Assert.Equal(expected, PowerAccuracy.FlatnessRows(option).Count);
    }

    [Fact]
    public void Option004BreaksAtDifferentLevelsFromStandard()
    {
        // Standard breaks at -9.95; Option 004 at -11.95. Getting these confused would apply the
        // wrong limit either side of the boundary.
        Assert.Equal(1.7, PowerAccuracy.AccuracyDb(InstrumentOption.Opt004, -11.0, 5.0));
        Assert.Equal(2.2, PowerAccuracy.AccuracyDb(InstrumentOption.Opt004, -13.0, 5.0));

        Assert.Equal(1.5, PowerAccuracy.AccuracyDb(InstrumentOption.Standard, -9.0, 5.0));
        Assert.Equal(2.0, PowerAccuracy.AccuracyDb(InstrumentOption.Standard, -11.0, 5.0));
    }

    [Fact]
    public void RowsAreOrderedAndDoNotOverlap()
    {
        foreach (var option in Enum.GetValues<InstrumentOption>())
        foreach (var rows in new[] { PowerAccuracy.AccuracyRows(option), PowerAccuracy.FlatnessRows(option) })
        {
            for (var i = 0; i < rows.Count; i++)
            {
                Assert.True(rows[i].UpperDbm > rows[i].LowerDbm, $"{option} row {i} is inverted.");
                // Rows descend and never overlap. They either share a boundary ("+18 to +10"
                // then "+10 to -9.95") or leave the manual's 0.05 dB gap ("-9.95" then "-10").
                if (i > 0)
                    Assert.True(rows[i].UpperDbm <= rows[i - 1].LowerDbm,
                        $"{option} rows {i - 1} and {i} overlap.");
            }
        }
    }

    [Fact]
    public void SharedBoundaryResolvesToTheRowThatHasASpec()
    {
        // +10 dBm appears in both "+18 to +10" and "+10 to -9.95". Band 0 has a dash in the first
        // and +/-0.9 dB in the second, and +10 dBm is band 0's maximum leveled power, so the
        // second is plainly the intended row.
        Assert.Equal(0.9, PowerAccuracy.AccuracyDb(InstrumentOption.Standard, +10.0, 1.0));
        Assert.Equal(1.5, PowerAccuracy.AccuracyDb(InstrumentOption.Standard, +10.0, 5.0));

        // Just above the boundary is still the above-spec ALC region, where band 0 has no spec.
        Assert.Null(PowerAccuracy.AccuracyDb(InstrumentOption.Standard, +10.01, 1.0));
    }

    [Fact]
    public void LevelInTheManualsPointZeroFiveGapTakesTheRowBelow()
    {
        // "+10 to -9.95 dBm" is followed by "-10 to -19.95 dBm", so -9.97 is in neither. The
        // 0.05 dB is the manual writing "just above -10", not a real discontinuity.
        Assert.Equal(1.2, PowerAccuracy.AccuracyDb(InstrumentOption.Standard, -9.97, 1.0));

        // Either side of the gap behaves as the table says.
        Assert.Equal(0.9, PowerAccuracy.AccuracyDb(InstrumentOption.Standard, -9.95, 1.0));
        Assert.Equal(1.2, PowerAccuracy.AccuracyDb(InstrumentOption.Standard, -10.0, 1.0));
    }

    [Theory]
    [InlineData(+25.0)]
    [InlineData(-120.0)]
    public void LevelOutsideTheTableThrows(double levelDbm) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PowerAccuracy.AccuracyDb(InstrumentOption.Standard, levelDbm, 5.0));

    [Fact]
    public void FrequencyOutsideTheInstrumentThrows() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PowerAccuracy.AccuracyDb(InstrumentOption.Standard, 0.0, 30.0));
}
