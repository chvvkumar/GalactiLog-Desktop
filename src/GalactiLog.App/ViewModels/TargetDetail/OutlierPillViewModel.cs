using CommunityToolkit.Mvvm.ComponentModel;

namespace GalactiLog.App.ViewModels.TargetDetail;

/// <summary>
/// One of the frames strip's outlier pills (P24 R22): "<c>HFR outliers (n)</c>" for a metric the
/// night has values for, n the frames the night's own rule flagged, disabled while n is 0.
/// <see cref="IsActive"/> is two way with the pill's checked state, as the rig pills' selection
/// is; <see cref="SessionCardViewModel"/> watches it and runs the one filter path, and pushes the
/// table's filter back into it, so Escape inside the table unchecks the pill.
/// </summary>
public sealed partial class OutlierPillViewModel(FrameOutlierFilter filter, string label, int count) : ObservableObject
{
    public FrameOutlierFilter Filter { get; } = filter;

    /// <summary>"HFR outliers (n)" and its siblings.</summary>
    public string Label { get; } = label;

    /// <summary>The detail's rows flagged with this kind.</summary>
    public int Count { get; } = count;

    public bool IsEnabled => Count > 0;

    /// <summary>Whether the frame table is filtered to this pill's kind right now.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }
}
