using System.Reflection;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using Xunit;

namespace GalactiLog.App.Tests.Services;

/// <summary>
/// Spec 12.7's Version, git SHA and release channel, read once by <see cref="BuildInfo"/> so the
/// About tab, the Diagnostics Versions group and the diagnostics bundle cannot disagree.
/// </summary>
/// <remarks>
/// Nothing here reaches the network. <see cref="BuildInfo.FromProcess"/> with no checker reads the
/// running assembly and asks nothing of Velopack, and the one case that does build the real probe
/// asserts the uninstalled answer, which is what a <c>dotnet test</c> process is by definition.
/// </remarks>
public class BuildInfoTests
{
    // ---------------------------------------------------------------- the git SHA

    [Fact]
    public void GitSha_IsTheSubstringAfterThePlus()
    {
        Assert.Equal(
            "06097b51f0d1c0ca4e2a9e4b6b1f0d0f0b2c3d4e",
            BuildInfo.ParseGitSha("1.0.0+06097b51f0d1c0ca4e2a9e4b6b1f0d0f0b2c3d4e"));
    }

    [Fact]
    public void GitSha_WithAPrereleaseLabel_TakesOnlyWhatFollowsTheFirstPlus()
    {
        // SemVer puts the prerelease label before the build metadata, so the label stays with the
        // version and only the metadata is the commit.
        Assert.Equal("abc1234", BuildInfo.ParseGitSha("1.2.3-alpha.4+abc1234"));
    }

    [Fact]
    public void GitSha_WithNoPlus_IsUnknown()
    {
        Assert.Equal(DiagnosticsService.Unknown, BuildInfo.ParseGitSha("1.0.0"));
        Assert.Equal(DiagnosticsService.Unknown, BuildInfo.ParseGitSha(null));
        Assert.Equal(DiagnosticsService.Unknown, BuildInfo.ParseGitSha(""));
    }

    [Fact]
    public void GitSha_WithAnEmptySuffix_IsUnknown()
    {
        Assert.Equal(DiagnosticsService.Unknown, BuildInfo.ParseGitSha("1.0.0+"));
        Assert.Equal(DiagnosticsService.Unknown, BuildInfo.ParseGitSha("1.0.0+   "));
    }

    [Fact]
    public void GitSha_IsTruncatedToFortyCharacters()
    {
        var suffix = new string('a', 200);

        var sha = BuildInfo.ParseGitSha("1.0.0+" + suffix);

        Assert.Equal(40, sha.Length);
        Assert.Equal(new string('a', 40), sha);
    }

    // ---------------------------------------------------------------- the version

    [Fact]
    public void Version_FallsBackToUnknown_WhenTheAssemblyHasNone()
    {
        Assert.Equal(DiagnosticsService.Unknown, BuildInfo.ReadVersion(null));
        Assert.Equal("4.3.2.1", BuildInfo.ReadVersion(new Version(4, 3, 2, 1)));
    }

    // ---------------------------------------------------------------- the running process

    [Fact]
    public void FromProcess_OnAnUninstalledBuild_ReportsLocalChannelAndNotInstalled_AndDoesNotThrow()
    {
        // A dotnet test process is an uninstalled build by definition, so this is the real case
        // rather than a simulated one. Every update path is gated on IsInstalled, which is what
        // keeps the suite off the network.
        var info = BuildInfo.FromProcess();

        Assert.False(info.IsInstalled);
        Assert.Equal(BuildInfo.LocalChannel, info.Channel);
        Assert.False(string.IsNullOrWhiteSpace(info.Version));
    }

    [Fact]
    public void FromProcess_ReadsTheGitShaFromTheRunningAssembly()
    {
        // Ruling Q13 correction: a build from a git working copy already carries the HEAD SHA in
        // the informational version through the SDK's SourceLink support, so this is asserted
        // against whatever the running assembly actually carries rather than against "unknown".
        var informational = typeof(BuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        Assert.Equal(BuildInfo.ParseGitSha(informational), BuildInfo.FromProcess().GitSha);
    }

    [Fact]
    public void Constructor_KeepsEveryValueItWasGiven()
    {
        var info = new BuildInfo("1.2.3.4", "abcdef0", "alpha", isInstalled: true);

        Assert.Equal("1.2.3.4", info.Version);
        Assert.Equal("abcdef0", info.GitSha);
        Assert.Equal("alpha", info.Channel);
        Assert.True(info.IsInstalled);
    }

    // ---------------------------------------------------------------- the build property

    [Fact]
    public void Csproj_SetsIncludeSourceRevisionInInformationalVersion()
    {
        // Ruling Q13: the property is the SDK default today and is set explicitly anyway, so a
        // future default change shows up here rather than as a silently empty About field. Task 5
        // owns the workflow's -p:SourceRevisionId; this task owns the property and the reader.
        var csproj = File.ReadAllText(Path.Combine(
            SourceScan.SrcRoot(), "GalactiLog.App", "GalactiLog.App.csproj"));

        Assert.Contains(
            "<IncludeSourceRevisionInInformationalVersion>true</IncludeSourceRevisionInInformationalVersion>",
            csproj,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AppHost_BindsBothDiagnosticsSeams_ToBuildInfo()
    {
        // Task 1 left gitSha and channel as Func<string> seams defaulting to "unknown". Binding
        // them is the whole of Task 4's change to that service, and it is asserted structurally
        // so an AppHost edit that drops a binding fails here rather than shipping a Versions
        // group that reads "unknown" (design-lessons rule 2).
        var appHost = File.ReadAllText(Path.Combine(
            SourceScan.SrcRoot(), "GalactiLog.App", "AppHost.cs"));

        Assert.Contains("gitSha: () => ", appHost, StringComparison.Ordinal);
        Assert.Contains("channel: () => ", appHost, StringComparison.Ordinal);
        Assert.Contains("BuildInfo>().GitSha", appHost, StringComparison.Ordinal);
        Assert.Contains("BuildInfo>().Channel", appHost, StringComparison.Ordinal);
    }
}
