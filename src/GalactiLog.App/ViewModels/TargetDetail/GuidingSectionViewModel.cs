using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using GalactiLog.App.Services;
using GalactiLog.Core.Phd2;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.TargetDetail;

/// <summary>
/// Spec 12.4's "Guiding" block, inside the session pane's Night detail section: the night
/// rollup's seven figures, the calibration chips, the state it reports when there is nothing to plot,
/// and the guide graph's view-model, which it builds and whose selection it follows. Ports
/// <c>frontend/src/components/Phd2GuidingPanel.tsx</c>'s summary half; the plot half is
/// <see cref="GuideGraphViewModel"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing loads until the card is shown.</b> The night query is issued once, the first time
/// the card is the one the page is showing, and never from the constructor.
/// </para>
/// <para>
/// <b>The block is about the card's rig, not the night.</b> A session card is one target's night,
/// so the night query is narrowed to the card's own rig when the card's night carries one and
/// unfiltered when it carries several. That is where the web narrows it, in both halves: the panel
/// is handed <c>isMultiRig() ? null : equipment.telescope</c> and the rollup is built from the
/// card's own telescopes. The panel's own rig filter is a separate thing and narrows what came back
/// locally, never through a second query.
/// </para>
/// <para>
/// Every collaborator arrives as a delegate, the rule <see cref="SessionCardViewModel"/> follows,
/// so the type constructs in a unit test with lambdas and no database (spec 18.3). The load runs
/// off the UI thread and publishes through the post seam; there is no
/// <c>ConfigureAwait(false)</c> in this file, because the continuation touches observable state
/// (TRACKING section 5, the Phase 14B rule).
/// </para>
/// <para>
/// <b>This section owns no arithmetic.</b> Every figure it prints was produced by
/// <see cref="Phd2Metrics.AggregateNight"/> or stored by the Phase 15A ingest. The only
/// computation here is presentation: a number to text, and the web's compact duration form.
/// </para>
/// </remarks>
public sealed partial class GuidingSectionViewModel : ObservableObject, IDisposable
{
    private readonly DateOnly _night;
    private readonly string? _rig;
    private readonly Func<DateOnly, string?, Phd2NightGuiding> _getGuiding;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;
    private readonly Func<Guid, Phd2SessionFrames?>? _getFrames;
    private readonly TimeZoneInfo _zone;
    private readonly bool _use24Hour;
    private readonly CancellationTokenSource _lifetime = new();

    // The first, unfiltered night. Sessions is overwritten on every publish, and the rig filter
    // narrows this list and never the night query.
    private IReadOnlyList<Phd2SessionSummary> _nightSessions = [];

    // Only the newest load may write to the bindings: a load that already finished can still be
    // sitting in the post queue when Invalidate runs, and publishing it then would show figures
    // from before a rescan.
    private int _generation;

    private bool _loading;
    private bool _loaded;
    private bool _disposed;

    /// <param name="night">The imaging night this section is about, the card's own session
    /// date.</param>
    /// <param name="getGuiding">Normally <c>Phd2NightQuery.Get</c>. Synchronous and takes a SQLite
    /// read, so it runs on the thread pool and publishes through <paramref name="post"/>. The
    /// second argument is <paramref name="rig"/>, the card's own rig.</param>
    /// <param name="post">How to reach the UI thread. Defaults to <c>UiPost.Default</c>.</param>
    /// <param name="logger">Optional. A failed night query is logged and surfaced on the section,
    /// never rethrown: a guide-log failure never takes the night down.</param>
    /// <param name="getFrames">Normally <c>Phd2FramesQuery.Get</c>. Reaches the graph only through
    /// <see cref="DrawableOnly"/>, so a session the graph cannot draw costs no read. Optional and
    /// trailing; null gives a graph that reads nothing, which is what a case that is not about the
    /// plot wants.</param>
    /// <param name="zone">The observer's zone, the card's own, so the selector, the axis and the
    /// caption read the clock the night header reads. Defaults to UTC.</param>
    /// <param name="use24Hour">The display settings' clock, the card's own.</param>
    /// <param name="rig">The card's own rig, which is what the band is about: the card is one
    /// target's night, not the night. Null means the whole night, unfiltered, which is what a card
    /// whose own night carries several rigs passes, exactly where the web stops narrowing
    /// (<c>SessionAccordionCard.tsx:1240</c>, <c>target_detail.py:563-571</c>). Optional and
    /// trailing; the default is the whole night.</param>
    public GuidingSectionViewModel(
        DateOnly night,
        Func<DateOnly, string?, Phd2NightGuiding> getGuiding,
        Action<Action>? post = null,
        ILogger? logger = null,
        Func<Guid, Phd2SessionFrames?>? getFrames = null,
        TimeZoneInfo? zone = null,
        bool use24Hour = true,
        string? rig = null)
    {
        _getFrames = getFrames;
        _zone = zone ?? TimeZoneInfo.Utc;
        _use24Hour = use24Hour;
        _night = night;
        _rig = rig;
        _getGuiding = getGuiding;
        _post = post ?? UiPost.Default;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Spec 12.4's eight-row state table, first match winning, as one ordered decision
    /// rather than eight independent visibility bindings, so a night that is several of these at
    /// once reads as the most specific one.</summary>
    public enum SectionState
    {
        /// <summary>The night query has not run yet, or is running.</summary>
        Loading,

        /// <summary>The night query threw. The section states it where the plot would be and the
        /// rest of the pane is unaffected.</summary>
        Failed,

        /// <summary>The night holds no guiding session at all.</summary>
        NoSessions,

        /// <summary>Sessions exist and the selected one carries no frame.</summary>
        NoFrames,

        /// <summary>Sessions exist and the selected one carries no pixel scale, which is spec
        /// 7.6's ASIAIR case. Decided from the session row, so no frame query is issued for
        /// it.</summary>
        NoPixelScale,

        /// <summary>Sessions exist and the selected one is drawable.</summary>
        Drawable,
    }

    /// <summary>
    /// The guide graph's view-model. Null until the night has been published with at least one
    /// session while the card is shown, and null again after
    /// <see cref="Invalidate"/>.
    /// </summary>
    /// <remarks>
    /// Built for every night that has a session and not only for a drawable one, because spec
    /// 12.4's no-frames and no-scale states still show the selectors and the selectors are the
    /// graph's. What is staged is the read: <see cref="DrawableOnly"/> stands between the graph and
    /// the frames query, so the first read is issued when a drawable session is selected on the
    /// shown card and never before. This type's <see cref="SelectedSession"/> and
    /// <see cref="State"/> follow the graph's selection, which leaves spec 12.4's default-selection
    /// rule with one implementation, the graph's.
    /// </remarks>
    [ObservableProperty]
    public partial GuideGraphViewModel? Graph { get; private set; }

    /// <summary>
    /// What the rig filter's combo box binds its selection to: the graph's own
    /// <see cref="GuideGraphViewModel.SelectedRigOption"/>, with a null write dropped.
    /// </summary>
    /// <remarks>A combo box rewrites its selection when its item list is replaced, and the graph
    /// replaces its session list on every <see cref="GuideGraphViewModel.SetSessions"/>. Bound
    /// two-way straight to the graph, that was seen red: the list replaced in the middle of the
    /// combo box's own commit made it write "All rigs" back over the rig the reader had just
    /// chosen. The graph now keeps its rig list instance while the content is equal, which is the
    /// root of that (fixer item 53), and the null write is still dropped here because the session
    /// list genuinely does change content on a narrowing. The selection is re-announced once the
    /// lists have settled.</remarks>
    public string? SelectedRigOption
    {
        get => Graph?.SelectedRigOption;
        set
        {
            if (value is not null && Graph is { } graph)
            {
                graph.SelectedRigOption = value;
            }
        }
    }

    /// <summary>The same pass-through for the session selector, for the same reason.</summary>
    public GuideSessionOption? SelectedSessionOption
    {
        get => Graph?.SelectedSession;
        set
        {
            if (value is not null && Graph is { } graph)
            {
                graph.SelectedSession = value;
            }
        }
    }

    /// <summary>
    /// Spec 12.4's "no frame query is issued for it": wraps a frames read so a session with no
    /// frame, or with a null pixel scale, answers null without the read being made. The decision
    /// is read off the session row.
    /// </summary>
    public static Func<Guid, Phd2SessionFrames?> DrawableOnly(
        IReadOnlyList<Phd2SessionSummary> sessions,
        Func<Guid, Phd2SessionFrames?>? getFrames)
        => id => getFrames is not null
                 && sessions.FirstOrDefault(session => session.Id == id) is { } session
                 && DecideState(sessions, session) == SectionState.Drawable
            ? getFrames(id)
            : null;

    /// <summary>True while the pane is showing the card this section belongs to. Set by the card,
    /// because "the selected night" is the page's fact and not this section's.</summary>
    public bool IsShown
    {
        get => _isShown;
        set
        {
            if (_isShown == value)
            {
                return;
            }

            _isShown = value;
            EnsureLoaded();
        }
    }

    private bool _isShown;

    /// <summary>Spec 12.4's state table, decided once per publish.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary), nameof(HasPlot), nameof(HasFailure))]
    public partial SectionState State { get; private set; } = SectionState.Loading;

    /// <summary>True while the night query is in flight.</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>The night's guiding sessions after the rig rule, ordered by start time. Empty
    /// until the card has been shown once.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<Phd2SessionSummary> Sessions { get; private set; } = [];

    /// <summary>The session row behind the graph's selection, which is spec 12.4's default until
    /// the reader picks another. Null before the load and on a night with no session.</summary>
    [ObservableProperty]
    public partial Phd2SessionSummary? SelectedSession { get; private set; }

    /// <summary>The number of guiding sessions the night holds, for the band's right-aligned
    /// summary. Empty at zero, as the "Night detail" band's findings summary is for a night with
    /// no outlier, rather than the digit 0.</summary>
    /// <remarks>
    /// Empty until the card has been shown once. <c>SessionOverview</c> carries no guiding session
    /// count, so there is nothing an unshown card could read without issuing the section's own
    /// query, which spec 12.4 forbids. Recorded as a blocker in this task's report.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSessionCount))]
    public partial string SessionCountText { get; private set; } = "";

    /// <summary>Drives the band summary's visibility, so the markup binds it with no
    /// converter.</summary>
    public bool HasSessionCount => SessionCountText.Length > 0;

    /// <summary>Spec 12.4's RMS figure, frame-count weighted over the night's sessions of at least
    /// <see cref="Phd2Metrics.MinFrames"/> frames. Empty when no session cleared the gate, because
    /// a weighted RMS over an empty set is null and a null figure draws nothing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRms))]
    public partial string RmsText { get; private set; } = "";

    /// <summary>The RA half of the same figure, on the same terms.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRmsRa))]
    public partial string RmsRaText { get; private set; } = "";

    /// <summary>The declination half of the same figure, on the same terms.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRmsDec))]
    public partial string RmsDecText { get; private set; } = "";

    /// <summary>Drives the RMS readout's visibility, label included: on a night where no session
    /// cleared the gate the figure is null and spec 12.4 draws nothing, and a bare "RMS" with
    /// nothing after it is not nothing.</summary>
    public bool HasRms => RmsText.Length > 0;

    /// <summary>The same, for the RA readout.</summary>
    public bool HasRmsRa => RmsRaText.Length > 0;

    /// <summary>The same, for the declination readout.</summary>
    public bool HasRmsDec => RmsDecText.Length > 0;

    /// <summary>Spec 12.4's Sessions figure: the session count, then "(n too short to grade)" when
    /// the night carries a gated session. The parenthetical is absent at zero rather than reading
    /// "0 too short".</summary>
    [ObservableProperty]
    public partial string SessionsText { get; private set; } = "";

    /// <summary>Spec 12.4's Star lost figure: the drop count, then "(t unguided, longest run r)"
    /// when the count is above zero, where t is the unguided time in the compact duration form and
    /// r is the longest consecutive run of dropped frames.</summary>
    [ObservableProperty]
    public partial string StarLostText { get; private set; } = "";

    /// <summary>Spec 12.4's Dithers figure. Always drawn, including at zero.</summary>
    [ObservableProperty]
    public partial string DithersText { get; private set; } = "";

    /// <summary>Spec 12.4's Settle figure: the median settle in seconds to one decimal. Empty when
    /// the night has no median, which draws nothing where the figure would be.</summary>
    [ObservableProperty]
    public partial string SettleText { get; private set; } = "";

    /// <summary>The "n failed" run that follows the settle median, in the warning ink, when the
    /// night carries a failed settle. Empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSettleFailed))]
    public partial string SettleFailedText { get; private set; } = "";

    /// <summary>Drives the failed run's visibility, so the markup binds it with no
    /// converter.</summary>
    public bool HasSettleFailed => SettleFailedText.Length > 0;

    /// <summary>
    /// Spec 12.4's calibration chips, which are <see cref="Phd2NightSummary.CalIssues"/> exactly:
    /// the distinct <c>last_cal_issue</c> values of the night's sessions, ordinally sorted, with
    /// null, the empty string and the literal <c>None</c> in any casing already excluded.
    /// </summary>
    /// <remarks>This view filters nothing. The exclusion lives in
    /// <see cref="Phd2Metrics.AggregateNight"/> and in one place only; a second filter here would
    /// be a second answer to the same question, and a PHD2 build that writes a fourth string would
    /// be dropped by whichever copy had not heard of it.</remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCalIssues))]
    public partial IReadOnlyList<string> CalIssues { get; private set; } = [];

    /// <summary>Drives the chip row and its "Calibration:" label together, so an empty list draws
    /// neither.</summary>
    public bool HasCalIssues => CalIssues.Count > 0;

    /// <summary>The sentence the section prints where the plot would be, or empty when the
    /// selected session is drawable. Spec 12.4: the empty and the failed cases say so where the
    /// plot would be and leave the band open, because a section that silently collapses when its
    /// query returns nothing reads as a section that is broken.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlotNotice))]
    public partial string PlotNoticeText { get; private set; } = "";

    /// <summary>Drives the notice's visibility, so the markup binds it with no converter.</summary>
    public bool HasPlotNotice => PlotNoticeText.Length > 0;

    /// <summary>True when the summary figures, the chips and the selectors are drawn at all, which
    /// is every state but a night with no session and a failed query.</summary>
    public bool HasSummary => State is SectionState.NoFrames or SectionState.NoPixelScale or SectionState.Drawable;

    /// <summary>True when the graph's host slot is drawn and reserves its height, which is the one
    /// state that has a plot: every other state prints <see cref="PlotNoticeText"/> in its
    /// place.</summary>
    public bool HasPlot => State == SectionState.Drawable;

    /// <summary>Drives the section's own failure line, in the shape the pane's existing failure
    /// callout uses.</summary>
    public bool HasFailure => State == SectionState.Failed;

    /// <summary>The number of times the night query has been issued, so a test can assert the
    /// staging rule rather than infer it from what is on screen.</summary>
    internal int Loads { get; private set; }

    /// <summary>The in-flight night query, so a test can await it instead of sleeping. Mirrors
    /// <c>SessionCardViewModel.PendingLoad</c>.</summary>
    internal Task? PendingLoad { get; private set; }

    /// <summary>
    /// The web's <c>formatSecondsShort</c> (<c>frontend/src/utils/phd2Format.ts</c> lines 9 to 21):
    /// below 60 seconds the rounded whole seconds with an "s"; otherwise the total is rounded to
    /// whole seconds once, and below an hour of that total it reads the whole minutes, and the
    /// seconds after them when they are not zero; otherwise the whole hours, and the minutes after
    /// them when they are not zero.
    /// </summary>
    /// <remarks>
    /// <b>The sub-minute branch is on the raw value and not on the rounded one</b>, so 59.6
    /// seconds reads "60s" and not "1m". The rounding is JavaScript's <c>Math.round</c>, half up,
    /// which for the non-negative values this member accepts is
    /// <see cref="MidpointRounding.AwayFromZero"/>; it is a presentation rounding of a duration
    /// and not a stored parity figure, so rule G6's <c>RoundLikePython</c> has no site here. It is the guiding readout's own format and does not replace
    /// <see cref="MetricText.Integration"/>, which pads to hours and minutes because it totals a
    /// whole night.
    /// </remarks>
    public static string CompactDuration(double seconds)
    {
        // The web's own guard, "0s" and not the empty string: this is the one branch a later
        // caller can reach with a value the summary path never produces (task4b-review.md P3-5).
        if (!double.IsFinite(seconds) || seconds < 0d)
        {
            return "0s";
        }

        if (seconds < 60d)
        {
            var whole = (long)Math.Round(seconds, MidpointRounding.AwayFromZero);
            return string.Create(CultureInfo.InvariantCulture, $"{whole}s");
        }

        // The total is rounded once and every later figure, the hour branch included, is derived
        // from the rounded total, as the web does: 3599.6 reads "1h" and 90.6 reads "1m 31s".
        var total = (long)Math.Round(seconds, MidpointRounding.AwayFromZero);
        var minutes = total / 60;
        var remainder = total % 60;
        if (minutes < 60)
        {
            return remainder == 0
                ? string.Create(CultureInfo.InvariantCulture, $"{minutes}m")
                : string.Create(CultureInfo.InvariantCulture, $"{minutes}m {remainder}s");
        }

        var hours = minutes / 60;
        var trailingMinutes = minutes % 60;
        return trailingMinutes == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}h")
            : string.Create(CultureInfo.InvariantCulture, $"{hours}h {trailingMinutes}m");
    }

    /// <summary>Drops what was loaded so the next time this card is shown the night is
    /// re-read. What a scan-driven page reload calls: the figures the section is showing are the
    /// ones a scan may have just superseded.</summary>
    public void Invalidate()
    {
        if (_disposed)
        {
            return;
        }

        _generation++;
        _loading = false;
        _loaded = false;
        IsLoading = false;
        DropGraph();
        _nightSessions = [];
        Sessions = [];
        SelectedSession = null;
        CalIssues = [];
        SessionCountText = "";
        RmsText = "";
        RmsRaText = "";
        RmsDecText = "";
        SessionsText = "";
        StarLostText = "";
        DithersText = "";
        SettleText = "";
        SettleFailedText = "";
        PlotNoticeText = "";
        State = SectionState.Loading;

        EnsureLoaded();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DropGraph();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    // The whole of the staging rule, in one place: the query runs once, when the card is shown.
    private void EnsureLoaded()
    {
        if (_disposed || _loading || !IsShown)
        {
            return;
        }

        if (_loaded)
        {
            // A night that was published while the card was not shown.
            EnsureGraph();
            return;
        }

        _loading = true;
        IsLoading = true;
        Loads++;
        var generation = ++_generation;
        var token = _lifetime.Token;
        PendingLoad = Task.Run(
            () =>
            {
                try
                {
                    // The card's own rig, or the whole night when the card's night carries
                    // several. The panel's rig filter is never this argument: it narrows the
                    // published list locally, because an unmapped rig's label is a profile name,
                    // which the night query answers with an empty list.
                    var guiding = _getGuiding(_night, _rig);
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    _post(() => Publish(generation, guiding, null));
                }
                catch (OperationCanceledException)
                {
                    // The section was disposed while the read was in flight.
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Loading the guiding of {Night} failed", _night);
                    _post(() => Publish(generation, null, ex));
                }
            },
            token);
    }

    // Runs on the UI thread through the post seam. A response older than the newest request is
    // dropped here rather than overwriting it.
    private void Publish(int generation, Phd2NightGuiding? guiding, Exception? failure)
    {
        if (generation != _generation || _disposed)
        {
            return;
        }

        _loading = false;
        IsLoading = false;

        if (failure is not null || guiding is null)
        {
            State = SectionState.Failed;
            PlotNoticeText = "";
            return;
        }

        _loaded = true;

        _nightSessions = guiding.Sessions;
        Sessions = guiding.Sessions;
        CalIssues = guiding.Summary.CalIssues;
        SessionCountText = guiding.Summary.SessionCount == 0
            ? ""
            : MetricText.Count(guiding.Summary.SessionCount);

        PublishFigures(guiding.Summary);
        if (_nightSessions.Count == 0)
        {
            FollowSelection();
        }
        else
        {
            EnsureGraph();
        }
    }

    // Builds the graph once per published night, and only while the card is shown: the graph's
    // constructor asks for its default session's frames, and nothing is read for a card nobody is
    // looking at (G2).
    private void EnsureGraph()
    {
        if (Graph is not null || _nightSessions.Count == 0 || !IsShown)
        {
            return;
        }

        // The flag is read and the graph published under one gate with the setter below: the
        // chart sets the flag from the detail query's publish and this runs from the night
        // query's, and a post seam that runs both inline puts them on two threads at once. Read
        // apart, the flag set between the constructor and the assignment reached no graph.
        GuideGraphViewModel graph;
        lock (_wholeNightGate)
        {
            graph = new GuideGraphViewModel(
                _nightSessions,
                DrawableOnly(_nightSessions, _getFrames),
                _zone,
                _use24Hour,
                _post,
                CompactDuration,
                _lifetime.Token,
                _wholeNight);
            graph.PropertyChanged += OnGraphChanged;
            Graph = graph;
        }

        FollowSelection();
    }

    private readonly Lock _wholeNightGate = new();
    private bool _wholeNight;

    /// <summary>Phase 24 R3: whether the graph draws every session of the night on one time base.
    /// Set by the night chart, which hosts the graph on the shared axis; a graph already built
    /// reloads.</summary>
    public bool DrawsWholeNight
    {
        get => _wholeNight;
        set
        {
            lock (_wholeNightGate)
            {
                _wholeNight = value;
                if (Graph is { } graph)
                {
                    graph.DrawsWholeNight = value;
                }
            }
        }
    }

    private void DropGraph()
    {
        if (Graph is { } graph)
        {
            graph.PropertyChanged -= OnGraphChanged;
            Graph = null;
        }
    }

    private void OnGraphChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not GuideGraphViewModel graph || !ReferenceEquals(graph, Graph))
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(GuideGraphViewModel.SelectedSession):
                FollowSelection();
                break;

            // Re-entrancy: when SetSessions clears a stale filter it raises this mid-body, and the
            // SetSessions below is then dropped by the graph by design. Nothing else happens here,
            // so there is nothing to guard.
            case nameof(GuideGraphViewModel.SelectedRig):
                var rig = graph.SelectedRig;
                graph.SetSessions(
                    _nightSessions,
                    rig is null
                        ? null
                        : [.. _nightSessions.Where(session => string.Equals(GuideGraphViewModel.RigLabel(session), rig, StringComparison.Ordinal))]);
                AnnounceSelectors();
                break;

            // The graph holds no logger by design; the previous plot stays on screen.
            case nameof(GuideGraphViewModel.LastFailure) when graph.LastFailure is { } failure:
                _logger.LogWarning(failure, "Loading the guide frames of a session of {Night} failed", _night);
                break;
        }
    }

    // The section's selection and state are the graph's selection read back as a session row.
    private void FollowSelection()
    {
        var id = Graph?.SelectedSession?.Id;
        SelectedSession = id is null ? null : _nightSessions.FirstOrDefault(session => session.Id == id);
        State = DecideState(_nightSessions, SelectedSession);
        PlotNoticeText = NoticeFor(State);
        AnnounceSelectors();
    }

    private void AnnounceSelectors()
    {
        OnPropertyChanged(nameof(SelectedRigOption));
        OnPropertyChanged(nameof(SelectedSessionOption));
    }

    // Spec 12.4's state table, first match winning. Row 3, the rig filter naming a rig the night
    // no longer has, is the graph's own SetSessions and is not decided here.
    private static SectionState DecideState(
        IReadOnlyList<Phd2SessionSummary> sessions,
        Phd2SessionSummary? selected)
        => sessions.Count == 0 || selected is null ? SectionState.NoSessions
            : selected.FrameCount == 0 ? SectionState.NoFrames
            : selected.PixelScaleArcsec is null ? SectionState.NoPixelScale
            : SectionState.Drawable;

    private static string NoticeFor(SectionState state) => state switch
    {
        SectionState.NoSessions => "No PHD2 guide logs for this night.",
        SectionState.NoFrames => "No guide frames for this session.",
        SectionState.NoPixelScale =>
            "No pixel scale in this session's log header, so guiding error cannot be shown in arcseconds.",
        SectionState.Failed => "",
        _ => "",
    };

    // Spec 12.4's two lines of labelled figures, every one of them already rounded by
    // Phd2Metrics.AggregateNight or by the Phase 15A ingest. Nothing here rounds for parity, so
    // rule G6 has no site in this file: these are presentation decimals through MetricText.
    private void PublishFigures(Phd2NightSummary summary)
    {
        RmsText = MetricText.Format(summary.RmsTotalArcsec, "0.00", " arcsec");
        RmsRaText = MetricText.Format(summary.RmsRaArcsec, "0.00", " arcsec");
        RmsDecText = MetricText.Format(summary.RmsDecArcsec, "0.00", " arcsec");

        SessionsText = summary.GatedSessionCount > 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{MetricText.Count(summary.SessionCount)} ({summary.GatedSessionCount} too short to grade)")
            : MetricText.Count(summary.SessionCount);

        StarLostText = summary.DropCount > 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{MetricText.Count(summary.DropCount)} ({CompactDuration(summary.UnguidedSeconds)} unguided, longest run {MetricText.Count(summary.MaxDropRun)})")
            : MetricText.Count(summary.DropCount);

        DithersText = MetricText.Count(summary.DitherCount);

        SettleText = MetricText.Format(summary.SettleMedianS, "0.0", "s");
        SettleFailedText = summary.SettleFailedCount > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{MetricText.Count(summary.SettleFailedCount)} failed")
            : "";
    }
}
