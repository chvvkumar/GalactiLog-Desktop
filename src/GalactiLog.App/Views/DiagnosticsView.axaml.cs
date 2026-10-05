using Avalonia.Controls;
using Avalonia.Platform.Storage;
using GalactiLog.App.ViewModels.Diagnostics;
using Serilog;

namespace GalactiLog.App.Views;

/// <summary>
/// Spec 12.8's Diagnostics page, bound to
/// <see cref="ViewModels.Diagnostics.DiagnosticsViewModel"/>. The only logic here is the export
/// save dialog: every group is data and the two buttons carry commands.
/// </summary>
/// <remarks>
/// The save dialog follows the shape the three landed pickers use (<c>LibraryTabView</c>,
/// <c>StorageTabView</c>, <c>SetupWizardWindow</c>): a
/// <c>TopLevel.GetTopLevel(this)?.StorageProvider</c> call in this code-behind, handed to the
/// view-model through a <c>Func&lt;Task&lt;string?&gt;&gt;</c> seam with one production binding
/// (coordinator ruling Q10). The dialog is the only place a path outside app data can enter the
/// application (spec 12.8), and nothing else in this file composes a path.
/// </remarks>
public partial class DiagnosticsView : UserControl
{
    public DiagnosticsView() => InitializeComponent();

    /// <summary>
    /// Installs the picker on whichever page instance this view is rendering. The page is a DI
    /// singleton shared by the rail and the Settings tab (coordinator ruling Q3) and is built
    /// before any view exists, so the seam is bound by the view that is actually on screen rather
    /// than at registration.
    /// </summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        BindDestinationPicker();
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        BindDestinationPicker();
    }

    /// <summary>
    /// Clears the seam when the installed picker is this view's own. The page is one singleton
    /// with two host surfaces (the rail entry and the Settings Diagnostics tab, ruling Q3), so
    /// leaving a detached view's picker installed would let a press be served by a delegate whose
    /// <c>TopLevel</c> is gone, which returns null and exports nothing with no visible outcome
    /// (review finding F3). An empty seam is better than a stale one: the command's null-seam
    /// guard is the documented "this surface has no dialog" path.
    /// </summary>
    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (DataContext is DiagnosticsViewModel page
            && page.DestinationPicker == PickBundleDestinationAsync)
        {
            page.DestinationPicker = null;
        }
    }

    private void BindDestinationPicker()
    {
        if (DataContext is DiagnosticsViewModel page)
        {
            page.DestinationPicker = PickBundleDestinationAsync;
        }
    }

    /// <summary>
    /// Opens the platform save dialog and returns the chosen absolute path, or null when the user
    /// cancelled, when there is no top level (a headless test), or when the chosen location is
    /// not on the filesystem.
    /// </summary>
    private async Task<string?> PickBundleDestinationAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return null;
        }

        IStorageFile? file;
        try
        {
            var start = await SaveDialogStart.DocumentsAsync(storage).ConfigureAwait(true);
            file = await storage.SaveFilePickerAsync(SaveOptions(start)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // The same shape the existing pickers use: a storage-provider fault is logged and
            // treated as "no destination" rather than taking the window down.
            Log.Warning(ex, "The diagnostics bundle save dialog could not be opened");
            return null;
        }

        // TryGetLocalPath is null for a non-filesystem location (a cloud provider shell item).
        // Null means "no destination" and the command returns; the application does not try to
        // resolve it into something writable.
        return file?.TryGetLocalPath();
    }

    /// <summary>
    /// The bundle save dialog's options, built apart from the dialog so a case can read what they
    /// carry, the same shape <c>LogViewerView.SaveOptions</c> uses.
    /// </summary>
    /// <param name="startLocation">Where the dialog opens, from
    /// <see cref="SaveDialogStart.DocumentsAsync"/>. Verification E2's ruling extends to this
    /// picker: without it the dialog opens wherever any picker in the process was last used, which
    /// in this application is a scan root. Null leaves the platform's own default.</param>
    internal static FilePickerSaveOptions SaveOptions(IStorageFolder? startLocation) => new()
    {
        Title = "Export diagnostics bundle",
        SuggestedFileName = $"galactilog-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.json",
        DefaultExtension = "json",
        SuggestedStartLocation = startLocation,

        // The operating system's own overwrite confirmation, which spec 12.8 step 3 names as the
        // only way an existing file is replaced. Not optional: the application never overwrites a
        // user file without this prompt and never chooses the path itself.
        ShowOverwritePrompt = true,
        FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
    };
}
