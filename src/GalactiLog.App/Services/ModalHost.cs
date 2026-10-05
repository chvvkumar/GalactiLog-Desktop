using Avalonia.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.Services;

/// <summary>
/// The one place a modal window is opened in this application. Spec 12.9's merge preview and spec
/// 11.5's frame preview are both modals, not pages: <c>MainWindowViewModel.Detail</c> is the
/// shell's only non-rail overlay (Phase 7 ruling Q9), so a second overlay would be a second
/// navigation owner.
/// </summary>
/// <remarks>
/// Extracted at the second occurrence of the pattern, not the sixth (design-lessons rule 1). The
/// owner window is reached at show time through the same deferred lookup
/// <see cref="ShellIntegration"/>'s clipboard delegate uses, because no window exists while the
/// host is being built. A missing owner makes <see cref="ShowAsync{TResult}"/> a no-op returning
/// <c>default</c> rather than an exception: a dialog with no window to show in has decided nothing.
/// <para>
/// This type takes no view-model factory and knows about no dialog. Two dialogs, one host, zero
/// knowledge of either.
/// </para>
/// </remarks>
/// <param name="owner">How to reach the window a dialog is shown over. Evaluated per call.</param>
/// <param name="logger">Optional. A missing owner and a failed show are logged, never rethrown.
/// </param>
public sealed class ModalHost(Func<Window?> owner, ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>Opens a modal window over the current owner and completes with what it closed
    /// with. Must be called on the UI thread: it builds a window.</summary>
    /// <param name="create">Builds the window and its data context. Runs on the UI thread, inside
    /// the try, so a constructor that throws is logged like a show that fails.</param>
    /// <param name="cleanup">Runs in a finally, whether the dialog closed, threw, or was never
    /// opened. This is where a disposable page view-model is disposed.</param>
    public async Task<TResult?> ShowAsync<TResult>(Func<Window> create, Action? cleanup = null)
    {
        ArgumentNullException.ThrowIfNull(create);

        try
        {
            var parent = owner();
            if (parent is null)
            {
                _logger.LogWarning("A modal window was requested with no owner window; nothing was opened.");
                return default;
            }

            // ConfigureAwait(true): the continuation runs on the UI thread, and the finally below
            // disposes a UI-thread object.
            return await create().ShowDialog<TResult>(parent).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // A dialog that could not be built or shown has decided nothing.
            _logger.LogWarning(ex, "Showing a modal window failed");
            return default;
        }
        finally
        {
            // One call site, so cleanup runs exactly once on every path.
            cleanup?.Invoke();
        }
    }
}
