using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Mosaics;
using GalactiLog.Core.Mosaics;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels.Mosaics;

// Phase 18 Task 4. The stubs the Mosaics page and table tests share.
internal static class MosaicsTestData
{
    internal static readonly Guid TargetA = Guid.NewGuid();
    internal static readonly Guid TargetB = Guid.NewGuid();

    internal static DateOnly Night(int day) => new(2026, 3, day);

    internal static MosaicSuggestionRow Suggestion(string name, params string[] labels)
        => new(
            Guid.NewGuid(), name, name,
            [.. labels.Select((label, index) => new SuggestionPanel(index % 2 == 0 ? TargetA : TargetB, label, "%", [Night(1)]))],
            "high", "both", null, [], "sig-" + name, DateTime.UtcNow);

    internal static MosaicListRow Mosaic(string name, int panels = 1, double seconds = 60, int frames = 1, int? first = null)
        => new(Guid.NewGuid(), name, panels, seconds, frames,
            first is { } day ? Night(day) : null, first is { } last ? Night(last) : null, []);

    // A mutable general document behind MutateGeneral, the way SettingsStore behaves.
    internal sealed class GeneralStore
    {
        public GeneralSettings Value { get; set; } = new();

        public GeneralSettings Mutate(Func<GeneralSettings, GeneralSettings> mutate) => Value = mutate(Value);
    }

    internal static async Task<MosaicsPageHarness> Ready(MosaicsBackend backend, DisplaySettings? display = null, ScanStatusService? scanStatus = null)
    {
        var harness = new MosaicsPageHarness(backend, display, scanStatus);
        await harness.Page.PendingLoad;
        return harness;
    }

    internal static PanelDetail Panel(string label, IReadOnlyList<string> targets)
        => new(Guid.NewGuid(), label, 0, null, null, 0, false, [TargetA], targets, 600, 2, 1, 0, 0, [], [], new Dictionary<string, double>());

    internal static MosaicDetail Detail(Guid id, IReadOnlyList<PanelDetail> panels)
        => new(id, "M 31", null, 0, panels.Sum(panel => panel.IntegrationSeconds), panels.Sum(panel => panel.Frames),
            null, null, [], panels, []);
}
