using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.ViewModels.Dashboard;

/// <summary>
/// One committed row of the FITS Header Query section, rendered as <c>key op value</c> with a
/// remove button (design-spec 12.2).
/// <para>
/// This type performs no validation. Spec 12.3's key gate and the numeric parse live in
/// <see cref="HeaderQueryBuilder"/>, which is the single place a header condition is ever
/// accepted or dropped; duplicating the gate here would be exactly the per-call-site security
/// convention the house rules forbid. A row whose key or value the builder rejects simply
/// narrows nothing.
/// </para>
/// </summary>
public sealed partial class HeaderConditionViewModel : ObservableObject
{
    private readonly Action<HeaderConditionViewModel> _remove;

    /// <param name="remove">Bound by <see cref="FilterPanelViewModel"/> to its own removal path,
    /// so the row's own button and the panel's <c>RemoveHeaderConditionCommand</c> are one code
    /// path and the view needs no binding to an ancestor's DataContext.</param>
    public HeaderConditionViewModel(string key, string @operator, string value, Action<HeaderConditionViewModel> remove)
    {
        Key = key;
        Operator = @operator;
        Value = value;
        _remove = remove;
    }

    public string Key { get; }

    public string Operator { get; }

    public string Value { get; }

    /// <summary>Spec 12.2's "key op value".</summary>
    public string Display => $"{Key} {Operator} {Value}";

    public HeaderCondition ToCondition() => new(Key, Operator, Value);

    [RelayCommand]
    private void Remove() => _remove(this);
}
