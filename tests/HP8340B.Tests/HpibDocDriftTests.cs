using HP8340B.Instruments;
using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// Keeps docs/HPIB-8340B.md in step with Hp8340BCommands.All.
///
/// The doc is not machine-generated: most of its value is the prose around the tables — how to
/// extract Table 3-2 without mis-pairing it, what the cal-constant sequence means, which bits of
/// the extended status byte matter. Generating it would throw that away. Instead these tests fail
/// if the two fall out of step, which is the property that actually matters.
/// </summary>
public class HpibDocDriftTests
{
    private static string DocPath()
    {
        // Walk up from the test binary to the repo root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HP-8340B-Adjust.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "docs", "HPIB-8340B.md");
    }

    [Fact]
    public void EveryVerifiedCodeAppearsInTheDoc()
    {
        var doc = File.ReadAllText(DocPath());

        var missing = Hp8340BCommands.All
            .Where(c => c.Status == CodeStatus.Verified)
            .Where(c => !doc.Contains($"`{c.Code}", StringComparison.Ordinal)
                        && !doc.Contains($"`{c.Code} ", StringComparison.Ordinal))
            .Select(c => c.Code)
            .ToList();

        Assert.True(missing.Count == 0,
            "These verified codes are in Hp8340BCommands.All but not in docs/HPIB-8340B.md: "
            + string.Join(", ", missing));
    }

    [Fact]
    public void DocDoesNotClaimAnythingIsUnverifiedWhileTheTableSaysOtherwise()
    {
        var doc = File.ReadAllText(DocPath());
        var unverified = Hp8340BCommands.Unverified.ToList();

        if (unverified.Count == 0)
        {
            // Nothing is Unverified today. The doc must not still carry an "Unverified" section
            // listing codes as unusable, or it would send someone hunting for a manual they no
            // longer need.
            Assert.DoesNotContain("## Unverified", doc, StringComparison.Ordinal);
        }
        else
        {
            foreach (var code in unverified)
                Assert.Contains(code.Code, doc, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DocCitesTheOperatingManualAsTheSource()
    {
        // The service manual carries only the handful of codes its own test programs use. Getting
        // this wrong sends the next person to the wrong document.
        var doc = File.ReadAllText(DocPath());

        Assert.Contains("Table 3-2", doc, StringComparison.Ordinal);
        Assert.Contains("Operating Manual Section III", doc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pdftotext -raw", doc, StringComparison.Ordinal);
    }

    [Fact]
    public void BothStatusByteTablesAreDocumented()
    {
        var doc = File.ReadAllText(DocPath());

        Assert.Contains("Status byte #1", doc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Extended status byte #2", doc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RF unleveled", doc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("External frequency reference selected", doc, StringComparison.OrdinalIgnoreCase);
    }
}
