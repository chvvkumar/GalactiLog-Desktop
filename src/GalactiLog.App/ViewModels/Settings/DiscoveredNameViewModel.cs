using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using GalactiLog.Core.Aliases;

namespace GalactiLog.App.ViewModels.Settings;

/// <summary>
/// One row of the web's <c>GroupingEditor</c> left column ("Ungrouped"): a raw name discovered
/// in the library that is not yet part of any canonical group, its frame count, and whether the
/// user has checked it for grouping. Names keep the discovered query's own order (most frames
/// first); there is no client-side sort (the brief's own wording, matching
/// <c>GroupingEditor.tsx</c>: "Discovered names keep the server order; there is no client-side
/// sort.").
/// </summary>
public sealed partial class DiscoveredNameViewModel(string name, int count) : ObservableObject
{
    public string Name { get; } = name;

    public int Count { get; } = count;

    /// <summary>The frame-count caption, the web's <c>`${count} frames`</c> verbatim (questions.md
    /// Q25: the discovered count is "frames", not "light frames").</summary>
    public string CountLabel => $"{Count} frames";

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    /// <summary>The colour this ungrouped name would be drawn in, resolved through the one spine
    /// (P13 R2c). Null until the tab seeds it, which it does through
    /// <see cref="FilterColor.Resolve"/>, so a name with no category seeds spec 5.8.4's grey
    /// rather than nothing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SwatchBrush))]
    public partial string? Color { get; set; }

    /// <summary>The App layer's one parse (review P3-1), the same one
    /// <see cref="AliasGroupViewModel.SwatchBrush"/> uses: a name with no category, or a malformed
    /// value, draws spec 5.8.4's grey rather than throwing.</summary>
    public IImmutableSolidColorBrush SwatchBrush => Dashboard.TargetRowViewModel.ParseTint(Color);
}
