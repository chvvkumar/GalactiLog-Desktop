using CommunityToolkit.Mvvm.ComponentModel;
using GalactiLog.App.Theme;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Data.Queries;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace GalactiLog.App.ViewModels.Stats;

/// <summary>One labelled byte figure of spec 12.5's Storage row.</summary>
public sealed record StorageFigure(string Label, string Value, string Subtitle);

/// <summary>
/// Spec 12.5's Storage row and spec 13's Storage breakdown pie.
/// </summary>
/// <remarks>
/// <para>
/// Three slices, not spec 13's four. The fourth, "other disk usage", was the difference between a
/// <c>du -sb</c> over the FITS directory and the catalogued total, and questions.md Q11 dropped
/// that figure: this port neither spawns a process to measure a user directory nor walks a scan
/// root on a page load. The three surviving slices keep spec 13's first three colours,
/// <c>metric-integration</c>, <c>metric-frames</c> and <c>metric-stars</c>;
/// <c>border-emphasis</c> goes unused. Coordinator ruling carried in the Task 2 review message,
/// which amends spec 13's Storage breakdown row at phase close.
/// </para>
/// <para>
/// The catalogued figure's subtitle says so in words, for the same reason: with no on-disk total
/// to compare against, a user reading "catalogued size" needs to be told it is a sum of database
/// rows and not a measurement of their disk.
/// </para>
/// </remarks>
public sealed partial class StorageViewModel : ObservableObject, IDisposable
{
    /// <summary>The three slices, in spec 13's order, paired with the token each is painted in.
    /// </summary>
    private static readonly (string Label, string TokenKey)[] SliceOrder =
    [
        ("Catalogued FITS", "ColorMetricIntegration"),
        ("Thumbnail cache", "ColorMetricFrames"),
        ("Database", "ColorMetricStars"),
    ];

    // FIXER LIST F8: the theme subscription and its unsubscribe as one token. The unsubscribe is
    // the half that matters: ChartTheme.Changed is static, so a handler left behind pins this
    // view-model for the life of the process.
    private readonly IDisposable _themeSubscription;

    private StorageStats _stats = new(0, 0, 0);
    private bool _disposed;

    public StorageViewModel()
    {
        Series = [];
        Figures = [];
        Update(_stats);
        _themeSubscription = ChartTheme.Subscribe(OnThemeChanged);
    }

    /// <summary>Spec 13's row name for this chart.</summary>
    public string Title => "Storage breakdown";

    [ObservableProperty]
    public partial IReadOnlyList<ISeries> Series { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<StorageFigure> Figures { get; private set; }

    /// <summary>True when every figure is zero, which is a library nothing has been catalogued
    /// into yet. A pie of three zero slices renders as nothing at all, so the view shows the
    /// message instead.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    public string EmptyMessage => "Nothing catalogued yet.";

    public void Update(StorageStats stats)
    {
        _stats = stats;
        Figures =
        [
            new StorageFigure(
                SliceOrder[0].Label,
                MetricText.Bytes(stats.FitsBytesCatalogued),
                "catalogued bytes, summed from the database"),
            new StorageFigure(
                SliceOrder[1].Label,
                MetricText.Bytes(stats.ThumbnailCacheBytes),
                "rendered thumbnails and previews"),
            new StorageFigure(
                SliceOrder[2].Label,
                MetricText.Bytes(stats.DatabaseBytes),
                "the SQLite file and its write-ahead log"),
        ];

        var values = new long[] { stats.FitsBytesCatalogued, stats.ThumbnailCacheBytes, stats.DatabaseBytes };
        IsEmpty = values.All(value => value <= 0);
        Series = IsEmpty
            ? []
            : [
                .. SliceOrder.Select((slice, index) => (ISeries)new PieSeries<double>
                {
                    Name = slice.Label,
                    Values = [Math.Max(0d, values[index])],
                    Fill = new SolidColorPaint(Colour(slice.TokenKey)),
                }),
            ];
    }

    private static SKColor Colour(string tokenKey)
        => ChartTheme.Palette.Metrics.TryGetValue(tokenKey, out var colour) ? colour : ChartTheme.Fallback;

    private void OnThemeChanged() => Update(_stats);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _themeSubscription.Dispose();
    }
}
