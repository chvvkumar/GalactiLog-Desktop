using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.ViewModels.Stats;

/// <summary>One camera or telescope row of spec 12.5's Equipment inventory.</summary>
/// <param name="FwhmFrameCount">Rendered beside the median as <c>n=NNN</c>, in a tertiary colour,
/// so a median over four frames does not read like a median over four hundred.
/// <see cref="MetricText.Missing"/> when no frame carried an FWHM.</param>
public sealed record EquipmentInventoryRow(
    string Name,
    bool Grouped,
    string FrameCount,
    string Nights,
    string TargetCount,
    string AvgSession,
    string Integration,
    string MedianFwhm,
    string FwhmFrameCount,
    string MedianGuidingRms);

/// <summary>
/// Spec 12.5's Equipment inventory row: two tables, cameras first and telescopes second, in the
/// order the query returned them.
/// </summary>
/// <remarks>
/// No client-side sort. The web's <c>EquipmentInventory.tsx</c> has none either, and adding one
/// here would mean the order a user sees depends on which screen they are looking at.
/// </remarks>
public sealed class EquipmentInventoryViewModel
{
    /// <summary>The empty shape, so the page has something to bind before its first load.
    /// </summary>
    public EquipmentInventoryViewModel()
        : this([], [])
    {
    }

    public EquipmentInventoryViewModel(
        IReadOnlyList<EquipmentInventoryItem> cameras,
        IReadOnlyList<EquipmentInventoryItem> telescopes)
    {
        Cameras = [.. cameras.Select(Row)];
        Telescopes = [.. telescopes.Select(Row)];
    }

    /// <summary>Rendered first, matching the web.</summary>
    public IReadOnlyList<EquipmentInventoryRow> Cameras { get; }

    public IReadOnlyList<EquipmentInventoryRow> Telescopes { get; }

    public bool IsEmpty => Cameras.Count == 0 && Telescopes.Count == 0;

    /// <summary>The web's own wording for this section.</summary>
    public string EmptyMessage => "No equipment data";

    private static EquipmentInventoryRow Row(EquipmentInventoryItem item) => new(
        item.Name,
        item.Grouped,
        MetricText.Count(item.FrameCount),
        MetricText.Count(item.Nights),
        MetricText.Count(item.TargetCount),
        item.AvgSessionSeconds is { } average ? MetricText.Integration(average) : MetricText.Missing,
        MetricText.Integration(item.IntegrationSeconds),
        // Arcseconds, unit in the column header.
        MetricText.Cell(item.MedianFwhmArcsec, "0.00"),
        item.FwhmFrameCount > 0 ? $"n={MetricText.Count(item.FwhmFrameCount)}" : MetricText.Missing,
        MetricText.Cell(item.MedianGuidingRmsArcsec, "0.00"));
}
