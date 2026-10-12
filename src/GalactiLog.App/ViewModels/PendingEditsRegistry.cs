using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels;

/// <summary>
/// App-wide list of surfaces with staged, unsaved edits. Exists so one save bar can answer "is
/// anything unsaved" and save or discard all of it, instead of every tab hiding its own Save
/// button where the user (and the scan, which reads disk) cannot see it. Sources raise on the UI
/// thread, so the registry does no marshalling and holds no lock.
/// </summary>
public sealed partial class PendingEditsRegistry : ObservableObject
{
    private readonly ILogger _logger;
    private readonly INotifier? _notifier;
    private readonly Action<Action> _post;
    private readonly List<IPendingEdits> _sources = [];

    /// <param name="logger">Optional. A source whose save threw is logged here.</param>
    /// <param name="notifier">Optional. A Save all after which every saved tab is clean says
    /// "Settings saved"; one that left a tab with unsaved changes says which, as an error.</param>
    /// <param name="post">How to reach the UI thread. Defaults to <c>UiPost.Default</c>. The
    /// verdict is read through it: a tab clears its dirty flag in a callback it posts when its
    /// write lands, which is queued before its save task completes, so a check posted after the
    /// last save runs after every one of those callbacks.</param>
    public PendingEditsRegistry(
        ILogger<PendingEditsRegistry>? logger = null, INotifier? notifier = null, Action<Action>? post = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _notifier = notifier;
        _post = post ?? UiPost.Default;

        // Discard is refused for the whole of a Save all, so its button has to re-read that the
        // moment the save starts and the moment it ends.
        SaveAllCommand.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SaveAllCommand.IsRunning))
            {
                DiscardAllCommand.NotifyCanExecuteChanged();
            }
        };
    }

    public IReadOnlyList<IPendingEdits> Pending { get; private set; } = [];

    public bool AnyPending { get; private set; }

    public string Summary { get; private set; } = "";

    public string? SaveRefusal { get; private set; }

    public bool CanSave => AnyPending && SaveRefusal is null;

    /// <summary>A one-line message another part of the app wants shown beside the bar (the scan
    /// gate uses it). Cleared automatically when <see cref="AnyPending"/> becomes false.</summary>
    [ObservableProperty]
    private string? _notice;

    public void Register(IPendingEdits source)
    {
        if (_sources.Contains(source)) return;
        _sources.Add(source);
        source.PropertyChanged += OnSourceChanged;
        Recompute();
    }

    public void Unregister(IPendingEdits source)
    {
        if (!_sources.Remove(source)) return;
        source.PropertyChanged -= OnSourceChanged;
        Recompute();
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IPendingEdits.HasPendingEdits) or nameof(IPendingEdits.SaveRefusal)
            or null or "")
        {
            Recompute();
        }
    }

    private void Recompute()
    {
        var wasPending = AnyPending;
        Pending = _sources.Where(s => s.HasPendingEdits).ToList();
        AnyPending = Pending.Count > 0;
        Summary = AnyPending ? "Unsaved changes: " + string.Join(", ", Pending.Select(p => p.Label)) : "";
        SaveRefusal = Pending.Select(p => p.SaveRefusal).FirstOrDefault(r => r is not null);

        OnPropertyChanged(nameof(Pending));
        if (wasPending != AnyPending) OnPropertyChanged(nameof(AnyPending));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(SaveRefusal));
        OnPropertyChanged(nameof(CanSave));
        SaveAllCommand.NotifyCanExecuteChanged();
        DiscardAllCommand.NotifyCanExecuteChanged();

        if (!AnyPending) Notice = null;
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAllAsync()
    {
        var attempted = Pending.ToList();
        var threw = new List<IPendingEdits>();
        foreach (var source in attempted)
        {
            try { await source.SaveAsync(); }
            catch (Exception ex)
            {
                threw.Add(source);
                _logger.LogError(ex, "Saving {Label} failed", source.Label);
            }
        }

        if (_notifier is null)
        {
            return;
        }

        // A tab does not report a refused or invalid save by throwing: it returns with its edits
        // still pending. Whether anything is still unsaved is therefore the only honest verdict.
        _post(() =>
        {
            var unsaved = attempted.Where(source => source.HasPendingEdits || threw.Contains(source)).ToList();
            if (unsaved.Count == 0)
            {
                _notifier.Show(SavedNotice, NoticeKind.Success);
            }
            else
            {
                _notifier.Show(NotSavedNotice(unsaved.Select(source => source.Label)), NoticeKind.Error);
            }
        });
    }

    /// <summary>The notice a successful Save all raises.</summary>
    public const string SavedNotice = "Settings saved";

    /// <summary>The notice a Save all raises when a tab still has unsaved changes after it.</summary>
    public static string NotSavedNotice(IEnumerable<string> labels)
    {
        var list = labels.ToList();
        return "Settings not saved: " + string.Join(", ", list)
            + (list.Count == 1 ? " still has" : " still have") + " unsaved changes";
    }

    // Not while Save all is running: a tab discarded mid-save would show the pre-save document
    // while disk holds the saved one, and its next save would undo this one. Each tab also defers
    // its own Discard until its own save lands; this keeps the button honest about that.
    private bool CanDiscardAll() => AnyPending && !SaveAllCommand.IsRunning;

    [RelayCommand(CanExecute = nameof(CanDiscardAll))]
    private void DiscardAll()
    {
        // TRACKING item 13: CanExecute is the affordance, the body is the guard.
        if (!CanDiscardAll()) return;

        foreach (var source in Pending.ToList()) source.Discard();
    }
}
