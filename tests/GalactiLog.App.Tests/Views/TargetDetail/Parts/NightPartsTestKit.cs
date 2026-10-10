using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Controls;
using GalactiLog.App.Controls.Table;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.Tests.ViewModels;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.CustomColumns;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Phd2;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;
using Cards = GalactiLog.App.Tests.TestSupport.SessionCardViewModelTestFactory;
using Page = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;

namespace GalactiLog.App.Tests.Views.TargetDetail.Parts;

/// <summary>Helpers the night-part cases share, so a part test hosts its part alone.</summary>
internal static class NightPartsTestKit
{
    public static Window Show(Control control, double width = 1600, double height = 900)
        => TargetPartHost.ShowInStarredRow(control, width, height);

    public static IReadOnlyList<string> VisibleTexts(Control root) => TargetPartHost.VisibleTexts(root);

    /// <summary>The local time the night strip's astronomical-night band starts.</summary>
    public static DateTime DuskOf(NightStripViewModel strip)
        => strip.StartLocal + (strip.EndLocal - strip.StartLocal) * strip.BandStartFraction;

    /// <summary>Every shown row of one table, header first, in visual order. A rig label item keeps
    /// a hidden TableRow, so visibility is what counts.</summary>
    public static IReadOnlyList<TableRow> TableRows(Control host, string tableName)
        => [.. host.Named<Control>(tableName)
            .GetVisualDescendants()
            .OfType<TableRow>()
            .Where(row => row.IsEffectivelyVisible)];

    public static Control TableCellAt(TableRow row, string key)
        => row.Children.First(child => TableRow.GetCol(child) == key);

    /// <summary>The frame table toolbar's items keep clear of each other and of the toolbar's edge,
    /// on at most <paramref name="mostLines"/> visual lines, the lines inside the slot's content counted. An item's extent is
    /// that of everything drawn inside it, so content that overflows its slot counts.</summary>
    public static void AssertTheToolbarClipsNothing(Control frames, int mostLines = 2)
    {
        var toolbar = frames.Named<Panel>("FrameTableToolbar");
        var items = toolbar.Children
            .SelectMany(child => child is WrapPanel wrap ? wrap.Children : [child])
            .Where(item => item.IsEffectivelyVisible)
            .Select(item => (Item: item, Extent: item.GetSelfAndVisualDescendants().OfType<Control>()
                .Where(control => control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0)
                .Select(control => new Rect(control.TranslatePoint(default, toolbar)!.Value, control.Bounds.Size))
                .Aggregate(default(Rect?), (all, next) => all?.Union(next) ?? next)))
            .Where(entry => entry.Extent is not null)
            .Select(entry => (entry.Item, Extent: entry.Extent!.Value))
            .ToList();
        string Name((Control Item, Rect Extent) entry) => $"{entry.Item.Name ?? (entry.Item as TextBlock)?.Text ?? entry.Item.GetType().Name} at {entry.Extent}";

        var bounds = new Rect(toolbar.Bounds.Size).Inflate(0.5);
        foreach (var entry in items)
        {
            Assert.True(bounds.Contains(entry.Extent), $"the toolbar item {Name(entry)} is cut by the toolbar's {toolbar.Bounds.Size}");
            foreach (var other in items.Where(other => !ReferenceEquals(other.Item, entry.Item)))
            {
                Assert.False(entry.Extent.Deflate(0.5).Intersects(other.Extent),
                    $"the toolbar item {Name(entry)} is drawn over {Name(other)}");
            }
        }

        // A line is a run of texts and buttons left of the actions that share a vertical band.
        var pieces = items.Where(entry => entry.Item is not Button)
            .SelectMany(entry => entry.Item.GetSelfAndVisualDescendants().OfType<Control>())
            .Where(control => control.IsEffectivelyVisible && control.Bounds.Height > 0
                && (control is Button || (control is TextBlock && control.FindAncestorOfType<Button>() is null)))
            .Select(control => new Rect(control.TranslatePoint(default, toolbar)!.Value, control.Bounds.Size));
        var lines = new List<Rect>();
        foreach (var extent in pieces.OrderBy(extent => extent.Top))
        {
            if (lines.Count == 0 || extent.Top >= lines[^1].Bottom - 0.5)
            {
                lines.Add(extent);
            }
            else
            {
                lines[^1] = lines[^1].Union(extent);
            }
        }

        Assert.True(lines.Count >= 1 && lines.Count <= mostLines,
            $"the toolbar's texts and buttons sit on {lines.Count} lines, the cap is {mostLines}: {string.Join("; ", lines)}");
    }

    public static IReadOnlyList<FrameRow> Frames(int count, DateOnly date) =>
        [.. Enumerable.Range(0, count).Select(i => FrameTableViewModelTests.Frame(
            fileName: $"frame_{i:000}.fits",
            captureDate: date.ToDateTime(new TimeOnly(21, 0)).AddSeconds(i * 120),
            filterUsed: "Ha",
            exposureTime: 300d,
            medianHfr: 2d + (i % 10) * 0.01d,
            isHfrOutlier: i % 25 == 0))];

    public static FrameTableViewModel Table(IReadOnlyList<FrameRow> frames, TargetPageState? targetPage = null)
    {
        var display = new DisplaySettings();
        foreach (var key in display.Groups.Keys.ToList())
        {
            display.Groups[key] = display.Groups[key] with { Enabled = true };
        }

        return new FrameTableViewModel(
            frames,
            display,
            new DisplayColumnWriter(() => display, _ => { }),
            new ShellIntegration(copyText: null, start: _ => null),
            openPreview: null,
            new GeneralSettings { Timezone = "UTC", Use24HTime = true },
            getHeaders: _ => null,
            // Phase 14A Task 3: null gives the table its own holder on the fresh-profile
            // defaults, which is what every case that is not about the baseline wants.
            targetPage: targetPage);
    }

    /// <summary>The frame table with spec 11.5's seam bound, so a tick press can be asserted to
    /// have opened the preview as well as moved the selection.</summary>
    public static FrameTableViewModel TableWithPreview(
        IReadOnlyList<FrameRow> frames,
        List<(IReadOnlyList<FrameRowViewModel> Rows, int Index)> opens)
    {
        var display = new DisplaySettings();
        foreach (var key in display.Groups.Keys.ToList())
        {
            display.Groups[key] = display.Groups[key] with { Enabled = true };
        }

        return new FrameTableViewModel(
            frames,
            display,
            new DisplayColumnWriter(() => display, _ => { }),
            new ShellIntegration(copyText: null, start: _ => null),
            openPreview: (rows, index) => opens.Add((rows, index)),
            new GeneralSettings { Timezone = "UTC", Use24HTime = true },
            getHeaders: _ => null);
    }

    /// <summary>A loaded table on the card's own holder, and the control shown. The shape every
    /// grading case starts from.</summary>
    public static (T Host, Cards.Harness Harness) Graded<T>(
        Func<SessionCardViewModel, T> create,
        TargetPageSettings? stored = null,
        int frameCount = 12,
        double width = 1600,
        double height = 900)
        where T : Control
    {
        var date = Page.LastSession;
        var frames = Frames(frameCount, date);
        var holder = new TargetPageState(stored);
        var harness = Cards.Create(
            detail: Cards.PopulatedDetail(sessionDate: date) with { Frames = frames },
            targetPage: holder);
        harness.FrameTableResult = Table(frames, holder);
        harness.Card.IsExpanded = true;
        harness.Settle();

        var host = create(harness.Card);
        Show(host, width, height);
        Dispatcher.UIThread.RunJobs();
        return (host, harness);
    }

    public static Phd2SessionSummary GuideSession(
        int minutes, string? telescope = "RC8", string profile = "TestScope_TestCam", double? pixelScale = 1.5d)
        => new(
            Guid.NewGuid(),
            new DateTime(2025, 3, 4, 21, 0, 0, DateTimeKind.Utc).AddMinutes(minutes),
            null,
            300d,
            400,
            profile,
            telescope,
            pixelScale,
            0.42d,
            0.51d,
            0.66d,
            null,
            null,
            0,
            0,
            0d,
            0,
            0,
            0,
            null,
            null,
            null,
            null,
            null);

    public static Phd2SessionFrames GuideFrames()
        => new(
            1.5d,
            null,
            [.. Enumerable.Range(0, 60).Select(i => new Phd2FramePoint(i * 2d, 0.3d, -0.2d, 0, "", 0, "", null, null, false))],
            []);

    // Everything the section loads settled BEFORE the control exists, because the harness posts
    // synchronously from the pool thread and a drawn control must not be invalidated there.
    public static T GuidingHost<T>(
        Cards.Harness harness,
        Func<SessionCardViewModel, T> create,
        double width = 1600,
        double height = 900,
        params Phd2SessionSummary[] sessions)
        where T : Control
    {
        harness.GuidingResult = new Phd2NightGuiding(new Phd2NightSummary { SessionCount = sessions.Length }, sessions);
        harness.FramesResult = _ => GuideFrames();
        harness.Card.IsExpanded = true;
        harness.Settle();
        harness.SettleGuiding().SettleFrames();

        var host = create(harness.Card);
        Show(host, width, height);
        Dispatcher.UIThread.RunJobs();
        return host;
    }
}

/// <summary>The rig label row fixtures the per filter table's rig row cases share.</summary>
internal static class RigRowKit
{
    public const string RigA = "Askar FMA180 / ASI2600MC";
    public const string RigB = "RedCat 51 / ASI2600MC";

    // The ranges table skips a rig's own label row entirely when it carries no measured range
    // (SessionCardViewModel.BuildRanges), so a multi-rig fixture needs a real Ranges array or the
    // ranges-table half of every case here would draw nothing to look at.
    public static readonly IReadOnlyList<MetricRangeSummary> SomeRanges =
    [
        new MetricRangeSummary(1.9d, 3.1d, 2.3d),
        new MetricRangeSummary(0.3d, 0.5d, 0.4d),
        new MetricRangeSummary(1.6d, 2.4d, 1.9d),
        new MetricRangeSummary(0.3d, 0.7d, 0.45d),
        new MetricRangeSummary(-10.5d, -9.5d, -10d),
    ];

    public static RigGroup Rig(
        string label, int index = 0, int frameCount = 6, IReadOnlyList<MetricRangeSummary>? ranges = null)
        => new(
            index,
            label,
            "not-the-telescope-half",
            "not-the-camera-half",
            frameCount,
            frameCount * 300d,
            Guid.NewGuid(),
            label + ".fits",
            [label + ".fits"],
            ranges);

    public static SessionDetail MultiRigDetail(params string[] labels)
        => Cards.PopulatedDetail() with
        {
            Rigs = [.. labels.Select((label, index) => Rig(label, index, ranges: SomeRanges))],
            FilterMedians =
                [.. labels.Select(label => new FilterMedians("Ha", 2.3d, 0.4d, 1.9d, 0.45d, 1400d, label))],
            FilterDetails =
                [.. labels.Select(label => new FilterDetailRow("Ha", 3, 900d, 2.3d, 0.4d, 300d, label))],
        };

    public static SessionDetail SingleRigDetail(string label)
        => Cards.PopulatedDetail() with
        {
            Rigs = [Rig(label)],
            FilterMedians = [new FilterMedians("Ha", 2.3d, 0.4d, 1.9d, 0.45d, 1400d)],
            FilterDetails = [new FilterDetailRow("Ha", 3, 900d, 2.3d, 0.4d, 300d)],
        };

    public static CustomColumnDefinition RigScopeColumn(string name = "Done", int order = 0)
        => CustomColumnTestFactory.Define(name, CustomColumnType.Boolean, CustomColumnScope.Rig, [], order);

    /// <summary>Builds and lays out a control over a night, optionally publishing a rig-scope
    /// column.</summary>
    public static (T Host, Cards.Harness Harness, Window Window) RigHost<T>(
        SessionDetail detail,
        Func<SessionCardViewModel, T> create,
        IReadOnlyList<CustomColumnDefinition>? columns = null,
        CustomColumnTestFactory.WriteLog? writes = null,
        double width = 1600,
        double height = 900)
        where T : Control
    {
        var harness = Cards.Create(detail: detail);
        if (columns is not null)
        {
            harness.Card.PublishCustomColumns(
                Guid.NewGuid(),
                columns,
                [],
                new Dictionary<(Guid, CustomValueKey), string>(),
                writes is null ? (_, _, _) => CustomColumnTestFactory.Written : writes.Write);
        }

        harness.Card.IsExpanded = true;
        harness.Settle();

        var host = create(harness.Card);
        var window = NightPartsTestKit.Show(host, width, height);
        return (host, harness, window);
    }

    public static IReadOnlyList<ContentControl> VisibleLabelRows(Control root, string name)
        => [.. root.GetVisualDescendants()
            .OfType<ContentControl>()
            .Where(control => control.Name == name && control.IsEffectivelyVisible)];
}

/// <summary>The thumbnail and rig fixtures the sharpest frame and frames part cases share.</summary>
internal static class ThumbnailKit
{
    public const string RigA = "Alpha / Cam";
    public const string RigB = "Bravo / Cam";
    public const string RigC = "Charlie / Cam";

    private static readonly TimeSpan ThumbnailBudget = TimeSpan.FromSeconds(30);

    /// <summary>A bitmap of a known pixel size, so a box measurement is an aspect assertion rather
    /// than a decode of a JPEG the suite does not have. <c>WriteableBitmap</c> allocates a CPU
    /// buffer and reports the size it was given, which is all the Image's Uniform measure
    /// reads.</summary>
    public static Bitmap Synthetic(int width, int height)
        => new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Premul);

    /// <summary>
    /// The thumbnail seam for a pane case: a real <see cref="ThumbnailSlotViewModel"/> over a
    /// worker whose render is immediate, with every callback funnelled onto the UI thread through
    /// one queue. A binding raised off the UI thread is what a bare inline post seam would do here,
    /// and Avalonia refuses it.
    /// </summary>
    public sealed class Thumbnails : IDisposable
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posts = new();
        private readonly Func<string, Bitmap?> _decode;
        private readonly ThumbnailWorker _worker;

        public Thumbnails(Func<string, Bitmap?> decode)
        {
            _decode = decode;
            _worker = new ThumbnailWorker(
                (path, _) => "frames/" + path,
                (path, _) => "previews/" + path,
                post: _posts.Enqueue);
        }

        public ThumbnailSlotViewModel Create(string framePath)
            => new(
                framePath,
                _worker,
                _ => [0x01],
                ThumbnailKind.Frame,
                decode: _ => _decode(framePath),
                post: _posts.Enqueue);

        /// <summary>
        /// Runs queued callbacks on this thread, which under an <c>[AvaloniaFact]</c> is the UI
        /// thread, until <paramref name="settled"/> holds, then lets layout catch up. The
        /// predicate rather than an empty queue is the terminal condition: the render runs on the
        /// worker's own pump and the decode on the thread pool, so an empty queue is where this
        /// starts rather than where it ends.
        /// </summary>
        public void PumpUntil(Func<bool> settled)
        {
            SpinWait.SpinUntil(
                () =>
                {
                    while (_posts.TryDequeue(out var callback))
                    {
                        callback();
                    }

                    Dispatcher.UIThread.RunJobs();
                    return settled();
                },
                ThumbnailBudget);

            while (_posts.TryDequeue(out var callback))
            {
                callback();
            }

            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose() => _worker.Dispose();
    }

    private static RigGroup RigGroupFor(int index, string label, int frameCount = 6)
        => new(
            index,
            label,
            label.Split(" / ")[0],
            "Cam",
            frameCount,
            frameCount * 300d,
            Guid.NewGuid(),
            label + ".fits",
            [label + ".fits"]);

    private static IReadOnlyList<FrameRow> RigFrames(DateOnly date, params string[] rigs)
        => [.. rigs.SelectMany((rig, group) => Enumerable.Range(0, 3).Select(i =>
            FrameTableViewModelTests.Frame(
                fileName: $"{rig[0]}_{i:000}.fits",
                captureDate: date.ToDateTime(new TimeOnly(21, 0)).AddMinutes((group * 30) + i),
                filterUsed: "Ha",
                exposureTime: 300d,
                medianHfr: 2d + (i * 0.01d),
                rig: rig)))];

    public static (T Host, Cards.Harness Harness) RigHost<T>(
        Func<SessionCardViewModel, T> create,
        string[] rigs,
        Thumbnails? thumbnails = null,
        double width = 1600,
        double height = 900,
        IReadOnlyList<MetricRangeSummary>? rigRanges = null)
        where T : Control
    {
        var date = Page.LastSession;
        var frames = RigFrames(date, rigs);
        var holder = new TargetPageState();
        var groups = rigs
            .Select((label, index) => RigGroupFor(index, label) with
            {
                Ranges = rigs.Length > 1 ? rigRanges ?? DefaultRigRanges : null,
            })
            .ToList();

        var harness = Cards.Create(
            detail: Cards.PopulatedDetail(sessionDate: date) with
            {
                Frames = frames,
                Rigs = groups,
                FilterMedians = rigs.Length > 1
                    ? [.. rigs.Select(label => new FilterMedians("Ha", 2.3d, 0.4d, 1.9d, 0.45d, 1400d, label))]
                    : [new FilterMedians("Ha", 2.3d, 0.4d, 1.9d, 0.45d, 1400d)],
                FilterDetails = rigs.Length > 1
                    ? [.. rigs.Select(label => new FilterDetailRow("Ha", 3, 900d, 2.3d, 0.4d, 300d, label))]
                    : [new FilterDetailRow("Ha", 3, 900d, 2.3d, 0.4d, 300d)],
            },
            targetPage: holder,
            createFrameThumbnail: thumbnails is null ? null : thumbnails.Create);

        harness.FrameTableResult = NightPartsTestKit.Table(frames, holder);
        harness.Card.IsExpanded = true;
        harness.Settle();

        var pane = create(harness.Card);
        NightPartsTestKit.Show(pane, width, height);

        // A walk that ran out of candidates settles on the placeholder, not on an image, and that
        // is a settled state too.
        thumbnails?.PumpUntil(() => harness.Card.Rigs.All(
            rig => rig.HasThumbnail || (rig.HasStarted && rig.ShowsPlaceholder)));

        Dispatcher.UIThread.RunJobs();
        return (pane, harness);
    }

    private static readonly IReadOnlyList<MetricRangeSummary> DefaultRigRanges =
    [
        new MetricRangeSummary(1.9d, 3.1d, 2.3d),
        new MetricRangeSummary(0.3d, 0.5d, 0.4d),
        new MetricRangeSummary(1.6d, 2.4d, 1.9d),
        new MetricRangeSummary(0.3d, 0.7d, 0.45d),
        new MetricRangeSummary(-10.5d, -9.5d, -10d),
    ];

    public static IReadOnlyList<Border> Boxes(Control pane)
        => [.. pane.Named<ItemsControl>("ThumbnailStrip")
            .GetVisualDescendants()
            .OfType<Border>()
            .Where(border => border.Name == "ThumbnailBox")];

    public static IReadOnlyList<Border> Placeholders(Control pane)
        => [.. pane.Named<ItemsControl>("ThumbnailStrip")
            .GetVisualDescendants()
            .OfType<Border>()
            .Where(border => border.Name == "ThumbnailPlaceholder")];
}
