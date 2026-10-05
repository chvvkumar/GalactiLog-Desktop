using Avalonia.Controls;
using Avalonia.Platform.Storage;
using GalactiLog.App.ViewModels.Diagnostics;
using Serilog;

namespace GalactiLog.App.Views;

/// <summary>
/// Spec 12.8's log viewer, bound to <see cref="ViewModels.Diagnostics.LogViewerViewModel"/>.
/// Hosted inside the Diagnostics page. The only logic here is Task 7's "Save log as" dialog seam,
/// the same shape <see cref="DiagnosticsView"/>'s bundle export picker uses (section 6.2).
/// </summary>
/// <remarks>
/// <b>Mind the lifecycle.</b> This view is rendered through an <c>App.axaml</c>
/// <c>DataTemplate</c> inside <c>DiagnosticsView</c>'s <c>ContentControl x:Name="LogViewerRegion"</c>,
/// so it attaches and detaches with that region rather than with the page, and the page itself is
/// a DI singleton shared by the rail and the Settings tab strip. The attach and detach pair below
/// uses the same "clear it only when it is still mine" guard <c>DiagnosticsView.axaml.cs</c> uses,
/// so two views cannot leave a dangling picker.
/// </remarks>
public partial class LogViewerView : UserControl
{
    public LogViewerView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        BindSaveLogAsPicker();
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        BindSaveLogAsPicker();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (DataContext is LogViewerViewModel page
            && page.SaveLogAsDestinationPicker == PickLogDestinationAsync)
        {
            page.SaveLogAsDestinationPicker = null;
        }
    }

    private void BindSaveLogAsPicker()
    {
        if (DataContext is LogViewerViewModel page)
        {
            page.SaveLogAsDestinationPicker = PickLogDestinationAsync;
        }
    }

    /// <summary>
    /// Opens the platform save dialog and returns the chosen absolute path, or null when the user
    /// cancelled, when there is no top level (a headless test), or when the chosen location is
    /// not on the filesystem. Departure: the suggested file name follows the diagnostics bundle's
    /// shape with a <c>.txt</c> extension, matching a log's own file type rather than the bundle's
    /// JSON (spec 12.8, section 6.2).
    /// </summary>
    private async Task<string?> PickLogDestinationAsync()
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
            // The same shape DiagnosticsView's picker uses: a storage-provider fault is logged
            // and treated as "no destination" rather than taking the window down.
            Log.Warning(ex, "The log save dialog could not be opened");
            return null;
        }

        // TryGetLocalPath is null for a non-filesystem location. Null means "no destination" and
        // the command returns; nothing here tries to resolve it into something writable.
        return file?.TryGetLocalPath();
    }

    /// <summary>
    /// The save dialog's options, built apart from the dialog so a case can read what they carry.
    /// </summary>
    /// <param name="startLocation">Where the dialog opens. Verification E2: without it the dialog
    /// opens wherever any picker in the process was last used, which in practice is a SCAN ROOT,
    /// because the folder pickers on the Library tab and in the setup wizard are the only other
    /// pickers most sessions open. Five default-named log files landed in a fixture library that
    /// way. Null leaves the platform's own default, which is what a platform with no Documents
    /// folder gets.</param>
    internal static FilePickerSaveOptions SaveOptions(IStorageFolder? startLocation) => new()
    {
        Title = "Save log as",
        SuggestedFileName = $"galactilog-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
        DefaultExtension = "txt",
        SuggestedStartLocation = startLocation,

        // The operating system's own overwrite confirmation, which spec 12.8 names as the only way
        // an existing file is replaced. The application never chooses the path itself and never
        // overwrites a user file without this prompt.
        ShowOverwritePrompt = true,
        FileTypeChoices = [new FilePickerFileType("Text") { Patterns = ["*.txt"] }],
    };

}
