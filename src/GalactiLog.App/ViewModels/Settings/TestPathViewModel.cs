using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Settings;

namespace GalactiLog.App.ViewModels.Settings;

/// <summary>
/// Design-spec 10.2's test-a-path tool, on the Library tab (spec 12.7): paste a path, get the
/// verdict the walker would reach and the ids of the rules that decided it.
/// </summary>
/// <remarks>
/// <para>
/// It calls <see cref="ScanFilterConfig.TestPath"/>. It does not reimplement any part of the
/// decision, because a test box that disagrees with the walker is worse than no test box at all.
/// </para>
/// <para>
/// It runs against the <strong>saved</strong> configuration, not the one being edited, and the
/// on-screen copy says so. That is the web's behaviour and the web's wording, and it is the only
/// honest one: <see cref="ScanFilterConfig.TestPath"/> confines the path to a scan root before it
/// looks at anything, so an unsaved root list would produce a verdict the scanner would never
/// reach.
/// </para>
/// <para>
/// The path is resolved and confined before any filesystem access, which is what keeps the box
/// from being used to probe for arbitrary host paths (spec 10.2's last paragraph, spec 2.3).
/// Nothing here adds a probe of its own.
/// </para>
/// </remarks>
public sealed partial class TestPathViewModel : ObservableObject
{
    /// <summary>The verdict headline per design-spec 10.2's four verdicts, verbatim from
    /// <c>ScanFiltersPanel.tsx</c>'s <c>VERDICT_LABEL</c>.</summary>
    public static readonly IReadOnlyDictionary<string, string> VerdictLabels =
        new Dictionary<string, string>
        {
            ["included"] = "Will be scanned",
            ["excluded_by_path"] = "Skipped: excluded by path",
            ["excluded_by_rule"] = "Skipped: matched an exclude rule",
            ["excluded_by_missing_include"] = "Skipped: no include rule matched",
        };

    /// <summary>The one-line explanation under the headline, verbatim from the web's
    /// <c>VERDICT_HINT</c>.</summary>
    public static readonly IReadOnlyDictionary<string, string> VerdictHints =
        new Dictionary<string, string>
        {
            ["included"] = "No rule caused this path to be skipped.",
            ["excluded_by_path"] = "A parent folder is listed under Exclude paths.",
            ["excluded_by_rule"] = "A name rule with action=exclude matched.",
            ["excluded_by_missing_include"] = "Include rules are set, but none of them matched this path.",
        };

    /// <summary>The sentence appended to an <c>included</c> verdict when at least one exclude
    /// rule exists, verbatim from the web.</summary>
    public const string IncludedWithExcludeRulesNote =
        "None of your exclude rules matched this path. If you expected one to match, double-check "
        + "the pattern against the exact filename.";

    private readonly Func<GeneralSettings> _saved;

    /// <param name="saved">The last saved <c>general</c> document. A delegate rather than a value,
    /// because a save on the tab above changes what the box must test against, and a delegate
    /// rather than a <c>SettingsStore</c> so the box constructs in a unit test with no database
    /// (design-spec 18.3).</param>
    public TestPathViewModel(Func<GeneralSettings> saved)
    {
        _saved = saved;
        Path = "";
    }

    /// <summary>The path being tested. It does not need to exist on disk.</summary>
    [ObservableProperty]
    public partial string Path { get; set; }

    /// <summary>Treat the path as a file, a folder, or auto-detect. Defaults to
    /// <see cref="ScanFilterConfig.PathKind.Auto"/>, as the web's kind select does.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KindIndex))]
    public partial ScanFilterConfig.PathKind Kind { get; set; }

    /// <summary>The kind combo box's selected index, over auto, file, folder. The enum's own
    /// order, so the two cannot drift apart.</summary>
    public int KindIndex
    {
        get => (int)Kind;
        set => Kind = (ScanFilterConfig.PathKind)Math.Clamp(value, 0, 2);
    }

    /// <summary>The raw verdict token from <see cref="ScanFilterConfig.TestPath"/>, or null
    /// before the first test.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVerdict))]
    public partial string? Verdict { get; private set; }

    /// <summary>The verdict headline. Null before the first test.</summary>
    [ObservableProperty]
    public partial string? VerdictLabel { get; private set; }

    /// <summary>The one-line explanation under the headline. Null before the first test.
    /// </summary>
    [ObservableProperty]
    public partial string? VerdictHint { get; private set; }

    /// <summary>True only for the <c>included</c> verdict, which is the one the view renders in
    /// the success colour; every other verdict is rendered in the error colour.</summary>
    [ObservableProperty]
    public partial bool IsIncluded { get; private set; }

    /// <summary>The extra sentence for an <c>included</c> verdict with at least one exclude rule
    /// present. Null otherwise.</summary>
    [ObservableProperty]
    public partial string? ExtraNote { get; private set; }

    /// <summary>Whether a verdict has been produced yet, so the view can keep the result block
    /// hidden until the first test.</summary>
    public bool HasVerdict => Verdict is not null;

    /// <summary>
    /// The deciding rules, rendered as the web's <c>describeRule</c> does:
    /// <c>action type on target: pattern</c>, falling back to the first eight characters of the
    /// id when the rule is no longer in the saved document.
    /// </summary>
    public ObservableCollection<string> MatchedRuleDescriptions { get; } = [];

    /// <summary>
    /// Runs the saved filter configuration against <see cref="Path"/>. An empty box clears the
    /// result rather than testing the empty string.
    /// </summary>
    [RelayCommand]
    private void Test()
    {
        MatchedRuleDescriptions.Clear();

        if (string.IsNullOrWhiteSpace(Path))
        {
            Verdict = null;
            VerdictLabel = null;
            VerdictHint = null;
            ExtraNote = null;
            IsIncluded = false;
            return;
        }

        var general = _saved();
        var filters = general.ScanFilters;
        var result = filters.TestPath(Path, general.ScanRoots, Kind);

        Verdict = result.Verdict;
        VerdictLabel = VerdictLabels.GetValueOrDefault(result.Verdict, result.Verdict);
        VerdictHint = VerdictHints.GetValueOrDefault(result.Verdict, "");
        IsIncluded = result.Verdict == "included";

        foreach (var id in result.MatchedRuleIds)
        {
            MatchedRuleDescriptions.Add(DescribeRule(filters, id));
        }

        // The web appends this only when the path was included and there is at least one exclude
        // rule to have missed it: without an exclude rule present the sentence would be noise.
        ExtraNote = IsIncluded && filters.NameRules.Any(rule => rule.Action == "exclude")
            ? IncludedWithExcludeRulesNote
            : null;
    }

    /// <summary>
    /// The web's <c>describeRule</c>: the rule rendered as <c>action type on target: pattern</c>,
    /// or the first eight characters of its id when the document no longer holds it.
    /// </summary>
    /// <remarks>
    /// The fallback is defensive here rather than routine. The web needs it because its verdict
    /// comes from the server while its rule list is the browser's edited copy, so the two can
    /// genuinely disagree; this port reads the verdict and the rules from the one saved document
    /// in one call, so an id with no rule behind it would mean
    /// <see cref="ScanFilterConfig.TestPath"/> had returned an id it did not take from
    /// <see cref="ScanFilterConfig.NameRules"/>. A blank bullet would be the worse failure, so
    /// the fallback stays and is tested directly.
    /// </remarks>
    internal static string DescribeRule(ScanFilterConfig filters, string id)
    {
        var rule = filters.NameRules.FirstOrDefault(candidate => candidate.Id == id);
        return rule is null
            ? id[..Math.Min(8, id.Length)]
            : NameRuleRowViewModel.Describe(rule);
    }
}
