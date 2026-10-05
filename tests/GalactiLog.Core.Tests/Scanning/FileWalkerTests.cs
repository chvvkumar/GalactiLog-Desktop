using System.Diagnostics;
using GalactiLog.Core.Scanning;
using Xunit;
using Xunit.Abstractions;

namespace GalactiLog.Core.Tests.Scanning;

// Exercises FileWalker (design-spec 10.3 steps 1-2). Real temp trees, raw System.IO: test
// code is exempt from FileSafetyTest (that scan only runs over src/**).
public class FileWalkerTests : IDisposable
{
    private readonly string _root;
    private readonly ITestOutputHelper _output;
    private readonly List<string> _cleanupJunctions = [];
    private readonly List<(string Path, string User)> _cleanupAcls = [];

    public FileWalkerTests(ITestOutputHelper output)
    {
        _output = output;
        _root = Path.Combine(Path.GetTempPath(), "flw-" + Guid.NewGuid());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        foreach (var (path, user) in _cleanupAcls)
        {
            RunIcacls($"\"{path}\" /remove:d \"{user}\"");
        }
        foreach (var junction in _cleanupJunctions)
        {
            try { if (Directory.Exists(junction)) Directory.Delete(junction, recursive: false); }
            catch (IOException) { /* best-effort cleanup only */ }
        }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort cleanup only */ }
    }

    private string Combine(params string[] parts) => Path.Combine([_root, .. parts]);

    private static void WriteFile(string path, int bytes = 4)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    private static List<DiscoveredFile> Walk(
        string root, ScanFilterConfig? filters = null, Action<int>? onProgress = null,
        Action<string>? onWarning = null, CancellationToken ct = default)
        => FileWalker.Walk([root], [root], filters ?? ScanFilterConfig.Empty, onProgress, onWarning, ct).ToList();

    // The same walk with spec 7.6's guide-log callback attached, returning both halves so a case
    // can assert what each one saw AND what it did not.
    private static (List<DiscoveredFile> Yielded, List<DiscoveredFile> GuideLogs) WalkWithGuideLogs(
        string root, ScanFilterConfig? filters = null)
    {
        var guideLogs = new List<DiscoveredFile>();
        var yielded = FileWalker.Walk(
            [root], [root], filters ?? ScanFilterConfig.Empty, null, null, default, guideLogs.Add).ToList();
        return (yielded, guideLogs);
    }

    // --- Walk: discovery ---

    [Fact]
    public void Walk_FindsFilesWithSupportedExtensions_CaseInsensitive()
    {
        WriteFile(Combine("a.fits"));
        WriteFile(Combine("b.FIT"));
        WriteFile(Combine("c.Fts"));
        WriteFile(Combine("d.xisf"));

        var result = Walk(_root);

        Assert.Equal(4, result.Count);
        Assert.All(result, f => Assert.True(File.Exists(f.Path)));
    }

    // The walk reads size and last-write time from the directory listing, not from a stat per
    // file. A failure here means the delta skip of spec 10.3 step 2 compares against the wrong
    // figure and either re-reads every unchanged frame or misses a changed one.
    [Fact]
    public void Walk_DiscoveredFile_CarriesTheListingSizeAndLastWriteTime()
    {
        var path = Combine("a.fits");
        WriteFile(path, bytes: 123);

        var found = Assert.Single(Walk(_root));

        var info = new FileInfo(path);
        Assert.Equal(info.Length, found.FileSize);
        Assert.Equal((info.LastWriteTimeUtc - DateTime.UnixEpoch).TotalSeconds, found.FileMtimeUnixSeconds);
    }

    [Fact]
    public void Walk_IgnoresUnsupportedExtensions()
    {
        WriteFile(Combine("keep.fits"));
        WriteFile(Combine("skip.txt"));
        WriteFile(Combine("skip.jpg"));

        var result = Walk(_root);

        var found = Assert.Single(result);
        Assert.EndsWith("keep.fits", found.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Walk_PrunesDirectoryPerScanFilterConfig()
    {
        WriteFile(Combine("keep.fits"));
        var pruned = Combine("WORK_AREA");
        WriteFile(Path.Combine(pruned, "hidden.fits"));

        // Proves the pruned subtree is genuinely never entered, not merely filtered post
        // hoc: denying list access on it means any attempt to descend would surface as a
        // warning via onWarning.
        var user = CurrentUserAccount();
        var acled = TryDenyAccess(pruned, user);

        var filters = new ScanFilterConfig { ExcludePaths = [pruned] };
        var warnings = new List<string>();

        var result = Walk(_root, filters, onWarning: warnings.Add);

        var found = Assert.Single(result);
        Assert.EndsWith("keep.fits", found.Path, StringComparison.OrdinalIgnoreCase);
        if (acled)
        {
            Assert.Empty(warnings);
        }
    }

    [Fact]
    public void Walk_ReportsProgressEvery50Files_AndOnceMoreAtEnd()
    {
        for (var i = 0; i < 120; i++)
        {
            WriteFile(Combine($"f{i:D4}.fits"));
        }
        var progress = new List<int>();

        var result = Walk(_root, onProgress: progress.Add);

        Assert.Equal(120, result.Count);
        Assert.Contains(50, progress);
        Assert.Contains(100, progress);
        Assert.Equal(120, progress[^1]);
    }

    [Fact]
    public void Walk_CancellationToken_ThrowsOperationCanceledException_MidWalk()
    {
        for (var i = 0; i < 10; i++)
        {
            WriteFile(Combine($"f{i}.fits"));
        }
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => Walk(_root, ct: cts.Token));
    }

    [Fact]
    public void Walk_InaccessibleDirectory_LoggedAndSkipped_DoesNotThrow()
    {
        WriteFile(Combine("sibling", "ok.fits"));
        var denied = Combine("denied");
        WriteFile(Path.Combine(denied, "unreachable.fits"));

        var user = CurrentUserAccount();
        var acled = TryDenyAccess(denied, user);
        if (!acled)
        {
            _output.WriteLine("Skipped: icacls deny did not take effect in this environment.");
            return;
        }

        var warnings = new List<string>();
        var result = Walk(_root, onWarning: warnings.Add);

        var found = Assert.Single(result);
        Assert.EndsWith("ok.fits", found.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(warnings, w => w.Contains(denied, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Walk_JunctionDirectoryInsideAnyRoot_IsFollowed()
    {
        var real = Combine("real-target");
        Directory.CreateDirectory(real);
        WriteFile(Path.Combine(real, "linked.fits"));
        var linkPath = Combine("link");

        if (!TryCreateJunction(linkPath, real))
        {
            _output.WriteLine("Skipped: mklink /J failed in this environment.");
            return;
        }

        var result = Walk(_root);

        Assert.Contains(result, f => f.Path.EndsWith("linked.fits", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Walk_JunctionDirectoryTargetOutsideEveryRoot_IsSkipped()
    {
        var outside = Path.Combine(Path.GetTempPath(), "flw-outside-" + Guid.NewGuid());
        Directory.CreateDirectory(outside);
        try
        {
            WriteFile(Path.Combine(outside, "outside.fits"));
            var linkPath = Combine("link-out");

            if (!TryCreateJunction(linkPath, outside))
            {
                _output.WriteLine("Skipped: mklink /J failed in this environment.");
                return;
            }

            var result = Walk(_root);

            Assert.DoesNotContain(result, f => f.Path.EndsWith("outside.fits", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Walk_JunctionLoopsBackToAncestorRoot_ReturnsOnceWithWarning()
    {
        WriteFile(Combine("keep.fits"));
        Directory.CreateDirectory(Combine("A"));
        var linkPath = Combine("A", "loop");

        if (!TryCreateJunction(linkPath, _root))
        {
            _output.WriteLine("Skipped: mklink /J failed in this environment.");
            return;
        }

        var warnings = new List<string>();
        var result = Walk(_root, onWarning: warnings.Add);

        var found = Assert.Single(result);
        Assert.EndsWith("keep.fits", found.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(warnings, w => w.Contains("loop", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Walk_EffectiveRootsFromIncludePaths_WalksOnlyThose()
    {
        WriteFile(Combine("A", "a.fits"));
        WriteFile(Combine("B", "b.fits"));
        var filters = new ScanFilterConfig { IncludePaths = [Combine("A")] };
        var effectiveRoots = filters.EffectiveRoots([_root]);

        var result = FileWalker.Walk(effectiveRoots, [_root], filters, null, null, default).ToList();

        var found = Assert.Single(result);
        Assert.EndsWith("a.fits", found.Path, StringComparison.OrdinalIgnoreCase);
    }

    // --- Walk: PHD2 guide-log discovery (spec 7.6, spec 10.3 step 5 item 1) ---

    [Fact]
    public void Walk_GuideLog_ReachesTheCallbackAndIsNotYielded()
    {
        WriteFile(Combine("keep.fits"));
        WriteFile(Combine("PHD2_GuideLog_2025-03-19_213000.txt"));

        var (yielded, guideLogs) = WalkWithGuideLogs(_root);

        // A failure here means the log was counted in scan_runs.discovered, offered to the
        // header reader, rejected, and then classified as new on every subsequent scan forever.
        var frame = Assert.Single(yielded);
        Assert.EndsWith("keep.fits", frame.Path, StringComparison.OrdinalIgnoreCase);
        var log = Assert.Single(guideLogs);
        Assert.EndsWith("PHD2_GuideLog_2025-03-19_213000.txt", log.Path, StringComparison.OrdinalIgnoreCase);

        // And it arrives with the stat the frame branch already took, so the delta skip of spec
        // 10.3 step 5 item 2 needs no second syscall per file.
        Assert.Equal(new FileInfo(log.Path).Length, log.FileSize);
        Assert.True(log.FileMtimeUnixSeconds > 0);
    }

    [Fact]
    public void Walk_DebugLog_ReachesNeitherTheCallbackNorTheIterator()
    {
        WriteFile(Combine("PHD2_DebugLog_2025-03-19_213000.txt"));

        var (yielded, guideLogs) = WalkWithGuideLogs(_root);

        // A failure here means the pass opens a multi-megabyte unstructured log per session and
        // records it unreadable, filling the Activity feed with warnings about files the product
        // should never have touched.
        Assert.Empty(yielded);
        Assert.Empty(guideLogs);
    }

    [Fact]
    public void Walk_GuideLogUnderAnExcludedPath_ReachesNeither()
    {
        var excluded = Combine("WORK_AREA");
        WriteFile(Path.Combine(excluded, "PHD2_GuideLog_2025-03-19_213000.txt"));
        WriteFile(Combine("PHD2_GuideLog_2025-03-20_213000.txt"));
        var filters = new ScanFilterConfig { ExcludePaths = [excluded] };

        var (yielded, guideLogs) = WalkWithGuideLogs(_root, filters);

        // A failure here means excluding a folder takes its frames out of the catalogue and
        // leaves its guiding in, so the two halves of the library disagree about what is in it.
        Assert.Empty(yielded);
        var log = Assert.Single(guideLogs);
        Assert.EndsWith("PHD2_GuideLog_2025-03-20_213000.txt", log.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("PHD2_GuideLog_2025-03-19_213000.txt", true)]
    [InlineData("phd2_guidelog_x.TXT", true)]
    [InlineData("PHD2_GUIDELOG_X.TxT", true)]
    [InlineData("PHD2_DebugLog_2025-03-19_213000.txt", false)]
    [InlineData("notes.txt", false)]
    [InlineData("PHD2_GuideLog_2025-03-19_213000.txt.bak", false)]
    [InlineData("frame.fits", false)]
    public void IsGuideLog_MatchesTheSpecPatternCaseInsensitively(string fileName, bool expected)
    {
        // A failure on the lower-cased row means a log written by a tool that lower-cased the
        // name is invisible, with nothing anywhere saying why that night has no guiding.
        Assert.Equal(expected, FileWalker.IsGuideLog(fileName));
        Assert.Equal(expected, FileWalker.IsGuideLog(Path.Combine(@"C:\Astro\Night", fileName)));
    }

    [Fact]
    public void Walk_WithNoGuideLogCallback_BehavesExactlyAsBefore()
    {
        WriteFile(Combine("keep.fits"));
        WriteFile(Combine("PHD2_GuideLog_2025-03-19_213000.txt"));

        // The four existing call sites pass no callback. A failure here means the parameter was
        // added in the wrong position, or that a .txt now leaves the iterator.
        var result = Walk(_root);

        var found = Assert.Single(result);
        Assert.EndsWith("keep.fits", found.Path, StringComparison.OrdinalIgnoreCase);
    }

    // --- Classify ---

    private static readonly DiscoveredFile SampleFile =
        new(@"C:\Astro\frame.fits", FileSize: 1000, FileMtimeUnixSeconds: 1_700_000_000.0);

    [Fact]
    public void Classify_PathNotInKnownSet_IsNew()
    {
        var known = new Dictionary<string, (long?, double?)>();

        Assert.Equal(FileClassification.New, FileWalker.Classify(SampleFile, known));
    }

    [Fact]
    public void Classify_KnownSizeDiffers_IsChanged()
    {
        var known = new Dictionary<string, (long?, double?)>
        {
            [SampleFile.Path] = (999, SampleFile.FileMtimeUnixSeconds),
        };

        Assert.Equal(FileClassification.Changed, FileWalker.Classify(SampleFile, known));
    }

    [Fact]
    public void Classify_KnownMtimeDiffersByMoreThanOneSecond_IsChanged()
    {
        var known = new Dictionary<string, (long?, double?)>
        {
            [SampleFile.Path] = (SampleFile.FileSize, SampleFile.FileMtimeUnixSeconds - 1.5),
        };

        Assert.Equal(FileClassification.Changed, FileWalker.Classify(SampleFile, known));
    }

    [Fact]
    public void Classify_KnownMtimeDiffersByLessThanOneSecond_IsUnchanged()
    {
        var known = new Dictionary<string, (long?, double?)>
        {
            [SampleFile.Path] = (SampleFile.FileSize, SampleFile.FileMtimeUnixSeconds - 0.5),
        };

        Assert.Equal(FileClassification.Unchanged, FileWalker.Classify(SampleFile, known));
    }

    [Fact]
    public void Classify_KnownSizeAndMtimeBothNull_IsUnchanged()
    {
        var known = new Dictionary<string, (long?, double?)>
        {
            [SampleFile.Path] = (null, null),
        };

        Assert.Equal(FileClassification.Unchanged, FileWalker.Classify(SampleFile, known));
    }

    [Fact]
    public void Classify_KnownSizeMatchesButMtimeNull_IsUnchanged()
    {
        var known = new Dictionary<string, (long?, double?)>
        {
            [SampleFile.Path] = (SampleFile.FileSize, null),
        };

        Assert.Equal(FileClassification.Unchanged, FileWalker.Classify(SampleFile, known));
    }

    // --- helpers: real NTFS junctions and ACL deny, matching Phase 1's AppWriterTests
    // precedent (external process, skip gracefully if the environment refuses) ---

    private bool TryCreateJunction(string linkPath, string targetPath)
    {
        using var mklink = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        })!;
        mklink.WaitForExit();
        if (mklink.ExitCode != 0) return false;
        _cleanupJunctions.Add(linkPath);
        return true;
    }

    private static string CurrentUserAccount() => Environment.UserDomainName + "\\" + Environment.UserName;

    // Denies list/read access to `path` for the current user via icacls, then verifies the
    // deny actually took effect (an elevated or SYSTEM test process can otherwise bypass
    // it). Returns false, leaving no ACL behind, when the environment does not honor it.
    private bool TryDenyAccess(string path, string user)
    {
        var deny = RunIcacls($"\"{path}\" /deny \"{user}\":(OI)(CI)RX");
        if (deny != 0) return false;

        var blocked = false;
        try { Directory.EnumerateFileSystemEntries(path).ToList(); }
        catch (UnauthorizedAccessException) { blocked = true; }

        if (!blocked)
        {
            RunIcacls($"\"{path}\" /remove:d \"{user}\"");
            return false;
        }

        _cleanupAcls.Add((path, user));
        return true;
    }

    private static int RunIcacls(string arguments)
    {
        using var icacls = Process.Start(new ProcessStartInfo
        {
            FileName = "icacls.exe",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        })!;
        icacls.WaitForExit();
        return icacls.ExitCode;
    }
}
