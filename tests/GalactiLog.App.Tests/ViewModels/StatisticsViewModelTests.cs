using System.Globalization;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using GalactiLog.App.Theme;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Stats;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Metrics;
using GalactiLog.Data;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.StatisticsViewModelTestFactory;

// Phase 14A Task 3 added GalactiLog.Core.Metrics.QualityBand for spec 12.4's frame-table bands
// beside the display-only QualityBand the Statistics page had carried since Phase 11, and this
// file aliased the two apart. The Phase 14A fixer folded the Statistics one onto the Core one, so
// there is one enum again and no alias.
namespace GalactiLog.App.Tests.ViewModels;

// Spec 12.5's page, its three states and its exact number formatting. Plain xunit facts: the page
// takes delegates and a post seam, so it constructs with no database and no window (spec 18.3).
public class StatisticsViewModelTests
{
    [Fact]
    public void Load_PopulatesEverySection()
    {
        using var page = Factory.Create();

        Assert.False(page.IsLoading);
        Assert.False(page.LoadFailed);
        Assert.False(page.IsEmpty);
        Assert.True(page.ShowSections);
        Assert.Equal(6, page.Overview.Tiles.Count);
        Assert.NotEmpty(page.Performance.Rows);
        Assert.NotEmpty(page.Inventory.Cameras);
        Assert.NotEmpty(page.Inventory.Telescopes);
        Assert.NotEmpty(page.FilterUsageRows);
        Assert.NotEmpty(page.TopTargetRows);
        Assert.NotEmpty(page.Timeline.Bars);
        Assert.NotEmpty(page.Calendar.Cells);
        Assert.NotEmpty(page.DataQuality.Figures);
        Assert.NotEmpty(page.Storage.Figures);
        Assert.NotEmpty(page.IngestHistoryChart.Series);
    }

    [Fact]
    public void OverviewTiles_AvgRigSessionLength_IsIntegrationOverRigSessionCount()
    {
        using var page = Factory.Create(loadStats: () => Factory.Sample(
            overview: Factory.Overview(integrationSeconds: 36_000d, rigSessionCount: 4)));

        var tile = page.Overview.Tiles[4];

        Assert.Equal("Avg Rig-Session Length", tile.Label);
        Assert.Equal(MetricText.Integration(9_000d), tile.Value);
        Assert.Equal("4 rig-sessions", tile.Subtitle);
    }

    [Fact]
    public void OverviewTiles_AvgPerTarget_IsIntegrationOverTargetCount()
    {
        using var page = Factory.Create(loadStats: () => Factory.Sample(
            overview: Factory.Overview(integrationSeconds: 36_000d, targetCount: 3)));

        var tile = page.Overview.Tiles[5];

        Assert.Equal("Avg per Target", tile.Label);
        Assert.Equal(MetricText.Integration(12_000d), tile.Value);
        Assert.Equal("3 resolved targets", tile.Subtitle);
    }

    [Fact]
    public void OverviewTiles_AvgTiles_RenderADash_WhenTheDivisorIsZero()
    {
        using var page = Factory.Create(loadStats: () => Factory.Sample(
            overview: Factory.Overview(integrationSeconds: 36_000d, rigSessionCount: 0, targetCount: 0)));

        Assert.Equal(MetricText.Missing, page.Overview.Tiles[4].Value);
        Assert.Equal(MetricText.Missing, page.Overview.Tiles[5].Value);
    }

    [Theory]
    // Under a month, including the "end day is earlier in the month" subtraction.
    [InlineData("2025-01-10", "2025-02-09", "< 1 mo")]
    [InlineData("2025-01-10", "2025-01-20", "< 1 mo")]
    // Months only.
    [InlineData("2025-01-10", "2025-04-10", "3 mo")]
    // Whole years.
    [InlineData("2022-05-01", "2025-05-01", "3y")]
    // Mixed.
    [InlineData("2022-05-01", "2025-08-15", "3y 3mo")]
    public void OverviewTiles_ActiveSpan_UsesTheWebSpanRule(string first, string last, string expected)
        => Assert.Equal(
            expected,
            OverviewTilesViewModel.Span(
                DateOnly.ParseExact(first, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                DateOnly.ParseExact(last, "yyyy-MM-dd", CultureInfo.InvariantCulture)));

    [Fact]
    public void OverviewTiles_ActiveSpan_IsADash_WithNoSessions()
        => Assert.Equal(MetricText.Missing, OverviewTilesViewModel.Span(null, null));

    [Fact]
    public void OverviewTiles_CataloguedSize_SubtitleSaysTheFigureIsCataloguedBytes()
    {
        using var page = Factory.Create();

        // questions.md Q11 dropped fits_disk_bytes, so the web's "<x> on disk" subtitle has no
        // figure to carry and the card says what its own number is instead.
        var tile = page.Overview.Tiles[2];
        Assert.Equal("Catalogued Size", tile.Label);
        Assert.Contains("catalogued bytes", tile.Subtitle, StringComparison.Ordinal);
        Assert.DoesNotContain("on disk", tile.Subtitle, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyLibrary_ShowsTheSpecEmptyState()
    {
        using var page = Factory.Create(loadStats: () => Factory.Empty());

        Assert.True(page.IsEmpty);
        Assert.False(page.LoadFailed);
        Assert.False(page.ShowSections);
        Assert.Equal("No data yet. Run a scan.", StatisticsViewModel.EmptyStateText);
    }

    [Fact]
    public void FailedLoad_ShowsAFailureLine_NotAnEmptyState()
    {
        using var page = Factory.Create(loadStats: () => throw new InvalidOperationException("no database"));

        // A failed read taught nothing about whether the library is empty.
        Assert.True(page.LoadFailed);
        Assert.False(page.IsEmpty);
        Assert.False(page.IsLoading);
        Assert.False(page.ShowSections);
        Assert.Equal("Statistics could not be loaded.", StatisticsViewModel.FailureText);
    }

    // Drops back to idle exactly the way ScanFinished does in production: the bare coordinator's
    // connection string is never migrated, so the run throws once its pipeline reads settings, but
    // not before its finally block has raised ScanFinished. The same shape StatusBarViewModelTests
    // already uses.
    private static Task RaiseScanFinishedAsync(GalactiLog.Data.Ingest.ScanCoordinator coordinator)
        => Assert.ThrowsAnyAsync<Exception>(
            () => coordinator.RunAsync(GalactiLog.Data.Ingest.ScanTrigger.Manual, null, CancellationToken.None));

    [Fact]
    public async Task ScanFinished_ReloadsOnce()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        using var status = new ScanStatusService(coordinator, action => action());
        var loads = 0;
        using var page = Factory.Create(
            loadStats: () =>
            {
                Interlocked.Increment(ref loads);
                return Factory.Sample();
            },
            scanStatus: status);
        Assert.Equal(1, loads);

        await RaiseScanFinishedAsync(coordinator);
        await Factory.SettleAsync(page);

        Assert.Equal(2, loads);
    }

    [Fact]
    public void ScanFinished_IsTheOnlyRefreshTrigger()
    {
        // Asserted by construction: the page takes a ScanStatusService and nothing else that could
        // push at it. There is no ScanCoordinator parameter, so a direct subscription is not
        // expressible, and there is no timer.
        var parameters = typeof(StatisticsViewModel).GetConstructors().Single().GetParameters();

        Assert.DoesNotContain(parameters, parameter => parameter.ParameterType.Name.Contains("ScanCoordinator", StringComparison.Ordinal));
        Assert.Contains(parameters, parameter => parameter.ParameterType == typeof(ScanStatusService));
    }

    [Fact]
    public void Refresh_WhileLoading_IsRefusedInTheCommandBody()
    {
        // RelayCommand.Execute ignores CanExecute (TRACKING section 6 item 13), so the guard has
        // to be in the body too. The gate here is a load that never completes.
        var gate = new ManualResetEventSlim(false);
        var started = new ManualResetEventSlim(false);
        var loads = 0;
        var page = new StatisticsViewModel(
            () =>
            {
                Interlocked.Increment(ref loads);
                started.Set();
                gate.Wait(TimeSpan.FromSeconds(30));
                return Factory.Sample();
            },
            (_, _) => [],
            Factory.WithCoordinates,
            Factory.NoAliases,
            post: action => action(),
            today: () => Factory.Today);

        started.Wait(TimeSpan.FromSeconds(30));
        Assert.True(page.IsLoading);
        Assert.False(page.RefreshCommand.CanExecute(null));
        page.RefreshCommand.Execute(null);

        Assert.Equal(1, loads);
        gate.Set();
        Factory.Settle(page);
        page.Dispose();
        gate.Dispose();
        started.Dispose();
    }

    [Fact]
    public void Refresh_InvalidatesTheMemoizedResponse_NotJustTheProjection()
    {
        // Review finding I2. Backed by a real StatsCache over a counting query stand-in, because a
        // bare lambda has no memo and would pass whether or not the command invalidates.
        using var database = new TempDatabase("galactilog-stats-refresh");
        var connectionString = new DatabaseConnectionString(database.ConnectionString);
        using var aliases = new AliasMapCache(new SettingsStore(new SettingsRepository(database.ConnectionString)));
        var cache = new StatsCache(new StatsQuery(
            connectionString,
            aliases,
            // No profile map: this case counts reads through the memo and has no guiding row.
            new GuidingStatsQuery(connectionString, aliases, static () => null)));
        var reads = 0;

        using var page = Factory.Create(
            loadStats: () =>
            {
                reads++;
                return cache.Current;
            },
            invalidate: cache.Invalidate);
        Assert.Equal(1, reads);
        var first = cache.Current;

        page.RefreshCommand.Execute(null);
        Factory.Settle(page);

        // Two reads of the page's delegate, and the second one got a freshly built response rather
        // than the memo: StatsCache carries no TTL, so without the invalidation these would be the
        // same instance forever.
        Assert.Equal(2, reads);
        Assert.NotSame(first, cache.Current);
    }

    [Fact]
    public void Refresh_WhileLoading_DoesNotInvalidate()
    {
        var gate = new ManualResetEventSlim(false);
        var started = new ManualResetEventSlim(false);
        var invalidations = 0;
        var page = new StatisticsViewModel(
            () =>
            {
                started.Set();
                gate.Wait(TimeSpan.FromSeconds(30));
                return Factory.Sample();
            },
            (_, _) => [],
            Factory.WithCoordinates,
            Factory.NoAliases,
            invalidate: () => Interlocked.Increment(ref invalidations),
            post: action => action(),
            today: () => Factory.Today);

        started.Wait(TimeSpan.FromSeconds(30));
        page.RefreshCommand.Execute(null);

        // The guard is ahead of the invalidation, so a refused click drops nothing.
        Assert.Equal(0, invalidations);
        gate.Set();
        Factory.Settle(page);
        page.Dispose();
        gate.Dispose();
        started.Dispose();
    }

    [Fact]
    public void RequestLoad_ClearsTheFailureAndEmptyStates_SoOnlyOneLineIsEverShowing()
    {
        // Review finding M1: the three states are exclusive, and the two that describe a finished
        // load are cleared when a new one starts. Without this a refresh after a failure painted
        // the loading line on top of the failure line.
        var fail = true;
        var gate = new ManualResetEventSlim(false);
        var started = new ManualResetEventSlim(false);
        var page = Factory.Create(loadStats: () => throw new InvalidOperationException("no database"));
        Assert.True(page.LoadFailed);
        page.Dispose();

        var second = new StatisticsViewModel(
            () =>
            {
                started.Set();
                gate.Wait(TimeSpan.FromSeconds(30));
                return fail ? throw new InvalidOperationException("no database") : Factory.Sample();
            },
            (_, _) => [],
            Factory.WithCoordinates,
            Factory.NoAliases,
            post: action => action(),
            today: () => Factory.Today);
        started.Wait(TimeSpan.FromSeconds(30));
        gate.Set();
        Factory.Settle(second);
        Assert.True(second.LoadFailed);

        fail = false;
        gate.Reset();
        started.Reset();
        second.RefreshCommand.Execute(null);
        started.Wait(TimeSpan.FromSeconds(30));

        Assert.True(second.IsLoading);
        Assert.False(second.LoadFailed);
        Assert.False(second.IsEmpty);

        gate.Set();
        Factory.Settle(second);
        second.Dispose();
        gate.Dispose();
        started.Dispose();
    }

    [Fact]
    public void OverviewTiles_CataloguedSize_RendersZeroBytes_NotThePlaceholder()
    {
        // Review finding M10: a catalogue that holds no bytes genuinely holds zero, which is not
        // the same claim as "there is no figure".
        using var page = Factory.Create(loadStats: () => Factory.Sample(
            storage: new StorageStats(0, 0, 0)));

        Assert.Equal("0 B", page.Overview.Tiles[2].Value);
    }

    [Fact]
    public void Refresh_AfterALoad_ReadsAgain()
    {
        var loads = 0;
        using var page = Factory.Create(loadStats: () =>
        {
            loads++;
            return Factory.Sample();
        });

        page.RefreshCommand.Execute(null);
        Factory.Settle(page);

        Assert.Equal(2, loads);
    }

    [AvaloniaFact]
    public async Task Dispose_UnsubscribesFromScanFinishedAndChartThemeChanged()
    {
        // An AvaloniaFact because it calls ChartTheme.Apply, which reads the merged token
        // dictionary: with no application every entry would fall back to the neutral grey.
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        using var status = new ScanStatusService(coordinator, action => action());
        var loads = 0;
        var page = Factory.Create(
            loadStats: () =>
            {
                Interlocked.Increment(ref loads);
                return Factory.Sample();
            },
            scanStatus: status);
        var seriesBefore = page.TopTargetsChart.Series;
        var timelineBefore = page.Timeline.Series;
        var storageBefore = page.Storage.Series;

        page.Dispose();
        await RaiseScanFinishedAsync(coordinator);
        ChartTheme.Apply();

        Assert.Equal(1, loads);
        Assert.Same(seriesBefore, page.TopTargetsChart.Series);
        Assert.Same(timelineBefore, page.Timeline.Series);
        Assert.Same(storageBefore, page.Storage.Series);
    }

    [Fact]
    public void EquipmentPerformance_GradesEccentricityAndFwhm_ButNeverHfr()
    {
        // Ten rigs, so the group clears FrameQuality.MinGroup, with one clear outlier.
        var combos = Enumerable.Range(0, 9)
            .Select(index => Factory.Combo(
                telescope: "RC8",
                camera: "Cam" + index,
                medianHfr: 2.0d + (index * 0.01d),
                medianEccentricity: 0.40d + (index * 0.01d),
                medianFwhm: 2.0d + (index * 0.01d)))
            .Append(Factory.Combo(
                telescope: "RC8",
                camera: "Outlier",
                medianHfr: 99d,
                medianEccentricity: 9d,
                medianFwhm: 9d))
            .ToList();

        using var page = Factory.Create(loadStats: () => Factory.Sample(performance: combos));
        var outlier = page.Performance.Rows.Single(row => row.Name.Contains("Outlier", StringComparison.Ordinal));

        Assert.Equal(QualityBand.Reject, outlier.MedianEccentricity.Band);
        Assert.Equal(QualityBand.Reject, outlier.MedianFwhm.Band);
        Assert.Contains("sigma vs catalog median", outlier.MedianEccentricity.Tooltip, StringComparison.Ordinal);

        // HFR is in pixels, so it is never graded across rigs even when it is the most extreme
        // value on the screen. It is a plain string with no band at all.
        Assert.Equal("99.00", outlier.MedianHfr);
        Assert.Equal(
            "Pixel HFR; only comparable within this optical train, not graded across rigs",
            EquipmentComboRowViewModel.HfrNotGradedTooltip);
    }

    [AvaloniaFact]
    public void EquipmentPerformance_TheFourBandColours_AreDistinctThemeTokens()
    {
        // Review finding I1: the first implementation named the *Value keys, which the dictionary
        // declares as Color rather than SolidColorBrush, so Better, Watch and Reject all resolved
        // to ChartTheme's neutral grey and the grading palette was dead on screen. Asserted on the
        // brushes, which is what the old tests never looked at.
        ChartTheme.Apply();
        var brushes = new BandBrushes();
        IImmutableSolidColorBrush[] all = [brushes.Neutral, brushes.Better, brushes.Watch, brushes.Reject];

        Assert.All(all, brush => Assert.NotEqual(
            Color.FromArgb(
                ChartTheme.Fallback.Alpha,
                ChartTheme.Fallback.Red,
                ChartTheme.Fallback.Green,
                ChartTheme.Fallback.Blue),
            brush.Color));
        Assert.Equal(4, all.Select(brush => brush.Color).Distinct().Count());

        // And each one is the token it names, not a value this test invented.
        for (var index = 0; index < all.Length; index++)
        {
            var token = ChartTheme.Read(BandBrushes.TokenKeys[index], ChartTheme.Fallback);
            Assert.Equal(token.Red, all[index].Color.R);
            Assert.Equal(token.Green, all[index].Color.G);
            Assert.Equal(token.Blue, all[index].Color.B);
        }

        // The missing-value ink is the faint token, outside the band order.
        var faint = ChartTheme.Read(BandBrushes.MissingKey, ChartTheme.Fallback);
        Assert.Equal((faint.Red, faint.Green, faint.Blue), (brushes.Missing.Color.R, brushes.Missing.Color.G, brushes.Missing.Color.B));
        Assert.DoesNotContain(BandBrushes.MissingKey, BandBrushes.TokenKeys);
    }

    [Fact]
    public void EquipmentPerformance_Grading_IsSuppressedBelowMinGroup()
    {
        Assert.Equal(8, FrameQuality.MinGroup);
        var combos = Enumerable.Range(0, 7)
            .Select(index => Factory.Combo(
                camera: "Cam" + index,
                medianEccentricity: index == 0 ? 9d : 0.40d + (index * 0.01d)))
            .ToList();

        using var page = Factory.Create(loadStats: () => Factory.Sample(performance: combos));

        // Seven rigs is too sparse to grade, so the outlier stays neutral and carries no tooltip.
        Assert.All(page.Performance.Rows, row => Assert.Equal(QualityBand.Neutral, row.MedianEccentricity.Band));
        Assert.All(page.Performance.Rows, row => Assert.Equal("", row.MedianEccentricity.Tooltip));
    }

    [Theory]
    [InlineData(null, QualityBand.Neutral)]
    [InlineData(-2.0d, QualityBand.Better)]
    [InlineData(-1.0d, QualityBand.Better)]
    [InlineData(0d, QualityBand.Neutral)]
    [InlineData(1.49d, QualityBand.Neutral)]
    [InlineData(1.5d, QualityBand.Watch)]
    [InlineData(2.99d, QualityBand.Watch)]
    [InlineData(3.0d, QualityBand.Reject)]
    public void BandForZ_MatchesTheWebThresholds(double? z, QualityBand expected)
        // The Statistics page's own BandForZ is gone with its enum, folded onto the Core pair the
        // frame table, the preview badges and the copy dialog already read. This case proves the
        // one surviving ladder against the web's thresholds; the graded-cell cases above prove
        // that the Statistics page reads it.
        => Assert.Equal(expected, FrameQuality.BandForZ(z));

    [Fact]
    public void EquipmentInventory_RendersCamerasBeforeTelescopes()
    {
        using var page = Factory.Create(loadStats: () => Factory.Sample(
            cameras: [Factory.Inventory("ASI2600MM"), Factory.Inventory("ASI533MC")],
            telescopes: [Factory.Inventory("RC8")]));

        Assert.Equal(new[] { "ASI2600MM", "ASI533MC" }, page.Inventory.Cameras.Select(row => row.Name));
        Assert.Equal(new[] { "RC8" }, page.Inventory.Telescopes.Select(row => row.Name));
    }

    [Fact]
    public void EquipmentInventory_PreservesTheServerOrder()
    {
        // Deliberately not alphabetical: the query already ordered these and there is no
        // client-side sort, exactly as in the web source.
        using var page = Factory.Create(loadStats: () => Factory.Sample(
            cameras: [Factory.Inventory("Zeta"), Factory.Inventory("Alpha"), Factory.Inventory("Mu")]));

        Assert.Equal(new[] { "Zeta", "Alpha", "Mu" }, page.Inventory.Cameras.Select(row => row.Name));
    }

    [Fact]
    public void Inventory_FiguresCarryNoUnitSuffix()
    {
        // The unit is in the column header once (spec.md item 3).
        using var page = Factory.Create(loadStats: () => Factory.Sample(
            cameras: [Factory.Inventory("ASI2600MM") with { MedianFwhmArcsec = 2.31d, MedianGuidingRmsArcsec = 0.52d }]));

        Assert.Equal("2.31", page.Inventory.Cameras[0].MedianFwhm);
        Assert.Equal("0.52", page.Inventory.Cameras[0].MedianGuidingRms);
    }

    [Fact]
    public void FwhmFrameCount_WithNoFwhmFrames_IsMissing()
    {
        using var page = Factory.Create(loadStats: () => Factory.Sample(
            performance: [Factory.Combo() with { FwhmFrameCount = 0 }],
            cameras: [Factory.Inventory("ASI2600MM") with { FwhmFrameCount = 0 }]));

        Assert.Equal(MetricText.Missing, page.Performance.Rows[0].FwhmFrameCount);
        Assert.Equal(MetricText.Missing, page.Inventory.Cameras[0].FwhmFrameCount);
    }

    [Fact]
    public void Performance_FiltersWithNoBreakdown_IsMissing()
    {
        using var page = Factory.Create(loadStats: () => Factory.Sample(
            performance: [Factory.Combo(breakdown: [])]));

        Assert.Equal(MetricText.Missing, page.Performance.Rows[0].Filters);
    }

    [AvaloniaFact]
    public void GradedCell_ForAMissingValue_TakesTheFaintInk()
    {
        // The view binds a graded cell's Foreground locally, which outranks the table's faint-dash
        // style, so the cell's own brush must be the faint one.
        ChartTheme.Apply();
        var brushes = new BandBrushes();

        var cell = MetricGrading.Grade(null, MetricBaseline.Of([0.4d]), "Ecc", brushes);

        Assert.Equal(MetricText.Missing, cell.Text);
        Assert.Same(brushes.Missing, cell.Brush);
    }

    [Fact]
    public void FilterUsage_IsOrderedByIntegrationDescending()
    {
        using var page = Factory.Create(loadStats: () => Factory.Sample(filterUsage:
        [
            new FilterUsageEntry("L", 1_000d),
            new FilterUsageEntry("Ha", 9_000d),
            new FilterUsageEntry("OIII", 5_000d),
        ]));

        Assert.Equal(new[] { "Ha", "OIII", "L" }, page.FilterUsageRows.Select(row => row.FilterName));
    }

    [Fact]
    public void TopTargets_TakesTheFirstTen()
    {
        var targets = Enumerable.Range(0, 20)
            .Select(index => new TopTargetEntry("T" + index, 20_000d - index))
            .ToList();
        using var page = Factory.Create(loadStats: () => Factory.Sample(topTargets: targets));

        Assert.Equal(10, page.TopTargetRows.Count);
        Assert.Equal("T0", page.TopTargetRows[0].Name);
        Assert.Equal(1, page.TopTargetRows[0].Rank);
        Assert.Equal("T9", page.TopTargetRows[9].Name);
        Assert.Equal(10, page.TopTargetRows[9].Rank);
    }

    [Theory]
    [InlineData(0d, "0 B")]
    [InlineData(-5d, "0 B")]
    [InlineData(512d, "512 B")]
    [InlineData(20_000d, "20 KB")]
    [InlineData(20_000_000d, "20 MB")]
    [InlineData(1_500_000_000d, "1.5 GB")]
    [InlineData(2_340_000_000_000d, "2.34 TB")]
    public void Storage_FormatsBytesWithTheDecimalSiRule(double bytes, string expected)
        => Assert.Equal(expected, MetricText.Bytes(bytes));

    [Theory]
    // A whole-minute total drops the seconds field.
    [InlineData(3_600d, "01h 00m")]
    [InlineData(3_660d, "01h 01m")]
    // A non-whole minute keeps it, zero-padded.
    [InlineData(3_665d, "01h 01m 05s")]
    // Hours are not capped at 24.
    [InlineData(1_123_500d, "312h 05m")]
    // Negative clamps to zero.
    [InlineData(-10d, "00h 00m")]
    public void FormatIntegration_MatchesTheWebRule(double seconds, string expected)
        => Assert.Equal(expected, MetricText.Integration(seconds));

    /// <summary>
    /// Phase 15B fixer F2. An open page re-reads when the host says the derived data behind it has
    /// been rewritten, and it unfollows when it is disposed.
    /// </summary>
    /// <remarks>
    /// A failure looks like the complete flow this phase built ending where it started: the reader
    /// reads the guiding empty notice, follows its "Map profiles" link into Settings, maps a
    /// profile, navigates back and reads the same notice, because this page is a DI singleton and
    /// the page they left is the page they come back to.
    /// </remarks>
    [Fact]
    public void ADerivedDataNotification_MakesTheOpenPageReRead_AndDisposeUnfollows()
    {
        var derived = new DerivedDataSource();
        var reads = 0;
        var page = Factory.Create(
            loadStats: () =>
            {
                Interlocked.Increment(ref reads);
                return Factory.Sample();
            },
            derivedData: derived);

        Assert.Equal(1, reads);

        derived.Raise();
        Factory.Settle(page);

        Assert.Equal(2, reads);

        page.Dispose();
        Assert.False(derived.HasFollower);

        // And a notification after the page has gone reaches nothing at all.
        derived.Raise();
        Assert.Equal(2, reads);
    }

    /// <summary>
    /// Phase 15B fixer F2's storm hazard. <c>GeneralChanged</c> is raised on EVERY general save,
    /// which includes each committed keystroke in the PHD2 profiles panel, so a burst of
    /// notifications arriving while a read is in flight must cost exactly one further read rather
    /// than one per notification.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A failure looks like a reader typing a latitude into the PHD2 profiles panel and starting a
    /// full-library aggregate per keystroke, each one on a pool thread and each one holding a
    /// SQLite connection.
    /// </para>
    /// <para>
    /// The further read is owed rather than optional, which is why the answer is coalescing and
    /// not dropping: the read in flight may have taken its response from the memo the
    /// notification's own handler had not yet dropped, so the page would settle on figures the
    /// save invalidated.
    /// </para>
    /// </remarks>
    [Fact]
    public void ABurstOfNotificationsDuringALoad_CostsExactlyOneFurtherRead()
    {
        using var gate = new ManualResetEventSlim(false);
        var derived = new DerivedDataSource();
        var reads = 0;
        using var page = Factory.Create(
            loadStats: () =>
            {
                // The second read is the one this case parks, so the burst below lands while a
                // load really is in flight rather than between two finished ones.
                if (Interlocked.Increment(ref reads) == 2)
                {
                    Assert.True(gate.Wait(TimeSpan.FromSeconds(30)), "the parked read was never released");
                }

                return Factory.Sample();
            },
            derivedData: derived);

        Assert.Equal(1, reads);

        // The first notification starts the read this case parks; the next three arrive while it
        // is in flight and are absorbed into the one reload it owes.
        derived.Raise();
        derived.Raise();
        derived.Raise();
        derived.Raise();

        gate.Set();
        Factory.Settle(page);

        Assert.Equal(3, reads);
    }

    /// <summary>
    /// Phase 15B fixer item 39. <c>ChartTheme.Read</c>'s own summary promises that a token it
    /// cannot resolve falls back to a documented neutral rather than throwing, and a token the
    /// dictionary declares as a mutable <c>SolidColorBrush</c> cannot be resolved off the UI
    /// thread: its <c>Color</c> getter verifies thread access. The guard belongs in that one
    /// member and not in a caller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It lives in this file because this is the caller the throw was found through:
    /// <c>StatisticsViewModel</c> builds <c>BandBrushes</c> and <c>ArcBrushes</c>, and a filtered
    /// run that tears the headless application down around these cases reached the getter from a
    /// pool thread. <c>Storage_ThreeFiguresAndThreeSlices</c> below is the case that failed.
    /// </para>
    /// <para>
    /// The probe is added to the live resources rather than named from the shipped dictionary, so
    /// the case pins the rule and not which of the theme's 37 tokens happens to be declared
    /// mutable today. A failure looks like a window that will not open where a grey chart would
    /// have done.
    /// </para>
    /// </remarks>
    [AvaloniaFact]
    public async Task ChartThemeRead_AnswersItsFallback_WhenTheBrushCannotBeReadOnTheCallingThread()
    {
        const string key = "Phase15BItem39ProbeBrush";
        var application = Application.Current;
        Assert.NotNull(application);
        application.Resources[key] = new SolidColorBrush(Color.FromRgb(0x12, 0x34, 0x56));

        try
        {
            // On the thread that may read it, the token is the token.
            Assert.Equal(
                new SkiaSharp.SKColor(0x12, 0x34, 0x56, 0xFF),
                ChartTheme.Read(key, ChartTheme.Fallback));

            // Off it, the documented neutral rather than an InvalidOperationException.
            Assert.Equal(
                ChartTheme.Fallback,
                await Task.Run(() => ChartTheme.Read(key, ChartTheme.Fallback)));
        }
        finally
        {
            application.Resources.Remove(key);
        }
    }

    [Fact]
    public void Storage_ThreeFiguresAndThreeSlices()
    {
        using var page = Factory.Create(loadStats: () => Factory.Sample(
            storage: new StorageStats(1_500_000_000L, 40_000_000L, 9_000_000L)));

        Assert.Equal(
            new[] { "1.5 GB", "40 MB", "9 MB" },
            page.Storage.Figures.Select(figure => figure.Value));
    }

    [Fact]
    public void TimelineBarClick_ForwardsTheRangeFromThePage()
    {
        using var page = Factory.Create(loadStats: () => Factory.Sample(
            monthly: [new TimelineEntry("2025-05", 3_600d)]));
        page.Timeline.SelectPresetCommand.Execute(TimelineRangePreset.Month);

        // The M preset sets Daily; the selector stays live, so this is also the case that proves
        // an override survives the preset (questions.md Q15).
        page.Timeline.Granularity = TimelineGranularity.Monthly;

        (DateOnly From, DateOnly To)? raised = null;
        page.DateRangeRequested += (_, range) => raised = range;

        page.Timeline.SelectPeriodCommand.Execute(0);

        // questions.md Q16: the page forwards it and the shell answers it.
        Assert.Equal((new DateOnly(2025, 5, 1), new DateOnly(2025, 5, 31)), raised);
    }

    [Fact]
    public void DataQuality_CarriesTheSpecFigures()
    {
        using var page = Factory.Create();

        Assert.Equal(7, page.DataQuality.Figures.Count);
        Assert.Equal("HFRStDev", page.DataQuality.EccentricitySource);
        Assert.Contains(page.DataQuality.Figures, figure => figure.Label == "No plate scale" && figure.Value == "17");
    }
}
