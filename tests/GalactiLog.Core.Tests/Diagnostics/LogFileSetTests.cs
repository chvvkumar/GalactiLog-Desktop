using GalactiLog.Core.Diagnostics;
using Xunit;

namespace GalactiLog.Core.Tests.Diagnostics;

// Fixture files are created by the test in its own temp directory and deleted on dispose, the
// convention the scan suites use. Nothing in src/** writes them.
public class LogFileSetTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "galactilog-logfileset-" + Guid.NewGuid().ToString("N"));

    public LogFileSetTests() => Directory.CreateDirectory(_directory);

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

    private string Write(string name, DateTime? lastWriteUtc = null)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, "2026-09-15 12:34:56.789 +00:00 [INF] Source A line" + Environment.NewLine);
        if (lastWriteUtc is { } stamp)
        {
            File.SetLastWriteTimeUtc(path, stamp);
        }

        return path;
    }

    // ---- Verification E1: which file the sink holds open -------------------------------------
    //
    // Clear log deletes everything Live does not name, so this is a delete rule and it must not
    // depend on the order of the list it is handed.

    [Fact]
    public void Live_IsTheSameFile_WhateverOrderTheListArrivesIn()
    {
        var older = Write("galactilog-20260914.log");
        var yesterday = Write("galactilog-20260915.log");
        var open = Write("galactilog-20260916.log");

        string[] forward = [open, yesterday, older];
        string[] reversed = [older, yesterday, open];
        string[] shuffled = [yesterday, open, older];

        Assert.Equal(open, LogFileSet.Live(forward));
        Assert.Equal(open, LogFileSet.Live(reversed));
        Assert.Equal(open, LogFileSet.Live(shuffled));
    }

    [Fact]
    public void Live_WithinADay_IsTheHighestSequence_ComparedAsANumber()
    {
        var bare = Write("galactilog-20260916.log");
        var first = Write("galactilog-20260916_001.log");
        var ninth = Write("galactilog-20260916_009.log");
        var tenth = Write("galactilog-20260916_010.log");

        // Reversed on purpose: the answer is the name's own sequence, not the position.
        Assert.Equal(tenth, LogFileSet.Live([bare, first, ninth, tenth]));
        Assert.Equal(tenth, LogFileSet.Live([tenth, ninth, first, bare]));

        // And past Serilog's three-digit zero padding, where an ordinal text sort stops agreeing
        // with a numeric one: _1000 is newer than _999.
        var thousandth = Write("galactilog-20260916_1000.log");
        var last = Write("galactilog-20260916_999.log");
        Assert.Equal(thousandth, LogFileSet.Live([last, thousandth]));
    }

    [Fact]
    public void Live_WithNoParseableName_FallsBackToTheNewestWrite_NotToThePosition()
    {
        var old = Write("galactilog-restored.log", new DateTime(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc));
        var recent = Write("galactilog-copy.log", new DateTime(2026, 9, 16, 8, 0, 0, DateTimeKind.Utc));

        Assert.Equal(recent, LogFileSet.Live([old, recent]));
        Assert.Equal(recent, LogFileSet.Live([recent, old]));
    }

    [Fact]
    public void Live_PrefersAParseableName_OverAnUnparseableOneWrittenLater()
    {
        var stamped = Write("galactilog-20260916.log", new DateTime(2026, 9, 16, 8, 0, 0, DateTimeKind.Utc));
        Write("galactilog-copy.log", new DateTime(2026, 9, 17, 8, 0, 0, DateTimeKind.Utc));

        // The sink's own naming wins: a file somebody dropped in the directory is not the one the
        // sink holds open, however recently it was touched.
        Assert.Equal(stamped, LogFileSet.Live([.. Directory.GetFiles(_directory)]));
    }

    [Fact]
    public void Live_OnAnEmptySet_IsNull() => Assert.Null(LogFileSet.Live([]));

    [Fact]
    public void Newest_OrdersByNameDescending_NotByLastWriteTime()
    {
        // The names and the timestamps disagree: the older-named file was written last, which is
        // what a copy or a restore does. The name has to win.
        Write("galactilog-20260914.log", new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Write("galactilog-20260915.log", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var files = LogFileSet.Newest(_directory);

        Assert.Equal(
            new[] { "galactilog-20260915.log", "galactilog-20260914.log" },
            files.Select(Path.GetFileName));
    }

    [Fact]
    public void Newest_PutsAHigherWithinDaySequenceFirst()
    {
        Write("galactilog-20260915.log");
        Write("galactilog-20260915_001.log");
        Write("galactilog-20260915_002.log");

        var files = LogFileSet.Newest(_directory);

        Assert.Equal(
            new[]
            {
                "galactilog-20260915_002.log",
                "galactilog-20260915_001.log",
                "galactilog-20260915.log",
            },
            files.Select(Path.GetFileName));
    }

    [Fact]
    public void Newest_IgnoresANonMatchingFile()
    {
        Write("galactilog-20260915.log");
        Write("notes.txt");
        Write("galactilog-20260915.log.bak");
        Write("other-20260915.log");

        var files = LogFileSet.Newest(_directory);

        Assert.Equal(new[] { "galactilog-20260915.log" }, files.Select(Path.GetFileName));
    }

    [Fact]
    public void Newest_OnAMissingDirectory_ReturnsEmpty()
    {
        var missing = Path.Combine(_directory, "not-created");

        Assert.Empty(LogFileSet.Newest(missing));
    }
}
