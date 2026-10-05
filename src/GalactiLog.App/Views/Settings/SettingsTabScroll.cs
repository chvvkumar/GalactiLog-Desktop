using System.ComponentModel;
using Avalonia.Threading;

namespace GalactiLog.App.Views.Settings;

/// <summary>
/// The deferred scroll a Settings tab performs when the shell routes a reader to one of its
/// sections: run the scroll once the tab has something to scroll to.
/// </summary>
/// <remarks>
/// <para>
/// Design-lessons rule 1, and the phase review's F8. Phase 15B Task 5c wrote this wait twice in
/// one task, once on the Library tab and once on the Equipment tab, thirty-five lines each and
/// differing only in the gate and the property name. The second occurrence is where the spine
/// gets built, so it is built here and both tabs are on it.
/// </para>
/// <para>
/// <strong>Two waits, not one.</strong> Posting at <c>Loaded</c> priority is the measurement
/// wait: at attach the target section has not been measured, so <c>BringIntoView</c> would
/// scroll against a rect that is not there yet. It is not enough on a <strong>first</strong>
/// visit, which is exactly what a shell route produces: the Library tab's sections all carry
/// <c>IsVisible="{Binding IsReady}"</c> and a collapsed control has no rect at any priority, so
/// the scroll lands at the top of the page with the section it was asked for below the fold; on
/// the Equipment tab the panel sits under editors that are empty until the read publishes, so a
/// scroll taken before that lands at an offset the growing content then pushes the panel past.
/// So the caller names a real signal on the tab and the scroll waits for it. Proved on a
/// launched application, Phase 15B Task 5c: the first click landed on the tab at its top and the
/// second landed on the target.
/// </para>
/// <para>
/// A wait that never comes good (a failed read leaves the Library tab's gate false forever) is
/// correct behaviour rather than a hang: the sections are collapsed and there is nothing to
/// scroll to. The tab view-model outlives the view, so the handler is what would leak, which is
/// why this returns the caller's way of dropping it from <c>OnDetachedFromVisualTree</c>.
/// </para>
/// </remarks>
internal static class SettingsTabScroll
{
    /// <summary>
    /// Runs <paramref name="scroll"/> once <paramref name="ready"/> holds, immediately if it
    /// already does.
    /// </summary>
    /// <param name="source">The tab view-model. Its <c>PropertyChanged</c> must be raised on the
    /// UI thread, as every Settings tab's publish is, so the caller's field write and the detach
    /// cannot race.</param>
    /// <param name="ready">The gate: <c>IsReady</c> on the Library tab, <c>!IsLoading</c> on the
    /// Equipment tab. Read again on every notification rather than assumed.</param>
    /// <param name="propertyName">The property whose change can make <paramref name="ready"/>
    /// true. A null or empty name means "everything changed", so it is never filtered out.</param>
    /// <param name="scroll">The <c>BringIntoView</c> gesture, posted at <c>Loaded</c> priority.
    /// </param>
    /// <returns>How to drop a wait that is still pending, or null when the scroll was posted
    /// straight away and there is nothing to drop. Calling it twice is harmless, and so is
    /// calling it after the wait came good.</returns>
    internal static Action? RunWhenReady(
        INotifyPropertyChanged source, Func<bool> ready, string propertyName, Action scroll)
    {
        if (ready())
        {
            Dispatcher.UIThread.Post(scroll, DispatcherPriority.Loaded);
            return null;
        }

        void OnSourcePropertyChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is not (null or "")
                && !string.Equals(args.PropertyName, propertyName, StringComparison.Ordinal))
            {
                return;
            }

            if (!ready())
            {
                return;
            }

            source.PropertyChanged -= OnSourcePropertyChanged;
            Dispatcher.UIThread.Post(scroll, DispatcherPriority.Loaded);
        }

        source.PropertyChanged += OnSourcePropertyChanged;
        return () => source.PropertyChanged -= OnSourcePropertyChanged;
    }
}
