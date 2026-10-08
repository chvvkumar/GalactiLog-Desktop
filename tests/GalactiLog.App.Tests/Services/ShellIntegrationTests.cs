using System.ComponentModel;
using System.Diagnostics;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GalactiLog.App.Tests.Services;

// Phase 6 Task 3. The one place the three permitted interactions with a user file live (spec
// 2.1). Both seams are faked, so nothing here spawns Explorer or touches the real clipboard, and
// the exact argument string is what the assertions pin: argument construction is the one place a
// defect here would be exploitable.
public class ShellIntegrationTests
{
    private const string FramePath = @"C:\Astro\M 31\2025-12-07\frame_0001.fits";

    private sealed class Harness
    {
        public List<ProcessStartInfo> Launched { get; } = [];

        public List<string>? Clipboard { get; init; } = [];

        public RecordingLogger Logger { get; } = new();

        public Exception? StartThrows { get; init; }

        public ShellIntegration Create() => new(
            Clipboard is null
                ? null
                : text =>
                {
                    Clipboard.Add(text);
                    return Task.CompletedTask;
                },
            info =>
            {
                Launched.Add(info);
                if (StartThrows is not null)
                {
                    throw StartThrows;
                }

                // Nothing is actually started, which is the point of the seam.
                return null;
            },
            Logger);
    }

    [Fact]
    public void RevealInExplorer_OnWindows_LaunchesExplorerWithTheSelectArgument()
    {
        var harness = new Harness();

        harness.Create().RevealInExplorer(FramePath);

        if (!ShellIntegration.IsWindowsShellAvailable)
        {
            // The name states a precondition, so it enforces it: off Windows the guard is what is
            // under test and RevealInExplorer_LaunchesExplorerExactlyWhereTheWindowsGuardAdmitsIt
            // owns that assertion.
            Assert.Empty(harness.Launched);
            return;
        }

        var info = Assert.Single(harness.Launched);
        Assert.Equal("explorer.exe", info.FileName);

        // Spec 11.5's exact invocation. explorer.exe requires /select,"path" as one token, which
        // ArgumentList cannot express.
        Assert.Equal($"/select,\"{FramePath}\"", info.Arguments);
        Assert.Empty(info.ArgumentList);
    }

    // ---- Phase 8 Task 7: spec 19.2's Windows guard, at the choke point --------------------

    [Fact]
    public void RevealInExplorer_LaunchesExplorerExactlyWhereTheWindowsGuardAdmitsIt()
    {
        // OperatingSystem.IsWindows() cannot be faked, so this asserts the guard rather than a
        // platform: explorer.exe is launched on exactly the platforms
        // IsWindowsShellAvailable admits and on no others. An inverted or missing guard fails it
        // on either kind of machine.
        var harness = new Harness();

        harness.Create().RevealInExplorer(FramePath);

        Assert.Equal(ShellIntegration.IsWindowsShellAvailable, harness.Launched.Count == 1);
        Assert.Equal(OperatingSystem.IsWindows(), ShellIntegration.IsWindowsShellAvailable);
    }

    [Fact]
    public void OpenWithDefaultApplication_IsNotPlatformGuarded()
    {
        // Ruling Q20: Process.Start with UseShellExecute is the documented cross-platform way to
        // hand a path to the registered handler, so the shell verb itself is not guarded here.
        // The command that calls it is guarded at the view-model, which is what the roadmap's
        // Verify line asks for.
        var harness = new Harness();

        harness.Create().OpenWithDefaultApplication(FramePath);

        Assert.Single(harness.Launched);
    }

    [Fact]
    public void RevealInExplorer_PathContainingAQuote_IsRefused()
    {
        var harness = new Harness();

        harness.Create().RevealInExplorer("C:\\Astro\\bad\"path\\frame.fits");

        Assert.Empty(harness.Launched);
        Assert.Contains(harness.Logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RevealInExplorer_BlankPath_LaunchesNothing(string path)
    {
        var harness = new Harness();

        harness.Create().RevealInExplorer(path);

        Assert.Empty(harness.Launched);
    }

    [Fact]
    public void RevealInExplorer_UseShellExecute_IsFalse()
    {
        var harness = new Harness();

        harness.Create().RevealInExplorer(FramePath);

        // The Explorer call passes an argument string, so it must not go through the shell
        // handler for the path itself (spec 11.5's two rows are deliberately different). Skipped
        // where the Windows guard means nothing was launched at all.
        if (ShellIntegration.IsWindowsShellAvailable)
        {
            Assert.False(Assert.Single(harness.Launched).UseShellExecute);
        }
    }

    [Fact]
    public void OpenWithDefaultApplication_UsesShellExecute()
    {
        var harness = new Harness();

        harness.Create().OpenWithDefaultApplication(FramePath);

        var info = Assert.Single(harness.Launched);
        Assert.Equal(FramePath, info.FileName);
        Assert.True(info.UseShellExecute, "UseShellExecute is what hands the path to the registered application");
        Assert.Empty(info.Arguments);
    }

    [Fact]
    public void Start_Throwing_IsLoggedNotPropagated()
    {
        var harness = new Harness { StartThrows = new Win32Exception("the file no longer exists") };

        // A missing file must not take the window down. Launched through the open-with verb,
        // which is the one that is not platform-guarded, so the catch is exercised everywhere.
        harness.Create().OpenWithDefaultApplication(FramePath);

        Assert.Contains(harness.Logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public void RevealFolderOf_PicksTheFolderPerTheRuling()
    {
        var harness = new Harness();
        string[] paths =
        [
            @"C:\Astro\M 31\2024-01-05\frame_0001.fits",
            @"C:\Astro\M 31\2025-12-07\frame_0001.fits",
            @"C:\Astro\M 31\2025-12-07\frame_0088.fits",
        ];

        harness.Create().RevealFolderOf(paths);

        // Ruling Q10: the most recent frame by capture date, which is the last entry of the
        // query's capture-ordered list, so a target with frames in several folders opens the one
        // it was last shot into. Reveal is Windows-guarded (spec 19.2), so the argument is
        // asserted where there is one and the guard is asserted where there is not.
        if (ShellIntegration.IsWindowsShellAvailable)
        {
            Assert.Equal($"/select,\"{paths[^1]}\"", Assert.Single(harness.Launched).Arguments);
        }
        else
        {
            Assert.Empty(harness.Launched);
        }
    }

    [Fact]
    public void RevealFolderOf_NoFrames_LaunchesNothing()
    {
        var harness = new Harness();

        harness.Create().RevealFolderOf([]);

        Assert.Empty(harness.Launched);
    }

    [Fact]
    public async Task CopyFrameListAsync_JoinsPathsWithNewlines()
    {
        var harness = new Harness();
        string[] paths =
        [
            @"C:\Astro\M 31\2024-01-05\frame_0001.fits",
            @"C:\Astro\M 31\2025-12-07\frame_0001.fits",
        ];

        await harness.Create().CopyFrameListAsync(paths);

        // Ruling Q11: one absolute path per line, in the order given, with a trailing newline.
        var copied = Assert.Single(harness.Clipboard!);
        Assert.Equal(paths[0] + Environment.NewLine + paths[1] + Environment.NewLine, copied);
    }

    [Fact]
    public async Task CopyFrameListAsync_NoFrames_CopiesNothing()
    {
        var harness = new Harness();

        await harness.Create().CopyFrameListAsync([]);

        Assert.Empty(harness.Clipboard!);
    }

    [Fact]
    public async Task CopyTextAsync_NoClipboard_IsANoOp()
    {
        // A headless or not-yet-shown top level has no clipboard, which must disable the copy
        // rather than throw on the UI thread.
        var harness = new Harness { Clipboard = null };

        await harness.Create().CopyTextAsync("anything");

        Assert.Empty(harness.Logger.Entries);
    }

    // ---- The export wizard's Open folder -----------------------------------------------------

    // A failure here is a path that is not an existing folder handed to Explorer, which would
    // launch whatever file sits there.
    [Fact]
    public void OpenFolderInExplorer_LaunchesNothing_ForAMissingFolder()
    {
        var harness = new Harness();
        var missing = Path.Combine(Path.GetTempPath(), "galactilog-shell-missing-" + Guid.NewGuid().ToString("N"));

        harness.Create().OpenFolderInExplorer(missing);

        Assert.Empty(harness.Launched);
    }

    // A failure here is an existing folder that is not opened, or opened through a shell execute
    // or a composed argument string rather than as Explorer's one argument.
    [Fact]
    public void OpenFolderInExplorer_LaunchesExplorer_WithTheFolderAsItsOneArgument()
    {
        var harness = new Harness();
        var folder = Directory.CreateTempSubdirectory("galactilog-shell-open-").FullName;
        try
        {
            harness.Create().OpenFolderInExplorer(folder);
        }
        finally
        {
            Directory.Delete(folder);
        }

        if (!ShellIntegration.IsWindowsShellAvailable)
        {
            Assert.Empty(harness.Launched);
            return;
        }

        var info = Assert.Single(harness.Launched);
        Assert.Equal("explorer.exe", info.FileName);
        Assert.False(info.UseShellExecute);
        Assert.Equal([folder], info.ArgumentList);
    }

    // ---- The export wizard's Run script ------------------------------------------------------

    // A failure here is a path that is not an existing file handed to PowerShell, which would
    // open a console that reports a missing script.
    [Fact]
    public void RunPowerShellScript_LaunchesNothing_ForAMissingFile()
    {
        var harness = new Harness();
        var missing = Path.Combine(Path.GetTempPath(), "galactilog-shell-missing-" + Guid.NewGuid().ToString("N") + ".ps1");

        harness.Create().RunPowerShellScript(missing);

        Assert.Empty(harness.Launched);
    }

    // A failure here is an existing script that is not run, or run through a shell execute, a
    // composed argument string, a different working directory, or without the policy bypass that
    // makes the pasted Unblock-File step unnecessary.
    [Fact]
    public void RunPowerShellScript_LaunchesPowerShell_WithTheScriptAsItsFileArgument()
    {
        var harness = new Harness();
        var folder = Directory.CreateTempSubdirectory("galactilog-shell-run-").FullName;
        var script = Path.Combine(folder, "wbpp.ps1");
        try
        {
            File.WriteAllText(script, "# nothing");
            harness.Create().RunPowerShellScript(script);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        if (!ShellIntegration.IsWindowsShellAvailable)
        {
            Assert.Empty(harness.Launched);
            return;
        }

        var info = Assert.Single(harness.Launched);
        Assert.Equal("powershell.exe", info.FileName);
        Assert.False(info.UseShellExecute);
        Assert.Equal(folder, info.WorkingDirectory);
        Assert.Equal(
            ["-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-NoExit", "-File", script],
            info.ArgumentList);
    }
}
