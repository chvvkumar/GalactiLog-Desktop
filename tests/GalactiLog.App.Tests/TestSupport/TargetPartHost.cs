using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Controls.Table;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;
using CardFactory = GalactiLog.App.Tests.TestSupport.SessionCardViewModelTestFactory;

namespace GalactiLog.App.Tests.TestSupport;

/// <summary>Hosting and lookup helpers shared by the Target page's shell cases and its part cases.
/// Every helper takes a <see cref="Control"/>, so the page and a part host through the same call.</summary>
internal static class TargetPartHost
{
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The page's real allotment, which no case rendered before the phase review: the shipped
    /// 1280x800 window less the 200 px navigation rail and the 64 px of title bar and status bar.
    /// Every other view case here hosts the page as the whole window content and so hands it about
    /// 64 px and 200 px more than the application ever does, which is how both P1s stayed green.
    /// </summary>
    public const double PageAllotmentWidth = 1080d;

    public const double PageAllotmentHeight = 736d;

    /// <summary>Joins the page's and every card's load and runs their posted publishes until a drain
    /// leaves none running, then fails on a card that settled with a failure unless
    /// <paramref name="failureExpected"/>, as <c>SettleCards</c> does.</summary>
    public static void SettleLoads(
        TargetDetailViewModel page, IEnumerable<SessionCardViewModel?> nights, bool failureExpected = false)
    {
        page.PendingLoad?.Wait(Budget);
        for (var pass = 0; ; pass++)
        {
            // A load posts its publish before it completes, so only a load already complete before a
            // drain is known to be published after it; any other load costs another pass.
            var completedBefore = Loads(page, nights).Where(load => load.IsCompleted).ToHashSet();
            Dispatcher.UIThread.RunJobs();
            var cards = page.Sessions.Concat(nights).OfType<SessionCardViewModel>().ToList();
            var unsettled = Loads(page, nights).Where(load => !completedBefore.Contains(load)).ToList();
            if (unsettled.Count == 0)
            {
                foreach (var card in cards)
                {
                    Assert.True(
                        failureExpected || card.LastFailure is null,
                        $"The card for {card.SessionDate} settled with a failure: {card.LastFailure}");
                }

                return;
            }

            Assert.True(pass < 20, $"{unsettled.Count} loads still unsettled after {pass} drains");
            Assert.True(Task.WaitAll([.. unsettled], Budget), "a load did not finish within the budget");
        }
    }

    private static List<Task> Loads(TargetDetailViewModel page, IEnumerable<SessionCardViewModel?> nights)
        => [.. page.Sessions.Concat(nights).Select(card => card?.PendingLoad).Append(page.PendingLoad).OfType<Task>()];

    public static Window Show(Control view, double width = 1280, double height = 800)
    {
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    /// <summary>Hosts the control in a starred row: the frame table has no height of its own and
    /// this is what bounds it.</summary>
    public static Window ShowInStarredRow(Control control, double width = 1600, double height = 900)
    {
        var host = new Grid { RowDefinitions = new RowDefinitions("*") };
        host.Children.Add(control);
        return Show(host, width, height);
    }

    public static Window ShowAtThePagesAllotment(Control view)
    {
        var window = new Window
        {
            Width = PageAllotmentWidth,
            Height = PageAllotmentHeight,
            Content = view,
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    /// <summary>Reaches one of the three rare actions inside the overflow flyout. A MenuFlyout's
    /// items live in a popup root, not in the view's own visual tree, so
    /// <c>view.GetControl&lt;MenuItem&gt;</c> finds nothing.</summary>
    public static MenuItem MenuItemFromOverflow(Control view, string name)
        => MenuItemFromFlyout(view, "OverflowButton", name);

    /// <summary>Reaches one of spec 12.4's two Export flyout entries (Phase 16). The same popup-root
    /// problem as the overflow's, so the same helper.</summary>
    public static MenuItem MenuItemFromExport(Control view, string name)
        => MenuItemFromFlyout(view, "ExportButton", name);

    public static MenuItem MenuItemFromFlyout(Control view, string buttonName, string name)
    {
        var owner = view.Named<Button>(buttonName);
        Assert.NotNull(owner.Flyout);
        owner.Flyout!.ShowAt(owner);
        Dispatcher.UIThread.RunJobs();

        // The declared MenuItems are the flyout's own items and are used as their own containers,
        // so this is the same instance the presenter realizes. Reading them after ShowAt is what
        // makes the command bindings and IsEffectivelyEnabled meaningful.
        var item = Assert.IsType<MenuFlyout>(owner.Flyout)
            .Items
            .OfType<MenuItem>()
            .FirstOrDefault(candidate => candidate.Name == name);

        Assert.NotNull(item);
        return item!;
    }

    public static IReadOnlyList<string> VisibleTexts(Control view)
        => [.. view.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible)
            .Select(block => block.Text ?? "")];

    public static Grid LedgerRowAt(Control view, int index)
    {
        var ledger = view.Named<ListBox>("NightsLedger");
        var container = ledger.ContainerFromIndex(index);
        Assert.NotNull(container);
        return container!.GetVisualDescendants().OfType<TableRow>().First();
    }

    public static TextBlock CellAt(Grid row, int column)
        => row.Children.OfType<TextBlock>().Single(cell => Grid.GetColumn(cell) == column);

    public static Color TokenColor(Control view, string key)
    {
        Assert.True(view.TryFindResource(key, out var resource));
        return ((ISolidColorBrush)resource!).Color;
    }

    /// <summary>A press and a release over the centre of a control, the shape
    /// <c>FrameTableViewTests</c> uses. A command executed directly would prove the view model and
    /// nothing about the button that is supposed to reach it.</summary>
    public static void Click(Window window, Visual target)
    {
        Render(window);
        var local = new Point(target.Bounds.Width / 2d, target.Bounds.Height / 2d);
        var point = target.TranslatePoint(local, window) ?? local;

        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>A pointer press at a window point after a render tick, so the hit test is current.</summary>
    public static void Press(Window window, Point point)
    {
        Render(window);
        window.MouseDown(point, MouseButton.Left);
    }

    /// <summary><see cref="Click(Window, Visual)"/> with key modifiers held through the press and release.</summary>
    public static void Click(Window window, Visual target, RawInputModifiers modifiers)
    {
        Render(window);
        var local = new Point(target.Bounds.Width / 2d, target.Bounds.Height / 2d);
        var point = target.TranslatePoint(local, window) ?? local;

        window.MouseDown(point, MouseButton.Left, modifiers);
        window.MouseUp(point, MouseButton.Left, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The visual under the centre of a control, as a press there would find it.</summary>
    public static Visual? HitAtCentre(Window window, Visual target)
    {
        Render(window);
        var local = new Point(target.Bounds.Width / 2d, target.Bounds.Height / 2d);
        return window.InputHitTest(target.TranslatePoint(local, window) ?? local) as Visual;
    }

    // Hit testing reads the last committed scene, so ticks run until the compositor's requested batch commit completes.
    private static void Render(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var compositor = ElementComposition.GetElementVisual(window)?.Compositor
            ?? throw new InvalidOperationException("the window has no composition visual");
        var commit = compositor.RequestCompositionBatchCommitAsync().Rendered;
        for (var tick = 0; !commit.IsCompleted; tick++)
        {
            Assert.True(tick < MaxCommitTicks, $"the compositor's batch commit did not complete in {MaxCommitTicks} ticks");
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private const int MaxCommitTicks = 20;

    public static Factory.Harness With(TargetTotals totals)
        => Factory.Create(get: _ => Factory.PopulatedDetail(totals: totals)).Settle();

    public static List<Border> Bars(Control view)
        => [.. view.Named<ItemsControl>("IntegrationBars")
            .GetVisualDescendants()
            .OfType<Border>()
            .Where(border => border.Height == 6d)];

    public static TargetDetailViewModel BuildPage(GeneralSettings? general = null)
    {
        var selection = new ChartSelectionViewModel(
            new GraphSettings(),
            new GraphSettingsWriter(() => new GraphSettings(), _ => { }),
            () => new AliasMap(new Dictionary<string, FilterSetting>(), new EquipmentSettings()));
        var shell = new ShellIntegration(_ => Task.CompletedTask, _ => null);

        var page = new TargetDetailViewModel(
            Factory.ResolvedGroupKey,
            _ => Factory.PopulatedDetail(),
            (headerBlock, overview, _) => new SessionCardViewModel(
                overview,
                headerBlock.GroupKey,
                (_, date) => CardFactory.PopulatedDetail(date),
                null,
                _ => null,
                _ => null,
                new DisplaySettings(),
                new GeneralSettings { Timezone = "UTC", Use24HTime = true },
                post: action => Dispatcher.UIThread.Post(action)),
            (_, _) => RenameOutcome.Renamed,
            (_, _) => { },
            (_, _, _) => Task.FromResult((false, "")),
            shell,
            selection,
            post: action => Dispatcher.UIThread.Post(action),
            getGeneral: () => general ?? new GeneralSettings());

        page.PendingLoad?.Wait(Budget);
        Dispatcher.UIThread.RunJobs();
        return page;
    }
}
