using CommunityToolkit.Mvvm.ComponentModel;

namespace GalactiLog.App.ViewModels.Settings;

/// <summary>
/// One row in a list of paths on the Library tab: the path itself plus an inline configuration
/// error, rendered in the error colour beside the row (design-spec 10.2's "a path outside every
/// root is a configuration error surfaced in Settings, not silently dropped").
/// </summary>
/// <remarks>
/// The spine for <see cref="ScanRootRowViewModel"/> and <see cref="FilterPathRowViewModel"/>,
/// extracted at the second occurrence rather than the sixth (design-lessons rule 1). The path is
/// not editable in place, in either list: the web's <c>PathList</c> offers add and remove only,
/// and an editable path would have to be revalidated on every keystroke against a root set that
/// is itself being edited.
/// </remarks>
public abstract partial class PathRowViewModel : ObservableObject
{
    protected PathRowViewModel(string path) => Path = path;

    /// <summary>The absolute path, exactly as it was added or as the settings document holds
    /// it. Never rewritten for display: what is shown is what will be saved.</summary>
    public string Path { get; }

    /// <summary>The inline configuration error, or null when the row is valid. Set by the
    /// owning tab's validation pass, never by the row itself: only the tab knows the current
    /// scan root set.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; set; }

    /// <summary>Drives the row's error styling and the visibility of its message.</summary>
    public bool HasError => ErrorText is not null;
}

/// <summary>
/// One entry of <c>general.scan_roots</c> (design-spec 5.8.1, 10.1). Port-only: the web has a
/// single environment-supplied <c>fits_root</c> and expresses everything else as include paths,
/// while spec 10.1 gives this port genuinely independent roots ("an imaging PC commonly has one
/// local capture folder and one NAS share"), each with its own confinement boundary.
/// </summary>
public sealed partial class ScanRootRowViewModel(string path) : PathRowViewModel(path);
