using System.Text.RegularExpressions;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.Data.Tests.Queries;

// Phase 14B Task 6: spec 12.2's Filters column. SessionSummary.Filters is the intersection of the
// second pass's filter distribution (no date) and its session summaries (no filter), so it is a
// fifth result set on the one enrichment command rather than a second round trip.
public class SessionFiltersTests
{
    private static readonly DateOnly Day = new(2025, 3, 1);
    private static readonly DateOnly OtherDay = new(2025, 3, 2);

    private sealed class Library : IDisposable
    {
        private readonly TestDatabaseHandle _db;
        private readonly AliasMapCache _aliases;

        private Library(TestDatabaseHandle db)
        {
            _db = db;
            Settings = new SettingsStore(new SettingsRepository(db.ConnectionString));
            _aliases = new AliasMapCache(Settings);
            Query = new TargetListingQuery(new DatabaseConnectionString(db.ConnectionString), _aliases);
        }

        public string ConnectionString => _db.ConnectionString;
        public SettingsStore Settings { get; }
        public TargetListingQuery Query { get; }

        public static Library Empty() => new(TestDatabaseFactory.CreateMigratedDatabase());

        public Guid AddTarget(string primaryName) => LibrarySeeder.AddTarget(ConnectionString, primaryName).Id;

        public void AddFrame(Guid? targetId, DateOnly sessionDate, Action<Data.Entities.Image>? configure = null)
            => LibrarySeeder.AddFrame(ConnectionString, targetId, sessionDate, configure);

        public void Dispose()
        {
            _aliases.Dispose();
            _db.Dispose();
        }
    }

    private static TargetListingCriteria Unsorted() => new() { Sort = TargetListingSort.Name, Descending = false };

    [Fact]
    public void EachSession_CarriesTheCanonicalFilterNamesOfItsLightFrames()
    {
        using var library = Library.Empty();
        var target = library.AddTarget("NGC 6960");
        library.AddFrame(target, Day, frame => frame.FilterUsed = "Ha");
        library.AddFrame(target, Day, frame => frame.FilterUsed = "OIII");

        var row = Assert.Single(library.Query.List(Unsorted()).Rows);
        var session = Assert.Single(row.Sessions);

        Assert.Equal(["Ha", "OIII"], session.Filters.Select(f => f.CanonicalName).OrderBy(n => n));
    }

    [Fact]
    public void TheNames_AreDeduplicated()
    {
        using var library = Library.Empty();
        var target = library.AddTarget("M 42");
        library.AddFrame(target, Day, frame => frame.FilterUsed = "L");
        library.AddFrame(target, Day, frame => frame.FilterUsed = "L");
        library.AddFrame(target, Day, frame => frame.FilterUsed = "L");

        var row = Assert.Single(library.Query.List(Unsorted()).Rows);
        var session = Assert.Single(row.Sessions);
        var badge = Assert.Single(session.Filters);

        Assert.Equal("L", badge.CanonicalName);
        Assert.Equal(3, badge.FrameCount);
    }

    [Fact]
    public void TheNames_AreInTheStoredFilterOrder()
    {
        using var library = Library.Empty();
        // Configured in the order Ha, OIII, L: the Filters tab's order, not alphabetical and not
        // the order the frames were inserted in.
        library.Settings.SaveFilters(new Dictionary<string, FilterSetting>
        {
            ["Ha"] = new() { Color = "#ff0000" },
            ["OIII"] = new() { Color = "#00ff00" },
            ["L"] = new() { Color = "#ffffff" },
        });
        var target = library.AddTarget("Veil");
        library.AddFrame(target, Day, frame => frame.FilterUsed = "L");
        library.AddFrame(target, Day, frame => frame.FilterUsed = "OIII");
        library.AddFrame(target, Day, frame => frame.FilterUsed = "Ha");

        var row = Assert.Single(library.Query.List(Unsorted()).Rows);
        var session = Assert.Single(row.Sessions);

        Assert.Equal(["Ha", "OIII", "L"], session.Filters.Select(f => f.CanonicalName));
    }

    [Fact]
    public void ANightWithNoFilter_CarriesNoName()
    {
        using var library = Library.Empty();
        var target = library.AddTarget("IC 1805");
        library.AddFrame(target, Day, frame => frame.FilterUsed = "Ha");
        library.AddFrame(target, Day, frame => frame.FilterUsed = null);

        var row = Assert.Single(library.Query.List(Unsorted()).Rows);
        var session = Assert.Single(row.Sessions);

        // Unlike the Palette column's Unknown fold, the null-filter frame contributes no badge at
        // all: the session carries one name, Ha, not two.
        var badge = Assert.Single(session.Filters);
        Assert.Equal("Ha", badge.CanonicalName);
    }

    [Fact]
    public void ANightWhoseEveryFrameHasNoFilter_CarriesAnEmptyList()
    {
        using var library = Library.Empty();
        var target = library.AddTarget("Barnard 33");
        library.AddFrame(target, Day, frame => frame.FilterUsed = null);
        library.AddFrame(target, Day, frame => frame.FilterUsed = "");

        var row = Assert.Single(library.Query.List(Unsorted()).Rows);
        var session = Assert.Single(row.Sessions);

        Assert.Empty(session.Filters);
    }

    [Fact]
    public void OnlyLightFramesContribute()
    {
        using var library = Library.Empty();
        var target = library.AddTarget("M 51");
        library.AddFrame(target, Day, frame => frame.FilterUsed = "L");
        library.AddFrame(target, Day, frame => { frame.FilterUsed = "Ha"; frame.ImageType = "DARK"; });

        var row = Assert.Single(library.Query.List(Unsorted()).Rows);
        var session = Assert.Single(row.Sessions);
        var badge = Assert.Single(session.Filters);

        Assert.Equal("L", badge.CanonicalName);
    }

    [Fact]
    public void TheNames_DescribeTheFilteredSet_NotTheWholeGroup()
    {
        using var library = Library.Empty();
        var target = library.AddTarget("NGC 281");
        library.AddFrame(target, Day, frame => frame.FilterUsed = "Ha");
        library.AddFrame(target, OtherDay, frame => frame.FilterUsed = "OIII");

        var page = library.Query.List(Unsorted() with { SessionDateTo = Day });

        var row = Assert.Single(page.Rows);
        var session = Assert.Single(row.Sessions);
        var badge = Assert.Single(session.Filters);

        Assert.Equal("Ha", badge.CanonicalName);
    }

    [Fact]
    public void TheSecondPass_StillIssuesOneCommand()
    {
        // Spec 12.2's "costs no extra round trip", pinned at the source: the fifth result set the
        // Filters column needs is appended to the one enrichment command rather than opening a
        // second one. A text assertion over the method body, in the style
        // AppHostTests.RegistrationBlock already uses for a comparable "one block, not two" rule.
        var path = FindSourceFile("src/GalactiLog.Data/Queries/TargetListingQuery.cs");
        var source = File.ReadAllText(path);

        var start = source.IndexOf("private static Enrichment ReadEnrichment(", StringComparison.Ordinal);
        Assert.True(start >= 0, "ReadEnrichment was not found in TargetListingQuery.cs");
        var end = source.IndexOf("\n    private static IReadOnlyList<FilterBadge> OrderedSessionFilters", start, StringComparison.Ordinal);
        Assert.True(end > start, "OrderedSessionFilters was not found after ReadEnrichment");
        var body = source[start..end];

        Assert.Single(Regex.Matches(body, @"connection\.CreateCommand\(\)"));
        Assert.Single(Regex.Matches(body, @"command\.ExecuteReader\(\)"));

        // Five SELECTs in the one command's text, read through four NextResult() advances off the
        // one ExecuteReader() call above: palette, equipment, sessions, aliases, session filters.
        Assert.Equal(5, Regex.Matches(body, @"^\s*SELECT", RegexOptions.Multiline).Count);
        Assert.Equal(4, Regex.Matches(body, @"reader\.NextResult\(\);").Count);
    }

    [Fact]
    public void ThePaletteBadgeCounts_AreUnchanged()
    {
        // The case that catches re-rolling set 1's own grouping instead of adding a fifth set:
        // the Palette column's totals must still sum to the group's frame count.
        using var library = Library.Empty();
        var target = library.AddTarget("M 81");
        library.AddFrame(target, Day, frame => frame.FilterUsed = "L");
        library.AddFrame(target, Day, frame => frame.FilterUsed = "Ha");
        library.AddFrame(target, OtherDay, frame => frame.FilterUsed = "L");

        var row = Assert.Single(library.Query.List(Unsorted()).Rows);

        Assert.Equal(row.FrameCount, row.Palette.Sum(badge => badge.FrameCount));
        Assert.Equal(2, row.Palette.Count);
    }

    [Fact]
    public void AnEmptyPage_SkipsTheSecondPassEntirely()
    {
        // Phase 14B fixer, fixer list item 37 (task6-review P3). This used to assert only that an
        // empty library produced no rows, which is true whether or not the short circuit exists.
        // The short circuit itself is the rule, and it is unreachable behaviourally here because
        // the query opens its own connection and nothing counts its commands, so it is pinned at
        // the source in the same style as TheSecondPass_StillIssuesOneCommand above: the early
        // return is inside List and it is ABOVE the ReadEnrichment call, which is the whole
        // content of "skips the second pass entirely".
        using var library = Library.Empty();

        var page = library.Query.List(Unsorted());

        Assert.Empty(page.Rows);

        var source = File.ReadAllText(FindSourceFile("src/GalactiLog.Data/Queries/TargetListingQuery.cs"));
        var listStart = source.IndexOf("var (rows, totals) = ReadPageAndTotals(", StringComparison.Ordinal);
        Assert.True(listStart >= 0, "ReadPageAndTotals was not called in TargetListingQuery.List");
        var shortCircuit = source.IndexOf("if (rows.Count == 0)", listStart, StringComparison.Ordinal);
        var enrichmentCall = source.IndexOf("= ReadEnrichment(", listStart, StringComparison.Ordinal);

        Assert.True(shortCircuit > listStart, "List has no zero-row short circuit after the first pass");
        Assert.True(enrichmentCall > shortCircuit, "The zero-row short circuit is below the enrichment call");
        Assert.Contains(
            "return new TargetListingPage([]",
            source[shortCircuit..enrichmentCall],
            StringComparison.Ordinal);
    }

    private static string FindSourceFile(string relativePath)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }

        throw new FileNotFoundException(relativePath);
    }
}
