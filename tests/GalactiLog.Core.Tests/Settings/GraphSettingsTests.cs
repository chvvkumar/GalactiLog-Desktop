using System.Text.Json;
using GalactiLog.Core.Settings;
using Xunit;

namespace GalactiLog.Core.Tests.Settings;

// Phase 24 R3: the night chart's Guiding pill is stored with the metric and filter pill choices,
// in the graph document, under show_guiding.
public class GraphSettingsTests
{
    // A failure is the key landing in ExtensionData, which is where an unknown key rides: the
    // flag would survive a round trip but nothing could read it.
    [Fact]
    public void ShowGuiding_IsAKnownKeyOfTheGraphDocument_AndDefaultsOff()
    {
        var stored = JsonSerializer.Deserialize<GraphSettings>("""{"show_guiding":true}""")!;
        var fresh = JsonSerializer.Deserialize<GraphSettings>("{}")!;

        Assert.True(stored.ExtensionData is null || !stored.ExtensionData.ContainsKey("show_guiding"));
        Assert.Contains("\"show_guiding\":", JsonSerializer.Serialize(fresh), StringComparison.Ordinal);
    }

    // A failure is the flag written on but read back off, or a profile that never wrote the key
    // opening with the guide trace.
    [Fact]
    public void ShowGuiding_RoundTrips_AndIsOffByDefault()
    {
        Assert.False(new GraphSettings().ShowGuiding);

        var written = JsonSerializer.Serialize(new GraphSettings { ShowGuiding = true });
        var read = JsonSerializer.Deserialize<GraphSettings>(written)!;

        Assert.True(read.ShowGuiding);
        Assert.Equal(["overall"], read.EnabledFilters);
    }
}
