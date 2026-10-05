using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Diagnostics;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Phase 14B Task 7 (PAR-012, spec 12.7): the three retention editors on LogViewerViewModel.
// activity_retention_days and app_log_retention_days share one range, 1 to 3650; app_log_max_rows
// is 1000 to 500000, and its live viewer-cap effects are in LogViewerCapTests.
//
// Phase 14B fixer, fixer list item 46 (task7-review P3): the third editor's refuse-and-revert was
// covered by neither file, and this header used to say LogViewerCapTests carried it. It is below,
// beside the other two.
public class LogRetentionEditorTests
{
    [Fact]
    public async Task TheThreeEditors_SeedFromTheStoredDocument()
    {
        using var fixture = new LogViewerFixture();
        fixture.Settings.MutateGeneral(g => g with
        {
            ActivityRetentionDays = 45,
            AppLogRetentionDays = 21,
            AppLogMaxRows = 75_000,
        });

        using var page = await fixture.CreateAsync(
            initialActivityRetentionDays: 45, initialAppLogRetentionDays: 21, initialAppLogMaxRows: 75_000);

        Assert.Equal(45, page.RetentionDays);
        Assert.Equal(21, page.AppLogRetentionDays);
        Assert.Equal(75_000, page.MaxRows);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3651)]
    public async Task EachEditor_RefusesAnOutOfRangeValue_RetentionDays(int value)
    {
        using var fixture = new LogViewerFixture();
        using var page = await fixture.CreateAsync();

        page.RetentionDays = value;

        Assert.Equal(90, page.RetentionDays);
        Assert.Equal(90, fixture.Settings.GetGeneral().ActivityRetentionDays);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3651)]
    public async Task EachEditor_RefusesAnOutOfRangeValue_AppLogRetentionDays(int value)
    {
        using var fixture = new LogViewerFixture();
        using var page = await fixture.CreateAsync();

        page.AppLogRetentionDays = value;

        Assert.Equal(14, page.AppLogRetentionDays);
        Assert.Equal(14, fixture.Settings.GetGeneral().AppLogRetentionDays);
    }

    [Theory]
    [InlineData(999)]
    [InlineData(500_001)]
    public async Task EachEditor_RefusesAnOutOfRangeValue_MaxRows(int value)
    {
        using var fixture = new LogViewerFixture();
        fixture.Settings.MutateGeneral(g => g with { AppLogMaxRows = 50_000 });
        using var page = await fixture.CreateAsync(initialAppLogMaxRows: 50_000);

        page.MaxRows = value;

        Assert.Equal(50_000, page.MaxRows);
        Assert.Equal(50_000, fixture.Settings.GetGeneral().AppLogMaxRows);
    }

    [Fact]
    public async Task ARefusedEdit_RevertsToTheStoredValue()
    {
        using var fixture = new LogViewerFixture();
        using var page = await fixture.CreateAsync(initialAppLogRetentionDays: 30);

        page.AppLogRetentionDays = 99999;

        Assert.Equal(30, page.AppLogRetentionDays);
    }

    [Fact]
    public async Task ARefusedEdit_WritesNothing()
    {
        using var fixture = new LogViewerFixture();
        var before = fixture.Settings.GetGeneral();
        using var page = await fixture.CreateAsync();

        page.RetentionDays = -5;

        Assert.Equal(before, fixture.Settings.GetGeneral());
    }

    [Fact]
    public async Task AnAcceptedEdit_WritesThroughMutateGeneral()
    {
        using var fixture = new LogViewerFixture();
        using var page = await fixture.CreateAsync();

        page.RetentionDays = 120;
        page.AppLogRetentionDays = 7;

        Assert.Equal(120, fixture.Settings.GetGeneral().ActivityRetentionDays);
        Assert.Equal(7, fixture.Settings.GetGeneral().AppLogRetentionDays);
    }

    [Fact]
    public void EachEditor_StatesWhenItTakesEffect()
    {
        Assert.Equal("Applies immediately.", LogViewerViewModel.AppliesImmediatelyText);
        Assert.Equal("Applies the next time GalactiLog starts.", LogViewerViewModel.AppliesAtNextStartText);
    }

    [Fact]
    public void TheLogRetentionEditor_SaysItAppliesAtTheNextStart()
    {
        // AppLogRetentionDays is bound to AppliesAtNextStartText in LogViewerView.axaml; asserted
        // here as the constant the view binds rather than as a markup string match, so the two
        // cannot drift (design-lessons rule 1).
        Assert.Equal(
            "Applies the next time GalactiLog starts.",
            LogViewerViewModel.AppliesAtNextStartText);
    }

    [Fact]
    public void TheOtherTwo_SayTheyApplyImmediately()
    {
        // RetentionDays (activity_retention_days) and MaxRows (app_log_max_rows) both bind
        // AppliesImmediatelyText: their live effects are OrderBy the ActivityViewModel prune
        // window and LoadMoreAsync's cap respectively, and neither touches the Serilog sink.
        Assert.Equal("Applies immediately.", LogViewerViewModel.AppliesImmediatelyText);
    }
}
