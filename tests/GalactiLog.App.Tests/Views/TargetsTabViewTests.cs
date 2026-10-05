using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.App.Views.Settings;
using GalactiLog.Core.Targets;
using Xunit;

namespace GalactiLog.App.Tests.Views;

/// <summary>
/// The Targets tab's first view test (design-spec 18.3's view smoke scope). The tab had no test of
/// its own and was reached only through <c>SettingsViewTests</c>; Phase 14B Task 3 adds the create
/// target form (spec 12.7, PAR-001) and moves the file into the Observing Ledger vocabulary, and
/// this is where both are asserted.
/// </summary>
public class TargetsTabViewTests
{
    private static string ViewSource(string fileName)
        => File.ReadAllText(Path.Combine(
            SourceScan.SrcRoot(), "GalactiLog.App", "Views", "Settings", fileName));

    private static (TargetsTabView View, TargetsTabViewModelTestFactory.Harness Harness) Show(
        CreateTargetViewModel? createForm = null)
    {
        var harness = TargetsTabViewModelTestFactory
            .Create(
                pending: [TargetsTabViewModelTestFactory.Candidate()],
                createTarget: createForm ?? TargetsTabViewModelTestFactory.CreateForm())
            .Settle();

        var view = new TargetsTabView { DataContext = harness.ViewModel };
        var window = new Window { Width = 1100, Height = 800, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (view, harness);
    }

    [AvaloniaFact]
    public void View_Constructs_AndLaysOutNonZero()
    {
        var (view, harness) = Show();
        using var scope = harness;

        Assert.True(view.Bounds.Width > 0);
        Assert.True(view.Bounds.Height > 0);
        Assert.True(view.GetControl<StackPanel>("CandidateSection").IsVisible);
        Assert.True(view.GetControl<StackPanel>("CreateTargetSection").IsVisible);
    }

    [AvaloniaFact]
    public void View_TheCreateFormIsClosedUntilTheButtonIsPressed()
    {
        var (view, harness) = Show();
        using var scope = harness;

        var form = view.GetControl<StackPanel>("CreateTargetForm");
        Assert.False(form.IsVisible);

        view.GetControl<Button>("CreateTargetButton").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(form.IsVisible);
    }

    [AvaloniaFact]
    public void View_TheFormHasTheSevenFieldsAndTheCheckBox()
    {
        var (view, harness) = Show();
        using var scope = harness;

        harness.ViewModel.CreateTarget!.OpenCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // Spec 12.7's six fields, with the free-text box the Other choice reveals as the seventh.
        Assert.NotNull(view.GetControl<TextBox>("CreateTargetNameBox"));
        Assert.NotNull(view.GetControl<ComboBox>("CreateTargetTypeBox"));
        Assert.NotNull(view.GetControl<TextBox>("CreateTargetOtherBox"));
        Assert.NotNull(view.GetControl<TextBox>("CreateTargetRaBox"));
        Assert.NotNull(view.GetControl<TextBox>("CreateTargetDecBox"));
        Assert.NotNull(view.GetControl<TextBox>("CreateTargetCatalogIdBox"));
        Assert.NotNull(view.GetControl<TextBox>("CreateTargetAliasBox"));
        Assert.NotNull(view.GetControl<CheckBox>("CreateTargetUserDefinedCheckBox"));
        Assert.NotNull(view.GetControl<Button>("CreateTargetSubmitButton"));
        Assert.NotNull(view.GetControl<Button>("CreateTargetCancelButton"));
        Assert.NotNull(view.GetControl<TextBlock>("CreateTargetMessageText"));

        // Departure 5: the checkbox ships checked.
        Assert.True(view.GetControl<CheckBox>("CreateTargetUserDefinedCheckBox").IsChecked);
    }

    [AvaloniaFact]
    public void View_TheTypeBoxListsFifteenChoicesPlusTheEmptyOne()
    {
        var (view, harness) = Show();
        using var scope = harness;

        harness.ViewModel.CreateTarget!.OpenCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var choices = view.GetControl<ComboBox>("CreateTargetTypeBox").ItemsSource!.Cast<string>().ToList();

        Assert.Equal(16, choices.Count);
        Assert.Equal("", choices[0]);
        Assert.Equal(ObjectTypeCategories.DisplayCategories, choices.Skip(1).Take(9));
        Assert.Equal(ObjectTypeCategories.SolarSystemCategories, choices.Skip(10).Take(5));
        Assert.Equal("Other", choices[^1]);
    }

    [AvaloniaFact]
    public void View_TheOtherBoxAppearsOnlyForOther()
    {
        var (view, harness) = Show();
        using var scope = harness;

        var form = harness.ViewModel.CreateTarget!;
        form.OpenCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var other = view.GetControl<TextBox>("CreateTargetOtherBox");
        Assert.False(other.IsVisible);

        form.SelectedObjectType = "Other";
        Dispatcher.UIThread.RunJobs();
        Assert.True(other.IsVisible);

        form.SelectedObjectType = "Galaxy";
        Dispatcher.UIThread.RunJobs();
        Assert.False(other.IsVisible);
    }

    [AvaloniaFact]
    public void View_TheCoordinateBoxesAreDisabledForASolarSystemCategory()
    {
        var (view, harness) = Show();
        using var scope = harness;

        var form = harness.ViewModel.CreateTarget!;
        form.OpenCommand.Execute(null);
        form.Ra = "10.5";
        Dispatcher.UIThread.RunJobs();

        form.SelectedObjectType = "Comet";
        Dispatcher.UIThread.RunJobs();

        // Spec 12.7: a solar system category clears RA and Dec, because a fixed position is
        // meaningless for a moving object.
        Assert.Equal("", view.GetControl<TextBox>("CreateTargetRaBox").Text);
        Assert.False(view.GetControl<TextBox>("CreateTargetRaBox").IsEnabled);
        Assert.False(view.GetControl<TextBox>("CreateTargetDecBox").IsEnabled);
    }

    [AvaloniaFact]
    public void View_DeclaresNoStyleOfItsOwn()
    {
        // Theme/Controls.axaml is the whole shared control vocabulary (HANDOFF rule 5), which
        // ControlStyleScanTest enforces across every view; this is the same rule for this one file.
        var source = ViewSource("TargetsTabView.axaml");

        Assert.DoesNotContain("<Style", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".Styles>", source, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void View_DeclaresNoCornerRadius()
    {
        // The one mechanical measure of the Observing Ledger move (roadmap section 0): radius is a
        // property of a shared style in Theme/Controls.axaml, never of a view's own element. This
        // file declared four before Phase 14B Task 3.
        Assert.DoesNotContain("CornerRadius", ViewSource("TargetsTabView.axaml"), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void View_SectionHeadingsUseTheTypeTiers()
    {
        var (view, harness) = Show();
        using var scope = harness;

        var headings = view.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(block => block.Classes.Contains("t-label"))
            .ToList();

        // Every section heading takes the label tier and the section class; a heading is
        // TextBlock.t-label.section, never a hand-set FontWeight with its own Foreground.
        Assert.Contains(headings, heading => heading.Text == "Duplicate suggestions");
        Assert.Contains(headings, heading => heading.Text == "Create target");
        Assert.All(headings, heading => Assert.Contains("section", heading.Classes));
        Assert.DoesNotContain("FontWeight", ViewSource("TargetsTabView.axaml"), StringComparison.Ordinal);

        // DESIGN.md section 8: a section heading sits on a hairline rule, never on a card.
        Assert.Contains(
            view.GetVisualDescendants().OfType<Border>(),
            border => border.Classes.Contains("rule"));
    }

    [AvaloniaFact]
    public void View_BadgesAreBorderTag()
    {
        var (view, harness) = Show();
        using var scope = harness;

        // The candidate row's method badge. Border.tag is the whole badge vocabulary; a
        // hand-rolled Border with ColorBadgeBg is the idiom this replaced.
        var tags = view.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("tag"))
            .ToList();

        // Phase 14B fixer, fixer list item 26 (task3-review P3): the class filter is the rule, and
        // the radius it draws with is Theme/Controls.axaml's literal. Pinning the literal here put
        // a second copy of it in a view test, so a deliberate change to the shared badge would
        // have failed this file rather than the vocabulary's own.
        Assert.NotEmpty(tags);
    }

    [AvaloniaFact]
    public void View_EveryTextBlock_RendersAtAReadableSize()
    {
        // Scales.axaml's FontSize* keys are ratios (0.500 to 0.786), not point sizes: binding one
        // to FontSize renders text at well under a pixel. Nothing in this view sets FontSize.
        var (view, harness) = Show();
        using var scope = harness;

        harness.ViewModel.CreateTarget!.OpenCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var blocks = view.GetVisualDescendants().OfType<TextBlock>().ToList();
        Assert.NotEmpty(blocks);
        Assert.All(blocks, block => Assert.True(
            block.FontSize >= 8,
            $"TextBlock '{block.Text}' renders at {block.FontSize}."));
    }
}
