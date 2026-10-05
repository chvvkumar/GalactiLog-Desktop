using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.Tests.Views.TargetDetail.Parts;
using GalactiLog.Data.Queries;
using Xunit;
using Cards = GalactiLog.App.Tests.TestSupport.SessionCardViewModelTestFactory;
using Factory = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

/// <summary>P25 R2: the review set (the lit row plus the checked nights) and the merged card the
/// page builds over it.</summary>
public class ReviewSessionTests
{
    private const int NewestFrames = 5;

    private const int OldestFrames = 3;

    // Two nights of different frame counts, so a merged frame count proves a merge rather than a copy.
    private static SessionDetail Night(DateOnly date)
        => Cards.PopulatedDetail(date) with
        {
            Frames = NightPartsTestKit.Frames(date == Factory.LastSession ? NewestFrames : OldestFrames, date),
        };

    private static Factory.Harness Page(Func<DateOnly, SessionDetail>? nightDetail = null)
        => Factory.Create(nightDetail: nightDetail ?? Night).Settle();

    private static async Task Reload(Factory.Harness harness)
    {
        harness.ReResolveOutcome = (true, "reloaded");
        await harness.ViewModel.ReResolveCommand.ExecuteAsync(null);
        harness.Settle();
    }

    [Fact]
    public void ASetOfOne_ReviewsTheLitCard_AndBuildsNoMerge()
    {
        using var harness = Page();

        Assert.Same(harness.ViewModel.SelectedSession, harness.ViewModel.ReviewSession);
        Assert.Null(harness.ViewModel.MergedSession);
        Assert.False(harness.ViewModel.ReviewSession!.IsMerged);
        Assert.Equal([Factory.LastSession], harness.ViewModel.ReviewSession.Nights);
        Assert.Empty(harness.ViewModel.ReviewSession.NoteNights);
    }

    [Fact]
    public void CheckingASecondNight_ReviewsAMergedCard()
    {
        using var harness = Page();
        var newest = harness.ViewModel.Sessions[0];
        var oldest = harness.ViewModel.Sessions[1];

        oldest.IsChecked = true;

        var merged = harness.ViewModel.MergedSession;
        Assert.NotNull(merged);
        Assert.Same(merged, harness.ViewModel.ReviewSession);
        Assert.True(merged!.IsMerged);
        Assert.Equal([Factory.FirstSession, Factory.LastSession], merged.Nights);
        Assert.Equal([oldest, newest], merged.NoteNights);
        Assert.Equal("2 nights, 2024-01-05 to 2025-12-07", merged.NightHeaderText);
        Assert.Equal("Loading 2 nights...", merged.LoadingText);
        Assert.Equal(Factory.FirstSession, merged.SessionDate);
        Assert.True(merged.IsExpanded);
        Assert.Null(merged.Notes);
        Assert.True(merged.HasNotesBox);
        Assert.Null(merged.Guiding);

        // The lit card keeps its own expansion; the other member's is untouched.
        Assert.True(newest.IsExpanded);
        Assert.False(oldest.IsExpanded);
    }

    [Fact]
    public void TheMergedCard_LoadsTheMembersMergedDetail()
    {
        using var harness = Page();
        harness.ViewModel.Sessions[1].IsChecked = true;

        harness.SettleReview();

        var merged = harness.ViewModel.MergedSession!;
        Assert.Null(merged.LastFailure);
        Assert.NotNull(merged.Detail);
        Assert.Equal(NewestFrames + OldestFrames, merged.Detail!.Frames.Count);
        Assert.Equal(2, merged.Detail.Nights.Count);

        // R8: the facts line dates both frame times on a merged card (UTC, 24 hour clock).
        Assert.Contains("12-07 21:05 to 12-08 03:40", merged.FactsLineText);
        Assert.Equal("21:05", merged.FirstFrameTimeText);
    }

    [Fact]
    public void UncheckingTheSecondNight_ReturnsToTheLitCard_AndDisposesTheMerge()
    {
        using var harness = Page();
        harness.ViewModel.Sessions[1].IsChecked = true;
        var merged = harness.ViewModel.MergedSession!;

        harness.ViewModel.Sessions[1].IsChecked = false;

        Assert.Same(harness.ViewModel.SelectedSession, harness.ViewModel.ReviewSession);
        Assert.Null(harness.ViewModel.MergedSession);
        Assert.True(merged.IsDisposed);
    }

    [Fact]
    public void ALitRowMove_ClearsTheSet_AndReviewsTheNewLitCard()
    {
        using var harness = Page();
        harness.ViewModel.Sessions[1].IsChecked = true;
        var merged = harness.ViewModel.MergedSession!;

        harness.ViewModel.SelectedSession = harness.ViewModel.Sessions[1];

        Assert.Same(harness.ViewModel.Sessions[1], harness.ViewModel.ReviewSession);
        Assert.Null(harness.ViewModel.MergedSession);
        Assert.True(merged.IsDisposed);
        Assert.Empty(harness.ViewModel.SelectedNights);
    }

    [Fact]
    public async Task AScanReload_RebuildsTheMergeOverTheCarriedChecks()
    {
        using var harness = Page();
        harness.ViewModel.Sessions[1].IsChecked = true;
        var before = harness.ViewModel.MergedSession!;

        await Reload(harness);

        var after = harness.ViewModel.MergedSession;
        Assert.NotNull(after);
        Assert.NotSame(before, after);
        Assert.True(before.IsDisposed);
        Assert.Same(after, harness.ViewModel.ReviewSession);
        Assert.Equal([Factory.FirstSession, Factory.LastSession], after!.Nights);
    }

    [Fact]
    public void AMemberWhoseReadThrows_SurfacesAsTheMergedCardsFailure()
    {
        using var harness = Page(date => date == Factory.FirstSession
            ? throw new InvalidOperationException("the oldest night is unreadable")
            : Night(date));
        harness.ViewModel.Sessions[1].IsChecked = true;

        harness.SettleReview();

        var merged = harness.ViewModel.MergedSession!;
        Assert.True(merged.HasFailure);
        Assert.Null(merged.Detail);
        Assert.False(merged.IsLoading);
    }

    [Fact]
    public void ThePage_DisposesTheMergedCardWithItself()
    {
        using var harness = Page();
        harness.ViewModel.Sessions[1].IsChecked = true;
        var merged = harness.ViewModel.MergedSession!;

        harness.ViewModel.Dispose();

        Assert.True(merged.IsDisposed);
        Assert.Null(harness.ViewModel.ReviewSession);
        Assert.Null(harness.ViewModel.MergedSession);
    }
}
