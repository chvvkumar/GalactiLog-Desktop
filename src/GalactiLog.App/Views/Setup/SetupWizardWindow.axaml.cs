using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using GalactiLog.App.ViewModels.Setup;
using Serilog;

namespace GalactiLog.App.Views.Setup;

/// <summary>
/// Design-spec 12.1's setup wizard window. The window owns the two folder pickers and the Escape
/// key and holds no other logic: the subscription, the
/// <see cref="SetupWizardViewModel.CloseRequested"/> to <c>Close(result)</c> path and the veto gate
/// belong to <see cref="ModalPageWindow{TViewModel}"/>, shared with <c>MergeDialogWindow</c> and
/// <c>ResetConfirmWindow</c>.
/// </summary>
/// <remarks>
/// <para>
/// Not dismissible by Escape or by the title bar, matching the web's <c>onClose={() =&gt; {}}</c>.
/// A half-configured application is a worse state than a wizard the user has to click through, and
/// "Skip setup" is the deliberate exit, which records the decision through the same completion path
/// Finish uses. The only dismissal refused is the user's own
/// (<see cref="WindowCloseReason.WindowClosing"/>); a shutdown, an application exit and an
/// owner-window close are never vetoed.
/// </para>
/// <para>
/// The pickers are Avalonia storage-provider calls here rather than a new service (HANDOFF.md
/// section 5), so every step view-model stays constructible with no window. A picker result is a
/// plain string; nothing here enumerates, creates or deletes anything on disk.
/// </para>
/// </remarks>
public partial class SetupWizardWindow : ModalPageWindow<SetupWizardViewModel>
{
    public SetupWizardWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Escape does not close the wizard. Swallowed here rather than left to the platform, because
    /// a modal <c>Window</c> closes on Escape by default and an accidental keypress would leave a
    /// first-run install with no scan roots and no explanation.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>
    /// Refuses the user's own dismissal, which is the port of the web dialog's
    /// <c>onClose={() =&gt; {}}</c>: the title bar and Alt+F4 are
    /// <see cref="WindowCloseReason.WindowClosing"/>, and only that is vetoed.
    /// </summary>
    /// <remarks>
    /// An OS shutdown, an application shutdown and an owner-window close are let through (Task 9
    /// review, Important finding 3). Cancelling those would keep the process alive with Windows
    /// reporting the application as blocking the session from ending, which is a far worse
    /// outcome than a first run the user has to start again.
    /// </remarks>
    protected override bool RefuseClose(WindowCloseReason reason)
        => reason == WindowCloseReason.WindowClosing;

    private async void OnAddScanFolder(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ScanFoldersStepViewModel step
            || StorageProvider is not { } storage)
        {
            return;
        }

        IReadOnlyList<IStorageFolder> folders;
        try
        {
            // AllowMultiple, unlike the cache location: spec 12.1 step 1 is a list, and the step's
            // own nesting rule refuses anything that overlaps a folder already chosen.
            folders = await storage
                .OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Choose a folder to scan",
                    AllowMultiple = true,
                })
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // An async void handler's continuation: an exception escaping here has nowhere to go
            // but the dispatcher's unhandled path. Logged so a storage-provider fault is
            // distinguishable from a cancelled picker, which changes nothing either way.
            Log.Warning(ex, "The scan folder picker could not be opened");
            return;
        }

        foreach (var folder in folders)
        {
            // TryGetLocalPath is null for a non-filesystem location (a cloud provider shell
            // folder); there is nothing this application could scan there, so it is skipped.
            if (folder.TryGetLocalPath() is { Length: > 0 } path)
            {
                step.AddFolder(path);
            }
        }
    }

    /// <summary>
    /// Step 2's data location picker (Phase 10 Task 9). The same shape as the two handlers above:
    /// the step is reached through the sender's DataContext, and a picked folder is recorded as a
    /// pending move that the next start performs. Nothing here touches disk.
    /// </summary>
    private async void OnBrowseDataRoot(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ThumbnailCacheStepViewModel step
            || StorageProvider is not { } storage)
        {
            return;
        }

        IReadOnlyList<IStorageFolder> folders;
        try
        {
            folders = await storage
                .OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Choose a location for GalactiLog's data",
                    AllowMultiple = false,
                })
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The data location folder picker could not be opened");
            return;
        }

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { Length: > 0 } path)
        {
            step.SetDataRoot(path);
        }
    }

    private async void OnBrowseCacheDir(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ThumbnailCacheStepViewModel step
            || StorageProvider is not { } storage)
        {
            return;
        }

        IReadOnlyList<IStorageFolder> folders;
        try
        {
            // One folder: this setting is a single path, unlike step 1's list.
            folders = await storage
                .OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Choose a thumbnail cache location",
                    AllowMultiple = false,
                })
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The thumbnail cache folder picker could not be opened");
            return;
        }

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { Length: > 0 } path)
        {
            step.SetCachePath(path);
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
