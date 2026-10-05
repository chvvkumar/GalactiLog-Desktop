using System.Text.Json;
using GalactiLog.Core.Settings;
using GalactiLog.Core.Wbpp;
using Xunit;

namespace GalactiLog.Core.Tests.Settings;

// Design-spec 5.8.1's four Phase 16 keys (wbpp_default_os, wbpp_staging_path, wbpp_exclusions,
// wbpp_quality_by_rig) and the tolerant readers that answer for them, WbppSettingsRead.
//
// Every key is held as JsonElement? for one reason: SettingsStore.Deserialize<T> has no catch, so
// a stored value of the wrong JSON kind throws inside GetGeneral and takes the application start
// down rather than one screen. Half of what these cases assert is therefore "the real
// GeneralSettings deserialises this hand-edited document at all"; the other half is "and the
// reader answers the fallback spec 5.8.1 names".
public class WbppSettingsKeysTests
{
    private const string MalformedDocument = """
        {
          "wbpp_default_os": "windows",
          "wbpp_staging_path": { "path": "D" },
          "wbpp_exclusions": ["WBPP", 5, "  ", "bad\"quote", "$x", "ok"],
          "wbpp_quality_by_rig": {
            "NullRig": null,
            "StringRig": "off",
            "BadEnabledRig": { "enabled": "yes", "baseline": "rig" },
            "BadBaselineRig": {
              "enabled": true,
              "baseline": "overall",
              "constraints": [
                { "metric": "nope",  "op": "lte", "value": 1,    "enabled": true },
                { "metric": "ecc",   "op": "eq",  "value": 1,    "enabled": true },
                { "metric": "hfr",   "op": "lte", "value": "x",  "enabled": true },
                { "metric": "fwhm",  "op": "lte", "value": null, "enabled": true },
                { "metric": "ecc",   "op": "lte", "value": 0.55, "enabled": true },
                { "metric": "hfr",   "op": "gte", "value": 1,    "enabled": "yes" },
                { "metric": "rms",   "op": "lte", "value": 1 },
                { "metric": "stars", "op": "gte", "value": 1e999, "enabled": true },
                "junk",
                5
              ]
            }
          },
          "a_key_this_build_does_not_know": { "kept": true }
        }
        """;

    private static readonly string[] NineDefaults =
    [
        "WBPP", "PixInsight", "finals", "WORK_AREA", "masters", "Masters", "MASTERS",
        "*CALIBRATED", "CALIBRATED",
    ];

    private static GeneralSettings Read(string document)
        => JsonSerializer.Deserialize<GeneralSettings>(document)!;

    private static JsonElement Element(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private static WbppQualityState State(bool enabled, QualityBaseline baseline, params RawConstraint[] constraints)
        => new(enabled, baseline, constraints);

    private static void AssertSameState(WbppQualityState expected, WbppQualityState actual)
    {
        // Member by member, not Assert.Equal on the record: WbppQualityState's Constraints member
        // is an IReadOnlyList, whose default equality comparer is a reference test, so two states
        // holding equal constraint lists are never equal as records.
        Assert.Equal(expected.Enabled, actual.Enabled);
        Assert.Equal(expected.Baseline, actual.Baseline);
        Assert.Equal(expected.Constraints, actual.Constraints);
    }

    // The defaults: an absent key and the fallback every reader answers for it.

    [Fact]
    public void TheFourKeys_DefaultToNullElements()
    {
        // Spec 5.8: a document written by an earlier version reads as absent and needs no
        // migration, and the default then lives in exactly one place, the reader.
        var settings = new GeneralSettings();

        Assert.Null(settings.WbppDefaultOsDocument);
        Assert.Null(settings.WbppStagingPathDocument);
        Assert.Null(settings.WbppExclusionsDocument);
        Assert.Null(settings.WbppQualityByRigDocument);
    }

    [Fact]
    public void TheFourReaders_AnswerTheSpecsFallbacks_ForAnAbsentKey()
    {
        var settings = Read("{}");

        Assert.Equal("powershell", WbppSettingsRead.DefaultOs(settings.WbppDefaultOsDocument));
        Assert.Null(WbppSettingsRead.StagingPath(settings.WbppStagingPathDocument));
        Assert.Equal(NineDefaults, WbppSettingsRead.Exclusions(settings.WbppExclusionsDocument));

        var byRig = WbppSettingsRead.ReadQualityByRig(settings.WbppQualityByRigDocument);
        Assert.Empty(byRig.Rigs);
        AssertSameState(WbppQualityState.Default, byRig.For("Askar 120 / ASI2600MM"));
        AssertSameState(WbppQualityState.Default, byRig.For(null));
        AssertSameState(WbppQualityState.Default, byRig.For(""));
        AssertSameState(WbppQualityState.Default, byRig.For("   "));
    }

    [Fact]
    public void TheNineDefaultExclusions_AreThePythonsListInItsOwnOrder()
    {
        // Order is part of the contract: both script flavours emit the patterns in the order the
        // list holds them.
        Assert.Equal(NineDefaults, WbppSettingsRead.Exclusions(null));

        // Finding 16: the generator keeps its own copy of the nine, because a one-home form would
        // make one of Tasks 3a and 4 compile against the other's production file. This assertion is
        // the whole edge between the two transcriptions, so a change to either list without the
        // other is a red case rather than a script that excludes a folder the settings default
        // never named.
        Assert.Equal(ScriptGenerator.DefaultExclusions, WbppSettingsRead.Exclusions(null));
    }

    // The round trip: the four keys under their snake_case names, with two rigs in the map.

    [Fact]
    public void TheFourKeys_RoundTripUnderTheirSnakeCaseNames()
    {
        var unknownRig = State(
            true,
            QualityBaseline.Rig,
            new RawConstraint(WbppMetric.Ecc, ConstraintOp.AtMost, 0.55, true),
            new RawConstraint(WbppMetric.Stars, ConstraintOp.AtLeast, 100, true));
        var stored = WbppSettingsRead.WriteQualityForRig(
            WbppSettingsRead.WriteQualityForRig(null, WbppQualityByRig.DefaultRigKey, State(false, QualityBaseline.Session)),
            "Unknown / Unknown",
            unknownRig);

        var json = JsonSerializer.Serialize(new GeneralSettings
        {
            WbppDefaultOsDocument = WbppSettingsRead.WriteDefaultOs("bash"),
            WbppStagingPathDocument = WbppSettingsRead.WriteStagingPath(@"S:\staging"),
            WbppExclusionsDocument = WbppSettingsRead.WriteExclusions(["WBPP", "finals"]),
            WbppQualityByRigDocument = stored,
        });

        Assert.Contains("\"wbpp_default_os\"", json, StringComparison.Ordinal);
        Assert.Contains("\"wbpp_staging_path\"", json, StringComparison.Ordinal);
        Assert.Contains("\"wbpp_exclusions\"", json, StringComparison.Ordinal);
        Assert.Contains("\"wbpp_quality_by_rig\"", json, StringComparison.Ordinal);

        var round = Read(json);
        Assert.Equal("bash", WbppSettingsRead.DefaultOs(round.WbppDefaultOsDocument));
        Assert.Equal(@"S:\staging", WbppSettingsRead.StagingPath(round.WbppStagingPathDocument));
        Assert.Equal(new[] { "WBPP", "finals" }, WbppSettingsRead.Exclusions(round.WbppExclusionsDocument));

        var readBack = WbppSettingsRead.ReadQualityByRig(round.WbppQualityByRigDocument);
        Assert.Equal(2, readBack.Rigs.Count);
        AssertSameState(State(false, QualityBaseline.Session), readBack.For(WbppQualityByRig.DefaultRigKey));
        AssertSameState(unknownRig, readBack.For("Unknown / Unknown"));

        // Writing a rig back with the state that was read leaves the document byte for byte as it
        // was, so a save that changed nothing changes nothing.
        Assert.Equal(
            round.WbppQualityByRigDocument!.Value.GetRawText(),
            WbppSettingsRead
                .WriteQualityForRig(round.WbppQualityByRigDocument, "Unknown / Unknown", readBack.For("Unknown / Unknown"))
                .GetRawText());
    }

    [Fact]
    public void WriteQualityForRig_ProducesADocumentReadQualityByRigAccepts()
    {
        // The seam review's P1: a reader written against the web's nested { enabled, config: {...} }
        // shape rejects every entry the port's own writer produces, and the user's saved limits
        // read back as the default on the next page open. The port's shape is flat.
        var rig = State(
            true,
            QualityBaseline.Rig,
            new RawConstraint(WbppMetric.Hfr, ConstraintOp.AtMost, 3.0, true),
            new RawConstraint(WbppMetric.Ecc, ConstraintOp.AtMost, 0.55, false),
            new RawConstraint(WbppMetric.Fwhm, ConstraintOp.AtMost, null, true),
            new RawConstraint(WbppMetric.Stars, ConstraintOp.AtLeast, 100, true),
            new RawConstraint(WbppMetric.Rms, ConstraintOp.AtMost, 0.8, true));

        var written = WbppSettingsRead.WriteQualityForRig(
            WbppSettingsRead.WriteQualityForRig(null, "RC8 / ASI2600MM", rig),
            WbppQualityByRig.DefaultRigKey,
            State(false, QualityBaseline.Session));
        var readBack = WbppSettingsRead.ReadQualityByRig(written);

        Assert.Equal(2, readBack.Rigs.Count);
        AssertSameState(rig, readBack.For("RC8 / ASI2600MM"));
        AssertSameState(State(false, QualityBaseline.Session), readBack.For(WbppQualityByRig.DefaultRigKey));
    }

    [Fact]
    public void WritingOneRig_LeavesEveryOtherRigsStoredTextByteIdentical()
    {
        // Spec 5.8.1: "A write replaces one rig's entry" and carries every other entry through
        // unchanged, and the reason one line earlier is a document written by a later build that
        // added a metric. Rig B therefore carries two things this build's typed map cannot hold, an
        // unknown member and a constraint on an unknown metric, and both have to be in the written
        // document unchanged.
        //
        // Both sides of the comparison are the STORED text and the WRITTEN text, never two outputs
        // of the writer: a writer that normalises every entry cancels itself out of that second
        // comparison and the case stays green while the user's stored data goes (settings review
        // P2-2).
        var stored = Element("""
            {
              "Askar 120 / ASI2600MM": { "enabled": true, "baseline": "rig",
                                         "constraints": [ { "metric": "ecc", "op": "lte", "value": 0.55, "enabled": true } ] },
              "Unknown / Unknown": { "enabled": true, "baseline": "rig", "future_knob": 7,
                                     "constraints": [ { "metric": "snr", "op": "gte", "value": 40, "enabled": true },
                                                      { "metric": "stars", "op": "gte", "value": 120, "enabled": true } ] }
            }
            """);
        var untouchedBefore = stored.GetProperty("Unknown / Unknown").GetRawText();

        var written = WbppSettingsRead.WriteQualityForRig(
            stored,
            "Askar 120 / ASI2600MM",
            State(false, QualityBaseline.Session, new RawConstraint(WbppMetric.Hfr, ConstraintOp.AtMost, 4.0, true)));

        Assert.Equal(untouchedBefore, written.GetProperty("Unknown / Unknown").GetRawText());
        Assert.Contains("future_knob", untouchedBefore, StringComparison.Ordinal);
        Assert.Contains("snr", untouchedBefore, StringComparison.Ordinal);

        // And the rig that was written reads back as what was written.
        AssertSameState(
            State(false, QualityBaseline.Session, new RawConstraint(WbppMetric.Hfr, ConstraintOp.AtMost, 4.0, true)),
            WbppSettingsRead.ReadQualityByRig(written).For("Askar 120 / ASI2600MM"));
    }

    [Fact]
    public void WritingOneRig_LeavesAMalformedRigsStoredTextUntouched()
    {
        // A rig this build reads as the default state is still a rig whose stored text is the
        // user's. Normalising it on a write of some other rig would quietly replace a document the
        // user may be about to fix by hand, or that a later build reads perfectly well.
        var stored = Element("""{ "A": "off", "B": { "enabled": false, "baseline": "session", "constraints": [] } }""");
        var untouchedBefore = stored.GetProperty("A").GetRawText();

        var written = WbppSettingsRead.WriteQualityForRig(stored, "B", State(true, QualityBaseline.Rig));

        Assert.Equal(untouchedBefore, written.GetProperty("A").GetRawText());
        AssertSameState(WbppQualityState.Default, WbppSettingsRead.ReadQualityByRig(written).For("A"));
        AssertSameState(State(true, QualityBaseline.Rig), WbppSettingsRead.ReadQualityByRig(written).For("B"));
    }

    [Fact]
    public void WritingARig_KeepsItsPositionAndAddsANewOneAtTheEnd()
    {
        var stored = Element("""{ "A": { "enabled": true, "baseline": "rig", "constraints": [] }, "B": "off" }""");

        var replaced = WbppSettingsRead.WriteQualityForRig(stored, "A", State(false, QualityBaseline.Session));
        Assert.Equal(new[] { "A", "B" }, replaced.EnumerateObject().Select(property => property.Name));

        var added = WbppSettingsRead.WriteQualityForRig(stored, "C", State(false, QualityBaseline.Session));
        Assert.Equal(new[] { "A", "B", "C" }, added.EnumerateObject().Select(property => property.Name));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("5")]
    [InlineData("\"off\"")]
    [InlineData("[]")]
    public void WritingARig_IntoAStoredValueThatIsNotAnObject_StartsFromAnEmptyObject(string value)
    {
        var written = WbppSettingsRead.WriteQualityForRig(Element(value), "A", State(true, QualityBaseline.Rig));

        Assert.Equal(new[] { "A" }, written.EnumerateObject().Select(property => property.Name));
        AssertSameState(State(true, QualityBaseline.Rig), WbppSettingsRead.ReadQualityByRig(written).For("A"));
    }

    // The default slot: read, never shadowed.

    [Fact]
    public void TheDefaultSlot_IsReadAndNotShadowed()
    {
        // A failure here reads as For(null) answering the hardcoded default state, so a user who
        // tunes the filter on a rigless selection can never read back what they saved.
        var byRig = WbppSettingsRead.ReadQualityByRig(Element("""
            { "default": { "enabled": true, "baseline": "rig",
                           "constraints": [ { "metric": "ecc", "op": "lte", "value": 0.55, "enabled": true } ] } }
            """));

        var expected = State(true, QualityBaseline.Rig, new RawConstraint(WbppMetric.Ecc, ConstraintOp.AtMost, 0.55, true));
        AssertSameState(expected, byRig.For(null));
        AssertSameState(expected, byRig.For(""));
        AssertSameState(expected, byRig.For("  "));
        AssertSameState(expected, byRig.For(WbppQualityByRig.DefaultRigKey));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AWriteUnderABlankRig_LandsInTheDefaultKeyAndInNoOther(string? rigKey)
    {
        var written = WbppSettingsRead.WriteQualityForRig(null, rigKey, State(true, QualityBaseline.Rig));

        Assert.Equal(new[] { WbppQualityByRig.DefaultRigKey }, written.EnumerateObject().Select(property => property.Name));
        AssertSameState(State(true, QualityBaseline.Rig), WbppSettingsRead.ReadQualityByRig(written).For(""));
    }

    // The entry is kept where the web keeps it.

    [Fact]
    public void AnEntryWhoseConstraintsAreJunk_KeepsItsEnablementAndBaseline()
    {
        // The web keeps enabled and baseline in this case
        // (const constraints = Array.isArray(cfg.constraints) ? ... : []). Promoting the entry to a
        // full default would silently switch a rig's filter off.
        var state = WbppSettingsRead
            .ReadQualityByRig(Element("""{ "R": { "enabled": true, "baseline": "rig", "constraints": "junk" } }"""))
            .For("R");

        AssertSameState(State(true, QualityBaseline.Rig), state);
    }

    [Fact]
    public void AnEntryWithNoConfigMember_IsWellFormed_AndANestedOneIsNot()
    {
        // The port's stored shape is flat. An entry carrying the web's nested config member has no
        // baseline and no constraints of its own at the flat level, so it reads enabled with the
        // session baseline and nothing else, rather than being rejected.
        var flat = WbppSettingsRead
            .ReadQualityByRig(Element("""{ "R": { "enabled": true, "baseline": "rig", "constraints": [] } }"""))
            .For("R");
        AssertSameState(State(true, QualityBaseline.Rig), flat);

        var nested = WbppSettingsRead
            .ReadQualityByRig(Element("""{ "R": { "enabled": true, "config": { "baseline": "rig", "constraints": [] } } }"""))
            .For("R");
        AssertSameState(State(true, QualityBaseline.Session), nested);
    }

    // The tolerance: one hand-edited document carrying every malformed shape at once.

    [Fact]
    public void AMalformedDocument_DeserialisesIntoGeneralSettings()
    {
        // This is the half a typed property could not pass at all: the throw would happen inside
        // SettingsStore.Deserialize, before any reader could drop anything, and take the whole
        // settings read and with it the application start down (carried item 41).
        var settings = Read(MalformedDocument);

        Assert.NotNull(settings);
        Assert.NotNull(settings.ExtensionData);
        Assert.True(settings.ExtensionData!.ContainsKey("a_key_this_build_does_not_know"));
    }

    [Fact]
    public void AMalformedDocument_ReadsAsTheFallbacks_AndDropsAtTheRightLevel()
    {
        var settings = Read(MalformedDocument);

        // An os literal outside the set is returned as stored here; ScriptGenerator.ParseOs is
        // what reads it as PowerShell, which is why this reader takes no dependency on that file.
        Assert.Equal("windows", WbppSettingsRead.DefaultOs(settings.WbppDefaultOsDocument));
        Assert.Null(WbppSettingsRead.StagingPath(settings.WbppStagingPathDocument));
        Assert.Equal(new[] { "WBPP", "ok" }, WbppSettingsRead.Exclusions(settings.WbppExclusionsDocument));

        var byRig = WbppSettingsRead.ReadQualityByRig(settings.WbppQualityByRigDocument);

        // Four rigs, and no malformed one removed another.
        Assert.Equal(4, byRig.Rigs.Count);
        AssertSameState(WbppQualityState.Default, byRig.For("NullRig"));
        AssertSameState(WbppQualityState.Default, byRig.For("StringRig"));
        AssertSameState(WbppQualityState.Default, byRig.For("BadEnabledRig"));

        // A baseline outside the set reads as session and does NOT discard the entry, and one
        // malformed constraint is dropped while the rest survive, in order.
        var kept = byRig.For("BadBaselineRig");
        Assert.True(kept.Enabled);
        Assert.Equal(QualityBaseline.Session, kept.Baseline);
        Assert.Equal(
            new[]
            {
                new RawConstraint(WbppMetric.Fwhm, ConstraintOp.AtMost, null, true),
                new RawConstraint(WbppMetric.Ecc, ConstraintOp.AtMost, 0.55, true),
            },
            kept.Constraints);
    }

    // One hostile-shape theory per key. Every one of these goes through the real GeneralSettings
    // round trip first, so the case proves both halves: the deserializer does not throw, and the
    // reader answers the fallback.

    [Theory]
    [InlineData("null")]
    [InlineData("5")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("""{"a": [1, {"b": null}, ["c"]]}""")]
    public void WbppDefaultOs_ReadsAsPowershell_ForEveryHostileShape(string value)
    {
        var settings = Read($$"""{"wbpp_default_os": {{value}} }""");

        Assert.Equal("powershell", WbppSettingsRead.DefaultOs(settings.WbppDefaultOsDocument));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("5")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("""{"a": [1, {"b": null}, ["c"]]}""")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public void WbppStagingPath_ReadsAsUnset_ForEveryHostileShape(string value)
    {
        var settings = Read($$"""{"wbpp_staging_path": {{value}} }""");

        Assert.Null(WbppSettingsRead.StagingPath(settings.WbppStagingPathDocument));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("5")]
    [InlineData("true")]
    [InlineData("\"WBPP\"")]
    [InlineData("""{"a": [1, {"b": null}, ["c"]]}""")]
    public void WbppExclusions_ReadsAsTheNineDefaults_ForEveryHostileShape(string value)
    {
        var settings = Read($$"""{"wbpp_exclusions": {{value}} }""");

        Assert.Equal(NineDefaults, WbppSettingsRead.Exclusions(settings.WbppExclusionsDocument));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("5")]
    [InlineData("true")]
    [InlineData("\"off\"")]
    [InlineData("""[{"enabled": true}]""")]
    public void WbppQualityByRig_ReadsAsAnEmptyMap_ForEveryHostileShape(string value)
    {
        var settings = Read($$"""{"wbpp_quality_by_rig": {{value}} }""");
        var byRig = WbppSettingsRead.ReadQualityByRig(settings.WbppQualityByRigDocument);

        Assert.Empty(byRig.Rigs);
        AssertSameState(WbppQualityState.Default, byRig.For("Askar 120 / ASI2600MM"));
    }

    [Fact]
    public void AMegabyteString_InEveryOneOfTheFourKeys_ThrowsNothingAndReadsAsItsFallback()
    {
        var huge = new string('x', 1024 * 1024);
        var json = JsonSerializer.Serialize(new GeneralSettings
        {
            WbppDefaultOsDocument = JsonSerializer.SerializeToElement(huge),
            WbppStagingPathDocument = JsonSerializer.SerializeToElement(huge),
            WbppExclusionsDocument = JsonSerializer.SerializeToElement(huge),
            WbppQualityByRigDocument = JsonSerializer.SerializeToElement(huge),
        });

        var settings = Read(json);

        // The os literal is returned as stored, exactly as any other unrecognised literal is, and
        // ScriptGenerator.ParseOs reads it as PowerShell. The staging path is a legal JSON string,
        // so it reads back as itself: bounding a path's length is the editor's business, not a
        // read-path fallback, and a read must not silently alter a stored destination. The other
        // two are the wrong JSON kind and read as their defaults.
        Assert.Equal(huge.Length, WbppSettingsRead.DefaultOs(settings.WbppDefaultOsDocument).Length);
        Assert.Equal(huge.Length, WbppSettingsRead.StagingPath(settings.WbppStagingPathDocument)!.Length);
        Assert.Equal(NineDefaults, WbppSettingsRead.Exclusions(settings.WbppExclusionsDocument));
        Assert.Empty(WbppSettingsRead.ReadQualityByRig(settings.WbppQualityByRigDocument).Rigs);
    }

    // The exclusion drop rules, entry by entry.

    [Theory]
    [InlineData("bad\"quote")]
    [InlineData("$x")]
    [InlineData("`x")]
    [InlineData("two\rlines")]
    [InlineData("two\nlines")]
    [InlineData(@"masters\")]
    [InlineData("ma”;whoami;“sters")]
    public void AnExclusionEntryCarryingARefusedCharacter_IsDropped(string entry)
    {
        // A pattern reaches both script flavours inside a quoted literal, so one of these entries
        // is what breaks out of the quoting (spec 5.8.1). The editor refuses it and the reader
        // drops it, which is design lesson 2's structural half: the value cannot arrive by a
        // hand edit either. The reader and the generator share one predicate,
        // ScriptGenerator.IsRefusedPatternChar, so the backslash, which ends a Bash double-quoted
        // literal, and the Unicode quote characters, which end a PowerShell one, cannot be present
        // on one side of that pair and missing on the other.
        var stored = JsonSerializer.SerializeToElement(new[] { "WBPP", entry, "ok" });

        Assert.Equal(new[] { "WBPP", "ok" }, WbppSettingsRead.Exclusions(stored));
    }

    [Fact]
    public void AnExclusionArray_DropsNonStringsAndBlanks_AndAnEmptyArrayStaysEmpty()
    {
        Assert.Equal(
            new[] { "WBPP", "ok" },
            WbppSettingsRead.Exclusions(Element("""["WBPP", 5, null, true, [], {}, "", "   ", "\t", "ok"]""")));

        // A user who cleared the list meant it: the scripts then exclude no folder.
        Assert.Empty(WbppSettingsRead.Exclusions(Element("[]")));
    }

    [Fact]
    public void AnExclusionListWrittenThenRead_KeepsItsOrder()
    {
        IReadOnlyList<string> patterns = ["masters", "WBPP", "finals"];

        Assert.Equal(patterns, WbppSettingsRead.Exclusions(WbppSettingsRead.WriteExclusions(patterns)));
    }

    // The staging path and the os literal, written and read.

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WriteStagingPath_WritesUnsetAsJsonNull(string? value)
    {
        var written = WbppSettingsRead.WriteStagingPath(value);

        Assert.Equal(JsonValueKind.Null, written.ValueKind);
        Assert.Null(WbppSettingsRead.StagingPath(written));
    }

    [Fact]
    public void WriteStagingPath_RoundTripsAPath()
    {
        Assert.Equal(@"S:\staging", WbppSettingsRead.StagingPath(WbppSettingsRead.WriteStagingPath(@"S:\staging")));
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("bash")]
    public void WriteDefaultOs_RoundTripsItsLiteral(string literal)
    {
        Assert.Equal(literal, WbppSettingsRead.DefaultOs(WbppSettingsRead.WriteDefaultOs(literal)));
    }

    // The baseline literals.

    [Theory]
    [InlineData("rig", QualityBaseline.Rig)]
    [InlineData("RIG", QualityBaseline.Session)]
    [InlineData("Rig", QualityBaseline.Session)]
    [InlineData("session", QualityBaseline.Session)]
    [InlineData("overall", QualityBaseline.Session)]
    [InlineData(null, QualityBaseline.Session)]
    [InlineData("", QualityBaseline.Session)]
    public void ParseBaseline_ReadsAnythingOutsideTheSetAsSession(string? stored, QualityBaseline expected)
    {
        // Ordinal and case sensitive by coordinator ruling at the settings review: the web's own
        // test is an equality against the two literals, and spec 5.8.1's "anything else reads as
        // session" reads that way. RIG is anything else.
        Assert.Equal(expected, WbppSettingsRead.ParseBaseline(stored));
    }

    [Theory]
    [InlineData(QualityBaseline.Session, "session")]
    [InlineData(QualityBaseline.Rig, "rig")]
    public void ToStored_WritesTheWebsOwnLiterals(QualityBaseline baseline, string expected)
    {
        Assert.Equal(expected, WbppSettingsRead.ToStored(baseline));
        Assert.Equal(baseline, WbppSettingsRead.ParseBaseline(expected));
    }

    // The stored constraint literals are the web's lte and gte, not atmost and atleast.

    [Fact]
    public void AConstraintIsStoredUnderTheWebsOwnLiterals()
    {
        var written = WbppSettingsRead.WriteQualityForRig(
            null,
            "R",
            State(
                true,
                QualityBaseline.Session,
                new RawConstraint(WbppMetric.Stars, ConstraintOp.AtLeast, 100, true),
                new RawConstraint(WbppMetric.Hfr, ConstraintOp.AtMost, 3.5, true)));

        var constraints = written.GetProperty("R").GetProperty("constraints");
        Assert.Equal("stars", constraints[0].GetProperty("metric").GetString());
        Assert.Equal("gte", constraints[0].GetProperty("op").GetString());
        Assert.Equal("hfr", constraints[1].GetProperty("metric").GetString());
        Assert.Equal("lte", constraints[1].GetProperty("op").GetString());
    }

    // Totality against the one input class a wrong JSON kind does not cover: a stored string
    // carrying an unpaired UTF-16 escape. The JSON grammar permits it and the deserializer accepts
    // it, so the throw lands later, inside a reader, on whoever opens the export page rather than
    // at start. Every probe goes through the real GeneralSettings round trip first, which is where
    // the document proves it deserialised and the reader proves it did not throw.

    [Fact]
    public void AnUnreadableOsLiteral_ReadsAsPowershell()
    {
        var settings = Read("""{"wbpp_default_os": "\ud800"}""");

        Assert.Equal("powershell", WbppSettingsRead.DefaultOs(settings.WbppDefaultOsDocument));
    }

    [Fact]
    public void AnUnreadableStagingPath_ReadsAsUnset()
    {
        var settings = Read("""{"wbpp_staging_path": "\udfff x"}""");

        Assert.Null(WbppSettingsRead.StagingPath(settings.WbppStagingPathDocument));
    }

    [Fact]
    public void AnUnreadableExclusionEntry_IsDroppedAndTheRestSurvive()
    {
        var settings = Read("""{"wbpp_exclusions": ["WBPP", "\ud800", "ok"]}""");

        Assert.Equal(new[] { "WBPP", "ok" }, WbppSettingsRead.Exclusions(settings.WbppExclusionsDocument));
    }

    [Fact]
    public void AnUnreadableBaseline_ReadsAsSession()
    {
        var settings = Read("""{"wbpp_quality_by_rig": {"R": {"enabled": true, "baseline": "\ud800", "constraints": []}}}""");
        var state = WbppSettingsRead.ReadQualityByRig(settings.WbppQualityByRigDocument).For("R");

        Assert.True(state.Enabled);
        Assert.Equal(QualityBaseline.Session, state.Baseline);
    }

    [Fact]
    public void AnUnreadableConstraintLiteral_DropsThatConstraintAndKeepsTheRest()
    {
        var settings = Read("""
            {"wbpp_quality_by_rig": {"R": {"enabled": true, "baseline": "rig", "constraints": [
              {"metric": "\ud800", "op": "lte", "value": 1, "enabled": true},
              {"metric": "ecc", "op": "\ud800", "value": 1, "enabled": true},
              {"metric": "ecc", "op": "lte", "value": 0.55, "enabled": true}]}}}
            """);
        var state = WbppSettingsRead.ReadQualityByRig(settings.WbppQualityByRigDocument).For("R");

        Assert.Equal(
            new[] { new RawConstraint(WbppMetric.Ecc, ConstraintOp.AtMost, 0.55, true) },
            state.Constraints);
    }

    [Fact]
    public void AnUnreadableRigKey_DropsThatEntryAndKeepsTheOthers()
    {
        // Spec 5.8.1's "A non-string key is dropped", which looked unreachable in JSON and is not:
        // JsonProperty.Name is where the decode happens.
        var settings = Read("""
            {"wbpp_quality_by_rig": {
              "\ud800": {"enabled": true, "baseline": "rig", "constraints": []},
              "R": {"enabled": true, "baseline": "rig", "constraints": []}}}
            """);
        var byRig = WbppSettingsRead.ReadQualityByRig(settings.WbppQualityByRigDocument);

        Assert.Equal(new[] { "R" }, byRig.Rigs.Keys);
        AssertSameState(State(true, QualityBaseline.Rig), byRig.For("R"));
    }

    [Fact]
    public void AnUnreadableRigKey_IsAlsoDroppedByAWriteOfAnotherRig()
    {
        var written = WbppSettingsRead.WriteQualityForRig(
            Read("""{"wbpp_quality_by_rig": {"\ud800": {"enabled": true}}}""").WbppQualityByRigDocument,
            "R",
            State(true, QualityBaseline.Rig));

        Assert.Equal(new[] { "R" }, written.EnumerateObject().Select(property => property.Name));
    }

    // Two rules the readers' own comments state and no other case covered.

    [Fact]
    public void AStagingPath_IsReturnedAsStoredAndNeverTrimmed()
    {
        // The member's comment says the editor normalises a path and a read must not silently
        // alter a destination. A trailing space is legal in a Windows path and a reader that
        // trimmed one would hand the script a different folder than the one that was stored.
        const string padded = "  S:\\staging  ";

        Assert.Equal(padded, WbppSettingsRead.StagingPath(WbppSettingsRead.WriteStagingPath(padded)));
    }

    [Fact]
    public void AnEntryWithNoEnabledMember_KeepsItsBaselineAndConstraints()
    {
        // A ruled departure from the web, which returns the full default state when enabled is not
        // a boolean and treats an absent member as exactly that. task3a.md section 6a item 2
        // narrows malformed to "enabled is present and is not a boolean", which is what this file
        // implements. Low consequence either way: the rig reads off and gates nothing.
        var state = WbppSettingsRead
            .ReadQualityByRig(Element("""
                { "R": { "baseline": "rig",
                         "constraints": [ { "metric": "ecc", "op": "lte", "value": 0.55, "enabled": true } ] } }
                """))
            .For("R");

        AssertSameState(
            State(false, QualityBaseline.Rig, new RawConstraint(WbppMetric.Ecc, ConstraintOp.AtMost, 0.55, true)),
            state);
    }

    // One limit that is not these four keys', recorded so it is not rediscovered as one.

    [Fact]
    public void ADeeplyNestedStoredValue_IsTheDocumentsLimitAndNotThisKeysFallback()
    {
        // SettingsStore.Deserialize passes a default JsonSerializerOptions, so MaxDepth is 64 and
        // a deeper stored value throws JsonException before any reader here runs. It is document
        // wide, applies the same way to an unknown key travelling through ExtensionData, predates
        // Phase 16 and is carried item 41. The day that method gains a catch, this case changes to
        // assert the reader's fallback instead of the throw; nothing about these four keys moves.
        var within = $$"""{"wbpp_exclusions": {{new string('[', 62)}}{{new string(']', 62)}} }""";
        var beyond = $$"""{"wbpp_exclusions": {{new string('[', 65)}}{{new string(']', 65)}} }""";

        // Within the limit the document reads and the reader answers, which is the half that is
        // this key's business. A nested array is still an array, so the drop rules apply to its one
        // non-string entry and the list reads empty rather than as the nine defaults.
        Assert.Empty(WbppSettingsRead.Exclusions(Read(within).WbppExclusionsDocument));
        Assert.Throws<JsonException>(() => Read(beyond));
    }
}
