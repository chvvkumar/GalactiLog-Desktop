using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.Views.TargetDetail;
using GalactiLog.App.Views.TargetDetail.Parts;
using GalactiLog.Core.Integrations;
using GalactiLog.Core.Settings;
using GalactiLog.Core.Survey;
using Xunit;
using static GalactiLog.App.Tests.TestSupport.TargetPartHost;
using Factory = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;

namespace GalactiLog.App.Tests.Views.TargetDetail.Parts;

// Content cases for the identity line, the log line, the callouts and the overflow and survey actions, hosted on the part alone.
public class TargetHeaderPartTests
{
    [AvaloniaFact]
    public void TheHeader_HasNoLayoutBox()
    {
        // A failure looks like the stage 3 layout selector still drawn beside Details.
        using var harness = Factory.Create().Settle();
        var view = new TargetHeaderPart { DataContext = harness.ViewModel };
        Show(view);

        Assert.True(view.Named<Button>("DetailsButton").IsEffectivelyVisible, "the header did not render");
        Assert.Null(view.NamedOrNull<ComboBox>("LayoutSelector"));
        Assert.Empty(view.GetVisualDescendants().OfType<ComboBox>());
    }

    // The outcome line beneath the header, which is the whole of what the action reports.
    [AvaloniaFact]
    public async Task TargetDetailView_RendersTheReResolveStatusLine()
    {
        using var harness = Factory.Create().Settle();
        harness.ReResolveOutcome = (false, "M 31 resolved from Offline. Nothing changed.");
        var view = new TargetHeaderPart { DataContext = harness.ViewModel };
        Show(view);

        var status = view.Named<TextBlock>("ReResolveStatus");
        Assert.False(status.IsVisible);

        await harness.ViewModel.ReResolveCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(status.IsVisible);
        Assert.Equal("M 31 resolved from Offline. Nothing changed.", status.Text);
    }

    [AvaloniaFact]
    public void TargetDetailView_MergedAwayCallout_NamesTheWinner()
    {
        var winnerId = Guid.Parse("40000000-0000-0000-0000-000000000000");
        using var harness = Factory.Create(get: _ => null, mergedInto: (winnerId, "M 31", "NGC 224")).Settle();
        var view = new TargetHeaderPart { DataContext = harness.ViewModel };
        Show(view);

        // Ruling Q12: the merge is named rather than hidden, and the generic stale-key callout
        // gives way to the one that says what happened.
        Assert.True(view.Named<Border>("MergedAwayCallout").IsVisible);
        Assert.False(view.Named<Border>("MissingCallout").IsVisible);

        Assert.Contains("This target was merged into \"M 31\".", VisibleTexts(view));

        var open = view.Named<Button>("OpenMergedIntoButton");
        Assert.True(open.IsEffectivelyEnabled);
        Assert.Equal("Open M 31", open.Content);
    }

    [AvaloniaFact]
    public void TargetDetailView_PrunedGroup_KeepsThePlainStaleKeyCallout()
    {
        using var harness = Factory.Create(get: _ => null).Settle();
        var view = new TargetHeaderPart { DataContext = harness.ViewModel };
        Show(view);

        Assert.True(view.Named<Border>("MissingCallout").IsVisible);
        Assert.False(view.Named<Border>("MergedAwayCallout").IsVisible);
    }

    [AvaloniaFact]
    public void TargetDetailView_TheThreeRareActions_AreInTheOverflowFlyout()
    {
        // P12 R10 and the workflow panel: rename, merge and re-resolve are rare, so they are one
        // click deeper and the identity line stays short. Their names are unchanged.
        using var harness = Factory.Create().Settle();
        var view = new TargetHeaderPart { DataContext = harness.ViewModel };
        Show(view);

        // The three names survive the move, which is what lets the existing cases re-target with
        // no logic change; the elements behind them are menu items now, not buttons.
        Assert.NotNull(view.Named<MenuItem>("RenameButton"));
        Assert.NotNull(view.Named<MenuItem>("MergeButton"));
        Assert.NotNull(view.Named<MenuItem>("ReResolveButton"));

        Assert.Equal("Rename", MenuItemFromOverflow(view, "RenameButton").Header);
        Assert.Equal("Merge into another target", MenuItemFromOverflow(view, "MergeButton").Header);
        Assert.Equal("Re-resolve", MenuItemFromOverflow(view, "ReResolveButton").Header);
    }

    [AvaloniaFact]
    public void TargetDetailView_LogLine_CarriesBothSpecDisclosures()
    {
        // Spec 12.4 asks for the count of frames excluded for lack of a plate scale and for the
        // eccentricity pool's source and excluded count. Both survive the redesign, joined into
        // the log line's tertiary sentence.
        using var harness = Factory.Create().Settle();
        var view = new TargetHeaderPart { DataContext = harness.ViewModel };
        Show(view);

        var disclosure = view.Named<TextBlock>("LogLineDisclosure");
        Assert.True(disclosure.IsVisible);
        Assert.Contains("23 frames without a plate scale", disclosure.Text);
        Assert.Contains("measured by header", disclosure.Text);

        // And the log line itself, as runs, with the filter dots beside it.
        var texts = VisibleTexts(view);
        Assert.Contains("12.4 h", texts);
        Assert.Contains("light frames,", texts);
        Assert.Contains("on ASI2600MM, RC8.", texts);
        Assert.Equal(
            2,
            view.Named<ItemsControl>("LogLineFilters").GetVisualDescendants().OfType<Ellipse>().Count());
    }

    [AvaloniaFact]
    public void NothingConfigured_TheOverflowMenuHidesBothSubmenusAndTheSeparator()
    {
        using var page = BuildPage();
        var view = new TargetHeaderPart { DataContext = page };
        Show(view);

        Assert.False(MenuItemFromOverflow(view, "SendToNinaMenuItem").IsVisible);
        Assert.False(MenuItemFromOverflow(view, "SlewStellariumMenuItem").IsVisible);

        // The separator carries no name, so it is found by type: it is the only one in the menu.
        var owner = view.Named<Button>("OverflowButton");
        var separator = Assert.Single(
            Assert.IsType<MenuFlyout>(owner.Flyout).Items.OfType<Separator>());
        Assert.False(separator.IsVisible);
    }

    [AvaloniaFact]
    public void OneOfferedNinaInstance_TheSubmenuIsVisible_WithTheViewModelsOwnItems()
    {
        var general = new GeneralSettings
        {
            NinaInstancesDocument = IntegrationSettings.WriteInstances(
                [new IntegrationInstance("Obsy1", "http://a.local", true)]),
        };
        using var page = BuildPage(general);
        var view = new TargetHeaderPart { DataContext = page };
        Show(view);

        var item = MenuItemFromOverflow(view, "SendToNinaMenuItem");
        Assert.True(item.IsVisible);
        Assert.Same(page.NinaSendItems, item.ItemsSource);
        Assert.False(MenuItemFromOverflow(view, "SlewStellariumMenuItem").IsVisible);
    }

    // wave2-review: the one part of ruling B5 that can fail silently. A broken or unresolved
    // IntegrationSendItemTheme renders blank, inert leaf items and every other case still passes,
    // so this one opens the submenu and reads the generated container.
    [AvaloniaFact]
    public void TheGeneratedSubmenuItem_CarriesTheThemesHeaderAndCommand()
    {
        var general = new GeneralSettings
        {
            NinaInstancesDocument = IntegrationSettings.WriteInstances(
                [new IntegrationInstance("Obsy1", "http://a.local", true)]),
        };
        using var page = BuildPage(general);
        var view = new TargetHeaderPart { DataContext = page };
        Show(view);

        var item = MenuItemFromOverflow(view, "SendToNinaMenuItem");
        item.Open();
        Dispatcher.UIThread.RunJobs();

        var container = Assert.IsType<MenuItem>(item.ContainerFromIndex(0));
        Assert.Equal("Obsy1", container.Header);
        Assert.Same(page.NinaSendItems[0].SendCommand, container.Command);
    }

    [AvaloniaFact]
    public void AstroBinCsvResultLine_IsHidden_UntilTheViewModelSetsIt()
    {
        using var page = BuildPage();
        var view = new TargetHeaderPart { DataContext = page };
        Show(view);

        var line = view.Named<TextBlock>("AstroBinCsvResultLine");
        Assert.False(line.IsVisible);
    }

    // Avalonia shows no tooltip on a disabled control unless ShowOnDisabled is set.
    [AvaloniaFact]
    public void SurveyViewButton_CoordinatesSwitchOff_IsVisibleDisabledAndShowsItsTooltip()
    {
        using var harness = Factory
            .Create(
                general: new GeneralSettings { SurveyDownloadsEnabled = false },
                openSurveyView: _ => Task.CompletedTask)
            .Settle();
        var view = new TargetHeaderPart { DataContext = harness.ViewModel };
        Show(view);

        var button = view.Named<Button>("SurveyViewButton");

        Assert.True(button.IsVisible);
        Assert.False(button.IsEffectivelyEnabled);
        Assert.Equal(SurveyMessages.DisabledTooltip, ToolTip.GetTip(button));
        Assert.True(ToolTip.GetShowOnDisabled(button));

        // The delegate is present throughout, so a failure past this point is the switch, not the
        // delegate, failing to gate the button.
        harness.General = harness.General with { SurveyDownloadsEnabled = true };
        harness.RaiseGeneralChanged();
        harness.Settle();
        Dispatcher.UIThread.RunJobs();

        Assert.True(button.IsEffectivelyEnabled);
    }

    // A failure is the filters out of the bars' order, the wrong hours text, or the figures
    // drawn with no filter used.
    [AvaloniaFact]
    public void LogLine_ShowsThePerFilterHoursInTheBarsOrder_AndHidesWithNoBars()
    {
        using var harness = With(Factory.PopulatedTotals() with
        {
            FiltersUsed = ["Ha", "OIII", "SII"],
            IntegrationSecondsByFilter = new Dictionary<string, double>
            {
                ["Ha"] = 7_200d,
                ["OIII"] = 3_600d,
                ["SII"] = 1_800d,
            },
        });
        var view = new TargetHeaderPart { DataContext = harness.ViewModel };
        Show(view);

        var figures = view.Named<TextBlock>("IntegrationFigures");
        Assert.True(figures.IsEffectivelyVisible);
        Assert.Equal("SII 0.5 h  Ha 2.0 h  OIII 1.0 h", figures.Text);

        using var empty = With(Factory.PopulatedTotals() with
        {
            FiltersUsed = [],
            IntegrationSecondsByFilter = new Dictionary<string, double>(),
        });
        var bare = new TargetHeaderPart { DataContext = empty.ViewModel };
        Show(bare);

        Assert.False(bare.Named<TextBlock>("IntegrationFigures").IsEffectivelyVisible);
    }

    // Spec 12.4 places the button between Reveal folder and the overflow menu; a failure is the
    // button drawn elsewhere in the header.
    [AvaloniaFact]
    public void ActionRow_ChildOrder_IsRevealFolderThenSurveyViewThenOverflow()
    {
        using var harness = Factory.Create().Settle();
        var view = new TargetHeaderPart { DataContext = harness.ViewModel };
        Show(view);

        var revealFolder = view.Named<Button>("RevealFolderButton");
        var row = Assert.IsType<StackPanel>(revealFolder.Parent);
        var firstThree = row.Children.OfType<Button>().Take(3).Select(button => button.Name ?? "").ToArray();

        // The Details toggle follows the overflow menu and is not pinned here.
        Assert.Equal(["RevealFolderButton", "SurveyViewButton", "OverflowButton"], firstThree);
    }

    [AvaloniaFact]
    public void TargetHeaderPart_AtThePagesAllotment_TheOtherNamesAndTheActionsDoNotOverlap()
    {
        // Phase 12 verification, deviation 2. The names were a horizontal StackPanel inside the
        // identity line's star column, and a horizontal StackPanel measures its children at
        // infinite width, so at 1280x800 Copy frame list was drawn on top of the "NGC 224, M 31"
        // other-names text. Both were painted, one over the other.
        //
        // The third alias is what makes this case able to fail. The shipped window runs at an 18 px
        // root (general.text_size "large") and the harness's stub face is narrower than that per
        // character, so the aliases the fixture carries stop short of the first action here while
        // they overprint it in the application. The names the page has to fit are what changes
        // between the two; the layout rule being asserted is the same one.
        //
        // Phase 16 (Task 5b review P2): the first action is the Export button, which is narrower
        // than the Copy frame list button it replaced, so the actions block asks for less width and
        // the names have more. Re-measured in this harness after the change: the names end 14 px
        // short of the button, where they ended 4 px short of Copy frame list before it. The case
        // still sits near its edge, and the rule asserted is unchanged. The shipped 1280x800 window
        // at the 18 px root has not been re-measured since the button narrowed; that is the
        // launched-app question already on the coordinator's desk (spec line 4671).
        //
        // Polish wave 9 ruling 1: Export moved into the Nights ledger, so the header's first action
        // is Reveal folder.
        using var harness = Factory
            .Create(get: _ => Factory.PopulatedDetail(
                header: Factory.PopulatedHeader() with
                {
                    Aliases = ["NGC 224", "Andromeda Galaxy", "UGC 454"],
                }))
            .Settle();
        var view = new TargetHeaderPart { DataContext = harness.ViewModel, Margin = new Thickness(20, 0) };
        ShowAtThePagesAllotment(view);

        var others = view.Named<TextBlock>("OtherNames");
        var firstAction = view.Named<Button>("RevealFolderButton");
        Assert.True(others.IsEffectivelyVisible, "the fixture drew no other names, so this case cannot fail");
        Assert.True(firstAction.IsEffectivelyVisible);

        Rect InPage(Control control)
        {
            var origin = control.TranslatePoint(new Point(0, 0), view);
            Assert.NotNull(origin);
            return new Rect(origin!.Value, control.Bounds.Size);
        }

        var names = InPage(others);
        var action = InPage(firstAction);
        Assert.True(names.Width > 0d && action.Width > 0d);
        Assert.False(
            names.Intersects(action),
            $"the other names at {names} and Reveal folder at {action} overlap");
    }
}
