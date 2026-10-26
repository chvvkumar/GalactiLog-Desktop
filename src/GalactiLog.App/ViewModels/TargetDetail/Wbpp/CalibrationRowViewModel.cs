using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using GalactiLog.Core.Wbpp;

namespace GalactiLog.App.ViewModels.TargetDetail.Wbpp;

/// <summary>One folder of calibration frames in a night's Calibration found block: its tick, its
/// selected and total counts, its size and the matcher's note.</summary>
public sealed partial class CalibrationRowViewModel : ObservableObject
{
    private readonly Action<CalibrationRowViewModel> _changed;

    /// <param name="row">The matcher's row.</param>
    /// <param name="scannedLevel">The folder the scan covered, which the row's path is shown
    /// relative to.</param>
    /// <param name="changed">Called when the user ticks or unticks the row.</param>
    public CalibrationRowViewModel(CalibrationRow row, string scannedLevel, Action<CalibrationRowViewModel> changed)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(changed);

        Row = row;
        RelativePath = Path.GetRelativePath(scannedLevel, row.Folder);
        _isTicked = row.DefaultTicked;
        _changed = changed;
    }

    /// <summary>The matcher's row.</summary>
    public CalibrationRow Row { get; }

    /// <summary>The folder, relative to the scanned level.</summary>
    public string RelativePath { get; }

    /// <summary>Whether the row's frames are copied.</summary>
    [ObservableProperty]
    private bool _isTicked;

    /// <summary>"210 of 210 files", or "40 files" when nothing in the row was selected.</summary>
    public string CountText => Row.SelectedCount > 0
        ? string.Create(CultureInfo.InvariantCulture, $"{Row.SelectedCount:N0} of {Row.TotalCount:N0} files")
        : Row.TotalCount == 1 ? "1 file" : string.Create(CultureInfo.InvariantCulture, $"{Row.TotalCount:N0} files");

    /// <summary>The selected files' size, or every file's when none is selected.</summary>
    public string BytesText => WbppPathText.Bytes(Row.SelectedCount > 0 ? Row.SelectedBytes : Row.TotalBytes);

    /// <summary>"same night", the night gap, or the first mismatching key.</summary>
    public string Note => Row.Note;

    /// <summary>Reserved for a note drawn in the warning ink. Always false today.</summary>
    public bool IsWarningNote => false;

    partial void OnIsTickedChanged(bool value) => _changed(this);
}
