using Avalonia.Headless.XUnit;
using Avalonia.Media.Immutable;
using GalactiLog.App.Theme;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Activity;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Queries;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.ActivityViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

// Spec 12.6's page, its three states, its two expanders and its two actions. Plain xunit facts:
// the page takes delegates and a post seam, so it constructs with no database and no window
// (spec 18.3). The roadmap's Phase 9 row 4 Verify line names one of these by behaviour, "a scan
// event expands to its children", which is ScanEvent_ExpandsToItsChildren.
public class ActivityViewModelTests
{
    // Drops back to idle exactly the way ScanFinished does in production: the bare coordinator's
    // connection string is never migrated, so the run throws once its pipeline reads settings, but
    // not before its finally block has raised ScanFinished. The same shape StatusBarViewModelTests
    // and StatisticsViewModelTests already use.
    private static Task RaiseScanFinishedAsync(ScanCoordinator coordinator)
        => Assert.ThrowsAnyAsync<Exception>(
            () => coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None));

    [Fact]
    public void Load_PopulatesTheRows()
    {
        using var page = Factory.Create(
            page: (_, _, _) => Factory.Page([Factory.Row(1, "first"), Factory.Row(2, "second")]));

        Assert.False(page.IsLoading);
        Assert.False(page.LoadFailed);
        Assert.False(page.IsEmpty);
        Assert.True(page.ShowList);
        Assert.Equal(["first", "second"], page.Rows.Select(row => row.Message));
        Assert.Equal(2, page.Total);
        Assert.Equal("2 events", page.TotalText);
    }

    [Fact]
    public void Load_AsksForTheWebPageSize()
    {
        var requested = 0;
        using var page = Factory.Create(page: (_, _, limit) =>
        {
            requested = limit;
            return Factory.Page();
        });

        Assert.Equal(50, requested);
        Assert.Equal(50, ActivityViewModel.PageSize);
    }

    // The roadmap's named assertion.
    [Fact]
    public void ScanEvent_ExpandsToItsChildren()
    {
        using var page = Factory.Create(page: (_, _, _) => Factory.WithChildren(
            Factory.Row(2, "Classifying", parentId: 1),
            Factory.Row(3, "Orphans pruned", parentId: 1)));

        var scan = page.Rows.Single();
        Assert.True(scan.HasChildren);

        scan.ToggleChildrenCommand.Execute(null);

        Assert.True(scan.AreChildrenExpanded);
        Assert.Equal(["Classifying", "Orphans pruned"], scan.Children.Select(child => child.Message));
    }

    [Fact]
    public void ScanEvent_ChildrenAreCollapsedByDefault()
    {
        using var page = Factory.Create(page: (_, _, _) => Factory.WithChildren(
            Factory.Row(2, "Classifying", parentId: 1)));

        var scan = page.Rows.Single();

        Assert.False(scan.AreChildrenExpanded);
        Assert.Equal("Expand sub-tasks", scan.SubTaskToggleTitle);

        scan.ToggleChildrenCommand.Execute(null);
        Assert.Equal("Collapse sub-tasks", scan.SubTaskToggleTitle);
    }

    [Theory]
    [InlineData(1, "1 sub-task")]
    [InlineData(2, "2 sub-tasks")]
    [InlineData(7, "7 sub-tasks")]
    public void ScanEvent_ToggleLabel_PluralisesSubTasks(int count, string expected)
    {
        var children = Enumerable.Range(0, count)
            .Select(i => Factory.Row(2 + i, $"step {i}", parentId: 1))
            .ToArray();
        using var page = Factory.Create(page: (_, _, _) => Factory.WithChildren(children));

        Assert.Equal(expected, page.Rows.Single().SubTaskLabel);
    }

    [Fact]
    public void ARowWithNoChildren_ShowsNoToggle()
    {
        using var page = Factory.Create();

        Assert.False(page.Rows.Single().HasChildren);
    }

    // FIXER item 4: a scan's terminal event carries parent_id, so a failed run is a child of its
    // scan_started row. The worst child severity has to reach the collapsed parent or the feed
    // reports a failed scan as an ordinary "Scan started" line.
    [Theory]
    [InlineData("error", "This scan recorded an error")]
    [InlineData("warning", "This scan recorded a warning")]
    public void ScanEvent_WithAFailedOrCancelledChild_SurfacesTheSeverityOnTheParent(
        string severity, string expectedTitle)
    {
        using var page = Factory.Create(page: (_, _, _) => Factory.WithChildren(
            Factory.Row(2, "Classifying", parentId: 1),
            Factory.Row(3, "Scan failed: disk full", severity: severity, eventType: "scan_failed", parentId: 1)));

        var scan = page.Rows.Single();

        Assert.True(scan.HasChildAlert);
        Assert.Equal(severity, scan.ChildAlertSeverity);
        Assert.Equal(expectedTitle, scan.ChildAlertTitle);
        Assert.False(scan.AreChildrenExpanded);
    }

    [Fact]
    public void ScanEvent_WithOnlyInfoChildren_SurfacesNoAlert()
    {
        using var page = Factory.Create(page: (_, _, _) => Factory.WithChildren(
            Factory.Row(2, "Classifying", parentId: 1)));

        Assert.False(page.Rows.Single().HasChildAlert);
        Assert.Null(page.Rows.Single().ChildAlertSeverity);
    }

    [Fact]
    public void LoadOlder_AppendsAndAdvancesTheCursor()
    {
        var first = new ActivityCursor(Factory.Noon, 2);
        var second = new ActivityCursor(Factory.Noon.AddMinutes(-5), 4);
        var seen = new List<ActivityCursor?>();
        using var page = Factory.Create(page: (_, cursor, _) =>
        {
            seen.Add(cursor);
            return seen.Count == 1
                ? Factory.Page([Factory.Row(1), Factory.Row(2)], next: first, total: 4)
                : Factory.Page([Factory.Row(3), Factory.Row(4)], next: second, total: 4);
        });
        Assert.Equal(first, page.NextCursor);

        page.LoadOlderCommand.Execute(null);
        Factory.Settle(page);

        Assert.Equal([null, first], seen);
        Assert.Equal([1, 2, 3, 4], page.Rows.Select(row => row.Id));
        Assert.Equal(second, page.NextCursor);
        Assert.True(page.CanLoadOlder);
    }

    [Fact]
    public void LoadOlder_WithNoCursor_IsRefusedInTheCommandBody()
    {
        // RelayCommand.Execute ignores CanExecute (TRACKING section 6 item 13), so the guard has
        // to be in the body too: without it this would re-fetch page one and append it to itself.
        var loads = 0;
        using var page = Factory.Create(page: (_, _, _) =>
        {
            loads++;
            return Factory.Page();
        });

        Assert.Null(page.NextCursor);
        Assert.False(page.LoadOlderCommand.CanExecute(null));
        page.LoadOlderCommand.Execute(null);
        Factory.Settle(page);

        Assert.Equal(1, loads);
        Assert.Single(page.Rows);
    }

    [Fact]
    public void LoadOlder_WhileLoading_IsRefusedInTheCommandBody()
    {
        using var gate = new ManualResetEventSlim(false);
        using var started = new ManualResetEventSlim(false);
        var loads = 0;
        var cursor = new ActivityCursor(Factory.Noon, 1);
        var page = Factory.Create(page: (_, _, _) =>
        {
            if (Interlocked.Increment(ref loads) > 1)
            {
                started.Set();
                gate.Wait(TimeSpan.FromSeconds(30));
            }

            return Factory.Page(next: cursor);
        });

        page.LoadOlderCommand.Execute(null);
        started.Wait(TimeSpan.FromSeconds(30));
        Assert.True(page.IsLoading);
        Assert.False(page.LoadOlderCommand.CanExecute(null));

        page.LoadOlderCommand.Execute(null);
        Assert.Equal(2, loads);

        gate.Set();
        Factory.Settle(page);
        page.Dispose();
    }

    [Fact]
    public void ChangingTheSeverityFilter_ReloadsFromTheStart()
    {
        var calls = new List<(ActivityFilters Filters, ActivityCursor? Cursor)>();
        using var page = Factory.Create(page: (filters, cursor, _) =>
        {
            calls.Add((filters, cursor));
            return Factory.Page(next: new ActivityCursor(Factory.Noon, 1));
        });

        page.SelectSeverityCommand.Execute(page.SeverityOptions[2]);
        Factory.Settle(page);

        Assert.Equal("warning", page.SeverityFilter);
        Assert.Equal(2, calls.Count);
        Assert.Equal("warning", calls[1].Filters.Severity);

        // From the start: the previous filter's cursor is not carried into the new ordering.
        Assert.Null(calls[1].Cursor);

        // The pill set is single-select, "all" included.
        Assert.Equal(["warn"], page.SeverityOptions.Where(o => o.IsSelected).Select(o => o.Label));
    }

    [Fact]
    public void ChangingTheCategoryFilter_ReloadsFromTheStart()
    {
        var calls = new List<(ActivityFilters Filters, ActivityCursor? Cursor)>();
        using var page = Factory.Create(page: (filters, cursor, _) =>
        {
            calls.Add((filters, cursor));
            return Factory.Page(next: new ActivityCursor(Factory.Noon, 1));
        });

        var system = page.CategoryOptions.Single(option => option.Key == "system");
        page.SelectCategoryCommand.Execute(system);
        Factory.Settle(page);

        Assert.Equal("system", page.CategoryFilter);
        Assert.Equal(2, calls.Count);
        Assert.Equal("system", calls[1].Filters.Category);
        Assert.Null(calls[1].Cursor);
        Assert.Equal("sys", system.Label);
    }

    [Fact]
    public void SelectingTheAllPill_ClearsTheFilter()
    {
        using var page = Factory.Create();

        page.SelectSeverityCommand.Execute(page.SeverityOptions[3]);
        Factory.Settle(page);
        Assert.Equal("error", page.SeverityFilter);

        page.SelectSeverityCommand.Execute(page.SeverityOptions[0]);
        Factory.Settle(page);

        Assert.Null(page.SeverityFilter);
        Assert.True(page.SeverityOptions[0].IsSelected);
    }

    [Fact]
    public async Task Search_IsDebouncedBeforeItReloads()
    {
        var delay = new Factory.ManualDelay();
        var calls = new List<ActivityFilters>();
        using var page = Factory.Create(
            page: (filters, _, _) =>
            {
                calls.Add(filters);
                return Factory.Page();
            },
            delay: delay.Wait);
        Assert.Single(calls);

        page.SearchText = "thumb";

        // The window is open and nothing has re-read yet.
        Assert.Equal(1, delay.Calls);
        Assert.Single(calls);
        Assert.Equal(ActivityViewModel.DebounceWindow, delay.LastWindow);

        delay.Elapse();
        if (page.PendingSearch is { } pending)
        {
            await pending;
        }

        await Factory.SettleAsync(page);

        Assert.Equal(2, calls.Count);
        Assert.Equal("thumb", calls[1].Search);
    }

    [Fact]
    public async Task Search_KeystrokesRestartTheWindow_AndOnlyTheLastTermReloads()
    {
        var delay = new Factory.ManualDelay();
        var calls = new List<ActivityFilters>();
        using var page = Factory.Create(
            page: (filters, _, _) =>
            {
                calls.Add(filters);
                return Factory.Page();
            },
            delay: delay.Wait);

        page.SearchText = "t";
        page.SearchText = "th";
        page.SearchText = "thu";
        Assert.Equal(3, delay.Calls);
        Assert.Single(calls);

        delay.Elapse();
        if (page.PendingSearch is { } pending)
        {
            await pending;
        }

        await Factory.SettleAsync(page);

        Assert.Equal(2, calls.Count);
        Assert.Equal("thu", calls[1].Search);
    }

    [Fact]
    public void PruneNow_CallsPruneWithTheConfiguredRetention_AndReportsTheCount()
    {
        var requested = new List<int>();
        using var page = Factory.Create(
            retentionDays: () => 45,
            pruneNow: days =>
            {
                requested.Add(days);
                return 12;
            });

        page.PruneNowCommand.Execute(null);
        Factory.Settle(page);

        Assert.Equal([45], requested);
        Assert.Equal("Pruned 12 entries older than 45 days.", page.PruneSummary);
        Assert.False(page.IsPruning);
    }

    [Fact]
    public void PruneNow_WithNothingToDelete_SaysSo()
    {
        using var page = Factory.Create(retentionDays: () => 90, pruneNow: _ => 0);

        page.PruneNowCommand.Execute(null);
        Factory.Settle(page);

        Assert.Equal("Nothing to prune: no activity is older than 90 days.", page.PruneSummary);
    }

    [Fact]
    public void PruneNow_OfOneEntry_IsSingular()
    {
        using var page = Factory.Create(retentionDays: () => 90, pruneNow: _ => 1);

        page.PruneNowCommand.Execute(null);
        Factory.Settle(page);

        Assert.Equal("Pruned 1 entry older than 90 days.", page.PruneSummary);
    }

    [Fact]
    public void PruneNow_ReloadsTheList()
    {
        var loads = 0;
        using var page = Factory.Create(
            page: (_, _, _) =>
            {
                loads++;
                return Factory.Page();
            },
            pruneNow: _ => 3);

        page.PruneNowCommand.Execute(null);
        Factory.Settle(page);

        Assert.Equal(2, loads);
    }

    // TRACKING section 2 item 8 and FIXER item 11: this awaits the work rather than blocking on
    // it. The headless UI thread is a thread-pool thread, so a blocking wait would inline the
    // delete onto it under pool saturation and the assertion would pass for the wrong reason.
    [Fact]
    public async Task PruneNow_RunsOffTheUiThread()
    {
        var callerThread = Environment.CurrentManagedThreadId;
        var pruneThread = 0;
        using var page = Factory.Create(pruneNow: _ =>
        {
            pruneThread = Environment.CurrentManagedThreadId;
            return 4;
        });

        page.PruneNowCommand.Execute(null);
        if (page.PendingPrune is { } prune)
        {
            await prune;
        }

        await Factory.SettleAsync(page);

        Assert.NotEqual(0, pruneThread);
        Assert.NotEqual(callerThread, pruneThread);
    }

    [Fact]
    public void PruneNow_WhileRunning_IsRefusedInTheCommandBody()
    {
        using var gate = new ManualResetEventSlim(false);
        using var started = new ManualResetEventSlim(false);
        var prunes = 0;
        var page = Factory.Create(pruneNow: _ =>
        {
            Interlocked.Increment(ref prunes);
            started.Set();
            gate.Wait(TimeSpan.FromSeconds(30));
            return 1;
        });

        page.PruneNowCommand.Execute(null);
        started.Wait(TimeSpan.FromSeconds(30));
        Assert.True(page.IsPruning);
        Assert.False(page.PruneNowCommand.CanExecute(null));

        page.PruneNowCommand.Execute(null);
        Assert.Equal(1, prunes);

        gate.Set();
        Factory.Settle(page);
        page.Dispose();
    }

    [Fact]
    public void PruneNow_ThatThrows_ReportsItAndDoesNotTakeThePageDown()
    {
        using var page = Factory.Create(pruneNow: _ => throw new InvalidOperationException("locked"));

        page.PruneNowCommand.Execute(null);
        Factory.Settle(page);

        Assert.Equal("The activity log could not be pruned. See the log for details.", page.PruneSummary);
        Assert.False(page.IsPruning);
    }

    [Fact]
    public void EmptyLog_ShowsTheSpecEmptyState()
    {
        using var page = Factory.Create(page: (_, _, _) => Factory.Empty());

        Assert.True(page.IsEmpty);
        Assert.False(page.LoadFailed);
        Assert.False(page.ShowList);
        Assert.Equal("No activity recorded.", ActivityViewModel.EmptyStateText);
        Assert.Equal("0 events", page.TotalText);
    }

    [Fact]
    public void FailedLoad_ShowsAFailureLine_NotAnEmptyState()
    {
        using var page = Factory.Create(
            page: (_, _, _) => throw new InvalidOperationException("no database"));

        // A failed read taught nothing about whether the log is empty.
        Assert.True(page.LoadFailed);
        Assert.False(page.IsEmpty);
        Assert.False(page.IsLoading);
        Assert.False(page.ShowList);
        Assert.Equal("The activity log could not be loaded.", ActivityViewModel.FailureText);
    }

    [Fact]
    public async Task ScanFinished_ReloadsOnce()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        using var status = new ScanStatusService(coordinator, action => action());
        var loads = 0;
        using var page = Factory.Create(
            page: (_, _, _) =>
            {
                Interlocked.Increment(ref loads);
                return Factory.Page();
            },
            scanStatus: status);
        Assert.Equal(1, loads);

        await RaiseScanFinishedAsync(coordinator);
        await Factory.SettleAsync(page);

        Assert.Equal(2, loads);
    }

    [Fact]
    public async Task ScanFinished_IsTheOnlyRefreshTrigger_ThereIsNoPoll()
    {
        // Asserted two ways. By construction: the page takes a ScanStatusService and nothing else
        // that could push at it, so a direct ScanCoordinator subscription is not expressible and
        // there is no timer parameter. And by behaviour: a stream of progress envelopes, which is
        // what a running scan pushes, reloads nothing; only the finish does.
        var parameters = typeof(ActivityViewModel).GetConstructors().Single().GetParameters();
        Assert.DoesNotContain(
            parameters,
            parameter => parameter.ParameterType.Name.Contains("ScanCoordinator", StringComparison.Ordinal));
        Assert.Contains(parameters, parameter => parameter.ParameterType == typeof(ScanStatusService));

        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        using var status = new ScanStatusService(coordinator, action => action());
        var loads = 0;
        using var page = Factory.Create(
            page: (_, _, _) =>
            {
                Interlocked.Increment(ref loads);
                return Factory.Page();
            },
            scanStatus: status);

        for (var i = 0; i < 5; i++)
        {
            coordinator.RaiseProgress("classify", i, 5, $"Classifying {i}/5", force: true);
        }

        await Factory.SettleAsync(page);
        Assert.Equal(1, loads);

        await RaiseScanFinishedAsync(coordinator);
        await Factory.SettleAsync(page);
        Assert.Equal(2, loads);
    }

    [Fact]
    public async Task Dispose_UnsubscribesFromScanFinished()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        using var status = new ScanStatusService(coordinator, action => action());
        var loads = 0;
        var page = Factory.Create(
            page: (_, _, _) =>
            {
                Interlocked.Increment(ref loads);
                return Factory.Page();
            },
            scanStatus: status);

        page.Dispose();
        await RaiseScanFinishedAsync(coordinator);

        Assert.Equal(1, loads);
    }

    [Theory]
    [InlineData(true, "12:34")]
    [InlineData(false, "12:34 PM")]
    public void Row_TimeText_UsesTheConfiguredTimezoneAndClock(bool use24Hour, string expected)
    {
        var instant = new DateTime(2025, 3, 4, 12, 34, 0, DateTimeKind.Utc);
        using var page = Factory.Create(
            page: (_, _, _) => Factory.Page([Factory.Row(1, timestamp: instant)]),
            general: Factory.Settings(use24Hour: use24Hour));

        var row = page.Rows.Single();

        Assert.Equal(expected, row.TimeText);
        Assert.Equal("2025-03-04", row.DateText);
    }

    [Fact]
    public void Row_SeverityBrushes_AreImmutableSolidColorBrushes()
    {
        using var page = Factory.Create(page: (_, _, _) => Factory.Page(
        [
            Factory.Row(1, severity: "info"),
            Factory.Row(2, severity: "warning"),
            Factory.Row(3, severity: "error"),
        ]));

        Assert.All(page.Rows, row => Assert.IsType<ImmutableSolidColorBrush>(row.SeverityBrush));

        // Three distinct glyphs, and the icon's tooltip is the raw severity string.
        Assert.Equal(3, page.Rows.Select(row => row.SeverityGlyph).Distinct().Count());
        Assert.Equal(["info", "warning", "error"], page.Rows.Select(row => row.SeverityTitle));
    }

    [Theory]
    [InlineData("scan", "scan")]
    [InlineData("rebuild", "reb")]
    [InlineData("thumbnail", "thumb")]
    [InlineData("enrichment", "enrich")]
    [InlineData("migration", "migr")]
    [InlineData("user_action", "user")]
    [InlineData("system", "sys")]
    [InlineData("something_new", "something_new")]
    public void Row_CategoryLabel_IsTheWebAbbreviation(string category, string expected)
    {
        using var page = Factory.Create(
            page: (_, _, _) => Factory.Page([Factory.Row(1, category: category)]));

        Assert.Equal(expected, page.Rows.Single().CategoryLabel);
    }

    // questions.md Q20: milliseconds below one second, one-decimal seconds below one minute,
    // m:ss at or above. The 59,999 and 59,950 cases are review finding 10: the branch is taken on
    // the rounded value, so nothing renders "60.0 s", which is a time the next branch spells
    // differently.
    [Theory]
    [InlineData(0, "0 ms")]
    [InlineData(999, "999 ms")]
    [InlineData(1_000, "1.0 s")]
    [InlineData(1_450, "1.5 s")]
    [InlineData(59_900, "59.9 s")]
    [InlineData(59_950, "1:00")]
    [InlineData(59_999, "1:00")]
    [InlineData(60_000, "1:00")]
    [InlineData(91_000, "1:31")]
    [InlineData(3_600_000, "60:00")]
    public void Row_DurationText_FormatsMillisecondsSecondsAndMinutes(int durationMs, string expected)
        => Assert.Equal(expected, ActivityRowViewModel.FormatDuration(durationMs));

    [Fact]
    public void Row_DurationText_IsBlankWhenDurationIsNull()
    {
        using var page = Factory.Create(page: (_, _, _) => Factory.Page([Factory.Row(1)]));

        Assert.Equal("", page.Rows.Single().DurationText);
        Assert.Equal("", ActivityRowViewModel.FormatDuration(null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    [InlineData("""{"action":"open"}""")]
    public void Row_IsNotExpandable_WhenDetailsAreNull_OrEmpty(string? details)
    {
        using var page = Factory.Create(
            page: (_, _, _) => Factory.Page([Factory.Row(1, details: details)]));

        Assert.False(page.Rows.Single().IsExpandable);
    }

    [Fact]
    public void Row_IsNotExpandable_WhenTheOnlyDetailIsANumberAlreadyInTheMessage()
    {
        using var page = Factory.Create(page: (_, _, _) => Factory.Page(
            [Factory.Row(1, "Orphan rows pruned: 12", details: """{"count":12}""")]));

        Assert.False(page.Rows.Single().IsExpandable);
    }

    [Fact]
    public void Row_IsExpandable_WhenDetailsCarryTwoKeys()
    {
        using var page = Factory.Create(page: (_, _, _) => Factory.Page(
            [Factory.Row(1, "Activity log pruned", details: """{"deleted_count":12,"retention_days":90}""")]));

        var row = page.Rows.Single();

        Assert.True(row.IsExpandable);
        Assert.False(row.IsDetailsExpanded);

        row.ToggleDetailsCommand.Execute(null);

        Assert.True(row.IsDetailsExpanded);
        Assert.True(row.Details!.ShowTable);
    }

    [Fact]
    public void Row_TwoExpandersAreIndependent()
    {
        using var page = Factory.Create(page: (_, _, _) => Factory.Page(
            [Factory.Row(1, "Scan started (manual)", details: """{"trigger":"manual","roots":2}""")],
            new Dictionary<int, IReadOnlyList<ActivityRow>>
            {
                [1] = [Factory.Row(2, "Classifying", parentId: 1)],
            }));

        var row = page.Rows.Single();
        row.ToggleDetailsCommand.Execute(null);

        Assert.True(row.IsDetailsExpanded);
        Assert.False(row.AreChildrenExpanded);
    }

    // ---- review fix pass -------------------------------------------------------------------

    // Review finding 1. Every load sets IsLoading, so a ShowList gated on it collapsed the whole
    // feed to the loading line on Refresh, on Load older and on every post-scan reload.
    [Fact]
    public void Reload_KeepsTheRowsVisible_AndShowsNoLoadingLine()
    {
        using var gate = new ManualResetEventSlim(false);
        using var started = new ManualResetEventSlim(false);
        var loads = 0;
        var page = Factory.Create(page: (_, _, _) =>
        {
            if (Interlocked.Increment(ref loads) > 1)
            {
                started.Set();
                gate.Wait(TimeSpan.FromSeconds(30));
            }

            return Factory.Page([Factory.Row(1, "first")]);
        });
        Assert.True(page.ShowList);
        Assert.False(page.ShowLoadingLine);

        page.RefreshCommand.Execute(null);
        started.Wait(TimeSpan.FromSeconds(30));

        // Mid-reload: the rows are still on screen and the loading line is not.
        Assert.True(page.IsLoading);
        Assert.Single(page.Rows);
        Assert.True(page.ShowList);
        Assert.False(page.ShowLoadingLine);

        gate.Set();
        Factory.Settle(page);
        page.Dispose();
    }

    [Fact]
    public void FirstLoad_ShowsTheLoadingLine_WhileThereAreNoRows()
    {
        using var gate = new ManualResetEventSlim(false);
        using var started = new ManualResetEventSlim(false);
        var page = new ActivityViewModel(
            (_, _, _) =>
            {
                started.Set();
                gate.Wait(TimeSpan.FromSeconds(30));
                return Factory.Page();
            },
            () => 90,
            _ => 0,
            () => Factory.Settings(),
            post: action => action(),
            delay: (_, _) => Task.CompletedTask);

        started.Wait(TimeSpan.FromSeconds(30));

        Assert.True(page.ShowLoadingLine);
        Assert.False(page.ShowList);

        gate.Set();
        Factory.Settle(page);
        page.Dispose();
    }

    // Review finding 1, second half: a failed append reports beside the button and leaves the page
    // the reader already has exactly where it is.
    [Fact]
    public void LoadOlder_ThatFails_KeepsTheLoadedRows_AndReportsAnAppendFailure()
    {
        var loads = 0;
        using var page = Factory.Create(page: (_, _, _) =>
        {
            if (Interlocked.Increment(ref loads) > 1)
            {
                throw new InvalidOperationException("the database went away");
            }

            return Factory.Page([Factory.Row(1, "first"), Factory.Row(2, "second")],
                next: new ActivityCursor(Factory.Noon, 2), total: 9);
        });

        page.LoadOlderCommand.Execute(null);
        Factory.Settle(page);

        Assert.True(page.AppendFailed);
        Assert.Equal("Older activity could not be loaded.", ActivityViewModel.AppendFailureText);
        Assert.False(page.LoadFailed);
        Assert.True(page.ShowList);
        Assert.Equal(["first", "second"], page.Rows.Select(row => row.Message));
    }

    [Fact]
    public void ASuccessfulReload_ClearsAPreviousAppendFailure()
    {
        var loads = 0;
        using var page = Factory.Create(page: (_, _, _) =>
        {
            if (Interlocked.Increment(ref loads) == 2)
            {
                throw new InvalidOperationException("transient");
            }

            return Factory.Page(next: new ActivityCursor(Factory.Noon, 1));
        });

        page.LoadOlderCommand.Execute(null);
        Factory.Settle(page);
        Assert.True(page.AppendFailed);

        page.RefreshCommand.Execute(null);
        Factory.Settle(page);

        Assert.False(page.AppendFailed);
    }

    // Review finding 2. A scan's child count is unbounded by construction, so no view-model is
    // built for a child until the reader expands its parent.
    [Fact]
    public void ScanEvent_BuildsNoChildViewModels_UntilItIsExpanded()
    {
        using var page = Factory.Create(page: (_, _, _) => Factory.WithChildren(
            Factory.Row(2, "Classifying", parentId: 1),
            Factory.Row(3, "Orphans pruned", parentId: 1)));

        var scan = page.Rows.Single();
        Assert.Equal(2, scan.ChildCount);
        Assert.True(scan.HasChildren);
        Assert.Equal("2 sub-tasks", scan.SubTaskLabel);
        Assert.Empty(scan.Children);

        scan.ToggleChildrenCommand.Execute(null);

        Assert.Equal(2, scan.Children.Count);
    }

    [Fact]
    public void ScanEvent_CollapsingAndReExpanding_BuildsTheChildrenOnce()
    {
        using var page = Factory.Create(page: (_, _, _) => Factory.WithChildren(
            Factory.Row(2, "Classifying", parentId: 1)));

        var scan = page.Rows.Single();
        scan.ToggleChildrenCommand.Execute(null);
        var first = scan.Children[0];
        scan.ToggleChildrenCommand.Execute(null);
        scan.ToggleChildrenCommand.Execute(null);

        Assert.Single(scan.Children);
        Assert.Same(first, scan.Children[0]);
    }

    // Review finding 11, ruled: the run's duration reaches the collapsed parent, because the only
    // writer of duration_ms is the terminal scan event and that carries parent_id.
    [Fact]
    public void ScanEvent_SurfacesTheTerminalChildsDuration_OnTheCollapsedParent()
    {
        using var page = Factory.Create(page: (_, _, _) => Factory.WithChildren(
            Factory.Row(2, "Classifying", parentId: 1),
            Factory.Row(3, "Scan complete (12 new)", eventType: "scan_complete", parentId: 1, durationMs: 91_000)));

        var scan = page.Rows.Single();

        Assert.True(scan.HasChildDuration);
        Assert.Equal("1:31", scan.ChildDurationText);
        Assert.False(scan.AreChildrenExpanded);

        // The parent's own duration_ms is still null, which is what the column reflects.
        Assert.Equal("", scan.DurationText);
    }

    [Fact]
    public void ARowWithNoChildDuration_SurfacesNone()
    {
        using var page = Factory.Create(page: (_, _, _) => Factory.WithChildren(
            Factory.Row(2, "Classifying", parentId: 1)));

        Assert.False(page.Rows.Single().HasChildDuration);
        Assert.Equal("", page.Rows.Single().ChildDurationText);
    }

    // Review finding 8: the details document is parsed on the first expand, not when the row is
    // built, because the view binds the nullable property rather than a lazy getter.
    [Fact]
    public void Row_ParsesItsDetails_OnlyOnTheFirstExpand()
    {
        using var page = Factory.Create(page: (_, _, _) => Factory.Page(
            [Factory.Row(1, "Activity log pruned", details: """{"deleted_count":12,"retention_days":90}""")]));

        var row = page.Rows.Single();
        Assert.Null(row.Details);

        row.ToggleDetailsCommand.Execute(null);
        var built = row.Details;
        Assert.NotNull(built);
        Assert.True(built!.ShowTable);

        // Collapsing and reopening keeps the parsed document.
        row.ToggleDetailsCommand.Execute(null);
        row.ToggleDetailsCommand.Execute(null);
        Assert.Same(built, row.Details);
    }

    [Fact]
    public void Row_ThatIsNotExpandable_NeverParsesItsDetails()
    {
        using var page = Factory.Create(page: (_, _, _) => Factory.Page(
            [Factory.Row(1, "Orphan rows pruned: 12", details: """{"count":12}""")]));

        var row = page.Rows.Single();
        row.ToggleDetailsCommand.Execute(null);

        Assert.False(row.IsExpandable);
        Assert.Null(row.Details);
        Assert.False(row.IsDetailsExpanded);
    }

    // Review finding 4: every other theme-brush holder in the application rebuilds on
    // ChartTheme.Changed, so the severity glyphs repaint with the text around them.
    [AvaloniaFact]
    public void ThemeChange_RebuildsTheSeverityBrushes_AndRaisesOnTheRows()
    {
        ChartTheme.Apply();
        using var page = Factory.Create(page: (_, _, _) => Factory.WithChildren(
            Factory.Row(2, "A file was rejected", severity: "warning", parentId: 1)));
        var row = page.Rows.Single();
        row.ToggleChildrenCommand.Execute(null);
        var child = row.Children.Single();

        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        var childRaised = new List<string?>();
        child.PropertyChanged += (_, e) => childRaised.Add(e.PropertyName);

        ChartTheme.Apply();

        Assert.Contains(nameof(ActivityRowViewModel.SeverityBrush), raised);
        Assert.Contains(nameof(ActivityRowViewModel.ChildAlertBrush), raised);
        Assert.Contains(nameof(ActivityRowViewModel.SeverityBrush), childRaised);
        Assert.IsType<ImmutableSolidColorBrush>(row.SeverityBrush);
    }

    [AvaloniaFact]
    public void Dispose_UnsubscribesFromTheThemeChange()
    {
        ChartTheme.Apply();
        var page = Factory.Create();
        var row = page.Rows.Single();
        var raised = 0;
        row.PropertyChanged += (_, _) => raised++;

        page.Dispose();
        ChartTheme.Apply();

        Assert.Equal(0, raised);
    }

    // Review finding 13: a slow load landing after a filter change must not overwrite the newer
    // page. The first read is gated open until after the filter has changed.
    [Fact]
    public void AStaleLoad_LandingAfterAFilterChange_DoesNotOverwriteTheNewerPage()
    {
        using var gate = new ManualResetEventSlim(false);
        using var started = new ManualResetEventSlim(false);
        var calls = 0;
        var page = new ActivityViewModel(
            (filters, _, _) =>
            {
                if (Interlocked.Increment(ref calls) == 2)
                {
                    started.Set();
                    gate.Wait(TimeSpan.FromSeconds(30));
                    return Factory.Page([Factory.Row(1, "stale")]);
                }

                return Factory.Page([Factory.Row(2, filters.Severity ?? "fresh")]);
            },
            () => 90,
            _ => 0,
            () => Factory.Settings(),
            post: action => action(),
            delay: (_, _) => Task.CompletedTask);
        Factory.Settle(page);

        // Open a read that will land late, then change the filter, which opens a newer one.
        page.SelectSeverityCommand.Execute(page.SeverityOptions[1]);
        started.Wait(TimeSpan.FromSeconds(30));
        page.SelectSeverityCommand.Execute(page.SeverityOptions[3]);
        Factory.Settle(page);
        gate.Set();
        Factory.Settle(page);

        Assert.Equal(["error"], page.Rows.Select(row => row.Message));
        Assert.DoesNotContain("stale", page.Rows.Select(row => row.Message));
        page.Dispose();
    }

    // Review finding 13, second half: Dispose cancels an in-flight load, so nothing is published
    // against a page whose database is going away.
    [Fact]
    public void Dispose_DuringAnInFlightLoad_PublishesNothingAfterwards()
    {
        using var gate = new ManualResetEventSlim(false);
        using var started = new ManualResetEventSlim(false);
        var calls = 0;
        var page = new ActivityViewModel(
            (_, _, _) =>
            {
                if (Interlocked.Increment(ref calls) > 1)
                {
                    started.Set();
                    gate.Wait(TimeSpan.FromSeconds(30));
                    return Factory.Page([Factory.Row(9, "late")]);
                }

                return Factory.Empty();
            },
            () => 90,
            _ => 0,
            () => Factory.Settings(),
            post: action => action(),
            delay: (_, _) => Task.CompletedTask);
        Factory.Settle(page);

        page.RefreshCommand.Execute(null);
        started.Wait(TimeSpan.FromSeconds(30));

        page.Dispose();
        gate.Set();
        Factory.Settle(page);

        Assert.Empty(page.Rows);
    }
}
