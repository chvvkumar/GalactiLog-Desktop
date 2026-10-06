using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using GalactiLog.App.ViewModels.Mosaics;

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

    /// <summary>Escape goes Back, as on the target page, unless a text box has focus: there it
    /// belongs to the box (the rename box cancels, the notes box keeps it).</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !IsTypingInATextBox(e) && DataContext is MosaicDetailViewModel page)
        {
            page.BackCommand.Execute(null);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private bool IsTypingInATextBox(KeyEventArgs e)
        => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox
           || e.Source is TextBox
           || (e.Source is Visual visual && visual.FindAncestorOfType<TextBox>() is not null);

    private void BindPicker()
    {
        if (DataContext is MosaicDetailViewModel page)
        {
            page.ExportDestinationPicker = PickDestinationAsync;
        }
    }

    private Task<string?> PickDestinationAsync(string suggestedFileName)
        => SaveDialogStart.PickPathAsync(this, "Export panels", suggestedFileName, "csv", "CSV");
}
