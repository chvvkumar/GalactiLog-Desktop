using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;

namespace GalactiLog.App.Theme;

// Six themes ship (P12 R7): civil-dusk, the default, luminance and deep-sky (formerly
// observing-ledger and glass-void), red-light, and the two
// light ones, atlas and logbook. Adding a seventh means adding one more dictionary with the same
// 41 keys and one more entry in the table below; no other file changes (spec 14). App.axaml pins
// RequestedThemeVariant="Dark" (TRACKING hard rule 4) for startup; Apply sets the variant
// alongside the dictionary swap, so this type stays the owner of both.
//
// Spec 13 requires the chart axis, legend and tooltip colours to be "read from the merged
// resource dictionary at startup and re-read on theme change". Startup is App.axaml.cs's
// ChartTheme.Apply() call. A theme swap must call ChartTheme.Apply() again, immediately after
// the dictionary and the theme variant are replaced: the paints LiveCharts2 holds are values, not
// DynamicResource lookups, so nothing else re-reads them. Apply is public and idempotent for
// exactly this reason and raises ChartTheme.Changed so live charts re-paint. Ruling Q19; Phase 9
// Task 6 owns the theme picker and this apply path (FIXER item 8).
/// <summary>
/// The theme id the settings document held when the process started, read once by
/// <c>AppHost.Build</c> and registered as a singleton beside <c>StartupState</c>.
/// </summary>
/// <remarks>
/// A value rather than a <c>Func&lt;GeneralSettings&gt;</c>: the one reader is
/// <c>App.OnFrameworkInitializationCompleted</c>, which runs on the UI thread immediately before
/// the first window is built, and <c>SettingsStore.GetGeneral</c> opens a SQLite context. The
/// document is already read once in <c>AppHost.Build</c> on <c>Program.Main</c>'s thread, so the
/// startup theme costs no second read (the same rule the phase review's item 2 records for every
/// other settings document a view-model needs). A theme chosen later in Settings does not come
/// through here: the Display tab calls <see cref="ThemeManager.Apply"/> directly and the store
/// keeps the new id for the next start.
/// </remarks>
/// <param name="ThemeId">The stored <c>general.theme</c>. An id this build does not ship falls
/// back to the first shipped theme when it is applied (ruling Q13); nothing rewrites the stored
/// value.</param>
public sealed record StartupTheme(string ThemeId);

public static class ThemeManager
{
    /// <summary>One entry per shipped theme, in picker order: the stored <c>general.theme</c> id,
    /// the label the picker shows, the dictionary that carries its 41 tokens, and the Fluent
    /// variant its surfaces assume, <c>Light</c> for the two paper themes so the platform controls
    /// follow. The first entry is the default, and an id this build does not ship, including the
    /// retired <c>observing-ledger</c> and <c>glass-void</c>, falls back to it; no stored
    /// <c>general.theme</c> value is rewritten (ruling Q13).</summary>
    private static readonly IReadOnlyList<(string Id, string Label, string Source, ThemeVariant Variant)> Table =
    [
        ("civil-dusk", "Civil Dusk", "avares://GalactiLog/Theme/Themes/CivilDusk.axaml", ThemeVariant.Dark),
        ("luminance", "Luminance", "avares://GalactiLog/Theme/Themes/Luminance.axaml", ThemeVariant.Dark),
        ("deep-sky", "Deep Sky", "avares://GalactiLog/Theme/Themes/DeepSky.axaml", ThemeVariant.Dark),
        ("red-light", "Red Light", "avares://GalactiLog/Theme/Themes/RedLight.axaml", ThemeVariant.Dark),
        ("atlas", "Atlas", "avares://GalactiLog/Theme/Themes/Atlas.axaml", ThemeVariant.Light),
        ("logbook", "Logbook", "avares://GalactiLog/Theme/Themes/Logbook.axaml", ThemeVariant.Light),
    ];

    /// <summary>The shipped theme ids, in the order the picker renders them.</summary>
    public static IReadOnlyList<string> Available { get; } = [.. Table.Select(entry => entry.Id)];

    /// <summary>The label for a theme id, or the id itself for one this build does not ship.
    /// </summary>
    public static string LabelFor(string themeId)
    {
        foreach (var entry in Table)
        {
            if (string.Equals(entry.Id, themeId, StringComparison.Ordinal))
            {
                return entry.Label;
            }
        }

        return themeId;
    }

    /// <summary>
    /// Applies the theme the settings document stores, once, at startup, before the first window
    /// is shown.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ruling Q13's "a stored id wins" is a startup claim, and before this existed nothing made it
    /// true: <c>App.axaml</c> merges the default dictionary by hand and the only
    /// callers of <see cref="Apply"/> were the Display tab and the delegate <c>AppHost</c> hands
    /// it, so a stored <c>red-light</c> or <c>deep-sky</c> was shown until the next launch and no
    /// further. A user at a dark site lost Red Light on every restart (Phase 12 verification,
    /// step 6).
    /// </para>
    /// <para>
    /// The seam is here rather than in <c>App.OnFrameworkInitializationCompleted</c> because the
    /// headless harness leaves <c>ApplicationLifetime</c> null and never runs a line of that method
    /// (<c>TRACKING.md</c> section 6 item 29), so a test can execute this and only census the call
    /// site. <paramref name="stored"/> is null on the paths that have no container, which is the
    /// headless harness, and the default theme is applied there exactly as before.
    /// </para>
    /// <para>
    /// Resolves resources and the theme variant, so it runs on the UI thread only
    /// (<c>TRACKING.md</c> section 6 item 24).
    /// </para>
    /// </remarks>
    public static void ApplyStored(StartupTheme? stored) => Apply(stored?.ThemeId ?? Table[0].Id);

    /// <summary>
    /// Swaps the application to a theme: replaces the merged theme dictionary, sets the theme
    /// variant, and then re-reads the chart palette.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order is the point (ruling Q19, FIXER item 8). <c>ChartTheme.Apply()</c> reads the
    /// merged resource dictionary and installs the colours it finds as LiveCharts2's global
    /// defaults, as values rather than as <c>DynamicResource</c> lookups, so it must run
    /// <strong>after</strong> the dictionary and the variant are in place or the charts keep the
    /// previous theme's paints for the life of the process.
    /// </para>
    /// <para>
    /// Idempotent, and safe to call for the theme that is already applied: that is what makes it
    /// callable from the Display tab on every selection change without a "did it actually change"
    /// test. An id this build does not ship falls back to the first shipped theme rather than
    /// leaving the window unstyled, the same fallback shape
    /// <c>MainWindowViewModel.ResolveContentMaxWidth</c> uses.
    /// </para>
    /// <para>
    /// Resolves resources and the theme variant, so it runs on the UI thread only (Phase 9 Task 3
    /// escalation c).
    /// </para>
    /// </remarks>
    public static void Apply(string themeId)
    {
        // An id this build does not ship falls back to the first shipped theme.
        var entry = Table[0];
        foreach (var candidate in Table)
        {
            if (string.Equals(candidate.Id, themeId, StringComparison.Ordinal))
            {
                entry = candidate;
                break;
            }
        }

        if (Application.Current is { } application)
        {
            // Review minor 10: the method is documented as UI-thread only and it is a public
            // static, so the rule is enforced rather than stated. Guarded on Application.Current
            // so the no-Application unit-test path, which resolves nothing, still runs anywhere.
            Dispatcher.UIThread.VerifyAccess();

            var merged = application.Resources.MergedDictionaries;
            var source = new Uri(entry.Source);

            // The theme dictionary is the one merged entry whose source is under Theme/Themes.
            // Located rather than indexed: Scales.axaml is merged alongside it and is not
            // per-theme, and a positional assumption would silently replace the wrong one.
            var replaced = false;
            for (var index = 0; index < merged.Count; index++)
            {
                if (merged[index] is ResourceInclude include
                    && include.Source is { } existing
                    && existing.AbsolutePath.Contains("/Theme/Themes/", StringComparison.OrdinalIgnoreCase))
                {
                    if (existing != source)
                    {
                        merged[index] = new ResourceInclude((Uri?)null) { Source = source };
                    }

                    replaced = true;
                    break;
                }
            }

            if (!replaced)
            {
                merged.Add(new ResourceInclude((Uri?)null) { Source = source });
            }

            application.RequestedThemeVariant = entry.Variant;
        }

        // Last, and unconditionally: a caller that reaches here with no Application (a unit test)
        // still gets the chart palette re-read, which is what the FIXER item 8 test asserts.
        ChartTheme.Apply();
    }
}
