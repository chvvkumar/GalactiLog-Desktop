using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Theme;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Metrics;
using GalactiLog.Data.Queries;
using SkiaSharp;

namespace GalactiLog.App.ViewModels.Stats;

/// <summary>
/// One rig's row on spec 12.5's guiding scorecard, with its guide-exposure sub-row.
/// </summary>
/// <remarks>
/// Port of <c>frontend/src/components/GuidingScorecard.tsx:137-161</c>. Three columns are graded
/// against the response's cross-rig baselines and six are not: <see cref="Filtered"/> and
/// <see cref="DecRaRatio"/> because the web never grades them, <see cref="SettleSeconds"/> because
/// it is a median of medians, and the first three because they are not metrics.
/// </remarks>
/// <param name="Rig">The canonical telescope, the name the alias map folded the profile onto.
/// </param>
/// <param name="Sessions">Every session of the rig, gated ones included.</param>
/// <param name="GatedNote"><c>"n too short to score"</c>, or empty when no session of the rig is
/// under the 100-frame gate. Empty is the absent state, not a zero.</param>
/// <param name="Hours">Guided hours to one decimal. The query already rounded to two through
/// <c>RoundLikePython</c>; this prints one of them.</param>
/// <param name="Filtered">The spike-filtered RMS total, never graded: the gap to
/// <see cref="RmsTotal"/> is how much of the error is excursions.</param>
/// <param name="DecRaRatio">Dec over RA, to two decimals. The column is labelled Dec:RA and the
/// figure is Dec over RA; the web's field name reads the other way round and the label is what is
/// true.</param>
/// <param name="SettleSeconds">The median of the rig's per-session settle medians, to one decimal.
/// </param>
/// <param name="GuideExposure">The sub-row, <c>"Guide exposure: 1000, 2000 ms"</c>, or empty when
/// no session of the rig carried an exposure. Empty renders no sub-row at all.</param>
public sealed record GuidingScorecardRow(
    string Rig,
    string Sessions,
    string GatedNote,
    string Hours,
    GradedCell RmsTotal,
    GradedCell RmsRa,
    GradedCell RmsDec,
    string Filtered,
    string DecRaRatio,
    string SettleSeconds,
    string GuideExposure)
{
    /// <summary>Whether the gate clause renders beside the session count.</summary>
    public bool HasGatedNote => GatedNote.Length > 0;

    /// <summary>Whether the guide-exposure sub-row renders at all.</summary>
    public bool HasGuideExposure => GuideExposure.Length > 0;
}

/// <summary>
/// One wedge of one rig's altitude arc: its shade, the three figures it prints and the seven the
/// tooltip carries.
/// </summary>
/// <remarks>
/// Port of <c>GuidingAltitude.tsx:121-130</c> and <c>:213-261</c>. A wedge with no session in its
/// band still exists: the query returns no row for an empty (rig, band) pair and this view fills
/// the gap, because the card draws three wedges per rig whatever the data holds.
/// </remarks>
/// <param name="Rig">The canonical telescope, repeated here so the tooltip needs no second
/// binding.</param>
/// <param name="BandLabel">The band in words, as the tooltip and the screen reader name it.</param>
/// <param name="HasData">False when the band holds no session. The wedge then prints
/// <see cref="NoDataText"/> and takes the empty shade.</param>
/// <param name="RmsTotal">To two decimals, or the missing placeholder when the band's sessions are
/// all under the gate.</param>
/// <param name="RmsRa">As <see cref="RmsTotal"/>. Tooltip only.</param>
/// <param name="RmsDec">As <see cref="RmsTotal"/>. Tooltip only.</param>
/// <param name="Sessions">The band's session count.</param>
/// <param name="Ratio">The band's RMS total over the same rig's above-60 figure, as a times sign
/// and two decimals. <b>Empty</b> when the rig has no above-60 band or its figure is zero, and
/// empty means the line is not drawn at all.</param>
/// <param name="Fill">One of the rig's own three accent steps, or the empty step. Ranked inside
/// the rig and never across rigs.</param>
public sealed record AltitudeWedge(
    string Rig,
    GuidingAltitudeBand Band,
    string BandLabel,
    bool HasData,
    string RmsTotal,
    string RmsRa,
    string RmsDec,
    string Sessions,
    string Ratio,
    IImmutableSolidColorBrush Fill)
{
    /// <summary>What a wedge with no session prints in place of a figure.</summary>
    public const string NoDataText = "no data";

    /// <summary>The figure the wedge draws, or <see cref="NoDataText"/>.</summary>
    public string ValueText => HasData ? RmsTotal : NoDataText;

    /// <summary>The session count as the wedge draws it, <c>"n 12"</c>.</summary>
    public string CountText => "n " + Sessions;

    /// <summary>Whether the ratio line is drawn.</summary>
    public bool HasRatio => Ratio.Length > 0;

    /// <summary>The ratio as the tooltip prints it, where an absent ratio is the missing
    /// placeholder rather than a blank cell.</summary>
    public string RatioText => HasRatio ? Ratio : MetricText.Missing;
}

/// <summary>One rig's altitude card: its name, its best-to-worst line and its three wedges.
/// </summary>
/// <param name="Subtitle">The rig's own best to worst band figure and its total session count,
/// <c>"0.62 to 1.04 arcsec, 14 sessions"</c>, the single figure when they are equal, or
/// <c>"No RMS recorded, 14 sessions"</c> (<c>GuidingAltitude.tsx:204-211</c>).</param>
/// <param name="Wedges">Always three, in horizon-first order.</param>
public sealed record AltitudeRig(string Telescope, string Subtitle, IReadOnlyList<AltitudeWedge> Wedges);

/// <summary>One row of the table view under the arcs. Exactly the figures the wedges carry.
/// </summary>
public sealed record AltitudeTableRow(
    string Rig,
    string Band,
    string Sessions,
    string RmsTotal,
    string RmsRa,
    string RmsDec);

/// <summary>
/// Spec 12.5's Guiding section: the per-rig scorecard, the RMS-by-altitude card with its arcs and
/// its table view, and the empty notice that replaces both.
/// </summary>
/// <remarks>
/// <para>
/// Port of <c>GuidingScorecard.tsx</c> and <c>GuidingAltitude.tsx</c> over
/// <see cref="GuidingStats"/>, which rides inside the one cached statistics response. Every
/// ordering is the query's: the rigs arrive ordinally sorted and the band rows arrive in rig order
/// then horizon-first band order, and a second sort here would be a second answer to a question the
/// query answered.
/// </para>
/// <para>
/// <b>The grading is neutral until the library holds eight rigs with a figure in a column</b>, and
/// that is not a defect. <see cref="FrameQuality.MinGroup"/> is 8 and
/// <see cref="GuidingBaselines"/>'s <c>N</c> counts <em>rigs</em> with a figure, one value per rig,
/// so one rig is neutral and so are seven. It falls out of the existing gate with no threshold,
/// no rig count and no branch of this type's own (ruling G4).
/// </para>
/// </remarks>
public sealed partial class GuidingViewModel : ObservableObject
{
    /// <summary>Spec 12.5's first empty-notice form, verbatim.</summary>
    public const string NoLogsText = "No PHD2 guide logs catalogued.";

    /// <summary>The link beside <see cref="NoLogsText"/>.</summary>
    public const string EnableScanningLink = "Enable guide log scanning";

    /// <summary>The link beside the unmapped-session form.</summary>
    public const string MapProfilesLink = "Map profiles";

    /// <summary>The web's own "no rows" line inside a card that is drawn
    /// (<c>GuidingScorecard.tsx:101</c>). What the altitude card shows in place of its grid, its
    /// legend and its table view when <see cref="HasArcs"/> is false.</summary>
    public const string NoRowsText = "No guiding data available";

    /// <summary>The Rig column header's hover text, on the scorecard and the altitude table.
    /// </summary>
    public const string RigTooltip =
        "The telescope mapped to the PHD2 profile. Cameras used under one telescope share a row.";

    /// <summary>
    /// The times sign both the wedge ratio and the legend print, the web's own <c>TIMES</c>
    /// (<c>GuidingAltitude.tsx:35</c>). Spelled as its code point rather than pasted, so a later
    /// editor cannot turn it back into the ASCII letter without the change showing. This
    /// repository forbids the em dash and the en dash; the multiplication sign is neither, which
    /// is the distinction <c>MetricText.Missing</c>'s own remark records.
    /// </summary>
    private const string Times = "×";

    /// <summary>The legend under the altitude grid, which states the ranking and what the times
    /// figure is (<c>GuidingAltitude.tsx:281,296-298</c>).</summary>
    public const string RankingLegend =
        "Shade runs best band to worst band, each rig on its own scale. The " + Times
        + " figure is the ratio to that rig's above-60 band. n is the sessions behind the figure. "
        + "Hover or keyboard-focus a wedge for the RA and Dec split.";

    /// <summary>The legend's second line, the geometry (<c>GuidingAltitude.tsx:291-294</c>).
    /// </summary>
    public const string GeometryLegend =
        "Wedge angle is target altitude at capture: 0 degrees at the horizon on the right, 90 "
        + "degrees at the zenith at the top. Values are RMS total in arcseconds.";

    private readonly Action<SettingsDestination>? _openSettings;

    /// <summary>The empty shape, so the page has something to bind before its first load.
    /// </summary>
    public GuidingViewModel()
        : this(Empty, new BandBrushes(), new ArcBrushes())
    {
    }

    /// <param name="brushes">The four grading band colours, and <paramref name="arcs"/> the four
    /// wedge shades. Both are resolved from the theme by their own types on the thread that
    /// constructs them, which must be the UI thread, and are handed in here for that reason: this
    /// type is built inside the page's load callback, which a test runs inline on the loading
    /// thread, and reading a token off the UI thread throws.</param>
    internal GuidingViewModel(
        GuidingStats stats,
        BandBrushes brushes,
        ArcBrushes arcs,
        Action<SettingsDestination>? openSettings = null)
    {
        _openSettings = openSettings;
        UnmappedSessionCount = stats.UnmappedSessionCount;

        Rows =
        [
            .. stats.Rigs.Select(rig => new GuidingScorecardRow(
                rig.Telescope,
                MetricText.Count(rig.SessionCount),
                rig.GatedSessionCount > 0
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $"{MetricText.Count(rig.GatedSessionCount)} too short to score")
                    : "",
                Num(rig.GuidedHours, "0.0"),
                MetricGrading.Grade(rig.RmsTotalArcsec, stats.Baselines.RmsTotal, "RMS total", brushes),
                MetricGrading.Grade(rig.RmsRaArcsec, stats.Baselines.RmsRa, "RMS RA", brushes),
                MetricGrading.Grade(rig.RmsDecArcsec, stats.Baselines.RmsDec, "RMS Dec", brushes),
                Num(rig.RmsTotalFilteredArcsec),
                Num(rig.RaDecRatio),
                Num(rig.SettleMedianS, "0.0"),
                rig.ExposureMsValues.Count > 0
                    ? "Guide exposure: "
                      + string.Join(", ", rig.ExposureMsValues.Select(
                          value => value.ToString(CultureInfo.InvariantCulture)))
                      + " ms"
                    : "")),
        ];

        Arcs = BuildArcs(stats.AltitudeBands, arcs.Steps);
        TableRows =
        [
            .. stats.AltitudeBands.Select(row => new AltitudeTableRow(
                row.Telescope,
                BandLabel(row.Band),
                MetricText.Count(row.SessionCount),
                Num(row.RmsTotalArcsec),
                Num(row.RmsRaArcsec),
                Num(row.RmsDecArcsec))),
        ];
    }

    /// <summary>The bare shape a fresh library answers with: no unmapped session, no rig, no band
    /// and three baselines that grade nothing.</summary>
    internal static GuidingStats Empty { get; } = new(
        0,
        [],
        [],
        new GuidingBaselines(
            new MetricBaseline(null, null, 0),
            new MetricBaseline(null, null, 0),
            new MetricBaseline(null, null, 0)));

    /// <summary>One row per rig, in the query's order.</summary>
    public IReadOnlyList<GuidingScorecardRow> Rows { get; }

    /// <summary>One arc per rig, in the query's order, each with exactly three wedges.</summary>
    public IReadOnlyList<AltitudeRig> Arcs { get; }

    /// <summary>The table view's rows: rig order then horizon-first band order, the query's own.
    /// </summary>
    public IReadOnlyList<AltitudeTableRow> TableRows { get; }

    /// <summary>Sessions whose PHD2 profile is mapped to no telescope, across the whole library.
    /// </summary>
    public int UnmappedSessionCount { get; }

    /// <summary>Whether the table view under the arcs is open. Session state, closed at every
    /// load: the web carries it on a <c>details</c> element and persists nothing either.</summary>
    [ObservableProperty]
    public partial bool IsTableExpanded { get; set; }

    /// <summary>Opens and closes the table view.</summary>
    [RelayCommand]
    private void ToggleTable() => IsTableExpanded = !IsTableExpanded;

    /// <summary>
    /// Whether the empty notice replaces both cards. Read from <see cref="GuidingStats.Rigs"/>
    /// alone and never from a guide-log EXISTS predicate: this page counts sessions and the Target
    /// detail band counts logs, so a library holding one empty log says "No PHD2 guide logs
    /// catalogued" here and "No PHD2 guide logs for this night" there, and both sentences are true
    /// of what their own page counts.
    /// </summary>
    public bool IsEmpty => Rows.Count == 0;

    /// <summary>Both cards render only when at least one rig has a row.</summary>
    public bool ShowCards => !IsEmpty;

    /// <summary>
    /// Whether the altitude card has an arc to draw. False on a library whose guiding sessions all
    /// carry a null <c>alt_deg</c>, which is the ASIAIR shape: those sessions are on the scorecard
    /// and in no band row, so <see cref="ShowCards"/> is true while this is false.
    /// </summary>
    /// <remarks>
    /// The web guards the same block on the same condition
    /// (<c>GuidingAltitude.tsx:195-198</c>, <c>rigs().length &gt; 0</c> with a "no data" fallback).
    /// Without it the card draws its heading, an empty wrapping grid, two legend paragraphs
    /// explaining wedges that are not there, and a disclosure onto an empty table.
    /// </remarks>
    public bool HasArcs => Arcs.Count > 0;

    /// <summary>
    /// Which of spec 12.5's two empty-notice forms applies. The unmapped form wins when both are
    /// true, because a library with logs and no mapping is one click from working and should not be
    /// told to enable something it already enabled.
    /// </summary>
    public bool HasUnmappedSessions => UnmappedSessionCount > 0;

    /// <summary>The empty notice's sentence, in whichever of the two forms applies.</summary>
    public string EmptyNoticeText => HasUnmappedSessions
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"{MetricText.Count(UnmappedSessionCount)} guiding sessions found but no PHD2 profile is mapped to a telescope.")
        : NoLogsText;

    /// <summary>The empty notice's link text.</summary>
    public string EmptyNoticeLink => HasUnmappedSessions ? MapProfilesLink : EnableScanningLink;

    /// <summary>Where that link goes.</summary>
    public SettingsDestination EmptyNoticeDestination => HasUnmappedSessions
        ? SettingsDestination.EquipmentPhd2Profiles
        : SettingsDestination.LibraryGuideLogSwitch;

    /// <summary>Raises the page's routing event for <see cref="EmptyNoticeDestination"/>. Inert
    /// when the page was built with no route, which is every unit test that does not assert the
    /// routing itself.</summary>
    [RelayCommand]
    private void OpenSettings() => _openSettings?.Invoke(EmptyNoticeDestination);

    /// <summary>The band in words, the App layer's text: nothing serialises these and the enum is
    /// the stored ordering.</summary>
    internal static string BandLabel(GuidingAltitudeBand band) => band switch
    {
        GuidingAltitudeBand.Below30 => "Below 30 degrees",
        GuidingAltitudeBand.From30To60 => "30 to 60 degrees",
        _ => "Above 60 degrees",
    };

    // fmtNum (GuidingScorecard.tsx:29-32): a fixed number of decimals, or the missing placeholder.
    // The placeholder is MetricText.Missing, the same hyphen the rest of this page prints, and not
    // the web's em dash, which this repository forbids outright.
    private static string Num(double? value, string format = "0.00")
        => value is { } present && double.IsFinite(present)
            ? present.ToString(format, CultureInfo.InvariantCulture)
            : MetricText.Missing;

    // buildRigs (GuidingAltitude.tsx:108-140). The rows arrive grouped and ordered by the query;
    // this walks them once, keeping first-seen rig order, and fills the (rig, band) pairs the query
    // returned no row for.
    private static IReadOnlyList<AltitudeRig> BuildArcs(
        IReadOnlyList<GuidingAltitudeBandRow> rows,
        IReadOnlyList<IImmutableSolidColorBrush> ramp)
    {
        var order = new List<string>();
        var byRig = new Dictionary<string, Dictionary<GuidingAltitudeBand, GuidingAltitudeBandRow>>(
            StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!byRig.TryGetValue(row.Telescope, out var bands))
            {
                byRig[row.Telescope] = bands = [];
                order.Add(row.Telescope);
            }

            bands[row.Band] = row;
        }

        var arcs = new List<AltitudeRig>(order.Count);
        foreach (var telescope in order)
        {
            arcs.Add(BuildArc(telescope, byRig[telescope], ramp));
        }

        return arcs;
    }

    private static AltitudeRig BuildArc(
        string telescope,
        Dictionary<GuidingAltitudeBand, GuidingAltitudeBandRow> bands,
        IReadOnlyList<IImmutableSolidColorBrush> ramp)
    {
        var present = Bands
            .Select(band => bands.TryGetValue(band, out var row) ? row.RmsTotalArcsec : null)
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .OrderBy(value => value)
            .ToList();

        // The ratio's denominator. Absent when the rig has no above-60 band, when that band's
        // figure is null, and when it is exactly zero: the web reads `base` as a truthiness test
        // and zero is false there as well as null.
        double? above60 = bands.TryGetValue(GuidingAltitudeBand.Above60, out var top)
            && top.RmsTotalArcsec is { } value
            && value != 0
                ? value
                : null;

        var wedges = new List<AltitudeWedge>(Bands.Length);
        foreach (var band in Bands)
        {
            var row = bands.GetValueOrDefault(band);
            var total = row?.RmsTotalArcsec;
            wedges.Add(new AltitudeWedge(
                telescope,
                band,
                BandLabel(band),
                row is not null,
                Num(total),
                Num(row?.RmsRaArcsec),
                Num(row?.RmsDecArcsec),
                MetricText.Count(row?.SessionCount ?? 0),
                total is { } figure && above60 is { } reference
                    ? Times + (figure / reference).ToString("0.00", CultureInfo.InvariantCulture)
                    : "",
                ramp[Step(present, total)]));
        }

        var sessions = MetricText.Count(bands.Values.Sum(row => row.SessionCount));
        var figures = present.Count == 0
            ? "No RMS recorded"
            : present[0] == present[^1]
                ? Num(present[0]) + " arcsec"
                : Num(present[0]) + " to " + Num(present[^1]) + " arcsec";

        return new AltitudeRig(telescope, $"{figures}, {sessions} sessions", wedges);
    }

    // The rank-inside-one-rig step (GuidingAltitude.tsx:126-128). Index 0 is the empty shade;
    // 1, 2 and 3 are the accent's three strengths. A rig with one band figure takes the middle
    // step rather than a false extreme, which is what `sorted.length < 2 ? 2` says.
    private static int Step(List<double> sorted, double? total)
    {
        if (total is not { } value)
        {
            return 0;
        }

        var index = sorted.IndexOf(value);
        if (index < 0)
        {
            return 0;
        }

        return sorted.Count < 2
            ? 2
            : 1 + (int)Math.Round(index * 2d / (sorted.Count - 1), MidpointRounding.AwayFromZero);
    }

    /// <summary>The three bands in horizon-first order, which is the enum's own declaration order
    /// and the order the wedges draw in.</summary>
    private static readonly GuidingAltitudeBand[] Bands =
    [
        GuidingAltitudeBand.Below30,
        GuidingAltitudeBand.From30To60,
        GuidingAltitudeBand.Above60,
    ];
}

/// <summary>
/// The altitude arc's four wedge shades, resolved once from the theme rather than per wedge.
/// </summary>
/// <remarks>
/// <para>
/// Spec 12.5: the accent at three strengths over the card, with the empty step the tertiary ink at
/// the lightest. Built the way <c>ImagingCalendarViewModel</c> builds the heatmap's ramp, by mixing
/// two bound tokens read through <c>ChartTheme</c>, so <b>no theme token is added</b> (spec 14.1
/// was deliberately not amended) and no colour is named in this application's source.
/// </para>
/// <para>
/// Shaped as a type beside <see cref="BandBrushes"/> and not as a helper, and for its reason:
/// resolving a token reads <c>Application.Current</c>'s merged dictionary, which must happen on the
/// UI thread, so the resolution belongs at a construction the page performs on that thread rather
/// than inside the load callback, which a test runs inline on the loading thread.
/// </para>
/// </remarks>
internal sealed class ArcBrushes
{
    /// <summary>The three tokens this type mixes, named so a test can assert against the
    /// dictionary rather than against a hard-coded hex string.</summary>
    internal static readonly string[] TokenKeys = ["ColorBgElevated", "ColorAccent", "ColorTextTertiary"];

    /// <summary>The web's three accent strengths (<c>GuidingAltitude.tsx:43-47</c>), and the same
    /// lightest strength for the empty step.</summary>
    private static readonly double[] Strengths = [0.22d, 0.42d, 0.62d];

    public ArcBrushes()
    {
        var card = ChartTheme.Read(TokenKeys[0], ChartTheme.Fallback);
        var accent = ChartTheme.Read(TokenKeys[1], ChartTheme.Fallback);
        var tertiary = ChartTheme.Read(TokenKeys[2], ChartTheme.Fallback);

        Steps =
        [
            Mix(card, tertiary, Strengths[0]),
            Mix(card, accent, Strengths[0]),
            Mix(card, accent, Strengths[1]),
            Mix(card, accent, Strengths[2]),
        ];
    }

    /// <summary>Index 0 is the empty step; 1, 2 and 3 are the accent's three strengths, lightest
    /// for a rig's best band and darkest for its worst.</summary>
    public IReadOnlyList<IImmutableSolidColorBrush> Steps { get; }

    private static IImmutableSolidColorBrush Mix(SKColor low, SKColor high, double amount)
        => new ImmutableSolidColorBrush(Color.FromArgb(
            Channel(low.Alpha, high.Alpha, amount),
            Channel(low.Red, high.Red, amount),
            Channel(low.Green, high.Green, amount),
            Channel(low.Blue, high.Blue, amount)));

    private static byte Channel(byte low, byte high, double amount)
        => (byte)Math.Round(low + ((high - low) * amount), MidpointRounding.AwayFromZero);
}
