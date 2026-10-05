using System.Text.Json;
using System.Text.Json.Serialization;

namespace GalactiLog.Core.Settings;

// design-spec 5.8.3. Defaults from schemas/settings.py::GraphSettings.
public sealed record GraphSettings
{
    [JsonPropertyName("enabled_metrics")]
    public string[] EnabledMetrics { get; init; } = ["hfr", "eccentricity", "fwhm", "guiding_rms"];

    [JsonPropertyName("enabled_filters")]
    public string[] EnabledFilters { get; init; } = ["overall"];

    [JsonPropertyName("default_chart_sessions")] public int DefaultChartSessions { get; init; } = 1;

    /// <summary>Phase 24 R3: the night chart's Guiding pill, stored with the pill choices it sits
    /// beside. On, the plot draws the guide trace instead of the metric dots.</summary>
    [JsonPropertyName("show_guiding")]
    public bool ShowGuiding { get; init; }

    /// <summary>The retired <c>session_chart_expanded</c> and <c>target_chart_expanded</c> keys ride
    /// here and nothing reads them.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
