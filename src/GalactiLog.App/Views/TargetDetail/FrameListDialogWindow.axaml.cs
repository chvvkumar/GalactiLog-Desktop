using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using GalactiLog.App.ViewModels.TargetDetail;

namespace GalactiLog.App.Views.TargetDetail;

/// <summary>
/// Spec 12.4's Copy Frame List modal. The window holds no logic of its own: the subscription, the
/// <see cref="FrameListDialogViewModel.CloseRequested"/> to <c>Close(result)</c> path and the veto
/// gate belong to <see cref="ModalPageWindow{TViewModel}"/>, and the one thing this dialog decides
/// for itself is <see cref="RefuseClose"/>.
/// </summary>
public partial class FrameListDialogWindow : ModalPageWindow<FrameListDialogViewModel>
{
    public FrameListDialogWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Refuses nothing. This dialog has nothing in flight to protect: it writes the clipboard and
    /// no file, and its one read is a query whose result is discarded when the window closes. A
    /// veto here would only ever trap the dialog open.
    /// </summary>
    protected override bool RefuseClose(WindowCloseReason reason) => false;

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
