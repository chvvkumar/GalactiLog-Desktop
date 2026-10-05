using System.Collections.Specialized;
using GalactiLog.App.ViewModels.Analysis;
using GalactiLog.App.ViewModels.Stats;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels.Analysis;

/// <summary>Spec 12.14's shared filter bar: its two lists, its segment, its two dates and the
/// reversed range rule that stops every tab querying.</summary>
public class SharedFilterTests
{
    // Awaited and never blocked (dispatch-common rule 5). The bound is the
    // Task's own, which is why every case below is an async Task: a helper that blocks on a load is
    // the shape xUnit1031 exists for, and it is not a warning here only because this is a helper
    // rather than a test method.
    private static async Task<SharedFilterViewModel> Bar(
        IReadOnlyList<EquipmentCombination>? equipment = null,
        IReadOnlyList<string>? filters = null,
        AnalysisGranularity granularity = AnalysisGranularity.Frame,
        Action<AnalysisGranularity>? writeGranularity = null)
    {
        var bar = new SharedFilterViewModel(
            () => equipment ?? [new EquipmentCombination("RC8", "ASI2600MM", false)],
            () => filters ?? ["Ha", "OIII"],
            granularity,
            action => action(),
            writeGranularity);

        // The bar built alone starts its own read. A page built through AnalysisViewModel does not
        // reach this helper: it calls Load as the last statement of its own constructor.
        bar.Load();
        await bar.PendingLoad;
        return bar;
    }

    private static DateTimeOffset On(int year, int month, int day)
        => new(new DateTime(year, month, day), TimeSpan.Zero);

    [Fact]
    public async Task TheEquipmentList_IsAllEquipmentPlusOneRowPerCombination_WithTheGroupedMarker()
    {
        var bar = await Bar(
        [
            new EquipmentCombination("RC8", "ASI2600MM", false),
            new EquipmentCombination("Askar FMA180", "ASI533MC", true),
        ]);

        Assert.Equal(
            new[] { "All equipment", "RC8 + ASI2600MM", "Askar FMA180 + ASI533MC" },
            bar.EquipmentChoices.Select(choice => choice.Label));
        Assert.Equal(new[] { false, false, true }, bar.EquipmentChoices.Select(choice => choice.Grouped));

        // The default is All equipment, which applies no equipment predicate at all.
        Assert.Same(SharedFilterViewModel.AllEquipment, bar.SelectedEquipment);
        Assert.Null(bar.Current!.Telescope);
        Assert.Null(bar.Current!.Camera);
    }

    // ---- the read is started by Load and by nothing else ----------------------------------------

    // Red against the read started in the bar's own constructor, on the first two assertions: the
    // counter reads 1 before Load has been called and the two lists are no longer sentinel-only.
    //
    // This is the structural half of the race spine B saw once, in which
    // AnalysisPageTests.TheStoredGranularityAndMetrics_SeedTheBarAndTheCorrelationTab threw
    // "Collection was modified" out of CompareTabViewModel's constructor: the Compare tab
    // enumerates these two collections while it is being built, and a read already in flight can
    // append to them from its own thread-pool thread. The read now starts when the owner says so,
    // so no publish can observe a half-built page whatever the post seam does.
    //
    // Every figure is taken behind an await of PendingLoad, which is what makes this deterministic
    // in BOTH directions rather than a race against a thread-pool thread: under the rule the first
    // await is the completed sentinel and returns at once, and against a read started in the
    // constructor it joins that read, so the counter below reads 1 and the two lists are filled.
    [Fact]
    public async Task TheBarReadsNothingUntilLoad_AndASecondLoadReadsNothingMore()
    {
        var reads = 0;
        var bar = new SharedFilterViewModel(
            () =>
            {
                Interlocked.Increment(ref reads);
                return [new EquipmentCombination("RC8", "ASI2600MM", false)];
            },
            () => ["Ha"],
            post: action => action());

        // Non-null and already completed before Load, so every existing await of it still holds.
        Assert.True(bar.PendingLoad.IsCompletedSuccessfully);
        await bar.PendingLoad;

        Assert.Equal(0, Volatile.Read(ref reads));
        Assert.Equal(new[] { "All equipment" }, bar.EquipmentChoices.Select(choice => choice.Label));
        Assert.Equal(new[] { "All filters" }, bar.FilterChoices);

        bar.Load();
        await bar.PendingLoad;

        Assert.Equal(1, Volatile.Read(ref reads));
        Assert.Equal(2, bar.EquipmentChoices.Count);
        Assert.Equal(2, bar.FilterChoices.Count);

        // Idempotent, so an owner that calls it twice neither re-reads nor appends the rows twice.
        bar.Load();
        await bar.PendingLoad;

        Assert.Equal(1, Volatile.Read(ref reads));
        Assert.Equal(2, bar.EquipmentChoices.Count);
        Assert.Equal(2, bar.FilterChoices.Count);
    }

    // ---- the re-read -----------------------------------------------------------------------------

    /// <summary>
    /// Phase review P1-A. The two lists were not merely stale but unrecoverable: <c>Load</c>
    /// returned on its second call and the publish appended, so a rescan with a second rig, an
    /// alias group renamed or a PHD2 profile mapped left both pickers, and the Compare tab's two
    /// group pickers, showing the rows this process first read for the life of the process.
    /// </summary>
    /// <remarks>
    /// Seen red twice against the shipped publish, restored by hash afterwards: with the append
    /// back in place the equipment list reads five rows with "RC8 + ASI2600MM" twice over instead
    /// of the three this case names, and with the selection reapplication removed the withdrawn
    /// filter is still the selection and <c>Current.FilterUsed</c> still names it.
    /// </remarks>
    [Fact]
    public async Task Reload_ReReadsBothLists_InPlace_KeepingASurvivingSelectionAndDroppingAWithdrawnOne()
    {
        var equipment = new List<EquipmentCombination> { new("RC8", "ASI2600MM", false) };
        var filters = new List<string> { "Ha", "OIII" };
        var bar = new SharedFilterViewModel(
            () => [.. equipment],
            () => [.. filters],
            post: action => action());
        var equipmentInstance = bar.EquipmentChoices;
        var filterInstance = bar.FilterChoices;

        bar.Load();
        await bar.PendingLoad;

        bar.SelectedEquipment = bar.EquipmentChoices[1];
        bar.SelectedFilter = "OIII";

        // The scan that catalogues a second optical train, and the filter alias edit that folds
        // OIII away: one row arrives, one row leaves.
        equipment.Add(new EquipmentCombination("Askar FMA180", "ASI533MC", true));
        filters.Remove("OIII");
        bar.Reload();
        await bar.PendingLoad;

        // The instances are the ones the constructor built, so no bound ItemsSource is replaced.
        Assert.Same(equipmentInstance, bar.EquipmentChoices);
        Assert.Same(filterInstance, bar.FilterChoices);
        Assert.Equal(
            new[] { "All equipment", "RC8 + ASI2600MM", "Askar FMA180 + ASI533MC" },
            bar.EquipmentChoices.Select(choice => choice.Label));
        Assert.Equal(new[] { "All filters", "Ha" }, bar.FilterChoices);

        // The equipment row the reader chose survived the re-read, so it is still the selection.
        Assert.Equal("RC8 + ASI2600MM", bar.SelectedEquipment.Label);
        Assert.Equal("RC8", bar.Current!.Telescope);

        // The filter they chose did not, so the bar falls back to the "All" row rather than
        // querying for a name no frame carries any more.
        Assert.Equal("All filters", bar.SelectedFilter);
        Assert.Null(bar.Current!.FilterUsed);
    }

    /// <summary>
    /// Phase review P1-A's storm hazard, the shape <c>StatisticsViewModel</c> already answers:
    /// <c>GeneralChanged</c> is raised on every general save, so a burst of notifications arriving
    /// while a read is in flight must cost exactly one further read rather than one per
    /// notification.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The further read is owed rather than optional, which is why the answer is coalescing and not
    /// dropping: the read in flight may have taken its answer from the memo the notification's own
    /// handler had not yet dropped, so the bar would settle on the lists the save invalidated.
    /// </para>
    /// <para>
    /// Seen red against <c>Reload</c> starting a read unconditionally: the counter reads 4 where
    /// this case names 3.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ABurstOfReloadsDuringARead_CostsExactlyOneFurtherRead()
    {
        using var gate = new ManualResetEventSlim(false);
        var reads = 0;
        var bar = new SharedFilterViewModel(
            () =>
            {
                // The second read is the one this case parks, so the burst below lands while a read
                // really is in flight rather than between two finished ones.
                if (Interlocked.Increment(ref reads) == 2)
                {
                    Assert.True(gate.Wait(TimeSpan.FromSeconds(30)), "the parked read was never released");
                }

                return [new EquipmentCombination("RC8", "ASI2600MM", false)];
            },
            () => ["Ha"],
            post: action => action());

        bar.Load();
        await bar.PendingLoad;
        Assert.Equal(1, Volatile.Read(ref reads));

        // The first reload starts the read this case parks; the next three arrive while it is in
        // flight and are absorbed into the one further read it owes.
        bar.Reload();
        bar.Reload();
        bar.Reload();
        bar.Reload();

        gate.Set();

        // Three rounds, because the read the coalescing owes is started inside the parked read's
        // own publish and so does not exist until that one has finished.
        for (var round = 0; round < 3; round++)
        {
            await bar.PendingLoad;
        }

        Assert.Equal(3, Volatile.Read(ref reads));
        Assert.Equal(2, bar.EquipmentChoices.Count);
    }

    /// <summary>
    /// The failure arm finishes the read like any other, so a database error cannot leave the bar
    /// refusing every later reload for the life of the page.
    /// </summary>
    /// <remarks>Seen red against a failure arm that returns without draining: the second reload
    /// reads nothing, the counter stays at 1 and both lists keep their sentinel rows alone.
    /// </remarks>
    [Fact]
    public async Task AFailedRead_LeavesTheBarReloadable()
    {
        var reads = 0;
        var bar = new SharedFilterViewModel(
            () =>
            {
                if (Interlocked.Increment(ref reads) == 1)
                {
                    throw new InvalidOperationException("the database is not readable");
                }

                return [new EquipmentCombination("RC8", "ASI2600MM", false)];
            },
            () => ["Ha"],
            post: action => action());

        bar.Load();
        await bar.PendingLoad;

        // Spec 12.14's States table gives no page state for a failed bar load: the bar keeps its
        // two "All" rows and the failure is logged.
        Assert.Equal(new[] { "All equipment" }, bar.EquipmentChoices.Select(choice => choice.Label));

        bar.Reload();
        await bar.PendingLoad;

        Assert.Equal(2, Volatile.Read(ref reads));
        Assert.Equal(2, bar.EquipmentChoices.Count);
        Assert.Equal(2, bar.FilterChoices.Count);
    }

    // ---- the selection across a re-read ----------------------------------------------------------

    // A bar whose two answers a case can rewrite between reads, with the reader's choices already
    // made. Every case below reads through it, so the shape of a re-read is written once.
    private static async Task<(SharedFilterViewModel Bar, List<EquipmentCombination> Equipment, List<string> Filters, Func<int> Raised)> Chosen(
        string telescope = "RC8",
        string camera = "ASI2600MM",
        string filter = "Ha")
    {
        var equipment = new List<EquipmentCombination>
        {
            new("RC8", "ASI2600MM", false),
            new("Askar FMA180", "ASI533MC", false),
        };
        var filters = new List<string> { "Ha", "OIII" };
        var bar = new SharedFilterViewModel(() => [.. equipment], () => [.. filters], post: action => action());

        bar.Load();
        await bar.PendingLoad;

        bar.SelectedEquipment = bar.EquipmentChoices.Single(
            choice => choice.Telescope == telescope && choice.Camera == camera);
        bar.SelectedFilter = filter;

        var raised = 0;
        bar.Changed += (_, _) => raised++;
        return (bar, equipment, filters, () => raised);
    }

    /// <summary>
    /// The shipped <c>ComboBox</c>'s own measured behaviour, so a windowless case can see the half
    /// of the selection defect that only a bound control produces.
    /// </summary>
    /// <remarks>
    /// Measured, not assumed:
    /// <c>AnalysisViewTests.AReReadUnderTheBoundPicker_LeavesTheChosenCombinationWhereTheReaderPutIt</c>
    /// shows the real page and reads the real picker. When the row at the selected index is
    /// REPLACED, Avalonia writes NULL back through the two-way binding, synchronously, inside the
    /// publish; it does not write the replacement back and it does not leave the selection alone.
    /// That is what this reproduces. A case that does not attach it is measuring the view-model
    /// alone, which for a reorder or an insert is the half that cannot fail.
    /// </remarks>
    private static void LikeTheBoundPicker(SharedFilterViewModel bar)
    {
        bar.EquipmentChoices.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Replace
                && args.OldItems?[0] is EquipmentChoice row
                && row == bar.SelectedEquipment)
            {
                bar.SelectedEquipment = null!;
            }
        };

        bar.FilterChoices.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Replace
                && args.OldItems?[0] is string row
                && string.Equals(row, bar.SelectedFilter, StringComparison.Ordinal))
            {
                bar.SelectedFilter = null!;
            }
        };
    }

    /// <summary>
    /// The equipment picker across a re-read. A re-read that answers the same pairs in a
    /// different order must leave the reader on the rig they chose and tell the page nothing.
    /// </summary>
    /// <remarks>
    /// The equipment list is sorted on frame count first (<c>StatsQuery.Performance</c>), so a scan
    /// that catalogues frames for one rig carries it past another with no row added and none
    /// removed. <c>Apply</c> writes in place, so the reader's row is REPLACED at its own index, and
    /// <see cref="LikeTheBoundPicker"/> is what makes this case see the consequence: without it the
    /// view-model alone keeps a row that is still a member of the list wherever it now sits, and the
    /// case cannot fail. Seen red against the membership test the shipped <c>Publish</c> used: the
    /// selection reads "All equipment" and one <c>Changed</c> has been raised, because the control's
    /// null write-back lands inside the publish and every membership test passes over it.
    /// </remarks>
    [Fact]
    public async Task AReReadThatReordersTheRows_KeepsTheChosenRig_AndRaisesNothing()
    {
        var (bar, equipment, _, raised) = await Chosen();
        LikeTheBoundPicker(bar);

        equipment.Reverse();
        bar.Reload();
        await bar.PendingLoad;

        Assert.Equal(
            new[] { "All equipment", "Askar FMA180 + ASI533MC", "RC8 + ASI2600MM" },
            bar.EquipmentChoices.Select(choice => choice.Label));
        Assert.Equal("RC8 + ASI2600MM", bar.SelectedEquipment.Label);
        Assert.Equal("RC8", bar.Current!.Telescope);
        Assert.Equal("ASI2600MM", bar.Current!.Camera);

        // Nothing the reader did, so no tab is marked stale and none requeries.
        Assert.Equal(0, raised());
    }

    /// <summary>
    /// An alias save that folds a second raw name into one of
    /// the chosen pair's canonical names flips <c>Grouped</c> alone, and must not widen the picker.
    /// </summary>
    /// <remarks><c>EquipmentChoice</c> is a record, so a membership test is value equality over all
    /// four members and the flip withdraws the reader's row while the pair itself still exists.
    /// Seen red against the shipped test: the selection falls back to "All equipment" for no reason
    /// a reader can see.</remarks>
    [Fact]
    public async Task AGroupedFlipOnTheChosenPair_KeepsTheSelection()
    {
        var (bar, equipment, _, raised) = await Chosen();
        LikeTheBoundPicker(bar);

        equipment[0] = new EquipmentCombination("RC8", "ASI2600MM", true);
        bar.Reload();
        await bar.PendingLoad;

        Assert.True(bar.EquipmentChoices[1].Grouped);
        Assert.Equal("RC8 + ASI2600MM", bar.SelectedEquipment.Label);
        Assert.True(bar.SelectedEquipment.Grouped);
        Assert.Equal("RC8", bar.Current!.Telescope);
        Assert.Equal(0, raised());
    }

    /// <summary>The "All" fallback, which the identity reassignment keeps exactly as it was: a pair
    /// no frame carries any more is gone and the bar widens, once.</summary>
    /// <remarks>
    /// Seen red against a publish with no selection reapplication at all, which leaves the bar
    /// querying for a rig the library no longer holds. <see cref="LikeTheBoundPicker"/> is
    /// deliberately NOT attached: the control's null write-back would reach the same answer by
    /// accident and hide whether the fallback arm is there, which is the one thing this case is for.
    /// The binding's own half of the fallback is carried by the view-level case.
    /// </remarks>
    [Fact]
    public async Task AVanishedPair_FallsBackToAllEquipment_AndRaisesChangedOnce()
    {
        var (bar, equipment, _, raised) = await Chosen();

        equipment.RemoveAt(0);
        bar.Reload();
        await bar.PendingLoad;

        Assert.Same(SharedFilterViewModel.AllEquipment, bar.SelectedEquipment);
        Assert.Null(bar.Current!.Telescope);
        Assert.Null(bar.Current!.Camera);

        // Once, not twice: the page marks every tab stale and refreshes the selected one on each.
        Assert.Equal(1, raised());
    }

    /// <summary>A rig catalogued by a scan that sorts ahead of the chosen one shifts every later
    /// row by an index, which <c>Apply</c> lands as a chain of replaces over the reader's own row.
    /// </summary>
    [Fact]
    public async Task ARowInsertedAheadOfTheSelection_KeepsIt()
    {
        var (bar, equipment, _, raised) = await Chosen();
        LikeTheBoundPicker(bar);

        equipment.Insert(0, new EquipmentCombination("Esprit 100", "ASI1600MM", false));
        bar.Reload();
        await bar.PendingLoad;

        Assert.Equal(
            new[] { "All equipment", "Esprit 100 + ASI1600MM", "RC8 + ASI2600MM", "Askar FMA180 + ASI533MC" },
            bar.EquipmentChoices.Select(choice => choice.Label));
        Assert.Equal("RC8 + ASI2600MM", bar.SelectedEquipment.Label);
        Assert.Equal(0, raised());
    }

    /// <summary>The same four shapes for the filter picker, whose identity is the name.</summary>
    /// <remarks>
    /// <para>
    /// The filter list is <c>ORDER BY i.filter_used</c>, so it cannot reorder by itself; the
    /// reordered arm is kept anyway, because the picker must not depend on that query's clause
    /// staying as it is. A name sorting into the middle is the one the real query produces, and it
    /// shifts every later row.
    /// </para>
    /// <para>
    /// Seen red against the shipped publish on the first two arms, both reading "All filters" and
    /// one <c>Changed</c> where they name "Ha" and none, and against a publish with no selection
    /// reapplication on the third, which keeps querying for a name no frame carries.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheFilterPicker_KeepsItsNameAcrossAReorderAndAnInsert_AndWidensOnlyWhenItIsGone()
    {
        var (reordered, _, reorderedFilters, reorderedRaised) = await Chosen();
        LikeTheBoundPicker(reordered);
        reorderedFilters.Reverse();
        reordered.Reload();
        await reordered.PendingLoad;
        Assert.Equal(new[] { "All filters", "OIII", "Ha" }, reordered.FilterChoices);
        Assert.Equal("Ha", reordered.SelectedFilter);
        Assert.Equal("Ha", reordered.Current!.FilterUsed);
        Assert.Equal(0, reorderedRaised());

        var (inserted, _, insertedFilters, insertedRaised) = await Chosen();
        LikeTheBoundPicker(inserted);
        insertedFilters.Insert(0, "Blue");
        inserted.Reload();
        await inserted.PendingLoad;
        Assert.Equal(new[] { "All filters", "Blue", "Ha", "OIII" }, inserted.FilterChoices);
        Assert.Equal("Ha", inserted.SelectedFilter);
        Assert.Equal(0, insertedRaised());

        // No picker stand-in on this arm, for the reason the equipment fallback case gives: the
        // control's null write-back would reach "All filters" by accident and hide the fallback.
        var (gone, _, goneFilters, goneRaised) = await Chosen();
        goneFilters.Remove("Ha");
        gone.Reload();
        await gone.PendingLoad;
        Assert.Equal("All filters", gone.SelectedFilter);
        Assert.Null(gone.Current!.FilterUsed);
        Assert.Equal(1, goneRaised());
    }

    /// <summary>
    /// A notification landing between the page's subscribe and
    /// <c>SharedFilter.Load()</c> reaches <c>Reload</c>, which must do nothing: the lists already
    /// hold what the first read will publish, and the constructor's last statement still owns the
    /// ordering.
    /// </summary>
    /// <remarks>Seen red against <c>Reload</c> setting <c>_loadStarted</c> and reading, which is
    /// the shipped shape: the counter reads 1 before <c>Load</c> and <c>Load</c> then returns
    /// early having done nothing, so the read ran from inside the owner's constructor.</remarks>
    [Fact]
    public async Task AReloadBeforeTheFirstLoad_ReadsNothing_AndLeavesLoadOwningTheFirstRead()
    {
        var reads = 0;
        var bar = new SharedFilterViewModel(
            () =>
            {
                Interlocked.Increment(ref reads);
                return [new EquipmentCombination("RC8", "ASI2600MM", false)];
            },
            () => ["Ha"],
            post: action => action());

        bar.Reload();
        await bar.PendingLoad;

        Assert.Equal(0, Volatile.Read(ref reads));
        Assert.Equal(new[] { "All equipment" }, bar.EquipmentChoices.Select(choice => choice.Label));
        Assert.Equal(new[] { "All filters" }, bar.FilterChoices);

        // Load still owns the first read, which is the whole of the ordering the page depends on.
        bar.Load();
        await bar.PendingLoad;

        Assert.Equal(1, Volatile.Read(ref reads));
        Assert.Equal(2, bar.EquipmentChoices.Count);
    }

    [Fact]
    public void TheGroupedMarker_IsTheStatisticsPagesOwnGlyphAndTooltip_AndNotASecondCopy()
    {
        // Spec 12.14: "the same mark the Statistics equipment inventory already uses". The glyph is
        // written once in this page's markup and the tooltip is declared once, on the Statistics
        // row view-model; a second copy of either half is a finding.
        Assert.Equal("(grouped)", SharedFilterViewModel.GroupedMarker);
        Assert.Equal(EquipmentComboRowViewModel.GroupedTooltip, SharedFilterViewModel.GroupedTooltip);
    }

    [Fact]
    public async Task TheFilterList_IsAllFiltersPlusTheStoredValues_AndAllFiltersAppliesNoPredicate()
    {
        var bar = await Bar(filters: ["Ha", "OIII", "SII"]);

        Assert.Equal(new[] { "All filters", "Ha", "OIII", "SII" }, bar.FilterChoices);
        Assert.Equal("All filters", bar.SelectedFilter);
        Assert.Null(bar.Current!.FilterUsed);

        bar.SelectedFilter = "OIII";
        Assert.Equal("OIII", bar.Current!.FilterUsed);
    }

    [Fact]
    public async Task BothCollectionInstances_SurviveTheLoad()
    {
        // The Phase 15B lesson the Library tab's Scan interval select and the rig picker both paid
        // for: rebuilding a bound option list under a live two-way SelectedItem binding renders the
        // control empty. The instances are built in the constructor and filled in place.
        var bar = new SharedFilterViewModel(
            () => [new EquipmentCombination("RC8", "ASI2600MM", false)],
            () => ["Ha"],
            post: action => action());
        var equipment = bar.EquipmentChoices;
        var filters = bar.FilterChoices;

        bar.Load();
        await bar.PendingLoad;

        Assert.Same(equipment, bar.EquipmentChoices);
        Assert.Same(filters, bar.FilterChoices);
        Assert.Equal(2, equipment.Count);
        Assert.Equal(2, filters.Count);
    }

    [Fact]
    public async Task TheGranularitySegment_SeedsFromTheStoredValue_AndWritesOnEveryMove()
    {
        var written = new List<AnalysisGranularity>();
        var bar = await Bar(granularity: AnalysisGranularity.Session, writeGranularity: written.Add);

        Assert.True(bar.IsPerSession);
        Assert.False(bar.IsPerFrame);
        Assert.Empty(written);

        bar.SelectGranularityCommand.Execute(AnalysisGranularity.Frame);

        Assert.True(bar.IsPerFrame);
        Assert.Equal([AnalysisGranularity.Frame], written);
        Assert.Equal(AnalysisGranularity.Frame, bar.Current!.Granularity);
    }

    [Fact]
    public async Task AReversedRange_RaisesTheError_AndOffersNoFilterAtAll()
    {
        var bar = await Bar();
        bar.DateFrom = On(2025, 6, 10);
        bar.DateTo = On(2025, 6, 1);

        Assert.True(bar.IsRangeReversed);
        Assert.True(bar.HasError);

        // The refusal is the view-model's, not the markup's: the tab base asks for this and returns
        // before it queries anything when it is null.
        Assert.Null(bar.Current);
    }

    [Fact]
    public void TheReversedRangeSentence_MatchesSpec1214Verbatim()
        => Assert.Equal("From date must be on or before To date.", SharedFilterViewModel.RangeErrorText);

    [Fact]
    public async Task ARangeWithOneEndSet_IsLegalAndIsPassedAsAOneSidedBound()
    {
        var bar = await Bar();

        bar.DateFrom = On(2025, 6, 10);
        Assert.False(bar.HasError);
        Assert.Equal(new DateOnly(2025, 6, 10), bar.Current!.From);
        Assert.Null(bar.Current!.To);

        bar.DateFrom = null;
        bar.DateTo = On(2025, 6, 1);
        Assert.False(bar.HasError);
        Assert.Null(bar.Current!.From);
        Assert.Equal(new DateOnly(2025, 6, 1), bar.Current!.To);
    }

    [Fact]
    public async Task EqualDates_AreLegal_BecauseBothBoundsAreInclusive()
    {
        var bar = await Bar();
        bar.DateFrom = On(2025, 6, 1);
        bar.DateTo = On(2025, 6, 1);

        Assert.False(bar.HasError);
        Assert.NotNull(bar.Current);
    }

    [Fact]
    public async Task AReversedRangeBecomingLegalAgain_RaisesChangedLikeAnyOtherEdit()
    {
        var bar = await Bar();
        bar.DateFrom = On(2025, 6, 10);
        bar.DateTo = On(2025, 6, 1);

        var raised = 0;
        bar.Changed += (_, _) => raised++;
        bar.DateTo = On(2025, 6, 20);

        Assert.Equal(1, raised);
        Assert.False(bar.HasError);
        Assert.NotNull(bar.Current);
    }

    [Fact]
    public async Task EveryControl_RaisesChangedOnce()
    {
        var bar = await Bar();
        var raised = 0;
        bar.Changed += (_, _) => raised++;

        bar.SelectedEquipment = bar.EquipmentChoices[1];
        bar.SelectedFilter = "Ha";
        bar.Granularity = AnalysisGranularity.Session;
        bar.DateFrom = On(2025, 6, 1);
        bar.DateTo = On(2025, 6, 20);

        Assert.Equal(5, raised);

        var filter = bar.Current!;
        Assert.Equal("RC8", filter.Telescope);
        Assert.Equal("ASI2600MM", filter.Camera);
        Assert.Equal("Ha", filter.FilterUsed);
        Assert.Equal(AnalysisGranularity.Session, filter.Granularity);
        Assert.Equal(new DateOnly(2025, 6, 1), filter.From);
        Assert.Equal(new DateOnly(2025, 6, 20), filter.To);
    }

    [Fact]
    public async Task ClearingADate_IsItsOwnGesture_BecauseADatePickerHasNone()
    {
        var bar = await Bar();
        bar.DateFrom = On(2025, 6, 1);
        bar.DateTo = On(2025, 6, 20);

        bar.ClearDateFromCommand.Execute(null);
        bar.ClearDateToCommand.Execute(null);

        Assert.Null(bar.Current!.From);
        Assert.Null(bar.Current!.To);
    }

    [Fact]
    public async Task ANullSelection_FallsBackToTheAllRow_RatherThanLeavingTheBarWithNone()
    {
        // A picker that writes null through its two-way binding must not leave the bar without a
        // choice, the same shape MainWindowViewModel.Selected uses for the rail.
        var bar = await Bar();
        bar.SelectedEquipment = bar.EquipmentChoices[1];
        bar.SelectedEquipment = null!;
        bar.SelectedFilter = null!;

        Assert.Same(SharedFilterViewModel.AllEquipment, bar.SelectedEquipment);
        Assert.Equal("All filters", bar.SelectedFilter);
    }
}
