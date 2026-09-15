using System.Text.Json;
using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// Table 4-8, Swept Frequency Accuracy Test Frequencies, manual p. 4-24, used by 4-4 (M4-05).
///
/// Transcribed from the rendered page image — the text layer damages the numerals in this table
/// (`2-32` for `2.32`, `24.55'` for `24.55`). The centre-frequency columns are computed as
/// start + {0.2, 0.5, 0.8} x span, which was verified against every printed row and so doubles
/// as an arithmetic check on the transcribed stop frequencies.
/// </summary>
public class SweptFrequencyTableTests
{
    private sealed record Centres(double F20, double F50, double F80);

    private sealed record Row(
        double StartGHz, double StopGHz, double SweepTimeMs,
        double ResolutionBandwidthKHz, double TestLimitKHz,
        Dictionary<string, double> CentreFrequencyGHz, double SpanMHz, bool Band4Only);

    private static List<Row> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "table-4-8-swept-frequency.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));

        return doc.RootElement.GetProperty("rows").EnumerateArray()
            .Select(r => new Row(
                r.GetProperty("startGHz").GetDouble(),
                r.GetProperty("stopGHz").GetDouble(),
                r.GetProperty("sweepTimeMs").GetDouble(),
                r.GetProperty("resolutionBandwidthKHz").GetDouble(),
                r.GetProperty("testLimitKHz").GetDouble(),
                r.GetProperty("centreFrequencyGHz").EnumerateObject()
                    .ToDictionary(p => p.Name, p => p.Value.GetDouble()),
                r.GetProperty("spanMHz").GetDouble(),
                r.GetProperty("band4Only").GetBoolean()))
            .ToList();
    }

    [Fact]
    public void TableHasAllTwentySevenRows() => Assert.Equal(27, Load().Count);

    [Fact]
    public void EveryRowStartsAt2Point3GHz()
    {
        // The whole table sweeps upward from the band-1 edge; only the stop frequency varies.
        Assert.All(Load(), r => Assert.Equal(2.3, r.StartGHz));
    }

    [Fact]
    public void CentreFrequenciesAreStartPlusFractionOfSpan()
    {
        // This is the arithmetic check on the transcription: if a stop frequency were mis-read,
        // its three centre frequencies would no longer match the printed ones.
        foreach (var row in Load())
        {
            var span = row.StopGHz - row.StartGHz;
            Assert.Equal(row.StartGHz + 0.2 * span, row.CentreFrequencyGHz["20"], 9);
            Assert.Equal(row.StartGHz + 0.5 * span, row.CentreFrequencyGHz["50"], 9);
            Assert.Equal(row.StartGHz + 0.8 * span, row.CentreFrequencyGHz["80"], 9);
        }
    }

    [Fact]
    public void StopFrequenciesAscendAndStayInRange()
    {
        var rows = Load();

        for (var i = 0; i < rows.Count; i++)
        {
            Assert.True(rows[i].StopGHz > rows[i].StartGHz, $"Row {i + 1} has a negative span.");
            Assert.True(rows[i].StopGHz <= 26.5, $"Row {i + 1} exceeds the instrument's range.");
            if (i > 0)
                Assert.True(rows[i].StopGHz > rows[i - 1].StopGHz, $"Row {i + 1} is out of order.");
        }
    }

    [Fact]
    public void OnlyStopsAbove20GHzAreMarked8340BOnly()
    {
        // Manual step 7: "Stop Frequencies greater than 20.0 GHz only apply to the HP 8340B."
        // The 8341B stops at 20 GHz, so those rows are skipped on that instrument.
        foreach (var row in Load())
            Assert.Equal(row.StopGHz > 20.0, row.Band4Only);

        Assert.Equal(2, Load().Count(r => r.Band4Only));
    }

    [Fact]
    public void StopFrequenciesComeInPairsBracketingSpanBoundaries()
    {
        // The table's design: pairs that straddle 100 kHz, 500 kHz, 5 MHz, 50 MHz, 100 MHz,
        // 500 MHz and 5 GHz of span, so the instrument is tested either side of each point where
        // its behaviour changes. Consecutive near-duplicate rows are deliberate, not a slip.
        var spans = Load().Select(r => r.SpanMHz).ToList();

        foreach (var boundary in new[] { 0.1, 0.5, 5.0, 50.0, 100.0, 500.0, 5000.0 })
        {
            Assert.True(spans.Any(s => s < boundary && s > boundary * 0.95),
                $"No row just below the {boundary} MHz span boundary.");
            Assert.True(spans.Any(s => s > boundary && s < boundary * 1.05),
                $"No row just above the {boundary} MHz span boundary.");
        }
    }

    [Fact]
    public void TestLimitsAreTakenFromTheTableNotComputed()
    {
        // The limits do not follow one percentage: 1% of span below 5 MHz, 2% from 5 MHz to about
        // 100 MHz, 1% again from about 500 MHz, then capped at 50 MHz absolute for the widest
        // spans. This test pins that shape so nobody "simplifies" it into a formula later.
        var rows = Load();

        Assert.Equal(0.99, rows[0].TestLimitKHz);          // 99 kHz span, 1%
        Assert.Equal(200.0, rows[6].TestLimitKHz);         // 10 MHz span, 2%
        Assert.Equal(4990.0, rows[18].TestLimitKHz);       // 499 MHz span, back to 1%
        Assert.All(rows.Where(r => r.SpanMHz > 5010.0),
            r => Assert.Equal(50000.0, r.TestLimitKHz));   // capped at 50 MHz
    }
}
