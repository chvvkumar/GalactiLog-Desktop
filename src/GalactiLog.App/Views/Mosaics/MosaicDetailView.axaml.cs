using Avalonia.Controls;
using Avalonia.Platform.Storage;
using GalactiLog.App.ViewModels.Mosaics;
using Serilog;

namespace GalactiLog.App.Views.Mosaics;

/// <summary>
/// Spec 12.17's mosaic detail page, bound to <see cref="MosaicDetailViewModel"/> on the shell's
/// detail overlay. The only logic here is Export panels' save dialog seam, the shape
/// <see cref="LogViewerView"/>'s Save log as uses: the view installs the picker while it is
/// attached and clears it only when it is still its own.
/// </summary>
public partial class MosaicDetailView : UserControl
{
    public MosaicDetailView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        BindPicker();
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        BindPicker();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (DataContext is MosaicDetailViewModel page && page.ExportDestinationPicker == PickDestinationAsync)
        {
            page.ExportDestinationPicker = null;
        }
    }

    private void BindPicker()
    {
        if (DataContext is MosaicDetailViewModel page)
        {
            page.ExportDestinationPicker = PickDestinationAsync;
        }
    }

    /// <summary>Opens the platform save dialog on the suggested name and returns the chosen
    /// absolute path, or null when the user cancelled, when there is no top level, or when the
    /// location is not on the filesystem.</summary>
    private async Task<string?> PickDestinationAsync(string suggestedFileName)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return null;
        }

        IStorageFile? file;
        try
        {
            var start = await SaveDialogStart.DocumentsAsync(storage).ConfigureAwait(true);
            file = await storage.SaveFilePickerAsync(SaveOptions(suggestedFileName, start)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The mosaic panels save dialog could not be opened");
            return null;
        }

        return file?.TryGetLocalPath();
    }

    /// <summary>The save dialog's options. The operating system's own overwrite prompt is the only
    /// way an existing file is replaced (spec 2.1.1).</summary>
    internal static FilePickerSaveOptions SaveOptions(string suggestedFileName, IStorageFolder? startLocation) => new()
    {
        Title = "Export panels",
        SuggestedFileName = suggestedFileName,
        DefaultExtension = "csv",
        SuggestedStartLocation = startLocation,
        ShowOverwritePrompt = true,
        FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
    };
}
