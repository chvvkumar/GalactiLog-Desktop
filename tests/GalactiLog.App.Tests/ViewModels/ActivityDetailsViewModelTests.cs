using System.Text.Json;
using GalactiLog.App.ViewModels.Activity;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Spec 12.6: "Details render as a key-value table, falling back to formatted JSON." Ported from
// ActivityFeed.tsx's RowDetails precedence, with the two failed-file list cases folded into the
// general case because this port emits neither payload (spec 10.9's table has no such key).
public class ActivityDetailsViewModelTests
{
    [Fact]
    public void ShallowPrimitiveObject_RendersAsAKeyValueTable()
    {
        var details = ActivityDetailsViewModel.Create(
            """{"deleted_count":1200,"retention_days":90,"dry_run":false,"note":null}""");

        Assert.True(details.ShowTable);
        Assert.False(details.ShowJson);
        Assert.Null(details.Json);
        Assert.Equal(
            ["Deleted count", "Retention days", "Dry run", "Note"],
            details.Rows.Select(row => row.Key));
        Assert.Equal(["1,200", "90", "No", "null"], details.Rows.Select(row => row.Value));

        // Numeric values are the ones the view right-aligns with tabular figures.
        Assert.Equal([true, true, false, false], details.Rows.Select(row => row.IsNumeric));
    }

    [Fact]
    public void NestedObject_FallsBackToFormattedJson()
    {
        var details = ActivityDetailsViewModel.Create("""{"outcome":{"generated":4,"failed":1}}""");

        Assert.False(details.ShowTable);
        Assert.True(details.ShowJson);
        Assert.Empty(details.Rows);

        // Indented two spaces, which is System.Text.Json's own default and matches the web's
        // JSON.stringify(details, null, 2).
        Assert.Contains("  \"outcome\":", details.Json);
        Assert.Contains("    \"generated\": 4", details.Json);
    }

    [Fact]
    public void ArrayValue_FallsBackToFormattedJson()
    {
        var details = ActivityDetailsViewModel.Create("""{"trigger":"manual","roots":["D:\\Lights"]}""");

        Assert.False(details.ShowTable);
        Assert.True(details.ShowJson);
        Assert.Contains("roots", details.Json);
    }

    [Fact]
    public void TopLevelArray_FallsBackToFormattedJson()
    {
        var details = ActivityDetailsViewModel.Create("""[1,2,3]""");

        Assert.True(details.ShowJson);
        Assert.Empty(details.Rows);
    }

    [Fact]
    public void NoDetails_RendersNothingAtAll()
    {
        var details = ActivityDetailsViewModel.Create(null);

        Assert.False(details.ShowTable);
        Assert.False(details.ShowJson);
    }

    [Theory]
    [InlineData("deleted_count", "Deleted count")]
    [InlineData("retention_days", "Retention days")]
    [InlineData("skipped_calibration", "Skipped calibration")]
    [InlineData("known-rows", "Known rows")]
    [InlineData("movedImageIds", "Moved image ids")]
    [InlineData("error", "Error")]
    [InlineData("", "")]
    public void HumanizeKey_MatchesTheWebRule(string key, string expected)
        => Assert.Equal(expected, ActivityDetailsViewModel.HumanizeKey(key));

    [Theory]
    [InlineData("null", "null")]
    [InlineData("true", "Yes")]
    [InlineData("false", "No")]
    [InlineData("1200", "1,200")]
    [InlineData("0", "0")]
    [InlineData("-42", "-42")]
    [InlineData("2.123456789", "2.1235")]
    [InlineData("1234.5", "1,234.5")]
    [InlineData("\"disk full\"", "disk full")]
    public void FormatValue_MatchesTheWebRule(string json, string expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(expected, ActivityDetailsViewModel.FormatValue(document.RootElement));
    }

    [Fact]
    public void InvalidJson_RendersTheRawString_AndDoesNotThrow()
    {
        var details = ActivityDetailsViewModel.Create("{not json at all");

        // One hand-edited or truncated payload must not be able to take the page down, and the raw
        // string is exactly what a reader needs to see when a payload is malformed.
        Assert.True(details.ShowJson);
        Assert.Equal("{not json at all", details.Json);
        Assert.Empty(details.Rows);
    }

    [Theory]
    [InlineData(null, "anything", false)]
    [InlineData("", "anything", false)]
    [InlineData("{}", "anything", false)]
    [InlineData("""{"action":"open"}""", "anything", false)]
    [InlineData("""{"count":12}""", "Orphan rows pruned: 12", false)]
    [InlineData("""{"count":12,"action":"open"}""", "Orphan rows pruned: 12", false)]
    [InlineData("""{"count":12}""", "Orphan rows pruned", true)]
    [InlineData("""{"count":12,"root":"D:\\Lights"}""", "Orphan rows pruned: 12", true)]
    [InlineData("""{"error":"disk full"}""", "Scan failed: disk full", true)]
    [InlineData("not json", "anything", true)]
    public void HasRenderableDetails_IsTheWebGate(string? details, string message, bool expected)
        => Assert.Equal(expected, ActivityDetailsViewModel.HasRenderableDetails(details, message));
}
