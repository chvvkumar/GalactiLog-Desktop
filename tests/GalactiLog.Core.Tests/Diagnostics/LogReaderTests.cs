using GalactiLog.Core.Diagnostics;
using Xunit;

namespace GalactiLog.Core.Tests.Diagnostics;

// Fixture files are created by the test in its own temp directory and deleted on dispose, the
// convention the scan suites use. Nothing in src/** writes them.
public class LogReaderTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "galactilog-logreader-" + Guid.NewGuid().ToString("N"));

    public LogReaderTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup only; a leftover temp file does not fail the test.
        }
    }

    // One entry exactly as spec 16.1's output template writes it.
    private static string Entry(
        string time, string level = "INF", string source = "GalactiLog.App.AppHost",
        string message = "Started")
        => $"2026-09-15 {time} +00:00 [{level}] {source} {message}";

    private string WriteFile(string name, params string[] lines)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
        return path;
    }

    private LogReader Reader() => new(_directory);

    [Fact]
    public void Page_ReturnsNewestFirst()
    {
        WriteFile("galactilog-20260914.log", Entry("09:00:00.000", message: "oldest"));
        WriteFile(
            "galactilog-20260915.log",
            Entry("10:00:00.000", message: "middle"),
            Entry("11:00:00.000", message: "newest"));

        var page = Reader().Page(new LogFilters());

        Assert.Equal(
            new[] { "newest", "middle", "oldest" },
            page.Lines.Select(line => line.Message));
    }

    [Fact]
    public void Page_AttachesAContinuationLineToThePrecedingEntry()
    {
        WriteFile(
            "galactilog-20260915.log",
            Entry("10:00:00.000", "ERR", message: "Ingest failed"),
            "System.IO.IOException: access denied",
            "   at GalactiLog.App.Program.Main()",
            Entry("10:00:01.000", message: "Continued"));

        var page = Reader().Page(new LogFilters());

        Assert.Equal(2, page.Lines.Count);

        var failure = Assert.Single(page.Lines, line => line.Message == "Ingest failed");
        Assert.NotNull(failure.Exception);
        Assert.Contains("System.IO.IOException: access denied", failure.Exception);
        Assert.Contains("at GalactiLog.App.Program.Main()", failure.Exception);
        Assert.Contains("at GalactiLog.App.Program.Main()", failure.Raw);

        // The continuation lines belong to the entry that threw, never to the one after it.
        var continued = Assert.Single(page.Lines, line => line.Message == "Continued");
        Assert.Null(continued.Exception);
    }

    [Fact]
    public void Page_WithACursor_ReturnsOnlyOlderEntries()
    {
        WriteFile(
            "galactilog-20260915.log",
            Entry("10:00:00.000", message: "a"),
            Entry("11:00:00.000", message: "b"),
            Entry("12:00:00.000", message: "c"));

        var reader = Reader();
        var first = reader.Page(new LogFilters(), limit: 2);

        Assert.Equal(new[] { "c", "b" }, first.Lines.Select(line => line.Message));
        Assert.NotNull(first.NextCursor);

        var second = reader.Page(new LogFilters(), first.NextCursor, limit: 2);

        Assert.Equal(new[] { "a" }, second.Lines.Select(line => line.Message));
    }

    [Fact]
    public void Page_WithACursorOnATiedTimestamp_UsesTheOrdinalTiebreaker()
    {
        // Two entries at the identical millisecond. A cursor on the timestamp alone would either
        // lose one of them or return it twice; the pair is what makes paging total. This is the
        // assertion ActivityCursor's XML doc exists for.
        WriteFile(
            "galactilog-20260915.log",
            Entry("10:00:00.000", message: "tied-first"),
            Entry("10:00:00.000", message: "tied-second"),
            Entry("11:00:00.000", message: "newest"));

        var reader = Reader();
        var seen = new List<string>();
        LogCursor? cursor = null;

        for (var round = 0; round < 3; round++)
        {
            var page = reader.Page(new LogFilters(), cursor, limit: 1);
            seen.AddRange(page.Lines.Select(line => line.Message));
            cursor = page.NextCursor;
            if (cursor is null)
            {
                break;
            }
        }

        Assert.Equal(new[] { "newest", "tied-second", "tied-first" }, seen);
        Assert.Equal(3, seen.Distinct().Count());
    }

    [Fact]
    public void Page_SetsNextCursor_OnlyWhenThePageCameBackFull()
    {
        WriteFile(
            "galactilog-20260915.log",
            Entry("10:00:00.000", message: "a"),
            Entry("11:00:00.000", message: "b"));

        var reader = Reader();

        Assert.NotNull(reader.Page(new LogFilters(), limit: 2).NextCursor);
        Assert.Null(reader.Page(new LogFilters(), limit: 3).NextCursor);
    }

    [Fact]
    public void Page_ClampsTheLimitToMaxLimit()
    {
        var lines = Enumerable
            .Range(0, LogReader.MaxLimit + 50)
            .Select(index => Entry(
                string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "10:{0:00}:{1:00}.000",
                    index / 60,
                    index % 60),
                message: "m" + index))
            .ToArray();
        WriteFile("galactilog-20260915.log", lines);

        var page = Reader().Page(new LogFilters(), limit: 10_000);

        Assert.Equal(LogReader.MaxLimit, page.Lines.Count);
    }

    [Fact]
    public void Page_ClampsTheLimitToMinLimit()
    {
        WriteFile(
            "galactilog-20260915.log",
            Entry("10:00:00.000", message: "a"),
            Entry("11:00:00.000", message: "b"));

        var page = Reader().Page(new LogFilters(), limit: 0);

        // MinLimit is 1, so a limit of 0 comes back with one entry rather than none.
        Assert.Equal(1, LogReader.MinLimit);
        Assert.Single(page.Lines);
    }

    [Fact]
    public void Page_MinimumLevelFilter_NarrowsTheList()
    {
        WriteFile(
            "galactilog-20260915.log",
            Entry("10:00:00.000", "VRB", message: "verbose"),
            Entry("10:00:01.000", "DBG", message: "debug"),
            Entry("10:00:02.000", "INF", message: "information"),
            Entry("10:00:03.000", "WRN", message: "warning"),
            Entry("10:00:04.000", "ERR", message: "error"),
            Entry("10:00:05.000", "FTL", message: "fatal"));

        var reader = Reader();

        Assert.Equal(6, reader.Page(new LogFilters(LogLineLevel.Verbose)).Lines.Count);
        Assert.Equal(
            new[] { "fatal", "error", "warning" },
            reader.Page(new LogFilters(LogLineLevel.Warning)).Lines.Select(line => line.Message));
        Assert.Equal(
            new[] { "fatal" },
            reader.Page(new LogFilters(LogLineLevel.Fatal)).Lines.Select(line => line.Message));
    }

    [Fact]
    public void Page_Search_IsCaseInsensitiveOverTheMessageOnly()
    {
        WriteFile(
            "galactilog-20260915.log",
            Entry("10:00:00.000", source: "GalactiLog.App.Needle", message: "in the source context"),
            Entry("10:00:01.000", "ERR", message: "threw"),
            "System.IO.IOException: needle in the exception",
            Entry("10:00:02.000", message: "A NEEDLE in the message"));

        var page = Reader().Page(new LogFilters(Search: "needle"));

        // Message only, matching logs.py's AppLog.message.ilike: not the source context, not the
        // exception text.
        var found = Assert.Single(page.Lines);
        Assert.Equal("A NEEDLE in the message", found.Message);
    }

    [Fact]
    public void Page_WalksIntoAnOlderFile_WhenTheNewestDoesNotFillThePage()
    {
        WriteFile(
            "galactilog-20260914.log",
            Entry("09:00:00.000", message: "old-a"),
            Entry("09:00:01.000", message: "old-b"));
        WriteFile("galactilog-20260915.log", Entry("10:00:00.000", message: "new-a"));

        var page = Reader().Page(new LogFilters(), limit: 3);

        Assert.Equal(new[] { "new-a", "old-b", "old-a" }, page.Lines.Select(line => line.Message));
    }

    [Fact]
    public void Page_ReachedOldest_IsTrueWhenEveryFileIsExhausted()
    {
        WriteFile(
            "galactilog-20260915.log",
            Entry("10:00:00.000", message: "a"),
            Entry("11:00:00.000", message: "b"));

        var reader = Reader();

        Assert.True(reader.Page(new LogFilters(), limit: 10).ReachedOldest);
        Assert.False(reader.Page(new LogFilters(), limit: 2).ReachedOldest);
    }

    [Fact]
    public void Page_SkipsAFileThatCannotBeOpened_AndContinues()
    {
        var locked = WriteFile("galactilog-20260915.log", Entry("10:00:00.000", message: "locked"));
        WriteFile("galactilog-20260914.log", Entry("09:00:00.000", message: "readable"));

        // FileShare.None: no other handle may open it at all, which is what a backup tool holding
        // the file looks like.
        using var exclusive = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        var page = Reader().Page(new LogFilters());

        Assert.Equal(new[] { "readable" }, page.Lines.Select(line => line.Message));
    }

    [Fact]
    public void Page_ReadsAFileAnotherHandleHoldsOpenForWriting()
    {
        var path = WriteFile("galactilog-20260915.log", Entry("10:00:00.000", message: "live"));

        // The share mode the live Serilog sink uses. UserFiles.OpenRead asks for
        // FileShare.ReadWrite, which is exactly what makes this readable.
        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);

        var page = Reader().Page(new LogFilters());

        Assert.Equal(new[] { "live" }, page.Lines.Select(line => line.Message));
    }

    [Fact]
    public void Page_DropsALeadingPartialEntry_WithoutThrowing()
    {
        WriteFile(
            "galactilog-20260915.log",
            "   at GalactiLog.App.Program.Main()",
            "System.IO.IOException: the tail of an entry this reader never saw the head of",
            Entry("10:00:00.000", message: "whole"));

        var page = Reader().Page(new LogFilters());

        var found = Assert.Single(page.Lines);
        Assert.Equal("whole", found.Message);
        Assert.Null(found.Exception);
    }

    [Fact]
    public void Page_OnAMissingDirectory_ReturnsAnEmptyPage()
    {
        var reader = new LogReader(Path.Combine(_directory, "not-created"));

        var page = reader.Page(new LogFilters());

        Assert.Empty(page.Lines);
        Assert.Null(page.NextCursor);
        Assert.True(page.ReachedOldest);
    }

    [Fact]
    public void All_RespectsTheCap()
    {
        WriteFile(
            "galactilog-20260915.log",
            [.. Enumerable.Range(0, 10).Select(index => Entry(
                string.Format(
                    System.Globalization.CultureInfo.InvariantCulture, "10:00:{0:00}.000", index),
                message: "m" + index))]);

        var lines = Reader().All(new LogFilters(), cap: 3);

        Assert.Equal(new[] { "m9", "m8", "m7" }, lines.Select(line => line.Message));
    }

    [Fact]
    public void All_AppliesTheSameFiltersAsPage()
    {
        WriteFile(
            "galactilog-20260915.log",
            Entry("10:00:00.000", "INF", message: "keep needle"),
            Entry("10:00:01.000", "WRN", message: "drop by search"),
            Entry("10:00:02.000", "DBG", message: "drop needle by level"));

        var filters = new LogFilters(LogLineLevel.Information, "NEEDLE");
        var reader = Reader();

        var all = reader.All(filters, cap: 100);
        var page = reader.Page(filters);

        Assert.Equal(new[] { "keep needle" }, all.Select(line => line.Message));
        Assert.Equal(page.Lines.Select(line => line.Message), all.Select(line => line.Message));
    }
}
