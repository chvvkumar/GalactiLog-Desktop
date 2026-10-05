using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.Merge;
using Xunit;

namespace GalactiLog.App.Tests.Services;

// Phase 7 Task 4, review finding 4. The one modal host's guard path: no owner window, nothing
// opened. A plain [Fact] rather than an [AvaloniaFact], because the assertion is precisely that
// ShowAsync returns before it touches a window.
public class MergeDialogServiceTests
{
    [Fact]
    public async Task ShowAsync_WithNoOwnerWindow_ReturnsFalseAndNeverBuildsTheDialog()
    {
        var created = 0;
        var service = new MergeDialogService(
            _ =>
            {
                created++;
                throw new InvalidOperationException("the dialog must not be built without an owner");
            },
            new ModalHost(() => null));

        var summary = await service.ShowAsync(new MergeRequest(Guid.NewGuid(), Guid.NewGuid(), null, null));

        // A merge with no window to confirm it in has not been confirmed, so there are no counts
        // to report.
        Assert.Null(summary);
        Assert.Equal(0, created);
    }
}
