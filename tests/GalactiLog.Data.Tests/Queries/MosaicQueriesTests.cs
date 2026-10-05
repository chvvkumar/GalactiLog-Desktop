using GalactiLog.Core.Mosaics;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.Data.Tests.Queries;

// Phase 18 Task 3. Spec 12.17's figures through the membership join of spec 5.24 (target, night
// and frame label against included rows, LIGHT frames only), the Available rule, the available
// labels banner, the dashboard link set of spec 12.2 and the CSV rows.
public class MosaicQueriesTests : IDisposable
{
    private const double Exposure = LibrarySeeder.ExposureSeconds;

    private readonly TestDatabaseHandle _db = TestDatabaseFactory.CreateMigratedDatabase();
    private readonly AliasMapCache _aliases;
    private readonly MosaicRepository _repository;
    private readonly MosaicQueries _queries;

    public MosaicQueriesTests()
    {
        _aliases = new AliasMapCache(new SettingsStore(new SettingsRepository(_db.ConnectionString)));
        _repository = new MosaicRepository(new DatabaseConnectionString(_db.ConnectionString));
        _queries = new MosaicQueries(new DatabaseConnectionString(_db.ConnectionString), _aliases);
    }

    public void Dispose()
    {
        _aliases.Dispose();
        _db.Dispose();
    }

    private static DateOnly Night(int day) => new(2026, 3, day);

    private Guid NewTarget(string name) => LibrarySeeder.AddTarget(_db.ConnectionString, name).Id;

    private void Frames(Guid target, int day, string? label, int count, string filter = "Ha", string imageType = "LIGHT", string? objectName = null)
    {
        for (var i = 0; i < count; i++)
        {
            LibrarySeeder.AddFrame(_db.ConnectionString, target, Night(day), image =>
            {
                image.PanelLabel = label;
                image.FilterUsed = filter;
                image.ImageType = imageType;
                if (objectName is not null) image.RawHeaders = LibrarySeeder.RawHeadersWithObject(objectName);
            });
        }
    }

    // One target, two panels shot on the same nights (ruling R19a): Panel 1 has 4 Ha and 2 OIII
    // frames on night 1 and 2 Ha on night 2; Panel 2 has 2 Ha on night 1. Night 3 carries 3 Panel 1
    // frames left available, a dark frame and frames of a third label nobody holds.
    private (Guid Mosaic, Guid Target, Guid First, Guid Second) Seeded()
    {
        var target = NewTarget("NGC 7000");
        Frames(target, 1, "Panel 1", 4);
        Frames(target, 1, "Panel 1", 2, filter: "OIII");
        Frames(target, 2, "Panel 1", 2);
        Frames(target, 1, "Panel 2", 2);
        Frames(target, 3, "Panel 1", 3);
        Frames(target, 1, "Panel 1", 5, imageType: "DARK");
        Frames(target, 2, "Panel 3", 1);

        var mosaic = _repository.Create("North America");
        var first = _repository.AddPanel(mosaic, "Panel 1");
        var second = _repository.AddPanel(mosaic, "Panel 2");
        _repository.IncludeNight(first, target, Night(1), "Panel 1");
        _repository.IncludeNight(first, target, Night(2), "Panel 1");
        _repository.IncludeNight(second, target, Night(1), "Panel 2");
        _repository.RemoveNight(first, target, Night(3), "Panel 1");
        return (mosaic, target, first, second);
    }

    [Fact]
    public void List_SumsThePanelsThroughTheJoin_LightFramesOfIncludedRowsOnly()
    {
        var (mosaic, _, _, _) = Seeded();
        var empty = _repository.Create("Empty");

        var rows = _queries.List();

        Assert.Equal(new[] { empty, mosaic }, rows.Select(row => row.Id));
        var row = rows[1];
        Assert.Equal(("North America", 2, 10, 10 * Exposure), (row.Name, row.Panels, row.Frames, row.IntegrationSeconds));
        Assert.Equal((Night(1), Night(2)), (row.FirstNight!.Value, row.LastNight!.Value));
        Assert.Equal(new[] { "Ha", "OIII" }, row.Filters);
        Assert.Equal((0, 0, (DateOnly?)null), (rows[0].Frames, rows[0].Panels, rows[0].FirstNight));
    }

    [Fact]
    public void Detail_GivesPerPanelFigures_TheDeficit_AndTheIncludedNightsNewestFirst()
    {
        var (mosaic, target, first, second) = Seeded();

        var detail = _queries.Detail(mosaic)!;

        Assert.Equal(("North America", 10, 10 * Exposure), (detail.Name, detail.Frames, detail.IntegrationSeconds));
        var one = detail.Panels[0];
        Assert.Equal((first, "Panel 1", 0, 8, 8 * Exposure, 2), (one.Id, one.Label, one.SortOrder, one.Frames, one.IntegrationSeconds, one.NightsIncluded));
        Assert.Equal(0d, one.DeficitSeconds);
        Assert.Equal([target], one.TargetIds);
        Assert.Equal(["NGC 7000"], one.TargetNames);
        Assert.Equal(new[] { Night(2), Night(1) }, one.Included.Select(night => night.Date));
        var night1 = one.Included[1];
        Assert.Equal(("NGC 7000", "Panel 1", 6, 6 * Exposure), (night1.TargetName, night1.FrameLabel, night1.Frames, night1.IntegrationSeconds));
        Assert.Equal(4, night1.FramesByFilter["Ha"]);
        Assert.Equal(2, night1.FramesByFilter["OIII"]);
        Assert.Equal(6 * Exposure, one.IntegrationByFilter["Ha"]);

        var two = detail.Panels[1];
        Assert.Equal((second, 2, 6 * Exposure), (two.Id, two.Frames, two.DeficitSeconds));
    }

    // Spec 12.17's Available: the contributing targets' triples except those included anywhere in
    // the mosaic, so night 1 Panel 2 (in the sibling) is hidden and night 3 is offered.
    [Fact]
    public void Detail_Available_ExcludesTriplesIncludedInAnyPanel()
    {
        var (mosaic, _, _, _) = Seeded();

        var detail = _queries.Detail(mosaic)!;

        var one = detail.Panels[0];
        Assert.Equal(
            new[] { (Night(3), (string?)"Panel 1", 3), (Night(2), "Panel 3", 1) },
            one.Available.Select(night => (night.Date, night.FrameLabel, night.Frames)));
        Assert.Equal(2, one.NightsAvailable);
        Assert.Equal(2, detail.Panels[1].NightsAvailable);
    }

    [Fact]
    public void Detail_AvailableLabels_AreLabelsOfContributingTargetsNoPanelCarries()
    {
        var (mosaic, target, _, _) = Seeded();
        var other = NewTarget("Pelican");
        Frames(other, 4, "panel 2", 1);
        Frames(other, 4, "Panel 9", 1);
        _repository.AddTargetNights(_repository.AddPanel(mosaic, "Panel 4"), other);

        var labels = _queries.Detail(mosaic)!.AvailableLabels;

        Assert.Equal(
            new[] { (target, "NGC 7000", "Panel 3"), (other, "Pelican", "Panel 9") },
            labels.Select(label => (label.TargetId, label.TargetName, label.Label)));
    }

    [Fact]
    public void Detail_OfAnUnknownMosaic_IsNull()
        => Assert.Null(_queries.Detail(Guid.NewGuid()));

    [Fact]
    public void Detail_WhenEveryPanelIsZero_NoPanelHasADeficit()
    {
        var mosaic = _repository.Create("Empty");
        _repository.AddPanel(mosaic, "Panel 1");
        _repository.AddPanel(mosaic, "Panel 2");

        Assert.All(_queries.Detail(mosaic)!.Panels, panel => Assert.Equal(0d, panel.DeficitSeconds));
    }

    // Spec 12.2: the link set is included rows only, and a target in several mosaics names them
    // all and opens the first by name.
    [Fact]
    public void MosaicLinks_AreIncludedRowsOnly_AndOpenTheFirstByName()
    {
        var (mosaic, target, _, _) = Seeded();
        var offeredOnly = NewTarget("Pelican");
        _repository.AddTargetNights(_repository.AddPanel(mosaic, "Panel 9"), offeredOnly);
        var earlier = _repository.Create("Cygnus wide");
        _repository.IncludeNight(_repository.AddPanel(earlier, "Panel 1"), target, Night(1), null);

        Assert.Equal([target], _queries.TargetsInAnyMosaic());
        Assert.Equal(earlier, _queries.MosaicByTarget()[target]);
        Assert.Equal(new[] { "Cygnus wide", "North America" }, _queries.MosaicLinksByTarget()[target].Select(link => link.Name));
    }

    [Fact]
    public void MosaicsIncludingTarget_CountsRowsOfEitherStatus_OrderedByName()
    {
        var (mosaic, target, _, _) = Seeded();
        var offered = _repository.Create("Another");
        _repository.AddTargetNights(_repository.AddPanel(offered, "Panel 1"), target);
        _repository.Create("Unrelated");

        Assert.Equal(new[] { offered, mosaic }, _queries.MosaicsIncludingTarget(target).Select(link => link.MosaicId));
    }

    [Fact]
    public void CsvRows_ProjectTheDetailOnePerPanel()
    {
        var (mosaic, _, _, _) = Seeded();

        var rows = MosaicQueries.CsvRows(_queries.Detail(mosaic)!);

        Assert.Equal(2, rows.Count);
        Assert.Equal(("Panel 1", 2, 8, 8 * Exposure), (rows[0].Label, rows[0].Nights, rows[0].Frames, rows[0].IntegrationSeconds));
        Assert.Equal(["NGC 7000"], rows[0].Targets);
        Assert.Equal(new[] { ("Ha", 6 * Exposure), ("OIII", 2 * Exposure) },
            rows[0].IntegrationByFilter.Select(pair => (pair.Key, pair.Value)));
    }

    // Spec 12.17's session table: one row per (entry, OBJECT, night, filter) of the entry
    // target's frames carrying the entry's label, flagged when the night is outside the campaign.
    [Fact]
    public void SuggestionSessions_ListTheEntriesFrames_AndFlagNightsOutsideTheCampaign()
    {
        var target = NewTarget("NGC 7000");
        Frames(target, 1, "Panel 1", 2, objectName: "NGC 7000 Panel 1");
        Frames(target, 1, "Panel 1", 1, filter: "OIII", objectName: "NGC 7000 P1");
        Frames(target, 9, "Panel 1", 1, objectName: "NGC 7000 Panel 1");
        Frames(target, 1, "Panel 2", 1, objectName: "NGC 7000 Panel 2");
        var panel = new SuggestionPanel(target, "Panel 1", "%NGC 7000%Panel%1%", [Night(1)]);
        var suggestion = new MosaicSuggestionRow(
            Guid.NewGuid(), "NGC 7000", "NGC 7000", [panel], "low", "name", null, [], "sig", DateTime.UtcNow);

        var rows = _queries.SuggestionSessions(suggestion);

        Assert.Equal(
            new[]
            {
                ("NGC 7000 P1", Night(1), (string?)"OIII", 1, true),
                ("NGC 7000 Panel 1", Night(1), "Ha", 2, true),
                ("NGC 7000 Panel 1", Night(9), "Ha", 1, false),
            },
            rows.Select(row => (row.ObjectName, row.Night, row.Filter, row.Frames, row.InCampaign)));
        Assert.All(rows, row => Assert.Equal((target, "Panel 1"), (row.TargetId, row.Label)));
    }
}
