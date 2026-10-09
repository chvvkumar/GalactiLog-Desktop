using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.App.Views.Dashboard;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.Views;

// Phase 14C Task 4, spec 12.2's Pager paragraph and "The refetch dim" paragraph. A new file
// rather than an append to the contended TargetListViewTests.cs (task4.md section 9.2), so the
// pager's own suite does not collide with Task 3's whole-file rewrite of that one.
public class TargetListPagerViewTests
{
    private static readonly TargetRow SampleRow = new(
        GroupKey: "10000000-0000-0000-0000-000000000000",
        TargetId: Guid.Parse("10000000-0000-0000-0000-000000000000"),
        Name: "M 31",
        CommonName: "Andromeda Galaxy",
        CatalogId: "M 31",
        ObjectType: "G",
        ObjectCategory: "Galaxy",
        IntegrationSeconds: 44_640d,
        FrameCount: 148,
        SessionCount: 2,
        FirstSession: new DateOnly(2024, 1, 5),
        LastSession: new DateOnly(2025, 12, 7),
        Palette: [new FilterBadge("Ha", "#FF0000", 5, 1_500d)],
        Equipment: ["RC8 / ASI2600MM"],
        Aliases: [],
        Sessions: [new SessionSummary(new DateOnly(2025, 12, 7), 88, 26_400d)]);

    private static TargetListViewModel CreateList(int totalGroups = 3, int pageSize = 50)
    {
        var display = new DisplaySettings();
        var list = new TargetListViewModel(display, () => display, _ => { }, pageSize);
        list.Load(new TargetListingPage([SampleRow], totalGroups, 44_640d, 148, 1, pageSize));
        return list;
    }

    // The page's own allotment (TargetListViewTests.cs's own harness comment): a bare window's
    // full width hides a row or a pager overflow that the shipped page never has room for.
    private const double PageAllotment = 720d;

    private static Window ShowList(TargetListView view, double allotment = PageAllotment, double windowWidth = 1280d)
    {
        var host = new Border
        {
            Width = allotment,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = view,
        };
        var window = new Window { Width = windowWidth, Height = 800, Content = host };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        view.InvalidateMeasure();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static Grid Pager(TargetListView view)
        => view.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "TargetListPager");

    // The template realizes both the ellipsis text and the page Button for every entry and
    // toggles IsVisible on each (the same idiom the header cell template already uses for a
    // column with no sort mapping), so "rendered" means effectively visible here, the same
    // reading VisibleHeaderTitles and VisibleCellTexts already give TargetListViewTests.cs.
    private static IReadOnlyList<Button> PageButtons(TargetListView view)
        => [.. view.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("page") && button.IsEffectivelyVisible)];

    [AvaloniaFact]
    public void ThePager_SitsAboveTheHeaderRow()
    {
        var view = new TargetListView { DataContext = CreateList() };
        ShowList(view);

        var pager = Pager(view);
        var header = view.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "TargetListHeaderRow");

        // Task 3 may have moved the header, so the two row indices are compared to each other
        // rather than to a literal: a literal would pin Task 3's own layout, not this ordering.
        Assert.True(
            Grid.GetRow(pager) < Grid.GetRow(header),
            "The pager does not sit above the header row.");
    }

    [AvaloniaFact]
    public void ThePager_RendersOneButtonPerPageNumber_AndAnEllipsisAsText()
    {
        var list = CreateList(totalGroups: 1000, pageSize: 25); // 40 pages
        list.Page = 20;
        var view = new TargetListView { DataContext = list };
        ShowList(view);

        var expectedNumbers = list.PageButtons.Where(button => button.Page is not null)
            .Select(button => button.Page!.Value.ToString())
            .ToList();
        var renderedButtons = PageButtons(view);

        Assert.Equal(expectedNumbers.Count, renderedButtons.Count);
        Assert.Equal(expectedNumbers, renderedButtons.Select(button => button.Content!.ToString()));

        // The ellipsis is a non-interactive text run of three ASCII full stops, never a button
        // and never the Unicode character (task4.md section 14).
        var ellipsisCount = list.PageButtons.Count(button => button.Page is null);
        var ellipsisText = Pager(view).GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(block => block.Text == "..." && block.IsEffectivelyVisible)
            .ToList();
        Assert.Equal(ellipsisCount, ellipsisText.Count);
        Assert.DoesNotContain(renderedButtons, button => button.Content!.ToString() == "...");

        // Never the typographic character, built at runtime rather than typed as a literal so
        // no Unicode ellipsis sits in this file's own source (task4.md section 14).
        var typographicEllipsis = char.ConvertFromUtf32(0x2026);
        Assert.DoesNotContain(
            typographicEllipsis,
            Pager(view).GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text ?? string.Empty));
    }

    [AvaloniaFact]
    public void TheCurrentPageButton_CarriesTheCurrentClass_AndNoOtherDoes()
    {
        var list = CreateList(totalGroups: 1000, pageSize: 25);
        list.Page = 20;
        var view = new TargetListView { DataContext = list };
        ShowList(view);

        var current = PageButtons(view).Where(button => button.Classes.Contains("current")).ToList();
        var only = Assert.Single(current);
        Assert.Equal(20, only.Content);
    }

    [AvaloniaFact]
    public void TheBottomPreviousAndNextPair_IsGone()
    {
        var view = new TargetListView { DataContext = CreateList() };
        ShowList(view);

        // Catches a pager added at the top with the old one left behind, which builds green
        // against every other case here: exactly one Previous and one Next, both inside the
        // pager's own region rather than below the rows.
        var previousAndNext = view.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Content is "Previous" or "Next")
            .ToList();

        Assert.Equal(2, previousAndNext.Count);
        var pagerDescendants = Pager(view).GetVisualDescendants().ToList();
        Assert.All(previousAndNext, button => Assert.Contains(button, pagerDescendants));
    }

    [AvaloniaFact]
    public void ThePageSizeSelect_OffersTheSpecsFour()
    {
        var view = new TargetListView { DataContext = CreateList() };
        ShowList(view);

        var select = Assert.Single(view.GetVisualDescendants().OfType<ComboBox>());
        Assert.Equal(TargetListViewModel.PageSizeOptions, select.ItemsSource);
    }

    [AvaloniaFact]
    public void TheRowsRegion_DimsAndDisablesWhileRefetching()
    {
        var list = CreateList();
        var view = new TargetListView { DataContext = list };
        ShowList(view);

        var rows = view.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "TargetListRows");

        list.SetRefetching(true);
        Dispatcher.UIThread.RunJobs();

        Assert.True(rows.Opacity < 1d, $"The rows region's opacity is {rows.Opacity} while refetching.");

        // IsHitTestVisible, not IsEnabled: an IsEnabled="False" subtree drops keyboard focus in
        // Avalonia (KeyboardFocusInsideARow_SurvivesARefetch is what caught it), and the web's own
        // treatment is pointer-events-none, a hit-test rule rather than a disablement.
        Assert.False(rows.IsHitTestVisible, "The rows region still takes pointer input while refetching.");
    }

    [AvaloniaFact]
    public void TheRowsRegion_IsNotDimmedAtRest()
    {
        var list = CreateList();
        var view = new TargetListView { DataContext = list };
        ShowList(view);

        var rows = view.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "TargetListRows");

        Assert.False(list.IsRefetching);
        Assert.Equal(1d, rows.Opacity);
        Assert.True(rows.IsHitTestVisible);
    }

    [AvaloniaFact]
    public void KeyboardFocusInsideARow_SurvivesARefetch()
    {
        var list = CreateList();
        var view = new TargetListView { DataContext = list };
        ShowList(view);

        var toggle = view.GetVisualDescendants().OfType<Button>().First(b => b.Name == "SessionsToggle");
        toggle.Focus();
        Dispatcher.UIThread.RunJobs();

        var focusManager = TopLevel.GetTopLevel(view)!.FocusManager!;
        Assert.Same(toggle, focusManager.GetFocusedElement());

        // A real focus move and a real refetch cycle, not a property that only records one:
        // nothing in Rows is ever cleared, so the same instance keeps the keyboard focus it had.
        list.SetRefetching(true);
        Dispatcher.UIThread.RunJobs();
        list.SetRefetching(false);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(toggle, focusManager.GetFocusedElement());
    }

    [AvaloniaFact]
    public void TheView_StillDeclaresNoStyleOfItsOwn()
    {
        var source = File.ReadAllText(FindTargetListViewAxaml());
        Assert.DoesNotContain("<Style ", source);
    }

    [AvaloniaFact]
    public void NoPageButton_CarriesATargetRowViewModelParameter()
    {
        var list = CreateList(totalGroups: 1000, pageSize: 25);
        list.Page = 20;
        var view = new TargetListView { DataContext = list };
        ShowList(view);

        var pageButtons = PageButtons(view);
        Assert.NotEmpty(pageButtons);
        Assert.DoesNotContain(pageButtons, button => button.CommandParameter is TargetRowViewModel);
        Assert.All(pageButtons, button => Assert.IsType<int>(button.CommandParameter));
    }

    private static string FindTargetListViewAxaml()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "src/GalactiLog.App/Views/Dashboard/TargetListView.axaml");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }

        throw new FileNotFoundException("TargetListView.axaml");
    }
}
