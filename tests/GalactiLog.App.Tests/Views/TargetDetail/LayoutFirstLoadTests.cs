using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.Tests.Views.TargetDetail.Parts;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.Views.TargetDetail;
using GalactiLog.App.Views.TargetDetail.Layouts;
using GalactiLog.App.Views.TargetDetail.Parts;
using GalactiLog.Core.Settings;
using Xunit;
using static GalactiLog.App.Tests.TestSupport.TargetPartHost;
using Cards = GalactiLog.App.Tests.TestSupport.SessionCardViewModelTestFactory;
using Factory = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;

namespace GalactiLog.App.Tests.Views.TargetDetail;

// The layout view built on the page's first load. The page is hosted as the application hosts it:
// a content region whose content is the page view model, turned into the shell by the
// application's data template.
public class LayoutFirstLoadTests
{
    private sealed class Opened(Factory.Harness harness, TargetPageState state, Window window) : IDisposable
    {
        public TargetPageState State => state;

        public Window Window => window;

        public TargetDetailViewModel Page => harness.ViewModel;

        public Control Layout
            => (Control)window.GetVisualDescendants().OfType<TargetDetailView>().Single().Named<ContentControl>("LayoutHost").Content!;

        public void Dispose()
        {
            SettleLoads(Page, []);
            window.Close();
            harness.Dispose();
        }
    }

    // The newest night has two rigs and the older one has one.
    private static Opened Open(double width, double height, TargetPageState? stored = null)
    {
        var state = stored ?? new TargetPageState();
        var harness = Factory.Create(
            pageState: state,
            post: action => Dispatcher.UIThread.Post(action),
            nightDetail: date => Cards.PopulatedDetail(sessionDate: date) with
            {
                Frames = NightPartsTestKit.Frames(12, date),
                Rigs = date == Factory.LastSession
                    ? [RigRowKit.Rig(RigRowKit.RigA, 0, 6, RigRowKit.SomeRanges), RigRowKit.Rig(RigRowKit.RigB, 1, 6, RigRowKit.SomeRanges)]
                    : [RigRowKit.Rig(RigRowKit.RigA, 0, 12)],
            },
            frameTable: detail => NightPartsTestKit.Table(detail.Frames, state)).Settle();
        var region = new ContentControl();
        var window = new Window { Width = width, Height = height, FontSize = 20, Content = region };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        region.Content = harness.ViewModel;
        SettleLoads(harness.ViewModel, []);
        return new Opened(harness, state, window);
    }

    [AvaloniaTheory]
    [InlineData("bench")]
    [InlineData("tracks")]
    [InlineData("no-such-layout")]
    public void AStoredLayoutOfAnyValue_OpensQuestionModes_AndIsNotRewritten(string stored)
    {
        // A failure looks like a stored key opening another view, or a later write that changes or drops it.
        var document = System.Text.Json.JsonSerializer.Deserialize<DisplaySettings>($"{{\"target_page\":{{\"layout\":\"{stored}\"}}}}")!;
        using var opened = Open(1600, 900, new TargetPageState(document.TargetPage, change => document = change(document)));

        Assert.IsType<ModesLayoutView>(opened.Layout);
        opened.State.GradingBaseline = GradingBaseline.Rig;
        Assert.Equal("rig", document.TargetPage.GradingBaseline);
        Assert.Equal(stored, document.TargetPage.ExtensionData!["layout"].GetString());
    }

    [AvaloniaFact]
    public void TheTitleAndTheNightHeading_AreAtTheirTierOnTheFirstLoad()
    {
        // A failure looks like the target title or the "Night of" heading drawn at the body size on
        // a fresh page.
        using var opened = Open(1600, 900);

        void AssertTiers(string when)
        {
            var title = opened.Layout.Named<TargetHeaderPart>("TargetHeaderPart").Named<TextBlock>("PrimaryName");
            var night = opened.Layout.Named<NightHeaderPart>("NightHeaderPart").Named<TextBlock>("NightHeader");
            Assert.True(Math.Abs(title.FontSize - 30d) < 0.01, $"{when}: the title is drawn at {title.FontSize}, the window's text is 20");
            Assert.True(Math.Abs(night.FontSize - 24d) < 0.01, $"{when}: the night heading is drawn at {night.FontSize}, the window's text is 20");
        }

        AssertTiers("first load");
    }

    [AvaloniaFact]
    public void CompareTo_ShowsTheStoredBaselineOnTheFirstLoad_AndAPressStillPicks()
    {
        // A failure looks like neither Compare to button lit while the stored baseline is unchanged,
        // or a press on the other button not reaching the holder.
        using var opened = Open(1600, 900);

        void AssertLit(string when)
        {
            var frames = opened.Layout.Named<FramesPart>("FramesPart");
            var session = frames.Named<ToggleButton>("CompareToSession").IsChecked;
            var rig = frames.Named<ToggleButton>("CompareToRig").IsChecked;
            var stored = opened.State.GradingBaseline;
            Assert.True(session == (stored == GradingBaseline.Session) && rig == (stored == GradingBaseline.Rig),
                $"{when}: stored {stored}, This session lit {session}, This rig lit {rig}");
        }

        AssertLit("first load");

        var other = opened.Layout.Named<FramesPart>("FramesPart").Named<ToggleButton>("CompareToRig");
        Assert.Contains(other, HitAtCentre(opened.Window, other)!.GetSelfAndVisualAncestors());
        Click(opened.Window, other);
        Assert.Equal(GradingBaseline.Rig, opened.State.GradingBaseline);
        AssertLit("after a press");
    }

    [AvaloniaFact]
    public void ANightAndBack_TheLitRowTheTimelineAndTheChartMark_NameTheSameFrame()
    {
        // A night's table keeps its own selection across a night switch. A failure looks like the
        // table, the timeline's active tick and the per-frame chart mark naming different frames.
        var state = new TargetPageState();
        using var page = Factory.Create(
            fullNight: true,
            pageState: state,
            post: action => Dispatcher.UIThread.Post(action),
            nightDetail: date => Cards.PopulatedDetail(sessionDate: date) with { Frames = NightPartsTestKit.Frames(12, date) },
            frameTable: loaded => NightPartsTestKit.Table(loaded.Frames, state)).Settle();
        var shell = new TargetDetailView { DataContext = page.ViewModel };
        var window = Show(shell, 1600, 900);
        var first = page.ViewModel.Sessions[0];
        var second = page.ViewModel.Sessions[1];
        page.ViewModel.SelectedSession = first;
        SettleLoads(page.ViewModel, []);
        first.FrameTable!.SelectFrameAt(3);
        Dispatcher.UIThread.RunJobs();

        page.ViewModel.SelectedSession = second;
        SettleLoads(page.ViewModel, []);
        AssertOneFrame(shell, second, null);

        page.ViewModel.SelectedSession = first;
        SettleLoads(page.ViewModel, []);
        AssertOneFrame(shell, first, 3);

        window.Close();
    }

    private static void AssertOneFrame(TargetDetailView shell, SessionCardViewModel night, int? expected)
    {
        var table = night.FrameTable!;
        var strip = night.NightStrip!;
        var chart = (SessionChartViewModel)night.Chart!;
        var list = shell.GetVisualDescendants().OfType<FrameTableView>().Single(view => view.IsEffectivelyVisible).Named<ListBox>("FrameRows");
        var lit = list.SelectedItems!.Cast<FrameRowViewModel>().ToList();
        var mark = chart.Sections.SingleOrDefault(section => section.Xi is not null)?.Xi;

        Assert.Equal(expected, table.SelectedCaptureIndex);
        Assert.Equal(expected, strip.ActiveFrame);
        Assert.Equal(table.SelectedRows, lit);
        Assert.Equal(expected is null ? 0 : 1, lit.Count);
        Assert.Equal(expected is null ? null : strip.Ticks.Single(tick => tick.FrameIndex == expected).Fraction, mark);
    }

    [AvaloniaFact]
    public void TheCompactFramesChrome_At540_ClipsNothingOfCompareTo_AndAPressOnEitherButtonLands()
    {
        // A failure looks like Compare to cut by or drawn under the toolbar's buttons in a narrow
        // centre, so a press on This rig lands on something else.
        var (part, harness) = NightPartsTestKit.Graded(card => new FramesPart { DataContext = card, IsCompact = true }, width: 540);
        using var owner = harness;
        var window = (Window)TopLevel.GetTopLevel(part)!;
        var session = part.Named<ToggleButton>("CompareToSession");
        var rig = part.Named<ToggleButton>("CompareToRig");

        foreach (var button in new[] { session, rig })
        {
            var right = button.TranslatePoint(new Point(button.Bounds.Width, 0), part)!.Value.X;
            Assert.True(button.IsEffectivelyVisible && right <= part.Bounds.Width + 0.5, $"{button.Name} ends at {right} in a part {part.Bounds.Width} wide");
            Assert.Contains(button, HitAtCentre(window, button)!.GetSelfAndVisualAncestors());
        }

        Click(window, rig);
        Assert.True(harness.Card.IsRigBaseline, "a press on This rig did not pick it");
        Click(window, session);
        Assert.True(harness.Card.IsSessionBaseline, "a press on This session did not pick it");
        window.Close();
    }

    [AvaloniaFact]
    public void ACtrlPressOnTheLitRow_WithAPageLoaded_LeavesANightSelected()
    {
        // A failure looks like a Ctrl press on the lit ledger row deselecting it, so the page has
        // no night open.
        using var opened = Open(1600, 900);
        var night = opened.Page.SelectedSession;
        Assert.NotNull(night);
        var ledger = opened.Layout.Named<ListBox>("NightsLedger");
        var row = LedgerRowAt(opened.Layout, opened.Page.Sessions.IndexOf(night));
        Assert.Contains(ledger.ContainerFromItem(night!)!, HitAtCentre(opened.Window, row)!.GetSelfAndVisualAncestors());

        Click(opened.Window, row, Avalonia.Input.RawInputModifiers.Control);
        SettleLoads(opened.Page, []);

        Assert.True(ReferenceEquals(night, opened.Page.SelectedSession) && ReferenceEquals(night, ledger.SelectedItem),
            $"after the Ctrl press the page's night is {opened.Page.SelectedSession?.SessionDate.ToString() ?? "none"} and the ledger's is {(ledger.SelectedItem as SessionCardViewModel)?.SessionDate.ToString() ?? "none"}");
    }
}
