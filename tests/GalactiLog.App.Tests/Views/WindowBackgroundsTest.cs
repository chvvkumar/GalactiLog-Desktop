using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using GalactiLog.App;
using Xunit;

namespace GalactiLog.App.Tests.Views;

/// <summary>
/// Every <see cref="Window"/> root in the application paints an opaque background.
/// </summary>
/// <remarks>
/// <para>
/// A user report against the installed 1.0.0-alpha.1 build: every dialog was see-through and hard
/// to read. Five windows painted their root with <c>ColorBgBase</c>, and the one shipped theme
/// defines that token as <c>#00000000</c>, fully transparent. It is transparent on purpose, for
/// the panels inside a window that sit over the page gradient, so the fix was the windows and not
/// the token. The main window was never affected because it already painted
/// <c>BrushPageBackground</c>.
/// </para>
/// <para>
/// A convention distributed across N windows will be forgotten at window N+1 (design-lessons
/// rule 2), so this is the structural form: the walk finds the windows itself, by reflection over
/// the application assembly, and a window added in a later phase is covered with no edit here.
/// The resolution is the real one, through the theme the headless application applies, rather than
/// a text match on the token name: a token whose value is later changed to a translucent colour
/// fails here too.
/// </para>
/// </remarks>
public class WindowBackgroundsTest
{
    [AvaloniaFact]
    public void EveryWindowRoot_PaintsAFullyOpaqueBackground()
    {
        var windowTypes = typeof(App).Assembly
            .GetTypes()
            .Where(type => typeof(Window).IsAssignableFrom(type))
            .Where(type => !type.IsAbstract && !type.IsGenericTypeDefinition)
            .Where(type => type.GetConstructor(Type.EmptyTypes) is not null)
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToList();

        // The six windows this application ships. A bound rather than an equality, so adding a
        // seventh does not fail here for the wrong reason, but a walk that found nothing does.
        Assert.True(
            windowTypes.Count >= 6,
            $"Expected at least six Window types, saw {windowTypes.Count}.");

        foreach (var type in windowTypes)
        {
            var window = (Window)Activator.CreateInstance(type)!;
            var background = window.Background;

            Assert.True(
                background is not null,
                $"{type.FullName} sets no Background, so it paints the platform default.");

            // The brush's own opacity multiplies every channel, so a fully opaque colour at 0.5
            // opacity is still see-through.
            Assert.True(
                background!.Opacity >= 1d,
                $"{type.FullName}'s background brush has opacity {background.Opacity}.");

            foreach (var alpha in Alphas(background, type))
            {
                Assert.True(
                    alpha == byte.MaxValue,
                    $"{type.FullName}'s background is translucent (alpha {alpha}). A Window root " +
                    "may only use an opaque theme token, normally BrushPageBackground.");
            }

            window.Close();
        }
    }

    // Every alpha channel a brush paints with: one for a solid colour, one per stop for a
    // gradient. A brush of any other kind fails rather than passing silently, because this case
    // cannot tell whether it is opaque.
    private static IReadOnlyList<byte> Alphas(IBrush brush, Type owner)
    {
        switch (brush)
        {
            case ISolidColorBrush solid:
                return [solid.Color.A];

            case IGradientBrush gradient:
                Assert.True(
                    gradient.GradientStops.Count > 0,
                    $"{owner.FullName}'s gradient background has no stops.");
                return [.. gradient.GradientStops.Select(stop => stop.Color.A)];

            default:
                Assert.Fail(
                    $"{owner.FullName}'s background is a {brush.GetType().Name}, whose opacity " +
                    "this case cannot read. Use an opaque theme token.");
                return [];
        }
    }
}
