using System.Text.Json;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.Data.Tests.Queries;

/// <summary>
/// Pure unit tests: no database. <see cref="HeaderQueryBuilder.Append"/> takes a
/// <c>bindParameter</c> delegate rather than a concrete parameter bag (see the deviation noted in
/// the class remarks), so these tests supply a small in-memory stand-in that records every bound
/// value and returns names shaped like the real <c>TargetListingQuery.SqlParameters</c>
/// (<c>@p0</c>, <c>@p1</c>, ...).
/// </summary>
public class HeaderQueryBuilderTests
{
    private static (int Accepted, List<string> Clauses, List<object?> Values) Run(params HeaderCondition[] conditions)
    {
        var clauses = new List<string>();
        var values = new List<object?>();
        var accepted = HeaderQueryBuilder.Append(conditions, clauses, value =>
        {
            var name = $"@p{values.Count}";
            values.Add(value);
            return name;
        });

        return (accepted, clauses, values);
    }

    // ---- the key gate --------------------------------------------------------------------

    [Theory]
    [InlineData("OBJECT'; DROP TABLE images;--")]
    [InlineData("$.OBJECT")]
    [InlineData("a b")]
    [InlineData("")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTU")] // 21 characters, one past the cap
    [InlineData("OBJ.ECT")]
    public void Append_InjectionShapedKey_IsDropped(string key)
    {
        var (accepted, clauses, values) = Run(new HeaderCondition(key, "=", "x"));

        Assert.Equal(0, accepted);
        Assert.Empty(clauses);
        Assert.Empty(values);
    }

    [Theory]
    [InlineData("A", true)]
    [InlineData("ABCDEFGHIJKLMNOPQRST", true)] // 20 characters
    [InlineData("ABCDEFGHIJKLMNOPQRSTU", false)] // 21 characters
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("abc_-09", true)]
    [InlineData("a b", false)]
    [InlineData("OBJ.ECT", false)]
    [InlineData("$OBJECT", false)]
    public void IsValidKey_AcceptsTheDocumentedCharacterSetAndLengthBounds(string? key, bool expected)
    {
        Assert.Equal(expected, HeaderQueryBuilder.IsValidKey(key));
    }

    // ---- no user-supplied substring ever reaches the SQL text ----------------------------

    [Theory]
    [InlineData("OBJECT'; DROP TABLE images;--", "=", "value")]
    [InlineData("$.OBJECT", "contains", "x")]
    [InlineData("a b", ">", "1")]
    [InlineData("", "!=", "x")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTU", "=", "x")]
    [InlineData("OBJECT", "=", "'; DROP TABLE images;--")]
    [InlineData("OBJECT", "contains", "%_\\'; DROP TABLE images;--")]
    [InlineData("OBJECT", ">", "1; DROP TABLE images;--")]
    public void Append_GeneratedSqlContainsNoUserSuppliedSubstring(string key, string op, string value)
    {
        var (_, clauses, _) = Run(new HeaderCondition(key, op, value));
        var sql = string.Join(" ", clauses);

        if (!string.IsNullOrEmpty(key))
        {
            Assert.DoesNotContain(key, sql, StringComparison.Ordinal);
        }

        if (!string.IsNullOrEmpty(value))
        {
            Assert.DoesNotContain(value, sql, StringComparison.Ordinal);
        }
    }

    // ---- numeric operators -----------------------------------------------------------------

    [Theory]
    [InlineData(">")]
    [InlineData("<")]
    [InlineData(">=")]
    [InlineData("<=")]
    public void Append_NonNumericValueOnGreaterThan_DropsTheClause(string op)
    {
        var (accepted, clauses, values) = Run(new HeaderCondition("EXPOSURE", op, "not-a-number"));

        Assert.Equal(0, accepted);
        Assert.Empty(clauses);
        Assert.Empty(values);
    }

    [Fact]
    public void Append_NumericOperator_ProducesRealCastClause()
    {
        var (accepted, clauses, values) = Run(new HeaderCondition("EXPOSURE", ">", "9.5"));

        Assert.Equal(1, accepted);
        var clause = Assert.Single(clauses);
        Assert.Contains("CAST(", clause, StringComparison.Ordinal);
        Assert.Contains("AS REAL) >", clause, StringComparison.Ordinal);
        Assert.Equal("EXPOSURE", values[0]);
        Assert.Equal(9.5, values[1]);
    }

    // ---- = and != (Q6) ---------------------------------------------------------------------

    [Fact]
    public void Append_Equals_BindsNumericValueAsDoubleWhenItParses()
    {
        var (_, _, values) = Run(new HeaderCondition("GAIN", "=", "1.5"));

        Assert.IsType<double>(values[1]);
        Assert.Equal(1.5, values[1]);
    }

    [Fact]
    public void Append_Equals_BindsNonNumericValueAsText()
    {
        var (_, _, values) = Run(new HeaderCondition("OBJECT", "=", "M31"));

        Assert.IsType<string>(values[1]);
        Assert.Equal("M31", values[1]);
    }

    [Fact]
    public void Append_NotEquals_UsesIsNotRatherThanNotEqual()
    {
        var (accepted, clauses, _) = Run(new HeaderCondition("OBJECT", "!=", "M31"));

        Assert.Equal(1, accepted);
        var clause = Assert.Single(clauses);
        Assert.Contains("IS NOT", clause, StringComparison.Ordinal);
        Assert.DoesNotContain("<>", clause, StringComparison.Ordinal);
    }

    // ---- contains ----------------------------------------------------------------------

    [Fact]
    public void Append_ContainsValue_IsWrappedInPercentSigns_AndCarriesEscapeClause()
    {
        var (accepted, clauses, values) = Run(new HeaderCondition("OBJECT", "contains", "NGC"));

        Assert.Equal(1, accepted);
        var clause = Assert.Single(clauses);
        Assert.Contains("LIKE", clause, StringComparison.Ordinal);
        Assert.Contains("ESCAPE '\\'", clause, StringComparison.Ordinal);
        Assert.Equal("OBJECT", values[0]);
        Assert.Equal("%NGC%", values[1]);
    }

    [Fact]
    public void Append_ContainsEscapesBackslashFirst()
    {
        var (_, _, values) = Run(new HeaderCondition("OBJECT", "contains", @"a\%b"));

        Assert.Equal(@"%a\\\%b%", values[1]);
    }

    // ---- review fix 4: a null Value must not reach EscapeLike ----------------------------

    [Fact]
    public void Append_NullValueOnContains_IsTreatedAsEmptyRatherThanThrowing()
    {
        var (accepted, clauses, values) = Run(new HeaderCondition("OBJECT", "contains", null!));

        Assert.Equal(1, accepted);
        Assert.Single(clauses);
        Assert.Equal("%%", values[1]);
    }

    [Fact]
    public void Append_NullValueOnNumericOperator_DropsTheClauseLikeEmpty()
    {
        var (accepted, clauses, values) = Run(new HeaderCondition("EXPOSURE", ">", null!));

        Assert.Equal(0, accepted);
        Assert.Empty(clauses);
        Assert.Empty(values);
    }

    // ---- operator and drop behaviour -----------------------------------------------------

    [Fact]
    public void Append_UnsupportedOperator_IsDropped()
    {
        var (accepted, clauses, values) = Run(new HeaderCondition("OBJECT", "LIKE", "x"));

        Assert.Equal(0, accepted);
        Assert.Empty(clauses);
        Assert.Empty(values);
    }

    [Fact]
    public void Append_DroppedCondition_DoesNotAffectTheRemainingOnes()
    {
        var (accepted, clauses, values) = Run(
            new HeaderCondition("OBJECT", "=", "M31"),
            new HeaderCondition("bad key", "=", "x"),
            new HeaderCondition("EXPOSURE", ">", "10"));

        Assert.Equal(2, accepted);
        Assert.Equal(2, clauses.Count);
        Assert.Equal(4, values.Count);
    }

    [Theory]
    [InlineData("=")]
    [InlineData("!=")]
    [InlineData(">")]
    [InlineData("<")]
    [InlineData(">=")]
    [InlineData("<=")]
    [InlineData("contains")]
    public void Append_KeyAndValueAreAlwaysBoundParameters(string op)
    {
        var value = op is ">" or "<" or ">=" or "<=" ? "5" : "text-value";
        var (accepted, _, values) = Run(new HeaderCondition("OBJECT", op, value));

        Assert.Equal(1, accepted);
        Assert.Equal(2, values.Count);
    }

    [Fact]
    public void Append_ParametersDoNotCollideWithCallersOwnParameters()
    {
        // The builder has no naming scheme of its own; every name it uses comes back from the
        // caller's own bindParameter delegate (Task 2's SqlParameters.Add), so it can never
        // collide with a name the caller already assigned itself.
        var values = new List<object?>();
        string Bind(object? value)
        {
            var name = $"@p{values.Count}";
            values.Add(value);
            return name;
        }

        var callerOwnName = Bind("caller-value");
        var clauses = new List<string>();
        var accepted = HeaderQueryBuilder.Append([new HeaderCondition("OBJECT", "=", "M31")], clauses, Bind);

        Assert.Equal(1, accepted);
        Assert.Equal(3, values.Count);
        Assert.Equal("caller-value", values[0]);
        Assert.DoesNotContain(callerOwnName, clauses[0], StringComparison.Ordinal);
    }

    // ---- execution tests against a seeded database ---------------------------------------
    //
    // These exercise HeaderCondition end to end through TargetListingQuery.List, so they live
    // here rather than in TargetListingQueryTests.cs: that file is Task 2's, and a concurrent
    // reviewer or fix pass may be touching it.

    private static readonly DateOnly Day = new(2025, 3, 1);

    private static string Headers(params (string Key, object Value)[] pairs)
        => JsonSerializer.Serialize(pairs.ToDictionary(pair => pair.Key, pair => pair.Value));

    private sealed class HeaderFilterLibrary : IDisposable
    {
        private readonly TestDatabaseHandle _db;
        private readonly AliasMapCache _aliases;

        public HeaderFilterLibrary()
        {
            _db = TestDatabaseFactory.CreateMigratedDatabase();
            var settings = new SettingsStore(new SettingsRepository(_db.ConnectionString));
            _aliases = new AliasMapCache(settings);
            Query = new TargetListingQuery(new DatabaseConnectionString(_db.ConnectionString), _aliases);
        }

        public TargetListingQuery Query { get; }

        /// <summary>One resolved target with one LIGHT frame, so each call adds exactly one row
        /// to the listing, identifiable by <paramref name="primaryName"/>.</summary>
        public void AddFrame(string primaryName, Action<Image> configure)
        {
            var target = LibrarySeeder.AddTarget(_db.ConnectionString, primaryName);
            LibrarySeeder.AddFrame(_db.ConnectionString, target.Id, Day, configure);
        }

        public void Dispose()
        {
            _aliases.Dispose();
            _db.Dispose();
        }
    }

    [Fact]
    public void List_HeaderEquals_MatchesFramesWithThatHeaderValue()
    {
        using var library = new HeaderFilterLibrary();
        library.AddFrame("Target A", frame => frame.RawHeaders = Headers(("OBJECT", "M31")));
        library.AddFrame("Target B", frame => frame.RawHeaders = Headers(("OBJECT", "M42")));

        var page = library.Query.List(new TargetListingCriteria
        {
            HeaderConditions = [new HeaderCondition("OBJECT", "=", "M31")],
        });

        var row = Assert.Single(page.Rows);
        Assert.Equal("Target A", row.Name);
    }

    [Fact]
    public void List_HeaderContains_PercentInValueMatchesLiterally()
    {
        using var library = new HeaderFilterLibrary();
        library.AddFrame("Fifty Percent", frame => frame.RawHeaders = Headers(("OBJECT", "50%")));
        library.AddFrame("Five Thousand Twelve", frame => frame.RawHeaders = Headers(("OBJECT", "5012")));

        var page = library.Query.List(new TargetListingCriteria
        {
            HeaderConditions = [new HeaderCondition("OBJECT", "contains", "50%")],
        });

        var row = Assert.Single(page.Rows);
        Assert.Equal("Fifty Percent", row.Name);
    }

    [Fact]
    public void List_HeaderContains_UnderscoreInValueMatchesLiterally()
    {
        using var library = new HeaderFilterLibrary();
        library.AddFrame("Underscore Object", frame => frame.RawHeaders = Headers(("OBJECT", "M_31")));
        library.AddFrame("Wildcard Match", frame => frame.RawHeaders = Headers(("OBJECT", "MX31")));

        var page = library.Query.List(new TargetListingCriteria
        {
            HeaderConditions = [new HeaderCondition("OBJECT", "contains", "M_31")],
        });

        var row = Assert.Single(page.Rows);
        Assert.Equal("Underscore Object", row.Name);
    }

    [Fact]
    public void List_HeaderNumericComparison_UsesRealCast()
    {
        using var library = new HeaderFilterLibrary();
        library.AddFrame("Ten", frame => frame.RawHeaders = Headers(("GAIN", "10")));
        library.AddFrame("Two", frame => frame.RawHeaders = Headers(("GAIN", "2")));

        var page = library.Query.List(new TargetListingCriteria
        {
            HeaderConditions = [new HeaderCondition("GAIN", ">", "9")],
        });

        // Lexical comparison would put "10" before "9"; the REAL cast compares numerically.
        var row = Assert.Single(page.Rows);
        Assert.Equal("Ten", row.Name);
    }

    [Fact]
    public void List_HeaderNotEquals_IncludesFramesMissingTheKey()
    {
        using var library = new HeaderFilterLibrary();
        library.AddFrame("Has Object M31", frame => frame.RawHeaders = Headers(("OBJECT", "M31")));
        library.AddFrame("Missing Object Key", frame => frame.RawHeaders = Headers(("GAIN", 100)));

        var page = library.Query.List(new TargetListingCriteria
        {
            HeaderConditions = [new HeaderCondition("OBJECT", "!=", "M31")],
        });

        var row = Assert.Single(page.Rows);
        Assert.Equal("Missing Object Key", row.Name);
    }

    [Fact]
    public void List_MultipleHeaderConditions_AreAnded()
    {
        using var library = new HeaderFilterLibrary();
        library.AddFrame("Both Match", frame => frame.RawHeaders = Headers(("OBJECT", "M31"), ("GAIN", "100")));
        library.AddFrame("Only Object Matches", frame => frame.RawHeaders = Headers(("OBJECT", "M31"), ("GAIN", "5")));

        var page = library.Query.List(new TargetListingCriteria
        {
            HeaderConditions =
            [
                new HeaderCondition("OBJECT", "=", "M31"),
                new HeaderCondition("GAIN", ">", "50"),
            ],
        });

        var row = Assert.Single(page.Rows);
        Assert.Equal("Both Match", row.Name);
    }

    [Fact]
    public void List_AllHeaderConditionsDropped_BehavesAsNoHeaderFilter()
    {
        using var library = new HeaderFilterLibrary();
        library.AddFrame("Target A", frame => frame.RawHeaders = Headers(("OBJECT", "M31")));
        library.AddFrame("Target B", frame => frame.RawHeaders = Headers(("OBJECT", "M42")));

        var page = library.Query.List(new TargetListingCriteria
        {
            HeaderConditions = [new HeaderCondition("bad key", "=", "x")],
        });

        Assert.Equal(2, page.Rows.Count);
    }
}
