using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GalactiLog.App.Services;

/// <summary>
/// The application's <see cref="INotifier"/>: the list of notices <c>Controls/NoticeHost</c>
/// draws over the page.
/// </summary>
/// <remarks>
/// Every mutation of <see cref="Notices"/> happens inside the post seam, so the collection is only
/// ever touched on the UI thread, the same rule <see cref="JobRegistry"/> follows. Timers go
/// through a second seam so a test can fire them by hand rather than sleep.
/// </remarks>
public sealed class Notifier : INotifier
{
    /// <summary>How many notices the stack holds. A fourth pushes the oldest out.</summary>
    public const int Cap = 3;

    /// <summary>How long a Success or Info notice stays when the caller names no span.</summary>
    public static readonly TimeSpan DefaultAutoDismiss = TimeSpan.FromSeconds(3);

    /// <summary>How long a dismissed notice takes to fade before it leaves the list. Matches the
    /// 150 ms opacity transition in <c>Theme/Feedback.axaml</c>.</summary>
    public static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(150);

    private readonly Action<Action> _post;
    private readonly Action<TimeSpan, Action> _schedule;

    /// <param name="post">How to reach the UI thread. Defaults to <c>UiPost.Default</c>.</param>
    /// <param name="schedule">Runs an action after a delay, on the UI thread. Defaults to a
    /// <see cref="Task.Delay(TimeSpan)"/> continuation that posts back through
    /// <paramref name="post"/>.</param>
    public Notifier(Action<Action>? post = null, Action<TimeSpan, Action>? schedule = null)
    {
        _post = post ?? UiPost.Default;
        _schedule = schedule ?? ((delay, action) =>
            _ = Task.Delay(delay).ContinueWith(_ => _post(action), TaskScheduler.Default));
    }

    /// <summary>The notices on screen, oldest first.</summary>
    public ObservableCollection<NoticeViewModel> Notices { get; } = [];

    /// <inheritdoc />
    public void Show(string text, NoticeKind kind, TimeSpan? autoDismiss = null)
    {
        var stay = autoDismiss ?? (kind is NoticeKind.Success or NoticeKind.Info ? DefaultAutoDismiss : null);
        _post(() =>
        {
            var notice = new NoticeViewModel(text, kind, Dismiss);
            Notices.Add(notice);

            // The oldest goes at once, without its fade: the stack never shows a fourth.
            while (Notices.Count > Cap)
            {
                Notices.RemoveAt(0);
            }

            if (stay is { } span && span > TimeSpan.Zero)
            {
                _schedule(span, () => Dismiss(notice));
            }
        });
    }

    /// <summary>Fades a notice out and then removes it. Called on the UI thread, by the notice's
    /// dismiss button or by its own timer. A second call, or a call for a notice the cap already
    /// pushed out, does nothing.</summary>
    public void Dismiss(NoticeViewModel notice)
    {
        if (notice.IsClosing || !Notices.Contains(notice))
        {
            return;
        }

        notice.IsClosing = true;
        _schedule(FadeDuration, () => Notices.Remove(notice));
    }
}

/// <summary>One notice in the stack.</summary>
public sealed partial class NoticeViewModel : ObservableObject
{
    private readonly Action<NoticeViewModel> _dismiss;

    internal NoticeViewModel(string text, NoticeKind kind, Action<NoticeViewModel> dismiss)
    {
        Text = text;
        Kind = kind;
        _dismiss = dismiss;
    }

    public string Text { get; }

    public NoticeKind Kind { get; }

    // One flag per kind, so the view picks the callout colours with Classes bindings rather than
    // a converter.
    public bool IsSuccess => Kind == NoticeKind.Success;

    public bool IsInfo => Kind == NoticeKind.Info;

    public bool IsWarning => Kind == NoticeKind.Warning;

    public bool IsError => Kind == NoticeKind.Error;

    /// <summary>True from the moment the notice starts fading out. The view binds it to the
    /// <c>closing</c> class, whose zero opacity the 150 ms transition animates.</summary>
    [ObservableProperty]
    public partial bool IsClosing { get; internal set; }

    [RelayCommand]
    private void Dismiss() => _dismiss(this);
}
