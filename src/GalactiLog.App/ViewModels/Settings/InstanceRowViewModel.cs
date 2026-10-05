using CommunityToolkit.Mvvm.ComponentModel;
using GalactiLog.Core.Integrations;

namespace GalactiLog.App.ViewModels.Settings;

/// <summary>
/// One row of the External Tools tab's NINA or Stellarium instances list (design-spec 12.7,
/// 12.16, core-shapes 2.9). Editable in place with no Save button: each field commits through
/// <see cref="ExternalToolsTabViewModel.CommitInstance"/> on change.
/// </summary>
public sealed partial class InstanceRowViewModel : ObservableObject
{
    private readonly ExternalToolsTabViewModel _owner;
    private readonly bool _constructed;
    private IntegrationInstance _committed;

    internal InstanceRowViewModel(ExternalToolsTabViewModel owner, bool isNina, string name, string url, bool enabled)
    {
        _owner = owner;
        IsNina = isNina;
        Name = name;
        Url = url;
        Enabled = enabled;
        _committed = new IntegrationInstance(name, url, enabled);
        _constructed = true;
    }

    /// <summary>This row's own last-committed values, tracked on the row rather than recovered
    /// from the tab's <c>Saved</c> document by list position (wave2-review :237): that lookup can
    /// lag a second edit made before the first one's queued write lands.</summary>
    internal IntegrationInstance Committed => _committed;

    internal void MarkCommitted() => _committed = new IntegrationInstance(Name, Url, Enabled);

    /// <summary>True for a row of the NINA list, false for the Stellarium list, so one commit
    /// method on the owner can tell which of the two stored keys to rewrite.</summary>
    internal bool IsNina { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOffered))]
    [NotifyPropertyChangedFor(nameof(ShowNotOfferedCaption))]
    public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOffered))]
    [NotifyPropertyChangedFor(nameof(ShowNotOfferedCaption))]
    public partial string Url { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOffered))]
    [NotifyPropertyChangedFor(nameof(ShowNotOfferedCaption))]
    public partial bool Enabled { get; set; }

    /// <summary>The refused-URL message, or null. Set only by a commit that refused a non-blank
    /// URL; a blank URL is stored, never refused (spec 5.8.1 amendment 1b).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNotOfferedCaption))]
    public partial string? UrlError { get; set; }

    /// <summary>Core-shapes 2.1's one declaration of the rule, never a second spelling: drives
    /// this row's "not offered" caption and is the exact predicate the Target page's menu filters
    /// by (spec 12.7's "the second half of the same rule").</summary>
    public bool IsOffered => IntegrationInstance.IsOffered(new IntegrationInstance(Name, Url, Enabled));

    /// <summary>wave2-review P2 (:106): hidden while <see cref="UrlError"/> is set, so the two
    /// lines never draw on the same row.</summary>
    public bool ShowNotOfferedCaption => !IsOffered && UrlError is null;

    partial void OnNameChanged(string value)
    {
        if (_constructed)
        {
            _owner.CommitInstance(this);
        }
    }

    partial void OnUrlChanged(string value)
    {
        if (_constructed)
        {
            _owner.CommitInstance(this);
        }
    }

    partial void OnEnabledChanged(bool value)
    {
        if (_constructed)
        {
            _owner.CommitInstance(this);
        }
    }
}
