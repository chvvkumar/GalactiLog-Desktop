using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.LibraryTabViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

// Spec 12.2's scan filter notice (PAR-014) on both surfaces, Phase 14B Task 5. The design
// requirement is that the two surfaces "cannot disagree", which is one computation and not two
// equal ones, so the cases below assert the two booleans move together across the truth table.
public class ScanFilterNoticeViewModelTests
{
    private static ScanFilterConfig Seeded() => new() { NameRules = ScanFilterConfig.SeededRules() };

    private static DashboardViewModel Dashboard(ScanFilterConfig filters)
        => Dashboard(new GeneralSettings { ScanFilters = filters });

    private static DashboardViewModel Dashboard(
        GeneralSettings general,
        Func<Func<GeneralSettings, GeneralSettings>, GeneralSettings>? mutateGeneral = null)
    {
        var dashboard = new DashboardViewModel(
            _ => DashboardViewModelTestFactory.EmptyPage,
            DashboardViewModelTestFactory.EmptyFacets,
            () => [],
            DashboardViewModelTestFactory.EmptyAliasMap,
            general,
            (_, _) => Task.CompletedTask,
            post: action => action(),
            mutateGeneral: mutateGeneral);
        dashboard.Filters.PendingReload!.GetAwaiter().GetResult();
        DashboardViewModelTestFactory.Settle(dashboard);
        return dashboard;
    }

    [Fact]
    public void TheLibraryTab_ShowsItUnderTheSeededCondition()
    {
        using var seeded = Factory
            .Create(general => general with { ScanFilters = Seeded() })
            .Settle();
        Assert.True(seeded.ViewModel.ShowScanFilterNotice);

        using var edited = Factory
            .Create(general => general with { ScanFilters = ScanFilterConfig.Empty })
            .Settle();
        Assert.False(edited.ViewModel.ShowScanFilterNotice);
    }

    [Fact]
    public void TheDashboard_ShowsItUnderTheSameCondition()
    {
        using var seeded = Dashboard(Seeded());
        Assert.True(seeded.ShowScanFilterNotice);

        using var edited = Dashboard(ScanFilterConfig.Empty);
        Assert.False(edited.ShowScanFilterNotice);
    }

    // Spec 12.2's "the two surfaces cannot disagree", asserted across the whole truth table.
    public static TheoryData<string> TruthTable() => new()
    {
        "seeded", "empty", "added", "deleted", "edited", "disabled", "include-path", "exclude-path",
    };

    private static ScanFilterConfig Case(string name)
    {
        var rules = ScanFilterConfig.SeededRules().ToList();
        return name switch
        {
            "seeded" => Seeded(),
            "empty" => ScanFilterConfig.Empty,
            "added" => Seeded() with
            {
                NameRules =
                [
                    .. rules,
                    new NameRule { Id = "mine", Action = "exclude", Type = "glob", Pattern = "*.tmp", Target = "file" },
                ],
            },
            "deleted" => Seeded() with { NameRules = [.. rules.Skip(1)] },
            "edited" => Seeded() with { NameRules = [rules[0] with { Pattern = "other" }, .. rules.Skip(1)] },
            "disabled" => Seeded() with { NameRules = [rules[0] with { Enabled = false }, .. rules.Skip(1)] },
            "include-path" => Seeded() with { IncludePaths = [@"C:\Astro\2025"] },
            "exclude-path" => Seeded() with { ExcludePaths = [@"C:\Astro\rejected"] },
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown truth-table row"),
        };
    }

    // The scan root every path row in the table sits under. The Library tab harness writes
    // through a real SettingsStore, which validates include and exclude paths for containment
    // (spec 10.2), so a path row with no configured root would be a refused document rather than
    // a truth-table case. No path here exists on disk and none has to.
    private const string Root = @"C:\Astro";

    [Theory]
    [MemberData(nameof(TruthTable))]
    public void BothSurfaces_ReadOneComputedCondition(string row)
    {
        var filters = Case(row);

        using var tab = Factory
            .Create(general => general with { ScanRoots = [Root], ScanFilters = filters })
            .Settle();
        using var dashboard = Dashboard(filters);

        Assert.Equal(dashboard.ShowScanFilterNotice, tab.ViewModel.ShowScanFilterNotice);


        // And both equal the one predicate, so neither has quietly grown its own copy.
        Assert.Equal(
            ScanFilterConfig.ShowsSetupNotice(new GeneralSettings { ScanFilters = filters }),
            dashboard.ShowScanFilterNotice);
    }

    // Polish ruling 1: Review hides the notice at once, then persists scan_filters_reviewed
    // through the general document's one writer. A failure is a notice that comes back on the
    // next launch, or a write that runs before the notice is down.
    [Fact]
    public void TheDashboardReview_HidesTheNoticeThenPersistsTheReviewedFlag()
    {
        var stored = new GeneralSettings { ScanRoots = [Root], ScanFilters = Seeded() };
        var noticeWhenWritten = new List<bool>();
        using var dashboard = Dashboard(stored, mutate =>
        {
            stored = mutate(stored);
            return stored;
        });
        dashboard.ReviewScanFiltersRequested += (_, _) => noticeWhenWritten.Add(dashboard.ShowScanFilterNotice);
        Assert.True(dashboard.ShowScanFilterNotice);

        dashboard.ReviewScanFiltersCommand.Execute(null);

        Assert.False(dashboard.ShowScanFilterNotice);
        Assert.True(stored.ScanFiltersReviewed);
        Assert.True(ScanFilterConfig.IsOnlyTheSeededRules(stored.ScanFilters));
        Assert.Equal([false], noticeWhenWritten);
    }

    // The persisted flag reaches both surfaces: the Library tab reads it from the stored
    // document, and the Dashboard reads it from its constructor document and again from the
    // GeneralChanged document AppHost forwards to FollowScanFilters. A failure is a surface that
    // still shows the notice after the flag is set, so a review would have to be repeated.
    [Fact]
    public void AReviewedFlag_HidesTheLibraryTabAndTheDashboardWithTheSeededFiveStillInPlace()
    {
        using var tab = Factory
            .Create(general => general with { ScanFilters = Seeded(), ScanFiltersReviewed = true })
            .Settle();
        Assert.True(ScanFilterConfig.IsOnlyTheSeededRules(tab.Stored.ScanFilters));
        Assert.False(tab.ViewModel.ShowScanFilterNotice);

        using var constructed = Dashboard(new GeneralSettings { ScanFilters = Seeded(), ScanFiltersReviewed = true });
        Assert.False(constructed.ShowScanFilterNotice);

        using var followed = Dashboard(Seeded());
        Assert.True(followed.ShowScanFilterNotice);
        followed.FollowScanFilters(new GeneralSettings { ScanFilters = Seeded(), ScanFiltersReviewed = true });
        Assert.False(followed.ShowScanFilterNotice);
    }

    // Ruling 1's second half: a Library tab save of the filter block sets the flag too, so a
    // user who added a root and kept the five rules has reviewed them. A failure is a stored
    // document with the flag still false after the save, and the notice still up.
    [Fact]
    public async Task ALibraryTabSave_SetsTheReviewedFlag()
    {
        using var harness = Factory.Create(general => general with { ScanFilters = Seeded() }).Settle();
        var tab = harness.ViewModel;
        Assert.True(tab.ShowScanFilterNotice);

        Assert.True(tab.AddScanRoot(Factory.Root));
        await tab.SaveCommand.ExecuteAsync(null);
        harness.SettleWrites();

        Assert.True(harness.Stored.ScanFiltersReviewed);
        Assert.True(ScanFilterConfig.IsOnlyTheSeededRules(harness.Stored.ScanFilters));
        Assert.False(tab.ShowScanFilterNotice);
    }

    // Spec 12.2: "It informs and never blocks."
    [Fact]
    public void TheNotice_BlocksNoCommand()
    {
        using var tab = Factory.Create(general => general with { ScanFilters = Seeded() }).Settle();

        Assert.True(tab.ViewModel.ShowScanFilterNotice);
        Assert.True(tab.ViewModel.RunScanCommand.CanExecute(null));
        Assert.True(tab.ViewModel.AddNameRuleCommand.CanExecute(null));

        using var dashboard = Dashboard(Seeded());
        Assert.True(dashboard.ShowScanFilterNotice);
        Assert.True(dashboard.ReviewScanFiltersCommand.CanExecute(null));
    }

    [Fact]
    public async Task AScanRunsWithTheNoticeShowing()
    {
        using var tab = Factory.Create(general => general with { ScanFilters = Seeded() }).Settle();
        Assert.True(tab.ViewModel.ShowScanFilterNotice);

        await tab.ViewModel.RunScanCommand.ExecuteAsync(null);

        Assert.Equal(1, tab.ScanRuns);
        Assert.True(tab.ViewModel.ShowScanFilterNotice);
    }

    // Spec 12.7: the Library tab's Review "scrolls to this tab's rule editor rather than
    // navigating". The gesture is Avalonia's BringIntoView in the view's code-behind, which is
    // where the folder pickers already live, so the view-model carries no navigation of its own.
    [Fact]
    public void TheLibraryReview_ScrollsToTheRuleEditor()
    {
        var codeBehind = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "GalactiLog.App", "Views", "Settings", "LibraryTabView.axaml.cs"));
        Assert.Contains("OnReviewScanFilters", codeBehind, StringComparison.Ordinal);
        Assert.Contains("NameRulesSection", codeBehind, StringComparison.Ordinal);
        Assert.Contains("BringIntoView", codeBehind, StringComparison.Ordinal);

        using var tab = Factory.Create(general => general with { ScanFilters = Seeded() }).Settle();
        var view = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "GalactiLog.App", "Views", "Settings", "LibraryTabView.axaml"));
        Assert.Contains("Click=\"OnReviewScanFilters\"", view, StringComparison.Ordinal);

        // And the tab's own view-model exposes no navigation command for it.
        Assert.Null(tab.ViewModel.GetType().GetProperty("ReviewScanFiltersCommand"));
    }

    // Spec 12.2: the Dashboard's Review "opens Settings on the Library tab with the rule editor in
    // view". The route is the shell's, the same idiom the Statistics timeline's DateRangeRequested
    // already uses.
    //
    // Phase 14B fixer, fixer list items 4 and 34. This case used to prove the route by asserting
    // three source-text substrings of MainWindowViewModel.cs, one of them with its semicolon, and
    // never constructed a shell, so neither the route nor the missing scroll was visible to it.
    // It builds a real shell over a real Library tab now and asserts where the route lands.
    [Fact]
    public void TheDashboardReview_OpensSettingsOnTheLibraryTabWithTheRuleEditorInView()
    {
        using var dashboard = Dashboard(Seeded());
        using var tab = Factory.Create(general => general with { ScanFilters = Seeded() }).Settle();
        var library = tab.ViewModel;
        var settings = new SettingsViewModel(
            library: () => library,
            targets: () => throw new NotSupportedException("No tab but Library is visited here."),
            filters: () => throw new NotSupportedException("No tab but Library is visited here."),
            equipment: () => throw new NotSupportedException("No tab but Library is visited here."),
            maintenance: () => throw new NotSupportedException("No tab but Library is visited here."),
            location: () => throw new NotSupportedException("No tab but Library is visited here."),
            display: () => throw new NotSupportedException("No tab but Library is visited here."),
            storage: () => throw new NotSupportedException("No tab but Library is visited here."));

        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        using var shell = new MainWindowViewModel(
            new GeneralSettings(),
            dashboard,
            new StatusBarViewModel(new ScanStatusService(coordinator, action => action()), coordinator.Cancel),
            settings,
            () => throw new NotSupportedException("The Statistics page is not visited here."),
            () => throw new NotSupportedException("The Analysis page is not visited here."),
            () => throw new NotSupportedException("The Activity page is not visited here."));

        var raised = 0;
        dashboard.ReviewScanFiltersRequested += (_, _) => raised++;

        dashboard.ReviewScanFiltersCommand.Execute(null);

        Assert.Equal(1, raised);
        Assert.Equal("settings", shell.Selected.Key);
        Assert.Equal("library", settings.Selected.Key);
        Assert.Same(library, settings.Selected.Page);

        // The scroll half: the tab holds the request LibraryTabView consumes on attach to bring
        // NameRulesSection into view, and it holds it exactly once, so a later visit to the tab
        // through the strip does not scroll again.
        Assert.True(library.ConsumeNameRulesInViewRequest());
        Assert.False(library.ConsumeNameRulesInViewRequest());
    }

    // Departure 10: the port drops the web's "use defaults" button, because the wizard has
    // already written those defaults by the time the notice can show. One action, Review.
    [Fact]
    public void TheNotice_HasNoUseDefaultsAction()
    {
        using var dashboard = Dashboard(Seeded());
        Assert.Null(dashboard.GetType().GetProperty("UseDefaultsCommand"));

        foreach (var relative in new[]
                 {
                     Path.Combine("Views", "DashboardView.axaml"),
                     Path.Combine("Views", "Settings", "LibraryTabView.axaml"),
                 })
        {
            // Comments stripped first: both files carry a note about the web button that has no
            // counterpart here, and a note about it is not an action.
            var markup = StripXmlComments(
                File.ReadAllText(Path.Combine(RepoRoot(), "src", "GalactiLog.App", relative)));
            Assert.DoesNotContain("Use defaults", markup, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string StripXmlComments(string markup)
        => System.Text.RegularExpressions.Regex.Replace(
            markup, "<!--.*?-->", "", System.Text.RegularExpressions.RegexOptions.Singleline);

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GalactiLog.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
