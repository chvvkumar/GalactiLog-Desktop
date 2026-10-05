using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.Data.Tests.Queries;

// Phase 9 Task 4. Spec 12.6's read, mirroring backend/app/api/activity.py::list_activity. The
// roadmap's row 4 Verify line names one of these by behaviour: "keyset paging returns no
// duplicates and no gaps across pages", which is
// Paging_AcrossEveryPage_ReturnsNoDuplicatesAndNoGaps and its identical-timestamp sibling.
//
// These seed activity_events directly rather than running a scan: the contract under test is the
// reader's, and a scan would pin it to whatever the writer happens to emit today.
public class ActivityQueryTests : IDisposable
{
    private readonly TestDatabaseHandle _db = TestDatabaseFactory.CreateMigratedDatabase();

    private static readonly DateTime Noon = new(2025, 3, 4, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose() => _db.Dispose();

    private string Cs => _db.ConnectionString;

    private ActivityQuery Query() => new(new DatabaseConnectionString(Cs));

    private GalactiLogContext Open()
        => new(GalactiLogContextOptions.Create(Cs, tracking: true));

    private int Add(
        string message,
        DateTime? timestamp = null,
        string severity = "info",
        string category = "scan",
        string eventType = "scan_started",
        string? details = null,
        int? parentId = null,
        int? durationMs = null)
    {
        using var context = Open();
        var row = new ActivityEvent
        {
            Timestamp = timestamp ?? Noon,
            Severity = severity,
            Category = category,
            EventType = eventType,
            Message = message,
            Details = details,
            ParentId = parentId,
            DurationMs = durationMs,
        };
        context.ActivityEvents.Add(row);
        context.SaveChanges();
        return row.Id;
    }

    // One event per minute, oldest first, so the ids ascend with the timestamps and the expected
    // descending page order is the reverse of the returned list.
    private List<int> AddSeries(int count, string prefix = "event")
        => [.. Enumerable.Range(0, count).Select(i => Add($"{prefix} {i}", Noon.AddMinutes(i)))];

    // The same series on one SaveChanges, for the counts big enough that a context per row would
    // dominate the test's runtime.
    private void AddManySeries(int count)
    {
        using var context = Open();
        for (var i = 0; i < count; i++)
        {
            context.ActivityEvents.Add(new ActivityEvent
            {
                Timestamp = Noon.AddMinutes(i),
                Severity = "info",
                Category = "scan",
                EventType = "scan_started",
                Message = $"bulk {i}",
            });
        }

        context.SaveChanges();
    }

    [Fact]
    public void Page_ReturnsTopLevelEventsOnly()
    {
        var parent = Add("Scan started (manual)");
        Add("Classifying", parentId: parent);
        Add("Orphans pruned", parentId: parent);

        var page = Query().Page(new ActivityFilters());

        Assert.Equal([parent], page.Rows.Select(row => row.Id));
        Assert.All(page.Rows, row => Assert.Null(row.ParentId));
    }

    [Fact]
    public void Page_OrdersByTimestampThenIdDescending()
    {
        var first = Add("oldest", Noon);
        var tieA = Add("tie a", Noon.AddMinutes(1));
        var tieB = Add("tie b", Noon.AddMinutes(1));

        var page = Query().Page(new ActivityFilters());

        Assert.Equal([tieB, tieA, first], page.Rows.Select(row => row.Id));
    }

    // Clamped, not rejected: the caller is a UI control, so a page size out of range is corrected
    // silently rather than made a reason to fail a read of the log.
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(500, 200)]
    [InlineData(int.MaxValue, 200)]
    public void Page_Limit_IsClampedToOneThroughTwoHundred(int requested, int expected)
    {
        AddManySeries(205);

        Assert.Equal(expected, Query().Page(new ActivityFilters(), null, requested).Rows.Count);
    }

    [Fact]
    public void Page_NextCursor_IsNullWhenFewerRowsThanTheLimitCameBack()
    {
        AddSeries(3);

        Assert.Null(Query().Page(new ActivityFilters(), null, 10).NextCursor);
    }

    [Fact]
    public void Page_NextCursor_IsTheLastRowWhenThePageIsFull()
    {
        var ids = AddSeries(5);

        var page = Query().Page(new ActivityFilters(), null, 3);
        var cursor = page.NextCursor;

        Assert.NotNull(cursor);
        Assert.Equal(page.Rows[^1].Id, cursor!.Id);
        Assert.Equal(page.Rows[^1].Timestamp, cursor.Timestamp);

        // Newest first, so a page of three from five ends on the third newest.
        Assert.Equal(ids[2], cursor.Id);
    }

    // The roadmap's named assertion.
    [Fact]
    public void Paging_AcrossEveryPage_ReturnsNoDuplicatesAndNoGaps()
    {
        var ids = AddSeries(23);
        var expected = ids.AsEnumerable().Reverse().ToList();

        var seen = Walk(new ActivityFilters(), limit: 5);

        Assert.Equal(expected, seen);
        Assert.Equal(expected.Count, seen.Distinct().Count());
    }

    // The case a cursor on timestamp alone loses or repeats. Seven rows share one instant, and the
    // page size of three puts a page boundary inside that block twice.
    [Fact]
    public void Paging_WithIdenticalTimestamps_StillReturnsNoDuplicates()
    {
        var before = Add("newer", Noon.AddMinutes(1));
        var tied = Enumerable.Range(0, 7).Select(i => Add($"tied {i}", Noon)).ToList();
        var after = Add("older", Noon.AddMinutes(-1));

        var expected = new List<int> { before };
        expected.AddRange(Enumerable.Reverse(tied));
        expected.Add(after);

        var seen = Walk(new ActivityFilters(), limit: 3);

        Assert.Equal(expected, seen);
        Assert.Equal(expected.Count, seen.Distinct().Count());
    }

    [Fact]
    public void Page_Total_IsTheFullFilteredCount_NotTheRemainingCount()
    {
        AddSeries(9);

        var first = Query().Page(new ActivityFilters(), null, 4);
        var second = Query().Page(new ActivityFilters(), first.NextCursor, 4);

        Assert.Equal(9, first.Total);
        Assert.Equal(9, second.Total);
    }

    [Fact]
    public void Page_SeverityFilter_NarrowsTheList()
    {
        Add("info one");
        Add("warned", severity: "warning");
        Add("failed", severity: "error");

        var page = Query().Page(new ActivityFilters(Severity: "warning"));

        Assert.Equal(["warned"], page.Rows.Select(row => row.Message));
        Assert.Equal(1, page.Total);
    }

    [Fact]
    public void Page_CategoryFilter_NarrowsTheList()
    {
        Add("scan one");
        Add("housekeeping", category: "system", eventType: "activity_pruned");
        Add("renamed", category: "user_action", eventType: "target_renamed");

        var page = Query().Page(new ActivityFilters(Category: "system"));

        Assert.Equal(["housekeeping"], page.Rows.Select(row => row.Message));
    }

    [Theory]
    [InlineData("critical")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("mosaic")]
    public void Page_UnknownSeverity_IsIgnoredRatherThanRejected(string severity)
    {
        Add("info one");
        Add("warned", severity: "warning");

        var page = Query().Page(new ActivityFilters(Severity: severity));

        Assert.Equal(2, page.Rows.Count);
    }

    // mosaic is a real web category the port drops (spec 5.12, 19.1), so it must behave like any
    // other unrecognized value: no filter at all, never an empty list.
    [Fact]
    public void Page_UnknownCategory_IsIgnoredRatherThanRejected()
    {
        Add("scan one");
        Add("housekeeping", category: "system");

        Assert.Equal(2, Query().Page(new ActivityFilters(Category: "mosaic")).Rows.Count);
    }

    [Fact]
    public void Page_Search_MatchesOnTheMessage_CaseInsensitively()
    {
        Add("Scan complete (12 new)");
        Add("Reference thumbnails: 4 generated");

        var page = Query().Page(new ActivityFilters(Search: "THUMBNAIL"));

        Assert.Equal(["Reference thumbnails: 4 generated"], page.Rows.Select(row => row.Message));
        Assert.Equal(1, page.Total);
    }

    [Theory]
    [InlineData("o'brien")]
    [InlineData("100%")]
    [InlineData("\"quoted\"")]
    [InlineData("a; DROP TABLE activity_events; --")]
    public void Page_Search_BindsTheTerm(string term)
    {
        Add("nothing to match");

        // The point is that no term can break or extend the statement: the read succeeds and the
        // table is still there afterwards.
        var page = Query().Page(new ActivityFilters(Search: term));

        Assert.Empty(page.Rows);
        Assert.Equal(1, Query().Page(new ActivityFilters()).Total);
    }

    [Fact]
    public void Page_Children_AreReturnedForEveryParentInThePage_OrderedAscending()
    {
        var first = Add("Scan A", Noon);
        var second = Add("Scan B", Noon.AddMinutes(5));
        var firstChildLate = Add("A late", Noon.AddMinutes(2), parentId: first);
        var firstChildEarly = Add("A early", Noon.AddMinutes(1), parentId: first);
        var secondChild = Add("B only", Noon.AddMinutes(6), parentId: second);

        var page = Query().Page(new ActivityFilters());

        Assert.Equal([firstChildEarly, firstChildLate], page.Children[first].Select(row => row.Id));
        Assert.Equal([secondChild], page.Children[second].Select(row => row.Id));
        Assert.All(page.Children[first], row => Assert.Equal(first, row.ParentId));
    }

    // "Fetched in one batch" is asserted on the statement the query builds, which is stronger than
    // a review note and needs no mutable counter on a query that is a DI singleton: one statement,
    // one IN list, one bound parameter per id.
    [Fact]
    public void Page_Children_AreFetchedInOneBatch()
    {
        var parameters = new SqlParameters();

        var sql = ActivityQuery.BuildChildStatement(parameters, [7, 11, 13]);

        Assert.Equal(1, CountOccurrences(sql, "SELECT"));
        Assert.Contains("parent_id IN (@p0, @p1, @p2)", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("7", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("11", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_Children_AreNotFilteredBySeverityOrCategory()
    {
        var parent = Add("Scan started (manual)");
        var warned = Add("A file was rejected", severity: "warning", eventType: "file_rejected", parentId: parent);
        var thumbnails = Add("Reference thumbnails", category: "thumbnail", parentId: parent);

        var page = Query().Page(new ActivityFilters(Severity: "info", Category: "scan"));

        Assert.Equal([parent], page.Rows.Select(row => row.Id));
        Assert.Equal([warned, thumbnails], page.Children[parent].Select(row => row.Id).Order());
    }

    [Fact]
    public void Page_Children_MayExceedTheLimit()
    {
        var parent = Add("Scan started (manual)");
        for (var i = 0; i < 9; i++)
        {
            Add($"step {i}", Noon.AddSeconds(i), parentId: parent);
        }

        var page = Query().Page(new ActivityFilters(), null, 2);

        Assert.Single(page.Rows);
        Assert.Equal(9, page.Children[parent].Count);
    }

    [Fact]
    public void Page_EmptyResult_RunsNoChildQuery()
    {
        var parent = Add("Scan started (manual)");
        Add("step", parentId: parent);

        // Nothing matches, so there is no id list to build and no statement to run. Asserted
        // through the result, because an IN () list is not valid SQLite: if the child read ran at
        // all on an empty page it would throw rather than return this.
        var page = Query().Page(new ActivityFilters(Search: "no such message"));

        Assert.Empty(page.Rows);
        Assert.Empty(page.Children);
        Assert.Equal(0, page.Total);
    }

    [Fact]
    public void Page_BindsEveryOperand()
    {
        var parameters = new SqlParameters();

        var sql = ActivityQuery.BuildPageStatement(
            parameters,
            new ActivityFilters(Severity: "error", Category: "scan", Search: "boom"),
            new ActivityCursor(Noon, 42),
            50);

        Assert.DoesNotContain("error", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("scan", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("boom", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("42", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("2025", sql, StringComparison.Ordinal);

        // The wildcards are concatenated in SQL around the bound term, never built in C#.
        Assert.Contains("message LIKE '%' || @p", sql, StringComparison.Ordinal);

        // The keyset predicate is the pair, not the id alone.
        Assert.Contains("timestamp < @p", sql, StringComparison.Ordinal);
        Assert.Contains("timestamp = @p", sql, StringComparison.Ordinal);
        Assert.Contains("AND id < @p", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Total_IsComputedWithoutTheCursorClause()
    {
        var parameters = new SqlParameters();

        var sql = ActivityQuery.BuildTotalStatement(
            parameters, new ActivityFilters(Severity: "error"));

        Assert.Contains("parent_id IS NULL", sql, StringComparison.Ordinal);
        Assert.Contains("severity = @p", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("timestamp <", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_ReadsEveryColumn()
    {
        var targetId = LibrarySeeder.AddTarget(Cs, "Andromeda").Id;
        using (var context = Open())
        {
            context.ActivityEvents.Add(new ActivityEvent
            {
                Timestamp = Noon,
                Severity = "error",
                Category = "scan",
                EventType = "scan_failed",
                Message = "Scan failed: disk full",
                Details = """{"error":"disk full"}""",
                TargetId = targetId,
                DurationMs = 91_000,
            });
            context.SaveChanges();
        }

        var row = Query().Page(new ActivityFilters()).Rows.Single();

        Assert.Equal(Noon, row.Timestamp);
        Assert.Equal(DateTimeKind.Utc, row.Timestamp.Kind);
        Assert.Equal("error", row.Severity);
        Assert.Equal("scan", row.Category);
        Assert.Equal("scan_failed", row.EventType);
        Assert.Equal("Scan failed: disk full", row.Message);
        Assert.Equal("""{"error":"disk full"}""", row.Details);
        Assert.Equal(targetId, row.TargetId);
        Assert.Equal(91_000, row.DurationMs);
        Assert.Null(row.ParentId);
    }

    private List<int> Walk(ActivityFilters filters, int limit)
    {
        var query = Query();
        var seen = new List<int>();
        ActivityCursor? cursor = null;

        // Bounded so a broken cursor fails the assertion rather than hanging the suite.
        for (var page = 0; page < 50; page++)
        {
            var result = query.Page(filters, cursor, limit);
            seen.AddRange(result.Rows.Select(row => row.Id));
            cursor = result.NextCursor;
            if (cursor is null)
            {
                break;
            }
        }

        Assert.Null(cursor);
        return seen;
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = text.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    // Phase 14B Task 7 (PAR-017, spec 12.6). CountUnseen, the rail badge's count: review P2-1.

    [Fact]
    public void CountUnseen_WithANullMarker_CountsEveryTopLevelRow()
    {
        Add("first", Noon);
        Add("second", Noon.AddMinutes(1));

        Assert.Equal(2, Query().CountUnseen(null));
    }

    [Fact]
    public void CountUnseen_WithAMarkerBetweenTwoRows_CountsOnlyTheNewer()
    {
        Add("older", Noon);
        Add("newer", Noon.AddMinutes(10));
        var marker = Noon.AddMinutes(5);

        Assert.Equal(1, Query().CountUnseen(marker));
    }

    [Fact]
    public void CountUnseen_ExcludesAChildRow()
    {
        var parent = Add("Scan started (manual)", Noon);
        Add("Classifying", Noon.AddMinutes(5), parentId: parent);

        // Only the top-level row counts; the child, however new, does not (spec 12.6: the marker
        // is over activity_events top-level rows, the same parent_id IS NULL scope Page uses).
        Assert.Equal(1, Query().CountUnseen(null));
    }

    [Fact]
    public void CountUnseen_ARowExactlyOnTheMarker_IsNotCounted()
    {
        Add("on the marker", Noon);

        // Strictly newer, not newer-or-equal (spec 12.6): a row timestamped exactly at the marker
        // is seen, not unseen.
        Assert.Equal(0, Query().CountUnseen(Noon));
    }
}
