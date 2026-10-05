using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Diagnostics;
using GalactiLog.Core.Diagnostics;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Phase 14B Task 7 (PAR-012, spec 12.8, ruling D3, task1-report.md departure 2). The copy cap
// (LogViewerViewModel.CopyAllCap, fixed at construction) and the viewer cap (MaxRows, live) are
// two different bounds derived from the same key; both are covered here because they share one
// fixture and one rule (min(50000, app_log_max_rows)).
public class LogViewerCapTests
{
    [Fact]
    public async Task TheCopyCap_IsTheMinimumOfFiftyThousandAndTheKey()
    {
        using var fixture = new LogViewerFixture();
        using var small = fixture.Build(copyAllCap: Math.Min(LogViewerViewModel.DefaultCopyAllCap, 2_000));
        await LogViewerFixture.SettleAsync(small);
        Assert.Equal(2_000, small.CopyAllCap);

        using var large = fixture.Build(copyAllCap: Math.Min(LogViewerViewModel.DefaultCopyAllCap, 400_000));
        await LogViewerFixture.SettleAsync(large);
        Assert.Equal(LogViewerViewModel.DefaultCopyAllCap, large.CopyAllCap);
    }

    [Fact]
    public async Task TheTooltip_StatesTheEffectiveCap()
    {
        using var fixture = new LogViewerFixture();
        using var page = fixture.Build(copyAllCap: 2_000);
        await LogViewerFixture.SettleAsync(page);

        Assert.Contains("2,000", page.CopyAllTooltip, StringComparison.Ordinal);
    }

    // ---- Verification B2 (steps 32 and 33): the cap is a live bound on the loaded set ---------
    //
    // Five shapes, one case each, all of them things the launched application got wrong: lowering
    // the cap did not trim, raising it did not re-enable paging, a Refresh at the cap offered
    // neither the button nor the sentence, the viewer held cap+1 lines with the button showing
    // beside the sentence, and Save log as wrote more lines than the viewer held (that last one is
    // in SaveLogAsTests). The caps here are inside the key's own 1000 to 500000 range, because the
    // editor refuses anything outside it and the launched application's caps were real ones.

    /// <summary>Entries with distinct timestamps: the reader pages by keyset.</summary>
    private static void WriteDeepLog(LogViewerFixture fixture, int count = 1100)
        => fixture.WriteLogFile(
            "galactilog-20260915.log",
            [.. Enumerable.Range(0, count).Select(i => LogViewerFixture.Entry(
                string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "10:{0:00}:{1:00}.000", i / 60, i % 60),
                message: "m" + i))]);

    private static async Task PageToTheCap(LogViewerViewModel page)
    {
        while (page.LoadMoreCommand.CanExecute(null))
        {
            page.LoadMoreCommand.Execute(null);
            await LogViewerFixture.SettleAsync(page);
        }
    }

    [Fact]
    public async Task TheViewer_HoldsExactlyTheCap_NeverAPartPageMore()
    {
        using var fixture = new LogViewerFixture();
        WriteDeepLog(fixture);

        // 1020 is not a multiple of the 50 line page size, so the last page would overflow it.
        using var page = await fixture.CreateAsync(initialAppLogMaxRows: 1020);
        await PageToTheCap(page);

        Assert.Equal(1020, page.Lines.Count);
    }

    [Fact]
    public async Task AtTheCap_TheSentenceReplacesTheButton_RatherThanJoiningIt()
    {
        using var fixture = new LogViewerFixture();
        WriteDeepLog(fixture);

        using var page = await fixture.CreateAsync(initialAppLogMaxRows: 1000);
        Assert.True(page.CanLoadMore);
        Assert.False(page.IsAtViewerCap);

        await PageToTheCap(page);

        Assert.Equal(1000, page.Lines.Count);
        Assert.True(page.IsAtViewerCap);
        Assert.False(page.CanLoadMore);
    }

    [Fact]
    public async Task LoweringTheCap_BelowTheLoadedCount_TrimsToItAtOnce()
    {
        using var fixture = new LogViewerFixture();
        WriteDeepLog(fixture);

        using var page = await fixture.CreateAsync(initialAppLogMaxRows: 1050);
        await PageToTheCap(page);
        Assert.Equal(1050, page.Lines.Count);

        page.MaxRows = 1000;

        Assert.Equal(1000, page.Lines.Count);
        Assert.True(page.IsAtViewerCap);
        Assert.False(page.CanLoadMore);
    }

    [Fact]
    public async Task RaisingTheCap_ReEnablesPaging_WithNoReload()
    {
        using var fixture = new LogViewerFixture();
        WriteDeepLog(fixture);

        using var page = await fixture.CreateAsync(initialAppLogMaxRows: 1000);
        await PageToTheCap(page);
        var loadsAtTheCap = page.LoadCount;
        Assert.Equal(1000, page.Lines.Count);
        Assert.False(page.CanLoadMore);

        page.MaxRows = 1050;

        // No reload was triggered by the edit itself: the next press is what fetches.
        Assert.Equal(loadsAtTheCap, page.LoadCount);
        Assert.True(page.CanLoadMore);
        Assert.False(page.IsAtViewerCap);

        page.LoadMoreCommand.Execute(null);
        await LogViewerFixture.SettleAsync(page);

        Assert.Equal(1050, page.Lines.Count);

        // And it resumed where it stopped rather than skipping the part page the cap dropped:
        // 1100 entries exist, m0 is the oldest, and the 1050 newest end at m50.
        Assert.Equal("m50", page.Lines[^1].Message);
    }

    [Fact]
    public async Task Refresh_AtTheCap_OffersTheButtonAgain_AndNotTheSentence()
    {
        using var fixture = new LogViewerFixture();
        WriteDeepLog(fixture);

        using var page = await fixture.CreateAsync(initialAppLogMaxRows: 1000);
        await PageToTheCap(page);
        Assert.True(page.IsAtViewerCap);

        await page.RefreshCommand.ExecuteAsync(null);
        await LogViewerFixture.SettleAsync(page);

        // Refresh re-reads page one, so the viewer is far below the cap again and the reader must
        // be offered exactly one of the two: the button, because there is more to read.
        Assert.Equal(50, page.Lines.Count);
        Assert.False(page.IsAtViewerCap);
        Assert.True(page.CanLoadMore);
    }

    // ---- Verification B2 residual (step 32) ---------------------------------------------------
    //
    // Both shapes are about a cap reached by LOWERING the value rather than by paging into it.
    // The launched application showed neither Load more nor the cap sentence there, and raising
    // the value again left Load more absent until a Refresh. Both cases were run red against the
    // tree before the fix.

    [Fact]
    public async Task LoweringTheCap_AtTheEndOfTheLog_StillSaysWhyTheRestIsNotShown()
    {
        using var fixture = new LogViewerFixture();

        // Small enough to page to the very end, so NextCursor is null when the cap is lowered.
        // That is the state the launched application was in and the one that showed nothing.
        WriteDeepLog(fixture, count: 120);

        using var page = await fixture.CreateAsync(initialAppLogMaxRows: 500_000);
        await PageToTheCap(page);
        Assert.Equal(120, page.Lines.Count);
        Assert.False(page.CanLoadMore);
        Assert.False(page.IsAtViewerCap);

        page.MaxRows = 1000;

        // 120 lines are loaded and the cap is 1000, so nothing is trimmed and nothing changes.
        Assert.Equal(120, page.Lines.Count);
        Assert.False(page.IsAtViewerCap);
    }

    [Fact]
    public async Task LoweringTheCap_BelowTheLoadedCount_OffersTheCapSentence_EvenAtTheEndOfTheLog()
    {
        using var fixture = new LogViewerFixture();
        WriteDeepLog(fixture, count: 1100);

        using var page = await fixture.CreateAsync(initialAppLogMaxRows: 500_000);
        await PageToTheCap(page);
        Assert.Equal(1100, page.Lines.Count);

        // The whole log is loaded, so there is no next cursor: paging reached the end.
        Assert.Null(page.NextCursor);
        Assert.False(page.IsAtViewerCap);
        Assert.False(page.CanLoadMore);

        page.MaxRows = 1000;

        // A hundred lines the reader had already fetched are gone, so there IS more to show than
        // the viewer holds. The sentence says so and the button stays away at the cap.
        Assert.Equal(1000, page.Lines.Count);
        Assert.True(page.IsAtViewerCap);
        Assert.False(page.CanLoadMore);
        Assert.NotNull(page.NextCursor);
    }

    [Fact]
    public async Task RaisingTheCap_AfterATrim_OffersLoadMoreWithNoRefresh_AndFetchesWhatWasTrimmed()
    {
        using var fixture = new LogViewerFixture();
        WriteDeepLog(fixture, count: 1100);

        using var page = await fixture.CreateAsync(initialAppLogMaxRows: 500_000);
        await PageToTheCap(page);
        Assert.Equal(1100, page.Lines.Count);
        var oldest = page.Lines[^1].Message;

        page.MaxRows = 1000;
        Assert.Equal(1000, page.Lines.Count);

        var loadsBeforeTheRaise = page.LoadCount;
        page.MaxRows = 5_000;

        // No Refresh: the button is offered by the raise itself.
        Assert.Equal(loadsBeforeTheRaise, page.LoadCount);
        Assert.True(page.CanLoadMore);
        Assert.True(page.LoadMoreCommand.CanExecute(null));
        Assert.False(page.IsAtViewerCap);

        await PageToTheCap(page);

        // And it fetched the trimmed lines back rather than re-reading page one: the list is whole
        // again, contiguous, and ends where it did before the trim.
        Assert.Equal(1100, page.Lines.Count);
        Assert.Equal(oldest, page.Lines[^1].Message);
        Assert.Equal(page.Lines.Count, page.Lines.Distinct().Count());
    }

    [Fact]
    public async Task PagingOlder_StopsAtTheCap()
    {
        using var fixture = new LogViewerFixture();
        var lines = Enumerable.Range(0, 220)
            .Select(i => LogViewerFixture.Entry(
                string.Format(System.Globalization.CultureInfo.InvariantCulture, "10:{0:00}:00.000", i % 60),
                message: "m" + i))
            .ToArray();
        fixture.WriteLogFile("galactilog-20260915.log", lines);

        // Page size is LogReader.DefaultLimit (50); a cap of 150 is exactly three pages.
        using var page = await fixture.CreateAsync(initialAppLogMaxRows: 150);
        Assert.Equal(50, page.Lines.Count);

        page.LoadMoreCommand.Execute(null);
        await LogViewerFixture.SettleAsync(page);
        Assert.Equal(100, page.Lines.Count);

        page.LoadMoreCommand.Execute(null);
        await LogViewerFixture.SettleAsync(page);
        Assert.Equal(150, page.Lines.Count);

        // The cap itself stops paging now, not the log running out (220 lines exist on disk).
        page.LoadMoreCommand.Execute(null);
        await LogViewerFixture.SettleAsync(page);
        Assert.Equal(150, page.Lines.Count);
    }

    [Fact]
    public async Task PagingOlder_StoppedAtTheCap_SaysWhy()
    {
        using var fixture = new LogViewerFixture();
        var lines = Enumerable.Range(0, 220)
            .Select(i => LogViewerFixture.Entry(
                string.Format(System.Globalization.CultureInfo.InvariantCulture, "10:{0:00}:00.000", i % 60),
                message: "m" + i))
            .ToArray();
        fixture.WriteLogFile("galactilog-20260915.log", lines);

        using var page = await fixture.CreateAsync(initialAppLogMaxRows: 100);
        Assert.Equal(50, page.Lines.Count);
        Assert.False(page.IsAtViewerCap);

        page.LoadMoreCommand.Execute(null);
        await LogViewerFixture.SettleAsync(page);

        Assert.Equal(100, page.Lines.Count);
        Assert.True(page.IsAtViewerCap);
    }

    // Phase 14B fixer, fixer list item 49 (task7-review P3). This used to set MaxRows and then
    // assert MaxRows, a setter tautology that would pass with the cap wired to nothing. The rule
    // its name promises is that a new cap binds the NEXT load with no restart, so the page is
    // paged until it stops at the stored cap, the cap is raised through the editor, and one more
    // Load more must append past the old bound. Both caps are inside the key's own 1000 to 500000
    // range, because the editor refuses anything outside it.
    [Fact]
    public async Task TheCap_TakesEffectOnTheNextLoad_WithNoRestart()
    {
        using var fixture = new LogViewerFixture();
        fixture.WriteLogFile(
            "galactilog-20260915.log",
            // Distinct timestamps: the reader pages by keyset, so 1100 entries sharing sixty
            // clock values do not page past the first few hundred.
            [.. Enumerable.Range(0, 1100).Select(i => LogViewerFixture.Entry(
                string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "10:{0:00}:{1:00}.000", i / 60, i % 60),
                message: "m" + i))]);

        using var page = await fixture.CreateAsync(initialAppLogMaxRows: 1000);
        while (page.LoadMoreCommand.CanExecute(null))
        {
            page.LoadMoreCommand.Execute(null);
            await LogViewerFixture.SettleAsync(page);
        }

        Assert.Equal(1000, page.Lines.Count);
        Assert.True(page.IsAtViewerCap);

        page.MaxRows = 1050;

        Assert.Equal(1050, page.MaxRows);
        Assert.False(page.IsAtViewerCap);
        Assert.True(page.LoadMoreCommand.CanExecute(null));

        page.LoadMoreCommand.Execute(null);
        await LogViewerFixture.SettleAsync(page);

        Assert.Equal(1050, page.Lines.Count);
    }

    [Fact]
    public async Task LoweringTheCap_TrimsTheOldestLoadedLines()
    {
        using var fixture = new LogViewerFixture();
        // Settled first: the constructor's own initial load (over an empty directory) must finish
        // and publish before the lines below are added directly, or its empty Publish races in
        // afterwards and clears them.
        using var page = await fixture.CreateAsync();

        // Lines is newest-first, and Add appends, so the newest synthetic entry (m1199) has to go
        // in at index 0 and the oldest (m0) last, matching what a real Publish would have left
        // behind. 1200 rows so a valid in-range cap (1000 to 500000) still has something to trim.
        for (var i = 1199; i >= 0; i--)
        {
            page.Lines.Add(LogLineViewModel.From(new LogLine(
                DateTimeOffset.UtcNow, LogLineLevel.Information, "src", "m" + i, null, i, "raw" + i)));
        }

        page.MaxRows = 5000;
        page.MaxRows = 1000;

        Assert.Equal(1000, page.Lines.Count);
        // The tail (oldest, since newest-first) is what was dropped: the thousand kept are the
        // newest thousand, "m1199" down to "m200".
        Assert.Equal("m1199", page.Lines[0].Message);
        Assert.Equal("m200", page.Lines[999].Message);
    }

    [Fact]
    public void TheCap_DoesNotResizeTheWarningRing()
    {
        // Departure 2 (task1-report.md): app_log_max_rows governs the viewer and the copy cap
        // only. LogRingBuffer's capacity is a fixed compile-time constant this task does not
        // touch; asserted here by construction rather than by reflection, since Capacity is not
        // exposed as a settable member anywhere in the solution (grep confirms no caller sets it).
        var ring = new LogRingBuffer();
        var template = new MessageTemplateParser().Parse("m");
        for (var i = 0; i < 600; i++)
        {
            ring.Emit(new LogEvent(
                DateTimeOffset.UtcNow, LogEventLevel.Warning, null, template, []));
        }

        Assert.Equal(500, ring.Snapshot().Count);
    }
}
