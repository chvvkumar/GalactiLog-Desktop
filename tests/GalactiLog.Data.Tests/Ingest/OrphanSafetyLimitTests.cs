using System.Text.RegularExpressions;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Ingest;
using Xunit;

namespace GalactiLog.Data.Tests.Ingest;

// Spec 10.3's second orphan guard, the 50 percent safety limit (PAR-013), which the port did not
// have before Phase 14B Task 5. A separate file from OrphanPrunerTests on purpose: those eleven
// cases are the pre-limit behaviour and read better left as they are.
//
// Nothing here touches the filesystem. Every path is a string and no directory is ever created,
// which is the same property OrphanPruner itself has and FileSafetyTest enforces.
public class OrphanSafetyLimitTests : IDisposable
{
    private readonly TestDatabaseHandle _db = TestDatabaseFactory.CreateMigratedDatabase();

    private static readonly string RootA = Path.Combine(Path.GetTempPath(), "galactilog-limit-a");
    private static readonly string RootB = Path.Combine(Path.GetTempPath(), "galactilog-limit-b");

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

    private int RowCount()
    {
        using var context = OpenRead();
        return context.Images.Count();
    }

    // Seeds `known` rows under `root` and returns the first `present` of them, which stands for
    // what the walk discovered this run. The rest are the missing ones.
    private string[] Seed(string root, int known, int present)
    {
        var paths = Enumerable.Range(0, known).Select(i => Path.Combine(root, $"f{i:000}.fits")).ToArray();
        SeedImages(paths);
        return [.. paths.Take(present)];
    }

    [Fact]
    public void MissingBelowHalf_IsPrunedWithNoOverride()
    {
        // 1 of 4 missing: 1 < max(1, 4) * 0.5.
        var discovered = Seed(RootA, known: 4, present: 3);

        using var context = OpenWrite();
        var result = OrphanPruner.Prune(context, [RootA], discovered);

        Assert.Equal(1, result.Removed);
        Assert.Empty(result.Limited);
        Assert.Equal(3, RowCount());
    }

    [Fact]
    public void MissingExactlyHalf_TripsTheLimit()
    {
        // 2 of 4 missing: the spec's predicate is >=, so exactly half is limited.
        var discovered = Seed(RootA, known: 4, present: 2);

        using var context = OpenWrite();
        var result = OrphanPruner.Prune(context, [RootA], discovered);

        Assert.Equal(0, result.Removed);
        Assert.Single(result.Limited);
        Assert.Equal(4, RowCount());
    }

    // The boundary itself, at two sizes: one row either side of max(1, known) * 0.5.
    [Theory]
    [InlineData(4, 3, 1, false)]   // 1 of 4, below
    [InlineData(4, 2, 2, true)]    // 2 of 4, exactly half
    [InlineData(4, 1, 3, true)]    // 3 of 4, above
    [InlineData(2, 2, 0, false)]   // nothing missing
    [InlineData(2, 1, 1, true)]    // 1 of 2, exactly half
    [InlineData(10, 6, 4, false)]  // 4 of 10, below
    [InlineData(10, 5, 5, true)]   // 5 of 10, exactly half
    public void TheBoundary_IsGreaterThanOrEqual(int known, int present, int missing, bool limited)
    {
        var discovered = Seed(RootA, known, present);

        using var context = OpenWrite();
        var result = OrphanPruner.Prune(context, [RootA], discovered);

        Assert.Equal(limited ? 0 : missing, result.Removed);
        Assert.Equal(limited ? 1 : 0, result.Limited.Count);
    }

    [Fact]
    public void MissingAboveHalf_TripsTheLimit()
    {
        var discovered = Seed(RootA, known: 4, present: 1);

        using var context = OpenWrite();
        var result = OrphanPruner.Prune(context, [RootA], discovered);

        Assert.Equal(0, result.Removed);
        Assert.Single(result.Limited);
        Assert.Equal(4, RowCount());
    }

    [Fact]
    public void ALimitedRoot_DeletesNothing()
    {
        var discovered = Seed(RootA, known: 6, present: 2);

        using var context = OpenWrite();
        OrphanPruner.Prune(context, [RootA], discovered);

        Assert.Equal(6, RowCount());
    }

    [Fact]
    public void ALimitedRoot_ReportsBothCounts()
    {
        var discovered = Seed(RootA, known: 6, present: 2);

        using var context = OpenWrite();
        var result = OrphanPruner.Prune(context, [RootA], discovered);

        var limited = Assert.Single(result.Limited);
        Assert.Equal(Path.GetFullPath(RootA), limited.Root);
        Assert.Equal(4, limited.MissingRows);
        Assert.Equal(6, limited.KnownRows);

        // A limited root's rows WERE found missing, unlike a skipped root's, so they count as
        // candidates: the prune_orphans envelope reports 0 of 4, not 0 of 0.
        Assert.Equal(4, result.Candidates);
        Assert.Equal(0, result.Removed);
    }

    [Fact]
    public void WithTheOverrideOn_ALimitedRootIsDeleted()
    {
        var discovered = Seed(RootA, known: 6, present: 2);

        using var context = OpenWrite();
        var result = OrphanPruner.Prune(context, [RootA], discovered, force: true);

        Assert.Equal(4, result.Removed);
        Assert.Equal(4, result.Candidates);

        // Still reported, so the caller can write orphan_prune_forced naming what it passed.
        var limited = Assert.Single(result.Limited);
        Assert.Equal(4, limited.MissingRows);
        Assert.Equal(6, limited.KnownRows);
        Assert.Equal(2, RowCount());
    }

    [Fact]
    public void TheLimit_IsEvaluatedPerRoot_NotPerRun()
    {
        // Across the run, 5 of 10 rows are missing, which would be limited if the guard were
        // global. Per root, A is 4 of 5 (limited) and B is 1 of 5 (pruned).
        var inA = Seed(RootA, known: 5, present: 1);
        var inB = Seed(RootB, known: 5, present: 4);

        using var context = OpenWrite();
        var result = OrphanPruner.Prune(context, [RootA, RootB], [.. inA, .. inB]);

        Assert.Equal(1, result.Removed);
        var limited = Assert.Single(result.Limited);
        Assert.Equal(Path.GetFullPath(RootA), limited.Root);
    }

    [Fact]
    public void ARootTrippingTheLimit_DoesNotStopAHealthyRootInTheSameRun()
    {
        // RootA is 3 of 4 missing, which the limit stops; RootB is 1 of 8, which it does not.
        var inA = Seed(RootA, known: 4, present: 1);
        var inB = Seed(RootB, known: 8, present: 7);

        using var context = OpenWrite();
        var result = OrphanPruner.Prune(context, [RootA, RootB], [.. inA, .. inB]);

        Assert.Equal(1, result.Removed);
        Assert.Empty(result.SkippedRoots);
        var limited = Assert.Single(result.Limited);
        Assert.Equal(Path.GetFullPath(RootA), limited.Root);

        // RootA keeps all four rows; RootB lost exactly its one orphan.
        Assert.Equal(11, RowCount());
    }

    // Phase 14B fixer, fixer list item 35 (task5-review P3): renamed to what it asserts. The
    // max(1, known) term is unreachable, because a root with zero catalogued rows is dropped
    // before either guard runs, so no case can assert it; what this one asserts is that such a
    // root is neither limited nor skipped, and that the fraction is the spec's constant.
    [Fact]
    public void ARootWithNoCatalogedRows_IsNeitherLimitedNorSkipped()
    {
        // The predicate is max(1, known) * 0.5, not known * 0.5, so a root with zero known rows
        // would be limited by any missing count of 1 or more rather than by a threshold of zero.
        // In practice zero known rows means zero missing rows, and such a root is dropped before
        // either guard runs, so it is neither limited nor skipped. The case writes that down
        // rather than reasoning about it.
        var inB = Seed(RootB, known: 4, present: 3);

        using var context = OpenWrite();
        var result = OrphanPruner.Prune(context, [RootA, RootB], inB);

        Assert.Equal(1, result.Removed);
        Assert.DoesNotContain(result.SkippedRoots, skip => skip.Root == Path.GetFullPath(RootA));
        Assert.DoesNotContain(result.Limited, limit => limit.Root == Path.GetFullPath(RootA));

        // And the constant the arithmetic reads is the spec's, not a local literal.
        Assert.Equal(0.5, OrphanPruner.SafetyLimitFraction);
    }

    // Spec 10.3: "This guard is absolute: the cleanup override does not lift it, because a root
    // that produced nothing at all carries no evidence that anything was deleted." This is the
    // case a careless implementation fails, because "force" reads like "do it anyway".
    [Fact]
    public void TheZeroDiscoveryGuard_IsNotLiftedByTheOverride()
    {
        Seed(RootA, known: 4, present: 0);

        using var context = OpenWrite();
        var result = OrphanPruner.Prune(context, [RootA], [], force: true);

        Assert.Equal(0, result.Removed);
        var skipped = Assert.Single(result.SkippedRoots);
        Assert.Equal(Path.GetFullPath(RootA), skipped.Root);
        Assert.Equal(4, skipped.KnownRows);
        Assert.Empty(result.Limited);
        Assert.Equal(4, RowCount());
    }

    // Spec 10.3: "No file, folder or image on disk is deleted, moved, renamed or modified by any
    // outcome of step 4, FORCED OR NOT." Real files this time, under a temp directory this test
    // owns, hashed before and after.
    [Fact]
    public void AForcedRun_DeletesNoFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "galactilog-forced-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var paths = Enumerable.Range(0, 4).Select(i => Path.Combine(directory, $"f{i}.fits")).ToArray();
            foreach (var path in paths)
            {
                File.WriteAllText(path, "not a real frame");
            }

            SeedImages(paths);
            var before = paths.Select(p => (p, File.ReadAllBytes(p).Length, File.GetLastWriteTimeUtc(p))).ToList();

            using (var context = OpenWrite())
            {
                // The walk "discovered" nothing but one of them, so three are missing: 3 of 4 is
                // past the limit, and the override deletes those three rows.
                var result = OrphanPruner.Prune(context, [directory], [paths[0]], force: true);
                Assert.Equal(3, result.Removed);
            }

            Assert.Equal(1, RowCount());

            // Every file is still there, the same size, with the same write time.
            foreach (var (path, length, written) in before)
            {
                Assert.True(File.Exists(path), $"{path} was deleted by a forced orphan prune.");
                Assert.Equal(length, File.ReadAllBytes(path).Length);
                Assert.Equal(written, File.GetLastWriteTimeUtc(path));
            }

            Assert.Equal(4, Directory.GetFiles(directory).Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // The belt to FileSafetyTest's braces, for the force path specifically: the new parameter must
    // not have brought a filesystem call in with it.
    [Fact]
    public void AForcedRun_TouchesNoFileSystemApi()
    {
        // Comments stripped first, exactly as OrphanPrunerTests does: the type's own doc comment
        // names the read-only UserFiles gateway in prose, which is not a reference to it.
        var source = StripComments(File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "GalactiLog.Data", "Ingest", "OrphanPruner.cs")));

        string[] forbidden =
        [
            @"\bFile\s*\.", @"\bDirectory\s*\.", @"\bFileInfo\b", @"\bDirectoryInfo\b",
            @"\bUserFiles\b", @"\.Delete\s*\(", @"\.MoveTo\s*\(", @"\bFileStream\b",
        ];

        foreach (var pattern in forbidden)
        {
            Assert.False(
                Regex.IsMatch(source, pattern),
                $"OrphanPruner.cs must reference no filesystem API, but matched '{pattern}'.");
        }

        // Sanity: the force path really is in the file the scan just read.
        Assert.Contains("bool force", source, StringComparison.Ordinal);
        Assert.Contains("SafetyLimitFraction", source, StringComparison.Ordinal);
    }

    // Line and block comments out, so a sentence about the filesystem is not read as a call.
    private static string StripComments(string source)
    {
        var withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
        var lines = withoutBlocks.Split('\n').Select(line =>
        {
            var start = line.IndexOf("//", StringComparison.Ordinal);
            return start >= 0 ? line[..start] : line;
        });

        return string.Join('\n', lines);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GalactiLog.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
