using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using GalactiLog.App.ViewModels.Update;

namespace GalactiLog.App.Views.Update;

/// <summary>
/// Design-spec 17.1's confirmation before an update is applied. The window holds no logic of its
/// own: the subscription, the <see cref="UpdatePromptViewModel.CloseRequested"/> to
/// <c>Close(result)</c> path and the veto gate belong to <see cref="ModalPageWindow{TViewModel}"/>,
/// and the one thing this dialog decides for itself is <see cref="RefuseClose"/>.
/// </summary>
public partial class UpdatePromptView : ModalPageWindow<UpdatePromptViewModel>
{
    public UpdatePromptView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Refuses nothing. Dismissing this dialog is the "Later" answer, so the title bar, Alt+F4,
    /// an owner-window close and a session ending all mean the same thing: no update is applied
    /// and the next check offers it again.
    /// </summary>
    protected override bool RefuseClose(WindowCloseReason reason) => false;

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
