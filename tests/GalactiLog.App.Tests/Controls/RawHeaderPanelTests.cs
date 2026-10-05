using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Controls;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.Controls;

// Design-spec 18.3's view smoke tests for spec 12.4's raw header panel, and the roadmap Verify
// line at the view level: the two FWHM rows carry distinct labels and a repeated COMMENT renders
// every line, both as a rendering claim rather than a view-model one.
public class RawHeaderPanelTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);
    private static readonly Guid ImageId = Guid.NewGuid();

    private static FrameHeaders Sample() => new(
        RawHeaders:
        [
            new HeaderEntry("GAIN", ["100"]),
            new HeaderEntry("OBJECT", ["M 51"]),
            new HeaderEntry("COMMENT", ["line one", "line two", "line three"]),
        ],
        Provenance: new Dictionary<string, string> { ["median_fwhm"] = "MEANFWHM" },
        MedianFwhm: 3.10,
        Fwhm: 2.85);

    private static RawHeaderPanelViewModel CreatePanel(FrameHeaders? result)
    {
        var panel = new RawHeaderPanelViewModel(ImageId, _ => result, post: action => action());
        panel.Load();
        panel.PendingLoad?.Wait(Budget);
        return panel;
    }

    private static RawHeaderPanelViewModel CreateFailingPanel()
    {
        var panel = new RawHeaderPanelViewModel(
            ImageId,
            _ => throw new InvalidOperationException("the database is locked"),
            post: action => action());
        panel.Load();
        panel.PendingLoad?.Wait(Budget);
        return panel;
    }

    private static Window Show(RawHeaderPanel control)
    {
        var window = new Window { Width = 800, Height = 600, Content = control };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static IReadOnlyList<string> VisibleTexts(RawHeaderPanel control)
        => [.. control.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible)
            .Select(block => block.Text ?? "")];

    [AvaloniaFact]
    public void RawHeaderPanel_Constructs_AndRendersAPopulatedViewModel()
    {
        var control = new RawHeaderPanel { DataContext = CreatePanel(Sample()) };
        Show(control);

        Assert.True(control.Bounds.Width > 0);
        Assert.True(control.Bounds.Height > 0);

        var texts = VisibleTexts(control);
        Assert.Contains("GAIN", texts);
        Assert.Contains("100", texts);
        Assert.Contains("OBJECT", texts);
        Assert.Contains("M 51", texts);
    }

    [AvaloniaFact]
    public void RawHeaderPanel_RendersBothFwhmRowsWithDistinctLabels()
    {
        var control = new RawHeaderPanel { DataContext = CreatePanel(Sample()) };
        Show(control);

        var texts = VisibleTexts(control);
        Assert.Contains("Header FWHM", texts);
        Assert.Contains("FWHM (arcsec)", texts);
        Assert.Contains("3.10 (MEANFWHM)", texts);
        Assert.Contains("2.85", texts);
    }

    [AvaloniaFact]
    public void RawHeaderPanel_MultiLineComment_RendersEveryLine()
    {
        var control = new RawHeaderPanel { DataContext = CreatePanel(Sample()) };
        Show(control);

        var texts = VisibleTexts(control);
        Assert.Contains("line one", texts);
        Assert.Contains("line two", texts);
        Assert.Contains("line three", texts);
    }

    [AvaloniaFact]
    public void RawHeaderPanel_FilterHidesNonMatchingRows()
    {
        var panel = CreatePanel(Sample());
        var control = new RawHeaderPanel { DataContext = panel };
        Show(control);

        panel.Filter = "gain";
        Dispatcher.UIThread.RunJobs();

        var texts = VisibleTexts(control);
        Assert.Contains("GAIN", texts);
        Assert.DoesNotContain("OBJECT", texts);
    }

    [AvaloniaFact]
    public void RawHeaderPanel_EmptyDocument_RendersTheEmptyState()
    {
        var empty = Sample() with { RawHeaders = [] };
        var control = new RawHeaderPanel { DataContext = CreatePanel(empty) };
        Show(control);

        Assert.Contains("No raw headers recorded for this frame.", VisibleTexts(control));
    }

    [AvaloniaFact]
    public void RawHeaderPanel_FailedRead_RendersTheFailureLine()
    {
        // Review finding 2: a failed read must not render as "No raw headers recorded".
        var control = new RawHeaderPanel { DataContext = CreateFailingPanel() };
        Show(control);

        var texts = VisibleTexts(control);
        Assert.Contains("This frame's headers could not be loaded. See the log for details.", texts);
        Assert.DoesNotContain("No raw headers recorded for this frame.", texts);
    }

    [AvaloniaFact]
    public void RawHeaderPanel_MissingFrame_RendersTheMissingLine()
    {
        // Review finding 2: a frame a rescan pruned must not render as "No raw headers recorded".
        var control = new RawHeaderPanel { DataContext = CreatePanel(null) };
        Show(control);

        var texts = VisibleTexts(control);
        Assert.Contains("This frame is no longer in the library. It may have been removed by a rescan.", texts);
        Assert.DoesNotContain("No raw headers recorded for this frame.", texts);
    }

    [AvaloniaFact]
    public void RawHeaderPanel_EveryTextBlock_RendersAtAReadableSize()
    {
        // Scales.axaml's FontSize* keys are ratios (0.500 to 0.786), not point sizes: binding one
        // to FontSize renders text at well under a pixel. Nothing in this control sets FontSize.
        var control = new RawHeaderPanel { DataContext = CreatePanel(Sample()) };
        Show(control);

        var blocks = control.GetVisualDescendants().OfType<TextBlock>().ToList();
        Assert.NotEmpty(blocks);
        Assert.All(blocks, block => Assert.True(
            block.FontSize >= 8,
            $"TextBlock '{block.Text}' renders at {block.FontSize}."));
    }
}
