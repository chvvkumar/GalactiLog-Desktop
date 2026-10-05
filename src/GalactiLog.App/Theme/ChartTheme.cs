using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.Themes;
using SkiaSharp;

namespace GalactiLog.App.Theme;

/// <summary>
/// Spec 13's global LiveCharts2 configuration. Reads the theme tokens out of the merged resource
/// dictionary and installs them as the library's global defaults, so no chart control and no chart
/// view-model spells a colour.
/// <para>
/// Invoked from <c>App.OnFrameworkInitializationCompleted</c> rather than from
/// <c>AppHost.Build</c>, which spec 13 names: the host is built before Avalonia starts, so
/// <c>Application.Current</c> does not exist yet during Build, and Build is also the CLI's entry
/// point. Ruling Q2.
/// </para>
/// <para>
/// Spec 13's "chart background transparent so the page gradient shows through" is not part of this
/// configuration at all: the Avalonia <c>CartesianChart</c> control inherits Avalonia's
/// <c>Background</c> property, so it is <c>Background="Transparent"</c> on each control in Task 8's
/// XAML. Recorded here so the requirement is not lost between the two tasks.
/// </para>
/// </summary>
public static class ChartTheme
{
    /// <summary>Spec 13: "Animations 200 ms, matching the interface's transition duration"
    /// (spec 14.4's page-enter and expander tiers).</summary>
    public static readonly TimeSpan AnimationsSpeed = TimeSpan.FromMilliseconds(200);

    /// <summary>Spec 14.1's ten <c>metric-*</c> tokens, in the order the token table lists them.
    /// This is the source of the series palette: spec 14.5 says "Metric colours are a data-series
    /// palette, one stable hue per metric in every theme. Never reassign them per chart", so the
    /// order here is fixed, and it is also what seeds the library's own fallback colour cycle in
    /// <see cref="Apply"/>.</summary>
    public static readonly ImmutableArray<string> MetricTokenOrder =
    [
        "ColorMetricIntegration", "ColorMetricFrames", "ColorMetricHfr", "ColorMetricEccentricity",
        "ColorMetricFwhm", "ColorMetricStars", "ColorMetricGuiding", "ColorMetricTemp",
        "ColorMetricGain", "ColorMetricTime",
    ];

    // A missing resource key falls back to this rather than throwing: a chart that renders grey is
    // better than a window that will not open. It is spec 5.8.4's neutral grey, the same value
    // FilterColor.Fallback carries, and it is the only literal colour in this file. Every Read
    // call below names the token it stands in for.
    private static readonly SKColor FallbackNeutral = new(0x80, 0x80, 0x80, 0xFF);

    // The axis rule is appended to the library's default theme rather than replacing it. rc5.4's
    // LiveChartsSettings.HasTheme constructs a brand new Theme, which drops the default theme's
    // Colors array and every default series builder, so a replaced theme renders unpainted series.
    // Theme.AxisBuilder is a List the HasRuleForAxes extension appends to, and the append survives
    // a later UseDefaults call, so it must happen exactly once however many times Apply runs. The
    // rule closure reads the current Palette rather than a captured one, which is what makes spec
    // 13's "re-read on theme change" work for every axis built after the swap.
    private static bool _axisRuleInstalled;

    /// <summary>The token colours the charts need, resolved once per <see cref="Apply"/> so a
    /// chart view-model never performs a resource lookup of its own. Every entry is read from
    /// <c>Application.Current</c> through <c>TryFindResource</c>, and a missing key falls back to
    /// a documented neutral rather than throwing.
    /// <para>
    /// Remark: this initializer runs when the type is
    /// first touched, on whatever thread touches it, and it reads
    /// <c>Application.Current.Resources</c>. In production that is harmless because
    /// <see cref="Apply"/> runs from <c>App.OnFrameworkInitializationCompleted</c>, on the UI
    /// thread, before any chart or view-model exists, and it overwrites this value with a palette
    /// read there. It matters for a test or a future caller that touches
    /// <c>ChartTheme.Palette</c> from a background thread before <see cref="Apply"/> has run: the
    /// read then happens off the UI thread, and with no application at all every entry falls back
    /// to the neutral grey. Apply must run first, on the UI thread.
    /// </para>
    /// </summary>
    public static ChartPalette Palette { get; private set; } = ReadPalette();

    /// <summary>
    /// The four resource keys <see cref="Apply"/> writes the tooltip and legend paints to, and
    /// which <c>Theme/Controls.axaml</c>'s chart style binds the four styled properties to through
    /// a <c>DynamicResource</c>. Spec 13's "the four tooltip and legend paints are assigned on
    /// every chart control".
    /// <para>
    /// They are written into <c>Application.Current.Resources</c> at run time rather than declared
    /// in the three theme dictionaries, for the reason TRACKING section 3 item 4 records from the
    /// other side: a <c>ResourceDictionary</c> entry is a constructed object and cannot carry a
    /// <c>DynamicResource</c>, and a <c>Paint</c> has no XAML form to declare. Writing them here
    /// also keeps the three dictionaries' key sets identical, which <c>ThemeResourceTest</c>
    /// asserts.
    /// </para>
    /// </summary>
    public const string TooltipBackgroundPaintKey = "ChartTooltipBackgroundPaint";

    /// <inheritdoc cref="TooltipBackgroundPaintKey"/>
    public const string TooltipTextPaintKey = "ChartTooltipTextPaint";

    /// <inheritdoc cref="TooltipBackgroundPaintKey"/>
    public const string LegendBackgroundPaintKey = "ChartLegendBackgroundPaint";

    /// <inheritdoc cref="TooltipBackgroundPaintKey"/>
    public const string LegendTextPaintKey = "ChartLegendTextPaint";

    /// <summary>Spec 13's "re-read on theme change". Raised at the end of every
    /// <see cref="Apply"/> so live charts re-paint the elements they already built. Phase 9's
    /// theme picker calls <see cref="Apply"/> after swapping the dictionary; see
    /// <see cref="ThemeManager"/>.</summary>
    public static event EventHandler? Changed;

    /// <summary>
    /// Subscribes <paramref name="rebuild"/> to <see cref="Changed"/> and returns the unsubscribe
    /// as an <see cref="IDisposable"/> the caller holds and disposes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Seven types re-paint on a theme change, and every one of them wrote the same
    /// subscribe in its constructor and the matching detach in its <c>Dispose</c>. The subscribe is
    /// trivial; the detach is a correctness rule, because <see cref="Changed"/> is a static event
    /// and a handler left behind pins its whole view-model for the life of the process and keeps
    /// rebuilding charts nobody can see. One member owns both halves now, so a type that holds the
    /// returned token in a readonly field cannot forget the half that matters.
    /// </para>
    /// <para>
    /// A disposable rather than a base class: the seven differ in what
    /// they rebuild and three already own their own lifetime, so there is no shared state for a
    /// base class to hold. Disposing twice is safe, and the token holds no reference to anything
    /// but the delegate.
    /// </para>
    /// </remarks>
    public static IDisposable Subscribe(Action rebuild)
    {
        ArgumentNullException.ThrowIfNull(rebuild);
        return new Subscription(rebuild);
    }

    private sealed class Subscription : IDisposable
    {
        private Action? _rebuild;

        internal Subscription(Action rebuild)
        {
            _rebuild = rebuild;
            Changed += OnChanged;
        }

        public void Dispose()
        {
            if (_rebuild is null)
            {
                return;
            }

            Changed -= OnChanged;
            _rebuild = null;
        }

        private void OnChanged(object? sender, EventArgs e) => _rebuild?.Invoke();
    }

    /// <summary>
    /// Installs the configuration. Safe to call again: a theme change calls it a second time and
    /// the second call replaces the first's paints. Idempotent with respect to the library's own
    /// one-time setup (backend, mappers and the appended axis rule).
    /// <para>
    /// Runs on the UI thread wherever an application exists. It writes the four chart paint keys
    /// into <c>Application.Current.Resources</c>, which raises <c>ResourcesChanged</c> into the
    /// visual tree and re-evaluates every <c>DynamicResource</c> bound to them. Before Phase 17
    /// this method degraded off the UI thread rather than throwing, because every lookup went
    /// through <see cref="Read"/>'s guard; the resource write has no such fallback. Every shipped
    /// caller is already on the UI thread: <see cref="ThemeManager.Apply"/> verifies access on the
    /// same <c>Application.Current</c> branch immediately before calling this, and the startup path
    /// reaches it from <c>App.OnFrameworkInitializationCompleted</c>. Task 4a review finding 4.
    /// </para>
    /// </summary>
    public static void Apply()
    {
        var palette = ReadPalette();
        Palette = palette;

        LiveCharts.Configure(config =>
        {
            config
                // UseDefaults registers the SkiaSharp backend and the library's default theme,
                // which is what supplies the default series builders. AddSkiaSharp on its own
                // leaves no theme at all. Both calls are no-ops after the first: verified against
                // rc5.4, where a second UseDefaults leaves the Theme instance and every builder
                // count unchanged.
                .UseDefaults()
                .AddDefaultMappers()
                .WithAnimationsSpeed(AnimationsSpeed)
                .WithTooltipBackgroundPaint(new SolidColorPaint(palette.TooltipBackground))
                .WithTooltipTextPaint(new SolidColorPaint(palette.TooltipText))
                // Spec 13 names only the legend's text colour. The library's default legend
                // background is a light paint, which would put a white box on a dark chart, so it
                // is set from the same token as the tooltip's. Deviation recorded in the report.
                .WithLegendBackgroundPaint(new SolidColorPaint(palette.TooltipBackground))
                .WithLegendTextPaint(new SolidColorPaint(palette.LegendText));

            // Spec 14.5: even the library's own fallback colour cycle comes from tokens, so a
            // series left unpainted still cannot show a colour that is not in the theme.
            config.GetTheme().Colors =
                [.. MetricTokenOrder.Select(key => palette.Metrics[key].AsLvcColor())];

            if (!_axisRuleInstalled)
            {
                config.GetTheme().HasRuleForAxes(axis =>
                {
                    axis.LabelsPaint = new SolidColorPaint(Palette.AxisLabels);
                    axis.SeparatorsPaint = new SolidColorPaint(Palette.Separators);

                    // An axis TITLE, which this rule left to the library's own axis rule until
                    // Phase 17. That rule paints every title in #FF232323 whatever theme is
                    // applied, measured off the builder list at rc5.4, which is a near-black
                    // title on all three of the shipped dark pages and one that no theme swap
                    // moves. Titles ship on eight axes (the Analysis page's seven and the Target
                    // detail session chart's "Frame"), so the correction belongs at this one rule
                    // and never at a chart (design lesson 2): a chart a later phase adds is
                    // covered without remembering anything.
                    //
                    // The same ink as the tick labels, and not a token of its own: a title and its
                    // ticks are one axis' furniture and spec 14.1 gives the axis one text token.
                    // ChartPalette carries no other axis colour, and reaching for TooltipText
                    // would tie an axis to the tooltip's token, which a later phase may move.
                    axis.NamePaint = new SolidColorPaint(Palette.AxisLabels);
                });
                _axisRuleInstalled = true;
            }
        });

        // Spec 13's "the four tooltip and legend paints are assigned on every chart control, not
        // left to the library default, and this corrects a shipped defect". The four settings
        // written above are the library's DEFAULTS, and the Avalonia chart controls resolve those
        // four styled-property defaults once per process, from whatever the settings held when the
        // first chart control in the process was built. Everything above therefore tracked the
        // theme correctly and no control ever re-read it: after a swap, a live chart and a chart
        // built afterwards alike kept the first theme's tooltip and legend ink.
        //
        // The correction is one choke point rather than a call at each control (design lesson 2):
        // Theme/Controls.axaml carries one style block whose three selectors set the four
        // properties from these four keys through a DynamicResource, so a Setter beats the stale
        // default and the DynamicResource re-evaluates on the write below. Every chart control in
        // the process is covered by construction, including the six the Statistics page's
        // BarChartBodyTemplate builds from one markup element, and a chart a later phase adds is
        // covered without remembering anything. ChartPaintCensusTests holds both halves.
        //
        // The keys are written here rather than declared in the three theme dictionaries because a
        // Paint is a constructed object with no XAML form, which is the same constraint TRACKING
        // section 3 item 4 records for the ScrollBar aliases from the other side. Writing them
        // keeps the three dictionaries' key sets identical.
        //
        // One paint instance per key, shared by every chart control in the process, exactly as the
        // library's own styled-property defaults above already shared one instance per key with
        // every control. Shape A cannot do otherwise, because one resource value is one object, so
        // task4a.md section 2.1's "a fresh SolidColorPaint per assignment" is a shape B rule that
        // does not carry over and the departure is recorded in the task report. What is fresh here
        // is per CALL, which is what makes a theme swap replace the paints rather than mutate them;
        // it was never isolation between controls. Task 4a review finding 3.
        //
        // Writing into the application's resources raises ResourcesChanged into the visual tree and
        // re-evaluates every DynamicResource, so this branch runs on the UI thread. Every shipped
        // caller already does: ThemeManager.Apply verifies UI-thread access on the same
        // Application.Current branch immediately before calling this. Review finding 4.
        if (Application.Current is { } current)
        {
            current.Resources[TooltipBackgroundPaintKey] = new SolidColorPaint(palette.TooltipBackground);
            current.Resources[TooltipTextPaintKey] = new SolidColorPaint(palette.TooltipText);
            current.Resources[LegendBackgroundPaintKey] = new SolidColorPaint(palette.TooltipBackground);
            current.Resources[LegendTextPaintKey] = new SolidColorPaint(palette.LegendText);
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// The one Avalonia-to-SkiaSharp colour conversion in the application. Task 8 tints a
    /// per-filter series split with <c>AliasMap.FilterColor</c> through
    /// <c>TargetRowViewModel.ParseTint</c>, which yields an Avalonia brush; this turns its colour
    /// into what LiveCharts2 paints with. Alpha is carried through: the surface tokens are
    /// translucent and dropping it renders opaque panels.
    /// </summary>
    internal static SKColor ToSkColor(Color color) => new(color.R, color.G, color.B, color.A);

    // Spec 14 says "a parallel Color resource with the suffix Color is provided for the few places
    // Avalonia needs a Color rather than a IBrush (gradient stops, chart paints)". The shipped
    // DeepSky.axaml does not follow that convention: it declares four semantic colours and the
    // two gradient stops with a Value suffix and every other token as a SolidColorBrush only.
    // Reading the brush and taking its Color is one method that works for all 37 tokens and keeps
    // the dictionary at exactly the key set ThemeResourceTest counts. Ruling Q3.
    //
    // Phase 9 Task 3 review finding I1, and the coordinator's structural escalation on it
    // (design-lessons rule 2): the brush-only branch returned the documented grey for every key
    // that is a Color rather than a brush, which is indistinguishable at the call site from a key
    // that is missing. The four *Value keys above are exactly that shape, and a caller that named
    // one got a silently grey palette. A Color resource is now accepted and wrapped in an
    // ImmutableSolidColorBrush, so the two declaration forms in the dictionary read identically
    // and Tasks 6 to 9 cannot walk into the same silence.
    internal static SKColor Read(string key, SKColor fallback)
    {
        if (Application.Current is not { } app)
        {
            return fallback;
        }

        // Phase 15B fixer item 39. Reading a token off the UI thread throws where this member's
        // own summary promises a documented neutral, and it can throw at EITHER of two frames, so
        // the guard covers the whole lookup rather than one of them:
        //
        //   1. inside TryFindResource, which materialises the dictionary's deferred content the
        //      first time a key is asked for. Building a mutable SolidColorBrush runs
        //      AvaloniaObject's constructor, and that verifies thread access. Whether this bites
        //      depends on which caller asks for the key first in the process, which is why it
        //      presented as an intermittent failure of an unrelated case rather than as a defect
        //      of its own;
        //   2. on brush.Color, for a token already materialised as a mutable SolidColorBrush
        //      rather than an ImmutableSolidColorBrush. Its getter verifies access too.
        //
        // One member, one guard: every present and future caller routes through here, and
        // BandBrushes and ArcBrushes are built wherever their owner happens to be constructed. A
        // chart that renders in the documented neutral is better than a window that will not open.
        try
        {
            return app.TryFindResource(key, out var value) && AsBrush(value) is { } brush
                ? ToSkColor(brush.Color)
                : fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    /// <summary>
    /// A theme resource as a solid colour brush, whichever of the dictionary's two declaration
    /// forms it uses. Returns null for a resource that is neither, which is what
    /// <see cref="Read"/> turns into its documented fallback.
    /// </summary>
    internal static ISolidColorBrush? AsBrush(object? value) => value switch
    {
        ISolidColorBrush brush => brush,
        Color color => new ImmutableSolidColorBrush(color),
        _ => null,
    };

    private static ChartPalette ReadPalette() => new(
        AxisLabels: Read("ColorTextSecondary", FallbackNeutral),
        Separators: Read("ColorBorderDefault", FallbackNeutral),
        LegendText: Read("ColorTextSecondary", FallbackNeutral),
        TooltipBackground: Read("ColorBgElevated", FallbackNeutral),
        TooltipText: Read("ColorTextPrimary", FallbackNeutral),
        Metrics: MetricTokenOrder.ToDictionary(key => key, key => Read(key, FallbackNeutral)))
    {
        Outlier = Read("ColorMetricWorst", FallbackNeutral),
    };

    /// <summary>The documented stand-in for a token that is missing from the dictionary, exposed
    /// so a test can assert the fallback path rather than guess the value.</summary>
    internal static SKColor Fallback => FallbackNeutral;
}

/// <summary>The resolved chart colours, as SkiaSharp colours because that is what LiveCharts2
/// paints with. <c>Metrics</c> is keyed by the spec 14.1 resource key (<c>ColorMetricHfr</c> and
/// the rest), which is what <c>ChartMetric.TokenKey</c> holds.</summary>
public sealed record ChartPalette(
    SKColor AxisLabels,
    SKColor Separators,
    SKColor LegendText,
    SKColor TooltipBackground,
    SKColor TooltipText,
    IReadOnlyDictionary<string, SKColor> Metrics)
{
    /// <summary>The outlier ink the night timeline uses (<c>ColorMetricWorst</c>), for the per-frame
    /// chart's outlier ring. Not a series colour, so not in <see cref="Metrics"/>.</summary>
    public SKColor Outlier { get; init; } = ChartTheme.Fallback;
}
