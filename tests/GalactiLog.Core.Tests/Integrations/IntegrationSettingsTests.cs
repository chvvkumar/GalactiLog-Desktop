using System.Text.Json;
using GalactiLog.Core.Integrations;
using GalactiLog.Core.Settings;
using Xunit;

namespace GalactiLog.Core.Tests.Integrations;

/// <summary>
/// Design-spec 5.8.1's four Phase 21 keys and the tolerant readers that answer for them,
/// task2-clients.md cases 17 to 21. Half of what these cases assert is that the real
/// <see cref="GeneralSettings"/> deserialises a hand-edited document at all, because
/// <c>SettingsStore.Deserialize</c> has no catch and a throw here takes the application start down
/// rather than one screen; the other half is the fallback spec 5.8.1 names.
/// </summary>
public class IntegrationSettingsTests
{
    private static JsonElement Element(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    // Every JSON kind a reader must survive, including the unpaired UTF-16 escape the grammar
    // permits and JsonElement.GetString throws on.
    public static TheoryData<string> HostileKinds =>
    [
        "\"a string\"",
        "5",
        "[1, 2]",
        "{\"a\": {\"nested\": [1]}}",
        "null",
        "\"\\ud800\"",
        "[\"\\ud800\", 5, null, [1], {\"name\": \"\\ud800\"}]",
    ];

    // Case 17. An id is an identity and not a magnitude, so a value outside the rule is dropped
    // and never clamped: a clamp would write a different filter into the reader's upload.
    [Fact]
    public void ReadFilterIdsDropsEveryValueThatIsNotAPositiveIntegerAndEveryUnreadableKey()
    {
        var ids = IntegrationSettings.ReadFilterIds(Element("""
            {"Zero": 0, "Negative": -3, "Text": "7", "Float": 2.5, "\ud800": 9,
             "Nested": {"id": 4}, "Null": null, "Ha": 4021, "Oiii": 1}
            """));

        Assert.Equal(2, ids.Count);
        Assert.Equal(4021, ids["Ha"]);
        Assert.Equal(1, ids["Oiii"]);
    }

    // Case 18. The cleared box removes the key rather than storing 0, and a write of one filter
    // must not delete a key a later build wrote. The unreadable stored value is the carry-through
    // leg of the totality claim: it reaches WriteRawValue, which never reads the string.
    [Fact]
    public void WriteFilterIdWithANullIdRemovesOnlyThatKeyAndCarriesTheRestThrough()
    {
        var stored = Element(
            """{"Ok":"\ud800","Ha":1,"Future":{"nested":[1,2],"unknown":"x"},"Oiii":2}""");

        var written = IntegrationSettings.WriteFilterId(stored, "Ha", null);

        Assert.False(written.TryGetProperty("Ha", out _));
        Assert.Equal(2, written.GetProperty("Oiii").GetInt32());
        Assert.Equal(
            """{"Ok":"\ud800","Future":{"nested":[1,2],"unknown":"x"},"Oiii":2}""",
            written.GetRawText());
    }

    // Case 18, the other half: a value replaces in place and a new key appends, so a write is as
    // small a change to the stored text as it can be.
    [Fact]
    public void WriteFilterIdReplacesInPlaceAndAppendsANewKey()
    {
        var stored = Element("""{"Ha":1,"Oiii":2}""");

        var replaced = IntegrationSettings.WriteFilterId(stored, "Ha", 99);
        var added = IntegrationSettings.WriteFilterId(replaced, "Sii", 7);

        Assert.Equal("""{"Ha":99,"Oiii":2,"Sii":7}""", added.GetRawText());
    }

    // Case 19. A hand-edited value outside the range reaches the CSV otherwise.
    [Theory]
    [InlineData(0, 1)]
    [InlineData(12, 9)]
    [InlineData(1, 1)]
    [InlineData(9, 9)]
    [InlineData(-40, 1)]
    public void ReadBortleClampsToTheNearerBound(int stored, int expected)
        => Assert.Equal(expected, IntegrationSettings.ReadBortle(stored));

    [Fact]
    public void ReadBortlePassesNullThrough()
        => Assert.Null(IntegrationSettings.ReadBortle(null));

    // Case 20. A half-filled row is kept so the tab can show it and the reader can repair it;
    // IsOffered is the one predicate that decides whether the menu offers it.
    [Fact]
    public void ReadInstancesKeepsHalfFilledRowsAndNoneOfThemIsOffered()
    {
        var instances = IntegrationSettings.ReadInstances(Element("""
            [{"name": "   ", "url": "http://h:1888", "enabled": true},
             {"name": "Rig", "url": "", "enabled": true},
             {"name": "Rig", "url": "ftp://h", "enabled": true},
             {"name": "Rig", "url": "http://h:1888", "enabled": false},
             {"name": 5, "enabled": "yes"},
             {"name": "Good", "url": "http://h:1888/", "enabled": true}]
            """));

        Assert.Equal(6, instances.Count);
        Assert.Equal("", instances[4].Name);
        Assert.Equal("", instances[4].Url);
        Assert.False(instances[4].Enabled);
        Assert.All(instances.Take(5), instance => Assert.False(IntegrationInstance.IsOffered(instance)));
        Assert.True(IntegrationInstance.IsOffered(instances[5]));
    }

    [Fact]
    public void WriteInstancesRoundTripsThroughReadInstances()
    {
        IReadOnlyList<IntegrationInstance> instances =
        [
            new("Rig one", "http://h:1888", true),
            new("", "", false),
        ];

        Assert.Equal(instances, IntegrationSettings.ReadInstances(IntegrationSettings.WriteInstances(instances)));
    }

    // Case 21. Every reader is total: no stored value, whatever a hand edit put there, stops this
    // application starting.
    [Theory]
    [MemberData(nameof(HostileKinds))]
    public void EveryReaderAnswersForEveryHostileStoredKind(string json)
    {
        var stored = Element(json);

        Assert.Empty(IntegrationSettings.ReadFilterIds(stored));
        Assert.All(
            IntegrationSettings.ReadInstances(stored),
            instance => Assert.Equal("", instance.Name));

        // The writer is total for the same reason: a stored value that is not an object starts
        // from an empty one, and an unreadable key is dropped exactly as the reader drops it.
        Assert.Equal(3, IntegrationSettings.WriteFilterId(stored, "Ha", 3).GetProperty("Ha").GetInt32());
    }

    [Fact]
    public void WriteFilterIdDropsAnUnreadableKeyExactlyAsTheReaderDoes()
    {
        var written = IntegrationSettings.WriteFilterId(
            Element("""{"\ud800": 5, "Oiii": 2}"""), "Ha", null);

        Assert.Equal("""{"Oiii":2}""", written.GetRawText());
    }

    [Fact]
    public void EveryReaderAnswersForAnAbsentKey()
    {
        Assert.Empty(IntegrationSettings.ReadFilterIds(null));
        Assert.Empty(IntegrationSettings.ReadInstances(null));
        Assert.Null(IntegrationSettings.ReadBortle(null));
        Assert.Equal("{}", IntegrationSettings.WriteFilterId(null, "Ha", null).GetRawText());
    }

    // The four keys hold a hand-edited document through the real record, which is the half of
    // totality that lives in GeneralSettings rather than in the readers.
    [Fact]
    public void GeneralSettingsDeserialisesAHandEditedDocumentAndTheReadersAnswerForIt()
    {
        var settings = JsonSerializer.Deserialize<GeneralSettings>("""
            {
              "astrobin_filter_ids": "not an object",
              "astrobin_bortle": 47,
              "nina_instances": {"not": "an array"},
              "stellarium_instances": [{"name": "S", "url": "https://s/", "enabled": true}]
            }
            """)!;

        Assert.Empty(IntegrationSettings.ReadFilterIds(settings.AstroBinFilterIdsDocument));
        Assert.Equal(9, IntegrationSettings.ReadBortle(settings.AstroBinBortle));
        Assert.Empty(IntegrationSettings.ReadInstances(settings.NinaInstancesDocument));
        var stellarium = Assert.Single(IntegrationSettings.ReadInstances(settings.StellariumInstancesDocument));
        Assert.True(IntegrationInstance.IsOffered(stellarium));
    }
}
