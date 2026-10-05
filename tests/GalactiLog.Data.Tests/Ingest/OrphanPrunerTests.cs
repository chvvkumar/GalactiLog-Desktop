using System.Text.RegularExpressions;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Ingest;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GalactiLog.Data.Tests.Ingest;

// Spec 10.3 step 4 and spec 18.1's "Orphan pruning" row.
//
// No test here needs a file on disk: OrphanPruner never touches the filesystem, and its
// "what is still there" input is the caller-supplied discovered set. That is the point --
// see Prune_ReferencesNoWriteCapableFileSystemApi.
//
// Phase 14B Task 5 added spec 10.3's second guard, the 50 percent safety limit, which four cases
// here tripped by accident: they seeded two rows and made one of them missing, which is exactly
// half and therefore limited. Each of those four keeps its own subject and now seeds a set whose
// missing fraction is below the limit. The limit's own cases are in OrphanSafetyLimitTests.
public class OrphanPrunerTests : IDisposable
{
    private readonly TestDatabaseHandle _db = TestDatabaseFactory.CreateMigratedDatabase();

    // Never created on disk. These are path strings and nothing else.
    private static readonly string RootA = Path.Combine(Path.GetTempPath(), "galactilog-orphan-a");
    private static readonly string RootB = Path.Combine(Path.GetTempPath(), "galactilog-orphan-b");

    public void Dispose() => _db.Dispose();

    private GalactiLogContext OpenRead() => new(GalactiLogContextOptions.Create(_db.ConnectionString));

    private GalactiLogContext OpenWrite() => new(GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true));

    private void SeedImages(params string[] paths)
    {
        using var context = OpenWrite();
        foreach (var path in paths)
        {
            context.Images.Add(new Image
            {
                Id = Guid.NewGuid(),
                FilePath = path,
                FileName = Path.GetFileName(path),
            });
        }
        context.SaveChanges();
    }

    private List<string> CataloguedPaths()
    {
        using var context = OpenRead();
        return context.Images.Select(i => i.FilePath).OrderBy(p => p).ToList();
    }

    [Fact]
    public void Prune_FileMissingFromDisk_RowDeleted()
    {
        // Three kept to one gone: 25 percent missing, below the safety limit, so this case stays
        // about the deletion itself (Phase 14B Task 5).
        var kept = Path.Combine(RootA, "here.fits");
        var kept2 = Path.Combine(RootA, "here2.fits");
        var kept3 = Path.Combine(RootA, "here3.fits");
        var gone = Path.Combine(RootA, "gone.fits");
        SeedImages(kept, kept2, kept3, gone);

        using var context = OpenWrite();
        var result = OrphanPruner.Prune(context, [RootA], [kept, kept2, kept3]);

        Assert.Equal(1, result.Removed);
        Assert.Empty(result.SkippedRoots);
        Assert.Empty(result.Limited);
        Assert.Equal(new[] { kept, kept2, kept3 }.Order().ToArray(), CataloguedPaths());
    }

    [Fact]
    public void Prune_FileStillOnDisk_RowKept()
    {
        var a = Path.Combine(RootA, "a.fits");
        var b = Path.Combine(RootA, "sub", "b.fits");
        SeedImages(a, b);

        using var context = OpenWrite();
        var result = OrphanPruner.Prune(context, [RootA], [a, b]);

        Assert.Equal(0, result.Removed);
        Assert.Equal(0, result.Candidates);
        Assert.Equal(2, CataloguedPaths().Count);
    }

    // Review item 3: Candidates is the denominator the prune_orphans progress event reports
    // against. Rows under a root the zero-discovery guard skipped were never candidates for
    // deletion, so they must not inflate it.
    [Fact]
    public void Prune_Candidates_CountsRowsFoundMissing_NotRowsUnderASkippedRoot()
    {
        // RootB keeps three rows to one orphan, below the 50 percent safety limit, so the case
        // stays about what the skipped root contributes to Candidates (Phase 14B Task 5).
        var onDeadShare = Path.Combine(RootA, "unreachable.fits");
        var stillThere = Path.Combine(RootB, "b.fits");
        var stillThere2 = Path.Combine(RootB, "b2.fits");
        var stillThere3 = Path.Combine(RootB, "b3.fits");
        var orphaned = Path.Combine(RootB, "gone.fits");
        SeedImages(onDeadShare, stillThere, stillThere2, stillThere3, orphaned);

        using var context = OpenWrite();
        var result = OrphanPruner.Prune(context, [RootA, RootB], [stillThere, stillThere2, stillThere3]);

        Assert.Equal(1, result.Candidates);
        Assert.Equal(result.Candidates, result.Removed);
        Assert.Single(result.SkippedRoots);
    }

    // Spec 10.3 step 4: "Rows under a root that was removed from configuration are left
    // alone; deleting them would lose data the moment a user temporarily unplugs a drive."
    [Fact]
    public void Prune_RowUnderUnconfiguredRoot_NeverConsidered()
    {
        var configured = Path.Combine(RootA, "a.fits");
        var unconfigured = Path.Combine(RootB, "old.fits");
        SeedImages(configured, unconfigured);

        // RootB is not in the walked list at all -- the user removed it from general.scan_roots.
        using var context = OpenWrite();
        var result = OrphanPruner.Prune(context, [RootA], [configured]);

        Assert.Equal(0, result.Removed);
        Assert.Empty(result.SkippedRoots);
        Assert.Contains(unconfigured, CataloguedPaths());
    }

    // A path is under a root only at a directory boundary: "...orphan-a-archive" is a
    // different root from "...orphan-a", not a child of it.
    [Fact]
    public void Prune_SiblingRootSharingAPathPrefix_IsNotTreatedAsUnderTheRoot()
    {
        var inside = Path.Combine(RootA, "a.fits");
        var sibling = Path.Combine(RootA + "-archive", "old.fits");
        SeedImages(inside, sibling);

        using var context = OpenWrite();
        var result = OrphanPruner.Prune(context, [RootA], [inside]);

        Assert.Equal(0, result.Removed);
        Assert.Contains(sibling, CataloguedPaths());
    }

    // The zero-discovery guard is per root: an unmounted share must not empty the catalogue
    // for THAT root, while a healthy root's orphans are still cleaned up in the same run.
    [Fact]
    public void Prune_ZeroDiscoveryForARootWithKnownRows_SkipsThatRootAndWarns_OtherRootsStillPruned()
    {
        // RootB keeps three rows to one orphan, below the 50 percent safety limit, so the case
        // stays about the zero-discovery guard (Phase 14B Task 5).
        var onDeadShare = Path.Combine(RootA, "unreachable.fits");
        var stillThere = Path.Combine(RootB, "b.fits");
        var stillThere2 = Path.Combine(RootB, "b2.fits");
        var stillThere3 = Path.Combine(RootB, "b3.fits");
        var orphaned = Path.Combine(RootB, "gone.fits");
        SeedImages(onDeadShare, stillThere, stillThere2, stillThere3, orphaned);

        using var context = OpenWrite();
        var result = OrphanPruner.Prune(context, [RootA, RootB], [stillThere, stillThere2, stillThere3]);

        Assert.Equal(1, result.Removed);
        var skipped = Assert.Single(result.SkippedRoots);
        Assert.Equal(Path.GetFullPath(RootA), skipped.Root);
        Assert.Equal(1, skipped.KnownRows);
        Assert.Equal(
            new[] { onDeadShare, stillThere, stillThere2, stillThere3 }.Order().ToArray(),
            CataloguedPaths());
    }

    // A configured root with no rows under it is not a "possible unmounted share"; there is
    // nothing to lose and nothing to warn about.
    [Fact]
    public void Prune_NoKnownRowsUnderARoot_NeverWarns()
    {
        var a = Path.Combine(RootA, "a.fits");
        SeedImages(a);

        using var context = OpenWrite();
        var result = OrphanPruner.Prune(context, [RootA, RootB], [a]);

        Assert.Equal(0, result.Removed);
        Assert.Empty(result.SkippedRoots);
    }

    // Before migration 0003, images.file_path was uniquely indexed BINARY and two rows
    // differing only in case were legal. 0003's NOCASE collation stops new pairs being
    // created, but a database written before it can still hold one, and a case-only variant
    // discovered on disk must never be read as "the other one is gone". The unique index is
    // dropped here to reproduce exactly that legacy shape. Matches how ScanCoordinator builds
    // its known-file map (OrdinalIgnoreCase).
    [Fact]
    public void Prune_DiscoveredPathDifferingOnlyByCase_IsNotAnOrphan()
    {
        var lower = Path.Combine(RootA, "a.fits").ToLowerInvariant();
        var upper = Path.Combine(RootA, "a.fits").ToUpperInvariant();
        using (var legacy = OpenWrite())
        {
            legacy.Database.ExecuteSqlRaw("DROP INDEX IX_images_file_path");
        }
        SeedImages(lower, upper);

        using var context = OpenWrite();
        var result = OrphanPruner.Prune(context, [RootA], [lower]);

        Assert.Equal(0, result.Removed);
        Assert.Equal(2, CataloguedPaths().Count);
    }

    // The deletes are chunked to bound the generated IN (...) list; the tail batch must not
    // be dropped, and SQLite's 999-parameter default limit must not be hit.
    [Fact]
    public void Prune_BatchesDeletesOver500Rows()
    {
        // 640 survivors to 620 orphans: 620 of 1260 is below max(1, 1260) * 0.5, so the batching
        // is what this case exercises rather than the safety limit (Phase 14B Task 5).
        var survivors = Enumerable.Range(0, 640)
            .Select(i => Path.Combine(RootA, $"survivor{i:0000}.fits")).ToArray();
        var orphans = Enumerable.Range(0, 620).Select(i => Path.Combine(RootA, $"gone{i:0000}.fits")).ToArray();
        SeedImages([.. survivors, .. orphans]);

        using var context = OpenWrite();
        var result = OrphanPruner.Prune(context, [RootA], survivors);

        Assert.Equal(620, result.Removed);
        Assert.Equal(620, result.Candidates);
        Assert.Empty(result.Limited);
        Assert.Equal(survivors.Order().ToArray(), CataloguedPaths());
    }

    [Fact]
    public void Prune_EmptyCatalogue_DoesNothing()
    {
        using var context = OpenWrite();
        var result = OrphanPruner.Prune(context, [RootA], []);

        Assert.Equal(0, result.Removed);
        Assert.Empty(result.SkippedRoots);
    }

    // ORPHAN PRUNING DELETES DATABASE ROWS ONLY. IT NEVER DELETES, MOVES, OR MODIFIES ANY
    // FILE ON DISK (spec 2.1). FileSafetyTest already enforces that across all of src/**;
    // this is the belt to its braces, because OrphanPruner is the single file in the
    // solution most likely to grow a "helpfully" wrong instinct to delete the file as well.
    [Fact]
    public void Prune_ReferencesNoWriteCapableFileSystemApi()
    {
        var source = StripComments(File.ReadAllText(
            Path.Combine(FindRepoRoot(), "src", "GalactiLog.Data", "Ingest", "OrphanPruner.cs")));

        string[] forbidden =
        [
            @"\bFile\s*\.", @"\bDirectory\s*\.", @"\bFileInfo\b", @"\bDirectoryInfo\b",
            @"\bUserFiles\b", @"\.Delete\s*\(", @"\.MoveTo\s*\(", @"\bFileStream\b",
            @"\bStreamWriter\b", @"\bFileMode\b", @"\bFileAccess\b",
        ];

        foreach (var pattern in forbidden)
        {
            Assert.False(
                Regex.IsMatch(source, pattern),
                $"OrphanPruner.cs must reference no filesystem-write API, but matched '{pattern}'.");
        }

        // Sanity: the scan actually read the file it thinks it did.
        Assert.Contains("ExecuteDelete", source);
    }

    private static string StripComments(string source)
    {
        source = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(source, @"//[^\r\n]*", "");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GalactiLog.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("GalactiLog.sln not found above the test output directory.");
    }
}
