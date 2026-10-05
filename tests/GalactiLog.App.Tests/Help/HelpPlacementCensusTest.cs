using System.Text.RegularExpressions;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Diagnostics;
using GalactiLog.App.ViewModels.Setup;
using GalactiLog.Core.Help;
using Xunit;

namespace GalactiLog.App.Tests.Help;

// Spec 12.12's census, in the shape of ControlStyleScanTest and FontSizeTokenTest: a plain text
// pass over the shipped markup, no shell and no rendering. Three assertions, and the
// falsifiability case TRACKING item 31 requires of a scan rule.
//
// Adding a page or a section to the application therefore fails this test until its topic is added
// to HelpTopics and placed, which is the structural enforcement DESIGN.md prefers to a convention:
// a help paragraph that every new view has to remember to carry is one that view N+1 forgets.
//
// The count is placements in markup, not placements at run time. DiagnosticsView is hosted both by
// the navigation rail and by the Settings Diagnostics tab, UnresolvedNamesView by the Targets
// settings tab and by the Diagnostics Unresolved group, and MergeHistoryView by the Targets
// settings tab and by the Target detail Details panel. Each declares its glyphs once.
public class HelpPlacementCensusTest
{
    /// <summary>A <c>HelpButton</c> element with any namespace prefix, up to its first close.
    /// </summary>
    private static readonly Regex HelpButtonElement = new(
        @"<(?:\w+:)?HelpButton\b[^>]*?/?>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex TopicAttribute = new(
        @"\bTopic\s*=\s*""([^""]*)""",
        RegexOptions.Compiled);

    private static readonly Regex XmlComment = new(
        @"<!--.*?-->",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// The attribute a bound placement carries. Both bound sites spell it the same way, because
    /// both bind the topic id off whichever view-model the surrounding template or window is
    /// bound to, so the id set a site expands to is resolved by the file rather than by the
    /// attribute (see <see cref="BoundSites"/>).
    /// </summary>
    private const string BoundTopicAttribute = "{Binding HelpTopicId}";

    /// <summary>
    /// The bound placements spec 12.12 allows: a set of headings rendered by one
    /// <c>DataTemplate</c>, or one shell heading standing for several pages, so there is one
    /// markup site for several ids.
    /// <list type="bullet">
    /// <item>Ruling Q2: seven Diagnostics group headers are one template over
    /// <c>DiagnosticsGroupViewModel</c>.</item>
    /// <item>Task 2B's fix pass: the setup wizard's five steps share the shell's one heading row,
    /// which binds the current step's topic.</item>
    /// </list>
    /// Each site contributes the set of ids that view-model's own total switch can produce, read
    /// from the type rather than restated here, because two copies of a list drift. The map is
    /// keyed by the markup file's name and is consulted only for an element carrying
    /// <see cref="BoundTopicAttribute"/>; a bound glyph in any other file is reported as the empty
    /// id rather than silently expanding to the wrong set.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> BoundSites =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["DiagnosticsView.axaml"] = DiagnosticsGroupViewModel.HelpTopicIds,
            ["SetupWizardWindow.axaml"] = SetupStepViewModel.HelpTopicIds,
            // Phase 24 R3 (ruling R10): the night chart's one glyph reads the metrics topic, or
            // the guiding topic while the Guiding pill is on.
            ["NightMetricsPart.axaml"] = GalactiLog.App.ViewModels.TargetDetail.SessionChartViewModel.HelpTopicIds,
            // Wizard ruling E3: the export wizard's one glyph reads the current step's topic.
            ["WbppExportWindow.axaml"] = GalactiLog.App.ViewModels.TargetDetail.Wbpp.WbppExportStepViewModel.HelpTopicIds,
        };

    /// <summary>The placed ids in one piece of markup, with the bound site expanded.</summary>
    /// <remarks>An element carrying no <c>Topic</c> at all contributes the empty string, which no
    /// topic id equals, so it fails the first assertion rather than vanishing from the census.
    /// </remarks>
    internal static IReadOnlyList<string> PlacedIn(string markup, string fileName = "")
    {
        var placed = new List<string>();

        foreach (Match element in HelpButtonElement.Matches(XmlComment.Replace(markup, "")))
        {
            var topic = TopicAttribute.Match(element.Value);
            var value = topic.Success ? topic.Groups[1].Value : "";

            if (value == BoundTopicAttribute && BoundSites.TryGetValue(fileName, out var ids))
            {
                placed.AddRange(ids);
                continue;
            }

            placed.Add(value);
        }

        return placed;
    }

    /// <summary>Every placed id under <c>src/GalactiLog.App</c>, with the file it was placed in.
    /// </summary>
    private static List<(string Id, string File)> Census()
    {
        var placed = new List<(string, string)>();
        var scanned = 0;

        foreach (var file in SourceScan.EnumerateMarkupFiles("GalactiLog.App"))
        {
            scanned++;
            var relative = Path.GetRelativePath(SourceScan.SrcRoot(), file);
            foreach (var id in PlacedIn(File.ReadAllText(file), Path.GetFileName(file)))
            {
                placed.Add((id, relative));
            }
        }

        Assert.True(scanned > 0, "No .axaml files were scanned under src/GalactiLog.App.");
        return placed;
    }

    [Fact]
    public void EveryPlacedTopic_ExistsInHelpTopics()
    {
        var offenders = Census()
            .Where(placement => !HelpTopics.Ids.Contains(placement.Id))
            .Select(placement => $"{placement.File}: {(placement.Id.Length == 0 ? "no Topic attribute" : placement.Id)}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "A HelpButton names a topic id that is not in HelpTopics, so HelpTopics.Get would "
            + "throw at run time. Offenders:" + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void EveryTopic_IsPlacedExactlyOnce()
    {
        var counts = Census()
            .GroupBy(placement => placement.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        var offenders = new List<string>();
        foreach (var id in HelpTopics.Ids.Order(StringComparer.Ordinal))
        {
            if (!counts.TryGetValue(id, out var placements))
            {
                offenders.Add($"{id}: placed nowhere");
                continue;
            }

            if (placements.Count > 1)
            {
                offenders.Add(
                    $"{id}: placed {placements.Count} times, in "
                    + string.Join(", ", placements.Select(placement => placement.File)));
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Spec 12.12 places one glyph per heading, once each. Offenders:" + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void ThePlacedSet_EqualsTheSpecTable()
    {
        // Against HelpTopics.Ids, which is spec 12.12's table transcribed once. The ids are not
        // written a second time here: two copies drift, and the figure itself is asserted in
        // GalactiLog.Core.Tests HelpTopicsTests and nowhere else.
        var placed = Census().Select(placement => placement.Id).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            HelpTopics.Ids.Order(StringComparer.Ordinal),
            placed.Order(StringComparer.Ordinal));
    }

    // TRACKING item 31: a scan rule needs a falsifiability case that fires it on its own offender.
    // A census that has never been seen to fail is a census that may be scanning nothing, which is
    // exactly how a needle that matches no real markup ships inert.
    [Fact]
    public void TheCensus_FiresOnItsOwnOffender()
    {
        Assert.Equal(new[] { "page.activity" }, PlacedIn("""
            <StackPanel Orientation="Horizontal" Spacing="6">
              <TextBlock Classes="section-label" Text="Activity" />
              <controls:HelpButton Topic="page.activity" />
            </StackPanel>
            """));

        // An id that is not in the table is reported rather than skipped.
        var unknown = PlacedIn("""<controls:HelpButton Topic="page.nowhere" />""");
        Assert.Equal(new[] { "page.nowhere" }, unknown);
        Assert.DoesNotContain(unknown[0], HelpTopics.Ids);

        // An element with no Topic at all is reported rather than vanishing.
        Assert.Equal(new[] { "" }, PlacedIn("""<controls:HelpButton />"""));

        // A second placement of one id is two entries, which is what the once-each rule counts.
        Assert.Equal(
            new[] { "target.notes", "target.notes" },
            PlacedIn("""
                <controls:HelpButton Topic="target.notes" />
                <HelpButton Topic="target.notes" />
                """));

        // Each bound site expands to the ids its own view-model's switch produces, and to those
        // only. If a Phase 15 group is added to DiagnosticsViewModel.GroupTitles, or a sixth step
        // to the wizard, this grows with it and the once-each rule then demands the new topic
        // exists in HelpTopics.
        var boundMarkup = $$"""<controls:HelpButton Topic="{{BoundTopicAttribute}}" />""";

        var groups = PlacedIn(boundMarkup, "DiagnosticsView.axaml");
        Assert.Equal(DiagnosticsGroupViewModel.HelpTopicIds, groups);
        Assert.Equal(7, groups.Count);
        Assert.All(groups, id => Assert.Contains(id, HelpTopics.Ids));

        var steps = PlacedIn(boundMarkup, "SetupWizardWindow.axaml");
        Assert.Equal(SetupStepViewModel.HelpTopicIds, steps);
        Assert.Equal(5, steps.Count);
        Assert.All(steps, id => Assert.Contains(id, HelpTopics.Ids));

        // The two sites spell the attribute identically, so the file is what tells them apart and
        // the wrong file must not expand to the wrong set.
        Assert.NotEqual(groups, steps);

        // A bound glyph in a file the map does not know expands to nothing: the binding text is
        // carried through as the placed id, which is in no topic table, so a third bound site
        // fails EveryPlacedTopic_ExistsInHelpTopics until it is added to BoundSites rather than
        // silently borrowing another surface's ids.
        var unmapped = PlacedIn(boundMarkup, "SomeOtherView.axaml");
        Assert.Equal(new[] { BoundTopicAttribute }, unmapped);
        Assert.DoesNotContain(unmapped[0], HelpTopics.Ids);

        // A commented-out glyph is not a placement: the scan strips XML comments first, so a
        // glyph parked in a comment cannot satisfy the once-each rule.
        Assert.Empty(PlacedIn("""<!-- <controls:HelpButton Topic="page.activity" /> -->"""));
    }
}
