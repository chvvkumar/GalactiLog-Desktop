using Avalonia.Controls;
using GalactiLog.App.ViewModels;

namespace GalactiLog.App.Views;

/// <summary>
/// The shared spine for the three modal dialog windows (FIXER LIST F20): the subscribed-view-model
/// field, the detach-and-reattach on a DataContext change, the
/// <see cref="IModalPageViewModel.CloseRequested"/> to <c>Close(result)</c> path, and the one
/// question each dialog answers for itself, <see cref="RefuseClose"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>MergeDialogWindow</c>, <c>ResetConfirmWindow</c> and <c>SetupWizardWindow</c> each wrote all
/// four by hand with three different close-reason policies, and the divergence cost one Important
/// review finding: two of them vetoed an OS shutdown while a write was in flight, which keeps the
/// process alive with Windows reporting the application as blocking the session from ending. The
/// policy is now one overridden method per dialog and nothing else, so a fourth modal cannot
/// forget the part that is not about it.
/// </para>
/// <para>
/// The XAML root of each dialog stays <c>&lt;Window&gt;</c>: this base adds no visual tree and no
/// styling, so the compiled-XAML loader sees the same root type it always did.
/// </para>
/// </remarks>
/// <typeparam name="TViewModel">The dialog's page view-model.</typeparam>
public abstract class ModalPageWindow<TViewModel> : Window
    where TViewModel : class, IModalPageViewModel
{
    private TViewModel? _subscribed;
    private bool _closingFromPage;

    /// <summary>The attached page, or null when the DataContext is not one (which is what a
    /// designer-time or bare construction leaves).</summary>
    protected TViewModel? Page => _subscribed;

    /// <summary>
    /// Whether the page itself asked to close. Sealed into <see cref="OnClosing"/> rather than left
    /// to each dialog: a veto that also refused the page's own close would trap the dialog open
    /// after the action it was waiting for had finished.
    /// </summary>
    protected bool ClosingFromPage => _closingFromPage;

    /// <summary>
    /// Which close requests this dialog refuses. Called only for a
    /// <see cref="WindowCloseReason.WindowClosing"/> that the page did not ask for.
    /// </summary>
    /// <remarks>
    /// <see cref="WindowCloseReason.OSShutdown"/>, <see cref="WindowCloseReason.ApplicationShutdown"/>
    /// and <see cref="WindowCloseReason.OwnerWindowClosing"/> pass through on every dialog, and
    /// <see cref="OnClosing"/> enforces that rather than each override
    /// (phase review finding P6). An implementation may still switch on
    /// <paramref name="reason"/>; it will only ever be handed the one candidate for a veto, the
    /// title bar and Alt+F4.
    /// </remarks>
    protected abstract bool RefuseClose(WindowCloseReason reason);

    protected sealed override void OnDataContextChanged(EventArgs e)
    {
        if (_subscribed is not null)
        {
            _subscribed.CloseRequested -= OnCloseRequested;
        }

        _subscribed = DataContext as TViewModel;

        if (_subscribed is not null)
        {
            _subscribed.CloseRequested += OnCloseRequested;
        }

        base.OnDataContextChanged(e);
    }

    protected sealed override void OnClosing(WindowClosingEventArgs e)
    {
        // The close-reason policy is enforced here, at the one registration point, rather than in
        // each dialog's RefuseClose (design-lessons rule 2, phase review finding P6). Phase 11
        // made the rule load-bearing: a vetoed close makes desktop.TryShutdown() return false
        // AFTER the ShutdownRequested handler has marked the residency service exiting and drained
        // the watcher, the scheduler, the update loop, the thumbnail worker, the scan completion
        // watcher and the single-instance listener, leaving a live process with every subsystem
        // stopped and nothing that restarts any of them (WindowResidencyService.RequestExit's
        // remarks). All four overrides are correct today; a fifth dialog cannot reintroduce it.
        if (!_closingFromPage &&
            e.CloseReason == WindowCloseReason.WindowClosing &&
            RefuseClose(e.CloseReason))
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    private void OnCloseRequested(object? sender, bool result)
    {
        _closingFromPage = true;
        Close(result);
    }
}
