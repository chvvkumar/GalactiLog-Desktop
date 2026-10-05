using System.Diagnostics;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace GalactiLog.Data.Tests.Queries;

public class TargetListingQueryTests(ITestOutputHelper output)
{
    private static readonly DateOnly Day = new(2025, 3, 1);

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
            Facets = new DashboardFacetsQuery(new DatabaseConnectionString(db.ConnectionString), _aliases);
        }

        public string ConnectionString => _db.ConnectionString;
        public SettingsStore Settings { get; }
        public TargetListingQuery Query { get; }
        public DashboardFacetsQuery Facets { get; }

        public static Library Empty() => new(TestDatabaseFactory.CreateMigratedDatabase());

        public static Library Seeded()
        {
            var library = Empty();
            LibrarySeeder.Seed(library.ConnectionString);
            return library;
        }

        public Target AddTarget(string primaryName, Action<Target>? configure = null)
            => LibrarySeeder.AddTarget(ConnectionString, primaryName, configure);

        public Image AddFrame(Guid? targetId, DateOnly sessionDate, Action<Image>? configure = null)
            => LibrarySeeder.AddFrame(ConnectionString, targetId, sessionDate, configure);

        /// <summary>Writes a raw <c>session_date</c> no writer in the application can produce.
        /// The column is TEXT with no format constraint, so this is what a hand-edited row or an
        /// import from another tool leaves behind; the typed seeder cannot express it.</summary>
        public void SetRawSessionDate(Guid imageId, string raw)
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE images SET session_date = @date WHERE id = @id;";
            command.Parameters.Add(new SqliteParameter("@date", raw));
            command.Parameters.Add(new SqliteParameter("@id", imageId));
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        /// <summary>A resolved target plus one frame per supplied shape, all on the same date.</summary>
        public Guid AddGroup(string primaryName, params Action<Image>[] frames)
        {
            var target = AddTarget(primaryName);
            foreach (var frame in frames)
            {
                AddFrame(target.Id, Day, frame);
            }

            return target.Id;
        }

        public void Dispose()
        {
            _aliases.Dispose();
            _db.Dispose();
        }
    }

    private static TargetListingCriteria Unsorted(TargetListingSort sort = TargetListingSort.Name, bool descending = false)
        => new() { Sort = sort, Descending = descending };

    // ---- base filter -------------------------------------------------------------------

    [Fact]
    public void List_ExcludesNonLightFrames()
    {
        using var library = Library.Empty();
        var target = library.AddTarget("NGC 7000");
        library.AddFrame(target.Id, Day);
        library.AddFrame(target.Id, Day, frame => frame.ImageType = "DARK");
        library.AddFrame(target.Id, Day, frame => frame.ImageType = "FLAT");
        library.AddFrame(target.Id, Day, frame => frame.ImageType = null);

        var page = library.Query.List(Unsorted());

        var row = Assert.Single(page.Rows);
        Assert.Equal(1, row.FrameCount);
        Assert.Equal(1, page.TotalFrames);
    }

    [Fact]
    public void List_ExcludesFramesOfMergedTargets()
    {
        using var library = Library.Empty();
        var winner = library.AddTarget("M 81");
        var loser = library.AddTarget("M 81 dup", target => target.MergedIntoId = winner.Id);
        library.AddFrame(winner.Id, Day);
        library.AddFrame(loser.Id, Day);
        library.AddFrame(loser.Id, Day);

        var page = library.Query.List(Unsorted());

        var row = Assert.Single(page.Rows);
        Assert.Equal(winner.Id, row.TargetId);
        Assert.Equal(1, row.FrameCount);
    }

    // ---- the group key -----------------------------------------------------------------

    [Fact]
    public void List_GroupKey_EmptyObject_MissingObject_AndNullRawHeaders_CollapseToOneGroup()
    {
        using var library = Library.Empty();
        library.AddFrame(null, Day, frame => frame.RawHeaders = null);
        library.AddFrame(null, Day, frame => frame.RawHeaders = """{"EXPTIME": 300}""");
        library.AddFrame(null, Day, frame => frame.RawHeaders = LibrarySeeder.RawHeadersWithObject(""));

        var page = library.Query.List(Unsorted());

        var row = Assert.Single(page.Rows);
        Assert.Equal("obj:__uncategorized__", row.GroupKey);
        Assert.Equal("Uncategorized", row.Name);
        Assert.Equal(3, row.FrameCount);
        Assert.Null(row.TargetId);
    }

    [Fact]
    public void List_UnresolvedFrames_GroupUnderObjPrefixedKey()
    {
        using var library = Library.Empty();
        library.AddFrame(null, Day, frame => frame.RawHeaders = LibrarySeeder.RawHeadersWithObject("Sh2-155"));
        library.AddFrame(null, Day, frame => frame.RawHeaders = LibrarySeeder.RawHeadersWithObject("Sh2-155"));
        library.AddFrame(null, Day, frame => frame.RawHeaders = LibrarySeeder.RawHeadersWithObject("LDN 1251"));

        var page = library.Query.List(Unsorted());

        Assert.Equal(["obj:LDN 1251", "obj:Sh2-155"], page.Rows.Select(row => row.GroupKey).Order());
        var cave = page.Rows.Single(row => row.GroupKey == "obj:Sh2-155");
        Assert.Equal("Sh2-155", cave.Name);
        Assert.Equal(TargetListingCriteria.UnresolvedCategory, cave.ObjectCategory);
        Assert.Equal(2, cave.FrameCount);
    }

    // ---- aggregates --------------------------------------------------------------------

    [Fact]
    public void List_Aggregates_IntegrationFramesSessionsFirstAndLastSessionAreCorrect()
    {
        using var library = Library.Seeded();

        var page = library.Query.List(Unsorted() with { PageSize = 100 });

        Assert.Equal(LibrarySeeder.TargetCount, page.TotalGroups);
        Assert.Equal(LibrarySeeder.FrameCount, page.TotalFrames);
        Assert.Equal(LibrarySeeder.TotalIntegrationSeconds, page.TotalIntegrationSeconds);

        foreach (var seeded in LibrarySeeder.Targets)
        {
            var row = page.Rows.Single(candidate => candidate.TargetId == seeded.Id);
            Assert.Equal(seeded.PrimaryName, row.Name);
            Assert.Equal(seeded.CommonName, row.CommonName);
            Assert.Equal(seeded.CatalogId, row.CatalogId);
            Assert.Equal(seeded.ObjectCategory, row.ObjectCategory);
            Assert.Equal(seeded.FrameCount, row.FrameCount);
            Assert.Equal(seeded.SessionCount, row.SessionCount);
            Assert.Equal(seeded.IntegrationSeconds, row.IntegrationSeconds);
            Assert.Equal(seeded.FirstSession, row.FirstSession);
            Assert.Equal(seeded.LastSession, row.LastSession);
            Assert.Equal(seeded.Aliases, row.Aliases);
        }
    }

    // ---- metric range filters (spec 12.2.1) --------------------------------------------

    [Fact]
    public void List_MetricRange_GroupWithOneInRangeAndOneOutOfRangeFrame_IsExcluded()
    {
        using var library = Library.Empty();
        library.AddGroup("Good", frame => frame.MedianHfr = 2.0, frame => frame.MedianHfr = 2.5);
        library.AddGroup("Mixed", frame => frame.MedianHfr = 2.0, frame => frame.MedianHfr = 5.0);

        var page = library.Query.List(Unsorted() with
        {
            MetricRanges = new Dictionary<string, MetricRange> { ["hfr"] = new(null, 3.0) },
        });

        Assert.Equal(["Good"], page.Rows.Select(row => row.Name));
        Assert.Equal(1, page.TotalGroups);
    }

    [Fact]
    public void List_MetricRange_GroupWithAllNullValues_IsIncluded()
    {
        using var library = Library.Empty();
        library.AddGroup("AllNull", frame => frame.MedianHfr = null, frame => frame.MedianHfr = null);

        var page = library.Query.List(Unsorted() with
        {
            MetricRanges = new Dictionary<string, MetricRange> { ["hfr"] = new(1.0, 3.0) },
        });

        var row = Assert.Single(page.Rows);
        Assert.Equal("AllNull", row.Name);
    }

    [Fact]
    public void List_MetricRange_OnlyTheBoundTheUserSet_ContributesAClause()
    {
        using var library = Library.Empty();
        library.AddGroup("Wide", frame => frame.MedianHfr = 2.0, frame => frame.MedianHfr = 9.0);

        var lowerBoundOnly = library.Query.List(Unsorted() with
        {
            MetricRanges = new Dictionary<string, MetricRange> { ["hfr"] = new(1.0, null) },
        });
        var bothBounds = library.Query.List(Unsorted() with
        {
            MetricRanges = new Dictionary<string, MetricRange> { ["hfr"] = new(1.0, 3.0) },
        });

        Assert.Single(lowerBoundOnly.Rows);
        Assert.Empty(bothBounds.Rows);
    }

    [Fact]
    public void List_MetricRange_EachMetricEvaluatedIndependently()
    {
        using var library = Library.Empty();
        library.AddGroup(
            "SharpButElongated",
            frame => { frame.MedianHfr = 2.0; frame.Eccentricity = 0.9; },
            frame => { frame.MedianHfr = 2.1; frame.Eccentricity = 0.9; });
        library.AddGroup(
            "SharpAndRound",
            frame => { frame.MedianHfr = 2.0; frame.Eccentricity = 0.3; },
            frame => { frame.MedianHfr = 2.1; frame.Eccentricity = 0.3; });

        var hfrOnly = library.Query.List(Unsorted() with
        {
            MetricRanges = new Dictionary<string, MetricRange> { ["hfr"] = new(null, 3.0) },
        });
        var both = library.Query.List(Unsorted() with
        {
            MetricRanges = new Dictionary<string, MetricRange>
            {
                ["hfr"] = new(null, 3.0),
                ["eccentricity"] = new(null, 0.5),
            },
        });

        Assert.Equal(2, hfrOnly.Rows.Count);
        Assert.Equal(["SharpAndRound"], both.Rows.Select(row => row.Name));
    }

    [Fact]
    public void List_MetricRange_UnknownMetricKey_IsDropped()
    {
        using var library = Library.Empty();
        library.AddGroup("Anything", frame => frame.MedianHfr = 7.0);

        var page = library.Query.List(Unsorted() with
        {
            MetricRanges = new Dictionary<string, MetricRange>
            {
                ["definitely_not_a_metric"] = new(0.0, 0.1),
                ["median_hfr"] = new(0.0, 0.1),
            },
        });

        Assert.Single(page.Rows);
    }

    [Theory]
    [InlineData("fwhm")]
    [InlineData("stars")]
    [InlineData("guiding_rms")]
    [InlineData("adu_mean")]
    [InlineData("focuser_temp")]
    [InlineData("ambient_temp")]
    [InlineData("humidity")]
    [InlineData("airmass")]
    public void List_MetricRange_EveryMappedMetricKey_FiltersOnItsOwnColumn(string metricKey)
    {
        using var library = Library.Empty();
        library.AddGroup("InRange", frame => Set(frame, metricKey, 5));
        library.AddGroup("OutOfRange", frame => Set(frame, metricKey, 500));

        var page = library.Query.List(Unsorted() with
        {
            MetricRanges = new Dictionary<string, MetricRange> { [metricKey] = new(1, 100) },
        });

        Assert.Equal(["InRange"], page.Rows.Select(row => row.Name));

        static void Set(Image frame, string key, double value)
        {
            switch (key)
            {
                case "fwhm": frame.Fwhm = value; break;
                case "stars": frame.DetectedStars = (int)value; break;
                case "guiding_rms": frame.GuidingRmsArcsec = value; break;
                case "adu_mean": frame.AduMean = value; break;
                case "focuser_temp": frame.FocuserTemp = value; break;
                case "ambient_temp": frame.AmbientTemp = value; break;
                case "humidity": frame.Humidity = value; break;
                case "airmass": frame.Airmass = value; break;
                default: throw new ArgumentOutOfRangeException(nameof(key), key, "unmapped metric key");
            }
        }
    }

    // ---- alias-aware criteria ----------------------------------------------------------

    [Fact]
    public void List_FilterCriterion_ExpandsCanonicalThroughAliasMap()
    {
        using var library = Library.Empty();
        library.Settings.SaveFilters(new Dictionary<string, FilterSetting>
        {
            ["OIII"] = new() { Color = "#00b0b0", Aliases = ["O3"] },
        });
        library.AddGroup("Oxygen", frame => frame.FilterUsed = "o3", frame => frame.FilterUsed = "Oiii");
        library.AddGroup("Hydrogen", frame => frame.FilterUsed = "Ha");

        var page = library.Query.List(Unsorted() with { Filters = ["OIII"] });

        var row = Assert.Single(page.Rows);
        Assert.Equal("Oxygen", row.Name);
        Assert.Equal(2, row.FrameCount);
    }

    [Fact]
    public void List_CameraAndTelescopeCriteria_ExpandThroughAliasMap()
    {
        using var library = Library.Empty();
        library.Settings.SaveEquipment(new EquipmentSettings
        {
            Cameras = new Dictionary<string, EquipmentItemSettings> { ["ASI2600MM Pro"] = new() { Aliases = ["ZWO ASI2600MM"] } },
            Telescopes = new Dictionary<string, EquipmentItemSettings> { ["RC8"] = new() { Aliases = ["GSO RC 8\""] } },
        });
        library.AddGroup("RigA", frame => { frame.Camera = "zwo asi2600mm"; frame.Telescope = "GSO RC 8\""; });
        library.AddGroup("RigB", frame => { frame.Camera = "ASI294MC"; frame.Telescope = "FRA600"; });

        var byCamera = library.Query.List(Unsorted() with { Camera = "ASI2600MM Pro" });
        var byTelescope = library.Query.List(Unsorted() with { Telescope = "RC8" });

        Assert.Equal(["RigA"], byCamera.Rows.Select(row => row.Name));
        Assert.Equal(["RigA"], byTelescope.Rows.Select(row => row.Name));
    }

    [Fact]
    public void List_DateRange_IsInclusiveOnBothBounds()
    {
        using var library = Library.Empty();
        var target = library.AddTarget("IC 1396");
        library.AddFrame(target.Id, new DateOnly(2025, 1, 1));
        library.AddFrame(target.Id, new DateOnly(2025, 1, 5));
        library.AddFrame(target.Id, new DateOnly(2025, 1, 10));
        library.AddFrame(target.Id, new DateOnly(2025, 1, 15));

        var page = library.Query.List(Unsorted() with
        {
            SessionDateFrom = new DateOnly(2025, 1, 5),
            SessionDateTo = new DateOnly(2025, 1, 10),
        });

        var row = Assert.Single(page.Rows);
        Assert.Equal(2, row.FrameCount);
        Assert.Equal(new DateOnly(2025, 1, 5), row.FirstSession);
        Assert.Equal(new DateOnly(2025, 1, 10), row.LastSession);
    }

    // ---- object type categories (spec 9.8) ---------------------------------------------

    [Fact]
    public void List_ObjectCategories_SelectsOnlyMatchingTargets()
    {
        using var library = Library.Seeded();

        var page = library.Query.List(Unsorted() with { ObjectCategories = ["Galaxy", "Planetary Nebula"] });

        Assert.Equal(["M 27", "M 31"], page.Rows.Select(row => row.Name).Order());
    }

    [Fact]
    public void List_ObjectCategories_UnresolvedSelectsOnlyObjGroups()
    {
        using var library = Library.Seeded();
        library.AddFrame(null, Day, frame => frame.RawHeaders = LibrarySeeder.RawHeadersWithObject("Barnard 150"));

        var unresolvedOnly = library.Query.List(Unsorted() with { ObjectCategories = ["Unresolved"] });
        var unresolvedPlusGalaxy = library.Query.List(Unsorted() with { ObjectCategories = ["Unresolved", "Galaxy"] });

        Assert.Equal(["obj:Barnard 150"], unresolvedOnly.Rows.Select(row => row.GroupKey));
        Assert.Equal(["Barnard 150", "M 31"], unresolvedPlusGalaxy.Rows.Select(row => row.Name).Order());
    }

    [Fact]
    public void List_ObjectCategories_NoTargetMatches_ReturnsNothingRatherThanEverything()
    {
        using var library = Library.Seeded();

        var page = library.Query.List(Unsorted() with { ObjectCategories = ["Dark Nebula"] });

        Assert.Empty(page.Rows);
        Assert.Equal(0, page.TotalGroups);
        Assert.Equal(0, page.TotalFrames);
    }

    // ---- pinned selections (Task 8 feeds these) ----------------------------------------

    [Fact]
    public void List_TargetIdCriterion_PinsExactlyOneGroup()
    {
        using var library = Library.Seeded();
        var pinned = LibrarySeeder.Targets[2];

        var page = library.Query.List(Unsorted() with { TargetId = pinned.Id });

        var row = Assert.Single(page.Rows);
        Assert.Equal(pinned.Id, row.TargetId);
        Assert.Equal(pinned.FrameCount, page.TotalFrames);
    }

    [Fact]
    public void List_UnresolvedObjectCriterion_PinsExactlyOneObjGroup()
    {
        using var library = Library.Seeded();
        library.AddFrame(null, Day, frame => frame.RawHeaders = LibrarySeeder.RawHeadersWithObject("Barnard 150"));
        library.AddFrame(null, Day, frame => frame.RawHeaders = LibrarySeeder.RawHeadersWithObject("Barnard 33"));

        var page = library.Query.List(Unsorted() with { UnresolvedObject = "Barnard 150" });

        var row = Assert.Single(page.Rows);
        Assert.Equal("obj:Barnard 150", row.GroupKey);
        Assert.Equal(1, page.TotalFrames);
    }

    // FIXER LIST F1: an unquoted numeric OBJECT card is stored as a JSON number, so json_extract
    // returns a NUMBER and SQLite never equates it to the TEXT the page handed back. The pin now
    // compares the group-key expression, which concatenates 'obj:' and therefore forces text.
    [Fact]
    public void List_UnresolvedObjectCriterion_PinsANumericObjectGroup()
    {
        using var library = Library.Empty();
        library.AddFrame(null, Day, frame => frame.RawHeaders = """{"OBJECT": 7331}""");
        library.AddFrame(null, Day, frame => frame.RawHeaders = LibrarySeeder.RawHeadersWithObject("Barnard 33"));

        var unpinned = library.Query.List(Unsorted()).Rows.Select(row => row.GroupKey).Order();
        Assert.Equal(["obj:7331", "obj:Barnard 33"], unpinned);

        var page = library.Query.List(Unsorted() with { UnresolvedObject = "7331" });

        var row = Assert.Single(page.Rows);
        Assert.Equal("obj:7331", row.GroupKey);
        Assert.Equal(1, page.TotalFrames);
    }

    // FIXER LIST F2: the collapsed group is pinnable like any other, through the public constant.
    [Fact]
    public void List_UnresolvedObjectCriterion_PinsTheUncategorizedGroup()
    {
        using var library = Library.Empty();
        library.AddFrame(null, Day, frame => frame.RawHeaders = null);
        library.AddFrame(null, Day, frame => frame.RawHeaders = LibrarySeeder.RawHeadersWithObject("Barnard 33"));

        var page = library.Query.List(
            Unsorted() with { UnresolvedObject = TargetListingCriteria.UncategorizedObject });

        var row = Assert.Single(page.Rows);
        Assert.Equal("obj:__uncategorized__", row.GroupKey);
        Assert.Equal("Uncategorized", row.Name);
        Assert.Equal(1, page.TotalFrames);
    }

    // FIXER LIST F3: AnyFilterActive drives the "filtered" marker and the choice between spec
    // 12.10's two empty states, so it has to agree with the SQL exactly. A blank entry contributes
    // no clause and an unmapped metric key is dropped like an invalid header key.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnyFilterActive_IsFalse_ForABlankFilterOrCategoryEntry(string blank)
    {
        Assert.False(new TargetListingCriteria { Filters = [blank] }.AnyFilterActive);
        Assert.False(new TargetListingCriteria { ObjectCategories = [blank] }.AnyFilterActive);
    }

    [Fact]
    public void AnyFilterActive_IsFalse_ForAnUnmappedMetricKey_AndTrueForAMappedOne()
    {
        var unmapped = new TargetListingCriteria
        {
            MetricRanges = new Dictionary<string, MetricRange> { ["not_a_metric"] = new(1, null) },
        };
        var mapped = new TargetListingCriteria
        {
            MetricRanges = new Dictionary<string, MetricRange> { ["hfr"] = new(1, null) },
        };

        Assert.False(unmapped.AnyFilterActive);
        Assert.True(mapped.AnyFilterActive);
    }

    [Fact]
    public void List_BlankFilterAndCategoryEntriesAndUnmappedMetricKeys_NarrowNothing()
    {
        using var library = Library.Seeded();
        var unfiltered = library.Query.List(Unsorted() with { PageSize = 100 });

        var page = library.Query.List(Unsorted() with
        {
            PageSize = 100,
            Filters = ["  "],
            ObjectCategories = [""],
            MetricRanges = new Dictionary<string, MetricRange> { ["not_a_metric"] = new(0, 1) },
        });

        Assert.Equal(unfiltered.TotalGroups, page.TotalGroups);
        Assert.Equal(unfiltered.TotalFrames, page.TotalFrames);
    }

    // ---- paging and totals -------------------------------------------------------------

    [Fact]
    public void List_Aggregates_DescribeTheSameSetAsThePage()
    {
        using var library = Library.Seeded();
        var criteria = Unsorted(TargetListingSort.Frames) with { PageSize = 2 };

        var first = library.Query.List(criteria);
        var seenGroups = 0;
        var seenFrames = 0;
        var seenIntegration = 0d;
        for (var page = 1; ; page++)
        {
            var slice = library.Query.List(criteria with { Page = page });
            if (slice.Rows.Count == 0)
            {
                break;
            }

            seenGroups += slice.Rows.Count;
            seenFrames += slice.Rows.Sum(row => row.FrameCount);
            seenIntegration += slice.Rows.Sum(row => row.IntegrationSeconds);
        }

        Assert.Equal(first.TotalGroups, seenGroups);
        Assert.Equal(first.TotalFrames, seenFrames);
        Assert.Equal(first.TotalIntegrationSeconds, seenIntegration);
    }

    [Fact]
    public void List_Paging_SecondPageContinuesWhereTheFirstEnded_NoOverlapNoGap()
    {
        using var library = Library.Seeded();
        var criteria = Unsorted(TargetListingSort.LastSession, descending: true) with { PageSize = 4 };

        var all = library.Query.List(criteria with { PageSize = 100 }).Rows.Select(row => row.GroupKey).ToList();
        var first = library.Query.List(criteria).Rows.Select(row => row.GroupKey).ToList();
        var second = library.Query.List(criteria with { Page = 2 }).Rows.Select(row => row.GroupKey).ToList();

        Assert.Equal(4, first.Count);
        Assert.Equal(LibrarySeeder.TargetCount - 4, second.Count);
        Assert.Empty(first.Intersect(second));
        Assert.Equal(all, first.Concat(second));
    }

    [Fact]
    public void List_PageBeyondTheLastPage_ReturnsNoRowsButCorrectTotals()
    {
        using var library = Library.Seeded();

        var page = library.Query.List(Unsorted() with { Page = 99, PageSize = 10 });

        Assert.Empty(page.Rows);
        Assert.Equal(LibrarySeeder.TargetCount, page.TotalGroups);
        Assert.Equal(LibrarySeeder.FrameCount, page.TotalFrames);
        Assert.Equal(LibrarySeeder.TotalIntegrationSeconds, page.TotalIntegrationSeconds);
    }

    [Fact]
    public void List_EmptyLibrary_ReturnsZeroRowsAndZeroTotals()
    {
        using var library = Library.Empty();

        var page = library.Query.List(Unsorted());

        Assert.Empty(page.Rows);
        Assert.Equal(0, page.TotalGroups);
        Assert.Equal(0, page.TotalFrames);
        Assert.Equal(0d, page.TotalIntegrationSeconds);
        Assert.Equal(1, page.Page);
    }

    [Fact]
    public void List_NonPositivePageAndPageSize_AreClampedRatherThanProducingANegativeOffset()
    {
        using var library = Library.Seeded();

        var page = library.Query.List(Unsorted() with { Page = 0, PageSize = -5 });

        Assert.Single(page.Rows);
        Assert.Equal(1, page.Page);
        Assert.Equal(1, page.PageSize);
        Assert.Equal(LibrarySeeder.TargetCount, page.TotalGroups);
    }

    // ---- sorting -----------------------------------------------------------------------

    [Theory]
    [InlineData(TargetListingSort.Name)]
    [InlineData(TargetListingSort.Integration)]
    [InlineData(TargetListingSort.Frames)]
    [InlineData(TargetListingSort.Sessions)]
    [InlineData(TargetListingSort.LastSession)]
    [InlineData(TargetListingSort.Equipment)]
    public void List_EachSortKey_OrdersAscendingAndDescending(TargetListingSort sort)
    {
        using var library = Library.Seeded();
        var criteria = Unsorted(sort) with { PageSize = 100 };

        var ascending = library.Query.List(criteria).Rows;
        var descending = library.Query.List(criteria with { Descending = true }).Rows;

        Assert.Equal(LibrarySeeder.TargetCount, ascending.Count);
        Assert.Equal(LibrarySeeder.TargetCount, descending.Count);

        var ascendingKeys = ascending.Select(row => SortKey(row, sort)).ToList();
        var descendingKeys = descending.Select(row => SortKey(row, sort)).ToList();
        Assert.Equal(ascendingKeys.Order(), ascendingKeys);
        Assert.Equal(descendingKeys.OrderDescending(), descendingKeys);
    }

    // Phase review item 7: an unresolved group's key is 'obj:' || OBJECT, so sorting by name on the
    // raw key filed every unresolved group under "o" regardless of the name the Name column shows.
    [Fact]
    public void List_SortByName_UsesTheDisplayName_NotTheObjPrefixedGroupKey()
    {
        using var library = Library.Empty();
        var zeta = library.AddTarget("Zeta");
        library.AddFrame(zeta.Id, Day);

        // "Milky" is the discriminator: the raw key "obj:andromeda-ish" sorts AFTER "milky", so a
        // sort on the unstripped key puts the unresolved group in the wrong place.
        var milky = library.AddTarget("Milky");
        library.AddFrame(milky.Id, Day);
        library.AddFrame(null, Day, frame => frame.RawHeaders = LibrarySeeder.RawHeadersWithObject("Andromeda-ish"));

        var rows = library.Query.List(Unsorted(TargetListingSort.Name)).Rows;

        Assert.Equal(["Andromeda-ish", "Milky", "Zeta"], rows.Select(row => row.Name));
    }

    [Fact]
    public void List_SortIsStable_TiebrokenByGroupKey()
    {
        using var library = Library.Seeded();

        // Targets 0 and 2 hold the same frame count, as do 1 and 3, so only the group key
        // tiebreaker can decide their order, and it decides it the same way in both directions.
        var ascending = library.Query.List(Unsorted(TargetListingSort.Frames) with { PageSize = 100 }).Rows;
        var descending = library.Query.List(Unsorted(TargetListingSort.Frames, descending: true) with { PageSize = 100 }).Rows;

        foreach (var rows in new[] { ascending, descending })
        {
            var tied = rows.Where(row => row.FrameCount == LibrarySeeder.Targets[0].FrameCount).ToList();
            Assert.Equal(2, tied.Count);
            Assert.Equal(tied.Select(row => row.GroupKey).Order(), tied.Select(row => row.GroupKey));
        }
    }

    private static IComparable SortKey(TargetRow row, TargetListingSort sort) => sort switch
    {
        TargetListingSort.Name => row.Name.ToLowerInvariant(),
        TargetListingSort.Integration => row.IntegrationSeconds,
        TargetListingSort.Frames => row.FrameCount,
        TargetListingSort.Sessions => row.SessionCount,
        TargetListingSort.LastSession => row.LastSession!.Value,
        // The query sorts on min(telescope || ' ' || camera) over the raw values; with no alias
        // configured the canonical rig strings order identically.
        TargetListingSort.Equipment => row.Equipment[0].ToLowerInvariant(),
        _ => throw new ArgumentOutOfRangeException(nameof(sort)),
    };

    // ---- the second pass ---------------------------------------------------------------

    [Fact]
    public void List_SecondPass_PaletteEquipmentSessionsAndAliases_CoverOnlyThePagesGroups()
    {
        using var library = Library.Seeded();

        var page = library.Query.List(Unsorted(TargetListingSort.Name) with { PageSize = 1 });

        var row = Assert.Single(page.Rows);
        var seeded = LibrarySeeder.Targets.Single(target => target.Id == row.TargetId);
        Assert.Equal(seeded.FrameCount, row.Palette.Sum(badge => badge.FrameCount));
        Assert.Equal(seeded.IntegrationSeconds, row.Palette.Sum(badge => badge.IntegrationSeconds));
        Assert.Equal(seeded.SessionCount, row.Sessions.Count);
        Assert.Equal(seeded.FrameCount, row.Sessions.Sum(session => session.FrameCount));
        Assert.Equal(row.Sessions.Select(session => session.SessionDate).OrderDescending(), row.Sessions.Select(session => session.SessionDate));
        Assert.Equal(seeded.Aliases, row.Aliases);
        Assert.NotEmpty(row.Equipment);
        Assert.All(row.Equipment, rig => Assert.Contains(" / ", rig));
    }

    [Fact]
    public void List_Sessions_ARowWhoseSessionDateDoesNotParse_IsSkippedAndTheTargetStillLists()
    {
        using var library = Library.Empty();
        var target = library.AddTarget("NGC 7000");
        library.AddFrame(target.Id, Day);
        var bad = library.AddFrame(target.Id, Day.AddDays(1));

        // The per-session statement filters on session_date IS NOT NULL, and nothing constrains the
        // stored text to yyyy-MM-dd, so a hand-edited or imported row can hold ''. Before review
        // P2-3's fix the !.Value on that row threw FormatException out of the enrichment read and
        // the whole dashboard listing went with it: the user saw no targets at all, not one target
        // short of a session.
        library.SetRawSessionDate(bad.Id, "");

        var page = library.Query.List(Unsorted());

        var row = Assert.Single(page.Rows);
        Assert.Equal("NGC 7000", row.Name);

        // The good session is listed and the unparseable one is simply not there, which is what
        // the fifth result set already does with the same row for the Filters column.
        var session = Assert.Single(row.Sessions);
        Assert.Equal(Day, session.SessionDate);
    }

    [Fact]
    public void List_SecondPass_DescribesTheFilteredSetRatherThanTheWholeGroup()
    {
        using var library = Library.Empty();
        var target = library.AddTarget("NGC 281");
        library.AddFrame(target.Id, new DateOnly(2025, 1, 1), frame => frame.FilterUsed = "Ha");
        library.AddFrame(target.Id, new DateOnly(2025, 2, 1), frame => frame.FilterUsed = "OIII");

        var page = library.Query.List(Unsorted() with { SessionDateTo = new DateOnly(2025, 1, 31) });

        var row = Assert.Single(page.Rows);
        var badge = Assert.Single(row.Palette);
        Assert.Equal("Ha", badge.CanonicalName);
        Assert.Single(row.Sessions);
    }

    [Fact]
    public void List_Palette_FoldsRawFilterNamesToCanonicalAndCarriesConfiguredColour()
    {
        using var library = Library.Empty();
        library.Settings.SaveFilters(new Dictionary<string, FilterSetting>
        {
            ["OIII"] = new() { Color = "#3ba7ff", Aliases = ["O3", "Oiii"] },
        });
        library.AddGroup("Veil", frame => frame.FilterUsed = "o3", frame => frame.FilterUsed = "OIII", frame => frame.FilterUsed = "Oiii");

        var page = library.Query.List(Unsorted());

        var row = Assert.Single(page.Rows);
        var badge = Assert.Single(row.Palette);
        Assert.Equal("OIII", badge.CanonicalName);
        Assert.Equal("#3ba7ff", badge.Color);
        Assert.Equal(3, badge.FrameCount);
        Assert.Equal(3 * LibrarySeeder.ExposureSeconds, badge.IntegrationSeconds);
    }

    [Fact]
    public void List_Palette_NullFilterUsed_BecomesUnknownBadge()
    {
        using var library = Library.Empty();
        library.AddGroup(
            "NoFilterRecorded",
            frame => frame.FilterUsed = null,
            frame => frame.FilterUsed = "",
            frame => frame.FilterUsed = "L");

        var page = library.Query.List(Unsorted());

        var row = Assert.Single(page.Rows);
        var unknown = row.Palette.Single(badge => badge.CanonicalName == "Unknown");
        Assert.Equal("#808080", unknown.Color);
        Assert.Equal(2, unknown.FrameCount);
        Assert.Equal(row.FrameCount, row.Palette.Sum(badge => badge.FrameCount));
    }

    // ---- the fixture and the performance ceiling ---------------------------------------

    [Fact]
    public void Seed_ProducesTheDeclaredSpec18_2Fixture()
    {
        using var library = Library.Seeded();

        Assert.Equal(LibrarySeeder.FrameCount, LibrarySeeder.Targets.Sum(target => target.FrameCount));
        Assert.Equal(LibrarySeeder.SessionCount, LibrarySeeder.Targets.Sum(target => target.SessionCount));
        Assert.Equal(LibrarySeeder.TargetCount, LibrarySeeder.Targets.Count);
        Assert.Equal(new DateOnly(2025, 12, 7), LibrarySeeder.LastSessionDate);

        var page = library.Query.List(Unsorted() with { PageSize = 100 });
        Assert.Equal(LibrarySeeder.FrameCount, page.TotalFrames);
        Assert.Equal(LibrarySeeder.SessionCount, page.Rows.Sum(row => row.SessionCount));
        Assert.Equal(2, page.Rows.SelectMany(row => row.Equipment).Distinct().Count());
        Assert.Equal(LibrarySeeder.Filters.Count, page.Rows.SelectMany(row => row.Palette).Select(badge => badge.CanonicalName).Distinct().Count());
    }

    [Fact]
    public void List_OnTheSeededLibrary_StaysUnderTheSpec12_2Ceiling()
    {
        using var library = Library.Seeded();
        var criteria = Unsorted(TargetListingSort.LastSession, descending: true) with
        {
            PageSize = 50,
            MetricRanges = new Dictionary<string, MetricRange> { ["hfr"] = new(0.5, 9.0) },
        };

        library.Query.List(criteria);
        library.Facets.Load();

        var listing = Measure(() => library.Query.List(criteria));
        var facets = Measure(() => library.Facets.Load());
        output.WriteLine($"seeded {LibrarySeeder.FrameCount} frames / {LibrarySeeder.TargetCount} groups: listing page {listing:F1} ms, facets {facets:F1} ms");

        // Spec 12.2's stated ceiling is 200 ms on a 50 000 frame library. The bar here is
        // deliberately loose: it catches an accidental N+1 or a cartesian join, not a slow
        // build agent.
        Assert.True(listing < 500, $"listing page took {listing:F1} ms");
        Assert.True(facets < 500, $"facets took {facets:F1} ms");
    }

    private static double Measure(Action action)
    {
        var best = double.MaxValue;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var stopwatch = Stopwatch.StartNew();
            action();
            stopwatch.Stop();
            best = Math.Min(best, stopwatch.Elapsed.TotalMilliseconds);
        }

        return best;
    }
}
