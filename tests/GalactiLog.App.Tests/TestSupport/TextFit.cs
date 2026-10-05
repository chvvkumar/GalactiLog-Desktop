using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Xunit;

namespace GalactiLog.App.Tests.TestSupport;

/// <summary>The fit check the two table part test files share: no visible text is wider than its
/// box (when it never trims) or taller than its row.</summary>
public static class TextFit
{
    public static void AssertTextFitsItsBox(Control root)
    {
        foreach (var block in root.GetVisualDescendants().OfType<TextBlock>().Where(b => !string.IsNullOrEmpty(b.Text) && b.IsEffectivelyVisible))
        {
            var text = new FormattedText(
                block.Text!,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch),
                block.FontSize,
                null);
            if (block.TextTrimming == TextTrimming.None)
            {
                Assert.True(
                    text.Width + block.Padding.Left + block.Padding.Right <= block.Bounds.Width + 0.5,
                    $"'{block.Text}' draws {text.Width + block.Padding.Left + block.Padding.Right} wide in a column of {block.Bounds.Width}");
            }

            var row = double.IsNaN(block.Height)
                ? block.GetVisualAncestors().First(a => a is Grid || a is StackPanel { Orientation: Orientation.Horizontal }).Bounds.Height
                : block.Bounds.Height;
            Assert.True(text.Height <= row + 0.5, $"'{block.Text}' is {text.Height} tall in a row of {row}");
        }
    }
}
