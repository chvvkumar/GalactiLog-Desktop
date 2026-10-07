using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Controls;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.Views.TargetDetail;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.Views;

/// <summary>
/// Design-spec 18.3's view smoke test for spec 12.17's Create mosaic modal: it parses, lays out,
/// binds the heading, the subline, the Nights rows and the disabled Add to existing with its
/// reason, and carries the <c>mosaic.create</c> glyph. Behaviour is asserted on the view model in
/// <c>CreateMosaicViewModelTests</c>.
/// </summary>
public class CreateMosaicWindowTests
{
    [AvaloniaFact]
    public void Window_LaysOut_AndBindsTheDialog()
    {
        var night = new DateOnly(2026, 3, 1);
        var page = new CreateMosaicViewModel(
            "NGC 7000",
            [night],
            [new NightFrameGroup(night, "Panel 1", "NGC 7000 Panel 1", 3), new NightFrameGroup(night, null, "NGC 7000", 2)],
            [],
            ["Panel", "P"],
            (_, _, _) => Guid.NewGuid(),
            _ => { });
        var window = new CreateMosaicWindow { DataContext = page };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.Bounds.Width > 0 && window.Bounds.Height > 0);
        Assert.Equal("mosaic.create", window.GetVisualDescendants().OfType<HelpButton>().Single().Topic);
        Assert.Equal("NGC 7000, 1 night", window.FindControl<TextBlock>("SublineText")!.Text);
        Assert.Equal("NGC 7000 (Mar 2026)", window.FindControl<TextBox>("NameBox")!.Text);

        var existing = window.FindControl<RadioButton>("ExistingMosaicRadio")!;
        Assert.False(existing.IsEffectivelyEnabled);
        Assert.Equal("No existing mosaic includes this target.", ToolTip.GetTip(existing));

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToList();
        Assert.Contains("No label", texts);
        Assert.Contains("Panel 1", texts);
        Assert.Contains("Nights with the same panel label are combined into one panel.", texts);
        Assert.Equal(2, window.GetVisualDescendants().OfType<TextBox>().Count(box => box.Watermark == "For example, Panel 1"));
        Assert.False(window.FindControl<Button>("CreateButton")!.IsEffectivelyEnabled);

        window.Close();
    }
}
