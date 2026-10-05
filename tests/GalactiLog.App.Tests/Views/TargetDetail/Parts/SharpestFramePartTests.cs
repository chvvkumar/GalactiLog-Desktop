using Avalonia.Threading;
using Avalonia;
using GalactiLog.App.Tests.TestSupport;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using GalactiLog.App.Views.TargetDetail.Parts;
using Xunit;
using Xunit.Abstractions;
using static GalactiLog.App.Tests.Views.TargetDetail.Parts.ThumbnailKit;
using static GalactiLog.App.Tests.Views.TargetDetail.Parts.NightPartsTestKit;

namespace GalactiLog.App.Tests.Views.TargetDetail.Parts;

// Thumbnail box cases of SharpestFramePart: the box bound, the aspect and the wrap.
public class SharpestFramePartTests(ITestOutputHelper output)
{
    [AvaloniaFact]
    public void SharpestFramePart_AThumbnailBox_IsBoundedAt120By240()
    {
        // A panorama, wider than 2:1: it is bounded by the width and falls below 120 high, which
        // is spec 12.4's own worked case for the 240 cap.
        using var thumbnails = new Thumbnails(_ => Synthetic(1200, 300));
        var (part, harness) = RigHost(card => new SharpestFramePart { DataContext = card }, [RigA], thumbnails);
        using var scope = harness;

        var box = Assert.Single(Boxes(part));

        Assert.Equal(120d, box.MaxHeight);
        Assert.Equal(240d, box.MaxWidth);
        Assert.True(double.IsNaN(box.Width), "the box declares a Width, which would stop Uniform fitting");
        Assert.True(double.IsNaN(box.Height), "the box declares a Height, which would stop Uniform fitting");

        Assert.Equal(240d, box.Bounds.Width, 1);
        Assert.Equal(60d, box.Bounds.Height, 1);

        var image = Assert.Single(box.GetVisualDescendants().OfType<Image>());
        Assert.Equal(Stretch.Uniform, image.Stretch);
    }

    [AvaloniaFact]
    public void SharpestFramePart_AThumbnailBox_KeepsTheFramesAspect()
    {
        // Spec 12.4's three worked examples, measured. Nothing is cropped and nothing is scaled
        // down to force a fit.
        foreach (var (pixelWidth, pixelHeight, boxWidth, boxHeight) in new[]
        {
            (300, 200, 180d, 120d),
            (400, 300, 160d, 120d),
            (200, 300, 80d, 120d),
        })
        {
            using var thumbnails = new Thumbnails(_ => Synthetic(pixelWidth, pixelHeight));
            var (part, harness) = RigHost(card => new SharpestFramePart { DataContext = card }, [RigA], thumbnails);
            using var scope = harness;

            var box = Assert.Single(Boxes(part));
            Assert.Equal(boxWidth, box.Bounds.Width, 1);
            Assert.Equal(boxHeight, box.Bounds.Height, 1);
        }
    }

    [AvaloniaFact]
    public void SharpestFramePart_TwoBoxes_SitSideBySideAtTheShippedPaneWidth()
    {
        using var thumbnails = new Thumbnails(_ => Synthetic(300, 200));
        var (part, harness) = RigHost(card => new SharpestFramePart { DataContext = card }, [RigA, RigB], thumbnails, width: 492, height: 620);
        using var scope = harness;

        var boxes = Boxes(part);
        Assert.Equal(2, boxes.Count);

        var first = boxes[0].TranslatePoint(new Point(0, 0), part)!.Value;
        var second = boxes[1].TranslatePoint(new Point(0, 0), part)!.Value;

        Assert.Equal(first.Y, second.Y, 1);
        Assert.Equal(8d, second.X - first.X - boxes[0].Bounds.Width, 1);

        output.WriteLine(
            $"492 px pane, two 3:2 boxes: first at x {first.X:0.##}, second at x {second.X:0.##}, " +
            $"each {boxes[0].Bounds.Width:0.##} by {boxes[0].Bounds.Height:0.##}");
    }

    [AvaloniaFact]
    public void SharpestFramePart_ThreeBoxes_WrapToASecondLine()
    {
        // Three panoramas at the 240 pixel cap need 736 pixels with their gaps and the pane has
        // 492, so the third goes to the next line in the same order. Nothing is scaled down.
        using var thumbnails = new Thumbnails(_ => Synthetic(1200, 300));
        var (part, harness) = RigHost(card => new SharpestFramePart { DataContext = card }, [RigA, RigB, RigC], thumbnails, width: 492, height: 620);
        using var scope = harness;

        var boxes = Boxes(part);
        Assert.Equal(3, boxes.Count);

        var tops = boxes
            .Select(box => box.TranslatePoint(new Point(0, 0), part)!.Value.Y)
            .ToList();

        Assert.Equal(tops[0], tops[1], 1);
        Assert.True(tops[2] > tops[1], $"the third box sits at {tops[2]}, on the first line at {tops[1]}");
    }

    [AvaloniaFact]
    public void SharpestFramePart_ANightWithNoThumbnail_DrawsThePlaceholder()
    {
        // No factory at all, which is the same state a night whose every candidate failed to
        // decode ends in.
        var (pane, harness) = RigHost(card => new SharpestFramePart { DataContext = card }, [RigA]);
        using var scope = harness;

        Assert.DoesNotContain(Boxes(pane), box => box.IsEffectivelyVisible);

        var placeholder = Assert.Single(Placeholders(pane), border => border.IsEffectivelyVisible);
        Assert.Equal(180d, placeholder.Bounds.Width, 1);
        Assert.Equal(120d, placeholder.Bounds.Height, 1);
        Assert.Equal(new Thickness(1), placeholder.BorderThickness);
        Assert.Contains("No thumbnail", VisibleTexts(pane));
    }

    [AvaloniaFact]
    public void SharpestFramePart_EveryCandidateUndecodable_DrawsThePlaceholderAndKeepsTheStrip()
    {
        // Verification escalation 2: with the reference frame's file renamed away the strip
        // appeared to vanish rather than showing spec 12.4's placeholder. This is that state built
        // headless, with a real slot factory whose decode comes back empty for every candidate,
        // which is what an unreadable file produces.
        //
        // The distinction the case draws is between "no factory", which the case above covers, and
        // "a factory that fails three times": the second is the one the reader hits, and the box
        // must end on the placeholder rather than on an empty button or a hidden strip.
        using var thumbnails = new Thumbnails(_ => null);
        var (pane, harness) = RigHost(card => new SharpestFramePart { DataContext = card }, [RigA], thumbnails);
        using var scope = harness;

        var rig = Assert.Single(harness.Card.Rigs);
        thumbnails.PumpUntil(() => rig.Thumbnail is { IsLoading: false });
        Dispatcher.UIThread.RunJobs();

        // The walk ran to its cap and came back with nothing.
        Assert.True(rig.HasStarted);
        Assert.False(rig.HasThumbnail);
        Assert.True(rig.ShowsPlaceholder);

        // The strip is still on screen and still has its one box's worth of height.
        var strip = pane.Named<ItemsControl>("ThumbnailStrip");
        Assert.True(strip.IsEffectivelyVisible);
        Assert.True(strip.Bounds.Height >= 120d, $"the strip is {strip.Bounds.Height} px, below the placeholder");

        // And the box is the placeholder, at its declared 180 by 120, with the words on it.
        Assert.DoesNotContain(Boxes(pane), box => box.IsEffectivelyVisible);
        var placeholder = Assert.Single(Placeholders(pane), border => border.IsEffectivelyVisible);
        Assert.Equal(180d, placeholder.Bounds.Width, 1);
        Assert.Equal(120d, placeholder.Bounds.Height, 1);
        Assert.Contains("No thumbnail", VisibleTexts(pane));
    }
}
