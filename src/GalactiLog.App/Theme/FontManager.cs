using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace GalactiLog.App.Theme;

/// <summary>One interface typeface the Display tab offers.</summary>
/// <param name="Id">The stored <c>general.font</c> value.</param>
/// <param name="DisplayName">The label the picker shows, rendered in the family itself.</param>
/// <param name="FontFamily">The family with the same system fallbacks App.axaml's FontSans names.
/// </param>
/// <param name="Folder">The folder under Assets/Fonts that holds the four static weights and the
/// licence, empty for Atkinson Hyperlegible Next, which predates the per-family folders.</param>
/// <param name="FilePrefix">The file name before <c>-Regular.ttf</c> and the other weights.</param>
public sealed record FontOption(string Id, string DisplayName, FontFamily FontFamily, string Folder, string FilePrefix)
{
    /// <summary>The Regular weight as an embedded asset, which is what the charts load their
    /// SkiaSharp typeface from.</summary>
    public Uri RegularAsset => new($"avares://GalactiLog/Assets/Fonts/{(Folder.Length == 0 ? "" : Folder + "/")}{FilePrefix}-Regular.ttf");
}

/// <summary>The font id the settings document held when the process started, read once by
/// <c>AppHost.Build</c>, the same shape and for the same reason as <see cref="StartupTheme"/>.
/// </summary>
public sealed record StartupFont(string FontId);

/// <summary>
/// The interface typeface picker's table and its apply path, structured as <see cref="ThemeManager"/>
/// is: one table, an <see cref="ApplyStored"/> for startup and an <see cref="Apply"/> the Display
/// tab calls on every selection change. An id this build does not ship falls back to Inter.
/// </summary>
public static class FontManager
{
    private const string Fallbacks = ",Segoe UI,sans-serif";

    /// <summary>The five families, in picker order. The first is the default.</summary>
    public static IReadOnlyList<FontOption> Options { get; } =
    [
        Option("inter", "Inter", "Inter", "Inter", "Inter"),
        Option("atkinson", "Atkinson Hyperlegible", "Atkinson Hyperlegible Next", "", "AtkinsonHyperlegibleNext"),
        Option("montserrat", "Montserrat", "Montserrat", "Montserrat", "Montserrat"),
        Option("ibm-plex-sans", "IBM Plex Sans", "IBM Plex Sans", "IBMPlexSans", "IBMPlexSans"),
        Option("source-sans-3", "Source Sans 3", "Source Sans 3", "SourceSans3", "SourceSans3"),
    ];

    // The fragment after the # is the family name in the font's own name table, which is what
    // Avalonia matches; see App.axaml's FontSans.
    private static FontOption Option(string id, string label, string family, string folder, string prefix)
    {
        var path = folder.Length == 0 ? "Assets/Fonts" : $"Assets/Fonts/{folder}";
        return new FontOption(id, label, new FontFamily($"avares://GalactiLog/{path}#{family}{Fallbacks}"), folder, prefix);
    }

    /// <summary>The option for a stored id, or Inter for one this build does not ship. The stored
    /// value is never rewritten.</summary>
    public static FontOption Resolve(string? fontId)
        => Options.FirstOrDefault(option => string.Equals(option.Id, fontId, StringComparison.Ordinal)) ?? Options[0];

    /// <summary>Applies the stored font once at startup, before the first window is built. Null on
    /// the headless harness, which has no container, and Inter is applied there.</summary>
    public static void ApplyStored(StartupFont? stored) => Apply(stored?.FontId);

    // Theme/Controls.axaml's Window style reads FontSans through a StaticResource, which is
    // resolved once when the styles load, before any stored id is known. This style is appended
    // after every shipped style, so it wins at equal specificity, and its DynamicResource is what
    // makes replacing the FontSans resource below reach windows that are already open.
    private static Style? _bridge;

    /// <summary>
    /// Swaps the interface typeface: replaces the <c>FontSans</c> resource, which every window
    /// inherits, and hands the Regular weight to the charts. Idempotent. UI thread only.
    /// </summary>
    public static void Apply(string? fontId)
    {
        var option = Resolve(fontId);

        if (Application.Current is { } application)
        {
            Dispatcher.UIThread.VerifyAccess();

            if (_bridge is null || !application.Styles.Contains(_bridge))
            {
                _bridge = new Style(selector => selector.OfType<Window>())
                {
                    Setters = { new Setter(Window.FontFamilyProperty, new DynamicResourceExtension("FontSans")) },
                };
                application.Styles.Add(_bridge);
            }

            application.Resources["FontSans"] = option.FontFamily;
        }

        ChartTheme.ApplyTypeface(option.RegularAsset);
    }
}
