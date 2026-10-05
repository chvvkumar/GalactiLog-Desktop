using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Phase 14B Task 6: spec 12.2's Filters column, at the view-model layer. TargetRowViewModel is
// where SessionSummary (GalactiLog.Data.Queries) becomes SessionRowViewModel; TargetListingQuery
// is the data-layer proof (SessionFiltersTests.cs), this is the projection's.
public class SessionExpanderTests
{
    private static TargetRow Row(string groupKey, IReadOnlyList<SessionSummary> sessions)
        => new(
            GroupKey: groupKey,
            TargetId: null,
            Name: "M 31",
            CommonName: null,
            CatalogId: null,
            ObjectType: null,
            ObjectCategory: "Galaxy",
            IntegrationSeconds: 0,
            FrameCount: 0,
            SessionCount: sessions.Count,
            FirstSession: sessions.Count > 0 ? sessions[^1].SessionDate : null,
            LastSession: sessions.Count > 0 ? sessions[0].SessionDate : null,
            Palette: [],
            Equipment: [],
            Aliases: [],
            Sessions: sessions);

    private static TargetRowViewModel Build(string groupKey, IReadOnlyList<SessionSummary> sessions)
        => new(Row(groupKey, sessions), []);

    [Fact]
    public void ASessionRow_CarriesItsRawDate()
    {
        var date = new DateOnly(2025, 12, 7);
        var row = Build("g1", [new SessionSummary(date, 10, 3_000d)]);

        Assert.Equal(date, Assert.Single(row.Sessions).SessionDate);
    }

    [Fact]
    public void ASessionRow_CarriesItsOwningGroupKey()
    {
        // The group key is passed down from the parent row at construction rather than the
        // session row holding a parent reference (task6.md 5.3); an unresolved obj: group's key
        // carries through exactly as a resolved target's does.
        var row = Build("obj:Bubble Neb", [new SessionSummary(new DateOnly(2025, 12, 7), 10, 3_000d)]);

        Assert.Equal("obj:Bubble Neb", Assert.Single(row.Sessions).GroupKey);
    }

    [Fact]
    public void ASessionRow_CarriesItsFiltersWithTheirTints()
    {
        var session = new SessionSummary(
            new DateOnly(2025, 12, 7), 10, 3_000d, [new FilterBadge("Ha", "#FF0000", 10, 3_000d)]);
        var row = Build("g1", [session]);

        var badge = Assert.Single(Assert.Single(row.Sessions).Filters);
        Assert.Equal("Ha", badge.CanonicalName);
        Assert.NotNull(badge.Tint);
    }

    [Fact]
    public void TheTint_ComesFromTheOneParser()
    {
        // FIXER F10, F29: TargetRowViewModel.ParseTint is the one filter-tint parser; the Palette
        // column's badges and the session Filters cell's badges must resolve through it and land
        // on the same fallback for an unparseable stored colour.
        var session = new SessionSummary(
            new DateOnly(2025, 12, 7), 10, 3_000d, [new FilterBadge("Ha", "not-a-colour", 10, 3_000d)]);
        var row = Build("g1", [session]);

        var badge = Assert.Single(Assert.Single(row.Sessions).Filters);
        Assert.Equal(TargetRowViewModel.ParseTint("not-a-colour").Color, badge.Tint.Color);
    }

    [Fact]
    public void ANightWithNoFilter_RendersNothingRatherThanAPlaceholder()
    {
        var row = Build("g1", [new SessionSummary(new DateOnly(2025, 12, 7), 10, 3_000d)]);

        Assert.Empty(Assert.Single(row.Sessions).Filters);
    }

    [Fact]
    public void DateTextIsUnchanged()
    {
        var row = Build("g1", [new SessionSummary(new DateOnly(2025, 12, 7), 10, 3_000d)]);

        Assert.Equal("2025-12-07", Assert.Single(row.Sessions).DateText);
    }

    [Fact]
    public void SessionRows_AreNewestFirst()
    {
        // The data layer already reads newest first (ORDER BY session_date DESC); this
        // view-model does no sorting of its own and must not reorder what it is handed.
        var row = Build(
            "g1",
            [
                new SessionSummary(new DateOnly(2025, 12, 7), 10, 3_000d),
                new SessionSummary(new DateOnly(2024, 1, 5), 5, 1_500d),
            ]);

        Assert.Equal(
            [new DateOnly(2025, 12, 7), new DateOnly(2024, 1, 5)],
            row.Sessions.Select(session => session.SessionDate));
    }

    // ---- Phase 14C Task 3 (spec 12.2, user ruling U3) -----------------------------------------

    // The three filters below are chosen so that alphabetical, frame count descending and the
    // order the query hands over are three different orders: alphabetical B, Ha, L; frame count
    // descending L, B, Ha; declared Ha, L, B. A fixture whose orders coincide proves nothing.
    private static readonly FilterBadge[] ThreeFilters =
    [
        new("Ha", "#FF0000", 5, 1_500d),
        new("L", "#FFFFFF", 900, 27_000d),
        new("B", "#4040FF", 40, 12_000d),
    ];

    [Fact]
    public void SessionFilters_AreInFilterOrder_WhateverOrderTheQueryReturned()
    {
        // TargetListingQuery returns a night's filters in the stored Filters tab order and its ten
        // Data cases pin that; the display order is applied here instead, once, in
        // TargetRowViewModel.SortBadges (spec 12.2, "a wire order, not a display order").
        var row = Build("g1", [new SessionSummary(new DateOnly(2025, 12, 7), 945, 40_500d, ThreeFilters)]);

        Assert.Equal(
            ["L", "B", "Ha"],
            Assert.Single(row.Sessions).Filters.Select(badge => badge.CanonicalName));
    }

    [Fact]
    public void PaletteBadges_AreInFilterOrder_WhateverOrderTheQueryReturned()
    {
        // The query returns the Palette by frame count descending, which would be L, B, Ha.
        var source = Row("g1", []) with { Palette = ThreeFilters };
        var row = new TargetRowViewModel(source, []);

        Assert.Equal(["L", "B", "Ha"], row.PaletteBadges.Select(badge => badge.CanonicalName));
    }

    [Fact]
    public void ABadge_FormatsItsFrameCountForTheTooltip_SingularAtOne()
    {
        var source = Row("g1", []) with
        {
            Palette = [new FilterBadge("Ha", "#FF0000", 60, 18_000d), new FilterBadge("B", "#4040FF", 1, 300d)],
        };
        var row = new TargetRowViewModel(source, []);

        // Alphabetical, so B is first. The visible text is the canonical name alone; the figure
        // only reads on the tooltip.
        Assert.Equal("B, 1 frame", row.PaletteBadges[0].FrameCountText);
        Assert.Equal("Ha, 60 frames", row.PaletteBadges[1].FrameCountText);
    }

    [Fact]
    public void TheSessionsToggleText_FlipsWithTheExpandedFlag()
    {
        var row = Build("g1", [new SessionSummary(new DateOnly(2025, 12, 7), 10, 3_000d)]);

        Assert.Equal("Expand", row.SessionsToggleText);

        row.IsExpanded = true;

        Assert.Equal("Collapse", row.SessionsToggleText);
    }
}
