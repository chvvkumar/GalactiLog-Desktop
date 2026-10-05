using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GalactiLog.App.Tests.Services;

// Phase 8 Task 7. The one modal host, extracted from MergeDialogService at the second occurrence
// of the pattern (design-lessons rule 1). Every test here is a guard path: a successful ShowDialog
// does not return until the dialog closes, so nothing in this file opens one that would.
//
// MergeDialogServiceTests is the other half of this suite. It is unchanged by the extraction, and
// its passing is the evidence the extraction was mechanical.
public class ModalHostTests
{
    private static Window NeverShownOwner() => new();

    [Fact]
    public async Task ShowAsync_NoOwnerWindow_ReturnsDefaultAndLogsAWarning()
    {
        var logger = new RecordingLogger();
        var host = new ModalHost(() => null, logger);

        var result = await host.ShowAsync<bool>(
            () => throw new InvalidOperationException("the window must not be built without an owner"));

        // A dialog with no window to show in has decided nothing, which is default(bool).
        Assert.False(result);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task ShowAsync_NoOwnerWindow_StillRunsCleanup()
    {
        var cleanups = 0;
        var host = new ModalHost(() => null);

        await host.ShowAsync<bool>(
            () => throw new InvalidOperationException("unreachable"),
            () => cleanups++);

        // The page may already have been built by the caller's closure, so cleanup runs on the
        // path that never opened anything too.
        Assert.Equal(1, cleanups);
    }

    [AvaloniaFact]
    public async Task ShowAsync_CreateThrows_ReturnsDefaultAndRunsCleanup()
    {
        var logger = new RecordingLogger();
        var cleanups = 0;
        var host = new ModalHost(NeverShownOwner, logger);

        var result = await host.ShowAsync<bool>(
            () => throw new InvalidOperationException("the page constructor failed"),
            () => cleanups++);

        // create runs inside the try, so a constructor that throws is logged like a show that
        // fails rather than escaping onto the UI thread.
        Assert.False(result);
        Assert.Equal(1, cleanups);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [AvaloniaFact]
    public async Task ShowAsync_ShowThrows_ReturnsDefaultAndRunsCleanup()
    {
        var logger = new RecordingLogger();
        var cleanups = 0;

        // An owner that was never shown: Avalonia refuses to open a modal over an invisible
        // parent, which is the real failure this guard exists for.
        Window? opened = null;
        var host = new ModalHost(NeverShownOwner, logger);

        // Bounded rather than awaited outright: this test leans on the platform refusing a modal
        // over an invisible parent, and if that ever stops being true the ShowDialog would not
        // complete until the dialog closed. A hang to the xUnit timeout says nothing; this fails
        // with the reason.
        var showing = host.ShowAsync<bool>(
            () => opened = new Window(),
            () => cleanups++);
        var finished = await Task.WhenAny(showing, Task.Delay(TimeSpan.FromSeconds(5)));
        if (finished != showing)
        {
            opened?.Close();
            Assert.Fail("ShowDialog over a never-shown owner opened a dialog instead of failing.");
        }

        Assert.False(await showing);
        Assert.Equal(1, cleanups);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [AvaloniaFact]
    public async Task ShowAsync_RunsCleanupExactlyOnce()
    {
        var cleanups = 0;

        await new ModalHost(() => null).ShowAsync<bool>(() => new Window(), () => cleanups++);
        Assert.Equal(1, cleanups);

        await new ModalHost(NeverShownOwner).ShowAsync<bool>(() => new Window(), () => cleanups++);

        // One call site in a finally: the no-owner path and the failure path each dispose the page
        // once, and neither disposes it twice.
        Assert.Equal(2, cleanups);
    }
}
