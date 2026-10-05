using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using GalactiLog.App.ViewModels.Merge;

namespace GalactiLog.App.Views.Merge;

/// <summary>
/// Spec 12.9's merge preview modal. The window holds no logic of its own: the subscription, the
/// <see cref="MergeDialogViewModel.CloseRequested"/> to <c>Close(result)</c> path and the veto gate
/// belong to <see cref="ModalPageWindow{TViewModel}"/>, and the one thing this dialog decides for
/// itself is <see cref="RefuseClose"/>.
/// </summary>
public partial class MergeDialogWindow : ModalPageWindow<MergeDialogViewModel>
{
    public MergeDialogWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Refuses the user's own dismissal while a merge is being written (review finding 2), and only
    /// then (Phase 7 fixer item 2: the flag was <c>IsBusy</c>, which is also true during every
    /// preview read, so the window refused to close while a preview loaded). What is blocked is the
    /// title-bar close and anything else that would abandon the dialog mid-transaction and report
    /// that nothing happened.
    /// </summary>
    /// <remarks>
    /// An OS shutdown, an application shutdown and an owner-window close go through even mid-write
    /// (FIXER LIST F20): the merge runs in one SQLite transaction, so a process ended underneath it
    /// leaves no half-merged catalogue, and cancelling a shutdown is a far worse outcome than
    /// losing the merge.
    /// </remarks>
    protected override bool RefuseClose(WindowCloseReason reason)
        => reason == WindowCloseReason.WindowClosing && Page is { IsWriting: true };

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
