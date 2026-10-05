using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.Core.Metrics;
using GalactiLog.Core.Settings;
using GalactiLog.Core.Text;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.TargetDetail;

/// <summary>One night the Copy Frame List dialog was opened over.</summary>
/// <param name="Date">The session date, in the ledger's own order at the call site.</param>
/// <param name="Loaded">The night's detail when the page has already loaded it, null when it has
/// not. A night already loaded is reached through this value, so a user who has been looking at
/// two nights pays for nothing (spec 12.4's queries paragraph).</param>
public sealed record FrameListNight(DateOnly Date, SessionDetail? Loaded);

/// <summary>One entry of the format combo box.</summary>
/// <param name="Value">The format itself.</param>
/// <param name="Label">What the combo box shows.</param>
public sealed record FrameListFormatOption(FrameListFormat Value, string Label);

/// <summary>
/// Spec 12.4's Copy Frame List dialog (PAR-006, rulings C5 and C6): a mode, an include-unmeasured
/// box, a live tally, a partial-load warning, three output formats and a Copy button.
/// </summary>
/// <remarks>
/// <para>
/// It writes the clipboard and nothing else. No file is created, moved, renamed or deleted, and no
/// move script is offered: the web's PowerShell and bash move scripts and its in-browser move are
/// excluded by decision (spec 2.1 and 19.2). <c>FileSafetyTest</c> proves that structurally over
/// <c>src/**</c> and this type adds no write-capable call for it to find.
/// </para>
/// <para>
/// The verdict per frame is the row score band, which is <see cref="FrameQuality.BandForScore"/>
/// over <see cref="FrameQuality.CombinedScore"/> of the frame's three axes under the page's active
/// baseline. Not the per-cell bands and not the outlier flags. Nothing here re-derives a band: the
/// deviations come from <c>FrameRow.Grading</c>, which the session query already computed, and the
/// two ladders are the Core functions the frame table reads, so the dialog and the table cannot
/// disagree.
/// </para>
/// </remarks>
public sealed partial class FrameListDialogViewModel : ObservableObject, IModalPageViewModel, IDisposable
{
    private readonly string _groupKey;
    private readonly IReadOnlyList<FrameListNight> _nights;
    private readonly Func<string, DateOnly, SessionDetail?> _getDetail;
    private readonly Func<string, Task> _copyText;
    private readonly TargetPageState _targetPage;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;

    // Every frame of every night that loaded, in ledger order then capture order, with its verdict
    // already decided. Built once, because neither the mode nor the box nor the format changes a
    // verdict; they only change which verdicts join the list.
    private IReadOnlyList<GradedPath> _frames = [];

    // The dialog can be dismissed while the first read is still parked on a locked database, and
    // a publish after that would raise property changes on a page nothing is bound to. The shape
    // TargetDetailViewModel already uses.
    private bool _disposed;

    /// <inheritdoc />
    public event EventHandler<bool>? CloseRequested;

    /// <param name="groupKey">The page's group key, which keys the per-night query.</param>
    /// <param name="nights">The checked nights, in the ledger's own order, newest first. Each
    /// carries the page's already-loaded detail or null.</param>
    /// <param name="getDetail">Normally <c>SessionDetailQuery.Get</c>, the same delegate shape
    /// <see cref="SessionCardViewModel"/> takes. Called off the UI thread, once per checked night
    /// whose detail the page has not already loaded.</param>
    /// <param name="copyText">Normally <c>ShellIntegration.CopyTextAsync</c>. The only thing this
    /// dialog writes.</param>
    /// <param name="targetPage">P13 R5's live <c>display.target_page</c> holder, which carries
    /// spec 5.8.2's three <c>frame_list_*</c> keys. Optional and trailing; null gives the dialog a
    /// private holder on the fresh-profile defaults that writes nowhere.</param>
    /// <param name="post">How to reach the UI thread. Defaults to <c>UiPost.Default</c>.</param>
    /// <param name="logger">Optional. A night that failed to load is logged and counted, never
    /// rethrown on the UI thread.</param>
    public FrameListDialogViewModel(
        string groupKey,
        IReadOnlyList<FrameListNight> nights,
        Func<string, DateOnly, SessionDetail?> getDetail,
        Func<string, Task> copyText,
        TargetPageState? targetPage = null,
        Action<Action>? post = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(nights);
        ArgumentNullException.ThrowIfNull(getDetail);
        ArgumentNullException.ThrowIfNull(copyText);

        _groupKey = groupKey;
        _nights = nights;
        _getDetail = getDetail;
        _copyText = copyText;
        _targetPage = targetPage ?? new TargetPageState();
        _post = post ?? UiPost.Default;
        _logger = logger ?? NullLogger.Instance;

        // Backing fields, not the properties: each setter persists its key, and assigning here
        // would queue a write of the three values just read out of the document. The same rule
        // TargetPageState's own constructor follows.
        _mode = FrameListFormats.ParseMode(_targetPage.FrameListMode);
        _includeUnmeasured = _targetPage.FrameListIncludeUnmeasured;
        _selectedFormat = AllFormats.First(
            option => option.Value == FrameListFormats.Parse(_targetPage.FrameListFormat));

        RefreshTally();
        Load();
    }

    /// <summary>The three formats spec 12.4's table lists, in the combo box's own order, with
    /// absolute paths first because it is the default (ruling C6).</summary>
    public static IReadOnlyList<FrameListFormatOption> AllFormats { get; } =
    [
        new(FrameListFormat.Paths, "Absolute paths"),
        new(FrameListFormat.Names, "File names"),
        new(FrameListFormat.Explorer, "Explorer search"),
    ];

    /// <summary>The combo box's own binding path. An instance member over
    /// <see cref="AllFormats"/> rather than the static list itself, because a compiled binding
    /// walks the data context and reaches no static member.</summary>
    public IReadOnlyList<FrameListFormatOption> Formats => AllFormats;

    /// <summary>The first read, exposed so a test settles on it rather than on a timer. Null only
    /// before the constructor has started it.</summary>
    public Task? PendingLoad { get; private set; }

    /// <summary>Spec 12.4's mode: Good lists the frames the grading did not reject, Bad lists the
    /// frames it rejected. Persisted in <c>display.target_page.frame_list_mode</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGoodMode))]
    [NotifyPropertyChangedFor(nameof(IsIncludeUnmeasuredOffered))]
    private FrameListMode _mode;

    /// <summary>Spec 12.4's include-unmeasured box. Offered in Good mode only and ignored in Bad
    /// mode, because an unmeasured frame is never bad. Persisted in
    /// <c>display.target_page.frame_list_include_unmeasured</c>.</summary>
    [ObservableProperty]
    private bool _includeUnmeasured;

    /// <summary>Spec 12.4's format combo box. Persisted in
    /// <c>display.target_page.frame_list_format</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Format))]
    private FrameListFormatOption _selectedFormat;

    /// <summary>The two-button segment's binding: the Good button binds this and the Bad button
    /// binds its negation, which is the shape the Statistics timeline segment already uses.
    /// </summary>
    public bool IsGoodMode
    {
        get => Mode == FrameListMode.Good;
        set => Mode = value ? FrameListMode.Good : FrameListMode.Bad;
    }

    /// <summary>Whether the box is on screen. Bad mode hides it rather than greying it: the value
    /// it holds still means something in Good mode and is not lost by switching.</summary>
    public bool IsIncludeUnmeasuredOffered => Mode == FrameListMode.Good;

    /// <summary>The format the Copy button will render, which is what a case asserts rather than
    /// the combo box's own option object.</summary>
    public FrameListFormat Format => SelectedFormat.Value;

    /// <summary>How many of the checked nights failed to load. Zero on the ordinary path.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPartialLoad))]
    private int _failedNightCount;

    /// <summary>How many nights the dialog was opened over.</summary>
    public int NightCount => _nights.Count;

    /// <summary>Spec 12.4's partial-load warning, which names how many of how many nights failed,
    /// which dates they were, and that the list below covers only the loaded nights.</summary>
    [ObservableProperty]
    private string _partialLoadText = "";

    /// <summary>Drives the <c>Border.callout warn</c> line's visibility with no converter.
    /// </summary>
    public bool HasPartialLoad => FailedNightCount > 0;

    /// <summary>Spec 12.4's live tally line.</summary>
    [ObservableProperty]
    private string _tallyText = "";

    /// <summary>How many frames the current mode, box and verdicts put in the list. Drives the
    /// Copy button's enablement.</summary>
    [ObservableProperty]
    private int _listCount;

    /// <summary>How many frames the loaded nights hold in total, which is the tally's m.</summary>
    [ObservableProperty]
    private int _frameCount;

    /// <summary>The dates that failed to load, in the order they were asked for. Exposed so a
    /// case can name them rather than parse the sentence.</summary>
    public IReadOnlyList<DateOnly> FailedDates { get; private set; } = [];

    /// <summary>The absolute paths the current mode, box and verdicts select, in the order the
    /// frame tables show the rows: nights in ledger order, newest first, and each night's frames
    /// in its own capture order, which is the order <c>SessionDetailQuery</c> returns them in.
    /// </summary>
    public IReadOnlyList<string> SelectedPaths()
    {
        var paths = new List<string>(_frames.Count);
        foreach (var frame in _frames)
        {
            if (Includes(frame.Verdict, Mode, IncludeUnmeasured))
            {
                paths.Add(frame.Path);
            }
        }

        return paths;
    }

    /// <summary>Spec 12.4's truth table, which is the one place the mode, the box and the verdict
    /// meet. Unmeasured is never bad: it is a frame the grading could not judge, and copying it
    /// into a Bad list would be a claim the grading did not make.</summary>
    /// <param name="band">The frame's row score band, null when the grading produced no score.
    /// </param>
    /// <param name="mode">The dialog's mode.</param>
    /// <param name="includeUnmeasured">The box, which is read in Good mode only.</param>
    public static bool Includes(QualityBand? band, FrameListMode mode, bool includeUnmeasured)
    {
        if (band is not { } verdict)
        {
            return mode == FrameListMode.Good && includeUnmeasured;
        }

        return mode == FrameListMode.Bad
            ? verdict == QualityBand.Reject
            : verdict != QualityBand.Reject;
    }

    /// <summary>Spec 12.4's Copy: writes the rendered list to the clipboard and closes. Disabled
    /// when the list is empty.</summary>
    [RelayCommand(CanExecute = nameof(CanCopy))]
    private async Task CopyAsync()
    {
        // TRACKING section 6 item 13: RelayCommand.Execute ignores CanExecute, so the rule that
        // makes the command correct is in the body as well as in the guard. An empty list copies
        // nothing at all, never a lone newline.
        var paths = SelectedPaths();
        if (paths.Count == 0)
        {
            return;
        }

        await _copyText(FrameListFormats.Render(Format, paths)).ConfigureAwait(true);
        CloseRequested?.Invoke(this, true);
    }

    private bool CanCopy() => ListCount > 0;

    /// <summary>Closes without copying.</summary>
    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    // Reads the checked nights, querying only the ones the page has not already loaded, and
    // publishes the graded frame list and the two lines over it. Off the UI thread: the query is a
    // synchronous SQLite read and the dialog is constructed on the UI thread from a command.
    private void Load()
    {
        var nights = _nights;
        var groupKey = _groupKey;
        var baseline = _targetPage.GradingBaseline;

        PendingLoad = Task.Run(() =>
        {
            var frames = new List<GradedPath>();
            var failed = new List<DateOnly>();

            foreach (var night in nights)
            {
                SessionDetail? detail;
                try
                {
                    // Already loaded: no second query, whatever the night holds (spec 12.4).
                    detail = night.Loaded ?? _getDetail(groupKey, night.Date);
                }
                catch (Exception ex)
                {
                    // A night that could not be read is a night the list does not cover, and the
                    // warning says so. Never rethrown on the UI thread.
                    _logger.LogWarning(ex, "Reading {Date} for the frame list failed", night.Date);
                    detail = null;
                }

                if (detail is null)
                {
                    failed.Add(night.Date);
                    continue;
                }

                foreach (var frame in detail.Frames)
                {
                    frames.Add(new GradedPath(frame.FilePath, VerdictOf(frame, baseline)));
                }
            }

            _post(() => Publish(frames, failed));
        });
    }

    private void Publish(IReadOnlyList<GradedPath> frames, IReadOnlyList<DateOnly> failed)
    {
        if (_disposed)
        {
            return;
        }

        _frames = frames;
        FailedDates = failed;
        FailedNightCount = failed.Count;
        PartialLoadText = BuildPartialLoadText(failed, _nights.Count);
        RefreshTally();
    }

    /// <summary>Spec 12.4's row score band for one frame under the page's active baseline: the
    /// signal axis from detected stars, the sharpness axis from HFR and the roundness axis from
    /// eccentricity, combined and banded by the Core functions the frame table reads.</summary>
    /// <remarks>The "Compare to" toggle governs the sharpness and roundness axes only. Detected
    /// stars is a signal metric and is always graded against the night's own baseline, which is
    /// why <c>FrameGrading</c> carries no rig twin for it.</remarks>
    private static QualityBand? VerdictOf(FrameRow frame, GradingBaseline baseline)
    {
        if (frame.Grading is not { } grading)
        {
            return null;
        }

        var sharp = baseline == GradingBaseline.Rig ? grading.RigHfr : grading.SessionHfr;
        var round = baseline == GradingBaseline.Rig
            ? grading.RigEccentricity
            : grading.SessionEccentricity;

        return FrameQuality.BandForScore(
            FrameQuality.CombinedScore(grading.DetectedStars.Z, sharp.Z, round.Z));
    }

    private static string BuildPartialLoadText(IReadOnlyList<DateOnly> failed, int nightCount)
    {
        if (failed.Count == 0)
        {
            return "";
        }

        var dates = string.Join(
            ", ",
            failed.Select(date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));

        return $"{failed.Count} of {nightCount} checked nights failed to load ({dates}). "
            + "The list below covers only the loaded nights.";
    }

    // Spec 12.4's tally, recomputed on every change to the mode, the box or the loaded set. The
    // g, b and u counts are over the verdicts alone and do not move when the mode does; the listed
    // count and the ", included" clause do.
    private void RefreshTally()
    {
        var good = 0;
        var bad = 0;
        var unmeasured = 0;
        foreach (var frame in _frames)
        {
            switch (frame.Verdict)
            {
                case null:
                    unmeasured++;
                    break;
                case QualityBand.Reject:
                    bad++;
                    break;
                default:
                    good++;
                    break;
            }
        }

        var listed = SelectedPaths().Count;
        var lead = Mode == FrameListMode.Bad ? "Bad list" : "Good list";
        var included = Mode == FrameListMode.Good && IncludeUnmeasured && unmeasured != 0
            ? ", included"
            : "";

        FrameCount = _frames.Count;
        ListCount = listed;
        TallyText = string.Create(
            CultureInfo.InvariantCulture,
            $"{lead}: {listed} of {_frames.Count} frames. Graded {good} good, {bad} bad, {unmeasured} unmeasured{included}");

        CopyCommand.NotifyCanExecuteChanged();
    }

    partial void OnModeChanged(FrameListMode value)
    {
        _targetPage.FrameListMode = FrameListFormats.ToStored(value);
        RefreshTally();
    }

    partial void OnIncludeUnmeasuredChanged(bool value)
    {
        _targetPage.FrameListIncludeUnmeasured = value;
        RefreshTally();
    }

    partial void OnSelectedFormatChanged(FrameListFormatOption value)
        => _targetPage.FrameListFormat = FrameListFormats.ToStored(value.Value);

    /// <summary>Stops a read that is still in flight from publishing onto a closed dialog. Called
    /// by <c>FrameListDialogService</c> in the host's cleanup, on every path.</summary>
    public void Dispose() => _disposed = true;

    // One frame's absolute path and the verdict the grading gave it, which is everything the
    // three formats and the truth table need and nothing else.
    private readonly record struct GradedPath(string Path, QualityBand? Verdict);
}
