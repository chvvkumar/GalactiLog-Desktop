using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GalactiLog.App.ViewModels.Dashboard;

/// <summary>
/// One multi-select toggle pill of the filter panel (design-spec 12.2): an Object Type category
/// or a canonical optical filter. <see cref="Key"/> is what reaches
/// <c>TargetListingCriteria</c>; <see cref="Label"/> is what the user reads. They are the same
/// string for both current uses, and stay separate so a later display rename cannot silently
/// change a query.
/// </summary>
public sealed partial class ToggleOptionViewModel : ObservableObject
{
    /// <summary>Spec 12.2 tints a Filters pill with the colour configured for that filter. That
    /// colour is user data, not a theme token, which is why this is the one place in the panel a
    /// literal colour value is legitimate (spec 14). Null for a pill that carries no colour, so
    /// the Object Type pills stay entirely on tokens.</summary>
    public ToggleOptionViewModel(string key, string label, ISolidColorBrush? tint = null, int frameCount = 0)
    {
        Key = key;
        Label = label;
        Tint = tint;
        FrameCount = frameCount;
    }

    public string Key { get; }

    public string Label { get; }

    public ISolidColorBrush? Tint { get; }

    public bool HasTint => Tint is not null;

    /// <summary>LIGHT frames in the library carrying this value, from <c>DashboardFacets</c>.
    /// Zero for a pill that is offered but present on nothing.</summary>
    public int FrameCount { get; }

    [ObservableProperty]
    private bool _isSelected;
}
