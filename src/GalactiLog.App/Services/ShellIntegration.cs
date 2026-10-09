using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.Services;

/// <summary>
/// Spec 2.1's three permitted interactions with a user file, and nothing else. Every member here
/// either copies text or launches a process with a path argument. None of them opens a stream,
/// creates, deletes, moves, renames, or writes anything: there is no method to call, so there is
/// nothing to forget (spec 2.1.1's reasoning, applied at the App layer).
/// <para>
/// It is therefore invisible to <c>FileSafetyTest</c> by construction rather than by allowlist:
/// the forbidden call list in spec 2.1.3 names <c>System.IO</c> write members, and this type calls
/// none of them. Do not add an allowlist entry for this file; if you find yourself wanting one,
/// the method you are writing is not allowed to exist.
/// </para>
/// </summary>
public sealed class ShellIntegration
{
    private readonly Func<string, Task>? _copyText;
    private readonly Func<ProcessStartInfo, Process?> _start;
    private readonly ILogger _logger;

    /// <param name="copyText">How to reach the clipboard. Avalonia 11 removed
    /// <c>Application.Current.Clipboard</c>; a clipboard belongs to a <c>TopLevel</c>, so the App
    /// binds this to <c>topLevel.Clipboard.SetTextAsync</c> and a unit test binds it to a
    /// recording delegate. Null means no clipboard, which makes the copy members no-ops rather
    /// than throwing.
    /// <para>
    /// A text-setter delegate rather than the brief's <c>Func&lt;IClipboard?&gt;</c>: Avalonia
    /// 11.3.21's <c>IClipboard</c> carries a member documented as "not implementable by user
    /// code", so no recording fake of that interface can be written and the copy actions would
    /// have had no test at all. Setting text is the whole of what this type asks a clipboard for.
    /// </para></param>
    /// <param name="start">Launches a process. The seam exists so the tests assert the exact
    /// argument strings without spawning Explorer; production passes
    /// <c>info =&gt; Process.Start(info)</c>.</param>
    /// <param name="logger">Optional. A launch that fails is logged, never rethrown.</param>
    public ShellIntegration(
        Func<string, Task>? copyText = null,
        Func<ProcessStartInfo, Process?>? start = null,
        ILogger? logger = null)
    {
        _copyText = copyText;
        _start = start ?? Process.Start;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Spec 19.2's macOS and Linux non-goal: "no platform-specific path in
    /// <c>ShellIntegration</c> is written for them beyond the guard that reveals the code path is
    /// Windows-only". Public so a command's <c>CanExecute</c> reads this one guard instead of
    /// calling <c>OperatingSystem.IsWindows()</c> again at each call site (design-lessons rule 2).
    /// </summary>
    public static bool IsWindowsShellAvailable => OperatingSystem.IsWindows();

    /// <summary>Spec 11.5: runs <c>explorer.exe /select,"&lt;path&gt;"</c>. Selects the file in
    /// its folder. Writes nothing. Windows only (spec 19.2): <c>explorer.exe</c> is a Windows
    /// shell verb, so this returns without launching anything anywhere else. The guard is here,
    /// at the one choke point, rather than at each caller, so every present and future caller
    /// inherits it. <see cref="OpenWithDefaultApplication"/> is deliberately not guarded: it is
    /// <c>Process.Start</c> with <c>UseShellExecute</c>, which is the documented cross-platform
    /// way to hand a path to the registered handler (ruling Q20).</summary>
    public void RevealInExplorer(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!IsWindowsShellAvailable)
        {
            // Debug, not warning: on a platform this project does not ship to, a reveal that does
            // nothing is the designed outcome and not a fault worth a warning line.
            _logger.LogDebug("Reveal in Explorer is a Windows-only shell verb; nothing was launched.");
            return;
        }

        // A double quote cannot appear in a Windows path, so a path that has one did not come
        // from the filesystem. Refused rather than escaped: the /select, form below is one
        // argument token that Explorer parses itself, so there is no quoting rule to get right.
        if (path.Contains('"'))
        {
            _logger.LogWarning("Refused to reveal a path containing a double quote");
            return;
        }

        Launch(new ProcessStartInfo
        {
            FileName = "explorer.exe",

            // ArgumentList is not usable here: explorer.exe requires the unusual /select,"path"
            // form as a single token, which ArgumentList would quote as one argument including
            // the /select, prefix.
            Arguments = $"/select,\"{path}\"",
            UseShellExecute = false,
        });
    }

    /// <summary>Spec 12.4's "reveal the target's folder in Explorer". The same verb against the
    /// most recent frame of the target (ruling Q10), so the folder opens with a frame
    /// highlighted. <paramref name="framePaths"/> is the query's capture-ordered list, so the
    /// most recent frame is its last entry. No frames means nothing to reveal.</summary>
    public void RevealFolderOf(IReadOnlyList<string> framePaths)
    {
        if (framePaths.Count == 0)
        {
            return;
        }

        RevealInExplorer(framePaths[^1]);
    }

    /// <summary>The export wizard's Open folder: <c>explorer.exe</c> with the folder as its one
    /// argument, and nothing at all unless that path is an existing folder, so a file sitting at
    /// the path is never handed to the shell. Windows only, like <see cref="RevealInExplorer"/>.
    /// </summary>
    public void OpenFolderInExplorer(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !IsWindowsShellAvailable || !Directory.Exists(folder))
        {
            return;
        }

        var info = new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = false };
        info.ArgumentList.Add(folder);
        Launch(info);
    }

    /// <summary>The export wizard's Run script: <c>powershell.exe</c> in its own console window,
    /// kept open afterwards so the script's own report stays readable, with the script as the
    /// one <c>-File</c> argument and its folder as the working directory. <c>-ExecutionPolicy
    /// Bypass</c> makes the Unblock-File step of the pasted command unnecessary. Nothing is
    /// launched unless the path is an existing file. What the script then writes is the user's
    /// business, as with <see cref="OpenWithDefaultApplication"/>. Windows only.</summary>
    public void RunPowerShellScript(string scriptPath)
    {
        if (string.IsNullOrWhiteSpace(scriptPath) || !IsWindowsShellAvailable || !File.Exists(scriptPath))
        {
            return;
        }

        var info = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? "",
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-NoExit", "-File", scriptPath })
        {
            info.ArgumentList.Add(argument);
        }

        Launch(info);
    }

    /// <summary>Spec 11.5: <c>Process.Start</c> with <c>UseShellExecute = true</c>, which is what
    /// hands the path to the registered application. Writes nothing itself; what the launched
    /// application then does is the user's business and outside this process.</summary>
    public void OpenWithDefaultApplication(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        Launch(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    /// <summary>Copies text. The only clipboard writer in the application.</summary>
    public async Task CopyTextAsync(string text)
    {
        if (_copyText is null)
        {
            // A headless or not-yet-shown top level has no clipboard. Nothing to copy to is not
            // a failure worth surfacing on the page.
            return;
        }

        try
        {
            await _copyText(text).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The Windows clipboard can be held open by another process. A copy that could not
            // happen must not take the window down.
            _logger.LogWarning(ex, "Copying to the clipboard failed");
        }
    }

    /// <summary>Spec 12.4's "copy frame list to clipboard": one absolute path per line, in the
    /// order given, with a trailing newline (ruling Q11: paths, not a table, and no dialog).
    /// </summary>
    public Task CopyFrameListAsync(IReadOnlyList<string> framePaths)
        => framePaths.Count == 0
            ? Task.CompletedTask
            : CopyTextAsync(string.Join(Environment.NewLine, framePaths) + Environment.NewLine);

    private void Launch(ProcessStartInfo info)
    {
        try
        {
            // The returned Process is deliberately dropped: this application does not own the
            // shell process it started and never waits on it.
            _start(info)?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ObjectDisposedException)
        {
            // A file that has since been moved or a shell that refuses the verb. Logged and
            // dropped: a missing file must not take the window down.
            _logger.LogWarning(ex, "Launching the shell for {FileName} failed", info.FileName);
        }
    }
}
