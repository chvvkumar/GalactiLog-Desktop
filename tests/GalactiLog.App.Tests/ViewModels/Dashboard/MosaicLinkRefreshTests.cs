using GalactiLog.App.Tests.TestSupport;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels.Dashboard;

// Phase 18 final review item 6: spec 12.2's mosaic link appears after accept or create and goes
// after delete without waiting for a scan. MosaicRepository.Changed is the one route (its half is
// MosaicRepositoryTests); this is the dashboard's half, the page-level call the host route makes.
public class MosaicLinkRefreshTests
{
    private static TargetListingPage Page(bool linked)
        => new(
            [
                new TargetRow(
                    GroupKey: "ngc-7000",
                    TargetId: Guid.Empty,
                    Name: "NGC 7000",
                    CommonName: null,
                    CatalogId: "NGC 7000",
                    ObjectType: "EN",
                    ObjectCategory: "Nebula",
                    IntegrationSeconds: 3_600d,
                    FrameCount: 12,
                    SessionCount: 1,
                    FirstSession: new DateOnly(2026, 3, 14),
                    LastSession: new DateOnly(2026, 3, 14),
                    Palette: [],
                    Equipment: [],
                    Aliases: [],
                    Sessions: [])
                {
                    Mosaics = linked ? [new MosaicLink(Guid.NewGuid(), "North America")] : [],
                },
            ],
            1, 3_600d, 12, 1, 50);

    [Fact]
    public void RefreshMosaicLinks_RequeriesTheListing_SoTheLinkFollowsTheWrite()
    {
        var linked = false;
        using var dashboard = DashboardViewModelTestFactory.Create(list: _ => Page(linked));
        Assert.False(Assert.Single(dashboard.Targets.Rows).HasMosaic);

        linked = true;
        dashboard.RefreshMosaicLinks();
        DashboardViewModelTestFactory.Settle(dashboard);
        Assert.Equal("North America", Assert.Single(dashboard.Targets.Rows).MosaicName);

        linked = false;
        dashboard.RefreshMosaicLinks();
        DashboardViewModelTestFactory.Settle(dashboard);
        Assert.False(Assert.Single(dashboard.Targets.Rows).HasMosaic);
    }
}
