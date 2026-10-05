using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using GalactiLog.App.ViewModels.Settings;

namespace GalactiLog.App.Views.Settings;

/// <summary>
/// Spec 12.7's typed confirmation for reset database. The window holds no logic of its own: the
/// subscription, the <see cref="ResetConfirmViewModel.CloseRequested"/> to <c>Close(result)</c>
/// path and the veto gate belong to <see cref="ModalPageWindow{TViewModel}"/>, and the one thing
/// this dialog decides for itself is <see cref="RefuseClose"/>.
/// </summary>
public partial class ResetConfirmWindow : ModalPageWindow<ResetConfirmViewModel>
{
    public ResetConfirmWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Refuses the user's own dismissal while the delete is in flight, and only then. What is
    /// blocked is the title-bar close and anything else that would abandon the dialog
    /// mid-transaction and report that nothing happened (spec 12.9's rule for the merge dialog,
    /// applied for the same reason).
    /// </summary>
    /// <remarks>
    /// An OS shutdown, an application shutdown and an owner-window close go through even mid-reset
    /// (FIXER LIST F20), for the reason they do on the merge dialog: a process ended underneath the
    /// reset is recoverable, a session Windows cannot end is not.
    /// </remarks>
    protected override bool RefuseClose(WindowCloseReason reason)
        => reason == WindowCloseReason.WindowClosing && Page is { IsResetting: true };

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
