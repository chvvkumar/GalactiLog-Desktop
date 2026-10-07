using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Controls;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.Views.TargetDetail;
using GalactiLog.Core.Metrics;
using GalactiLog.Core.Text;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.Views;

/// <summary>
/// Design-spec 18.3's view smoke tests for spec 12.4's Copy Frame List modal: it parses, lays out,
/// binds against a populated page, paints the page background, and carries the
/// <c>page.frame-list</c> help glyph. The behaviour is asserted on the view-model, in
/// <c>FrameListDialogViewModelTests</c>.
/// </summary>
public class FrameListDialogWindowTests
{
    private static readonly DateOnly Night = new(2025, 12, 7);

    private static FrameListDialogViewModel Page()
    {
        var grade = new MetricGrade(0d, 2.1d);
        var grading = new FrameGrading(grade, grade, grade, grade, grade, grade, grade, grade, grade);
        var detail = SessionCardViewModelTestFactory.PopulatedDetail(Night) with
        {
            Frames =
            [
                PreviewModalViewModelTestFactory.Row(
                    Guid.NewGuid(), @"C:\Astro\M 31\2025-12-07\a.fits", "a.fits", grading),
            ],
        };

        var page = new FrameListDialogViewModel(
            TargetDetailViewModelTestFactory.ResolvedGroupKey,
            [new FrameListNight(Night, detail)],
            (_, _) => null,
            _ => Task.CompletedTask,
            post: action => action());

        page.PendingLoad?.Wait(TimeSpan.FromSeconds(30));
        return page;
    }

    private static FrameListDialogWindow Show(FrameListDialogViewModel page)
    {
        var window = new FrameListDialogWindow { DataContext = page };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void Window_ProducesANonZeroLayout()
    {
        var window = Show(Page());

        Assert.True(window.Bounds.Width > 0);
        Assert.True(window.Bounds.Height > 0);

        window.Close();
    }

    [AvaloniaFact]
    public void Window_PaintsThePageBackground()
    {
        // WindowBackgroundsTest auto-enrols this window; the per-view restatement is here so a
        // regression names this file rather than the reflective census.
        var window = Show(Page());

        Assert.NotNull(window.Background);
        Assert.NotEqual(Brushes.Transparent, window.Background);

        window.Close();
    }

    [AvaloniaFact]
    public void Window_HasEveryNamedControl()
    {
        var page = Page();
        var window = Show(page);

        Assert.NotNull(window.GetControl<ToggleButton>("GoodModeToggle"));
        Assert.NotNull(window.GetControl<ToggleButton>("BadModeToggle"));
        Assert.NotNull(window.GetControl<CheckBox>("IncludeUnmeasuredBox"));
        Assert.NotNull(window.GetControl<ComboBox>("FormatSelector"));
        Assert.NotNull(window.GetControl<ContentControl>("PartialLoadWarning"));
        Assert.NotNull(window.GetControl<Button>("CopyButton"));
        Assert.NotNull(window.GetControl<Button>("CancelButton"));
        Assert.Equal(page.TallyText, window.GetControl<TextBlock>("TallyText").Text);

        // The warning is silent on a fully loaded selection.
        Assert.False(window.GetControl<ContentControl>("PartialLoadWarning").IsVisible);

        window.Close();
    }

    [AvaloniaFact]
    public void Window_IncludeUnmeasured_IsHiddenInBadMode()
    {
        var page = Page();
        var window = Show(page);
        Assert.True(window.GetControl<CheckBox>("IncludeUnmeasuredBox").IsVisible);

        page.Mode = FrameListMode.Bad;
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.GetControl<CheckBox>("IncludeUnmeasuredBox").IsVisible);
        Assert.False(window.GetControl<ToggleButton>("GoodModeToggle").IsChecked);
        Assert.True(window.GetControl<ToggleButton>("BadModeToggle").IsChecked);

        window.Close();
    }

    [AvaloniaFact]
    public void Window_CarriesTheHelpGlyph()
    {
        // Spec 12.12 places page.frame-list on this dialog's heading and nowhere else, so Task 2's
        // census counts this one site.
        var window = Show(Page());

        var help = Assert.Single(
            window.GetVisualDescendants().OfType<HelpButton>().ToList());
        Assert.Equal("page.frame-list", help.Topic);

        window.Close();
    }

    [Fact]
    public void Window_UsesOnlyThemeTokens()
    {
        var markup = ReadMarkup();

        // Spec 14: no literal colour anywhere. Every brush is a DynamicResource token.
        Assert.DoesNotMatch(new Regex(@"=""#[0-9A-Fa-f]{3,8}"""), markup);
        Assert.DoesNotMatch(new Regex(@"(Background|Foreground|BorderBrush|Fill|Stroke)=""(?!\{)"), markup);
    }

    [Fact]
    public void Window_SetsNoFontSize()
    {
        // The repository-wide FontSizeTokenTest owns the ratio rule; this is the per-view
        // restatement, and it is stricter: this view sets no FontSize at all.
        Assert.DoesNotContain("FontSize", ReadMarkup(), StringComparison.Ordinal);
    }

    private static string ReadMarkup()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GalactiLog.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine(
            directory!.FullName,
            "src",
            "GalactiLog.App",
            "Views",
            "TargetDetail",
            "FrameListDialogWindow.axaml");
        Assert.True(File.Exists(path), $"{path} was not found.");
        return File.ReadAllText(path);
    }
}
