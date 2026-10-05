using System.Text.Json;
using System.Text.RegularExpressions;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Ingest;
using Xunit;

namespace GalactiLog.Data.Tests.Ingest;

// Spec 10.3's two orphan guards, applied to phd2_logs rows through the FRAME SIDE'S OWN
// arithmetic rather than a second copy of it (spec 10.3: "OrphanPruner already holds known,
// missing, the missing >= max(1, known) * 0.5 test and the override rule, and the guide-log pass
// calls that shared code with phd2_logs rows in place of images rows").
//
// Every case here seeds its stored rows so that the surviving candidates match on size and mtime
// and are therefore never opened: the guard is what is under test, not the parser.
public class Phd2OrphanGuardTests : IDisposable
{
    private readonly TestDatabaseHandle _db = TestDatabaseFactory.CreateMigratedDatabase();
    private readonly string _rootA = Directory.CreateTempSubdirectory("galactilog-phd2orphan-a-").FullName;
    private readonly string _rootB = Directory.CreateTempSubdirectory("galactilog-phd2orphan-b-").FullName;

    public void Dispose()
    {
        _db.Dispose();
        foreach (var root in new[] { _rootA, _rootB })
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { /* best effort */ }
        }

        GC.SuppressFinalize(this);
    }

    // --- harness --------------------------------------------------------------------------

    // A guide log that exists on disk, with a stored row whose size and mtime match it exactly.
    // The delta skip therefore never opens it and it is a DISCOVERED path for the guard.
    private DiscoveredFile Present(string root, string name)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, "PHD2 version, Log version 2.5. Log enabled at 2025-03-19 21:30:00\n");
        var info = new FileInfo(path);
        var candidate = new DiscoveredFile(
            path, info.Length, (info.LastWriteTimeUtc - DateTime.UnixEpoch).TotalSeconds);
        StoreRow(candidate.Path, candidate.FileSize, candidate.FileMtimeUnixSeconds);
        return candidate;
    }

    // A stored row whose file is gone: an orphan candidate.
    private string Missing(string root, string name)
    {
        var path = Path.Combine(root, name);
        StoreRow(path, 1, 0);
        return path;
    }

    private void StoreRow(string path, long size, double mtime)
    {
        using var context = new GalactiLogContext(
            GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true));
        context.Phd2Logs.Add(new Phd2Log
        {
            Id = Guid.NewGuid(),
            FilePath = path,
            FileSize = size,
            FileMtime = mtime,
            ParseStatus = "ok",
            ParsedAt = DateTime.UtcNow,
        });
        context.SaveChanges();
    }

    private Phd2PassResult Run(
        IReadOnlyList<DiscoveredFile> candidates, IReadOnlyList<string> walkedRoots, bool force)
        => Phd2Ingest.Run(
            _db.ConnectionString, candidates,
            new GeneralSettings { Phd2ScanEnabled = true, ScanFilters = ScanFilterConfig.Empty },
            walkedRoots, force, parentActivityId: null, report: null, warn: null,
            CancellationToken.None);

    private GalactiLogContext OpenRead() => new(GalactiLogContextOptions.Create(_db.ConnectionString));

    private static JsonElement Details(GalactiLogContext context, string eventType)
    {
        var row = context.ActivityEvents.Single(e => e.EventType == eventType);
        Assert.Equal("scan", row.Category);
        Assert.Equal("warning", row.Severity);
        return JsonDocument.Parse(row.Details!).RootElement.Clone();
    }

    private List<string> StoredPaths()
    {
        using var context = OpenRead();
        return context.Phd2Logs.Select(row => row.FilePath).ToList();
    }

    // --- 15: the zero-discovery guard is absolute ------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroDiscoveryUnderARootHoldingRows_KeepsEveryRow_ForcedOrNot(bool force)
    {
        Missing(_rootA, "PHD2_GuideLog_one.txt");
        Missing(_rootA, "PHD2_GuideLog_two.txt");

        var result = Run([], [_rootA], force);

        // A failure here means an unplugged drive empties the guiding half of the catalogue on
        // the next scan, which is irreversible without a re-scan of a drive that is not there.
        // The override reaches guard 2 only and never this one.
        Assert.Equal(0, result.Removed);
        Assert.Equal(2, StoredPaths().Count);

        using var context = OpenRead();
        var skipped = Details(context, "phd2_orphan_prune_skipped");
        Assert.Equal(_rootA, skipped.GetProperty("root").GetString());
        Assert.Equal(2, skipped.GetProperty("known_rows").GetInt32());
        Assert.DoesNotContain(
            context.ActivityEvents.ToList(), e => e.EventType == "phd2_orphan_prune_forced");
    }

    // --- 16 and 17: the 50 percent limit, and the override that lifts it only ----------------

    [Fact]
    public void MissingRowsAtExactlyHalf_AreLimitedAndNothingIsDeleted()
    {
        var present = new[] { Present(_rootA, "a.txt"), Present(_rootA, "b.txt") };
        Missing(_rootA, "gone1.txt");
        Missing(_rootA, "gone2.txt");

        var result = Run(present, [_rootA], force: false);

        // Exactly 2 of 4, which is exactly max(1, known) * SafetyLimitFraction. A failure here
        // means `>` in place of `>=`, so exactly half a root vanishing is deleted silently.
        Assert.Equal(0, result.Removed);
        Assert.Equal(4, StoredPaths().Count);

        using var context = OpenRead();
        var limited = Details(context, "phd2_orphan_prune_limited");
        Assert.Equal(_rootA, limited.GetProperty("root").GetString());
        Assert.Equal(2, limited.GetProperty("missing_rows").GetInt32());
        Assert.Equal(4, limited.GetProperty("known_rows").GetInt32());
    }

    [Fact]
    public void TheSameRootWithTheOverrideOn_DeletesAndWritesTheForcedEvent()
    {
        var present = new[] { Present(_rootA, "a.txt"), Present(_rootA, "b.txt") };
        Missing(_rootA, "gone1.txt");
        Missing(_rootA, "gone2.txt");

        var result = Run(present, [_rootA], force: true);

        Assert.Equal(2, result.Removed);
        Assert.Equal(present.Select(f => f.Path).Order(), StoredPaths().Order());

        using var context = OpenRead();
        var forced = Details(context, "phd2_orphan_prune_forced");
        Assert.Equal(_rootA, forced.GetProperty("root").GetString());
        Assert.Equal(2, forced.GetProperty("removed").GetInt32());
        Assert.Equal(4, forced.GetProperty("known_rows").GetInt32());
        Assert.Equal(JsonValueKind.Number, forced.GetProperty("percent_removed").ValueKind);
        Assert.Equal(50.0, forced.GetProperty("percent_removed").GetDouble());
        Assert.True(forced.GetProperty("forced").GetBoolean());
        Assert.DoesNotContain(
            context.ActivityEvents.ToList(), e => e.EventType == "phd2_orphan_prune_limited");
    }

    // --- 18: one dead root does not stop a healthy one --------------------------------------

    [Fact]
    public void ADeadRootAndAHealthyRoot_AreDecidedIndependentlyInOneRun()
    {
        // The healthy root: 4 known, 1 missing, which is under the limit.
        var healthy = new[]
        {
            Present(_rootB, "a.txt"), Present(_rootB, "b.txt"), Present(_rootB, "c.txt"),
        };
        var healthyOrphan = Missing(_rootB, "gone.txt");

        // The dead root: rows, nothing discovered.
        Missing(_rootA, "dead1.txt");
        Missing(_rootA, "dead2.txt");

        var result = Run(healthy, [_rootA, _rootB], force: false);

        // A failure here means one unreachable share stops cleanup everywhere, which is the
        // corpus-wide behaviour spec 7.6's departures list says the port does not have.
        Assert.Equal(1, result.Removed);
        var stored = StoredPaths();
        Assert.DoesNotContain(healthyOrphan, stored);

        // Six rows were seeded (3 present and 1 missing under the healthy root, 2 under the dead
        // one) and exactly the healthy root's one orphan went.
        Assert.Equal(5, stored.Count);

        using var context = OpenRead();
        var skipped = Details(context, "phd2_orphan_prune_skipped");
        Assert.Equal(_rootA, skipped.GetProperty("root").GetString());
    }

    // --- 19: the arithmetic is shared, not copied --------------------------------------------

    [Fact]
    public void TheBoundaryIsOrphanPrunersOwnConstant()
    {
        // The boundary is derived from the constant, not written out again here: if the frame
        // side's fraction ever moves, this case moves with it and the guide-log side has to
        // agree, which is the whole point of sharing the planner.
        const int known = 4;
        var missingAtBoundary = (int)Math.Ceiling(known * OrphanPruner.SafetyLimitFraction);
        var present = Enumerable.Range(0, known - missingAtBoundary)
            .Select(i => Present(_rootA, $"present{i}.txt"))
            .ToList();
        for (var i = 0; i < missingAtBoundary; i++)
        {
            Missing(_rootA, $"gone{i}.txt");
        }

        var atBoundary = Run(present, [_rootA], force: false);
        Assert.Equal(0, atBoundary.Removed);

        // One fewer missing row is below the boundary and is deleted, so the assertion above is
        // a boundary and not a blanket refusal.
        var belowRoot = Directory.CreateTempSubdirectory("galactilog-phd2orphan-c-").FullName;
        try
        {
            var below = Enumerable.Range(0, known - missingAtBoundary + 1)
                .Select(i => Present(belowRoot, $"present{i}.txt"))
                .ToList();
            for (var i = 0; i < missingAtBoundary - 1; i++)
            {
                Missing(belowRoot, $"gone{i}.txt");
            }

            Assert.Equal(missingAtBoundary - 1, Run(below, [belowRoot], force: false).Removed);
        }
        finally
        {
            try { Directory.Delete(belowRoot, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public void Phd2Ingest_ContainsNoSecondCopyOfTheGuardArithmetic()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "GalactiLog.Data", "Ingest", "Phd2Ingest.cs"));

        // Comments carry the arithmetic in prose deliberately, so they are stripped before the
        // scan; what must not appear is a second implementation of it in code.
        var code = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
        code = Regex.Replace(code, @"///[^\r\n]*", "");
        code = Regex.Replace(code, @"//[^\r\n]*", "");

        // A failure here is a copied constant that is correct on the day it is written and
        // drifts the first time one side is tuned, which is the exact failure design lesson 1
        // names.
        Assert.DoesNotContain("0.5", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.Max(1,", code, StringComparison.Ordinal);
        Assert.DoesNotContain("SafetyLimitFraction", code, StringComparison.Ordinal);

        // And it does reach the shared planner.
        Assert.Contains("OrphanPruner.Plan(", code, StringComparison.Ordinal);
    }

    // --- a root this run did not walk prunes nothing -----------------------------------------

    [Fact]
    public void ARootThisRunDidNotWalk_PrunesNothing()
    {
        Missing(_rootA, "PHD2_GuideLog_one.txt");
        var present = new[] { Present(_rootB, "b.txt") };

        // Only root B was walked, so root A's rows are not even considered, exactly as the frame
        // side leaves rows under a root that left the configuration alone.
        var result = Run(present, [_rootB], force: true);

        Assert.Equal(0, result.Removed);
        Assert.Equal(2, StoredPaths().Count);
        using var context = OpenRead();
        Assert.DoesNotContain(
            context.ActivityEvents.ToList(),
            e => e.EventType.StartsWith("phd2_orphan_prune_", StringComparison.Ordinal));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GalactiLog.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("GalactiLog.sln was not found above the test assembly.");
    }
}
