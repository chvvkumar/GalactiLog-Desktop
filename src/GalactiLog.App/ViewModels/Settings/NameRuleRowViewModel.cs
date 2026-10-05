using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using GalactiLog.Core.Scanning;

namespace GalactiLog.App.ViewModels.Settings;

/// <summary>
/// One row of the name rule editor (design-spec 10.2, 12.7), the port of
/// <c>ScanFiltersPanel.tsx</c>'s rule table: <c>On | Action | Type | Target | Pattern</c> plus a
/// remove action, over one <see cref="NameRule"/>.
/// </summary>
/// <remarks>
/// <para>
/// Action, type and target stay plain strings here for the same reason they are plain strings on
/// <see cref="NameRule"/>: they round-trip through the <c>general.scan_filters</c> JSON column
/// untouched, and validity is a validation-time concern rather than a type-system one.
/// </para>
/// <para>
/// Pattern validation is <strong>local</strong>. The web posts every regex to
/// <c>/api/scan/filters/validate-regex</c> and caches the answer, because its scanner uses
/// Python's <c>re</c> and the browser's regex engine is not the same one. This port's scanner is
/// .NET <see cref="Regex"/>, which is the very engine running in this process, so
/// <see cref="ComputeError"/> compiles the pattern here: no round trip, no cache, and no
/// pending-or-failed state that has to be treated as valid.
/// </para>
/// </remarks>
public sealed partial class NameRuleRowViewModel : ObservableObject
{
    private static readonly string[] ActionValues = ["include", "exclude"];
    private static readonly string[] TargetValues = ["file", "folder"];

    // The Type column's values, in the order the view's combo box lists its labels
    // (wildcard, substring, regex), which is ScanFiltersPanel.tsx's own order and labelling:
    // "glob" is shown as "wildcard" because that is the word a user recognizes.
    private static readonly string[] TypeValues = ["glob", "substring", "regex"];

    /// <param name="rule">The rule as the settings document holds it.</param>
    public NameRuleRowViewModel(NameRule rule)
    {
        Id = rule.Id;
        Enabled = rule.Enabled;
        Action = rule.Action;
        Type = rule.Type;
        Target = rule.Target;
        Pattern = rule.Pattern;
    }

    /// <summary>
    /// A new rule with the web's defaults, verbatim from <c>ScanFiltersPanel.tsx</c>'s "Add
    /// rule": a fresh id, <c>exclude</c>, <c>glob</c>, <c>file</c>, an empty pattern and enabled.
    /// The id is a GUID where the web uses <c>crypto.randomUUID()</c>.
    /// </summary>
    public static NameRuleRowViewModel NewRule() => new(new NameRule
    {
        Id = Guid.NewGuid().ToString(),
        Action = "exclude",
        Type = "glob",
        Pattern = "",
        Target = "file",
        Enabled = true,
    });

    /// <summary>The rule id, stable for the life of the rule. The test-a-path box reports it.
    /// </summary>
    public string Id { get; }

    /// <summary>The <c>Action</c> column's two values, which are also its labels. An instance
    /// property rather than a static one because a compiled binding resolves a member against
    /// the row it is bound to.</summary>
    public IReadOnlyList<string> Actions => ActionValues;

    /// <summary>The <c>Target</c> column's two values, which are also its labels.</summary>
    public IReadOnlyList<string> Targets => TargetValues;

    /// <summary>The <c>On</c> column. A disabled rule is a no-op everywhere in this port,
    /// include rules included (design-spec 10.2's deliberate divergence).</summary>
    [ObservableProperty]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    public partial string Action { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TypeIndex))]
    [NotifyPropertyChangedFor(nameof(PatternPlaceholder))]
    public partial string Type { get; set; }

    [ObservableProperty]
    public partial string Target { get; set; }

    [ObservableProperty]
    public partial string Pattern { get; set; }

    /// <summary>The inline validation message, or null when the rule is valid. Set by the owning
    /// tab's validation pass from <see cref="ComputeError"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; set; }

    /// <summary>Drives the row's error border and the visibility of its message.</summary>
    public bool HasError => ErrorText is not null;

    /// <summary>
    /// The <c>Type</c> combo box's selected index, over the fixed list wildcard, substring,
    /// regex. An index rather than a value-to-label list because the three labels are a closed
    /// set declared in the view; <see cref="Type"/> stays the source of truth.
    /// </summary>
    public int TypeIndex
    {
        get
        {
            var index = Array.IndexOf(TypeValues, Type);
            return index < 0 ? 0 : index;
        }

        set => Type = TypeValues[Math.Clamp(value, 0, TypeValues.Length - 1)];
    }

    /// <summary>The pattern box's watermark, which depends on the type, exactly as the web's
    /// placeholder does.</summary>
    public string PatternPlaceholder => Type switch
    {
        "glob" => "*_bad.fits",
        "regex" => @"^M\d+$",
        _ => "rejected",
    };

    /// <summary>
    /// The row's validation message, or null when it is valid.
    /// </summary>
    /// <remarks>
    /// Review finding M2: everything except the empty check is the choke point's own rule, not a
    /// second copy of it. <c>ScanFilterConfig.Validate</c> over a one-rule configuration with no
    /// paths runs exactly the per-rule checks <c>SettingsStore.SaveGeneral</c> will run, including
    /// the action, type and target vocabulary and the compilation of a glob-derived pattern, which
    /// a local <c>new Regex</c> on the <c>regex</c> type alone could not predict. The inline
    /// message is then incapable of disagreeing with the refusal, which is what makes it a
    /// usability layer rather than a competing rule.
    /// <para>
    /// The empty check stays local and stays stricter: the store rejects only
    /// <c>Pattern.Length == 0</c>, while the web's <c>ruleIsInvalid</c> reports "empty pattern"
    /// for a whitespace-only pattern too. Being stricter here refuses a user mistake early and
    /// can never let an invalid document through.
    /// </para>
    /// <para>
    /// Still local, still no round trip and no cache: <c>ScanFilterConfig</c> is
    /// <c>GalactiLog.Core</c>, compiled into this process, and the regex engine it compiles with
    /// is the one the scanner matches with.
    /// </para>
    /// </remarks>
    public string? ComputeError()
    {
        if (string.IsNullOrWhiteSpace(Pattern))
        {
            return "empty pattern";
        }

        try
        {
            new ScanFilterConfig { NameRules = [ToRule()] }.Validate([]);
        }
        catch (ScanFilterValidationException ex)
        {
            return ex.Message;
        }

        return null;
    }

    /// <summary>The row as the settings document stores it.</summary>
    public NameRule ToRule() => new()
    {
        Id = Id,
        Action = Action,
        Type = Type,
        Pattern = Pattern,
        Target = Target,
        Enabled = Enabled,
    };

    /// <summary>
    /// The web's <c>describeRule</c>, verbatim: how a matched rule id is rendered under a
    /// test-a-path verdict.
    /// </summary>
    public static string Describe(NameRule rule) => $"{rule.Action} {rule.Type} on {rule.Target}: {rule.Pattern}";
}
