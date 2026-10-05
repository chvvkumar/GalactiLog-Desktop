using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using GalactiLog.App.ViewModels.Settings;
using Serilog;

namespace GalactiLog.App.Views.Settings;

/// <summary>
/// Design-spec 12.7's Storage tab. The only logic here is the folder picker: HANDOFF.md section 5
/// keeps pickers as Avalonia storage-provider calls in the view rather than a new service, so the
/// view-model stays constructible with no window.
/// </summary>
/// <remarks>
/// The picker returns a path and nothing else happens to it here: the tab neither enumerates,
/// creates nor deletes anything under it, and relocating the cache moves no files (design-spec
/// 2.1, 11.3). A folder chosen while the picker is unavailable (a headless test, a top level that
/// is not a window) simply changes nothing.
/// </remarks>
public partial class StorageTabView : UserControl
{
    public StorageTabView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Enter commits the numeric fields, which is what the web's own inputs do. Both bind with
    /// <c>UpdateSourceTrigger=LostFocus</c>, so the commit is a matter of dropping focus rather
    /// than of pushing the binding by hand.
    /// </summary>
    private void OnCommitFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && TopLevel.GetTopLevel(this)?.FocusManager is { } focus)
        {
            focus.ClearFocus();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Spec 12.7's data location picker (Phase 10 Task 9), the same shape as the cache picker
    /// below it. The view creates nothing, enumerates nothing and deletes nothing: a picked folder
    /// becomes a pending move the next start performs.
    /// </summary>
    private async void OnBrowseDataRoot(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not StorageTabViewModel viewModel
            || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
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
            viewModel.SetDataRoot(path);
        }
    }

    private async void OnBrowseCacheDir(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not StorageTabViewModel viewModel
            || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        IReadOnlyList<IStorageFolder> folders;
        try
        {
            // One folder: this setting is a single path, unlike the Library tab's lists.
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
            // An async void handler's continuation: an exception escaping here has nowhere to go
            // but the dispatcher's unhandled path. Logged so a storage-provider fault is
            // distinguishable from a cancelled picker, which changes nothing either way.
            Log.Warning(ex, "The thumbnail cache folder picker could not be opened");
            return;
        }

        // TryGetLocalPath is null for a non-filesystem location (a cloud provider shell folder);
        // there is nothing this application could cache there, so it is skipped.
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { Length: > 0 } path)
        {
            viewModel.SetThumbnailCacheDir(path);
        }
    }
}
