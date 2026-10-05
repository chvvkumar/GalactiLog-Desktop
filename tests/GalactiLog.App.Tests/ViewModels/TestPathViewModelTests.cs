using GalactiLog.App.ViewModels.Settings;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Settings;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Design-spec 10.2's test-a-path tool. The roadmap's third named assertion for this row is that
// "the test-a-path box returns each of the four verdicts", one case per verdict against a
// configuration that produces only that verdict.
//
// Nothing here creates a file or a folder. ScanFilterConfig.TestPath resolves and confines the
// path before any filesystem access, and in auto mode a path that does not exist is decided by
// whether it has an extension, so every case below runs against paths that are not on disk.
public class TestPathViewModelTests
{
    private const string Root = @"C:\Astro\Captures";

    private static TestPathViewModel Create(
        IReadOnlyList<string>? includePaths = null,
        IReadOnlyList<string>? excludePaths = null,
        IReadOnlyList<NameRule>? rules = null,
        IReadOnlyList<string>? scanRoots = null)
    {
        var document = new GeneralSettings
        {
            ScanRoots = [.. scanRoots ?? [Root]],
            ScanFilters = new ScanFilterConfig
            {
                IncludePaths = includePaths ?? [],
                ExcludePaths = excludePaths ?? [],
                NameRules = rules ?? [],
            },
        };

        return new TestPathViewModel(() => document);
    }

    private static NameRule Rule(
        string id, string action, string type, string pattern, string target, bool enabled = true)
        => new()
        {
            Id = id,
            Action = action,
            Type = type,
            Pattern = pattern,
            Target = target,
            Enabled = enabled,
        };

    // ---- the four verdicts --------------------------------------------------------------------

    [Fact]
    public void Test_APathUnderARootWithNoRuleAgainstIt_IsIncluded()
    {
        var box = Create();
        box.Path = Path.Combine(Root, "2025", "M31.fits");

        box.TestCommand.Execute(null);

        Assert.Equal("included", box.Verdict);
        Assert.Equal("Will be scanned", box.VerdictLabel);
        Assert.True(box.IsIncluded);
        Assert.Empty(box.MatchedRuleDescriptions);
    }

    [Fact]
    public void Test_APathUnderAnExcludePath_IsExcludedByPath()
    {
        var box = Create(excludePaths: [Path.Combine(Root, "rejected")]);
        box.Path = Path.Combine(Root, "rejected", "M31.fits");

        box.TestCommand.Execute(null);

        Assert.Equal("excluded_by_path", box.Verdict);
        Assert.Equal("Skipped: excluded by path", box.VerdictLabel);
        Assert.False(box.IsIncluded);
    }

    [Fact]
    public void Test_APathOutsideEveryRoot_IsExcludedByPath()
    {
        var box = Create();
        box.Path = @"E:\Somewhere\Else\M31.fits";

        box.TestCommand.Execute(null);

        Assert.Equal("excluded_by_path", box.Verdict);
        Assert.False(box.IsIncluded);
    }

    [Fact]
    public void Test_APathMatchingAnExcludeNameRule_IsExcludedByRule_AndNamesTheRuleId()
    {
        var box = Create(rules: [Rule("rule-bad", "exclude", "glob", "*_bad.fits", "file")]);
        box.Path = Path.Combine(Root, "2025", "M31_bad.fits");

        box.TestCommand.Execute(null);

        Assert.Equal("excluded_by_rule", box.Verdict);
        Assert.Equal("Skipped: matched an exclude rule", box.VerdictLabel);
        Assert.Equal("exclude glob on file: *_bad.fits", Assert.Single(box.MatchedRuleDescriptions));
    }

    [Fact]
    public void Test_APathWithIncludeRulesThatNoneMatch_IsExcludedByMissingInclude()
    {
        var box = Create(rules: [Rule("rule-m", "include", "regex", @"^M\d+", "file")]);
        box.Path = Path.Combine(Root, "2025", "NGC7331.fits");

        box.TestCommand.Execute(null);

        Assert.Equal("excluded_by_missing_include", box.Verdict);
        Assert.Equal("Skipped: no include rule matched", box.VerdictLabel);
        Assert.False(box.IsIncluded);
    }

    [Theory]
    [InlineData("included", "Will be scanned", "No rule caused this path to be skipped.")]
    [InlineData("excluded_by_path", "Skipped: excluded by path", "A parent folder is listed under Exclude paths.")]
    [InlineData("excluded_by_rule", "Skipped: matched an exclude rule", "A name rule with action=exclude matched.")]
    [InlineData(
        "excluded_by_missing_include",
        "Skipped: no include rule matched",
        "Include rules are set, but none of them matched this path.")]
    public void Test_VerdictLabelsAndHints_MatchTheWebStrings(string verdict, string label, string hint)
    {
        // ScanFiltersPanel.tsx's VERDICT_LABEL and VERDICT_HINT maps, verbatim.
        Assert.Equal(label, TestPathViewModel.VerdictLabels[verdict]);
        Assert.Equal(hint, TestPathViewModel.VerdictHints[verdict]);
    }

    // ---- the extra note -----------------------------------------------------------------------

    [Fact]
    public void Test_IncludedWithAnExcludeRulePresent_AppendsTheExtraNote()
    {
        var box = Create(rules: [Rule("rule-bad", "exclude", "glob", "*_bad.fits", "file")]);
        box.Path = Path.Combine(Root, "2025", "M31.fits");

        box.TestCommand.Execute(null);

        Assert.Equal("included", box.Verdict);
        Assert.Equal(TestPathViewModel.IncludedWithExcludeRulesNote, box.ExtraNote);
    }

    [Fact]
    public void Test_IncludedWithNoExcludeRules_AppendsNoNote()
    {
        var box = Create();
        box.Path = Path.Combine(Root, "2025", "M31.fits");

        box.TestCommand.Execute(null);

        Assert.Equal("included", box.Verdict);
        Assert.Null(box.ExtraNote);
    }

    // ---- describeRule -------------------------------------------------------------------------

    [Fact]
    public void Test_MatchedRuleDescription_IsActionTypeTargetPattern()
    {
        var box = Create(rules: [Rule("rule-folder", "exclude", "substring", "WORK_AREA", "folder")]);
        box.Path = Path.Combine(Root, "WORK_AREA", "M31.fits");

        box.TestCommand.Execute(null);

        Assert.Equal(
            "exclude substring on folder: WORK_AREA",
            Assert.Single(box.MatchedRuleDescriptions));
    }

    [Fact]
    public void Test_MatchedRuleDescription_FallsBackToTheIdPrefix_WhenTheRuleIsGone()
    {
        // DescribeRule is the production path the loop above calls, exercised directly. It cannot
        // be reached through Test() in this port: the verdict and the rule list come from one
        // document read in one call, so TestPath can only name ids that are in NameRules. The
        // fallback exists so an id with no rule behind it renders as something rather than as a
        // blank bullet, and the web needs it for real because its verdict is server-side while
        // its rule list is the browser's edited copy.
        var filters = new ScanFilterConfig
        {
            NameRules = [Rule("abcdefghijklmnop", "include", "glob", "M31*.fits", "file")],
        };

        Assert.Equal(
            "include glob on file: M31*.fits",
            TestPathViewModel.DescribeRule(filters, "abcdefghijklmnop"));
        Assert.Equal("removed-", TestPathViewModel.DescribeRule(filters, "removed-rule-id"));
        Assert.Equal("short", TestPathViewModel.DescribeRule(filters, "short"));
    }

    // ---- the saved configuration, and the kind selector ----------------------------------------

    [Fact]
    public void Test_UsesTheSavedConfiguration_NotTheEditedOne()
    {
        // The tab above this box edits its own collections; the box reads the saved document
        // through its delegate and never sees those edits. Here the saved document is replaced
        // between two runs, which is what a successful Save does.
        var saved = new GeneralSettings[1];
        saved[0] = new GeneralSettings { ScanRoots = [Root] };
        var box = new TestPathViewModel(() => saved[0]);
        box.Path = Path.Combine(Root, "rejected", "M31.fits");

        box.TestCommand.Execute(null);
        Assert.Equal("included", box.Verdict);

        saved[0] = new GeneralSettings
        {
            ScanRoots = [Root],
            ScanFilters = new ScanFilterConfig { ExcludePaths = [Path.Combine(Root, "rejected")] },
        };
        box.TestCommand.Execute(null);

        Assert.Equal("excluded_by_path", box.Verdict);
    }

    [Fact]
    public void Test_KindAuto_TreatsAPathWithAnExtensionAsAFile_AndOneWithoutAsAFolder()
    {
        var box = Create(rules: [Rule("rule-file", "exclude", "substring", "M31", "file")]);

        // Auto plus an extension, on a path that is not on disk: treated as a file, so the file
        // rule applies.
        box.Path = Path.Combine(Root, "2025", "M31.fits");
        box.TestCommand.Execute(null);
        Assert.Equal("excluded_by_rule", box.Verdict);

        // Auto with no extension: treated as a folder, so the file rule does not apply.
        box.Path = Path.Combine(Root, "2025", "M31");
        box.TestCommand.Execute(null);
        Assert.Equal("included", box.Verdict);
    }

    [Fact]
    public void Test_KindFile_AndKindFolder_OverrideTheAutoDecision()
    {
        var box = Create(rules: [Rule("rule-file", "exclude", "substring", "M31", "file")]);
        box.Path = Path.Combine(Root, "2025", "M31");

        box.Kind = ScanFilterConfig.PathKind.File;
        box.TestCommand.Execute(null);
        Assert.Equal("excluded_by_rule", box.Verdict);

        box.Kind = ScanFilterConfig.PathKind.Folder;
        box.TestCommand.Execute(null);
        Assert.Equal("included", box.Verdict);
    }

    [Fact]
    public void KindIndex_TracksKind_InTheEnumsOwnOrder()
    {
        var box = Create();

        Assert.Equal(ScanFilterConfig.PathKind.Auto, box.Kind);
        Assert.Equal(0, box.KindIndex);

        box.KindIndex = 2;
        Assert.Equal(ScanFilterConfig.PathKind.Folder, box.Kind);

        box.Kind = ScanFilterConfig.PathKind.File;
        Assert.Equal(1, box.KindIndex);
    }

    [Fact]
    public void Test_AnEmptyBox_ClearsTheResult()
    {
        var box = Create();
        box.Path = Path.Combine(Root, "2025", "M31.fits");
        box.TestCommand.Execute(null);
        Assert.True(box.HasVerdict);

        box.Path = "   ";
        box.TestCommand.Execute(null);

        Assert.False(box.HasVerdict);
        Assert.Null(box.VerdictLabel);
        Assert.Empty(box.MatchedRuleDescriptions);
    }
}
