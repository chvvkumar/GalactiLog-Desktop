using CommunityToolkit.Mvvm.ComponentModel;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.ViewModels.Dashboard;

/// <summary>
/// One metric's min and max boxes in the Metrics Quality section (design-spec 12.2, semantics in
/// 12.2.1). Nothing here rounds or validates a value: an integer metric differs only in the step
/// and the format string the view uses.
/// </summary>
public sealed partial class MetricRangeViewModel : ObservableObject
{
    // Spec 12.2's Metrics Quality row, verbatim: group, key, label, step, integer. Labels are the
    // plain metric name ("HFR", not "Avg HFR"), which spec 12.2 states explicitly. The keys are
    // the contract with MetricColumns.ByKey (Task 2) and must not drift from it; the test
    // Metrics_MetricKeys_MatchTheQueryColumnMapExactly is what enforces that.
    private static readonly (string Group, string Key, string Label, double Step, bool IsInteger)[] Table =
    [
        ("Quality", "hfr", "HFR", 0.1, false),
        ("Quality", "fwhm", "FWHM", 0.1, false),
        ("Quality", "eccentricity", "Eccentricity", 0.01, false),
        ("Quality", "stars", "Stars", 1, true),
        ("Guiding", "guiding_rms", "Guiding RMS", 0.01, false),
        ("ADU", "adu_mean", "ADU Mean", 1, true),
        ("Focuser", "focuser_temp", "Focuser Temp", 0.1, false),
        ("Weather", "ambient_temp", "Ambient Temp", 0.1, false),
        ("Weather", "humidity", "Humidity", 1, true),
        ("Mount", "airmass", "Airmass", 0.01, false),
    ];

    /// <summary>The ten metric keys of spec 12.2, in spec order.</summary>
    public static IReadOnlyList<string> Keys { get; } = [.. Table.Select(row => row.Key)];

    /// <summary>The six groups of spec 12.2, each holding its own metrics, in spec order. A fresh
    /// set per call: these carry mutable bounds, so two panels must not share them.</summary>
    public static IReadOnlyList<MetricGroupViewModel> CreateGroups() =>
        [.. Table
            .GroupBy(row => row.Group)
            .Select(group => new MetricGroupViewModel(
                group.Key,
                [.. group.Select(row => new MetricRangeViewModel(row.Key, row.Label, row.Step, row.IsInteger))]))];

    private MetricRangeViewModel(string key, string label, double step, bool isInteger)
    {
        Key = key;
        Label = label;
        Step = step;
        IsInteger = isInteger;
    }

    public string Key { get; }

    public string Label { get; }

    public double Step { get; }

    /// <summary>Drives <see cref="FormatString"/> only. The bound value stays a double.</summary>
    public bool IsInteger { get; }

    /// <summary>What the view binds to <c>NumericUpDown.FormatString</c>. Spec 12.2's step-1
    /// metrics (<c>stars</c>, <c>adu_mean</c>, <c>humidity</c>) render with no decimals; nothing
    /// here rounds the bound value, this is display only.</summary>
    public string FormatString => IsInteger ? "0" : "0.####";

    [ObservableProperty]
    private double? _min;

    [ObservableProperty]
    private double? _max;

    /// <summary>What <see cref="FilterPanelViewModel.BuildCriteria"/> projects. A range with
    /// neither bound set contributes no entry at all.</summary>
    public MetricRange Range => new(Min, Max);

    /// <summary>Clears both bounds. Used by Reset Filters.</summary>
    public void Clear()
    {
        Min = null;
        Max = null;
    }
}

/// <summary>One labelled group of metrics in the Metrics Quality section (spec 12.2).</summary>
public sealed record MetricGroupViewModel(string Title, IReadOnlyList<MetricRangeViewModel> Metrics);
