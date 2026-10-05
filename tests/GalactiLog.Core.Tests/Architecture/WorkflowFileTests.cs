using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace GalactiLog.Core.Tests.Architecture;

// Phase 10 Task 6, roadmap row 6: dry-run validation of each workflow file under
// .github/workflows against design-spec.md 17.5, following FileSafetyTest's shape (a plain text
// scan, no YAML parser dependency).
//
// Assertions run against normalized content: carriage returns are stripped and runs of two or
// more spaces are collapsed to one, so a reformatting that does not change meaning (a re-indent,
// for example) does not fail the suite. This is done in one helper, NormalizeContent.
public class WorkflowFileTests
{
    private const string BuildTestFile = "build-test.yml";
    private const string ReleaseFile = "release.yml";
    private const string BranchPolicyFile = "branch-merge-policy.yml";
    private const string SelfHostedRunner = "runs-on: [self-hosted, Windows, X64]";
    private const string GitBashPathStep = "name: Put Git Bash on the path\n shell: cmd";

    // ---------------------------------------------------------------------------------------
    // build-test.yml
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void BuildTest_HasNoPushTrigger()
    {
        // Every push runs release.yml, whose Test step is the gate; running build-test as well
        // ran the suite twice on the one runner (CI review 2026-09-25, user ruling on snd alphas).
        var content = ReadNormalized(BuildTestFile);
        Assert.DoesNotContain("push:", content);
    }

    [Fact]
    public void BuildTest_TriggersOnPullRequestToDevAndMain()
    {
        var content = ReadNormalized(BuildTestFile);
        Assert.Contains("pull_request:", content);
        Assert.Contains("branches: [dev, main]", content);
    }

    [Fact]
    public void BuildTest_IgnoresMarkdownDocsAndLicensePaths()
    {
        var content = ReadNormalized(BuildTestFile);

        // spec 17.5: paths-ignore on the pull_request trigger, the file's only trigger.
        AssertOccurrenceCount(content, "'**/*.md'", 1);
        AssertOccurrenceCount(content, "'docs/**'", 1);
        AssertOccurrenceCount(content, "'LICENSE'", 1);
    }

    [Fact]
    public void BuildTest_PermissionsAreContentsRead()
    {
        var content = ReadNormalized(BuildTestFile);
        Assert.Contains("permissions:\n contents: read", content);
    }

    [Fact]
    public void BuildTest_ConcurrencyIsPerRef_AndCancelsInProgress()
    {
        var content = ReadNormalized(BuildTestFile);
        Assert.Contains("concurrency:\n group: build-test-${{ github.ref }}\n cancel-in-progress: true", content);
    }

    [Fact]
    public void BuildTest_RunsOnTheSelfHostedWindowsRunner_WithNoBashStepAndNoPathStep()
    {
        var content = ReadNormalized(BuildTestFile);
        Assert.Contains(SelfHostedRunner, content);
        // Job default is cmd: the runner account's execution policy is Restricted, so the
        // implicit powershell shell cannot load the script GitHub writes for a run step.
        Assert.Contains("defaults:\n run:\n shell: cmd", content);
        Assert.DoesNotContain("shell: powershell", content);
        Assert.DoesNotContain("shell: pwsh", content);
        Assert.DoesNotContain("shell: bash", content);
        Assert.DoesNotContain(GitBashPathStep, content);
    }

    [Fact]
    public void BuildTest_StepsAreInSpecOrder()
    {
        var content = ReadNormalized(BuildTestFile);
        AssertAscendingOrder(
            content,
            "build-test.yml step order (spec 17.5): checkout, setup-dotnet, restore, build, test",
            "actions/checkout@v4",
            "actions/setup-dotnet@v4",
            "dotnet restore",
            "dotnet build --no-restore -c Release",
            "dotnet test --no-build -c Release");
    }

    [Fact]
    public void BuildTest_TestStepHasNoTrxLoggerAndNoArtifactUpload()
    {
        // CI review 2026-09-25 item 9: nothing read the trx, and a failed test's name is already
        // in the job log. The test line is asserted to its end so a re-added logger flag fails.
        var content = ReadNormalized(BuildTestFile);
        Assert.Contains("- run: dotnet test --no-build -c Release\n", content);
        Assert.DoesNotContain("--logger trx", content);
        Assert.DoesNotContain("actions/upload-artifact", content);
    }

    // ---------------------------------------------------------------------------------------
    // release.yml
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Release_TriggersOnPushToTheThreeBranches_OnDispatch_AndNotOnPullRequest()
    {
        // The user wants an alpha per snd push. The suite still runs once per push because
        // build-test.yml has no push trigger.
        var content = ReadNormalized(ReleaseFile);
        Assert.Contains("push:\n branches: [snd, dev, main]", content);
        Assert.Contains("workflow_dispatch:", content);
        Assert.DoesNotContain("pull_request:", content);
    }

    [Fact]
    public void Release_PermissionsAreContentsWrite()
    {
        var content = ReadNormalized(ReleaseFile);
        Assert.Contains("permissions:\n contents: write", content);
    }

    [Fact]
    public void Release_ConcurrencyIsPerRef_AndDoesNotCancelInProgress()
    {
        var content = ReadNormalized(ReleaseFile);
        Assert.True(
            content.Contains("concurrency:\n group: release-${{ github.ref }}\n cancel-in-progress: false"),
            "release.yml's concurrency group must be per-ref with cancel-in-progress: false " +
            "(spec 17.5): two pushes must not derive the same version.");
    }

    [Fact]
    public void Release_CheckoutUsesFetchDepthZero()
    {
        var content = ReadNormalized(ReleaseFile);
        Assert.Contains("actions/checkout@v4\n with:\n fetch-depth: 0", content);
    }

    [Fact]
    public void Release_FetchesTagsForced()
    {
        var content = ReadNormalized(ReleaseFile);
        Assert.Contains("git fetch --tags --force", content);
    }

    [Fact]
    public void Release_StepsAreInSpecOrder()
    {
        var content = ReadNormalized(ReleaseFile);
        AssertAscendingOrder(
            content,
            "release.yml step order (spec 17.5): checkout, fetch tags, setup-dotnet, derive " +
            "version, test, publish, install vpk, download previous release, pack, tag, upload, " +
            "notes, prune",
            "actions/checkout@v4",
            "name: Fetch tags",
            "actions/setup-dotnet@v4",
            "name: Derive version",
            "name: Test",
            "name: Publish",
            "name: Install vpk",
            "name: Download previous release",
            "name: Pack",
            "name: Tag release",
            "name: Upload to GitHub Releases",
            "name: Generate release notes",
            "name: Prune old prereleases");
    }

    [Fact]
    public void Release_DownloadsThePreviousReleaseBeforePack_AndToleratesAChannelsFirstRelease()
    {
        // CI review 2026-09-25 item 7: vpk pack emits a delta only when the channel's previous
        // full package is in its output directory, so the download runs after Install vpk (it
        // needs vpk on PATH) and before Pack. Step-scoped: continue-on-error and the channel
        // and prerelease flags must sit in this step's own block, not somewhere else in the file.
        var raw = ReadRaw(ReleaseFile);
        var block = NormalizeContent(ExtractStepBlock(raw, "name: Download previous release"));
        Assert.Contains("continue-on-error: true", block);
        Assert.Contains("vpk download github", block);
        Assert.Contains("--channel \"${{ steps.version.outputs.channel }}\"", block);
        Assert.Contains("$PRE_FLAG", block);
    }

    [Fact]
    public void Release_TestsRunBeforePublish()
    {
        var content = ReadNormalized(ReleaseFile);
        AssertAscendingOrder(
            content,
            "release.yml spec 17.5 step 5: a failing suite never publishes",
            "name: Test",
            "name: Publish");
    }

    [Fact]
    public void Release_TagsOnlyAfterASuccessfulPack()
    {
        var content = ReadNormalized(ReleaseFile);
        AssertAscendingOrder(
            content,
            "release.yml spec 17.5 step 9: the tag is pushed only after a successful pack",
            "name: Pack",
            "name: Tag release");
    }

    [Fact]
    public void Release_UploadsWithVpkUploadGithub()
    {
        var content = ReadNormalized(ReleaseFile);
        Assert.Contains("vpk upload github", content);
    }

    [Fact]
    public void Release_HasNoGhReleaseCreate()
    {
        // spec 17.5: "There is no gh release create anywhere in the workflow." Scans all three
        // files, not just release.yml: a stray gh release create in any of them would create a
        // second, competing release for the same tag.
        foreach (var file in new[] { BuildTestFile, ReleaseFile, BranchPolicyFile })
        {
            var content = ReadNormalized(file);
            Assert.True(
                !content.Contains("gh release create"),
                $"{file} must not contain 'gh release create' (spec 17.5): vpk upload github is " +
                "the only release-creating command.");
        }
    }

    [Fact]
    public void Release_VpkUploadGithubAppearsExactlyOnce()
    {
        var content = ReadNormalized(ReleaseFile);
        AssertOccurrenceCount(content, "vpk upload github", 1);
    }

    [Fact]
    public void Release_GeneratesNotesByEditingTheVpkCreatedRelease()
    {
        // Inverts the former Release_HasNoGenerateNotesFlag (CI review 2026-09-25 item 8). That
        // pin guarded against gh release create, whose --generate-notes flag would have made a
        // second release; gh release edit runs against the release vpk already created, so notes
        // are generated without a second create. The runner's gh has no --generate-notes on edit,
        // so the body comes from the generate-notes API endpoint through --notes-file -.
        var raw = ReadRaw(ReleaseFile);
        var block = NormalizeContent(ExtractStepBlock(raw, "name: Generate release notes"));
        Assert.Contains("releases/generate-notes", block);
        Assert.Contains("gh release edit \"${{ steps.version.outputs.version }}\"", block);
        Assert.Contains("--notes-file -", block);
        Assert.DoesNotContain("--generate-notes", block);
    }

    [Fact]
    public void Release_PruneKeepsTwoAlphaTwoRcAndFiveStable()
    {
        var content = ReadNormalized(ReleaseFile);
        // The full call, not the bare "alpha 2" / "rc 2" substrings: a bare substring would also
        // be satisfied by a comment containing those words while the actual invocation carried
        // different numbers.
        Assert.Contains("prune_prerelease alpha 2", content);
        Assert.Contains("prune_prerelease rc 2", content);
        Assert.Contains("tail -n +6", content);
    }

    [Fact]
    public void Release_VpkToolInstallIsPinned()
    {
        // Task 5's change, asserted here because this is the file where workflow assertions
        // live: the installed vpk version must equal the Velopack PackageVersion in
        // Directory.Packages.props, or CI could pack a format the pinned library cannot read.
        var repoRoot = FindRepoRoot();
        var packagesPropsPath = Path.Combine(repoRoot, "Directory.Packages.props");
        Assert.True(File.Exists(packagesPropsPath), $"Directory.Packages.props not found at {packagesPropsPath}");

        var packagesProps = File.ReadAllText(packagesPropsPath);
        var match = Regex.Match(packagesProps, "<PackageVersion\\s+Include=\"Velopack\"\\s+Version=\"([^\"]+)\"");
        Assert.True(match.Success, "Velopack PackageVersion entry not found in Directory.Packages.props");
        var expectedVersion = match.Groups[1].Value;

        var content = ReadNormalized(ReleaseFile);
        Assert.Contains($"vpk --version {expectedVersion}", content);
    }

    [Fact]
    public void Release_PublishOutputAndPackDirAgree()
    {
        // Task 5's change, asserted here for the same reason as Release_VpkToolInstallIsPinned:
        // the publish output directory and the pack input directory must name the same path.
        // Isolated to each step's own block (the way BranchPolicy_PassesRefsThroughEnv... isolates
        // its run: block) rather than matched against the whole file, so an -o flag added to an
        // earlier step could not be read as this comparison's value in either direction.
        var raw = ReadRaw(ReleaseFile);

        var publishBlock = ExtractStepBlock(raw, "name: Publish");
        var publishMatch = Regex.Match(publishBlock, @"-o\s+(\S+)");
        Assert.True(publishMatch.Success, "release.yml's Publish step has no -o flag");

        var packBlock = ExtractStepBlock(raw, "name: Pack");
        var packDirMatch = Regex.Match(packBlock, @"--packDir\s+(\S+)");
        Assert.True(packDirMatch.Success, "release.yml's Pack step has no --packDir flag");

        Assert.Equal(publishMatch.Groups[1].Value, packDirMatch.Groups[1].Value);
    }

    [Fact]
    public void Release_PublishIncludesSourceRevisionId()
    {
        // Q13 (Phase 10 questions.md, with the Q13 correction): the workflow keeps an explicit
        // -p:SourceRevisionId flag on the publish step rather than relying on an SDK default.
        var content = ReadNormalized(ReleaseFile);
        Assert.Contains("-p:SourceRevisionId=${{ github.sha }}", content);
    }

    [Fact]
    public void Release_DerivesTheVersionThroughTheScript()
    {
        var content = ReadNormalized(ReleaseFile);
        Assert.Contains("tools/derive-version.sh", content);

        // The inline stable-tag filter must not exist a second time in this file: if it does,
        // the derivation exists in two places and they can drift apart.
        Assert.DoesNotContain("grep -E '^[0-9]+\\.[0-9]+\\.[0-9]+$'", content);
    }

    // ---------------------------------------------------------------------------------------
    // branch-merge-policy.yml
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void BranchPolicy_TriggersOnPullRequestToMainAndDev_WithNoPathFilter()
    {
        var content = ReadNormalized(BranchPolicyFile);
        Assert.Contains("pull_request:\n branches: [main, dev]", content);
        Assert.DoesNotContain("paths-ignore", content);
    }

    [Fact]
    public void BranchPolicy_PermissionsAreAnExplicitEmptySet()
    {
        var content = ReadNormalized(BranchPolicyFile);
        Assert.Contains("permissions: {}", content);
    }

    [Fact]
    public void BranchPolicy_RunsOnTheSelfHostedWindowsRunner_WithNoCheckoutAndNoActions()
    {
        var content = ReadNormalized(BranchPolicyFile);
        Assert.Contains(SelfHostedRunner, content);
        Assert.DoesNotContain("actions/checkout", content);
        Assert.DoesNotContain("uses:", content);
    }

    [Fact]
    public void BranchPolicy_PassesRefsThroughEnv_NeverInterpolatedIntoTheScript()
    {
        // Script-injection guard, the one security-relevant assertion in this file: github.base_ref
        // and github.head_ref must reach the shell only through env vars, never interpolated
        // directly into the run: block, which is where the check itself must run.
        var raw = ReadRaw(BranchPolicyFile);
        var runIndex = raw.IndexOf("run: |", StringComparison.Ordinal);
        Assert.True(runIndex >= 0, "branch-merge-policy.yml has no 'run: |' block");
        var runBlock = raw.Substring(runIndex);

        Assert.Contains("$BASE", runBlock);
        Assert.Contains("$HEAD", runBlock);
        Assert.DoesNotContain("${{ github.base_ref }}", runBlock);
        Assert.DoesNotContain("${{ github.head_ref }}", runBlock);
    }

    [Fact]
    public void BranchPolicy_MainAcceptsOnlyDev_AndDevAcceptsOnlySnd()
    {
        var content = ReadRaw(BranchPolicyFile);
        Assert.Contains("\"$BASE\" = \"main\"", content);
        Assert.Contains("\"$HEAD\" != \"dev\"", content);
        Assert.Contains("\"$BASE\" = \"dev\"", content);
        Assert.Contains("\"$HEAD\" != \"snd\"", content);
    }

    // ---------------------------------------------------------------------------------------
    // All three files
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Workflows_AreExactlyTheThreeSpecFiles()
    {
        var repoRoot = FindRepoRoot();
        var workflowsDir = Path.Combine(repoRoot, ".github", "workflows");
        // The full directory listing, not just "*.yml": GitHub Actions also runs a ".yaml"
        // workflow, so a *.yml-only enumeration would let a fourth file with that extension
        // appear silently, defeating the point of this test.
        var actual = Directory.EnumerateFiles(workflowsDir)
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var expected = new[] { BranchPolicyFile, BuildTestFile, ReleaseFile }
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Workflows_EveryJobRunsOnTheSelfHostedWindowsRunner()
    {
        foreach (var file in new[] { BuildTestFile, ReleaseFile, BranchPolicyFile })
        {
            var content = ReadNormalized(file);
            var jobs = Regex.Matches(content, "runs-on:").Count;
            Assert.True(jobs >= 1, $"{file} has no runs-on line");
            AssertOccurrenceCount(content, SelfHostedRunner, jobs);
        }
    }

    [Fact]
    public void Workflows_PutGitBashOnThePathWithCmd_BeforeTheFirstBashStep()
    {
        // The self-hosted runner has no pwsh and carries only Git\cmd on PATH, so every bash step
        // depends on this step having run earlier in the file's single job. It is a cmd step: the
        // runner account's Windows PowerShell execution policy is Restricted, so a powershell step
        // cannot load the script GitHub writes for it.
        foreach (var file in new[] { ReleaseFile, BranchPolicyFile })
        {
            var content = ReadNormalized(file);
            AssertOccurrenceCount(content, "runs-on:", 1);
            Assert.DoesNotContain("shell: pwsh", content);
            Assert.DoesNotContain("shell: powershell", content);
            AssertAscendingOrder(
                content,
                $"{file}: the Git Bash path step must precede the first bash step",
                GitBashPathStep,
                "shell: bash");
        }
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    // Isolates one step's text, from its "name: X" (or other) marker up to the next step's list
    // item marker ("\n      - ") or the end of the file. Used where a step-scoped flag (such as
    // -o or --packDir) could otherwise be matched against an unrelated step.
    private static string ExtractStepBlock(string raw, string stepMarker)
    {
        var start = raw.IndexOf(stepMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"step marker '{stepMarker}' not found");
        var nextStepIndex = raw.IndexOf("\n      - ", start + stepMarker.Length, StringComparison.Ordinal);
        var end = nextStepIndex >= 0 ? nextStepIndex : raw.Length;
        return raw.Substring(start, end - start);
    }

    private static void AssertOccurrenceCount(string content, string needle, int expectedCount)
    {
        var actualCount = Regex.Matches(content, Regex.Escape(needle)).Count;
        Assert.True(
            actualCount == expectedCount,
            $"expected '{needle}' to occur {expectedCount} time(s) but found {actualCount}");
    }

    private static void AssertAscendingOrder(string content, string message, params string[] markersInOrder)
    {
        var lastIndex = -1;
        var lastMarker = "(start of file)";
        foreach (var marker in markersInOrder)
        {
            var index = content.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(index >= 0, $"{message}: marker '{marker}' not found");
            Assert.True(
                index > lastIndex,
                $"{message}: '{marker}' must appear after '{lastMarker}', but was found at index {index} <= {lastIndex}");
            lastIndex = index;
            lastMarker = marker;
        }
    }

    private static string ReadRaw(string workflowFileName)
    {
        var repoRoot = FindRepoRoot();
        var path = Path.Combine(repoRoot, ".github", "workflows", workflowFileName);
        Assert.True(File.Exists(path), $"workflow file not found: {path}");
        return File.ReadAllText(path);
    }

    private static string ReadNormalized(string workflowFileName)
    {
        return NormalizeContent(ReadRaw(workflowFileName));
    }

    private static string NormalizeContent(string content)
    {
        var noCarriageReturns = content.Replace("\r\n", "\n").Replace("\r", "\n");
        return Regex.Replace(noCarriageReturns, " {2,}", " ");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GalactiLog.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new DirectoryNotFoundException("GalactiLog.sln not found above test output directory.");
    }
}
