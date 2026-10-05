using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.ViewModels.CustomColumns;
using GalactiLog.Core.Aliases;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;

namespace GalactiLog.App.ViewModels.Dashboard;

/// <summary>
/// One row of the dashboard target list: a display projection of Task 2's <see cref="TargetRow"/>
/// read model (design-spec 12.2). Formatting only. No business logic, no queries, no filesystem.
/// </summary>
public sealed partial class TargetRowViewModel : ObservableObject, IDisposable
{
    // Spec 14's one legitimate literal colour: a filter's configured colour is user data, not a
    // token. Same fallback AliasMap.FilterColor uses when a filter has no configured colour.


    /// <param name="row">Task 2's read model for one group.</param>
    /// <param name="columns">The owning list's column objects, shared by every row. The row cells
    /// bind their visibility straight to these rather than walking up to the list's DataContext
    /// from inside an item template.</param>
    /// <param name="isRefetching">Reads the owning list's own <c>IsRefetching</c> live (Phase
    /// 14C Task 4 fix pass). <c>IsHitTestVisible="{Binding !IsRefetching}"</c> on the rows region
    /// blocks the pointer alone; this is what <see cref="ToggleSessionsCommand"/> checks so Enter
    /// or Space on a focused, already-dimmed row does nothing during a flight either. Null lets a
    /// caller with no list to ask build a row that never refuses, which is every existing test.
    /// </param>
    /// <param name="custom">Spec 12.15's page-wide wiring for the custom column cells, or null on
    /// a list built with no custom column delegates, which is every existing construction site.
    /// </param>
    /// <param name="customValues">This target's own stored target-scope values, already picked out
    /// of the page-wide read by <see cref="TargetListViewModel"/>. Null or empty leaves every cell
    /// unset rather than absent.</param>
    /// <param name="aliases">Normally <c>AliasMapCache.Current</c>, for the badge order's alias
    /// fallback (polish wave 2 ruling 1). Null folds the canonical names alone.</param>
    public TargetRowViewModel(
        TargetRow row,
        IReadOnlyList<ColumnViewModel> columns,
        Func<bool>? isRefetching = null,
        CustomCellContext? custom = null,
        IReadOnlyList<CustomValueRow>? customValues = null,
        Func<AliasMap>? aliases = null)
    {
        Row = row;
        Columns = columns;
        _isRefetching = isRefetching;
        var order = FilterOrder.Comparer(aliases?.Invoke());
        PaletteBadges = SortBadges(row.Palette, order);
        // The group key is passed down at construction rather than the session row holding a
        // parent reference (task6.md 5.3), which keeps this the one place a SessionRowViewModel
        // is built.
        _sessions = [.. row.Sessions.Select(session => new SessionRowViewModel(
            session.SessionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            session.FrameCount,
            FormatHours(session.IntegrationSeconds),
            session.SessionDate,
            row.GroupKey,
            SortBadges(session.Filters, order)))];

        _custom = custom;
        _cells = ReconcileTargetCells(CustomCellGroup.Empty, custom, customValues);
    }

    /// <summary>Both badge lists read in <see cref="FilterOrder"/> (polish ruling 5, which
    /// reverses Phase 14C ruling U3's alphabetical order); the query's Palette and Filters orders
    /// are wire orders, not display orders.</summary>
    private static IReadOnlyList<PaletteBadgeViewModel> SortBadges(IReadOnlyList<FilterBadge> badges, IComparer<string?> order)
        => [.. badges
            .Select(badge => new PaletteBadgeViewModel(badge, ParseTint(badge.Color)))
            .OrderBy(badge => badge.CanonicalName, order)];

    /// <summary>The read model behind the row. Phase 6 navigates on <see cref="TargetId"/> or
    /// <see cref="GroupKey"/> from here.</summary>
    public TargetRow Row { get; }

    /// <summary>The owning list's columns, in the documented order, for per-cell visibility.</summary>
    public IReadOnlyList<ColumnViewModel> Columns { get; }

    public string GroupKey => Row.GroupKey;
    public Guid? TargetId => Row.TargetId;

    /// <summary>The primary name, or the raw <c>OBJECT</c> string for an unresolved
    /// <c>obj:</c> group (Task 2 already resolves which).</summary>
    public string Name => Row.Name;

    /// <summary>Spec 12.2's "Name (with the common name)". Empty when there is none.</summary>
    public string CommonName => Row.CommonName ?? "";

    /// <summary>
    /// Whether the Name cell draws the common name beside the primary name. False when the
    /// primary name already carries it, which is what made a row read
    /// "NGC 1909 - the Witch Head Nebula  the Witch Head Nebula" (Phase 14C fixer pass, from the
    /// launched application). The comparison is ordinal and case insensitive over trimmed text,
    /// and a comma separated common name is matched on its first entry, because a resolver that
    /// gives "Veil Nebula,Filamentary Nebula,Western Veil" puts the first of them in the name.
    /// </summary>
    public bool HasCommonName
    {
        get
        {
            var common = (Row.CommonName ?? "").Trim();
            if (common.Length == 0)
            {
                return false;
            }

            var name = Row.Name.Trim();
            var first = common.Split(',')[0].Trim();
            return !name.Contains(common, StringComparison.OrdinalIgnoreCase)
                && !(first.Length > 0 && name.Contains(first, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Spec 12.2's Designation column, rendered monospace by the view.</summary>
    public string Designation => Row.CatalogId ?? "";

    public IReadOnlyList<PaletteBadgeViewModel> PaletteBadges { get; }

    /// <summary>Hours with one decimal, for example <c>12.4 h</c>.</summary>
    public string IntegrationText => FormatHours(Row.IntegrationSeconds);

    /// <summary><c>yyyy-MM-dd</c>, empty when the group has no dated session.</summary>
    public string LastSessionText => Row.LastSession?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";

    /// <summary>The canonical rig strings, joined. Task 2 already deduplicates and sorts.</summary>
    public string EquipmentText => string.Join(", ", Row.Equipment);

    public int FrameCount => Row.FrameCount;

    public int SessionCount => Row.SessionCount;

    /// <summary>The sessions control's tooltip since Phase 14C (spec 12.2, user ruling U3): the
    /// count is reachable on hover rather than crowding the name cell. Singular at one, unchanged.
    /// </summary>
    public string SessionsSummaryText => Row.SessionCount == 1 ? "1 session" : $"{Row.SessionCount} sessions";

    /// <summary>The per-session lines under the row while <see cref="IsExpanded"/> is set.
    /// <para>
    /// Replaced wholesale, not mutated, when the night cells arrive (ruling C11): the line is a
    /// record, so each one is rebuilt with its own cells through <c>with</c> and the whole list is
    /// published in one assignment. Every line the list already held keeps its identity in every
    /// other respect.
    /// </para></summary>
    [ObservableProperty]
    private IReadOnlyList<SessionRowViewModel> _sessions;

    // ---- Spec 12.15's custom column cells (Phase 20 Task 5a) ----

    // Replaced along with the cells it built when the reader ticks a column in the gear, so the
    // lazy night read below never works from the set the row was constructed with.
    private CustomCellContext? _custom;

    // Ruling C20: one group per row, replaced by disposing the old one, disposed when the row
    // goes. A bare list of cells would owe a flush and a dispose at every one of those points.
    private CustomCellGroup _cells;

    // One group per night line, keyed by the night, so a re-read reseeds the cells that line already
    // draws instead of replacing them: ruling C27's reseed has to leave a cell the reader is holding
    // alone, and CustomCellFactory is what decides that per line.
    private readonly Dictionary<DateOnly, CustomCellGroup> _sessionCells = [];

    // Every group a surface builds is disposed when the surface goes. The lazy night read
    // publishes on a later turn, so the page can be replaced between the read and the publish;
    // without this the publish would build a second group on a row whose Dispose has already run,
    // and nothing would ever dispose that one.
    private bool _disposed;

    // Ruling C24: how many of the cells the row holds are actually drawn. The width rule owns the
    // figure and pushes it down; the cells themselves are not rebuilt, so a column that comes back
    // when the panel folds is the same cell it was, with whatever it was holding.
    private int _drawnCells = int.MaxValue;

    /// <summary>Spec 12.15's target-scope cells, in display order, after <c>last_session</c> and
    /// before the sessions control. Empty when no target-scope column is switched on, which is
    /// every library until the reader switches one on, and empty on an unresolved <c>obj:</c>
    /// group: that row has no <see cref="TargetId"/>, a value cannot be keyed without one, and a
    /// cell that silently writes nothing is worse than a cell that is not drawn.
    /// <para>
    /// Ruling C24: the trailing cells are dropped while the row does not fit the width the list
    /// has. The header strip is driven by the same figure, so the two always show the same set.
    /// </para></summary>
    public IReadOnlyList<CustomValueViewModel> CustomCells
        => _drawnCells >= _cells.Cells.Count ? _cells.Cells : [.. _cells.Cells.Take(_drawnCells)];

    /// <summary>Ruling C24's figure, pushed down by <see cref="TargetListViewModel"/> from the
    /// width rule. Nothing is disposed: a dropped column is not drawn, not discarded.</summary>
    internal void SetDrawnCustomCells(int count)
    {
        if (_drawnCells == count)
        {
            return;
        }

        _drawnCells = count;
        OnPropertyChanged(nameof(CustomCells));
    }

    /// <summary>The in-flight lazy read of this row's session values, so a case awaits it instead
    /// of sleeping. Null until the row is first expanded.</summary>
    internal Task? PendingSessionCells { get; private set; }

    /// <summary>Brings the target-scope cells into line with a changed column set: the reader ticked
    /// or unticked a custom column in the gear, or the page of rows was republished. A column set
    /// that is unchanged reseeds the cells already on screen, so a value being typed into one of them
    /// survives a republish; a changed one disposes the old group, which flushes whatever was being
    /// typed into it before its cells go.</summary>
    internal void RebuildCustomCells(CustomCellContext? custom, IReadOnlyList<CustomValueRow>? values)
    {
        var replacement = ReconcileTargetCells(_cells, custom, values);
        _custom = custom;

        // Reference equal means the cells on screen were reseeded in place, so there is no new list
        // for the strip to draw and nothing to raise.
        if (ReferenceEquals(replacement, _cells))
        {
            return;
        }

        _cells = replacement;
        OnPropertyChanged(nameof(CustomCells));
    }

    private CustomCellGroup ReconcileTargetCells(
        CustomCellGroup existing, CustomCellContext? custom, IReadOnlyList<CustomValueRow>? values)
        => CustomCellFactory.Reconcile(
            existing,
            custom?.TargetColumns ?? [],
            Row.TargetId is Guid targetId ? CustomValueKey.ForTarget(targetId) : null,
            Row.Name,
            column => Stored(values, column.Id, null),
            custom?.Write,
            custom?.Delay,
            custom?.Post);

    private static string? Stored(
        IReadOnlyList<CustomValueRow>? values, Guid columnId, DateOnly? night)
        => values?.FirstOrDefault(value =>
            value.ColumnId == columnId
            && value.Key.SessionDate == night
            && value.Key.RigLabel is null)?.Value;

    /// <summary>Whether the per-session lines show under the row. Per row, not persisted (spec
    /// 5.8.2 persists column visibility and nothing else about the list).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SessionsToggleText))]
    private bool _isExpanded;

    /// <summary>The trailing sessions button's label, the web application's own wording (spec 12.2,
    /// user ruling U3). It replaces the chevron pair the leading toggle cell carried.</summary>
    public string SessionsToggleText => IsExpanded ? "Collapse" : "Expand";

    private readonly Func<bool>? _isRefetching;

    /// <summary>The row's sessions button. A command rather than a two-way binding on the flag so
    /// the view holds no toggle state of its own. Guarded by the owning list's own refetch flag
    /// (fix pass): the pointer is already blocked by <c>IsHitTestVisible</c>, and this is what
    /// keeps Enter or Space on a focused, already-dimmed row from doing the same thing twice.
    /// </summary>
    [RelayCommand]
    private void ToggleSessions()
    {
        if (_isRefetching?.Invoke() == true)
        {
            return;
        }

        IsExpanded = !IsExpanded;

        if (IsExpanded)
        {
            LoadSessionCells();
        }
    }

    /// <summary>
    /// Re-reads this row's night values while it is expanded, and does nothing while it is not.
    /// Ruling C27's recovery for the one value drawn on two surfaces: a session-scope value is
    /// editable here and on the Nights ledger of the Target page, and the shell calls this on the
    /// rows that are open when a detail page closes, so the expander cannot keep showing text the
    /// other surface has replaced. A cell the reader is holding is left alone, because the reseed
    /// goes through <c>CustomValueViewModel.Reseed</c>, which refuses while the field is dirty or a
    /// write of its own is in flight.
    /// </summary>
    internal void RefreshSessionCells()
    {
        if (IsExpanded)
        {
            LoadSessionCells();
        }
    }

    /// <summary>
    /// Ruling C11: the night lines' session-scope values are read when the row opens, never with
    /// the page. A page of 50 targets would otherwise issue 50 reads to draw rows nobody has
    /// expanded.
    /// </summary>
    /// <remarks>
    /// Ruling C27: every expansion reads, rather than the first one only. A read-once gate left the
    /// reader no way at all to refresh a night value the Target page had changed, short of a scan or
    /// a filter change, and the stale cell's next keystroke wrote its own whole content over the
    /// newer value. Collapsing and re-expanding is now that recovery, and it costs one read of one
    /// target, which is the same read ruling C11 already accepted.
    /// <para>
    /// The read runs on a thread-pool thread and publishes through the page's own post seam, the
    /// same rule every other background read on this page follows. A read that throws leaves the
    /// night lines as they were and logs, rather than leaving the row permanently blank with nothing
    /// in the log.
    /// </para>
    /// </remarks>
    private void LoadSessionCells()
    {
        if (_custom is not { SessionColumns.Count: > 0 } custom
            || Row.TargetId is not Guid targetId
            || Sessions.Count == 0)
        {
            return;
        }

        PendingSessionCells = Task.Run(() =>
        {
            try
            {
                var values = custom.LoadValuesForTarget(targetId);
                custom.Post(() => PublishSessionCells(custom, targetId, values));
            }
            catch (Exception ex)
            {
                custom.Logger?.LogWarning(
                    ex, "Reading this target's night custom column values failed; the lines keep what they hold");
            }
        });
    }

    // Runs on the UI thread, through the post seam. Ruling C20: one group per night line, so a
    // line's cells are flushed and disposed together when the row goes; the reconcile keeps the
    // cells of a line whose columns and night are unchanged, which is what lets a re-read leave
    // typing alone.
    private void PublishSessionCells(
        CustomCellContext custom, Guid targetId, IReadOnlyList<CustomValueRow> values)
    {
        // The row went while the read was in flight: expanding a row and then sorting is enough.
        // Nothing is built, because nothing would ever dispose it.
        if (_disposed)
        {
            return;
        }

        var lines = new List<SessionRowViewModel>(Sessions.Count);
        var replaced = false;

        foreach (var session in Sessions)
        {
            // Ruling C17's session key: the target and the night, never the rig label. The read
            // answers this target's session-scope AND rig-scope rows in one list, so the rig rows
            // are what the RigLabel test in Stored drops here.
            var existing = _sessionCells.GetValueOrDefault(session.SessionDate, CustomCellGroup.Empty);
            var group = CustomCellFactory.Reconcile(
                existing,
                custom.SessionColumns,
                CustomValueKey.ForSession(targetId, session.SessionDate),
                session.DateText,
                column => Stored(values, column.Id, session.SessionDate),
                custom.Write,
                custom.Delay,
                custom.Post,
                custom.Logger);

            _sessionCells[session.SessionDate] = group;
            replaced |= !ReferenceEquals(group, existing);
            lines.Add(session with { CustomCells = group.Cells });
        }

        // The lines are records and are replaced wholesale, so this is skipped when every group was
        // reseeded in place: republishing the list would reset any control bound to it, and a
        // re-read that changed nothing must not disturb a cell the reader is in.
        if (replaced)
        {
            Sessions = lines;
        }
    }

    /// <summary>Awaits the flush of every cell group this row holds, the target cells and every
    /// night's cells. <see cref="Dispose"/> starts the same flushes and cannot wait on them
    /// (<c>CustomCellGroup.Dispose</c> does <c>_ = FlushAsync()</c>), so the one surface that must
    /// know the last keystroke reached the catalogue, <c>TargetListViewModel.Dispose</c> at
    /// shutdown, awaits this first on one bounded budget for the whole page. This is the dashboard's
    /// side of the rule <c>SessionCardViewModel</c> already carries on the Target page.</summary>
    internal Task FlushAsync()
        => Task.WhenAll([_cells.FlushAsync(), .. _sessionCells.Values.Select(group => group.FlushAsync())]);

    /// <summary>Flushes and disposes every cell group. Called when the page of rows is replaced
    /// and when the list itself goes: an undisposed cell holds an open debounce window
    /// (<c>AutosaveField.Dispose</c>). Stays non-blocking, so a page recycle never waits on the UI
    /// thread; the shutdown wait is <see cref="FlushAsync"/>'s, once for the whole page.</summary>
    public void Dispose()
    {
        _disposed = true;
        _cells.Dispose();
        foreach (var group in _sessionCells.Values)
        {
            group.Dispose();
        }

        _sessionCells.Clear();
    }

    private static string FormatHours(double seconds)
        => string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600d:0.0} h");

    /// <summary>
    /// The one filter-tint parser (FIXER LIST F10); the filter panel's pills use it too. A filter's
    /// tint is user data, not a theme token, which is why this is the one place in the dashboard a
    /// colour value comes from outside <c>Theme/</c>. The absent/blank rule lives in
    /// <see cref="FilterColor"/> in Core, which cannot reference Avalonia; a value that is present
    /// but unparseable falls back to the same default here.
    /// </summary>
    /// <remarks>FIXER LIST F29: the return type says <c>Immutable</c> because every value it
    /// produces is one, and a caller that stores it (<c>FilterUsageRow.Tint</c>) can then declare
    /// the immutability structurally rather than by convention.</remarks>
    internal static IImmutableSolidColorBrush ParseTint(string? color)
        => new ImmutableSolidColorBrush(
            Color.TryParse(FilterColor.OrFallback(color), out var parsed)
                ? parsed
                : Color.Parse(FilterColor.Fallback));
}

/// <summary>One filter badge of spec 12.2's Palette column: the read model plus its parsed colour,
/// so the view binds a brush and holds no conversion logic.</summary>
/// <remarks>FIXER LIST F29, widened: <see cref="Tint"/> is only ever
/// <see cref="TargetRowViewModel.ParseTint"/>'s result, so the type says immutable rather than
/// leaving spec 14.5's rule to convention, exactly as <c>FilterUsageRow.Tint</c> does.</remarks>
public sealed record PaletteBadgeViewModel(FilterBadge Badge, IImmutableSolidColorBrush Tint)
{
    public string CanonicalName => Badge.CanonicalName;
    public int FrameCount => Badge.FrameCount;

    /// <summary>The web app's "frosted-glass" badge (GalactiLog frontend
    /// <c>utils/filterStyles.ts</c>), ported figure for figure: an 18% tint fill inside a 25%
    /// tint edge, a 6% white inset top highlight and a soft 15% tinted drop shadow, with the label
    /// in the tint itself. The three are derived from <see cref="Tint"/> here so the template binds
    /// brushes and holds no arithmetic.</summary>
    public IImmutableSolidColorBrush GlassFill { get; } = WithAlpha(Tint, 0.18);
    public IImmutableSolidColorBrush GlassEdge { get; } = WithAlpha(Tint, 0.25);
    public BoxShadows GlassShadow { get; } = new(
        new BoxShadow { OffsetY = 1, Color = Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF), IsInset = true },
        [new BoxShadow { OffsetY = 2, Blur = 8, Color = Alpha(Tint.Color, 0.15) }]);

    private static ImmutableSolidColorBrush WithAlpha(IImmutableSolidColorBrush tint, double alpha)
        => new(Alpha(tint.Color, alpha));

    private static Color Alpha(Color c, double alpha)
        => Color.FromArgb((byte)Math.Round(alpha * 255), c.R, c.G, c.B);

    /// <summary>The badge's tooltip since Phase 14C (spec 12.2, user ruling U3): the badge draws
    /// <see cref="CanonicalName"/> alone, so the figure that used to sit inside it and wrap the
    /// cell to a second line reads on hover instead. Singular at one frame.
    /// <para>
    /// <see cref="FrameCount"/> stays public beside it: showing the figure in the badge again is a
    /// one line template change rather than a view-model change (Q1, provisional).
    /// </para></summary>
    public string FrameCountText
        => FrameCount == 1 ? $"{CanonicalName}, 1 frame" : $"{CanonicalName}, {FrameCount} frames";
}

/// <summary>One line inside the Sessions expander: date, frames, integration, and, since Phase
/// 14B Task 6, the raw <see cref="SessionDate"/> and owning <see cref="GroupKey"/> the Deep dive
/// action needs and the night's <see cref="Filters"/> badges (spec 12.2, "a fifth column is an
/// added cell rather than a rewrite": the session line is a <c>StackPanel</c> of fixed-width
/// cells, not a shared-width <c>Grid</c>, so this is one more cell rather than a layout
/// change).</summary>
public sealed record SessionRowViewModel(
    string DateText,
    int FrameCount,
    string IntegrationText,
    DateOnly SessionDate,
    string GroupKey,
    IReadOnlyList<PaletteBadgeViewModel> Filters)
{
    /// <summary>Spec 12.15 amendment 2.4's session-scope cells for this night, in display order,
    /// after the line's own columns and with no picker over them (user choice 4). Empty until the
    /// row is expanded and the lazy read comes back (ruling C11), and empty for good on a library
    /// with no session-scope column.
    /// <para>
    /// An init-only member rather than a seventh positional parameter: a positional one cannot
    /// carry an empty list as its default, and every existing construction site of this record
    /// has to keep compiling unchanged.
    /// </para></summary>
    public IReadOnlyList<CustomValueViewModel> CustomCells { get; init; } = [];
}

/// <summary>
/// The page-wide wiring one dashboard row needs to build its custom column cells (spec 12.15).
/// One object rather than five constructor parameters on a row built in a loop: the row is not the
/// thing that decides which columns exist, which delegate writes or which thread publishes, and
/// <see cref="TargetListViewModel"/> composes this once per query rather than per row.
/// </summary>
/// <param name="TargetColumns">The target-scope columns the reader has switched on, in display
/// order, from <c>CustomColumnSet.DashboardRow</c>.</param>
/// <param name="SessionColumns">Every session-scope column, in display order, from
/// <c>CustomColumnSet.NightExpander</c>. Ungated: the night expander has no picker (user choice 4).
/// </param>
/// <param name="LoadValuesForTarget">Normally <c>CustomColumnRepository.ValuesForTarget</c>.
/// Invoked on a thread-pool thread when a row is first expanded, never with the page.</param>
/// <param name="Write">Normally <c>CustomColumnRepository.SetValue</c>. Handed straight to every
/// cell, which invokes it off the UI thread itself.</param>
/// <param name="Post">How a background read reaches the UI thread. The page's own seam, so a case
/// runs its closure inline and needs no dispatcher.</param>
/// <param name="Delay">The cells' debounce seam, handed straight to each one. Null in the
/// application, which takes <c>AutosaveField.IdleWindow</c>; a case parks and releases it instead
/// of sleeping.</param>
/// <param name="Logger">Optional, trailing, and handed to every cell this page builds: a write that
/// throws on the dashboard was swallowed with the one approved sentence on screen and nothing in the
/// log, while the same failure on the Target page was logged, because that page passes its own
/// (review P3-2). The lazy night read logs through it too. Null keeps every existing construction
/// site unchanged and falls back to the cell's own null logger.</param>
public sealed record CustomCellContext(
    IReadOnlyList<CustomColumnDefinition> TargetColumns,
    IReadOnlyList<CustomColumnDefinition> SessionColumns,
    Func<Guid, IReadOnlyList<CustomValueRow>> LoadValuesForTarget,
    Func<Guid, CustomValueKey, string?, CustomWriteResult> Write,
    Action<Action> Post,
    Func<TimeSpan, CancellationToken, Task>? Delay = null,
    ILogger? Logger = null);
