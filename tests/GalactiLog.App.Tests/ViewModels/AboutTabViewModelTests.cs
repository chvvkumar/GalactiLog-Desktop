using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Settings;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.AboutTabViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

/// <summary>
/// Spec 12.7's About tab: version, git SHA, release channel, the update check button, release
/// notes and the log folder link.
/// </summary>
/// <remarks>
/// Plain xunit, no window: the tab is a view-model over <c>BuildInfo</c> and <c>UpdateService</c>
/// and constructs with neither a database nor a dispatcher (design-spec 18.3). Nothing here
/// reaches the network.
/// </remarks>
public class AboutTabViewModelTests
{
    [Fact]
    public void Fields_RenderVersionGitShaAndChannel()
    {
        using var harness = Factory.Create(
            new BuildInfo("2.5.1.0", "9f8e7d6c5b4a", "rc", isInstalled: true));

        Assert.Equal("2.5.1.0", harness.ViewModel.Version);
        Assert.Equal("9f8e7d6c5b4a", harness.ViewModel.GitSha);
        Assert.Equal("rc", harness.ViewModel.Channel);
    }

    [Fact]
    public void ReleaseNotes_FallBackToTheTagName_WhenNoCheckHasRun()
    {
        using var harness = Factory.Create(
            new BuildInfo("1.4.2.0", "0b1d3f5", "stable", isInstalled: true));

        // Ruling Q19 step 2: spec 17.5's release name is the tag, and a four part assembly
        // version whose revision is zero is released as its three part tag.
        Assert.Equal("v1.4.2", harness.ViewModel.ReleaseNotes);
    }

    [Fact]
    public void ReleaseNotes_FallBackToAMessage_WhenThereAreNone()
    {
        using var harness = Factory.Create(
            new BuildInfo(DiagnosticsService.Unknown, DiagnosticsService.Unknown, "local", false));

        // Ruling Q19 step 3: no notes, and no tag name to fall back to either.
        Assert.Equal(AboutTabViewModel.NoReleaseNotesMessage, harness.ViewModel.ReleaseNotes);
    }

    [Fact]
    public async Task ReleaseNotes_PreferTheNotesTheLastCheckCarried()
    {
        using var harness = Factory.Create();
        harness.Checker.Offered = new AvailableUpdate("1.5.0", "Fixed the thing", new object());

        await harness.Updates!.CheckNowAsync();

        // Ruling Q19 step 1, which wins over the tag name.
        Assert.Equal("Fixed the thing", harness.ViewModel.ReleaseNotes);
    }

    [Fact]
    public void CheckForUpdates_WithNoUpdateService_IsDisabled_AndSaysWhy()
    {
        using var harness = Factory.Create(withUpdateService: false);

        Assert.False(harness.ViewModel.CheckForUpdatesCommand.CanExecute(null));
        Assert.Equal(AboutTabViewModel.NotInstalledMessage, harness.ViewModel.UpdateStatus);
    }

    [Fact]
    public void CheckForUpdates_OnANotInstalledBuild_IsDisabled_AndSaysWhy()
    {
        using var harness = Factory.Create(
            new BuildInfo("1.0.0.0", "abc1234", BuildInfo.LocalChannel, isInstalled: false));

        // The button is present and disabled with the reason on screen: an absent button reads as
        // a missing feature.
        Assert.False(harness.ViewModel.CheckForUpdatesCommand.CanExecute(null));
        Assert.Equal(AboutTabViewModel.NotInstalledMessage, harness.ViewModel.UpdateStatus);
    }

    [Fact]
    public async Task CheckForUpdates_PressedTwice_ChecksOnce()
    {
        using var harness = Factory.Create();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Checker.CheckGate = gate;

        harness.ViewModel.CheckForUpdatesCommand.Execute(null);
        var first = harness.ViewModel.CheckForUpdatesCommand.ExecutionTask!;

        // Awaited, not assumed: the first check runs off the dispatcher, so the second press has
        // to be made while that check is genuinely parked in the feed rather than while it is
        // still queued on the pool.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (harness.Checker.Checks < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(2);
        }

        harness.ViewModel.CheckForUpdatesCommand.Execute(null);

        gate.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, harness.Checker.Checks);
    }

    [Fact]
    public async Task CheckForUpdates_ExecutedDirectly_IsGuardedInTheCommandBody()
    {
        using var harness = Factory.Create(withUpdateService: false);

        // A direct Execute bypasses CanExecute, so the body repeats the rule (TRACKING item 13).
        harness.ViewModel.CheckForUpdatesCommand.Execute(null);
        if (harness.ViewModel.CheckForUpdatesCommand.ExecutionTask is { } execution)
        {
            await execution;
        }

        Assert.Equal(0, harness.Checker.Checks);
    }

    [Fact]
    public async Task CheckForUpdates_ShowsTheResultOnTheStatusLine()
    {
        using var harness = Factory.Create();
        harness.Checker.Offered = null;

        await harness.ViewModel.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Equal(1, harness.Checker.Checks);
        Assert.Contains("No update available", harness.ViewModel.UpdateStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenLogFolder_RevealsTheNewestLogFile()
    {
        using var harness = Factory.Create(
            logFiles: ["galactilog-20250914.log", "galactilog-20250915.log"]);

        harness.ViewModel.OpenLogFolderCommand.Execute(null);

        // Ruling Q28: the folder opens with the current log selected.
        var launch = Assert.Single(harness.Launches);
        Assert.Equal("explorer.exe", launch.FileName);
        Assert.Contains("galactilog-20250915.log", launch.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenLogFolder_WithNoFilesYet_OpensTheDirectory()
    {
        using var harness = Factory.Create();

        harness.ViewModel.OpenLogFolderCommand.Execute(null);

        var launch = Assert.Single(harness.Launches);
        Assert.Equal(harness.LogDirectory, launch.FileName);
        Assert.True(launch.UseShellExecute);
    }

    [Fact]
    public void OpenLogFolder_IsDisabled_WhenTheWindowsShellIsUnavailable()
    {
        using var harness = Factory.Create();

        // The guard is ShellIntegration's one Windows check, read rather than repeated
        // (design-lessons rule 2), so this case asserts the command follows it either way.
        Assert.Equal(
            ShellIntegration.IsWindowsShellAvailable,
            harness.ViewModel.OpenLogFolderCommand.CanExecute(null));
    }

    [Fact]
    public void OpenLogFolder_ComposesNoPath()
    {
        var text = SourceScan.StripComments(File.ReadAllText(Path.Combine(
            SourceScan.SrcRoot(), "GalactiLog.App", "ViewModels", "Settings", "AboutTabViewModel.cs")));

        foreach (var forbidden in new[] { "Path.Combine", "Path.GetFullPath", "AppDataRoot" })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void OpenReleases_OpensTheReleasesPageInTheDefaultBrowser()
    {
        using var harness = Factory.Create();

        harness.ViewModel.OpenReleasesCommand.Execute(null);

        var launch = Assert.Single(harness.Launches);
        Assert.Equal(AboutTabViewModel.ReleasesUrl, launch.FileName);
        Assert.True(launch.UseShellExecute);
        Assert.StartsWith(VelopackUpdateChecker.RepositoryUrl, launch.FileName, StringComparison.Ordinal);
    }

    [Fact]
    public void Tab_RunsNoNetworkCallInItsConstructor()
    {
        using var harness = Factory.Create();

        Assert.Equal(0, harness.Checker.Checks);
        Assert.Equal(0, harness.Checker.Downloads);
    }

    [Fact]
    public async Task Dispose_DetachesTheUpdateStateSubscription()
    {
        using var harness = Factory.Create();
        var before = harness.ViewModel.UpdateStatus;
        harness.ViewModel.Dispose();

        harness.Checker.Offered = new AvailableUpdate("9.9.9", "Notes", new object());
        await harness.Updates!.CheckNowAsync();

        Assert.Equal(before, harness.ViewModel.UpdateStatus);
        Assert.False(harness.ViewModel.CheckForUpdatesCommand.CanExecute(null));
    }
}
