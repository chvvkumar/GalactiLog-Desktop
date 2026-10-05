using GalactiLog.Core.Scanning;
using GalactiLog.Core.Settings;
using Xunit;

namespace GalactiLog.Core.Tests.Scanning;

// Spec 12.2's scan filter notice condition (PAR-014), Phase 14B Task 5. The truth table the
// roadmap's Verify line names, over the one computation both surfaces read.
public class ScanFilterNoticeTests
{
    private static ScanFilterConfig Seeded() => new() { NameRules = ScanFilterConfig.SeededRules() };

    [Fact]
    public void AFreshlySeededProfile_ShowsTheNotice()
    {
        Assert.True(ScanFilterConfig.IsOnlyTheSeededRules(Seeded()));
    }

    // An empty configuration is not a seeded one: the wizard has not run, or the user cleared
    // everything. Either way the notice's sentence would be false.
    [Fact]
    public void AnEmptyConfig_ShowsNoNotice()
    {
        Assert.False(ScanFilterConfig.IsOnlyTheSeededRules(ScanFilterConfig.Empty));
    }

    [Fact]
    public void AnAddedRule_HidesTheNotice()
    {
        var filters = Seeded() with
        {
            NameRules =
            [
                .. ScanFilterConfig.SeededRules(),
                new NameRule { Id = "mine", Action = "exclude", Type = "glob", Pattern = "*_bad.fits", Target = "file" },
            ],
        };

        Assert.False(ScanFilterConfig.IsOnlyTheSeededRules(filters));
    }

    // Spec 12.2: "a deletion included". A user who removed one seeded rule has made a decision.
    [Fact]
    public void ADeletedSeededRule_HidesTheNotice()
    {
        var filters = Seeded() with { NameRules = [.. ScanFilterConfig.SeededRules().Skip(1)] };

        Assert.False(ScanFilterConfig.IsOnlyTheSeededRules(filters));
    }

    // Spec-writer question 7, ruled as written: the comparison is on the whole rule, not the id,
    // so a rewritten pattern under a seeded id takes the notice down.
    [Fact]
    public void AnEditedSeededPattern_HidesTheNotice()
    {
        var rules = ScanFilterConfig.SeededRules().ToList();
        rules[0] = rules[0] with { Pattern = "something-else" };

        Assert.False(ScanFilterConfig.IsOnlyTheSeededRules(Seeded() with { NameRules = rules }));
    }

    [Fact]
    public void ADisabledSeededRule_HidesTheNotice()
    {
        var rules = ScanFilterConfig.SeededRules().ToList();
        rules[2] = rules[2] with { Enabled = false };

        Assert.False(ScanFilterConfig.IsOnlyTheSeededRules(Seeded() with { NameRules = rules }));
    }

    [Fact]
    public void AnIncludePath_HidesTheNotice()
    {
        Assert.False(ScanFilterConfig.IsOnlyTheSeededRules(
            Seeded() with { IncludePaths = [@"C:\Astro\2025"] }));
    }

    [Fact]
    public void AnExcludePath_HidesTheNotice()
    {
        Assert.False(ScanFilterConfig.IsOnlyTheSeededRules(
            Seeded() with { ExcludePaths = [@"C:\Astro\rejected"] }));
    }

    // Spec 12.2: "it returns if they are restored to exactly that set."
    [Fact]
    public void RestoringExactlyTheSeededFive_ShowsItAgain()
    {
        var edited = Seeded() with { NameRules = [.. ScanFilterConfig.SeededRules().Skip(1)] };
        Assert.False(ScanFilterConfig.IsOnlyTheSeededRules(edited));

        // Rebuilt in a different order, to pin that order is not part of the condition.
        var restored = edited with { NameRules = [.. ScanFilterConfig.SeededRules().Reverse()] };
        Assert.True(ScanFilterConfig.IsOnlyTheSeededRules(restored));
    }

    // Polish wave 1, ruling 1: a user who keeps the wizard's five rules can still dismiss the
    // notice. Review on either surface persists scan_filters_reviewed, and the predicate both
    // surfaces read honours it. A failure here is a notice that never goes away for that user.
    [Fact]
    public void AReviewedFlag_HidesTheNoticeEvenWithTheSeededFive()
    {
        var seeded = new GeneralSettings { ScanFilters = Seeded() };
        Assert.True(ScanFilterConfig.ShowsSetupNotice(seeded));
        Assert.False(ScanFilterConfig.ShowsSetupNotice(seeded with { ScanFiltersReviewed = true }));
    }

    // Section 8.1's one-list rule asserted: the predicate's expected set and the wizard's own
    // list are the same five names from the same source (questions.md Q6). The App-side half of
    // this pairing is in SetupWizardViewModelTests.
    [Fact]
    public void TheFiveRules_AreTheWizardsOwnList()
    {
        Assert.Equal(
            new[] { "masters", "WBPP", "calibrated", "WORK_AREA", "PixInsight" },
            ScanFilterConfig.SeededExcludeNames);

        var rules = ScanFilterConfig.SeededRules();
        Assert.Equal(ScanFilterConfig.SeededExcludeNames.Count, rules.Count);
        foreach (var name in ScanFilterConfig.SeededExcludeNames)
        {
            var rule = Assert.Single(rules, r => r.Pattern == name);
            Assert.Equal($"setup-exclude-{name}", rule.Id);
            Assert.Equal("exclude", rule.Action);
            Assert.Equal("substring", rule.Type);
            Assert.Equal("folder", rule.Target);
            Assert.True(rule.Enabled);
        }
    }
}
