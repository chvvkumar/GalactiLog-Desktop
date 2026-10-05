using System.Globalization;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.ViewModels.Stats;

/// <summary>One overview card: a label, a value and an italic subtitle.</summary>
/// <param name="Tooltip">Hover text, empty when the card needs none. Only the size card carries
/// one, because it is the only figure whose meaning is not obvious from its label.</param>
public sealed record OverviewTile(string Label, string Value, string Subtitle, string Tooltip = "");

/// <summary>
/// Spec 12.5's Overview tiles row: the six cards of <c>StatsOverview.tsx</c>, in that order.
/// A projection with no state of its own, rebuilt whenever the page's stats response is replaced.
/// </summary>
/// <remarks>
/// The two averages and the active span are computed here rather than in <c>StatsQuery</c>
/// (Task 2's brief says so): they are divisions of figures the query already returns, and each one
/// has a divisor that can be zero on a fresh library. Every one of them renders
/// <see cref="MetricText.Missing"/> rather than an infinity or a NaN.
/// </remarks>
public sealed class OverviewTilesViewModel
{
    /// <summary>The empty shape, so the page has something to bind before its first load.</summary>
    public OverviewTilesViewModel()
        : this(new StatsOverview(0d, 0, 0, 0, null, null), new StorageStats(0, 0, 0))
    {
    }

    public OverviewTilesViewModel(StatsOverview overview, StorageStats storage)
    {
        Tiles =
        [
            new OverviewTile(
                "Total Integration",
                MetricText.Integration(overview.TotalIntegrationSeconds),
                "all LIGHT frames"),
            new OverviewTile(
                "Total Frames",
                MetricText.Count(overview.TotalFrames),
                "all LIGHT frames"),
            new OverviewTile(
                "Catalogued Size",
                // Review finding M10: unconditional, as the web renders it. MetricText.Bytes(0) is
                // "0 B", which is the honest reading of a catalogue that holds no bytes; the
                // missing placeholder would claim there is no figure.
                MetricText.Bytes(storage.FitsBytesCatalogued),
                // questions.md Q11 dropped fits_disk_bytes, so the web's "<x> on disk" subtitle has
                // no figure to carry. The subtitle says what the number above it actually is
                // instead, which is what Q11's ruling asks for.
                "catalogued bytes, from the database",
                // The web's own tooltip for this card, because it is what explains the figure.
                "Catalogued Size: total size of FITS files recorded in the catalog (sum of file "
                    + "sizes in the database; reflects the include-calibration setting)."),
            new OverviewTile(
                "Active Span",
                Span(overview.FirstSessionDate, overview.LastSessionDate),
                SpanSubtitle(overview.FirstSessionDate, overview.LastSessionDate)),
            new OverviewTile(
                "Avg Rig-Session Length",
                Average(overview.TotalIntegrationSeconds, overview.RigSessionCount),
                string.Create(CultureInfo.InvariantCulture, $"{overview.RigSessionCount:N0} rig-sessions")),
            new OverviewTile(
                "Avg per Target",
                Average(overview.TotalIntegrationSeconds, overview.TargetCount),
                string.Create(CultureInfo.InvariantCulture, $"{overview.TargetCount:N0} resolved targets")),
        ];
    }

    public IReadOnlyList<OverviewTile> Tiles { get; }

    /// <summary>
    /// <c>formatIntegration(total / count)</c>, or the missing placeholder when the count is zero.
    /// Internal so the divide-by-zero guard is asserted directly rather than through the tile list.
    /// </summary>
    internal static string Average(double totalSeconds, int count)
        => count > 0 ? MetricText.Integration(totalSeconds / count) : MetricText.Missing;

    /// <summary>
    /// <c>StatsOverview.tsx::formatSpan</c>, transcribed. Whole months between the two dates, one
    /// fewer when the end day of month is before the start's, floored at zero; then
    /// <c>"&lt; 1 mo"</c> below a month, <c>"N mo"</c> under a year, <c>"Ny"</c> on a whole number
    /// of years and <c>"Ny Nmo"</c> otherwise.
    /// </summary>
    internal static string Span(DateOnly? first, DateOnly? last)
    {
        if (first is not { } start || last is not { } end)
        {
            return MetricText.Missing;
        }

        var months = ((end.Year - start.Year) * 12) + (end.Month - start.Month);
        if (end.Day < start.Day)
        {
            months--;
        }

        months = Math.Max(0, months);
        var years = months / 12;
        var remainder = months % 12;

        return (years, remainder) switch
        {
            (0, 0) => "< 1 mo",
            (0, _) => string.Create(CultureInfo.InvariantCulture, $"{remainder} mo"),
            (_, 0) => string.Create(CultureInfo.InvariantCulture, $"{years}y"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{years}y {remainder}mo"),
        };
    }

    // The web renders `${first} -> ${last}`. Kept verbatim, with the dates in the application's
    // one date rendering (MetricText.Date) rather than the browser locale's.
    private static string SpanSubtitle(DateOnly? first, DateOnly? last)
        => first is { } start && last is { } end
            ? $"{MetricText.Date(start)} -> {MetricText.Date(end)}"
            : "no sessions recorded";
}
