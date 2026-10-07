using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Analysis;
using GalactiLog.App.Views;
using GalactiLog.Core.Metrics;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using TabFactory = GalactiLog.App.Tests.TestSupport.TargetsTabViewModelTestFactory;

namespace GalactiLog.App.Tests.TestSupport;

/// <summary>
/// The one place App.Tests shows the Analysis page inside the real shell, plus the two layout
/// predicates the page's view cases ask with.
/// </summary>
/// <remarks>
/// <para>
/// The harness is the shell and not a bare window. <c>MainWindowViewModel.NavRailExpandedWidth</c>
/// is 200 and the status bar takes its own row, so at 1280 by 800 the page has about 1080 by 720
/// and at the 1024 by 700 minimum about 824 by 620. A view put straight into a
/// <c>new Window { Width = 1280 }</c> is handed the full 1280 and the fixed-width red proofs never
/// fire. It is also the only harness that exercises ruling A12's seam in the shipped order: the
/// shell builds the page, the content region binds it, and <c>AnalysisView</c> activates it.
/// </para>
/// <para>
/// Five files carried a copy of this before it was extracted: <c>AnalysisViewTests</c> and the four
/// tab view files, about 100 lines apiece, differing in the entry point's tuple, which tab is
/// selected and which of the two layout predicates they kept. Each of them now keeps only its own
/// one-line entry point over <see cref="Show"/>.
/// </para>
/// </remarks>
internal static class AnalysisShellHarness
{
    /// <summary>
    /// The seam a SHOWN view publishes through, and the reason <see cref="Page"/> exists: the
    /// markup binds <c>State</c>, <c>StatusLine</c> and <c>PlateScaleWarningVisible</c>, so a
    /// publish raised from the query's own thread-pool thread throws inside the binding and the
    /// load loop's catch turns it into <see cref="AnalysisTabState.Failed"/>, a state nothing
    /// chose. That is TRACKING section 5's first false-green shape. The posted closure is drained
    /// by the <c>Dispatcher.UIThread.RunJobs()</c> that follows every settle here.
    /// </summary>
    public static readonly Action<Action> Post = UiPost.Default;

    /// <summary>
    /// A page for a case that SHOWS it, with the seam fixed and no <c>post</c> parameter to get
    /// wrong. Every query not named here keeps <see cref="AnalysisViewModelTestFactory"/>'s
    /// default. A windowless case calls that factory directly and takes the inline seam.
    /// </summary>
    /// <param name="loadEquipment">The bar's equipment list read, for a shown case that re-reads it
    /// under the bound pickers. It sits first because that is where
    /// <see cref="AnalysisViewModelTestFactory.Create"/> takes it.</param>
    /// <param name="loadFilters">The bar's filter list read, under the same rule.</param>
    public static AnalysisViewModel Page(
        Func<IReadOnlyList<EquipmentCombination>>? loadEquipment = null,
        Func<IReadOnlyList<string>>? loadFilters = null,
        Func<AnalysisMetric, AnalysisMetric, AnalysisFilter, CorrelationResult>? correlation = null,
        Func<AnalysisMetric, AnalysisFilter, DistributionResult?>? distribution = null,
        Func<AnalysisMetric, BoxPlotGrouping, AnalysisFilter, BoxPlotResult>? boxPlot = null,
        Func<AnalysisMetric, AnalysisFilter, TimeSeriesResult>? timeSeries = null,
        Func<AnalysisFilter, MatrixResult>? matrix = null,
        Func<AnalysisMetric, CompareMode, string, string, DateOnly?, DateOnly?, CompareResult>? compare = null)
        => AnalysisViewModelTestFactory.Create(
            loadEquipment: loadEquipment,
            loadFilters: loadFilters,
            correlation: correlation,
            distribution: distribution,
            boxPlot: boxPlot,
            timeSeries: timeSeries,
            matrix: matrix,
            compare: compare,
            post: Post);

    /// <summary>
    /// Builds the shell over <paramref name="page"/>, shows it at <paramref name="width"/> by
    /// <paramref name="height"/>, navigates to the Analysis rail item, selects the tab
    /// <paramref name="tab"/> names through the page's own <c>SelectTabCommand</c>, and settles.
    /// </summary>
    /// <param name="disposables">
    /// Takes the Analysis page and the Statistics and Activity pages the shell's lazy factories
    /// build. The window and the scan coordinator are left to the caller, which is what every copy
    /// of this harness did.
    /// </param>
    /// <param name="tab">
    /// The tab to select, or null to leave the page on its first. Each caller passes its own
    /// lookup, by key or by ordinal, so the route a case exercises is the route it always took.
    /// </param>
    public static (Window Window, AnalysisView View, AnalysisViewModel Page) Show(
        ICollection<IDisposable> disposables,
        double width = 1280d,
        double height = 800d,
        AnalysisViewModel? page = null,
        Func<AnalysisViewModel, AnalysisTabViewModel>? tab = null)
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        var analysis = page ?? Page();
        disposables.Add(analysis);

        var shell = new MainWindowViewModel(
            new GeneralSettings(),
            DashboardViewModelTestFactory.Create(),
            new StatusBarViewModel(new ScanStatusService(coordinator, action => action()), coordinator.Cancel),
            TabFactory.CreateSettingsPage(),
            () => Track(disposables, StatisticsViewModelTestFactory.Create()),
            () => analysis,
            () => Track(disposables, ActivityViewModelTestFactory.Create()));

        var window = new MainWindow { DataContext = shell, Width = width, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        shell.Selected = shell.Items[3];
        Dispatcher.UIThread.RunJobs();

        if (tab is not null)
        {
            analysis.SelectTabCommand.Execute(tab(analysis));
        }

        AnalysisViewModelTestFactory.Settle(analysis);
        Dispatcher.UIThread.RunJobs();

        // After the settle, because a tab body is materialised by the selection above: a lookup
        // taken before it finds nothing for every tab but the first.
        var region = window.GetControl<ContentControl>("ContentRegion");
        var view = region.GetVisualDescendants().OfType<AnalysisView>().Single();
        return (window, view, analysis);
    }

    /// <summary>The one tab body of type <typeparamref name="TBody"/> under <paramref name="root"/>.</summary>
    public static TBody Body<TBody>(Control root)
        where TBody : Control
        => root.GetVisualDescendants().OfType<TBody>().Single();

    public static T Named<T>(Control root, string name)
        where T : Control
        => root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    /// <summary>
    /// Whether <paramref name="target"/> is inside the viewport on BOTH axes.
    /// </summary>
    /// <remarks>
    /// <see cref="ScrollAssertions.IsInView"/> answers the vertical question only, which is what the
    /// two Settings <c>BringIntoView</c> routes ask. The page's filter cases ask a different one,
    /// because the defect they exist for is a filter card laid out in one non-wrapping row whose
    /// date fields land off the right edge.
    /// </remarks>
    public static bool IsFullyInView(Control target, ScrollViewer scroller)
    {
        if (!target.IsEffectivelyVisible || target.TranslatePoint(default, scroller) is not { } point)
        {
            return false;
        }

        return point.X >= -0.5d
            && point.Y >= -0.5d
            && point.X + target.Bounds.Width <= scroller.Viewport.Width + 0.5d
            && point.Y < scroller.Viewport.Height;
    }

    /// <summary>
    /// The same predicate without the last clause: the control's top is not scrolled off, and its
    /// width is inside the viewport, but it may sit below the fold. That is the right question
    /// where the page scrolls vertically and spec 12.14 asks only for no horizontal page scroll,
    /// which is what the Matrix grid asks. It carried the two-axis name until it was split from
    /// <see cref="IsFullyInView"/>.
    /// </summary>
    public static bool IsHorizontallyInView(Control target, ScrollViewer scroller)
    {
        if (!target.IsEffectivelyVisible || target.TranslatePoint(default, scroller) is not { } point)
        {
            return false;
        }

        return point.X >= -0.5d
            && point.Y >= -0.5d
            && point.X + target.Bounds.Width <= scroller.Viewport.Width + 0.5d;
    }

    private static T Track<T>(ICollection<IDisposable> disposables, T page)
        where T : IDisposable
    {
        disposables.Add(page);
        return page;
    }
}
