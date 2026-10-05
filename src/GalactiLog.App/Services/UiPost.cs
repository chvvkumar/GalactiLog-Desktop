using Avalonia.Threading;

namespace GalactiLog.App.Services;

/// <summary>
/// The default "how to reach the UI thread" seam (phase review item 5). Three types took an
/// <c>Action&lt;Action&gt;? post</c> and each spelled the same default inline; one definition means
/// a change to how the application marshals onto the dispatcher is a change in one place.
/// </summary>
internal static class UiPost
{
    public static readonly Action<Action> Default = action => Dispatcher.UIThread.Post(action);
}
