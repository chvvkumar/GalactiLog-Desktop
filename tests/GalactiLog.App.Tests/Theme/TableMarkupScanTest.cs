using System.Reflection;
using System.Text.RegularExpressions;
using GalactiLog.App.Controls.Table;
using GalactiLog.App.Tests.TestSupport;
using Xunit;

namespace GalactiLog.App.Tests.Theme;

// The table spine's static scan (spine-spec 6.3): a table is built on t:TableRow, its column set
// declared once, its gutters, alignment and headings from the spine, never by hand per row. The
// two lists below only shrink: each migration removes its own entries in the same commit, and a
// stale entry fails here.
public class TableMarkupScanTest
{
    private const string SpineNamespace = "using:GalactiLog.App.Controls.Table";

    // Files that may still declare a SharedSizeGroup. Paths are relative to src/GalactiLog.App.
    private static readonly string[] SharedSizeGroupAllowlist =
    [
        // Tables, removed by their migration.
        "Views/TargetDetail/Parts/PerFilterTablePart.axaml",
        "Views/TargetDetail/Parts/RangesTablePart.axaml",
        // Form and bar layouts, not tables (ruling R3): these stay.
        "Views/Mosaics/MosaicDetailView.axaml",
        "Views/TargetDetail/CreateMosaicWindow.axaml",
        "Views/TargetDetail/Parts/IntegrationBarsPart.axaml",
    ];

    // Every table not yet on the spine (spine-spec 7.2, teams A to G). A migration that adds the
    // spine namespace to one of these removes it here. The ActivityView log grid and the
    // LibraryTabView grid are out of scope for this overhaul (ruling R3) and later candidates;
    // they are not listed because nothing here is waiting on them.
    private static readonly string[] PendingTables =
    [
        "Views/TargetDetail/Parts/PerFilterTablePart.axaml",
        "Views/TargetDetail/Parts/RangesTablePart.axaml",
        "Views/StatisticsView.axaml",
        "Views/Settings/CustomColumnsTabView.axaml",
        "Views/TargetDetail/FrameTableView.axaml",
    ];

    private static readonly Regex XmlComment = new("<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TableRowElement = new(@"<t:TableRow\b(?<open>[^>]*)>(?<body>.*?)</t:TableRow>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex LiteralText = new(@"\bText\s*=\s*""(?<text>[^""{][^""]*)""", RegexOptions.Compiled);
    private static readonly Regex Shouting = new(@"\b[A-Z]{3,}\b", RegexOptions.Compiled);
    private static readonly string[] AllowedCaps = ["HFR", "FWHM", "RMS"];

    private static readonly (Regex Rule, string Name)[] RowNeedles =
    [
        (new(@"<Grid\.ColumnDefinitions>"), "Grid.ColumnDefinitions"),
        (new(@"\bGrid\.Column\s*="), "Grid.Column"),
        (new(@"Classes\s*=\s*""(?:[^""]*\s)?num(?:\s[^""]*)?"""), "Classes=\"num\""),
        (new(@"Padding\s*=\s*""0,0,6,0"""), "Padding=\"0,0,6,0\""),
        (new(@"Margin\s*=\s*""2,0"""), "Margin=\"2,0\""),
        (new(@"Padding\s*=\s*""8,0"""), "Padding=\"8,0\""),
    ];

    private static string AppRoot() => Path.Combine(SourceScan.SrcRoot(), "GalactiLog.App");

    private static string Relative(string file) => Path.GetRelativePath(AppRoot(), file).Replace('\\', '/');

    // The spine's own files, where these rules are implemented rather than broken.
    private static bool IsSpine(string relative)
        => relative.StartsWith("Controls/Table/", StringComparison.Ordinal) || relative == "Theme/Table.axaml";

    private static IEnumerable<(string Relative, string Markup)> Markup()
        => SourceScan.EnumerateMarkupFiles("GalactiLog.App")
            .Select(file => (Relative(file), XmlComment.Replace(File.ReadAllText(file), "")))
            .Where(file => !IsSpine(file.Item1));

    internal static IEnumerable<string> RowOffences(string markup)
        => from row in TableRowElement.Matches(markup)
           from needle in RowNeedles
           where needle.Rule.IsMatch(row.Groups["body"].Value)
           select needle.Name;

    internal static IEnumerable<string> HeadingOffences(string heading)
    {
        if (heading.Contains("arcsec", StringComparison.OrdinalIgnoreCase))
        {
            yield return $"'{heading}': arcsec (the unit is \")";
        }

        if (heading.Contains("(px)", StringComparison.Ordinal))
        {
            yield return $"'{heading}': (px) (the unit is px)";
        }

        foreach (Match word in Shouting.Matches(heading))
        {
            if (!AllowedCaps.Contains(word.Value))
            {
                yield return $"'{heading}': {word.Value} (headings are sentence case)";
            }
        }
    }

    internal static IEnumerable<string> HeaderOffences(string markup)
        => from row in TableRowElement.Matches(markup)
           where Regex.IsMatch(row.Groups["open"].Value, @"\bKind\s*=\s*""Header""")
           from text in LiteralText.Matches(row.Groups["body"].Value)
           from offence in HeadingOffences(text.Groups["text"].Value)
           select offence;

    [Fact]
    public void SharedSizeGroup_AppearsOnlyInTheSpineAndTheAllowlist()
    {
        var files = Markup().ToList();
        Assert.NotEmpty(files);

        var offenders = files
            .Where(file => file.Markup.Contains("SharedSizeGroup", StringComparison.Ordinal))
            .Select(file => file.Relative)
            .Except(SharedSizeGroupAllowlist)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "A table declares its columns once in a t:TableColumns, not in a SharedSizeGroup per row. Offenders:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void TheLists_OnlyShrink()
    {
        var files = Markup().ToDictionary(file => file.Relative, file => file.Markup);
        var stale = new List<string>();

        foreach (var entry in SharedSizeGroupAllowlist)
        {
            if (!files.TryGetValue(entry, out var markup) || !markup.Contains("SharedSizeGroup", StringComparison.Ordinal))
            {
                stale.Add($"{entry}: no SharedSizeGroup left; remove it from the allowlist");
            }
        }

        foreach (var entry in PendingTables)
        {
            if (!files.TryGetValue(entry, out var markup) || markup.Contains(SpineNamespace, StringComparison.Ordinal))
            {
                stale.Add($"{entry}: on the spine now (or gone); remove it from the pending tables");
            }
        }

        Assert.True(stale.Count == 0, string.Join(Environment.NewLine, stale));
    }

    [Fact]
    public void NoTableRow_PlacesOrPadsItsCellsByHand()
    {
        var offenders = Markup()
            .SelectMany(file => RowOffences(file.Markup).Select(offence => $"{file.Relative}: {offence}"))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "A TableRow's columns and gutters come from its TableColumns and Theme/Table.axaml. Offenders:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void NoHeading_SpellsAUnitTheOldWayOrShouts()
    {
        var shared = typeof(TableHeads)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && !field.Name.EndsWith("Tip", StringComparison.Ordinal))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();
        Assert.Contains(TableHeads.Fwhm, shared);

        var offenders = shared.SelectMany(HeadingOffences).Select(offence => $"TableHeads {offence}")
            .Concat(Markup().SelectMany(file => HeaderOffences(file.Markup).Select(offence => $"{file.Relative}: {offence}")))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Headings are sentence case, plain words, units as px and \" (spec.md items 4 and 5). Offenders:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    // The scans above run over a tree with no t:TableRow yet, so they pass as happily with a rule
    // that never fires. Each rule is run over markup that offends it and over markup that does not.
    [Fact]
    public void EveryRule_FiresOnItsOffenderAndIsSilentOnASpineRow()
    {
        const string clean = """
            <t:TableRow Kind="Header" Columns="{StaticResource Cols}">
              <TextBlock t:TableRow.Col="label" Text="Filter" />
              <TextBlock t:TableRow.Col="hfr" Text="{x:Static t:TableHeads.Hfr}" />
              <TextBlock t:TableRow.Col="fwhm" Text="FWHM &quot;" Classes="tc-num-like" />
            </t:TableRow>
            """;
        Assert.Empty(RowOffences(clean));
        Assert.Empty(HeaderOffences(clean));

        string[] cells =
        [
            "<Grid.ColumnDefinitions><ColumnDefinition /></Grid.ColumnDefinitions>",
            "<TextBlock Grid.Column=\"2\" />",
            "<TextBlock Classes=\"muted num\" />",
            "<TextBlock Padding=\"0,0,6,0\" />",
            "<TextBlock Margin=\"2,0\" />",
            "<TextBlock Padding=\"8,0\" />",
        ];
        Assert.Equal(RowNeedles.Length, cells.Length);
        for (var i = 0; i < cells.Length; i++)
        {
            Assert.Equal([RowNeedles[i].Name], RowOffences($"<t:TableRow Columns=\"x\">{cells[i]}</t:TableRow>"));
        }

        foreach (var heading in new[] { "FWHM arcsec", "HFR (px)", "OBJECT", "Last SESSION" })
        {
            Assert.NotEmpty(HeaderOffences($"<t:TableRow Kind=\"Header\"><TextBlock Text=\"{heading}\" /></t:TableRow>"));
        }

        // A data row's text is not a heading.
        Assert.Empty(HeaderOffences("<t:TableRow><TextBlock Text=\"OBJECT\" /></t:TableRow>"));
    }
}
