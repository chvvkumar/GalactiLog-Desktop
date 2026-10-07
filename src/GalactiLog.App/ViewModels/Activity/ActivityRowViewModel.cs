using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.ViewModels.Activity;

/// <summary>
/// One row of spec 12.6's feed: timestamp, severity, category, message and duration, plus the two
/// things a row can expand into, its details document and its sub-events.
/// </summary>
/// <remarks>
/// <para>
/// Formatting and local expansion state only. The page owns every read and the two write actions,
/// because one row must not own a write, which is the rule
/// <c>MergeHistoryRowViewModel</c> and <c>UnresolvedNameRowViewModel</c> already state.
/// </para>
/// <para>
/// Both expansions default collapsed, matching <c>ActivityFeed.tsx</c>. They are independent: a
/// scan's sub-task list and its own details document answer different questions. Both are also
/// lazy: the details document is parsed on the first expand and the child view-models are built on
/// the first expand, because a scan's child count is unbounded by construction (review finding 2;
/// <c>ScanWriter</c> parents one <c>file_rejected</c> per rejected file and one
/// <c>target_created</c> per new target to the run's <c>scan_started</c> row).
/// </para>
/// </remarks>
public sealed partial class ActivityRowViewModel : ObservableObject
{
    private readonly IReadOnlyList<ActivityRow> _childRows;
    private readonly Func<ActivityRow, ActivityRowViewModel>? _buildChild;

    /// <param name="row">The query row.</param>
    /// <param name="children">This row's sub-events as query rows, oldest first. Empty for a child
    /// row and for a top-level row with no sub-events. Nothing is turned into a view-model until
    /// the reader expands the row.</param>
    /// <param name="buildChild">How to turn one of those rows into a child view-model. Supplied by
    /// the page, which owns the brushes and the zone; null on a child row, which has no children of
    /// its own (<c>parent_id</c> is one level deep by construction, spec 10.9).</param>
    /// <param name="zone">Spec 5.8.1's <c>general.timezone</c>, resolved once by the page.</param>
    /// <param name="use24Hour">Spec 5.8.1's <c>general.use_24h_time</c>.</param>
    /// <param name="isUnseen">Spec 12.6, PAR-017. Whether this row's <c>timestamp</c> was strictly
    /// newer than <c>general.activity_seen_at</c> at the moment the page built this row. Baked in
    /// once, by the page, rather than read live: trailing and optional so this stays additive to
    /// the constructor's one existing call site.</param>
    internal ActivityRowViewModel(
        ActivityRow row,
        IReadOnlyList<ActivityRow> children,
        Func<ActivityRow, ActivityRowViewModel>? buildChild,
        TimeZoneInfo zone,
        bool use24Hour,
        bool isUnseen = false)
    {
        Row = row;
        _childRows = children;
        _buildChild = buildChild;
        IsUnseen = isUnseen;

        // The one general.timezone / general.use_24h_time path in the application (collision-map
        // designated owners): SessionTimeFormat for the clock, MetricText.Date for the date.
        // Nothing here re-implements either half. The feed spans days, so the row carries both,
        // unlike the web's clock-only row.
        var local = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(row.Timestamp, DateTimeKind.Utc), zone);
        DateText = MetricText.Date(DateOnly.FromDateTime(local));
        TimeText = SessionTimeFormat.Format(row.Timestamp, zone, use24Hour);

        IsExpandable = ActivityDetailsViewModel.HasRenderableDetails(row.Details, row.Message);

        // FIXER item 4 (ruling: closed by this page). A scan's terminal event carries parent_id,
        // so a failed or cancelled run is a CHILD of its scan_started row and the top-level row
        // stays "info". Without this the feed would report a failed scan as an ordinary entry
        // until the reader expanded it. The worst child severity is surfaced on the parent, beside
        // the sub-task toggle. Computed from the query rows, so it costs no view-model.
        ChildAlertSeverity = children
            .Select(child => child.Severity)
            .OrderByDescending(Rank)
            .FirstOrDefault(severity => Rank(severity) > 0);

        // Review finding 11, ruled: the only writer of duration_ms is ScanCoordinator's terminal
        // scan event, which carries parent_id, so the duration column would otherwise be blank on
        // every top-level row the feed can show. The run's duration is surfaced on the collapsed
        // parent through the same mechanism as the alert glyph. The last child that carries one
        // wins, which is the terminal event: it is emitted after every other sub-event.
        ChildDurationText = FormatDuration(
            children.LastOrDefault(child => child.DurationMs is not null)?.DurationMs);
    }

    public ActivityRow Row { get; }

    public int Id => Row.Id;

    public string Severity => Row.Severity;

    public string Message => Row.Message;

    public string EventType => Row.EventType;

    /// <summary>Spec 12.6, PAR-017: this row carries the "new" <c>Border.tag</c> in its leading
    /// cell. Set when the page builds this row and recomputed in place on every later open
    /// through <see cref="RefreshUnseen"/> (P2-2, coordinator ruling: the singleton page does not
    /// reload on open, so an already-loaded row's marker is corrected where it sits rather than
    /// by rebuilding the row).</summary>
    [ObservableProperty]
    public partial bool IsUnseen { get; private set; }

    /// <summary>The event's local date, in <c>general.timezone</c>.</summary>
    public string DateText { get; }

    /// <summary>The event's local clock time, 24 or 12 hour per <c>general.use_24h_time</c>.
    /// </summary>
    public string TimeText { get; }

    /// <summary>The web's <c>SEVERITY_ICON</c>: a filled circle, a filled triangle, a cross. Plain
    /// geometric characters rather than emoji or private-use glyphs, the same rule
    /// <c>FrameRowViewModel.GuidingRmsSourceGlyph</c> follows.</summary>
    public string SeverityGlyph => GlyphFor(Row.Severity);

    /// <summary>The web's icon <c>title</c>: the raw severity string.</summary>
    public string SeverityTitle => Row.Severity;

    /// <summary>The web's <c>CATEGORY_LABELS</c> abbreviation, minus <c>mosaic</c>. An
    /// unrecognized category renders as itself rather than as a blank cell.</summary>
    public string CategoryLabel => CategoryLabelFor(Row.Category);

    /// <summary>The one abbreviation map, so the filter pills and the rows cannot disagree about
    /// what a category is called (design-lessons rule 1).</summary>
    internal static string CategoryLabelFor(string category) => category switch
    {
        "scan" => "scan",
        "rebuild" => "reb",
        "thumbnail" => "thumb",
        "enrichment" => "enrich",
        "migration" => "migr",
        "user_action" => "user",
        "system" => "sys",
        var other => other,
    };

    /// <summary>
    /// Spec 12.6's duration column, from this row's own <c>duration_ms</c>. Blank on every
    /// top-level row the feed can show today, because the only writer of the column is
    /// <c>ScanCoordinator</c>'s terminal scan event and that carries <c>parent_id</c>. The run's
    /// duration reaches the collapsed parent through <see cref="ChildDurationText"/> instead
    /// (review finding 11).
    /// </summary>
    /// <remarks>
    /// The web feed has no duration column at all, so the format is this port's (questions.md
    /// Q20): milliseconds below one second, one-decimal seconds below one minute, <c>m:ss</c> at
    /// or above, with the branch taken on the ROUNDED value so nothing renders "60.0 s". The
    /// minute field is not capped at an hour, because a two-hour scan reading "127:14" is more
    /// useful in a log than one reading "2:07:14" in a narrow column.
    /// </remarks>
    public string DurationText => FormatDuration(Row.DurationMs);

    /// <summary>The duration of the last sub-event that carries one, which is the scan's terminal
    /// event. Blank when no child carries a duration. Rendered on the collapsed parent beside
    /// <see cref="ChildAlertGlyph"/>.</summary>
    public string ChildDurationText { get; }

    public bool HasChildDuration => ChildDurationText.Length > 0;

    /// <summary>The web's <c>hasDetails()</c> gate. False leaves the row with no chevron at all.
    /// </summary>
    public bool IsExpandable { get; }

    /// <summary>
    /// The details rendering, or null until the reader opens the row. Built on the first expand and
    /// kept afterwards, so a page of 50 rows parses no JSON at all until somebody asks (review
    /// finding 8: the view binds THIS, not a lazy getter, because a binding evaluates its source
    /// whatever <c>IsVisible</c> says).
    /// </summary>
    [ObservableProperty]
    public partial ActivityDetailsViewModel? Details { get; private set; }

    /// <summary>This row's sub-events, oldest first, built on the first expand. Empty until then,
    /// and empty for good on a row with no children.</summary>
    public ObservableCollection<ActivityRowViewModel> Children { get; } = [];

    /// <summary>How many sub-events this row has, whether or not they have been built.</summary>
    public int ChildCount => _childRows.Count;

    public bool HasChildren => _childRows.Count > 0;

    /// <summary>The web's <c>`${n} sub-task${n !== 1 ? "s" : ""}`</c>.</summary>
    public string SubTaskLabel
        => _childRows.Count == 1 ? "1 sub-task" : $"{_childRows.Count} sub-tasks";

    /// <summary>The web's toggle <c>title</c>.</summary>
    public string SubTaskToggleTitle
        => AreChildrenExpanded ? "Collapse sub-tasks" : "Expand sub-tasks";

    /// <summary>The worst severity among this row's children, or null when they are all
    /// <c>info</c> or there are none. FIXER item 4: what makes a failed or cancelled scan visible
    /// on its collapsed parent row.</summary>
    public string? ChildAlertSeverity { get; }

    public bool HasChildAlert => ChildAlertSeverity is not null;

    public string ChildAlertGlyph => ChildAlertSeverity is { } severity ? GlyphFor(severity) : "";

    public string ChildAlertTitle => ChildAlertSeverity switch
    {
        "error" => "This scan recorded an error",
        "warning" => "This scan recorded a warning",
        _ => "",
    };

    /// <summary>Collapsed by default, matching the web's <c>ParentHistoryRow</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubTaskToggleTitle))]
    public partial bool AreChildrenExpanded { get; private set; }

    /// <summary>Collapsed by default. Independent of <see cref="AreChildrenExpanded"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailsToggleGlyph))]
    public partial bool IsDetailsExpanded { get; private set; }

    /// <summary>The chevron on the details button: U+25BE pointing down when open, U+25B8 pointing
    /// right when closed. The web rotates one icon; a rotation is a transition this list does not
    /// need, so the two states are two characters.</summary>
    public string DetailsToggleGlyph => IsDetailsExpanded ? DetailsOpenGlyph : DetailsClosedGlyph;

    [RelayCommand]
    private void ToggleChildren()
    {
        if (!HasChildren)
        {
            return;
        }

        // Built once, on the first expand, and kept: collapsing does not throw the view-models
        // away, because a reader who opened a scan once is likely to open it again and the second
        // expand must not re-walk thousands of rows.
        if (Children.Count == 0 && _buildChild is { } build)
        {
            foreach (var child in _childRows)
            {
                Children.Add(build(child));
            }
        }

        AreChildrenExpanded = !AreChildrenExpanded;
    }

    [RelayCommand]
    private void ToggleDetails()
    {
        if (!IsExpandable)
        {
            return;
        }

        Details ??= ActivityDetailsViewModel.Create(Row.Details);
        IsDetailsExpanded = !IsDetailsExpanded;
    }

    /// <summary>
    /// Recomputes <see cref="IsUnseen"/> in place against <paramref name="cutoff"/>, the marker
    /// stored before the open that is asking (spec 12.6, section 7.2; P2-2 review). Called by the
    /// page's <c>MarkOpened</c> on every open, for every row already loaded: the page does not
    /// reload on open (it keeps its scroll position and its loaded page set on purpose), so an
    /// already-built row's marker has to be corrected here or it would read whatever cutoff was in
    /// force the one time it was built, forever. Applies to this row's already-built children too.
    /// </summary>
    internal void RefreshUnseen(DateTimeOffset? cutoff)
    {
        IsUnseen = ComputeIsUnseen(Row.Timestamp, cutoff);

        foreach (var child in Children)
        {
            child.RefreshUnseen(cutoff);
        }
    }

    /// <summary>The one comparison spec 12.6's marker uses, "strictly newer than": null means
    /// never opened, so every row is unseen. Internal and static so the page's own build-time
    /// computation (<c>ActivityViewModel.IsUnseen(ActivityRow)</c>) and this row's later in-place
    /// recomputation share one definition (design-lessons rule 1).</summary>
    internal static bool ComputeIsUnseen(DateTime timestamp, DateTimeOffset? cutoff)
        => cutoff is null || timestamp > cutoff.Value.UtcDateTime;

    // U+25CF BLACK CIRCLE, U+25B2 BLACK UP-POINTING TRIANGLE, U+00D7 MULTIPLICATION SIGN, and
    // U+25BE / U+25B8 for the details chevron. Written as escapes rather than as the characters
    // themselves, so this file stays ASCII and no editor or encoding step can silently swap one for
    // a look-alike. Plain geometric characters, never emoji and never private-use glyphs.
    private const string InfoGlyph = "\u25CF";
    private const string WarningGlyph = "\u25B2";
    private const string ErrorGlyph = "\u00D7";
    private const string DetailsOpenGlyph = "\u25BE";
    private const string DetailsClosedGlyph = "\u25B8";

    private static string GlyphFor(string severity) => severity switch
    {
        "warning" => WarningGlyph,
        "error" => ErrorGlyph,
        _ => InfoGlyph,
    };

    private static int Rank(string? severity) => severity switch
    {
        "error" => 2,
        "warning" => 1,
        _ => 0,
    };

    /// <summary>
    /// questions.md Q20. The branch is taken on the rounded value, not the raw one, so 59,999 ms
    /// renders "1:00" rather than "60.0 s", which is a time the next branch would spell
    /// differently (review finding 10).
    /// </summary>
    /// <summary>The first duration that renders as a minute rather than as seconds: 59.95 s is
    /// "60.0 s" at one decimal, so the seconds branch stops just below it.</summary>
    private const int SecondsCeilingMs = 59_950;

    internal static string FormatDuration(int? durationMs)
    {
        if (durationMs is not { } ms)
        {
            return "";
        }

        if (ms < 1_000)
        {
            return ms.ToString("N0", CultureInfo.InvariantCulture) + " ms";
        }

        // The boundary is the one that matters to the READER, not the raw one: anything from
        // 59,950 ms up would render "60.0 s" at one decimal, which is a time the next branch spells
        // "1:00". Expressed as a millisecond constant rather than as a rounded double, because
        // Math.Round(ms / 1000d, 1) scales by ten internally and turns 1,450 ms into 1.4 s.
        if (ms < SecondsCeilingMs)
        {
            return (ms / 1_000d).ToString("0.0", CultureInfo.InvariantCulture) + " s";
        }

        var totalSeconds = (int)Math.Round(ms / 1_000d, MidpointRounding.AwayFromZero);
        return string.Create(
            CultureInfo.InvariantCulture, $"{totalSeconds / 60}:{totalSeconds % 60:00}");
    }
}
