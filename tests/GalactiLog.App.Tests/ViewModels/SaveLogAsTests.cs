using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Diagnostics;
using GalactiLog.Core.Io;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Phase 14B Task 7 (PAR-016, spec 12.8, section 6). Save log as writes through
// DiagnosticsService.ExportLog by delegate (questions.md Q8); LogViewerFixture.ExportLog is the
// honest substitute (its own doc explains why), so these cases assert on the fixture's Exported
// list rather than on a real DiagnosticsService.
public class SaveLogAsTests
{
    // Phase 14B fixer, fixer list item 52 (task7-review P3). Four cases used to hand the picker
    // the relative path "ignored.txt", which AppWriter.BeginExport resolves with
    // Path.GetFullPath, so every run wrote a real file into the test host's working directory.
    // A temp path plus a GUID, deleted whatever the case does, which is the shape the three
    // destination cases in this file already build.
    private sealed class Scratch : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "galactilog-savelog-" + Guid.NewGuid().ToString("N") + ".txt");

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }

    private static void WriteThreeEntries(LogViewerFixture fixture) => fixture.WriteLogFile(
        "galactilog-20260915.log",
        LogViewerFixture.Entry("10:00:00.000", message: "first"),
        LogViewerFixture.Entry("10:00:01.000", message: "second"),
        LogViewerFixture.Entry("10:00:02.000", message: "third"));

    [Fact]
    public async Task SaveLogAs_WritesOneNewFileAtTheDialogPath()
    {
        using var fixture = new LogViewerFixture();
        WriteThreeEntries(fixture);
        var destination = Path.Combine(Path.GetTempPath(), "galactilog-save-" + Guid.NewGuid().ToString("N") + ".txt");
        fixture.SaveLogAsDestinationPicker = () => Task.FromResult<string?>(destination);

        using var page = await fixture.CreateAsync();
        await page.SaveLogAsCommand.ExecuteAsync(null);

        try
        {
            Assert.True(File.Exists(destination));
            Assert.Single(fixture.Exported);
        }
        finally
        {
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }
        }
    }

    [Fact]
    public async Task SaveLogAs_WritesTheLoadedLines_NotTheWholeLog()
    {
        using var fixture = new LogViewerFixture();
        // 150 entries on disk; the page's first load holds 50 (LogReader.DefaultLimit).
        fixture.WriteLogFile(
            "galactilog-20260915.log",
            [.. Enumerable.Range(0, 150).Select(i => LogViewerFixture.Entry(
                string.Format(System.Globalization.CultureInfo.InvariantCulture, "10:{0:00}:00.000", i % 60),
                message: "m" + i))]);
        using var scratch = new Scratch();
        fixture.SaveLogAsDestinationPicker = () => Task.FromResult<string?>(scratch.Path);

        using var page = await fixture.CreateAsync();
        Assert.Equal(50, page.Lines.Count);

        await page.SaveLogAsCommand.ExecuteAsync(null);

        var written = Assert.Single(fixture.Exported).Contents;
        var writtenLines = written.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(50, writtenLines.Length);
    }

    [Fact]
    public async Task SaveLogAs_HonoursTheMinimumLevelFilter()
    {
        using var fixture = new LogViewerFixture();
        fixture.WriteLogFile(
            "galactilog-20260915.log",
            LogViewerFixture.Entry("10:00:00.000", level: "INF", message: "info line"),
            LogViewerFixture.Entry("10:00:01.000", level: "WRN", message: "warn line"));
        using var scratch = new Scratch();
        fixture.SaveLogAsDestinationPicker = () => Task.FromResult<string?>(scratch.Path);

        using var page = await fixture.CreateAsync();
        page.MinimumLevel = GalactiLog.Core.Diagnostics.LogLineLevel.Warning;
        await LogViewerFixture.SettleAsync(page);

        await page.SaveLogAsCommand.ExecuteAsync(null);

        var written = Assert.Single(fixture.Exported).Contents;
        Assert.DoesNotContain("info line", written, StringComparison.Ordinal);
        Assert.Contains("warn line", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveLogAs_HonoursTheSearch()
    {
        using var fixture = new LogViewerFixture();
        fixture.WriteLogFile(
            "galactilog-20260915.log",
            LogViewerFixture.Entry("10:00:00.000", message: "alpha"),
            LogViewerFixture.Entry("10:00:01.000", message: "beta"));
        using var scratch = new Scratch();
        fixture.SaveLogAsDestinationPicker = () => Task.FromResult<string?>(scratch.Path);

        using var page = await fixture.CreateAsync();
        page.Search = "alpha";
        var pendingSearch = page.PendingSearch;
        fixture.Delay.Release();
        if (pendingSearch is not null)
        {
            await pendingSearch;
        }

        await LogViewerFixture.SettleAsync(page);

        await page.SaveLogAsCommand.ExecuteAsync(null);

        var written = Assert.Single(fixture.Exported).Contents;
        Assert.Contains("alpha", written, StringComparison.Ordinal);
        Assert.DoesNotContain("beta", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveLogAs_WritesOldestFirst()
    {
        using var fixture = new LogViewerFixture();
        WriteThreeEntries(fixture);
        using var scratch = new Scratch();
        fixture.SaveLogAsDestinationPicker = () => Task.FromResult<string?>(scratch.Path);

        using var page = await fixture.CreateAsync();
        await page.SaveLogAsCommand.ExecuteAsync(null);

        var written = Assert.Single(fixture.Exported).Contents;
        var firstIndex = written.IndexOf("first", StringComparison.Ordinal);
        var secondIndex = written.IndexOf("second", StringComparison.Ordinal);
        var thirdIndex = written.IndexOf("third", StringComparison.Ordinal);

        Assert.True(firstIndex < secondIndex);
        Assert.True(secondIndex < thirdIndex);
    }

    // Phase 14B fixer, fixer list item 50 (task7-review P3). This used to load 50 lines and assert
    // the written count was at most 1000, which holds for any implementation at all. The cap is
    // now one the fixture reaches, the page is paged until it stops there, and the assertion is an
    // equality against that bound.
    [Fact]
    public async Task SaveLogAs_IsBoundedByTheViewerCap()
    {
        using var fixture = new LogViewerFixture();
        fixture.WriteLogFile(
            "galactilog-20260915.log",
            [.. Enumerable.Range(0, 220).Select(i => LogViewerFixture.Entry(
                string.Format(System.Globalization.CultureInfo.InvariantCulture, "10:{0:00}:00.000", i % 60),
                message: "m" + i))]);
        using var scratch = new Scratch();
        fixture.SaveLogAsDestinationPicker = () => Task.FromResult<string?>(scratch.Path);

        using var page = await fixture.CreateAsync(initialAppLogMaxRows: 100);
        Assert.Equal(50, page.Lines.Count);

        page.LoadMoreCommand.Execute(null);
        await LogViewerFixture.SettleAsync(page);
        Assert.Equal(100, page.Lines.Count);
        Assert.True(page.IsAtViewerCap);

        await page.SaveLogAsCommand.ExecuteAsync(null);

        var written = Assert.Single(fixture.Exported).Contents;
        var writtenLines = written.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(100, writtenLines.Length);
    }

    // Verification B2 (step 33): the launched application wrote 1503 lines against 1001 the viewer
    // was showing. Save log as writes THE LOADED SET, so its count is Lines.Count and nothing else,
    // at a cap the fixture actually reaches and over a log with far more on disk than either.
    [Fact]
    public async Task SaveLogAs_WritesExactlyTheLoadedLines_AtTheCap()
    {
        using var fixture = new LogViewerFixture();
        fixture.WriteLogFile(
            "galactilog-20260915.log",
            [.. Enumerable.Range(0, 1100).Select(i => LogViewerFixture.Entry(
                string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "10:{0:00}:{1:00}.000", i / 60, i % 60),
                message: "m" + i))]);
        using var scratch = new Scratch();
        fixture.SaveLogAsDestinationPicker = () => Task.FromResult<string?>(scratch.Path);

        using var page = await fixture.CreateAsync(initialAppLogMaxRows: 1020);
        while (page.LoadMoreCommand.CanExecute(null))
        {
            page.LoadMoreCommand.Execute(null);
            await LogViewerFixture.SettleAsync(page);
        }

        Assert.Equal(1020, page.Lines.Count);

        await page.SaveLogAsCommand.ExecuteAsync(null);

        var written = Assert.Single(fixture.Exported).Contents;
        var writtenLines = written.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(page.Lines.Count, writtenLines.Length);
        Assert.Equal(1020, writtenLines.Length);
    }

    [Fact]
    public async Task SaveLogAs_WithNoPicker_IsDisabled()
    {
        using var fixture = new LogViewerFixture();
        using var page = await fixture.CreateAsync();

        Assert.False(page.SaveLogAsCommand.CanExecute(null));
    }

    [Fact]
    public async Task SaveLogAs_CancelledInTheDialog_WritesNothing()
    {
        using var fixture = new LogViewerFixture();
        WriteThreeEntries(fixture);
        fixture.SaveLogAsDestinationPicker = () => Task.FromResult<string?>(null);

        using var page = await fixture.CreateAsync();
        await page.SaveLogAsCommand.ExecuteAsync(null);

        Assert.Empty(fixture.Exported);
    }

    [Fact]
    public async Task SaveLogAs_GoesThroughTheExportWriter()
    {
        using var fixture = new LogViewerFixture();
        WriteThreeEntries(fixture);
        var destination = Path.Combine(Path.GetTempPath(), "galactilog-save-" + Guid.NewGuid().ToString("N") + ".txt");
        fixture.SaveLogAsDestinationPicker = () => Task.FromResult<string?>(destination);

        using var page = await fixture.CreateAsync();
        await page.SaveLogAsCommand.ExecuteAsync(null);

        try
        {
            // AppWriter.BeginExport / ExportWriter is what fixture.ExportLog calls, so a file at
            // exactly the destination is the observable proof the writer, not a raw File.WriteAllText
            // elsewhere, produced it.
            Assert.True(File.Exists(destination));
        }
        finally
        {
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }
        }
    }

    [Fact]
    public async Task SaveLogAs_ToAPathOutsideTheDestination_Throws()
    {
        using var fixture = new LogViewerFixture();
        WriteThreeEntries(fixture);
        var destination = Path.Combine(Path.GetTempPath(), "galactilog-save-" + Guid.NewGuid().ToString("N") + ".txt");
        var elsewhere = Path.Combine(Path.GetTempPath(), "galactilog-save-elsewhere-" + Guid.NewGuid().ToString("N") + ".txt");
        fixture.SaveLogAsDestinationPicker = () => Task.FromResult<string?>(destination);

        // Reaches into the fixture's own AppWriter the same way DiagnosticsService.ExportLog
        // would: a writer opened for "destination" refuses "elsewhere" (ExportWriter.AuthorizeExact).
        using var writer = fixture.AppWriter.BeginExport(destination);
        Assert.Throws<UnauthorizedPathException>(() => writer.WriteAllText(elsewhere, "x"));
    }

    [Fact]
    public async Task SaveLogAsCommand_ExecutedPastCanExecute_StillRefusesWithNoPicker()
    {
        using var fixture = new LogViewerFixture();
        using var page = await fixture.CreateAsync();

        // RelayCommand.Execute ignores CanExecute (TRACKING section 6 item 13): the guard inside
        // the command body is what this case pins, not the disabled button.
        await page.SaveLogAsCommand.ExecuteAsync(null);

        Assert.Empty(fixture.Exported);
    }
}
