using GalactiLog.App.Converters;
using GalactiLog.App.Tests.TestSupport;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Phase 14B Task 7 (PAR-017, spec 12.6). The rail badge count: events newer than the marker,
// ignoring every filter and the search, which ActivityViewModel.Total cannot answer (it is the
// filtered total).
public class UnseenActivityCountTests
{
    [Fact]
    public async Task TheCount_IsEventsNewerThanTheMarker()
    {
        DateTime? capturedSince = null;
        using var page = await ActivityViewModelTestFactory.CreateAsync(
            countUnseen: since =>
            {
                capturedSince = since;
                return 7;
            });

        Assert.Equal(7, page.UnseenCount);
        Assert.Null(capturedSince); // A fresh profile's marker is null.
    }

    [Fact]
    public async Task TheCount_IsNotTheFilteredPageTotal()
    {
        // The trap: Total is the filtered total the query reports. UnseenCount comes from the
        // countUnseen delegate alone and must not equal Total just because both happen to be
        // small numbers in a fixture with few rows.
        using var page = await ActivityViewModelTestFactory.CreateAsync(
            page: (_, _, _) => ActivityViewModelTestFactory.Page(
                [ActivityViewModelTestFactory.Row(1)], total: 42),
            countUnseen: _ => 3);

        Assert.Equal(42, page.Total);
        Assert.Equal(3, page.UnseenCount);
        Assert.NotEqual(page.Total, page.UnseenCount);
    }

    [Fact]
    public async Task TheCount_IgnoresTheSeverityAndCategoryFilters()
    {
        var calls = 0;
        using var page = await ActivityViewModelTestFactory.CreateAsync(countUnseen: _ =>
        {
            calls++;
            return 5;
        });

        var before = calls;
        page.SelectSeverityCommand.Execute(page.SeverityOptions[1]);
        ActivityViewModelTestFactory.Settle(page);

        // Selecting a pill reloads the filtered page; it does not re-run the unseen count, which
        // is unaffected by severity or category (spec 12.6).
        Assert.Equal(before, calls);
        Assert.Equal(5, page.UnseenCount);
    }

    [Fact]
    public async Task TheCount_IgnoresTheSearch()
    {
        var calls = 0;
        using var page = await ActivityViewModelTestFactory.CreateAsync(countUnseen: _ =>
        {
            calls++;
            return 5;
        });

        var before = calls;
        page.SearchText = "anything";
        page.RefreshCommand.Execute(null);
        ActivityViewModelTestFactory.Settle(page);

        Assert.Equal(before, calls);
        Assert.Equal(5, page.UnseenCount);
    }

    [Fact]
    public async Task ZeroUnseen_ShowsNoBadge()
    {
        using var page = await ActivityViewModelTestFactory.CreateAsync(countUnseen: _ => 0);

        Assert.Equal(0, page.UnseenCount);
    }

    [Fact]
    public async Task TheCount_DropsToZeroOnOpen()
    {
        var callCount = 0;
        using var page = await ActivityViewModelTestFactory.CreateAsync(countUnseen: _ => callCount++ == 0 ? 5 : 0);

        Assert.Equal(5, page.UnseenCount);

        page.MarkOpened();
        if (page.PendingOpen is { } pending)
        {
            await pending;
        }

        Assert.Equal(0, page.UnseenCount);
    }

    [Fact]
    public void TheRail_ShowsItOnlyOnTheActivityEntry()
    {
        // MainWindow.axaml gates the badge on a MultiBinding of these two converters: NavigationItem
        // stays a plain sealed class with no INotifyPropertyChanged (section 7.3), so the "only on
        // Activity" rule lives here rather than on the item itself. Views/ShellNavigationTests.cs
        // covers the rendered rail end to end; this pins the two conditions it composes.
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        Assert.True((bool)NavigationBadgeConverters.IsActivityKey.Convert("activity", typeof(bool), null, culture)!);
        Assert.False((bool)NavigationBadgeConverters.IsActivityKey.Convert("dashboard", typeof(bool), null, culture)!);
        Assert.False((bool)NavigationBadgeConverters.IsActivityKey.Convert("settings", typeof(bool), null, culture)!);
        Assert.True((bool)NavigationBadgeConverters.IsPositive.Convert(3, typeof(bool), null, culture)!);
        Assert.False((bool)NavigationBadgeConverters.IsPositive.Convert(0, typeof(bool), null, culture)!);
    }
}
