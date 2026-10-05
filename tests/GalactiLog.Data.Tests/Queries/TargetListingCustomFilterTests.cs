using GalactiLog.Core.Settings;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GalactiLog.Data.Tests.Queries;

// Spec 12.15's dashboard custom column filters, Phase 20 Task 5b. One EXISTS per active filter,
// all combined with AND, every one of them target level whatever the column's scope (user choice
// 5): the dashboard's unit is a target and the rows it returns are whole targets.
//
// The value rows are written through CustomColumnRepository.SetValue rather than by hand, so no
// case here can seed a key shape the application cannot produce; the two cases that need a shape
// SetValue will not write say so and write it directly.
public class TargetListingCustomFilterTests
{
    private static readonly DateOnly NightOne = new(2025, 3, 1);
    private static readonly DateOnly NightTwo = new(2025, 3, 2);

    private sealed class Library : IDisposable
    {
        private readonly TestDatabaseHandle _db;
        private readonly AliasMapCache _aliases;

        public Library()
        {
            _db = TestDatabaseFactory.CreateMigratedDatabase();
            var connectionString = new DatabaseConnectionString(_db.ConnectionString);
            _aliases = new AliasMapCache(new SettingsStore(new SettingsRepository(_db.ConnectionString)));
            Query = new TargetListingQuery(connectionString, _aliases);
            Columns = new CustomColumnRepository(connectionString);
        }

        public string ConnectionString => _db.ConnectionString;

        public TargetListingQuery Query { get; }

        public CustomColumnRepository Columns { get; }

        /// <summary>One resolved target with one LIGHT frame per night.</summary>
        public Guid AddTarget(string name, params DateOnly[] nights)
        {
            var target = LibrarySeeder.AddTarget(ConnectionString, name);
            foreach (var night in nights.Length == 0 ? [NightOne] : nights)
            {
                LibrarySeeder.AddFrame(ConnectionString, target.Id, night);
            }

            return target.Id;
        }

        public CustomColumnDefinition AddColumn(
            string name, CustomColumnType type, CustomColumnScope scope, params string[] options)
        {
            var result = Columns.Create(name, type, scope, options);
            Assert.True(result.Ok, result.Message);
            return result.Column!;
        }

        public void SetValue(CustomColumnDefinition column, CustomValueKey key, string value)
            => Assert.True(Columns.SetValue(column.Id, key, value).Ok);

        public void Dispose()
        {
            _aliases.Dispose();
            _db.Dispose();
        }
    }

    private static TargetListingCriteria Unsorted()
        => new() { Sort = TargetListingSort.Name, Descending = false, PageSize = 100 };

    private static TargetListingCriteria With(params CustomColumnFilter[] filters)
        => Unsorted() with { CustomFilters = filters };

    private static IEnumerable<string> Names(TargetListingPage page)
        => page.Rows.Select(row => row.Name).Order(StringComparer.Ordinal);

    // ---- 1 to 2: the boolean truth table -----------------------------------------------

    [Fact]
    public void ABooleanYesFilter_ReturnsOnlyTargetsWhoseValueIsTrue()
    {
        using var library = new Library();
        var yes = library.AddTarget("M 31");
        var no = library.AddTarget("M 42");
        library.AddTarget("M 51");
        var column = library.AddColumn("Processed", CustomColumnType.Boolean, CustomColumnScope.Target);
        library.SetValue(column, CustomValueKey.ForTarget(yes), CustomColumnSlug.True);
        library.SetValue(column, CustomValueKey.ForTarget(no), CustomColumnSlug.False);

        var page = library.Query.List(With(new CustomColumnFilter(column.Slug, CustomFilterMode.Yes, null)));

        // The false target and the one with no row at all are both excluded.
        Assert.Equal(["M 31"], Names(page));
        Assert.Equal(1, page.TotalGroups);
    }

    [Fact]
    public void ABooleanNoFilter_ExcludesATargetWithNoValueRow()
    {
        using var library = new Library();
        var no = library.AddTarget("M 42");
        library.AddTarget("M 51");
        var column = library.AddColumn("Processed", CustomColumnType.Boolean, CustomColumnScope.Target);
        library.SetValue(column, CustomValueKey.ForTarget(no), CustomColumnSlug.False);

        var page = library.Query.List(With(new CustomColumnFilter(column.Slug, CustomFilterMode.No, null)));

        // Spec 12.15's own sentence: a target with no value row at all fails both Yes and No. A
        // NOT EXISTS shape, or an "= 'false' OR the row is missing" shape, would include M 51.
        Assert.Equal(["M 42"], Names(page));
    }

    // ---- 3 to 4: the text filter -------------------------------------------------------

    [Fact]
    public void ATextFilter_IsCaseInsensitiveAndSubstring()
    {
        using var library = new Library();
        var hit = library.AddTarget("M 31");
        var miss = library.AddTarget("M 42");
        var column = library.AddColumn("Notes tag", CustomColumnType.Text, CustomColumnScope.Target);
        library.SetValue(column, CustomValueKey.ForTarget(hit), "needs MORE Luminance");
        library.SetValue(column, CustomValueKey.ForTarget(miss), "finished");

        var page = library.Query.List(With(new CustomColumnFilter(column.Slug, CustomFilterMode.Contains, "more lum")));

        Assert.Equal(["M 31"], Names(page));
    }

    [Fact]
    public void ATextFilter_TreatsPercentAndUnderscoreLiterally()
    {
        using var library = new Library();
        var percent = library.AddTarget("M 31");
        var underscore = library.AddTarget("M 42");
        var plain = library.AddTarget("M 51");
        var column = library.AddColumn("Notes tag", CustomColumnType.Text, CustomColumnScope.Target);
        library.SetValue(column, CustomValueKey.ForTarget(percent), "clipped 100% of frames");
        library.SetValue(column, CustomValueKey.ForTarget(underscore), "keyed a_b");
        library.SetValue(column, CustomValueKey.ForTarget(plain), "clipped 100 aof frames");

        // Departure 17. Without the ESCAPE clause "100%" matches anything beginning "100" and
        // "a_b" matches "aab", so both of these would return two rows instead of one. No other
        // case in this file can see that defect.
        var percentPage = library.Query.List(With(new CustomColumnFilter(column.Slug, CustomFilterMode.Contains, "100%")));
        var underscorePage = library.Query.List(With(new CustomColumnFilter(column.Slug, CustomFilterMode.Contains, "a_b")));

        Assert.Equal(["M 31"], Names(percentPage));
        Assert.Equal(["M 42"], Names(underscorePage));
    }

    // ---- 5: the dropdown filter --------------------------------------------------------

    [Fact]
    public void ADropdownFilter_MatchesExactlyAndCaseSensitively()
    {
        using var library = new Library();
        var exact = library.AddTarget("M 31");
        var folded = library.AddTarget("M 42");
        var column = library.AddColumn(
            "Priority", CustomColumnType.Dropdown, CustomColumnScope.Target, "High", "Low");
        library.SetValue(column, CustomValueKey.ForTarget(exact), "High");

        // "high" is not one of the column's options, so SetValue refuses it; the stored spelling
        // a case-folded match would wrongly catch is written directly.
        WriteValueDirectly(library, column.Id, CustomValueKey.ForTarget(folded), "high");

        var page = library.Query.List(With(new CustomColumnFilter(column.Slug, CustomFilterMode.Equals, "High")));

        Assert.Equal(["M 31"], Names(page));
    }

    // ---- 6 to 8: the three scope predicates --------------------------------------------

    [Fact]
    public void ASessionScopeFilter_ReturnsTheWholeTarget()
    {
        using var library = new Library();
        var target = library.AddTarget("M 31", NightOne, NightTwo);
        library.AddTarget("M 42", NightOne, NightTwo);
        var column = library.AddColumn("Night note", CustomColumnType.Text, CustomColumnScope.Session);
        library.SetValue(column, CustomValueKey.ForSession(target, NightTwo), "clouds");

        var page = library.Query.List(With(new CustomColumnFilter(column.Slug, CustomFilterMode.Contains, "clouds")));

        // User choice 5: one matching night keeps the whole target, both nights and both frames.
        // A query that narrowed the night set would report one session and one frame.
        var row = Assert.Single(page.Rows);
        Assert.Equal("M 31", row.Name);
        Assert.Equal(2, row.SessionCount);
        Assert.Equal(2, row.FrameCount);
    }

    [Fact]
    public void ARigScopeFilter_RequiresANonNullRigLabel()
    {
        using var library = new Library();
        var rigged = library.AddTarget("M 31", NightOne);
        var nightOnly = library.AddTarget("M 42", NightOne);
        var column = library.AddColumn("Rig note", CustomColumnType.Text, CustomColumnScope.Rig);
        library.SetValue(column, CustomValueKey.ForRig(rigged, NightOne, "RC8 / ASI2600MM"), "focus drift");

        // A value of the same column under a session-scope key. `SetValue` refuses that shape
        // (Task 2's KeyDoesNotMatchScope guard), so the row is written directly: what it stands
        // for is a hand-edited catalogue or one written before that guard existed, and the query
        // still has to defend against it. Only the rig_label IS NOT NULL term keeps it out.
        WriteValueDirectly(library, column.Id, CustomValueKey.ForSession(nightOnly, NightOne), "focus drift");

        var page = library.Query.List(With(new CustomColumnFilter(column.Slug, CustomFilterMode.Contains, "focus")));

        Assert.Equal(["M 31"], Names(page));
    }

    [Fact]
    public void ATargetScopeFilter_RequiresBothKeyPartsNull()
    {
        using var library = new Library();
        var target = library.AddTarget("M 31", NightOne);
        var nightOnly = library.AddTarget("M 42", NightOne);
        var column = library.AddColumn("Notes tag", CustomColumnType.Text, CustomColumnScope.Target);
        library.SetValue(column, CustomValueKey.ForTarget(target), "review");

        // Same as the rig case above: the mis-shaped row is written directly, because it is the
        // shape a hand edit leaves behind and the one the two IS NULL terms exist to exclude.
        WriteValueDirectly(library, column.Id, CustomValueKey.ForSession(nightOnly, NightOne), "review");

        var page = library.Query.List(With(new CustomColumnFilter(column.Slug, CustomFilterMode.Contains, "review")));

        // Red against a predicate that omits the two IS NULL terms, which is the shape that lets a
        // night value satisfy a target filter.
        Assert.Equal(["M 31"], Names(page));
    }

    // ---- 9: combination ----------------------------------------------------------------

    [Fact]
    public void TwoCustomFilters_AreCombinedWithAnd()
    {
        using var library = new Library();
        var both = library.AddTarget("M 31");
        var doneOnly = library.AddTarget("M 42");
        var taggedOnly = library.AddTarget("M 51");
        var done = library.AddColumn("Processed", CustomColumnType.Boolean, CustomColumnScope.Target);
        var tag = library.AddColumn("Notes tag", CustomColumnType.Text, CustomColumnScope.Target);
        library.SetValue(done, CustomValueKey.ForTarget(both), CustomColumnSlug.True);
        library.SetValue(tag, CustomValueKey.ForTarget(both), "wide field");
        library.SetValue(done, CustomValueKey.ForTarget(doneOnly), CustomColumnSlug.True);
        library.SetValue(tag, CustomValueKey.ForTarget(taggedOnly), "wide field");

        var page = library.Query.List(With(
            new CustomColumnFilter(done.Slug, CustomFilterMode.Yes, null),
            new CustomColumnFilter(tag.Slug, CustomFilterMode.Contains, "wide")));

        Assert.Equal(["M 31"], Names(page));

        // And with a filter from another section. Every frame this library holds carries an HFR of
        // 2.0, so a floor of 5.0 must empty the result rather than widen it: the custom clauses and
        // the metric HAVING combine rather than replacing one another.
        SetEveryFrameHfr(library, 2.0d);
        var withMetric = library.Query.List(With(
            new CustomColumnFilter(done.Slug, CustomFilterMode.Yes, null),
            new CustomColumnFilter(tag.Slug, CustomFilterMode.Contains, "wide")) with
        {
            MetricRanges = new Dictionary<string, MetricRange>(StringComparer.Ordinal)
            {
                ["hfr"] = new MetricRange(Min: 5.0d, Max: null),
            },
        });

        Assert.Empty(withMetric.Rows);
    }

    // ---- 10 to 11: a column or an option that is gone ----------------------------------

    [Fact]
    public void AFilterNamingADeletedColumn_IsDropped()
    {
        using var library = new Library();
        var kept = library.AddTarget("M 31");
        library.AddTarget("M 42");
        var column = library.AddColumn("Processed", CustomColumnType.Boolean, CustomColumnScope.Target);
        library.SetValue(column, CustomValueKey.ForTarget(kept), CustomColumnSlug.True);

        var page = library.Query.List(With(
            new CustomColumnFilter("custom_deleted_yesterday", CustomFilterMode.Yes, null),
            new CustomColumnFilter(column.Slug, CustomFilterMode.Yes, null)));

        // Red against a query that emits "1 = 0" or an EXISTS on a null column id when one slug is
        // unknown, which would return nothing at all instead of applying the filter that remains.
        Assert.Equal(["M 31"], Names(page));
    }

    [Fact]
    public void ADropdownFilterHoldingARemovedOption_IsKeptAndMatchesNothing()
    {
        using var library = new Library();
        var target = library.AddTarget("M 31");
        library.AddTarget("M 42");
        var column = library.AddColumn(
            "Priority", CustomColumnType.Dropdown, CustomColumnScope.Target, "High", "Low");
        library.SetValue(column, CustomValueKey.ForTarget(target), "Low");

        var page = library.Query.List(With(new CustomColumnFilter(column.Slug, CustomFilterMode.Equals, "Medium")));

        // Red against dropping the filter because its option is not offered any more, which would
        // silently widen the result to both targets.
        Assert.Empty(page.Rows);
        Assert.Equal(0, page.TotalGroups);
    }

    // ---- 12: the unresolved group ------------------------------------------------------

    [Fact]
    public void AnUnresolvedGroup_MatchesNoCustomFilter()
    {
        using var library = new Library();
        var resolved = library.AddTarget("M 31");
        LibrarySeeder.AddFrame(
            library.ConnectionString, null, NightOne,
            frame => frame.RawHeaders = LibrarySeeder.RawHeadersWithObject("Sh2-155"));
        var column = library.AddColumn("Processed", CustomColumnType.Boolean, CustomColumnScope.Target);
        library.SetValue(column, CustomValueKey.ForTarget(resolved), CustomColumnSlug.True);

        var unfiltered = library.Query.List(Unsorted());
        var page = library.Query.List(With(new CustomColumnFilter(column.Slug, CustomFilterMode.Yes, null)));

        // The unresolved group exists and is listed unfiltered, and it holds no target row, so it
        // can hold no custom value and matches no custom filter. Red against the group join
        // widened to OR i.resolved_target_id IS NULL, which would let an unresolved group match
        // any target's value. The EXISTS keeps the i.resolved_target_id spelling because that
        // column is always present and stays correct if a later change touches the join.
        Assert.Equal(["M 31", "Sh2-155"], Names(unfiltered));
        Assert.Equal(["M 31"], Names(page));
    }

    // ---- 13: departure 18, the matched-night marker ------------------------------------

    [Fact]
    public void ACustomFilter_DoesNotChangeAnyRowsSessionCount()
    {
        using var library = new Library();
        var target = library.AddTarget("M 31", NightOne, NightTwo);
        var column = library.AddColumn("Rig note", CustomColumnType.Text, CustomColumnScope.Rig);
        library.SetValue(column, CustomValueKey.ForRig(target, NightTwo, "RC8 / ASI2600MM"), "focus drift");

        var unfiltered = library.Query.List(Unsorted());
        var filtered = library.Query.List(With(new CustomColumnFilter(column.Slug, CustomFilterMode.Contains, "focus")));

        // Departure 18: the port adds no second pre-fetch and adjusts no "n of m sessions" marker,
        // so every row's session count is exactly what an unfiltered query reports. Red against a
        // second pre-fetch that counts only the matched nights.
        var before = unfiltered.Rows.Single(row => row.Name == "M 31");
        var after = filtered.Rows.Single(row => row.Name == "M 31");
        Assert.Equal(2, before.SessionCount);
        Assert.Equal(before.SessionCount, after.SessionCount);
        Assert.Equal(before.Sessions.Count, after.Sessions.Count);
        Assert.Equal(before.FrameCount, after.FrameCount);
    }

    // ---- 14: F3, AnyFilterActive against the SQL ---------------------------------------

    [Theory]
    [InlineData(CustomFilterMode.Yes, null)]
    [InlineData(CustomFilterMode.No, null)]
    [InlineData(CustomFilterMode.Contains, "tag")]
    [InlineData(CustomFilterMode.Equals, "High")]
    public void AnyFilterActive_AgreesWithTheSqlForEveryMode(CustomFilterMode mode, string? text)
    {
        using var library = new Library();
        var yes = library.AddTarget("M 31");
        var no = library.AddTarget("M 42");
        library.AddTarget("M 51");
        var boolean = library.AddColumn("Processed", CustomColumnType.Boolean, CustomColumnScope.Target);
        var tag = library.AddColumn("Notes tag", CustomColumnType.Text, CustomColumnScope.Target);
        var priority = library.AddColumn(
            "Priority", CustomColumnType.Dropdown, CustomColumnScope.Target, "High", "Low");
        library.SetValue(boolean, CustomValueKey.ForTarget(yes), CustomColumnSlug.True);
        library.SetValue(boolean, CustomValueKey.ForTarget(no), CustomColumnSlug.False);
        library.SetValue(tag, CustomValueKey.ForTarget(yes), "one tag");
        library.SetValue(priority, CustomValueKey.ForTarget(yes), "High");

        var slug = mode switch
        {
            CustomFilterMode.Contains => tag.Slug,
            CustomFilterMode.Equals => priority.Slug,
            _ => boolean.Slug,
        };

        var unfiltered = library.Query.List(Unsorted());
        var criteria = With(new CustomColumnFilter(slug, mode, text));

        // Every mode the panel can emit contributes a clause, so AnyFilterActive reporting true
        // and the row set actually narrowing are the same fact.
        Assert.True(criteria.AnyFilterActive);
        Assert.NotEqual(Names(unfiltered), Names(library.Query.List(criteria)));
    }

    [Theory]
    [InlineData(CustomFilterMode.Any, null)]
    [InlineData(CustomFilterMode.Any, "ignored")]
    [InlineData(CustomFilterMode.Contains, "")]
    [InlineData(CustomFilterMode.Contains, "   ")]
    public void AFilterThatWouldContributeNoClause_ProducesNoClauseAtAll(CustomFilterMode mode, string? text)
    {
        // The other half of F3, from the query's side. The panel drops these before they reach the
        // criteria (CustomColumnFilterTests cases 20 and 21), and if one ever did reach it, it
        // would report a filter active while narrowing nothing, which is exactly the disagreement
        // TargetListingCriteria.AnyFilterActive forbids. The clause list stays empty, so the query
        // is fail-safe as well.
        using var library = new Library();
        library.AddTarget("M 31");
        var column = library.AddColumn("Notes tag", CustomColumnType.Text, CustomColumnScope.Target);

        var clauses = ComposeClauses(library, new CustomColumnFilter(column.Slug, mode, text));

        Assert.Empty(clauses);
    }

    // ---- 15: no user text in the command text ------------------------------------------

    [Fact]
    public void NoUserTextReachesTheCommandText()
    {
        using var library = new Library();
        library.AddTarget("M 31");
        var tag = library.AddColumn("Notes tag", CustomColumnType.Text, CustomColumnScope.Target);
        var priority = library.AddColumn(
            "Priority", CustomColumnType.Dropdown, CustomColumnScope.Target, "High");

        const string typed = "M 31' OR 1=1 --";
        var clauses = ComposeClauses(
            library,
            new CustomColumnFilter(tag.Slug, CustomFilterMode.Contains, typed),
            new CustomColumnFilter(priority.Slug, CustomFilterMode.Equals, typed));

        var sql = string.Join("\n", clauses);

        Assert.Equal(2, clauses.Count);
        Assert.DoesNotContain(typed, sql, StringComparison.Ordinal);
        Assert.DoesNotContain("OR 1=1", sql, StringComparison.Ordinal);
        Assert.DoesNotContain(tag.Slug, sql, StringComparison.Ordinal);
        Assert.DoesNotContain(priority.Slug, sql, StringComparison.Ordinal);

        // The boolean literals are bound too: no literal 'true' is written into the text.
        var boolean = library.AddColumn("Processed", CustomColumnType.Boolean, CustomColumnScope.Target);
        var booleanSql = string.Join("\n", ComposeClauses(
            library, new CustomColumnFilter(boolean.Slug, CustomFilterMode.Yes, null)));
        Assert.DoesNotContain($"'{CustomColumnSlug.True}'", booleanSql, StringComparison.Ordinal);

        // And the whole listing runs with that text, proving the composed statement is valid and
        // the injection attempt narrows to nothing rather than widening to everything.
        Assert.Empty(library.Query.List(With(
            new CustomColumnFilter(tag.Slug, CustomFilterMode.Contains, typed))).Rows);
    }

    // ---- helpers -----------------------------------------------------------------------

    /// <summary>The clause text <c>BuildBaseFilter</c> would append, read straight from the one
    /// method that composes it.</summary>
    private static List<string> ComposeClauses(Library library, params CustomColumnFilter[] filters)
    {
        var clauses = new List<string>();
        using var connection = new SqliteConnection(library.ConnectionString);
        connection.Open();
        TargetListingQuery.AppendCustomFilters(filters, clauses, new SqlParameters(), connection);
        return clauses;
    }

    /// <summary>Gives every LIGHT frame the same median HFR, so a metric range filter is a
    /// deterministic narrowing rather than one the seeder's null metrics let through.</summary>
    private static void SetEveryFrameHfr(Library library, double hfr)
    {
        using var connection = new SqliteConnection(library.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE images SET median_hfr = @hfr;";
        command.Parameters.Add(new SqliteParameter("@hfr", hfr));
        command.ExecuteNonQuery();
    }

    /// <summary>A stored value the repository refuses to write, for the cases whose subject is a
    /// row a hand edit, an option removal or a catalogue written before Task 2's key-shape guard
    /// can leave behind. The query has to answer correctly for such a row, which is exactly why
    /// the scope predicate and the case-sensitive comparison are load bearing.</summary>
    private static void WriteValueDirectly(Library library, Guid columnId, CustomValueKey key, string value)
    {
        using var context = new GalactiLogContext(
            GalactiLogContextOptions.Create(library.ConnectionString, tracking: true));
        context.CustomColumnValues.Add(new CustomColumnValue
        {
            Id = Guid.NewGuid(),
            ColumnId = columnId,
            TargetId = key.TargetId,
            MosaicId = key.MosaicId,
            SessionDate = key.SessionDate,
            RigLabel = key.RigLabel,
            Value = value,
            UpdatedAt = DateTime.UtcNow,
        });
        context.SaveChanges();
    }
}
