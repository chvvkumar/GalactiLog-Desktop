using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Reflection;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Analysis;
using GalactiLog.Core.Metrics;
using GalactiLog.Data.Queries;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels.Analysis;

/// <summary>
/// Spec 12.14's Compare tab (Task 6, Compare).
/// </summary>
/// <remarks>
/// <para>
/// Windowless, so every tab takes the inline post seam of the harness ruling. Every assertion on a
/// query count, a state or a series collection is preceded by an await on <c>PendingLoad</c> or by
/// an identity check on it.
/// </para>
/// <para>
/// The two group pickers are fed the way the page feeds them: the filter bar's own two collection
/// instances are constructor arguments, so a bar list read that lands after the tab is built still
/// reaches them.
/// </para>
/// </remarks>
public class CompareTabTests : IDisposable
{
    private const string NoFramesText =
        "No frames match the current filters. Widen them in the filter bar above.";

    private const string TelescopeA = "RC8";
    private const string CameraA = "ASI2600MM";
    private const string TelescopeB = "Askar FMA180";
    private const string CameraB = "ASI533MC";
    private const string TelescopeC = "Esprit 100";
    private const string CameraC = "ASI294MC";

    private readonly List<CompareTabViewModel> _tabs = [];

    public void Dispose()
    {
        foreach (var tab in _tabs)
        {
            tab.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private static AnalysisFilter Filter(DateOnly? from = null, DateOnly? to = null)
        => new("RC8", "ASI2600MM", "Ha", AnalysisGranularity.Session, from, to);

    private static async Task<SharedFilterViewModel> Bar()
    {
        var bar = new SharedFilterViewModel(
            () => [new EquipmentCombination(TelescopeA, CameraA, false), new EquipmentCombination(TelescopeB, CameraB, true)],
            () => ["Ha", "OIII"],
            post: action => action());

        // A bar built alone starts its own read; a page built through AnalysisViewModel has its
        // Load called as the last statement of that page's constructor.
        bar.Load();
        await bar.PendingLoad;
        return bar;
    }

    private CompareTabViewModel Tab(
        Func<AnalysisMetric, CompareMode, string, string, DateOnly?, DateOnly?, CompareResult>? query = null,
        Func<AnalysisFilter?>? filter = null,
        ObservableCollection<EquipmentChoice>? equipment = null,
        ObservableCollection<string>? filters = null,
        bool show = true)
    {
        var tab = new CompareTabViewModel(
            filter ?? (() => Filter()),
            query ?? ((_, _, _, _, _, _) => Result()),
            equipment ?? [],
            filters ?? [],
            action => action());

        _tabs.Add(tab);
        if (show)
        {
            tab.IsVisible = true;
        }

        return tab;
    }

    // The tab as the page builds it: the filter bar own two collection INSTANCES, handed to the
    // constructor, with the bar asynchronous list read already landed.
    private async Task<CompareTabViewModel> Wired(
        Func<AnalysisMetric, CompareMode, string, string, DateOnly?, DateOnly?, CompareResult>? query = null,
        Func<AnalysisFilter?>? filter = null)
    {
        var bar = await Bar();
        return Tab(query, filter, bar.EquipmentChoices, bar.FilterChoices);
    }

    private static BoxPlot Box(string name) => new(name, 1d, 2d, 3d, 4d, 5d, [], 12);

    private static CompareResult Result(
        CompareState state = CompareState.Ok,
        string nameA = "RC8 + ASI2600MM",
        string nameB = "Askar FMA180 + ASI533MC",
        int countA = 40,
        int countB = 38,
        string? verdict = "Group one has 12% lower median than group two (N=40 vs N=38) (arcsec)",
        bool comparable = true,
        double? arcsecA = 1.1d,
        double? arcsecB = 1.3d)
        => new(
            state,
            nameA,
            nameB,
            countA,
            countB,
            state == CompareState.Ok ? new CompareGroup(nameA, Box("query key A"), new SummaryStats(40, 1d, 5d, 3d, 2.5d, 0.7d)) : null,
            state == CompareState.Ok ? new CompareGroup(nameB, Box("query key B"), new SummaryStats(38, 2d, 6d, 4d, 3.5d, 0.9d)) : null,
            state == CompareState.Ok ? verdict : null,
            comparable,
            comparable ? arcsecA : null,
            comparable ? arcsecB : null);

    private static async Task<CompareTabViewModel> Chosen(
        CompareTabViewModel tab, int indexA = 0, int indexB = 1)
    {
        tab.SelectedGroupA = tab.GroupChoices[indexA];
        tab.SelectedGroupB = tab.GroupChoices[indexB];
        await tab.PendingLoad!;
        return tab;
    }

    // ---- case 21, section 3.2's OWNER ruling --------------------------------------------------

    // RED against the shell as landed, which published AnalysisTabState.Empty and therefore the
    // base's generic no-frames sentence. The first assertion is the proof: it names the generic
    // sentence against that shell. Also red against a tab that queries on any change.
    [Fact]
    public async Task NoGroupChosen_ShowsTheChooseSentenceVerbatimAndQueriesNothing()
    {
        var calls = 0;
        var tab = await Wired(query: (_, _, _, _, _, _) => { calls++; return Result(); });

        Assert.Equal("Select two different groups to compare.", tab.StatusLine);
        Assert.NotEqual(NoFramesText, tab.StatusLine);
        Assert.Null(tab.PendingLoad);
        Assert.Equal(0, calls);
        Assert.False(tab.ShowsResult);

        // The body exists all the same, which is what makes the two pickers reachable.
        Assert.NotNull(tab.Body);
    }

    [Fact]
    public async Task OneGroupChosen_ShowsTheChooseSentenceVerbatimAndQueriesNothing()
    {
        var calls = 0;
        var tab = await Wired(query: (_, _, _, _, _, _) => { calls++; return Result(); });

        tab.SelectedGroupA = tab.GroupChoices[0];

        Assert.Equal(CompareTabViewModel.ChooseGroupsText, tab.StatusLine);
        Assert.NotEqual(NoFramesText, tab.StatusLine);
        Assert.Null(tab.PendingLoad);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task TheSameGroupTwice_ShowsTheChooseSentenceVerbatimAndQueriesNothing()
    {
        var calls = 0;
        var tab = await Wired(query: (_, _, _, _, _, _) => { calls++; return Result(); });

        tab.SelectedGroupA = tab.GroupChoices[0];
        tab.SelectedGroupB = tab.GroupChoices[0];

        Assert.Equal(CompareTabViewModel.ChooseGroupsText, tab.StatusLine);
        Assert.NotEqual(NoFramesText, tab.StatusLine);
        Assert.Null(tab.PendingLoad);
        Assert.Equal(0, calls);
    }

    // ---- case 22 ------------------------------------------------------------------------------

    // Red against a switch that keeps them: the tab then queries an equipment name against the
    // filter mode.
    [Fact]
    public async Task SwitchingTheSegment_ClearsBothGroups()
    {
        var tab = await Chosen(await Wired());

        Assert.Equal(AnalysisTabState.Ready, tab.State);

        var pending = tab.PendingLoad;
        tab.Mode = CompareMode.Filter;

        Assert.Same(pending, tab.PendingLoad);
        Assert.Null(tab.SelectedGroupA);
        Assert.Null(tab.SelectedGroupB);
        Assert.Equal(string.Empty, tab.GroupA);
        Assert.Equal(string.Empty, tab.GroupB);
        Assert.Equal(CompareTabViewModel.ChooseGroupsText, tab.StatusLine);

        // And the pickers now offer the filters rather than the equipment combinations.
        Assert.Equal(["Ha", "OIII"], tab.GroupChoices.Select(choice => choice.Value));
    }

    // ---- case 22a, ruling P2-9 and ruling P3-11 -----------------------------------------------

    // Red against a tab that sends the LABEL: the string carries " + " and the assertion names it,
    // and every equipment comparison would then match no rows, silently.
    [Fact]
    public async Task AnEquipmentSelection_ReachesTheQueryAsTheWireValueAndNotTheLabel()
    {
        string? seenA = null;
        string? seenB = null;
        var tab = await Wired(query: (_, _, groupA, groupB, _, _) =>
        {
            seenA = groupA;
            seenB = groupB;
            return Result();
        });

        // Index 1 is the combination whose telescope folds two aliases.
        var grouped = tab.GroupChoices[1];
        Assert.Equal($"{TelescopeB} + {CameraB}", grouped.Label);
        Assert.True(grouped.Grouped);
        Assert.False(tab.GroupChoices[0].Grouped);

        await Chosen(tab, indexA: 1, indexB: 0);

        Assert.Equal(TelescopeB + CompareGroups.EquipmentSeparator + CameraB, seenA);

        // ONE home for the encoding, in Data beside the thing that splits on it: a second spelling
        // in the App let the two drift silently.
        Assert.Equal("|||", CompareGroups.EquipmentSeparator);
        Assert.DoesNotContain(" + ", seenA);
        Assert.DoesNotContain(SharedFilterViewModel.GroupedMarker, seenA);
        Assert.Equal(TelescopeA + CompareGroups.EquipmentSeparator + CameraA, seenB);
    }

    // The bar's two sentinel rows are a scope and not a group, and offering one would send an
    // empty or sentinel string into the query.
    [Fact]
    public async Task NeitherPicker_OffersTheAllEquipmentOrAllFiltersRow()
    {
        var tab = await Wired();

        Assert.Equal(2, tab.GroupChoices.Count);
        Assert.DoesNotContain(
            SharedFilterViewModel.AllEquipmentLabel,
            tab.GroupChoices.Select(choice => choice.Label));
        Assert.All(tab.GroupChoices, choice => Assert.Contains(CompareGroups.EquipmentSeparator, choice.Value));

        tab.Mode = CompareMode.Filter;

        Assert.Equal(2, tab.GroupChoices.Count);
        Assert.DoesNotContain(
            SharedFilterViewModel.AllFiltersLabel,
            tab.GroupChoices.Select(choice => choice.Label));
    }

    // ---- case 23 ------------------------------------------------------------------------------

    // The dates ARE applied and the bar's equipment and filter selection is not: the two groups
    // are that selection, and applying the bar's on top would let a reader compare two groups the
    // bar had already emptied. This case is what would catch a signature change.
    [Fact]
    public async Task TheDatesReachTheQueryAndTheBarsOtherSelectionsDoNot()
    {
        var from = new DateOnly(2025, 6, 1);
        var to = new DateOnly(2025, 6, 30);
        DateOnly? seenFrom = null;
        DateOnly? seenTo = null;
        AnalysisMetric? seenMetric = null;
        CompareMode? seenMode = null;

        var tab = await Wired(
            query: (metric, mode, _, _, low, high) =>
            {
                seenMetric = metric;
                seenMode = mode;
                seenFrom = low;
                seenTo = high;
                return Result();
            },
            filter: () => Filter(from, to));

        await Chosen(tab);

        Assert.Equal(from, seenFrom);
        Assert.Equal(to, seenTo);
        Assert.Equal(AnalysisMetric.Hfr, seenMetric);
        Assert.Equal(CompareMode.Equipment, seenMode);

        // The tab holds no AnalysisFilter of its own, which is what makes the rule structural
        // rather than a promise.
        Assert.DoesNotContain(
            typeof(CompareTabViewModel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(AnalysisFilter));
    }

    // ---- case 24 ------------------------------------------------------------------------------

    // CHANGED by the F2 ruling of fix wave 3. This case pinned the query's string printed verbatim,
    // which is how the persisted key reached the reader in the verdict. The tab now rebuilds the
    // sentence through the SAME Analysis.CompareVerdict with the reader's own two names, so what is
    // pinned instead is that only the two names differ: the wording, the percentage, the tail and
    // the rounding are still that one function's, and the figures are still the query's own.
    // Red against a tab that re-formats the percentage or composes a sentence of its own, and red
    // against the shipped verbatim print, whose sentence names the two wire values.
    [Fact]
    public async Task TheVerdictIsTheQuerysSentenceWithOnlyTheTwoNamesDifferent()
    {
        var tab = await Wired(query: (_, _, groupA, groupB, _, _) => Result(
            nameA: groupA,
            nameB: groupB,
            verdict: GalactiLog.Core.Metrics.Analysis.CompareVerdict(groupA, groupB, 1.1d, 1.3d, 40, 38, " (arcsec)")));

        await Chosen(tab);

        var expected = GalactiLog.Core.Metrics.Analysis.CompareVerdict(
            $"{TelescopeA} + {CameraA}",
            $"{TelescopeB} + {CameraB} {SharedFilterViewModel.GroupedMarker}",
            1.1d,
            1.3d,
            40,
            38,
            " (arcsec)");

        Assert.Equal(expected, tab.Verdict);
        Assert.DoesNotContain(CompareGroups.EquipmentSeparator, tab.Verdict, StringComparison.Ordinal);
        Assert.False(tab.IsNotComparable);
    }

    // ---- case 25, ruling A29 and seam ruling S9 -----------------------------------------------

    // The web's OTHER not-comparable sentence, the one that reads the two arcsecond medians, is
    // unreachable: analysis.py lines 910 to 913 set both medians only inside the branch that also
    // keeps comparable true. It is deliberately not built, and the property that makes it
    // unreachable is asserted rather than the absent string.
    [Fact]
    public async Task NotComparable_ShowsTheOneSentenceAndBothArcsecondMediansAreNull()
    {
        CompareResult? answered = null;
        var tab = await Wired(query: (_, _, _, _, _, _) =>
        {
            answered = Result(verdict: null, comparable: false);
            return answered;
        });

        await Chosen(tab);

        Assert.True(tab.IsNotComparable);
        Assert.Equal(string.Empty, tab.Verdict);
        Assert.False(answered!.Comparable);
        Assert.Null(answered.MedianArcsecA);
        Assert.Null(answered.MedianArcsecB);
        Assert.Equal(
            "Different optical trains: pixel HFR is not comparable between these groups, and "
            + "arcsecond data is unavailable (plate scale unknown). No improvement figure is shown.",
            CompareTabViewModel.NotComparableText);
    }

    // The unwritten branch, asserted as source rather than as an absent string on screen: a tab
    // that grew the web's second sentence would carry its words here.
    [Fact]
    public void TheUnreachableNotComparableSentenceIsNotBuilt()
    {
        var source = File.ReadAllText(Path.Combine(
            SourceScan.SrcRoot(), "GalactiLog.App", "ViewModels", "Analysis", "CompareTabViewModel.cs"));

        Assert.DoesNotContain("Comparing in arcseconds", source, StringComparison.OrdinalIgnoreCase);

        // NARROWED by the F2 ruling of fix wave 3. The scan was "MedianArcsec" appears nowhere,
        // which said the unwritten sentence is unwritten by saying the tab reads neither median at
        // all. The tab now reads both, to tell which branch of the query built the verdict it is
        // rebuilding with the reader's names, so the scan says the thing it meant: the two medians
        // reach no string. The case above still asserts both are null whenever the sentence would
        // have been reachable.
        foreach (var line in source.Split('\n').Where(line => line.Contains("MedianArcsec", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("$\"", line, StringComparison.Ordinal);
            Assert.DoesNotContain("ToString", line, StringComparison.Ordinal);
        }
    }

    // ---- case 26, seam ruling S7: four states, four cases --------------------------------------

    // Red proof: a tab that always names A passes the first and fails the second; a tab that reads
    // GroupA.Stats.Count < 4 instead of the published state passes all three short cases and fails
    // the no-rows one, which is the exact confusion S7 exists to end.
    //
    // The three of them are also the too-few half of finding F2 of the launched look, and their
    // expected names CHANGED with its ruling. They read the fabricated "Alpha rig" and "Beta rig"
    // the query was made to answer, which the shipped tab printed; the query echoes the two names
    // it is GIVEN, so on the running application those names were the wire values, pipes and all.
    // Each now hands the query's echo back unchanged, as the shipped query does, and reads the
    // chosen row's own label out of the sentence. Red against the shipped tab, whose sentence
    // carries the separator that the second assertion of each names.
    [Fact]
    public async Task GroupAShort_NamesGroupAAndNotGroupB()
    {
        var tab = await Wired(query: (_, _, groupA, groupB, _, _) => Result(
            CompareState.GroupAShort, nameA: groupA, nameB: groupB, countA: 2, countB: 40));

        await Chosen(tab);

        Assert.Equal(AnalysisTabState.TooFew, tab.State);
        Assert.Contains(LabelA, tab.StatusLine, StringComparison.Ordinal);
        Assert.DoesNotContain(LabelB, tab.StatusLine, StringComparison.Ordinal);
        Assert.DoesNotContain(CompareGroups.EquipmentSeparator, tab.StatusLine, StringComparison.Ordinal);
        Assert.Contains("2", tab.StatusLine, StringComparison.Ordinal);
        Assert.False(tab.ShowsResult);
        Assert.Empty(tab.Chart.Series);
    }

    [Fact]
    public async Task GroupBShort_NamesGroupBAndNotGroupA()
    {
        var tab = await Wired(query: (_, _, groupA, groupB, _, _) => Result(
            CompareState.GroupBShort, nameA: groupA, nameB: groupB, countA: 40, countB: 3));

        await Chosen(tab);

        Assert.Equal(AnalysisTabState.TooFew, tab.State);
        Assert.Contains(LabelB, tab.StatusLine, StringComparison.Ordinal);
        Assert.DoesNotContain(LabelA, tab.StatusLine, StringComparison.Ordinal);
        Assert.DoesNotContain(CompareGroups.EquipmentSeparator, tab.StatusLine, StringComparison.Ordinal);
        Assert.Contains("3", tab.StatusLine, StringComparison.Ordinal);
        Assert.False(tab.ShowsResult);
    }

    [Fact]
    public async Task BothGroupsShort_NamesBoth()
    {
        var tab = await Wired(query: (_, _, groupA, groupB, _, _) => Result(
            CompareState.BothShort, nameA: groupA, nameB: groupB, countA: 1, countB: 2));

        await Chosen(tab);

        Assert.Equal(AnalysisTabState.TooFew, tab.State);
        Assert.Contains(LabelA, tab.StatusLine, StringComparison.Ordinal);
        Assert.Contains(LabelB, tab.StatusLine, StringComparison.Ordinal);
        Assert.DoesNotContain(CompareGroups.EquipmentSeparator, tab.StatusLine, StringComparison.Ordinal);
        Assert.False(tab.ShowsResult);
    }

    [Fact]
    public async Task NoRows_ShowsTheBasesEmptyWordingAndNamesNeitherGroup()
    {
        var tab = await Wired(query: (_, _, _, _, _, _) => Result(
            CompareState.NoRows, nameA: "Alpha rig", nameB: "Beta rig", countA: 0, countB: 0));

        await Chosen(tab);

        Assert.Equal(AnalysisTabState.Empty, tab.State);
        Assert.Equal(NoFramesText, tab.StatusLine);
        Assert.DoesNotContain("Alpha rig", tab.StatusLine, StringComparison.Ordinal);
        Assert.DoesNotContain("Beta rig", tab.StatusLine, StringComparison.Ordinal);
        Assert.False(tab.ShowsResult);
    }

    // No literal four anywhere in the tab: four is the query's threshold and a second copy here
    // would disagree the next time it moves.
    [Fact]
    public void TheShortGroupSentences_DeriveNothingFromACountOfTheirOwn()
    {
        var source = File.ReadAllText(Path.Combine(
            SourceScan.SrcRoot(), "GalactiLog.App", "ViewModels", "Analysis", "CompareTabViewModel.cs"));

        Assert.DoesNotContain("CountA <", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CountB <", source, StringComparison.Ordinal);

        // NARROWED by the F2 ruling of fix wave 3. The scan was "Stats.Count" appears nowhere,
        // which forbade a threshold of the tab's own by forbidding the tab any count at all. The
        // verdict rebuilt with the reader's two names has to hand Analysis.CompareVerdict the same
        // two counts the query handed it, which are Stats.Count of each group, so the scan now
        // forbids the thing it meant: no count is COMPARED here, and the four is still nowhere.
        Assert.DoesNotContain("Stats.Count <", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Stats.Count >", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Stats.Count =", source, StringComparison.Ordinal);
        Assert.DoesNotContain("< 4", source, StringComparison.Ordinal);
        Assert.DoesNotContain(">= 4", source, StringComparison.Ordinal);
    }

    // ---- case 27 ------------------------------------------------------------------------------

    // Spec 12.14's "When it is shown" excludes Compare outright: it converts to arcseconds and
    // carries its own not-comparable sentence. Red against an IsPixelMetric that is not
    // overridden to false.
    [Fact]
    public async Task TheMixedPlateScaleWarningNeverAppearsOnThisTab()
    {
        var tab = await Wired();
        await Chosen(tab);

        Assert.Equal(AnalysisMetric.Hfr, tab.Metric);
        Assert.False(tab.PlateScaleWarningVisible);
        Assert.Equal(0, tab.DistinctPlateScales);

        var pixelMetric = typeof(CompareTabViewModel)
            .GetProperty("IsPixelMetric", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.False((bool)pixelMetric!.GetValue(tab)!);
    }

    // ---- case 28 ------------------------------------------------------------------------------

    // Spec 13: "The Compare tab draws the same one series with exactly two categories". Red
    // against two series.
    [Fact]
    public async Task TheResult_IsTwoCategoriesInOneBoxSeries()
    {
        var tab = await Wired();
        await Chosen(tab);

        Assert.Equal(AnalysisTabState.Ready, tab.State);
        Assert.True(tab.ShowsResult);

        var box = Assert.Single(tab.Chart.Series.OfType<BoxSeries<BoxValue>>());
        Assert.Equal(2, box.Values!.Count());

        // Two categories, labelled with what the two pickers show for the chosen rows. The second
        // expected string gained the grouped marker with the F2 ruling of fix wave 3: index 1 is
        // the combination whose telescope folds two aliases, and the ruling is that every
        // user-visible place shows the same text the picker shows.
        string[] shown =
        [
            $"{TelescopeA} + {CameraA}",
            $"{TelescopeB} + {CameraB} {SharedFilterViewModel.GroupedMarker}",
        ];
        Assert.Equal(shown, Assert.IsType<Axis>(tab.Chart.XAxes[0]).Labels!);
        Assert.Equal(shown, tab.Chart.GroupNames);

        // One stats card per group, each labelled with its own group's name, in pixels.
        Assert.Equal(shown[0], tab.CardA.Label);
        Assert.Equal(shown[1], tab.CardB.Label);
        Assert.Equal("2.50 px", tab.CardA.Median);
        Assert.Equal("3.50 px", tab.CardB.Median);
    }

    // ---- finding F2 of the launched look: the persisted key reached the screen in five places ---

    // The look saw "TestScope|||TestCam" in the box plot's two category labels, the verdict and
    // both card headings. The query ECHOES the two names it is given, which are the wire values, so
    // every case below hands the tab a result whose names are those wire values, exactly as the
    // shipped query answers, and reads what the tab draws. Every one is red against the tab as the
    // look saw it, where the echoed key was printed, and red again against any tab that split or
    // trimmed the key instead of resolving the chosen row's own label.
    //
    // Index 1 is the combination whose telescope folds two aliases, so its display label carries
    // the page's grouped marker, the same text the picker shows for that row.

    private static CompareResult Echoed(string keyA, string keyB)
        => Result(nameA: keyA, nameB: keyB);

    private static string LabelA => $"{TelescopeA} + {CameraA}";

    private static string LabelB => $"{TelescopeB} + {CameraB} {SharedFilterViewModel.GroupedMarker}";

    [Fact]
    public async Task TheBoxPlotsTwoCategoryLabels_ShowTheChosenRowsOwnLabel_AndNeverTheKey()
    {
        var tab = await Wired(query: (_, _, groupA, groupB, _, _) => Echoed(groupA, groupB));

        await Chosen(tab);

        Assert.Equal([LabelA, LabelB], tab.Chart.GroupNames);
        Assert.Equal([LabelA, LabelB], Assert.IsType<Axis>(tab.Chart.XAxes[0]).Labels!);
        Assert.All(
            tab.Chart.GroupNames,
            name => Assert.DoesNotContain(CompareGroups.EquipmentSeparator, name, StringComparison.Ordinal));
    }

    [Fact]
    public async Task BothCardHeadings_ShowTheChosenRowsOwnLabel_AndNeverTheKey()
    {
        var tab = await Wired(query: (_, _, groupA, groupB, _, _) => Echoed(groupA, groupB));

        await Chosen(tab);

        Assert.Equal(LabelA, tab.CardA.Label);
        Assert.Equal(LabelB, tab.CardB.Label);
        Assert.True(tab.CardA.HasLabel);
        Assert.True(tab.CardB.HasLabel);
    }

    [Fact]
    public async Task TheVerdict_NamesBothGroupsByTheirOwnLabel_AndNeverTheKey()
    {
        var tab = await Wired(query: (_, _, groupA, groupB, _, _) => Echoed(groupA, groupB));

        await Chosen(tab);

        Assert.Contains(LabelA, tab.Verdict, StringComparison.Ordinal);
        Assert.Contains(LabelB, tab.Verdict, StringComparison.Ordinal);
        Assert.DoesNotContain(CompareGroups.EquipmentSeparator, tab.Verdict, StringComparison.Ordinal);
    }

    // The three too-few sentences are the fifth place, and their three cases are the S7 ones
    // above: each hands the query's echo back and reads the chosen row's label out of the
    // sentence, so there is no fourth copy of them here.

    // The marker is the picker's own rule and not a second spelling: a row that folds no alias
    // carries none. Red against a tab that appends the marker to every label or to none.
    [Fact]
    public async Task OnlyAGroupedRowCarriesTheMarker()
    {
        var tab = await Wired();

        Assert.Equal($"{TelescopeA} + {CameraA}", tab.GroupChoices[0].Display);
        Assert.Equal(
            $"{TelescopeB} + {CameraB} {SharedFilterViewModel.GroupedMarker}",
            tab.GroupChoices[1].Display);
        Assert.DoesNotContain(
            SharedFilterViewModel.GroupedMarker, tab.GroupChoices[1].Value, StringComparison.Ordinal);

        tab.Mode = CompareMode.Filter;

        Assert.Equal(["Ha", "OIII"], tab.GroupChoices.Select(choice => choice.Display));
    }

    // The parity condition on the rebuilt sentence: the tab's is the query's with ONLY the two
    // names different, on every branch. The three real scenarios are the user's own library,
    // through docs/superpowers/work/phase17/realdata/oracle.json (compare_two_rigs_hfr, the
    // arcsecond branch; compare_two_rigs_fwhm, the pixel branch; compare_ha_oiii_hfr, the
    // arcsecond branch in filter mode), each with the oracle's own pct_u2_rounded as the figure
    // user ruling U2 requires and the web's own figure refused. The fourth is the equal-medians
    // branch, which no real scenario reaches.
    [Theory]
    [InlineData(1.772d, 3.997d, 1.7913d, 1.3607d, 308, 108, "has 56% lower median (arcsec) than")]
    [InlineData(null, null, 3.301721d, 6.978465d, 301, 108, "has 53% lower median than")]
    [InlineData(1.944d, 3.879d, 1.67635d, 1.4199d, 54, 79, "has 50% lower median (arcsec) than")]
    [InlineData(null, null, 3d, 3d, 10, 10, "Both groups have identical median values")]
    public async Task TheRebuiltVerdict_IsTheQuerysSentenceWithOnlyTheTwoNamesSubstituted(
        double? arcsecA, double? arcsecB,
        double medianA, double medianB,
        int countA, int countB,
        string fragment)
    {
        string? keyA = null;
        string? keyB = null;
        CompareResult? answered = null;
        var tab = await Wired(query: (_, _, groupA, groupB, _, _) =>
        {
            keyA = groupA;
            keyB = groupB;
            answered = AsTheQueryBuildsIt(groupA, groupB, arcsecA, arcsecB, medianA, medianB, countA, countB);
            return answered;
        });

        await Chosen(tab);

        // The substitution is done HERE and never in the tab, which resolves each name from the
        // chosen row: a canonical telescope or camera name may hold any text, so a substitution on
        // the built sentence is not a fix, only a measurement of one.
        var expected = answered!.Verdict!.Replace(keyA!, LabelA, StringComparison.Ordinal)
            .Replace(keyB!, LabelB, StringComparison.Ordinal);

        Assert.Equal(expected, tab.Verdict);
        Assert.Contains(fragment, tab.Verdict, StringComparison.Ordinal);
        Assert.DoesNotContain(CompareGroups.EquipmentSeparator, tab.Verdict, StringComparison.Ordinal);
    }

    // AnalysisQuery.Compare's own two calls, with the SAME locals reaching CompareVerdict and the
    // published record: the two rounded arcsecond medians on the branch that also passes the
    // arcsecond unit, the two summaries' medians on the other, and both counts from those two
    // summaries either way, never from the result's raw CountA and CountB.
    private static CompareResult AsTheQueryBuildsIt(
        string keyA, string keyB,
        double? arcsecA, double? arcsecB,
        double medianA, double medianB,
        int countA, int countB)
    {
        var statsA = new SummaryStats(countA, 0d, 9d, medianA, medianA, 1d);
        var statsB = new SummaryStats(countB, 0d, 9d, medianB, medianB, 1d);

        var verdict = arcsecA is { } a && arcsecB is { } b
            ? GalactiLog.Core.Metrics.Analysis.CompareVerdict(keyA, keyB, a, b, countA, countB, " (arcsec)")
            : GalactiLog.Core.Metrics.Analysis.CompareVerdict(keyA, keyB, medianA, medianB, countA, countB, string.Empty);

        return new CompareResult(
            CompareState.Ok,
            keyA,
            keyB,
            countA,
            countB,
            new CompareGroup(keyA, Box(keyA), statsA),
            new CompareGroup(keyB, Box(keyB), statsB),
            verdict,
            true,
            arcsecA,
            arcsecB);
    }

    // The one shape in which a chosen row can be gone by the time a result lands:
    // AnalysisTabViewModel.Refresh returns through PublishCannotQuery WITHOUT bumping its
    // generation, so the in-flight result still publishes and draws. The names are taken when the
    // query goes out, so the drawn result's figures and its two names come from one choice. Red
    // against a tab that re-reads the chosen rows when the result lands, which finds none and
    // draws two empty names.
    [Fact]
    public async Task AGroupWithdrawnWhileItsQueryIsInFlight_IsDrawnWithTheNameItWasQueriedUnder()
    {
        var bar = await Bar();
        var tab = Tab(
            query: (_, _, groupA, groupB, _, _) =>
            {
                // The bar's answer to a scan that no longer holds the second rig, applied to the
                // instance the tab follows, while this query is running.
                bar.EquipmentChoices.RemoveAt(bar.EquipmentChoices.Count - 1);
                return Echoed(groupA, groupB);
            },
            equipment: bar.EquipmentChoices,
            filters: bar.FilterChoices);

        await Chosen(tab);

        Assert.Null(tab.SelectedGroupB);
        Assert.Equal(string.Empty, tab.GroupB);
        Assert.Equal(LabelA, tab.CardA.Label);
        Assert.Equal(LabelB, tab.CardB.Label);
        Assert.Equal([LabelA, LabelB], tab.Chart.GroupNames);
        Assert.Contains(LabelB, tab.Verdict, StringComparison.Ordinal);
    }

    // ---- case 20d, the empty-state axis rule ---------------------------------------------------

    // This chart is the most exposed of the three, because it renders nothing until two groups are
    // chosen and again in all four S7 short states. Red against a PublishEmpty that publishes an
    // empty axis array: rc5.4 throws "XAxes and YAxes must contain at least one element".
    [Fact]
    public async Task TheChartPublishesOneAxisPerSideBeforeAnythingIsChosen()
    {
        var tab = await Wired();

        Assert.Empty(tab.Chart.Series);
        Assert.Single(tab.Chart.XAxes);
        Assert.Single(tab.Chart.YAxes);
        Assert.True(tab.Chart.IsEmpty);

        await Chosen(tab);
        tab.Mode = CompareMode.Filter;

        Assert.Single(tab.Chart.XAxes);
        Assert.Single(tab.Chart.YAxes);
    }

    // ---- the metric picker --------------------------------------------------------------------

    [Fact]
    public async Task TheMetricPicker_OffersExactlyTheTenYMetricsAndSendsTheChosenOne()
    {
        AnalysisMetric? seen = null;
        var tab = await Wired(query: (metric, _, _, _, _, _) => { seen = metric; return Result(); });

        Assert.Equal(AnalysisMetrics.Y, CompareTabViewModel.Metrics);
        Assert.Equal(
            AnalysisMetrics.Y.Select(AnalysisMetricLabels.Label).ToList(),
            CompareTabViewModel.MetricChoices.Select(choice => choice.Label).ToList());

        await Chosen(tab);
        tab.SelectedMetricChoice = CompareTabViewModel.MetricChoices[6];
        await tab.PendingLoad!;

        Assert.Equal(AnalysisMetric.DetectedStars, tab.Metric);
        Assert.Equal(AnalysisMetric.DetectedStars, seen);
    }

    // ---- the view-assigned group source --------------------------------------------------------

    // The bar's two collections are created once and filled in place by its own background read,
    // so a tab attached before that read lands must still see the lists when they arrive.
    // The bar one list read is ASYNCHRONOUS: it starts in the bar constructor and lands after the
    // page, and therefore this tab, has been built. The constructor takes the bar collection
    // instances rather than snapshots of them, so the rows reach both pickers when they arrive.
    // Red against a constructor that copies the two lists: the pickers stay empty for the life of
    // the page and the tab can never become queryable.
    [Fact]
    public void ThePickersFollowTheBarsOwnLists_WhichFillAfterTheTabIsBuilt()
    {
        // Built first, against the two collections while they still hold only the sentinel rows,
        // which both pickers exclude.
        ObservableCollection<EquipmentChoice> equipment = [SharedFilterViewModel.AllEquipment];
        ObservableCollection<string> filters = [SharedFilterViewModel.AllFiltersLabel];
        var tab = Tab(equipment: equipment, filters: filters);

        Assert.Empty(tab.GroupChoices);

        // Exactly what SharedFilterViewModel.Publish does when its background read lands: it
        // appends to the instances it created, in place.
        equipment.Add(new EquipmentChoice($"{TelescopeA} + {CameraA}", false, TelescopeA, CameraA));
        equipment.Add(new EquipmentChoice($"{TelescopeB} + {CameraB}", true, TelescopeB, CameraB));
        filters.Add("Ha");

        Assert.Equal(2, tab.GroupChoices.Count);
        Assert.Equal(TelescopeA + CompareGroups.EquipmentSeparator + CameraA, tab.GroupChoices[0].Value);

        tab.Mode = CompareMode.Filter;
        Assert.Equal(["Ha"], tab.GroupChoices.Select(choice => choice.Value));
    }

    // The same thing against the real bar, whose read is genuinely off-thread: the tab is
    // constructed from the bar collections before that read is awaited.
    [Fact]
    public async Task ThePickersFillFromTheRealBar_WhoseListReadIsAsynchronous()
    {
        var bar = new SharedFilterViewModel(
            () => [new EquipmentCombination(TelescopeA, CameraA, false)],
            () => ["Ha"],
            post: action => action());

        var tab = Tab(equipment: bar.EquipmentChoices, filters: bar.FilterChoices);

        // Below the tab, not above it, which is what this case is about: the read must land after
        // the tab has been built. The bar's constructor no longer starts it, so this is the one
        // line that decides the order, and the order it gives is the one the page itself gives.
        bar.Load();
        await bar.PendingLoad;

        Assert.Equal(
            TelescopeA + CompareGroups.EquipmentSeparator + CameraA,
            Assert.Single(tab.GroupChoices).Value);
    }

    // Kept although the method the lesson lived on is gone: the option list both pickers bind is
    // updated IN PLACE and never cleared, because a clear under a live
    // two-way SelectedItem makes the control write null back through it. Red against a
    // Clear-and-refill: the rebuild raises a Reset although nothing about the equipment rows
    // changed.
    [Fact]
    public async Task ARebuildThatChangesNothing_DoesNotTouchTheBoundOptionList()
    {
        var bar = await Bar();
        var tab = Tab(equipment: bar.EquipmentChoices, filters: bar.FilterChoices);

        tab.SelectedGroupA = tab.GroupChoices[0];
        tab.SelectedGroupB = tab.GroupChoices[1];
        await tab.PendingLoad!;

        var events = 0;
        var held = tab.GroupChoices[0];
        ((INotifyCollectionChanged)tab.GroupChoices).CollectionChanged += (_, _) => events++;

        // A list change that leaves the equipment rows exactly as they were: the filter list is
        // the one that moved, and the current mode does not read it.
        bar.FilterChoices.Add("SII");

        Assert.Equal(0, events);
        Assert.Same(held, tab.GroupChoices[0]);
        Assert.Equal(AnalysisTabState.Ready, tab.State);
        Assert.NotNull(tab.SelectedGroupA);
        Assert.NotNull(tab.SelectedGroupB);
    }

    // ---- both group pickers across a bar re-read -------------------------------------------------

    /// <summary>
    /// Waits out the one further query a re-read can cause, which the base runs on the pool.
    /// </summary>
    /// <remarks>
    /// The five cases below assert the tab's STATE after a re-read, and a re-read whose
    /// intermediate list does not offer a chosen row leaves one query behind (the wave 2 report's
    /// section 8 says why). Awaiting the bar alone does not await that, so those assertions read
    /// <c>Refreshing</c> whenever the pool is busy enough to finish second. The full Compare run of
    /// fix wave 3 caught it, one case in one round and a different case in the next; the three
    /// alone, with nothing competing, passed five rounds of five. It is the cases that raced and
    /// not the tab.
    /// </remarks>
    private static Task Settled(CompareTabViewModel tab) => AnalysisSettle.Tab(tab);

    /// <summary>
    /// A wired tab over MUTABLE source lists, so a case can move a row the way a scan or an alias
    /// save moves one and apply it through the bar's own <c>Reload</c>, which is the route the page
    /// takes. Three rigs and three filters, so a case can choose two groups and leave the first row
    /// unchosen.
    /// </summary>
    private async Task<(CompareTabViewModel Tab, SharedFilterViewModel Bar, List<EquipmentCombination> Equipment, List<string> Filters, List<(string A, string B)> Queries)> Movable()
    {
        var equipment = new List<EquipmentCombination>
        {
            new(TelescopeA, CameraA, false),
            new(TelescopeB, CameraB, false),
            new(TelescopeC, CameraC, false),
        };
        var filters = new List<string> { "Ha", "OIII", "SII" };
        var bar = new SharedFilterViewModel(() => [.. equipment], () => [.. filters], post: action => action());

        bar.Load();
        await bar.PendingLoad;

        // The PAIR each query names, not merely a count: the question this defect asks is whether
        // the tab ever queries for a group the reader did not choose, which a count cannot answer.
        var queries = new List<(string A, string B)>();
        var tab = Tab(
            query: (_, _, groupA, groupB, _, _) =>
            {
                queries.Add((groupA, groupB));
                return Result();
            },
            equipment: bar.EquipmentChoices,
            filters: bar.FilterChoices);

        return (tab, bar, equipment, filters, queries);
    }

    /// <summary>
    /// The shipped <c>ComboBox</c>'s own measured behaviour, so a windowless case can see the half
    /// of this defect that only a bound control produces.
    /// </summary>
    /// <remarks>
    /// Measured, not assumed:
    /// <c>CompareTabViewTests.AReReadThatReordersTheRigs_LeavesBothGroupPickersWhereTheReaderPutThem</c>
    /// shows the real page and reads the two real pickers. When the row at a picker's selected index
    /// is REPLACED, Avalonia writes null back through the two-way binding, synchronously, inside the
    /// rebuild; it does not write the replacement back and it does not leave the selection alone.
    /// That is what this reproduces. A case that does not attach it is measuring the view-model
    /// alone, which for a reorder or an insert is the half that cannot fail.
    /// </remarks>
    private static void LikeTheBoundPicker(CompareTabViewModel tab)
        => ((INotifyCollectionChanged)tab.GroupChoices).CollectionChanged += (_, args) =>
        {
            if (args.OldItems is null)
            {
                return;
            }

            foreach (var row in args.OldItems.OfType<CompareGroupChoice>())
            {
                if (ReferenceEquals(row, tab.SelectedGroupA))
                {
                    tab.SelectedGroupA = null;
                }

                if (ReferenceEquals(row, tab.SelectedGroupB))
                {
                    tab.SelectedGroupB = null;
                }
            }
        };

    /// <summary>
    /// A re-read that answers the same rigs in a different order leaves both of the reader's groups
    /// where they put them and queries for no other pair.
    /// </summary>
    /// <remarks>
    /// The equipment list is ordered by frame count first, so a scan that catalogues frames for one
    /// rig carries it past another with no row added and none removed. The rebuild writes in place,
    /// so the reader's own row is REPLACED at its own index, and
    /// <see cref="LikeTheBoundPicker"/> is what makes this case see the consequence: without it the
    /// view-model alone keeps a row that is still a member of the list wherever it now sits, and the
    /// case cannot fail. Seen red against the rebuild that reassigned nothing: both groups read
    /// empty, the tab leaves <see cref="AnalysisTabState.Ready"/> and shows
    /// <see cref="CompareTabViewModel.ChooseGroupsText"/>.
    /// </remarks>
    [Fact]
    public async Task AReReadThatReordersTheRigs_KeepsBothGroups_AndQueriesForNoOtherPair()
    {
        var (tab, bar, equipment, _, queries) = await Movable();
        await Chosen(tab, indexA: 1, indexB: 2);
        LikeTheBoundPicker(tab);

        var groupA = tab.GroupA;
        var groupB = tab.GroupB;
        var before = queries.Count;

        // The rotation a scan produces: every row lands at a new index, the two chosen among them.
        equipment.Insert(0, equipment[^1]);
        equipment.RemoveAt(equipment.Count - 1);
        bar.Reload();
        await bar.PendingLoad;
        await Settled(tab);

        Assert.Equal(
            new[] { $"{TelescopeC} + {CameraC}", $"{TelescopeA} + {CameraA}", $"{TelescopeB} + {CameraB}" },
            tab.GroupChoices.Select(choice => choice.Label));

        Assert.Equal(groupA, tab.GroupA);
        Assert.Equal(groupB, tab.GroupB);
        Assert.Equal(groupA, tab.SelectedGroupA?.Value);
        Assert.Equal(groupB, tab.SelectedGroupB?.Value);

        // The rows the pickers now hold are the LIVE ones, at their new indices, and not the
        // withdrawn instances.
        Assert.Same(tab.GroupChoices[2], tab.SelectedGroupA);
        Assert.Same(tab.GroupChoices[0], tab.SelectedGroupB);

        Assert.Equal(AnalysisTabState.Ready, tab.State);
        Assert.Equal(string.Empty, tab.StatusLine);

        // Nothing was queried on a HALF-APPLIED list. The bar writes its own answer to the two
        // collections row by row, so this tab rebuilds once per moved row, over a list in which the
        // reader's own row can be missing; every query the re-read caused still names the reader's
        // own two groups, and none names a group nobody chose.
        Assert.All(queries.Skip(before), pair => Assert.Equal((groupA, groupB), pair));
    }

    // An alias save that folds a second raw name into one of the chosen rigs flips Grouped alone
    // and leaves both canonical names standing, so the wire value does not move and neither group
    // does. CompareGroupChoice is a record, so the flip still REPLACES the row and the control still
    // writes null back: matching on the whole record rather than on the wire value would withdraw
    // the reader's own group over a marker. Red against the rebuild that reassigned nothing.
    [Fact]
    public async Task AGroupedFlipOnAChosenRig_KeepsBothGroups()
    {
        var (tab, bar, equipment, _, queries) = await Movable();
        await Chosen(tab, indexA: 1, indexB: 2);
        LikeTheBoundPicker(tab);

        var groupA = tab.GroupA;
        var groupB = tab.GroupB;
        var before = queries.Count;

        equipment[1] = equipment[1] with { Grouped = true };
        bar.Reload();
        await bar.PendingLoad;
        await Settled(tab);

        Assert.True(tab.GroupChoices[1].Grouped);
        Assert.Equal(groupA, tab.GroupA);
        Assert.Equal(groupB, tab.GroupB);
        Assert.Same(tab.GroupChoices[1], tab.SelectedGroupA);
        Assert.Equal(AnalysisTabState.Ready, tab.State);

        // Nothing at all was requeried: the marker never leaves the wire value, so no group moved.
        Assert.Equal(before, queries.Count);
    }

    // A rig added by a scan sorts ahead of both chosen rows, which the rebuild applies as a chain
    // of replaces from index 0 down plus one append: every later index, the two chosen among them,
    // is written. Red against the rebuild that reassigned nothing.
    [Fact]
    public async Task ARigInsertedAheadOfBothGroups_KeepsThem()
    {
        var (tab, bar, equipment, _, queries) = await Movable();
        await Chosen(tab, indexA: 1, indexB: 2);
        LikeTheBoundPicker(tab);

        var groupA = tab.GroupA;
        var groupB = tab.GroupB;
        var before = queries.Count;

        equipment.Insert(0, new EquipmentCombination("Newton 200", "ASI1600MM", false));
        bar.Reload();
        await bar.PendingLoad;
        await Settled(tab);

        Assert.Equal(4, tab.GroupChoices.Count);
        Assert.Equal(groupA, tab.GroupA);
        Assert.Equal(groupB, tab.GroupB);
        Assert.Same(tab.GroupChoices[2], tab.SelectedGroupA);
        Assert.Same(tab.GroupChoices[3], tab.SelectedGroupB);
        Assert.Equal(AnalysisTabState.Ready, tab.State);
        Assert.All(queries.Skip(before), pair => Assert.Equal((groupA, groupB), pair));
    }

    // A group whose identity is truly gone, the rig having left the library or an alias having
    // folded its name away, clears to unchosen: there is no row left to compare and the tab says
    // so. The OTHER group is kept, because nothing happened to it. Red against the rebuild that
    // reassigned nothing, which lost BOTH.
    [Fact]
    public async Task AVanishedRig_ClearsOnlyThatGroup_KeepsTheOther_AndShowsTheCannotQuerySentence()
    {
        var (tab, bar, equipment, _, _) = await Movable();
        await Chosen(tab, indexA: 1, indexB: 2);
        LikeTheBoundPicker(tab);

        var groupA = tab.GroupA;

        // The rig group B names leaves the library.
        equipment.RemoveAt(2);
        bar.Reload();
        await bar.PendingLoad;
        await Settled(tab);

        Assert.Equal(groupA, tab.GroupA);
        Assert.NotNull(tab.SelectedGroupA);
        Assert.Same(tab.GroupChoices[1], tab.SelectedGroupA);

        Assert.Null(tab.SelectedGroupB);
        Assert.Equal(string.Empty, tab.GroupB);

        Assert.Equal(AnalysisTabState.Empty, tab.State);
        Assert.Equal(CompareTabViewModel.ChooseGroupsText, tab.StatusLine);
    }

    // The filter list cannot reorder by itself, being sorted on the value it holds, but a new
    // filter name sorts into the MIDDLE and shifts every later row by one, which reaches both
    // chosen indices as a replace. Red against the rebuild that reassigned nothing.
    [Fact]
    public async Task InFilterMode_ANewNameSortingIntoTheMiddle_KeepsBothGroups()
    {
        var (tab, bar, _, filters, queries) = await Movable();
        tab.Mode = CompareMode.Filter;
        await Chosen(tab, indexA: 1, indexB: 2);
        LikeTheBoundPicker(tab);

        Assert.Equal("OIII", tab.GroupA);
        Assert.Equal("SII", tab.GroupB);
        var before = queries.Count;

        // Ordinally ascending, which is the order the filter query answers in.
        filters.Insert(1, "Lum");
        bar.Reload();
        await bar.PendingLoad;
        await Settled(tab);

        Assert.Equal(["Ha", "Lum", "OIII", "SII"], tab.GroupChoices.Select(choice => choice.Value));
        Assert.Equal("OIII", tab.GroupA);
        Assert.Equal("SII", tab.GroupB);
        Assert.Same(tab.GroupChoices[2], tab.SelectedGroupA);
        Assert.Same(tab.GroupChoices[3], tab.SelectedGroupB);
        Assert.Equal(AnalysisTabState.Ready, tab.State);
        Assert.All(queries.Skip(before), pair => Assert.Equal(("OIII", "SII"), pair));
    }

    // ---- the base's reversed-range sentence wins on a first selection --------------------------

    // A Compare tab selected for the FIRST time while the date range is reversed reads the base
    // sentence that names why nothing was queried, not this tab own not-yet-queryable sentence.
    // Red against an unconditional false arm: the reader is told to choose two groups while the
    // reason nothing will run is the date range.
    [Fact]
    public void AFirstSelectionUnderAReversedRange_ShowsTheBasesOwnSentence()
    {
        AnalysisFilter? current = null;
        var tab = Tab(filter: () => current, show: false);

        tab.IsVisible = true;

        Assert.Equal(AnalysisTabState.NotLoaded, tab.State);
        Assert.Equal(AnalysisTabViewModel.ReversedRangeText, tab.StatusLine);
        Assert.NotEqual(CompareTabViewModel.ChooseGroupsText, tab.StatusLine);
        Assert.Null(tab.PendingLoad);

        // The range becomes legal: the load loop publishes a state and this tab sentence takes
        // over, because no group has been chosen yet.
        current = Filter();
        tab.Refresh();

        Assert.Equal(AnalysisTabState.Empty, tab.State);
        Assert.Equal(CompareTabViewModel.ChooseGroupsText, tab.StatusLine);
        Assert.Null(tab.PendingLoad);
    }
}
