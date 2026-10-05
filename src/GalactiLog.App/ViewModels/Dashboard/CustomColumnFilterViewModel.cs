using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.ViewModels.Dashboard;

/// <summary>
/// One custom column's row in the filter panel's eighth section (design-spec 12.15). The column's
/// <see cref="CustomColumnDefinition.Type"/> chooses the control and nothing else does: three
/// pills for a checkbox column, a Contains box for a text column, a combo box led by
/// <see cref="AnyChoice"/> for a list column.
/// </summary>
/// <remarks>
/// <para>
/// Exactly one observable property per kind, and no <c>NotifyPropertyChangedFor</c> on a derived
/// member, which is the same shape <see cref="ToggleOptionViewModel"/> and
/// <see cref="MetricRangeViewModel"/> already have. The panel turns every
/// <c>PropertyChanged</c> from a child into one <c>Changed</c> through its own
/// <c>OnChildChanged</c>, so a second notification per change would cost a second query window.
/// <see cref="IsActive"/> is therefore a plain computed property, polled by the section's own
/// active predicate rather than raised.
/// </para>
/// <para>
/// The boolean choice is a string rather than a <see cref="CustomFilterMode"/> so the three pills
/// compare against it through Avalonia's own <c>ObjectConverters.Equal</c>, with no converter of
/// this application's making and with one bound property behind all three.
/// </para>
/// </remarks>
public sealed partial class CustomColumnFilterViewModel : ObservableObject
{
    /// <summary>The unset choice: the leading combo box entry and the first of the three pills.
    /// Selecting it means "no criterion from this column", never a value named "Any".</summary>
    public const string AnyChoice = "Any";

    /// <summary>Spec 12.15: a checkbox column's second pill. A target passes only when a matching
    /// row holds <c>true</c>, so a target with no row at all fails both this and
    /// <see cref="NoChoice"/>.</summary>
    public const string YesChoice = "Yes";

    /// <summary>Spec 12.15: a checkbox column's third pill.</summary>
    public const string NoChoice = "No";

    public CustomColumnFilterViewModel(CustomColumnDefinition column)
    {
        Column = column;
        Choices = [AnyChoice, .. column.Options];
        _selected = AnyChoice;
    }

    /// <summary>The definition this row filters on. Fixed for the row's life: a rename or an
    /// option change arrives as a fresh row through <c>PublishCustomColumns</c>.</summary>
    public CustomColumnDefinition Column { get; }

    /// <summary>The column's name, as the user reads it above its control.</summary>
    public string Label => Column.Name;

    public bool IsBoolean => Column.Type == CustomColumnType.Boolean;

    public bool IsText => Column.Type == CustomColumnType.Text;

    public bool IsDropdown => Column.Type == CustomColumnType.Dropdown;

    /// <summary><see cref="AnyChoice"/>, then the column's options in the user's own order. Empty
    /// of options on the other two kinds. Observable rather than fixed because a chosen option the
    /// column no longer offers is re-added when it is adopted across a refresh: dropping it would
    /// silently widen the result, which spec 12.15 refuses.</summary>
    public ObservableCollection<string> Choices { get; }

    /// <summary>The checkbox column's chosen pill: <see cref="AnyChoice"/>,
    /// <see cref="YesChoice"/> or <see cref="NoChoice"/>. Ignored on the other two kinds.</summary>
    [ObservableProperty]
    private string _booleanChoice = AnyChoice;

    /// <summary>The text column's typed value, debounced by the panel's own window rather than
    /// written through an <c>AutosaveField</c>: this is a filter value, not a stored one.</summary>
    [ObservableProperty]
    private string _text = "";

    /// <summary>The list column's chosen option, or <see cref="AnyChoice"/>. Null while the combo
    /// box has no selection at all, which reads as no criterion.</summary>
    [ObservableProperty]
    private string? _selected;

    /// <summary>True while this row contributes a clause. False for every unset choice and for a
    /// text whose trimmed value is empty, which is the half of F3 that keeps
    /// <c>TargetListingCriteria.AnyFilterActive</c> agreeing with the SQL.</summary>
    public bool IsActive => Column.Type switch
    {
        CustomColumnType.Boolean => !string.Equals(BooleanChoice, AnyChoice, StringComparison.Ordinal),
        CustomColumnType.Text => !string.IsNullOrWhiteSpace(Text),
        CustomColumnType.Dropdown =>
            Selected is not null && !string.Equals(Selected, AnyChoice, StringComparison.Ordinal),
        _ => false,
    };

    /// <summary>What <c>FilterPanelViewModel.BuildCriteria</c> projects. Only called for a row
    /// whose <see cref="IsActive"/> is true, so the answer never carries
    /// <see cref="CustomFilterMode.Any"/> and a <see cref="CustomFilterMode.Contains"/> always
    /// carries a trimmed, non-empty text.</summary>
    public CustomColumnFilter ToFilter() => Column.Type switch
    {
        CustomColumnType.Boolean => new CustomColumnFilter(
            Column.Slug,
            string.Equals(BooleanChoice, YesChoice, StringComparison.Ordinal)
                ? CustomFilterMode.Yes
                : CustomFilterMode.No,
            null),
        CustomColumnType.Text => new CustomColumnFilter(Column.Slug, CustomFilterMode.Contains, Text.Trim()),
        _ => new CustomColumnFilter(Column.Slug, CustomFilterMode.Equals, Selected),
    };

    /// <summary>Returns every control to its unset choice. Called by Reset Filters inside the
    /// panel's own suspend block, so the whole reset still costs one <c>Changed</c>.</summary>
    public void Clear()
    {
        BooleanChoice = AnyChoice;
        Text = "";
        Selected = AnyChoice;
    }

    /// <summary>
    /// Takes over the chosen value of the row this one replaces, so a post-scan refresh of the
    /// definition list never silently drops a filter the user set. A previous row of a different
    /// type contributes nothing: the column's type cannot change (spec 12.15), so that only
    /// happens when a slug was reused by a hand-edited catalogue.
    /// </summary>
    public void AdoptFrom(CustomColumnFilterViewModel previous)
    {
        if (previous.Column.Type != Column.Type)
        {
            return;
        }

        BooleanChoice = previous.BooleanChoice;
        Text = previous.Text;

        // A chosen option the column no longer offers is kept and applied, and matches nothing.
        // Without this the combo box would clear a selection that is not in its item source and
        // the result would widen instead.
        if (previous.Selected is { } chosen && !Choices.Contains(chosen, StringComparer.Ordinal))
        {
            Choices.Add(chosen);
        }

        Selected = previous.Selected;
    }

    [RelayCommand]
    private void SetBooleanChoice(string? choice) => BooleanChoice = choice ?? AnyChoice;
}

/// <summary>One scope's heading and its rows in the filter panel's eighth section (spec 12.15).
/// A scope with no column gets no group at all, so no empty heading is ever drawn. The record
/// shape is <see cref="MetricGroupViewModel"/>'s, which is the same problem one section earlier.
/// </summary>
public sealed record CustomFilterGroupViewModel(
    string Title, IReadOnlyList<CustomColumnFilterViewModel> Filters);
