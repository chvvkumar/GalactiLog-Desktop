using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Task 7 (user ruling U1). FrameTableViewModel.SelectedCaptureIndex is the projection the night
// strip's active tick reads at rest: the _captureOrder position of the one selected row, or null.
// A read of state SelectedRows already owns, never a second source of truth for it.
public class FrameTableSelectedIndexTests
{
    private static FrameTableViewModel Table(IReadOnlyList<FrameRow> frames)
    {
        var display = new DisplaySettings();
        return new FrameTableViewModel(
            frames,
            display,
            new DisplayColumnWriter(() => display, _ => { }),
            new ShellIntegration(copyText: null, start: _ => null),
            openPreview: null,
            new GeneralSettings { Timezone = "UTC", Use24HTime = true },
            getHeaders: _ => null);
    }

    private static IReadOnlyList<FrameRow> ThreeFrames() =>
    [
        FrameTableViewModelTests.Frame(fileName: "frame_000.fits"),
        FrameTableViewModelTests.Frame(fileName: "frame_001.fits"),
        FrameTableViewModelTests.Frame(fileName: "frame_002.fits"),
    ];

    [Fact]
    public void SelectedCaptureIndex_IsNull_WithNoSelection()
    {
        var table = Table(ThreeFrames());

        Assert.Null(table.SelectedCaptureIndex);
    }

    [Fact]
    public void SelectedCaptureIndex_IsTheCaptureOrderPosition_OfTheOneSelectedRow()
    {
        var table = Table(ThreeFrames());

        table.SelectFrameAt(1);

        Assert.Equal(1, table.SelectedCaptureIndex);
    }

    [Fact]
    public void SelectedCaptureIndex_IsNull_WithMoreThanOneRowSelected()
    {
        var table = Table(ThreeFrames());

        table.SelectedRows.Add(table.Rows[0]);
        table.SelectedRows.Add(table.Rows[1]);

        Assert.Equal(2, table.SelectedRows.Count);
        Assert.Null(table.SelectedCaptureIndex);
    }

    [Fact]
    public void SelectedCaptureIndex_Follows_SelectAndPreviewFrameAt()
    {
        // The same entry point a night strip tick click uses (R8, R9).
        var table = Table(ThreeFrames());

        table.SelectAndPreviewFrameAt(2);

        Assert.Equal(2, table.SelectedCaptureIndex);
    }

    [Fact]
    public void SelectedCaptureIndex_ChangesNoSelection_ByBeingRead()
    {
        // The case that catches a projection written as a setter: reading it must never mutate
        // SelectedRows, which is the state it projects from.
        var table = Table(ThreeFrames());
        table.SelectFrameAt(1);

        _ = table.SelectedCaptureIndex;
        _ = table.SelectedCaptureIndex;

        var selected = Assert.Single(table.SelectedRows);
        Assert.Equal("frame_001.fits", selected.FileName);
    }
}
