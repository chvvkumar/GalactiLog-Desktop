using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace GalactiLog.Core.Tests.Architecture;

// Phase 10 Task 6, roadmap row 6. Drives tools/derive-version.sh through bash with a fixture tag
// list on stdin, exactly the way release.yml's Derive version step does: the script never calls
// git itself, so a fixture tag list is enough to cover all three branches, the anchored stable-tag
// pattern, patch-only auto-increment, and the prerelease counter reset on a base change (spec
// 17.4, design-spec.md).
public class VersionDerivationTests
{
    private const int TimeoutMs = 20000;

    private readonly record struct ScriptResult(int ExitCode, string StandardOutput, string StandardError);

    [Fact]
    public void NoTags_OnMain_YieldsOneZeroZero()
    {
        var (version, channel, prerelease) = Derive("main");
        Assert.Equal("1.0.0", version);
        Assert.Equal("stable", channel);
        Assert.False(prerelease);
    }

    [Fact]
    public void NoTags_OnDev_YieldsOneZeroZeroRcOne()
    {
        var (version, channel, prerelease) = Derive("dev");
        Assert.Equal("1.0.0-rc.1", version);
        Assert.Equal("rc", channel);
        Assert.True(prerelease);
    }

    [Fact]
    public void NoTags_OnSnd_YieldsOneZeroZeroAlphaOne()
    {
        var (version, channel, prerelease) = Derive("snd");
        Assert.Equal("1.0.0-alpha.1", version);
        Assert.Equal("alpha", channel);
        Assert.True(prerelease);
    }

    [Fact]
    public void StableTag_OnMain_IncrementsThePatch()
    {
        var (version, channel, prerelease) = Derive("main", "1.4.0");
        Assert.Equal("1.4.1", version);
        Assert.Equal("stable", channel);
        Assert.False(prerelease);
    }

    [Fact]
    public void StableTag_OnMain_IncrementsOnlyThePatch_NeverTheMinor()
    {
        var (version, _, _) = Derive("main", "1.4.9");
        Assert.Equal("1.4.10", version);
    }

    [Fact]
    public void StableTag_OnSnd_StartsTheAlphaCounterAtOne()
    {
        var (version, channel, prerelease) = Derive("snd", "1.4.0");
        Assert.Equal("1.4.1-alpha.1", version);
        Assert.Equal("alpha", channel);
        Assert.True(prerelease);
    }

    [Fact]
    public void ExistingAlphaTags_OnSnd_ContinueTheCounter()
    {
        var (version, _, _) = Derive("snd", "1.4.0", "1.4.1-alpha.1", "1.4.1-alpha.2");
        Assert.Equal("1.4.1-alpha.3", version);
    }

    [Fact]
    public void AlphaCounter_SortsNumerically_NotLexically()
    {
        // 9 then 10, not 1 then 2: a lexical sort would place "1.4.1-alpha.10" before
        // "1.4.1-alpha.9" and this case would wrongly yield alpha.10 again instead of alpha.11.
        var (version, _, _) = Derive("snd", "1.4.0", "1.4.1-alpha.9", "1.4.1-alpha.10");
        Assert.Equal("1.4.1-alpha.11", version);
    }

    [Fact]
    public void AlphaTagsOnAnOlderBase_DoNotAffectTheNewBase()
    {
        // 1.4.1 is itself a stable tag, so the base becomes 1.4.2 and the old 1.4.1-alpha.7
        // tag belongs to a base that no longer exists: the counter restarts at 1.
        var (version, _, _) = Derive("snd", "1.4.0", "1.4.1-alpha.7", "1.4.1");
        Assert.Equal("1.4.2-alpha.1", version);
    }

    [Fact]
    public void RcTags_DoNotAffectTheAlphaCounter()
    {
        var (version, _, _) = Derive("snd", "1.4.0", "1.4.1-rc.5");
        Assert.Equal("1.4.1-alpha.1", version);
    }

    [Fact]
    public void UnescapedBaseDots_DoNotMatchAsARegexWildcard()
    {
        // Review escalation: BASE is interpolated into the prerelease grep and sed patterns. If
        // its dots are not escaped, "." matches any character, so "1x4x1-alpha.5" would wrongly
        // be read as belonging to base 1.4.1 and the counter would pick up 6 instead of 1.
        var (version, _, _) = Derive("snd", "1.4.0", "1x4x1-alpha.5");
        Assert.Equal("1.4.1-alpha.1", version);
    }

    [Fact]
    public void AlphaTags_DoNotAffectTheRcCounter()
    {
        var (version, _, _) = Derive("dev", "1.4.0", "1.4.1-alpha.5");
        Assert.Equal("1.4.1-rc.1", version);
    }

    [Fact]
    public void PrereleaseTags_AreNotTreatedAsStable()
    {
        var (version, _, _) = Derive("main", "1.4.0", "1.5.0-rc.1");
        Assert.Equal("1.4.1", version);
    }

    [Fact]
    public void StableSelection_IsVersionSorted_NotLexical()
    {
        // A lexical sort would place "1.10.0" before "1.9.0" and this case would wrongly pick
        // 1.9.0 as newest, yielding 1.9.1 instead of 1.10.1.
        var (version, _, _) = Derive("main", "1.9.0", "1.10.0");
        Assert.Equal("1.10.1", version);
    }

    [Fact]
    public void MajorAndMinor_MoveOnlyByAManualTag()
    {
        var (version, _, _) = Derive("main", "2.0.0", "1.9.0");
        Assert.Equal("2.0.1", version);
    }

    [Fact]
    public void UnknownBranch_ExitsOne()
    {
        var result = RunScript("feature/x", new[] { "1.0.0" });
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.StandardError));
    }

    [Fact]
    public void BlankLinesInTheTagList_AreIgnored()
    {
        var (version, _, _) = Derive("main", "1.4.0", "", "");
        Assert.Equal("1.4.1", version);
    }

    [Fact]
    public void MalformedTags_AreIgnored()
    {
        // v1.5.0 matters most: this repository tags without a v prefix, so a v-prefixed tag
        // arriving from anywhere must not be read as stable.
        var (version, _, _) = Derive("main", "1.4.0", "v1.5.0", "1.5", "1.5.0.1");
        Assert.Equal("1.4.1", version);
    }

    [Fact]
    public void AZeroPaddedPrereleaseCounter_IsReadAsBaseTen()
    {
        // Phase review minor P8. The counters the script itself produces are never zero-padded,
        // so this needs a hand-created tag to reach. Without the 10# prefix the arithmetic reads
        // 08 as an invalid octal literal and set -euo pipefail ends the release job with a shell
        // error rather than a version: the same reason the patch segment carries the prefix.
        var (version, channel, prerelease) = Derive("snd", "1.4.0", "1.4.1-alpha.08");

        Assert.Equal("1.4.1-alpha.9", version);
        Assert.Equal("alpha", channel);
        Assert.True(prerelease);
    }

    [Fact]
    public void AZeroPaddedPatchSegment_IsReadAsBaseTen()
    {
        // The half the script already had, asserted beside the new one so the two cannot drift.
        var (version, _, _) = Derive("main", "1.4.08");

        Assert.Equal("1.4.9", version);
    }

    [Fact]
    public void Output_IsExactlyThreeLines_InOrder()
    {
        var result = RunScript("snd", new[] { "1.4.0" });
        Assert.Equal(0, result.ExitCode);
        var lines = SplitLines(result.StandardOutput);
        Assert.Equal(3, lines.Count);
        Assert.StartsWith("version=", lines[0]);
        Assert.StartsWith("channel=", lines[1]);
        Assert.StartsWith("prerelease=", lines[2]);
    }

    private static (string Version, string Channel, bool Prerelease) Derive(string branch, params string[] tags)
    {
        var result = RunScript(branch, tags);
        Assert.True(
            result.ExitCode == 0,
            $"derive-version.sh exited {result.ExitCode} for branch '{branch}'. stderr: {result.StandardError}");

        var lines = SplitLines(result.StandardOutput);
        Assert.Equal(3, lines.Count);

        var version = ParseKeyValue(lines[0], "version");
        var channel = ParseKeyValue(lines[1], "channel");
        var prereleaseText = ParseKeyValue(lines[2], "prerelease");
        bool prerelease = prereleaseText switch
        {
            "true" => true,
            "false" => false,
            _ => throw new InvalidOperationException($"Unexpected prerelease value '{prereleaseText}'"),
        };

        return (version, channel, prerelease);
    }

    private static string ParseKeyValue(string line, string expectedKey)
    {
        var prefix = expectedKey + "=";
        Assert.True(line.StartsWith(prefix, StringComparison.Ordinal), $"expected line to start with '{prefix}' but got '{line}'");
        return line.Substring(prefix.Length);
    }

    private static IReadOnlyList<string> SplitLines(string text)
    {
        // No RemoveEmptyEntries: a blank line emitted among or after the three key=value lines
        // must show up as an extra entry, not be silently dropped, so Output_IsExactlyThreeLines
        // can actually catch it. Only the one trailing newline that terminates the last line is
        // trimmed; a second trailing newline still yields a fourth, empty entry.
        var normalized = text.Replace("\r\n", "\n");
        if (normalized.EndsWith("\n", StringComparison.Ordinal))
        {
            normalized = normalized.Substring(0, normalized.Length - 1);
        }
        return normalized.Length == 0 ? Array.Empty<string>() : normalized.Split('\n');
    }

    // Resolution order: the two standard Git for Windows locations, then bash on PATH. The brief
    // had PATH first, but a machine with WSL carries C:\Windows\System32\bash.exe, the WSL shim,
    // and System32 sits ahead of any Git directory on the machine PATH. Under the CI runner's
    // service account that shim has no distribution, exits 1 with empty stderr, and the whole
    // class fails; under a user account with a distribution it runs the script in Linux and
    // derives from the wrong tag set. Git Bash is the documented prerequisite, so it wins when
    // present. When nothing is found this fails loudly (naming HANDOFF.md section 3.1 item 3)
    // rather than skipping, because a silent skip would hide a broken derivation on exactly the
    // machine about to cut a release.
    private static string ResolveBash()
    {
        var standardLocations = new[]
        {
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\Program Files\Git\usr\bin\bash.exe",
        };

        foreach (var candidate in standardLocations)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var fromPath = FindOnPath();
        if (fromPath is not null)
        {
            return fromPath;
        }

        throw new InvalidOperationException(
            "bash was not found on PATH or at C:\\Program Files\\Git\\bin\\bash.exe or " +
            "C:\\Program Files\\Git\\usr\\bin\\bash.exe. Git for Windows with Git Bash is a " +
            "documented prerequisite of this repository: see HANDOFF.md section 3.1 item 3.");
    }

    private static string? FindOnPath()
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar))
        {
            return null;
        }

        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in new[] { "bash.exe", "bash" })
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(dir, name);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static ScriptResult RunScript(string branch, IEnumerable<string> tags)
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "derive-version.sh").Replace('\\', '/');
        var bashPath = ResolveBash();

        var psi = new ProcessStartInfo
        {
            FileName = bashPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = repoRoot,
        };
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add(branch);

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.Append(e.Data).Append('\n'); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.Append(e.Data).Append('\n'); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        foreach (var tag in tags)
        {
            process.StandardInput.WriteLine(tag);
        }
        process.StandardInput.Close();

        if (!process.WaitForExit(TimeoutMs))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort: the process may have exited between the WaitForExit timeout
                // and the Kill call. Either way the timeout below is what fails the test.
            }

            throw new TimeoutException($"tools/derive-version.sh timed out after {TimeoutMs} ms for branch '{branch}'.");
        }

        // Ensures the async output/error event handlers have finished flushing before we read
        // the accumulated buffers.
        process.WaitForExit();

        return new ScriptResult(process.ExitCode, stdout.ToString(), stderr.ToString());
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
