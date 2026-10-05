using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.Settings;

/// <summary>
/// Spec 12.7's rename history: the <c>target_renamed</c> activity events
/// <c>TargetWriteRepository.Rename</c> writes, newest first (questions.md Q11).
/// </summary>
/// <remarks>
/// It subscribes to nothing, and nothing reloads it automatically. A scan writes no rename, and
/// the only writer of one is Target detail, which is a different page: this list is re-read when
/// the Settings tab is rebuilt, or through <see cref="ReloadCommand"/>. A rename made on Target
/// detail while the Settings page is already open is therefore not shown until then. The list is
/// also bounded by the activity retention window (spec 5.12), which the query documents.
/// </remarks>
public sealed partial class RenameHistoryViewModel : ObservableObject, IDisposable
{
    private readonly Func<IReadOnlyList<RenameHistoryRow>> _load;
    private readonly TimeZoneInfo _zone;
    private readonly bool _use24Hour;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;

    private readonly CancellationTokenSource _lifetime = new();

    private int _generation;
    private bool _hasLoaded;
    private bool _disposed;

    /// <param name="load">Normally a lambda over <c>RenameHistoryQuery.Recent</c>.</param>
    /// <param name="general">For the rename time's rendering, by value. The same
    /// <see cref="GeneralSettings"/> shape every other view-model takes.</param>
    /// <param name="post">How to reach the UI thread. Defaults to <c>UiPost.Default</c>.</param>
    /// <param name="logger">Optional. A failed load is logged, never rethrown on the UI thread.
    /// </param>
    public RenameHistoryViewModel(
        Func<IReadOnlyList<RenameHistoryRow>> load,
        GeneralSettings general,
        Action<Action>? post = null,
        ILogger? logger = null)
    {
        _load = load;
        _zone = SessionTimeFormat.Resolve(general.DisplayTimezoneId);
        _use24Hour = general.Use24HTime;
        _post = post ?? UiPost.Default;
        _logger = logger ?? NullLogger.Instance;

        Load();
    }

    /// <summary>The renames, newest first, as the query ordered them.</summary>
    public ObservableCollection<RenameHistoryRowViewModel> Rows { get; } = [];

    /// <summary>Reads "No renames recorded." True only once a load has completed, so the empty
    /// state does not flash before the rows arrive.</summary>
    public bool ShowEmptyState => _hasLoaded && Rows.Count == 0;

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>A load that threw, so the section renders one neutral line instead of a bare
    /// header (the rule Task 3's review applied to the candidate list). Carries no text from the
    /// exception; the log has that.</summary>
    [ObservableProperty]
    public partial bool LoadFailed { get; private set; }

    /// <summary>The in-flight load, so a test can await it instead of sleeping.</summary>
    internal Task? PendingLoad { get; private set; }

    /// <summary>Re-reads the history. The path a host-driven refresh takes.</summary>
    internal void Reload() => Load();

    /// <summary>Re-reads the history and completes with that read.</summary>
    [RelayCommand]
    private async Task ReloadAsync()
    {
        Load();
        if (PendingLoad is { } load)
        {
            await load.ConfigureAwait(false);
        }
    }

    private void Load()
    {
        if (_disposed)
        {
            return;
        }

        IsLoading = true;
        var generation = ++_generation;
        var token = _lifetime.Token;
        PendingLoad = Task.Run(
            () =>
            {
                try
                {
                    var rows = _load();
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    _post(() => Publish(generation, rows));
                }
                catch (OperationCanceledException)
                {
                    // The host went away while the read was in flight.
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Loading the rename history failed");
                    _post(() => Publish(generation, null));
                }
            },
            token);
    }

    private void Publish(int generation, IReadOnlyList<RenameHistoryRow>? rows)
    {
        if (generation != _generation || _disposed)
        {
            return;
        }

        if (rows is not null)
        {
            Rows.Clear();
            foreach (var row in rows)
            {
                Rows.Add(new RenameHistoryRowViewModel(row, _zone, _use24Hour));
            }

            _hasLoaded = true;
        }

        LoadFailed = rows is null;
        IsLoading = false;
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    /// <summary>Cancels the lifetime source, so nothing this list started can publish into a host
    /// whose database is gone.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

/// <summary>
/// One rename: the previous name, the new name, when it happened, and where that target's name
/// stands now. Formatting only.
/// </summary>
/// <remarks>
/// The rename time is a stored instant, so it renders through <see cref="SessionTimeFormat"/> plus
/// <see cref="MetricText.Date"/>: the one <c>general.timezone</c> / <c>general.use_24h_time</c>
/// path the session cards and the merge history already use (collision-map designated owners).
/// Nothing here re-implements either half.
/// </remarks>
public sealed class RenameHistoryRowViewModel
{
    public RenameHistoryRowViewModel(RenameHistoryRow row, TimeZoneInfo zone, bool use24Hour)
    {
        Row = row;

        var local = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(row.Timestamp, DateTimeKind.Utc),
            zone);

        RenamedAtText = MetricText.Date(DateOnly.FromDateTime(local))
            + " "
            + SessionTimeFormat.Format(row.Timestamp, zone, use24Hour);
    }

    public RenameHistoryRow Row { get; }

    public string PreviousName => Row.PreviousName;

    public string NewName => Row.NewName;

    public string RenamedAtText { get; }

    /// <summary>What the target is called now, when that is neither the name this row recorded nor
    /// gone. A later rename, or a deleted target, is the only way the two differ.</summary>
    public string CurrentNameText => Row.CurrentName is { } current && current != Row.NewName
        ? $"now {current}"
        : "";

    public bool HasCurrentName => CurrentNameText.Length > 0;
}
