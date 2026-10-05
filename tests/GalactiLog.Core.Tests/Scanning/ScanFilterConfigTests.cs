using GalactiLog.Core.Scanning;
using Xunit;

namespace GalactiLog.Core.Tests.Scanning;

public class ScanFilterConfigTests
{
    private const string Root = @"C:\Astro";
    private static readonly string[] Roots = [Root];

    private static NameRule Rule(string action, string type, string pattern, string target, bool enabled = true, string id = "r1") => new()
    {
        Id = id,
        Action = action,
        Type = type,
        Pattern = pattern,
        Target = target,
        Enabled = enabled,
    };

    // --- EffectiveRoots ---

    [Fact]
    public void EffectiveRoots_NoIncludePaths_ReturnsScanRoots()
    {
        var config = ScanFilterConfig.Empty;

        var result = config.EffectiveRoots(Roots);

        Assert.Equal(Roots, result);
    }

    [Fact]
    public void EffectiveRoots_DropsExactDuplicates()
    {
        var config = new ScanFilterConfig { IncludePaths = [@"C:\Astro\A", @"C:\Astro\A"] };

        var result = config.EffectiveRoots(Roots);

        Assert.Single(result);
        Assert.Equal(@"C:\Astro\A", result[0]);
    }

    [Fact]
    public void EffectiveRoots_DropsNestedIncludePaths_PreservingOrder()
    {
        var config = new ScanFilterConfig
        {
            IncludePaths = [@"C:\Astro\A", @"C:\Astro\A\Sub", @"C:\Astro\B"],
        };

        var result = config.EffectiveRoots(Roots);

        Assert.Equal([@"C:\Astro\A", @"C:\Astro\B"], result);
    }

    // --- ShouldWalkDir ---

    [Fact]
    public void ShouldWalkDir_ExcludePathEntry_Prunes()
    {
        var config = new ScanFilterConfig { ExcludePaths = [@"C:\Astro\WORK_AREA"] };

        Assert.False(config.ShouldWalkDir(@"C:\Astro\WORK_AREA\Sub", Root));
    }

    [Fact]
    public void ShouldWalkDir_FolderExcludeRule_MatchesAncestorSegment()
    {
        var config = new ScanFilterConfig
        {
            NameRules = [Rule("exclude", "substring", "Lights", "folder")],
        };

        // "Lights" is an ancestor segment, not the directory's own leaf name.
        Assert.False(config.ShouldWalkDir(@"C:\Astro\Lights\WORK_AREA", Root));
    }

    [Fact]
    public void ShouldWalkDir_PathOutsideRoot_FallsBackToOwnName()
    {
        var config = new ScanFilterConfig
        {
            NameRules = [Rule("exclude", "substring", "WORK_AREA", "folder")],
        };

        // dirPath is not under root at all; should_walk_dir falls back to a single-segment
        // list containing just the directory's own name rather than excluding outright.
        Assert.False(config.ShouldWalkDir(@"D:\Other\WORK_AREA", Root));
        Assert.True(config.ShouldWalkDir(@"D:\Other\Keepers", Root));
    }

    // --- ShouldIncludeFile ---

    [Fact]
    public void ShouldIncludeFile_ExcludePathEntry_Excludes()
    {
        var config = new ScanFilterConfig { ExcludePaths = [@"C:\Astro\WORK_AREA"] };

        Assert.False(config.ShouldIncludeFile(@"C:\Astro\WORK_AREA\light001.fits", Root));
    }

    [Fact]
    public void ShouldIncludeFile_IncludePathNarrowing_ExcludesOutsideEveryIncludePath()
    {
        var config = new ScanFilterConfig { IncludePaths = [@"C:\Astro\Targets"] };

        Assert.False(config.ShouldIncludeFile(@"C:\Astro\Other\light001.fits", Root));
        Assert.True(config.ShouldIncludeFile(@"C:\Astro\Targets\light001.fits", Root));
    }

    [Fact]
    public void ShouldIncludeFile_PathOutsideRoot_ExcludesOutright()
    {
        var config = new ScanFilterConfig
        {
            NameRules = [Rule("include", "glob", "*.fits", "file")],
        };

        // Unlike ShouldWalkDir there is no single-segment fallback for a file: outside root
        // means excluded, full stop, regardless of any rule that would otherwise match.
        Assert.False(config.ShouldIncludeFile(@"D:\Other\light001.fits", Root));
    }

    [Fact]
    public void ShouldIncludeFile_FileExcludeRule_MatchesFilename()
    {
        var config = new ScanFilterConfig
        {
            NameRules = [Rule("exclude", "glob", "cal_*.fits", "file")],
        };

        Assert.False(config.ShouldIncludeFile(@"C:\Astro\Lights\cal_bias.fits", Root));
        Assert.True(config.ShouldIncludeFile(@"C:\Astro\Lights\light001.fits", Root));
    }

    [Fact]
    public void ShouldIncludeFile_FolderExcludeRule_MatchesAncestorSegment()
    {
        var config = new ScanFilterConfig
        {
            NameRules = [Rule("exclude", "substring", "WORK_AREA", "folder")],
        };

        Assert.False(config.ShouldIncludeFile(@"C:\Astro\WORK_AREA\light001.fits", Root));
    }

    [Fact]
    public void ShouldIncludeFile_FileIncludeNarrowing_ExcludesWhenNoIncludeRuleMatches()
    {
        var config = new ScanFilterConfig
        {
            NameRules = [Rule("include", "glob", "m31_*.fits", "file")],
        };

        Assert.False(config.ShouldIncludeFile(@"C:\Astro\Lights\m42_001.fits", Root));
        Assert.True(config.ShouldIncludeFile(@"C:\Astro\Lights\m31_001.fits", Root));
    }

    [Fact]
    public void ShouldIncludeFile_FolderIncludeNarrowing_ExcludesWhenNoIncludeRuleMatches()
    {
        var config = new ScanFilterConfig
        {
            NameRules = [Rule("include", "substring", "Targets", "folder")],
        };

        Assert.False(config.ShouldIncludeFile(@"C:\Astro\Other\light001.fits", Root));
        Assert.True(config.ShouldIncludeFile(@"C:\Astro\Targets\light001.fits", Root));
    }

    [Fact]
    public void ShouldIncludeFile_DisabledIncludeRule_IsANoOp()
    {
        // Deliberate divergence from scan_filters.py (spec 10.2): disabling the only include
        // rule must mean the rule does nothing (as if it were absent), not that every file
        // is excluded because the disabled rule never matches.
        var config = new ScanFilterConfig
        {
            NameRules = [Rule("include", "glob", "m31_*.fits", "file", enabled: false)],
        };

        Assert.True(config.ShouldIncludeFile(@"C:\Astro\Lights\m42_001.fits", Root));
    }

    [Fact]
    public void ShouldIncludeFile_GlobVsSubstringVsRegex_EachSemanticsDistinct()
    {
        var glob = new ScanFilterConfig { NameRules = [Rule("exclude", "glob", "cal_*.fits", "file")] };
        var substring = new ScanFilterConfig { NameRules = [Rule("exclude", "substring", "cal_", "file")] };
        var regex = new ScanFilterConfig { NameRules = [Rule("exclude", "regex", @"^cal_", "file")] };

        // Glob is a full-string, case-sensitive match: a prefix match on a longer name fails.
        Assert.True(glob.ShouldIncludeFile(@"C:\Astro\light_cal_bias.fits", Root));
        // Substring is case-insensitive containment: the same longer name is caught.
        Assert.False(substring.ShouldIncludeFile(@"C:\Astro\light_cal_bias.fits", Root));
        // Regex is an anchored (here, "^") search: a non-prefix occurrence is not caught.
        Assert.True(regex.ShouldIncludeFile(@"C:\Astro\light_cal_bias.fits", Root));
        Assert.False(regex.ShouldIncludeFile(@"C:\Astro\cal_bias.fits", Root));
    }

    // --- Validate ---

    [Fact]
    public void Validate_IncludePathOutsideEveryScanRoot_Throws()
    {
        var config = new ScanFilterConfig { IncludePaths = [@"D:\Other"] };

        Assert.Throws<ScanFilterValidationException>(() => config.Validate(Roots));
    }

    [Fact]
    public void Validate_ExcludePathOutsideEveryScanRoot_Throws()
    {
        var config = new ScanFilterConfig { ExcludePaths = [@"D:\Other"] };

        Assert.Throws<ScanFilterValidationException>(() => config.Validate(Roots));
    }

    [Fact]
    public void Validate_UnrecognizedAction_Throws()
    {
        var config = new ScanFilterConfig { NameRules = [Rule("delete", "glob", "*.fits", "file")] };

        Assert.Throws<ScanFilterValidationException>(() => config.Validate(Roots));
    }

    [Fact]
    public void Validate_UnrecognizedType_Throws()
    {
        var config = new ScanFilterConfig { NameRules = [Rule("exclude", "wildcard", "*.fits", "file")] };

        Assert.Throws<ScanFilterValidationException>(() => config.Validate(Roots));
    }

    [Fact]
    public void Validate_UnrecognizedTarget_Throws()
    {
        var config = new ScanFilterConfig { NameRules = [Rule("exclude", "glob", "*.fits", "directory")] };

        Assert.Throws<ScanFilterValidationException>(() => config.Validate(Roots));
    }

    [Fact]
    public void Validate_UncompilableRegex_Throws()
    {
        var config = new ScanFilterConfig { NameRules = [Rule("exclude", "regex", "(unterminated", "file")] };

        Assert.Throws<ScanFilterValidationException>(() => config.Validate(Roots));
    }

    // Review item 1: reject before any Path.GetFullPath call. A relative value would
    // otherwise resolve silently against the process's current directory, defeating the
    // whole point of a trust-boundary check.
    [Fact]
    public void Validate_ScanRootNotFullyQualified_Throws()
    {
        var config = ScanFilterConfig.Empty;

        Assert.Throws<ScanFilterValidationException>(() => config.Validate(["Astro"]));
    }

    [Fact]
    public void Validate_IncludePathNotFullyQualified_Throws()
    {
        var config = new ScanFilterConfig { IncludePaths = ["Targets"] };

        Assert.Throws<ScanFilterValidationException>(() => config.Validate(Roots));
    }

    [Fact]
    public void Validate_ExcludePathNotFullyQualified_Throws()
    {
        var config = new ScanFilterConfig { ExcludePaths = ["WORK_AREA"] };

        Assert.Throws<ScanFilterValidationException>(() => config.Validate(Roots));
    }

    // Review item 2: an empty entry is already caught by the fully-qualified check above
    // (Path.IsPathFullyQualified("") is false, no exception); this covers the remaining
    // Path.GetFullPath call, which can still throw for an otherwise drive-rooted-looking
    // string that contains an embedded NUL character.
    [Fact]
    public void Validate_EmptyIncludePath_Throws()
    {
        var config = new ScanFilterConfig { IncludePaths = [""] };

        Assert.Throws<ScanFilterValidationException>(() => config.Validate(Roots));
    }

    [Fact]
    public void Validate_PathWithNulChar_Throws()
    {
        var config = new ScanFilterConfig { IncludePaths = ["C:\\Astro\\bad\0name"] };

        Assert.Throws<ScanFilterValidationException>(() => config.Validate(Roots));
    }

    // --- the scan-root rule (FIXER LIST F10) ---

    // Two UI surfaces refused a duplicate or nested root at the moment it was typed and this
    // validator did not know the rule at all, so a nested pair reaching the document any other way
    // saved cleanly and then walked and ingested every file under the inner root twice.
    [Theory]
    [InlineData(@"C:\Astro", @"C:\Astro\Captures")]
    [InlineData(@"C:\Astro\Captures", @"C:\Astro")]
    [InlineData(@"C:\Astro", @"C:\Astro")]
    [InlineData(@"C:\Astro", @"C:\astro\captures")]
    [InlineData(@"C:\Astro", @"C:\Astro\")]
    public void Validate_DuplicateOrNestedScanRoots_Throws(string first, string second)
    {
        var exception = Assert.Throws<ScanFilterValidationException>(
            () => ScanFilterConfig.Empty.Validate([first, second]));

        Assert.Contains("'" + second + "'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_DisjointScanRoots_DoNotThrow()
    {
        var exception = Record.Exception(
            () => ScanFilterConfig.Empty.Validate([@"C:\Astro", @"D:\Nas\Astro", @"C:\AstroOther"]));

        Assert.Null(exception);
    }

    // The one sentence both the Settings Library tab and the setup wizard's step 1 show, and the
    // one Validate throws, so the same gesture is refused with the same text wherever it is made.
    [Fact]
    public void RefuseScanRoot_ADisjointRoot_IsAccepted()
        => Assert.Null(ScanFilterConfig.RefuseScanRoot(@"D:\Nas\Astro", [@"C:\Astro"]));

    [Fact]
    public void RefuseScanRoot_ADuplicate_NamesIt()
        => Assert.Equal(
            @"'C:\Astro' is already a library folder.",
            ScanFilterConfig.RefuseScanRoot(@"C:\Astro", [@"C:\Astro"]));

    [Theory]
    [InlineData(@"C:\Astro\Captures", @"C:\Astro")]
    [InlineData(@"C:\Astro", @"C:\Astro\Captures")]
    public void RefuseScanRoot_ANestedPair_SaysRootsMayNotBeNested(string candidate, string existing)
    {
        var message = ScanFilterConfig.RefuseScanRoot(candidate, [existing]);

        Assert.NotNull(message);
        Assert.Contains("Library folders may not be nested", message, StringComparison.Ordinal);
        Assert.Contains("'" + existing + "'", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"Astro\Captures")]
    [InlineData("")]
    [InlineData(@"C:Astro")]
    public void RefuseScanRoot_ARelativeOrDriveRelativePath_SaysAbsolutePath(string candidate)
    {
        var message = ScanFilterConfig.RefuseScanRoot(candidate, []);

        Assert.NotNull(message);
        Assert.Contains("absolute path", message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefuseScanRoot_AnUnusableExistingEntry_IsSkippedRatherThanReportedTwice()
    {
        // An existing entry that is not a usable path carries its own refusal; saying something
        // about it again here would say nothing new.
        Assert.Null(ScanFilterConfig.RefuseScanRoot(@"C:\Astro", [@"Astro\Relative"]));
    }

    [Fact]
    public void Validate_ValidConfig_DoesNotThrow()
    {
        var config = new ScanFilterConfig
        {
            IncludePaths = [@"C:\Astro\Targets"],
            ExcludePaths = [@"C:\Astro\WORK_AREA"],
            NameRules = [Rule("exclude", "regex", @"^cal_", "file")],
        };

        var exception = Record.Exception(() => config.Validate(Roots));

        Assert.Null(exception);
    }

    // --- TestPath ---

    [Fact]
    public void TestPath_Included_ReturnsMatchedIncludeRuleIds()
    {
        var config = new ScanFilterConfig
        {
            NameRules = [Rule("include", "glob", "m31_*.fits", "file", id: "inc1")],
        };

        var result = config.TestPath(@"C:\Astro\m31_001.fits", Roots, ScanFilterConfig.PathKind.File);

        Assert.Equal("included", result.Verdict);
        Assert.Equal(["inc1"], result.MatchedRuleIds);
    }

    [Fact]
    public void TestPath_ExcludedByPath_OutsideEveryRoot()
    {
        var config = ScanFilterConfig.Empty;

        var result = config.TestPath(@"D:\Other\light001.fits", Roots, ScanFilterConfig.PathKind.File);

        Assert.Equal("excluded_by_path", result.Verdict);
        Assert.Empty(result.MatchedRuleIds);
    }

    [Fact]
    public void TestPath_ExcludedByPath_OutsideEveryIncludePath()
    {
        var config = new ScanFilterConfig { IncludePaths = [@"C:\Astro\Targets"] };

        var result = config.TestPath(@"C:\Astro\Other\light001.fits", Roots, ScanFilterConfig.PathKind.File);

        Assert.Equal("excluded_by_path", result.Verdict);
    }

    [Fact]
    public void TestPath_ExcludedByRule_ReturnsFirstMatchingExcludeRuleId()
    {
        var config = new ScanFilterConfig
        {
            NameRules =
            [
                Rule("exclude", "glob", "cal_*.fits", "file", id: "exc1"),
            ],
        };

        var result = config.TestPath(@"C:\Astro\cal_bias.fits", Roots, ScanFilterConfig.PathKind.File);

        Assert.Equal("excluded_by_rule", result.Verdict);
        Assert.Equal(["exc1"], result.MatchedRuleIds);
    }

    [Fact]
    public void TestPath_ExcludedByMissingInclude()
    {
        var config = new ScanFilterConfig
        {
            NameRules = [Rule("include", "glob", "m31_*.fits", "file")],
        };

        var result = config.TestPath(@"C:\Astro\m42_001.fits", Roots, ScanFilterConfig.PathKind.File);

        Assert.Equal("excluded_by_missing_include", result.Verdict);
    }

    [Fact]
    public void TestPath_AutoKind_UsesOnDiskTypeWhenPathExists()
    {
        var tempRoot = Directory.CreateTempSubdirectory("GalactiLogScanFilterTests_");
        try
        {
            // A real directory whose name looks like it has an extension: auto mode must
            // trust Directory.Exists over the extension heuristic and treat it as a folder.
            var dottedDir = Directory.CreateDirectory(Path.Combine(tempRoot.FullName, "M31.session"));
            var config = new ScanFilterConfig
            {
                NameRules = [Rule("include", "glob", "*.session", "file", id: "should-not-narrow-a-folder")],
            };
            var scanRoots = new[] { tempRoot.FullName };

            var result = config.TestPath(dottedDir.FullName, scanRoots, ScanFilterConfig.PathKind.Auto);

            // Evaluated as a folder (no filename component), so the file-target include
            // rule above cannot match it and does not narrow the folder verdict either.
            Assert.Equal("included", result.Verdict);
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public void TestPath_AutoKind_FallsBackToExtensionHeuristicWhenMissing()
    {
        var config = ScanFilterConfig.Empty;

        var fileResult = config.TestPath(@"C:\Astro\does_not_exist.fits", Roots, ScanFilterConfig.PathKind.Auto);
        var folderResult = config.TestPath(@"C:\Astro\does_not_exist", Roots, ScanFilterConfig.PathKind.Auto);

        Assert.Equal("included", fileResult.Verdict);
        Assert.Equal("included", folderResult.Verdict);
    }

    [Fact]
    public void TestPath_NeverTouchesDiskBeforeConfinementSucceeds()
    {
        var config = ScanFilterConfig.Empty;

        // A NUL byte makes Path.GetFullPath throw; confinement must catch that and report
        // the same verdict as any other out-of-root path rather than letting the exception
        // escape (which would otherwise make this tool double as an existence/validity
        // oracle for arbitrary host input).
        var result = config.TestPath("C:\\Astro\\bad\0name.fits", Roots, ScanFilterConfig.PathKind.File);

        Assert.Equal("excluded_by_path", result.Verdict);
    }

    // --- AcceptsFile (review item 10) ---
    // The one admission test WatcherService and ScanCoordinator.RunTargetedAsync both call,
    // so a file one accepts is never one the other silently drops.

    [Theory]
    [InlineData(@"C:\Astro\m31_001.fits", true)]
    [InlineData(@"C:\Astro\sub\m31_001.xisf", true)]
    [InlineData(@"C:\Astro\NINA.log", false)]           // not a supported frame format
    [InlineData(@"C:\Astro\ImageMetaData.csv", false)]  // read during ingest, never ingested itself
    [InlineData(@"D:\Other\m31_001.fits", false)]       // outside every configured root
    public void AcceptsFile_CombinesFormatSupportAndTheFilterDecision(string path, bool expected)
    {
        Assert.Equal(expected, ScanFilterConfig.Empty.AcceptsFile(path, Roots));
    }

    [Fact]
    public void AcceptsFile_HonoursExcludeRulesAndIncludeNarrowing()
    {
        var excluding = new ScanFilterConfig
        {
            NameRules = [Rule("exclude", "glob", "cal_*.fits", "file", id: "ex1")],
        };
        Assert.False(excluding.AcceptsFile(@"C:\Astro\cal_bias.fits", Roots));
        Assert.True(excluding.AcceptsFile(@"C:\Astro\m31_001.fits", Roots));

        var narrowed = new ScanFilterConfig { IncludePaths = [@"C:\Astro\Keep"] };
        Assert.True(narrowed.AcceptsFile(@"C:\Astro\Keep\m31_001.fits", Roots));
        Assert.False(narrowed.AcceptsFile(@"C:\Astro\Other\m31_001.fits", Roots));
    }
}
