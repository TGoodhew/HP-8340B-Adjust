using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// Keeps docs/PROVISIONAL.md in step with the source.
///
/// <para><b>Why the marker lives in the code rather than only in a note.</b> A number that
/// survives a few readings starts to look measured. A comment saying it has never been checked
/// against an instrument is hard to launder into fact, because it sits next to the value every
/// time anybody reads it — and a doc nobody opens is not a safeguard.</para>
///
/// <para>The suggestion came from the HP-Attenuator session on this bench, which had the same
/// problem in a different form: "it builds and the sim passes" kept being mistaken for "it
/// works". These tests are the mechanism that stops the two halves drifting apart, following the
/// same pattern as <see cref="HpibDocDriftTests"/>.</para>
/// </summary>
public class ProvisionalDriftTests
{
    private const string Marker = "PROVISIONAL: never checked against an instrument.";

    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HP-8340B-Adjust.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!;
    }

    /// <summary>Every source file carrying the marker, and the member it sits above.</summary>
    private static IReadOnlyList<(string File, string Member)> MarkedInSource()
    {
        var src = Path.Combine(RepoRoot().FullName, "src");
        var found = new List<(string, string)>();

        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);

            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains(Marker, StringComparison.Ordinal)) continue;

                // The declaration is the next line that is not a doc comment or blank.
                var member = "(unknown)";

                for (var j = i + 1; j < lines.Length; j++)
                {
                    var trimmed = lines[j].Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith("///")) continue;

                    member = trimmed;
                    break;
                }

                found.Add((Path.GetFileName(file), member));
            }
        }

        return found;
    }

    private static string Doc() =>
        File.ReadAllText(Path.Combine(RepoRoot().FullName, "docs", "PROVISIONAL.md"));

    [Fact]
    public void ThereAreProvisionalNumbersAndTheyAreMarked()
    {
        // If this ever goes to zero it means either every figure has been confirmed — worth
        // celebrating and worth checking — or somebody removed the markers without confirming
        // anything.
        Assert.NotEmpty(MarkedInSource());
    }

    [Fact]
    public void EveryMarkedNumberIsListedInTheDoc()
    {
        var doc = Doc();
        var missing = new List<string>();

        foreach (var (file, member) in MarkedInSource())
        {
            // The doc names each by its member name, e.g. SelfChecks.LevelToleranceDb.
            var name = ExtractName(member);

            if (name is not null && !doc.Contains(name, StringComparison.Ordinal))
                missing.Add($"{file}: {name}");
        }

        Assert.True(missing.Count == 0,
            "These are marked PROVISIONAL in the source but not listed in docs/PROVISIONAL.md, so "
            + "somebody reading the doc would think the list was complete:\n  "
            + string.Join("\n  ", missing));
    }

    [Fact]
    public void EveryNumberInTheDocsTableIsStillMarkedInTheSource()
    {
        // The other direction: a figure confirmed at the bench should lose its marker AND move out
        // of the table. If only one of the two happens, this catches it.
        var marked = MarkedInSource()
            .Select(m => ExtractName(m.Member))
            .Where(n => n is not null)
            .ToHashSet(StringComparer.Ordinal);

        var doc = Doc();
        // The doc grew a second table (unsourced hardware claims), which uses a different
        // mechanism -- Confirmed: false in the source rather than the PROVISIONAL phrase. Bound
        // this to the first table only, or it reports the second table's rows as unmarked.
        var table = doc[doc.IndexOf("## The list", StringComparison.Ordinal)..];
        table = table[..table.IndexOf("## Unsourced hardware claims", StringComparison.Ordinal)];

        var listed = table
            .Split('\n')
            .Where(l => l.TrimStart().StartsWith("| `", StringComparison.Ordinal))
            .Select(l => l.Split('`')[1])
            .Select(entry => entry.Split('.')[^1])
            .ToList();

        Assert.NotEmpty(listed);

        var stale = listed.Where(name => !marked.Contains(name)).ToList();

        Assert.True(stale.Count == 0,
            "These are in docs/PROVISIONAL.md's table but no longer marked in the source. If they "
            + "have been confirmed, move them out of the table and say what settled them:\n  "
            + string.Join("\n  ", stale));
    }

    [Fact]
    public void TheDocSaysWhatWouldSettleEachOne()
    {
        // A list of unconfirmed numbers with no route to confirming them is a list of complaints.
        var doc = Doc();
        // The doc grew a second table (unsourced hardware claims), which uses a different
        // mechanism -- Confirmed: false in the source rather than the PROVISIONAL phrase. Bound
        // this to the first table only, or it reports the second table's rows as unmarked.
        var table = doc[doc.IndexOf("## The list", StringComparison.Ordinal)..];
        table = table[..table.IndexOf("## Unsourced hardware claims", StringComparison.Ordinal)];

        var rows = table
            .Split('\n')
            .Where(l => l.TrimStart().StartsWith("| `", StringComparison.Ordinal))
            .ToList();

        Assert.All(rows, row =>
        {
            var columns = row.Split('|', StringSplitOptions.RemoveEmptyEntries);

            Assert.True(columns.Length >= 3,
                $"This row has no 'what would settle it' column:\n  {row.Trim()}");

            Assert.True(columns[2].Trim().Length > 20,
                $"This row's 'what would settle it' is too vague to act on:\n  {row.Trim()}");
        });
    }

    /// <summary>
    /// Pulls the member name out of a declaration line.
    ///
    /// <para>The name is the last identifier before the initialiser, so the accessor block has to
    /// come out first: without that, an auto-property's name is hidden behind
    /// <c>{ get; set; }</c> and the first attempt returned the VALUE rather than the name.</para>
    /// </summary>
    private static string? ExtractName(string declaration)
    {
        var text = declaration;

        // Strip any { get; set; } so the last token before '=' is the member name.
        var open = text.IndexOf('{');
        var close = text.IndexOf('}');
        if (open >= 0 && close > open) text = text[..open] + " " + text[(close + 1)..];

        var equals = text.IndexOf('=');
        if (equals >= 0) text = text[..equals];

        var words = text.Split(
            [' ', '	'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var name = words.LastOrDefault();

        // An identifier, not a keyword or a fragment of punctuation.
        return name is not null && name.All(c => char.IsLetterOrDigit(c) || c == '_')
            ? name
            : null;
    }
}

/// <summary>
/// The other half of the same discipline: figures stated ABOUT INSTRUMENTS with no manual to cite.
///
/// <para>Different from a threshold I chose. These need confirming against a data sheet or the
/// hardware itself, not against a measurement — and unlike the thresholds they are checked
/// behaviourally rather than by parsing a doc, because the mechanism is a flag in the code.</para>
///
/// <para>Prompted by the HP-Attenuator session auditing its own output and finding a
/// reconstruction printing as fact: the comment said it was reconstructed, the log line did not.
/// The same audit here found the 33311 ratings, which is the sharpest case in this repo because
/// that number is what decides whether a band-4 result may be reported as Spec.</para>
/// </summary>
public class UnsourcedRatingTests
{
    [Theory]
    [InlineData("33311C")]
    [InlineData("33311B")]
    [InlineData("44472A")]
    public void RatingsWithNoDataSheetAreMarkedUnconfirmed(string model)
    {
        var rating = HP8340B.Instruments.SwitchModule.RatingForSwitch(model);
        var (maxHz, confirmed, source) = (rating.MaxHz, rating.Confirmed, rating.Source);

        Assert.True(maxHz > 0, "The limit still applies — unsourced is not the same as unknown.");
        Assert.False(confirmed);
        // Each says WHY it is unsourced rather than just flagging it, so somebody can tell a
        // missing data sheet from a figure nobody has looked for.
        Assert.Contains("local", source, StringComparison.OrdinalIgnoreCase);
        Assert.True(source.Length > 40, $"'{source}' is too terse to act on.");
    }

    [Fact]
    public void ARatingTheGuideActuallyGivesIsConfirmed()
    {
        // The distinction has to be capable of coming out true, or the flag means "rating".
        var rating = HP8340B.Instruments.SwitchModule.RatingForSwitch("44476A");
        var (maxHz, confirmed, source) = (rating.MaxHz, rating.Confirmed, rating.Source);

        Assert.Equal(18e9, maxHz);
        Assert.True(confirmed);
        Assert.Contains("DC to 18 GHz", source);
    }

    [Fact]
    public void AnUnconfirmedRatingStillConstrainsTheLeg()
    {
        // Unsourced is better than no limit. A band-4 result through a 33311B is still refused.
        var leg = new HP8340B.Bench.Model.RoutedLeg { SwitchModel = "33311B" };

        Assert.False(leg.LimitConfirmed);
        Assert.False(leg.Covers(HP8340B.Instruments.Model.BandId.Band4));
    }

    [Fact]
    public void TheHookUpCardSaysTheRatingIsUnconfirmedRatherThanPrintingItAsFact()
    {
        // This is the actual finding: a comment three files away does not help somebody reading
        // "to 26.5 GHz" on a card at the bench.
        var leg = new HP8340B.Bench.Model.RoutedLeg
        {
            Id = "SR-A", Name = "test", SwitchModel = "33311C", Slot = 1, Channels = [100],
        };

        Assert.Contains("unconfirmed rating", leg.Describe());
    }

    [Fact]
    public void AConfirmedRatingIsPrintedPlainly()
    {
        var leg = new HP8340B.Bench.Model.RoutedLeg
        {
            Id = "X", Name = "test", SwitchModel = "44476A", Slot = 1, Channels = [100],
        };

        Assert.DoesNotContain("unconfirmed", leg.Describe());
    }

    [Fact]
    public void EveryUnconfirmedRatingIsListedInTheDoc()
    {
        var doc = File.ReadAllText(Path.Combine(
            RepoRoot().FullName, "docs", "PROVISIONAL.md"));

        var section = doc[doc.IndexOf("## Unsourced hardware claims", StringComparison.Ordinal)..];

        foreach (var model in new[] { "33311C", "33311B", "44472A" })
            Assert.Contains(model, section, StringComparison.Ordinal);
    }

    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HP-8340B-Adjust.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!;
    }
}
