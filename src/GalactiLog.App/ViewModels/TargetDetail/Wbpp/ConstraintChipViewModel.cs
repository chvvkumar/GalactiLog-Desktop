using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.Core.Wbpp;

namespace GalactiLog.App.ViewModels.TargetDetail.Wbpp;

/// <summary>One entry of the comparator select, the port of the web's <c>OP_GLYPH</c> pair. Spec
/// 12.13 states the two labels as the words "at most" and "at least" rather than the web's
/// glyphs.</summary>
/// <remarks>A record, so a bound selection compares by value; the instances are the two static
/// ones on <see cref="ConstraintChipViewModel"/> and no third is ever built.</remarks>
public sealed record ComparatorOption(ConstraintOp Op, string Label);

/// <summary>One of the eccentricity chip's three quick-fill thresholds (spec 12.13: Strict 0.55,
/// Balanced 0.65, Relaxed 0.75). <see cref="IsActive"/> is the lit state, which is true only while
/// the chip's value equals <see cref="Value"/> <b>and</b> its comparator is
/// <see cref="ConstraintOp.AtMost"/>.</summary>
public sealed partial class PresetOptionViewModel(string label, double value) : ObservableObject
{
    /// <summary>The preset's own name, declared by the panel rather than by Core: the figures come
    /// from <c>QualityFilter.EccentricityPresets</c> and the words from spec 12.13.</summary>
    public string Label { get; } = label;

    /// <summary>The threshold this preset sets.</summary>
    public double Value { get; } = value;

    /// <summary>The figure as the chip prints it, at eccentricity's own two decimals and in
    /// invariant culture.</summary>
    public string ValueText { get; } = QualityFilter.Format(WbppMetric.Ecc, value);

    /// <summary>What the preset button reads: spec 12.13's own phrasing of the three, "Strict 0.55,
    /// Balanced 0.65 and Relaxed 0.75", one preset at a time.</summary>
    public string Caption { get; } = label + " " + QualityFilter.Format(WbppMetric.Ecc, value);

    /// <summary>Whether this preset is the chip's current value and comparator pair.</summary>
    [ObservableProperty]
    private bool _isActive;
}

/// <summary>
/// One metric chip of spec 12.13's quality filter toolbar: ghost, held or active, with the
/// comparator select, the numeric threshold, the three eccentricity presets and the button that
/// disables the constraint without deleting it. Port of the per-metric arm of
/// <c>frontend/src/components/wbpp/WbppQualityPanel.tsx</c>'s chip row.
/// </summary>
/// <remarks>
/// <para>
/// The chip is a surface over one <see cref="RawConstraint"/>, never the order of record: the
/// panel owns the constraint list in the user's own chip order (spec 12.13: "Every enabled
/// constraint the frame violated is recorded, in the user's own chip order") and each chip reads
/// its own entry out of it through <see cref="Adopt"/>. Every user edit raises the change callback
/// once and the panel rebuilds its list from the five chips' current entries, preserving the order
/// the constraints were added in.
/// </para>
/// <para>
/// A disabled constraint keeps its value. Spec 12.13: "a button that disables the constraint
/// without deleting it: the chip drops back to a ghost that still shows its held value, and
/// clicking it again restores that value rather than starting over."
/// </para>
/// <para>
/// Every number that becomes a string, or is parsed from one, goes through
/// <see cref="CultureInfo.InvariantCulture"/>, so a machine whose culture writes a comma decimal
/// mark cannot put <c>0,55</c> into a stored threshold.
/// </para>
/// </remarks>
public sealed partial class ConstraintChipViewModel : ObservableObject
{
    /// <summary>
    /// The comparator select's two options, built once and never rebuilt.
    /// </summary>
    /// <remarks>Static and shared on purpose (the Phase 15B <c>IntervalChoices</c> lesson): a list
    /// that is cleared and refilled under a two-way <c>SelectedItem</c> renders the select empty on
    /// its first visit, because clearing the collection clears the selection and the assignment
    /// that follows announces no change. A list that is never touched cannot do that.</remarks>
    public static IReadOnlyList<ComparatorOption> Comparators { get; } =
    [
        new(ConstraintOp.AtMost, "at most"),
        new(ConstraintOp.AtLeast, "at least"),
    ];

    private static readonly IReadOnlyList<string> PresetLabels = ["Strict", "Balanced", "Relaxed"];

    private readonly Action<ConstraintChipViewModel> _edited;

    // True while Adopt is writing the stored entry into the bound properties, so re-seeding a chip
    // from settings does not queue a write of the value it has just read.
    private bool _adopting;

    /// <param name="metric">The metric this chip constrains. Its polarity decides the comparator a
    /// freshly added constraint carries (<c>QualityFilter.EmptyConstraintFor</c>).</param>
    /// <param name="edited">Raised once per user edit, on the UI thread. The panel rebuilds its
    /// constraint list and re-runs the verdict pass from it.</param>
    public ConstraintChipViewModel(WbppMetric metric, Action<ConstraintChipViewModel> edited)
    {
        ArgumentNullException.ThrowIfNull(edited);

        Metric = metric;
        _edited = edited;
        Name = LabelFor(metric);
        _comparator = Comparators[0];
        _thresholdText = "";

        // Spec 12.13: "The eccentricity chip alone carries three presets". Eccentricity is the one
        // constrained metric whose meaning is independent of the rig, which is why it is the one
        // with fixed figures: a preset seeded from the frames under judgment would pass most of
        // them and read as an authority it does not have. The other four chips carry none.
        Presets = metric == WbppMetric.Ecc
            ? [.. QualityFilter.EccentricityPresets.Select(
                (value, index) => new PresetOptionViewModel(PresetLabels[index], value))]
            : [];
    }

    /// <summary>The metric this chip constrains.</summary>
    public WbppMetric Metric { get; }

    /// <summary>Spec 12.13's chip label: HFR, Ecc, FWHM, Stars or RMS. Deliberately not
    /// <c>QualityFilter.ShortName</c>, which is the terser wording a failure sentence uses inside a
    /// table cell.</summary>
    public string Name { get; }

    /// <summary>The three eccentricity presets, or an empty list on the other four metrics.</summary>
    public IReadOnlyList<PresetOptionViewModel> Presets { get; }

    /// <summary>The comparator select's options, which are <see cref="Comparators"/>. An instance
    /// property so the view binds it through its <c>x:DataType</c>; it is the same list object on
    /// every chip and is never rebuilt.</summary>
    public IReadOnlyList<ComparatorOption> ComparatorOptions => Comparators;

    /// <summary>The disable button's accessible name, so the control reads as more than its
    /// mark.</summary>
    public string DisableAccessibleName => "Disable the " + Name + " constraint";

    /// <summary>The comparator select's accessible name.</summary>
    public string ComparatorAccessibleName => Name + " comparison";

    /// <summary>The threshold field's accessible name.</summary>
    public string ThresholdAccessibleName => Name + " threshold";

    /// <summary>Whether this chip has any presets to draw, so the view binds a bool rather than a
    /// count.</summary>
    public bool HasPresets => Presets.Count > 0;

    /// <summary>Whether a <see cref="RawConstraint"/> for this metric exists in the stored list at
    /// all. False is spec 12.13's "inactive chip": a ghost carrying a plus and the metric
    /// name.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive), nameof(GhostText))]
    private bool _isPresent;

    /// <summary>The constraint's <c>Enabled</c> flag. False is spec 12.13's held chip: a ghost that
    /// still shows the value it holds.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive), nameof(GhostText))]
    private bool _isOn;

    /// <summary>The comparator select's two-way selection.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GhostText))]
    private ComparatorOption _comparator;

    /// <summary>The threshold field's text, exactly as the user typed it.</summary>
    /// <remarks>Bound as a string rather than as a nullable double so spec 12.13's "Emptying the
    /// threshold field returns the chip to valueless rather than pinning the last number typed"
    /// can hold: a numeric binding has nowhere to put an empty field, and a partially typed or
    /// unparseable entry has to stay on screen so the user can correct it.</remarks>
    [ObservableProperty]
    private string _thresholdText;

    /// <summary>The chip's held threshold, or null while it is valueless. Never changed by an entry
    /// that does not parse: spec 12.13's field can be corrected in place.</summary>
    public double? Value { get; private set; }

    /// <summary>True in spec 12.13's active state: a constraint exists and is enabled. The view
    /// draws the full chip for it and the ghost otherwise.</summary>
    public bool IsActive => IsPresent && IsOn;

    /// <summary>What the ghost reads. A plus and the metric name while there is nothing held, and
    /// the held comparator and value beside them once there is: spec 12.13's ghost "still shows its
    /// held value", which is what tells the user a click restores it rather than starting
    /// over.</summary>
    public string GhostText => IsPresent && Value is { } held
        ? string.Concat(Name, " ", Comparator.Label, " ", QualityFilter.Format(Metric, held))
        : string.Concat("+ ", Name);

    /// <summary>The chip's own entry in the stored constraint list, or null while it is absent.
    /// The panel reads this to rebuild the list and never rebuilds a constraint of its own.</summary>
    public RawConstraint? Constraint
        => IsPresent ? new RawConstraint(Metric, Comparator.Op, Value, IsOn) : null;

    /// <summary>
    /// Adopts the stored entry for this metric, without raising the edit callback. Null means the
    /// metric carries no stored constraint and the chip returns to the ghost with nothing held.
    /// </summary>
    /// <remarks>The panel calls this once per chip when it loads the rig's entry, and never again:
    /// after that the chip is the source of its own state and the panel reads
    /// <see cref="Constraint"/> back out of it.</remarks>
    public void Adopt(RawConstraint? constraint)
    {
        _adopting = true;
        try
        {
            IsPresent = constraint is not null;
            IsOn = constraint?.Enabled ?? false;
            Comparator = Comparators.First(option => option.Op == (constraint?.Op ?? DefaultOp()));
            Value = constraint?.Value;
            ThresholdText = Value is { } value ? QualityFilter.Format(Metric, value) : "";
        }
        finally
        {
            _adopting = false;
        }

        RefreshPresets();
        OnPropertyChanged(nameof(GhostText));
    }

    /// <summary>
    /// Spec 12.13's click on a ghost chip. An absent chip gains
    /// <c>QualityFilter.EmptyConstraintFor</c>: enabled, valueless, with the comparator its
    /// polarity implies. A held chip is re-enabled and keeps the value it was holding.
    /// </summary>
    /// <remarks><c>RelayCommand.Execute</c> ignores <c>CanExecute</c> (TRACKING section 6 item 13),
    /// so the already-active guard is in the body rather than in an enablement: a click on a chip
    /// that is already active must not reset the value the user typed.</remarks>
    [RelayCommand]
    private void Enable()
    {
        if (IsActive)
        {
            return;
        }

        _adopting = true;
        try
        {
            if (!IsPresent)
            {
                var empty = QualityFilter.EmptyConstraintFor(Metric);
                IsPresent = true;
                Comparator = Comparators.First(option => option.Op == empty.Op);
                Value = empty.Value;
                ThresholdText = "";
            }

            IsOn = true;
        }
        finally
        {
            _adopting = false;
        }

        Publish();
    }

    /// <summary>Spec 12.13's disable button: it "disables the constraint without deleting it", so
    /// the entry stays in the list with its value intact and the chip drops back to a ghost that
    /// still shows it.</summary>
    /// <remarks>Guarded in the body for the reason <see cref="Enable"/> is: a command body runs
    /// whatever its enablement says.</remarks>
    [RelayCommand]
    private void Disable()
    {
        if (!IsActive)
        {
            return;
        }

        _adopting = true;
        try
        {
            IsOn = false;
        }
        finally
        {
            _adopting = false;
        }

        Publish();
    }

    /// <summary>Spec 12.13's preset, which sets "the value and the comparator to at most"
    /// together. Only the eccentricity chip offers one.</summary>
    /// <remarks>Guarded the way <c>OnThresholdTextChanged</c> guards its own publish: a
    /// click on the preset that is already lit moves neither the comparator nor the value, and the
    /// panel answers an edit by clearing every row override and queueing a settings write. An
    /// unguarded body would therefore cost the user every row they had rescued, from a button that
    /// changes nothing.</remarks>
    [RelayCommand]
    private void ApplyPreset(PresetOptionViewModel? preset)
    {
        if (preset is null || !IsActive)
        {
            return;
        }

        if (Comparator.Op == ConstraintOp.AtMost && Nullable.Equals(Value, preset.Value))
        {
            return;
        }

        _adopting = true;
        try
        {
            Comparator = Comparators.First(option => option.Op == ConstraintOp.AtMost);
            Value = preset.Value;
            ThresholdText = preset.ValueText;
        }
        finally
        {
            _adopting = false;
        }

        Publish();
    }

    // Generated by [ObservableProperty]. A comparator moved by the user is a constraint-set change
    // like any other, which is what clears the overrides on the panel.
    partial void OnComparatorChanged(ComparatorOption value)
    {
        if (_adopting)
        {
            return;
        }

        Publish();
    }

    // Generated by [ObservableProperty]. Spec 12.13: "Emptying the threshold field returns the chip
    // to valueless rather than pinning the last number typed." An entry that does not parse leaves
    // the held value exactly where it was and the text exactly as typed, so the field can be
    // corrected in place; it publishes nothing, because the constraint set did not move.
    partial void OnThresholdTextChanged(string value)
    {
        if (_adopting)
        {
            return;
        }

        double? next;
        if (string.IsNullOrWhiteSpace(value))
        {
            next = null;
        }
        else if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && double.IsFinite(parsed))
        {
            next = parsed;
        }
        else
        {
            return;
        }

        if (Nullable.Equals(Value, next))
        {
            return;
        }

        Value = next;
        Publish();
    }

    // One notification per edit: the derived text, the preset lamps, then the panel.
    private void Publish()
    {
        OnPropertyChanged(nameof(GhostText));
        RefreshPresets();
        _edited(this);
    }

    // Spec 12.13: a preset is lit "while it is the current pair", which is both the value and the
    // comparator, not the value alone.
    private void RefreshPresets()
    {
        foreach (var preset in Presets)
        {
            preset.IsActive = Comparator.Op == ConstraintOp.AtMost
                && Value is { } value
                && value.Equals(preset.Value);
        }
    }

    private ConstraintOp DefaultOp() => QualityFilter.EmptyConstraintFor(Metric).Op;

    // Spec 12.13's chip column, verbatim.
    private static string LabelFor(WbppMetric metric) => metric switch
    {
        WbppMetric.Hfr => "HFR",
        WbppMetric.Ecc => "Ecc",
        WbppMetric.Fwhm => "FWHM",
        WbppMetric.Stars => "Stars",
        _ => "RMS",
    };
}
