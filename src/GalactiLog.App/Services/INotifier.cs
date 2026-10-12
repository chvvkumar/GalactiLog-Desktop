namespace GalactiLog.App.Services;

/// <summary>The four tones an in-window notice can take. Each maps to one pair of the themes'
/// callout composites (<c>Color*CalloutFill</c> and <c>Color*CalloutBorder</c>).</summary>
public enum NoticeKind
{
    /// <summary>An action completed. Dismisses itself.</summary>
    Success,

    /// <summary>Something worth knowing that needs no action. Dismisses itself.</summary>
    Info,

    /// <summary>Something the user should look at. Stays until dismissed.</summary>
    Warning,

    /// <summary>An action failed. Stays until dismissed.</summary>
    Error,
}

/// <summary>
/// The one way anything in the application confirms an action to the user: a short notice at the
/// top centre of the main window. In-window only, never a Windows toast (owner decision).
/// </summary>
/// <remarks>
/// Jobs do not call this themselves. <see cref="JobRegistry"/> calls it from
/// <see cref="JobHandle.Finish"/>, so every registered job reports through one path and a new job
/// cannot forget to (design-lessons rule 2).
/// </remarks>
public interface INotifier
{
    /// <summary>Shows a notice. Callable from any thread.</summary>
    /// <param name="text">The sentence to show.</param>
    /// <param name="kind">The tone.</param>
    /// <param name="autoDismiss">How long the notice stays. Null takes the kind's default: three
    /// seconds for <see cref="NoticeKind.Success"/> and <see cref="NoticeKind.Info"/>, until
    /// dismissed for <see cref="NoticeKind.Warning"/> and <see cref="NoticeKind.Error"/>. A zero
    /// or negative span (for example <see cref="Timeout.InfiniteTimeSpan"/>) means until
    /// dismissed.</param>
    void Show(string text, NoticeKind kind, TimeSpan? autoDismiss = null);
}
