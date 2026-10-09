using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using GalactiLog.App.Controls.Table;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.Views.TargetDetail.Parts;
using GalactiLog.Core.Settings;
using Xunit;
using static GalactiLog.App.Tests.Views.TargetDetail.Parts.NightPartsTestKit;
using Cards = GalactiLog.App.Tests.TestSupport.SessionCardViewModelTestFactory;
using Factory = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

// The harness post stands in for the UI thread, so no card publish may run while the page's is
// still running. Each case parks the page at the end of its publish; a card publish landing in that
// window throws, and a failure is a card whose load completed with its detail only partly published.
public class CardPublishOverlapTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    // How long the parked page waits for a card publish that, through a serialised post, never comes.
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    private static readonly GeneralSettings Utc = new()
    {
        Timezone = "UTC",
        Use24HTime = true,
        ObserverLatitude = 40,
        ObserverLongitude = -75,
    };

    [Fact]
    public void FirstLoad_CardLoadLandingInsideThePagePublish_StillPublishesTheNightStrip()
    {
        using var pageGate = new ManualResetEventSlim();
        using var cardGate = new ManualResetEventSlim();
        using var harness = Gated(pageGate, cardGate, general: Utc);
        using var arrived = ParkThePageInsideItsPublish(harness, cardGate);

        pageGate.Set();
        harness.Settle().SettleCards();

        var card = harness.ViewModel.SelectedSession!;
        Assert.True(DuskOf(card.NightStrip!) > DateTime.MinValue);
    }

    [Fact]
    public void Rebuild_CardLoadLandingInsideThePagePublish_StillPublishesTheFrameTimes()
    {
        using var pageGate = new ManualResetEventSlim(initialState: true);
        using var cardGate = new ManualResetEventSlim(initialState: true);
        using var harness = Gated(pageGate, cardGate, general: Utc).Settle().SettleCards();
        var beforeTime = harness.ViewModel.SelectedSession!.Detail!.FirstFrameTime;

        cardGate.Reset();
        using var arrived = ParkThePageInsideItsPublish(harness, cardGate);
        harness.General = Utc with { Timezone = "Eastern Standard Time", Use24HTime = false };
        harness.RaiseGeneralChanged();
        harness.Settle().SettleCards();

        var after = harness.ViewModel.SelectedSession!;
        Assert.True(after.Detail is not null, $"The rebuilt card loaded no detail. LastFailure: {after.LastFailure}");
        Assert.Equal(
            SessionTimeFormat.Format(beforeTime, SessionTimeFormat.Resolve("Eastern Standard Time"), false),
            after.FirstFrameTimeText);
    }

    [AvaloniaFact]
    public void PerFilterTable_CardLoadLandingInsideThePagePublish_StillDrawsTheFilterRows()
    {
        using var pageGate = new ManualResetEventSlim();
        using var cardGate = new ManualResetEventSlim();
        using var harness = Gated(pageGate, cardGate, fullNight: true);
        using var arrived = ParkThePageInsideItsPublish(harness, cardGate);

        pageGate.Set();
        harness.Settle().SettleCards();
        var part = new PerFilterTablePart { DataContext = harness.ViewModel.SelectedSession, Width = 700 };
        TargetPartHost.Show(part);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var counts = part.Named<ItemsControl>("FilterRowsList").GetVisualDescendants().OfType<TableRow>()
            .Where(row => row.DataContext is FilterTableRowViewModel { IsRigLabel: false })
            .SelectMany(row => row.Children.OfType<TextBlock>().Where(cell => TableRow.GetCol(cell) == "frames"))
            .ToList();
        Assert.NotEmpty(counts);
    }

    // The page query and the selected card's query each wait on a gate, so the hook below is in
    // place before either publishes.
    [Fact]
    public void Rebuild_DisposingACardWithAnUnsavedNote_DisposesTheCardAndWritesTheNote()
    {
        // Red if a rebuild drops a note typed into a night it disposes, or leaves that card live.
        using var harness = Factory.Create(general: Utc).Settle().SettleCards();
        var card = harness.ViewModel.SelectedSession!;
        card.Notes!.Text = "typed and not yet saved";

        harness.General = Utc with { Timezone = "Eastern Standard Time" };
        harness.RaiseGeneralChanged();
        harness.Settle();

        Assert.True(card.IsDisposed);
        Assert.Contains(harness.SessionNoteWrites, write => write.Notes == "typed and not yet saved");
    }

    [Fact]
    public async Task HarnessDispose_WhileAReloadIsInFlight_WaitsForItBeforeReadingTheLedger()
    {
        // Red if the harness enumerates Sessions while a reload's publish can still refill it.
        using var pageGate = new ManualResetEventSlim(initialState: true);
        using var cardGate = new ManualResetEventSlim(initialState: true);
        var harness = Gated(pageGate, cardGate, general: Utc).Settle().SettleCards();
        pageGate.Reset();
        harness.General = Utc with { Timezone = "Eastern Standard Time" };
        harness.RaiseGeneralChanged();

        var disposing = Task.Run(harness.Dispose);
        await Task.WhenAny(disposing, Task.Delay(500));
        var returnedEarly = disposing.IsCompleted;
        pageGate.Set();
        await disposing.WaitAsync(Budget);

        Assert.False(returnedEarly, "Dispose returned while the reload was still in flight.");
    }

    [Fact]
    public void SettleCards_ACardWhoseQueryThrows_FailsNamingTheCard()
    {
        // The settle check itself: a card load that failed must not settle quietly.
        using var harness = Factory.Create(nightDetail: _ => throw new InvalidOperationException("night query down")).Settle();

        var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => harness.SettleCards());

        Assert.Contains($"The card for {Factory.LastSession} settled with a failure", failure.Message);
        Assert.Contains("night query down", failure.Message);
        harness.SettleCards(failureExpected: true);
    }

    [AvaloniaFact]
    public void SettleLoads_ACardWhoseQueryThrows_FailsNamingTheCard()
    {
        // The view settle's check: the same failed load through the dispatcher post.
        using var harness = Factory.Create(
            nightDetail: _ => throw new InvalidOperationException("night query down"),
            post: action => Avalonia.Threading.Dispatcher.UIThread.Post(action)).Settle();

        var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => TargetPartHost.SettleLoads(harness.ViewModel, []));

        Assert.Contains($"The card for {Factory.LastSession} settled with a failure", failure.Message);
        TargetPartHost.SettleLoads(harness.ViewModel, [], failureExpected: true);
    }

    [AvaloniaFact]
    public void SettleLoads_ACardLoadFailingJustAfterADrain_StillFailsNamingTheCard()
    {
        // The card's load starts inside a drain and fails only after it, so its failure publish is
        // still queued when the helper looks again; red if the helper settles without running it.
        using var query = new ManualResetEventSlim();
        using var harness = Factory.Create(
            nightDetail: _ =>
            {
                query.Wait(Budget);
                throw new InvalidOperationException("night query down");
            },
            post: action => Avalonia.Threading.Dispatcher.UIThread.Post(action)).Settle();

        var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => TargetPartHost.SettleLoads(harness.ViewModel, FailingTheSelectedLoad(harness.ViewModel, query)));

        Assert.Contains($"The card for {Factory.LastSession} settled with a failure", failure.Message);
        TargetPartHost.SettleLoads(harness.ViewModel, [], failureExpected: true);
    }

    // When the helper reads the extra nights after a drain has started the selected card's load,
    // that load is released and run to completion there, between the drain and the helper's check.
    private static IEnumerable<SessionCardViewModel?> FailingTheSelectedLoad(TargetDetailViewModel page, ManualResetEventSlim query)
    {
        if (page.SelectedSession?.PendingLoad is { } load)
        {
            query.Set();
            load.Wait(Budget);
        }

        yield break;
    }

    private static Factory.Harness Gated(
        ManualResetEventSlim pageGate,
        ManualResetEventSlim cardGate,
        GeneralSettings? general = null,
        bool fullNight = false)
        => Factory.Create(
            get: _ =>
            {
                pageGate.Wait(Budget);
                return Factory.PopulatedDetail();
            },
            general: general,
            fullNight: fullNight,
            nightDetail: date =>
            {
                cardGate.Wait(Budget);
                return Cards.PopulatedDetail(date);
            });

    // SelectedSession arms it once the selected card's load has started; the rename command's
    // CanExecuteChanged, the publish's last step, parks the page until the card publishes or the
    // window closes. The returned signal is the caller's to dispose.
    private static ManualResetEventSlim ParkThePageInsideItsPublish(Factory.Harness harness, ManualResetEventSlim cardGate)
    {
        var armed = 0;
        var pageInside = 0;
        var cardArrived = new ManualResetEventSlim();
        harness.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(TargetDetailViewModel.SelectedSession)
                || harness.ViewModel.SelectedSession is not { } card)
            {
                return;
            }

            card.PropertyChanged += (_, changed) =>
            {
                if (changed.PropertyName == nameof(SessionCardViewModel.Detail) && Volatile.Read(ref pageInside) == 1)
                {
                    cardArrived.Set();
                    throw new InvalidOperationException("The card published inside the page's publish.");
                }
            };

            Volatile.Write(ref armed, 1);
        };

        harness.ViewModel.BeginRenameCommand.CanExecuteChanged += (_, _) =>
        {
            if (Interlocked.Exchange(ref armed, 0) == 0)
            {
                return;
            }

            Volatile.Write(ref pageInside, 1);
            cardGate.Set();
            cardArrived.Wait(Window);
            Volatile.Write(ref pageInside, 0);
        };

        return cardArrived;
    }
}
