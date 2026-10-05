using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using GalactiLog.App.ViewModels.TargetDetail;

namespace GalactiLog.App.Views.TargetDetail;

/// <summary>
/// Spec 12.17's Create mosaic modal. The subscription, the close path and the veto gate belong to
/// <see cref="ModalPageWindow{TViewModel}"/>; this dialog decides only <see cref="RefuseClose"/>.
/// </summary>
public partial class CreateMosaicWindow : ModalPageWindow<CreateMosaicViewModel>
{
    public CreateMosaicWindow()
    {
        InitializeComponent();
    }

    /// <summary>Refuses nothing: its one write is synchronous and finishes before any close can
    /// be asked for.</summary>
    protected override bool RefuseClose(WindowCloseReason reason) => false;

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
