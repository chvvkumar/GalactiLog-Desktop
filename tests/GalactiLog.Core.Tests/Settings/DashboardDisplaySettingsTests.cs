using System.Text.Json;
using GalactiLog.Core.Settings;
using Xunit;

namespace GalactiLog.Core.Tests.Settings;

// Spec 5.8.2's display.dashboard object (Phase 14C, UI layout ruling 4). The shape copies
// TargetPageSettingsTests beside this file: the defaults, the JSON names, the extension-data rule
// and the no-migration rule.
public class DashboardDisplaySettingsTests
{
    [Fact]
    public void Defaults_AreCollapsedAtThreeHundred()
    {
        // Polish 1 ruling 4: a fresh profile opens with the filter panel as its strip. A regression
        // is the dashboard rendering the full panel before the user has ever opened it.
        var settings = new DashboardDisplaySettings();

        Assert.False(settings.FilterPanelExpanded);
        Assert.Equal(300, settings.FilterPanelWidth);
        Assert.Equal(300, settings.ClampedFilterPanelWidth);
    }

    [Fact]
    public void ClampedFilterPanelWidth_ClampsAtBothEnds()
    {
        // The read clamp is a member on the record rather than a line in whichever view-model
        // happens to read the key, so a second reader cannot forget it.
        Assert.Equal(220, new DashboardDisplaySettings { FilterPanelWidth = 10 }.ClampedFilterPanelWidth);
        Assert.Equal(480, new DashboardDisplaySettings { FilterPanelWidth = 4000 }.ClampedFilterPanelWidth);
        Assert.Equal(220, DashboardDisplaySettings.MinPanelWidth);
        Assert.Equal(480, DashboardDisplaySettings.MaxPanelWidth);
        Assert.Equal(300, DashboardDisplaySettings.DefaultPanelWidth);

        // A stored figure inside the range is untouched.
        Assert.Equal(361, new DashboardDisplaySettings { FilterPanelWidth = 361 }.ClampedFilterPanelWidth);
    }

    [Fact]
    public void TheJsonNames_AreTheSpecKeys()
    {
        var document = new DisplaySettings
        {
            Dashboard = new DashboardDisplaySettings { FilterPanelExpanded = false, FilterPanelWidth = 420 },
        };

        var json = JsonSerializer.Serialize(document);

        Assert.Contains("\"dashboard\"", json, StringComparison.Ordinal);
        Assert.Contains("\"filter_panel_expanded\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"filter_panel_width\":420", json, StringComparison.Ordinal);

        // The read clamp is computed and must never reach the document as a key of its own.
        Assert.DoesNotContain("ClampedFilterPanelWidth", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnrecognizedKey_SurvivesARoundTrip()
    {
        const string stored = """
        {"dashboard":{"filter_panel_expanded":false,"filter_panel_width":260,"future_key":7}}
        """;

        var read = JsonSerializer.Deserialize<DisplaySettings>(stored);
        Assert.NotNull(read);
        Assert.False(read!.Dashboard.FilterPanelExpanded);
        Assert.Equal(260, read.Dashboard.FilterPanelWidth);

        var written = JsonSerializer.Serialize(read);
        Assert.Contains("\"future_key\":7", written, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDocumentWithNoDashboardObject_DeserializesToTheDefaults()
    {
        // Spec 5.8.2: no migration. A document carrying no dashboard key at all takes both
        // defaults, not only the ones it is missing.
        var read = JsonSerializer.Deserialize<DisplaySettings>("""{"columns":{}}""");

        Assert.NotNull(read);
        Assert.False(read!.Dashboard.FilterPanelExpanded);
        Assert.Equal(300, read.Dashboard.FilterPanelWidth);
    }
}
