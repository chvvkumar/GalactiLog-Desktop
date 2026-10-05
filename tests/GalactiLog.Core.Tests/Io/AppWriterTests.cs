using System;
using System.Diagnostics;
using System.IO;
using GalactiLog.Core.Io;
using Xunit;
using Xunit.Abstractions;

namespace GalactiLog.Core.Tests.Io;

// Exercises AppWriter, the file-safety choke point (spec 2.1.1). These tests write real
// temp files under isolated per-test directories; AppWriter.cs is the one allowlisted file
// in FileSafetyTest, so this is expected and not a violation (that scan only runs over
// src/**, never tests/**).
public class AppWriterTests : IDisposable
{
    private readonly string _appDataRoot;
    private readonly string _thumbnailCacheRoot;
    private readonly ITestOutputHelper _output;
    private string? _junctionPath;

    public AppWriterTests(ITestOutputHelper output)
    {
        _output = output;
        _appDataRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _thumbnailCacheRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_appDataRoot);
        Directory.CreateDirectory(_thumbnailCacheRoot);
    }

    public void Dispose()
    {
        if (_junctionPath is not null)
        {
            try
            {
                if (Directory.Exists(_junctionPath))
                {
                    // Deletes the junction point itself, not the directory it targets.
                    Directory.Delete(_junctionPath, recursive: false);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup only; leaving temp files behind does not fail the test.
            }
        }
        TryDelete(_appDataRoot);
        TryDelete(_thumbnailCacheRoot);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup only; leaving temp files behind does not fail the test.
        }
    }

    // Review finding M6: an explicit temp pointer path, never the constructor default, which is the
    // real %APPDATA%GalactiLogdatapath.json.
    private AppWriter CreateWriter()
        => new AppWriter(
            _appDataRoot, _thumbnailCacheRoot, Path.Combine(_appDataRoot, "datapath.json"));

    [Fact]
    public void ResolveAppDataPath_UnderAppData_Accepted()
    {
        var writer = CreateWriter();

        var resolved = writer.ResolveAppDataPath("galactilog.db");

        Assert.StartsWith(_appDataRoot, resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveAppDataPath_DotDotTraversal_Throws()
    {
        var writer = CreateWriter();

        Assert.Throws<UnauthorizedPathException>(() => writer.ResolveAppDataPath(Path.Combine("..", "escaped.txt")));
    }

    [Fact]
    public void WriteAllBytes_UnderThumbnailCache_Accepted()
    {
        var writer = CreateWriter();
        var target = Path.Combine(_thumbnailCacheRoot, "thumb.jpg");
        var bytes = new byte[] { 1, 2, 3 };

        writer.WriteAllBytes(target, bytes);

        Assert.True(File.Exists(target));
        Assert.Equal(bytes, File.ReadAllBytes(target));
    }

    [Fact]
    public void WriteAllBytes_UnderScanRoot_Throws()
    {
        var writer = CreateWriter();
        var scanRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(scanRoot);
        try
        {
            var target = Path.Combine(scanRoot, "frame.fits");

            Assert.Throws<UnauthorizedPathException>(() => writer.WriteAllBytes(target, new byte[] { 1 }));
        }
        finally
        {
            TryDelete(scanRoot);
        }
    }

    [Fact]
    public void WriteAllBytes_OutsideAllRoots_Throws()
    {
        var writer = CreateWriter();
        var sibling = _appDataRoot + "-sibling";
        var target = Path.Combine(sibling, "file.txt");

        Assert.Throws<UnauthorizedPathException>(() => writer.WriteAllBytes(target, new byte[] { 1 }));
    }

    [Fact]
    public void Delete_UnderAppData_RemovesFile()
    {
        var writer = CreateWriter();
        var target = writer.ResolveAppDataPath("to-delete.txt");
        writer.WriteAllBytes(target, new byte[] { 1 });

        writer.Delete(target);
        // A second delete of an already-missing file must not throw (idempotent).
        writer.Delete(target);

        Assert.False(File.Exists(target));
    }

    [Fact]
    public void ThumbnailCacheRoot_DriveRoot_Throws()
    {
        var writer = CreateWriter();
        var driveRoot = Path.GetPathRoot(_appDataRoot)!;

        Assert.Throws<UnauthorizedPathException>(() => writer.ThumbnailCacheRoot = driveRoot);
    }

    [Fact]
    public void ThumbnailCacheRoot_Setter_NormalizesPath()
    {
        var writer = CreateWriter();
        var messy = _thumbnailCacheRoot.Replace('\\', '/') + "/sub/../thumbs";

        writer.ThumbnailCacheRoot = messy;

        Assert.Equal(Path.GetFullPath(messy), writer.ThumbnailCacheRoot);
    }

    [Fact]
    public void Delete_UnderScanRoot_Throws()
    {
        var writer = CreateWriter();
        var scanRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(scanRoot);
        try
        {
            var target = Path.Combine(scanRoot, "frame.fits");

            Assert.Throws<UnauthorizedPathException>(() => writer.Delete(target));
        }
        finally
        {
            TryDelete(scanRoot);
        }
    }

    [Fact]
    public void WriteAllBytes_ThroughReparsePointInAppData_Throws()
    {
        var writer = CreateWriter();
        var scanRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(scanRoot);
        var linkPath = Path.Combine(_appDataRoot, "link");

        // Directory junctions need no special privilege on Windows (unlike symbolic
        // links), so mklink /J gives a reliable, unprivileged way to exercise the
        // reparse-point defense in Authorize/ThrowIfReparsePointBetween.
        using var mklink = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{linkPath}\" \"{scanRoot}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        })!;
        mklink.WaitForExit();

        if (mklink.ExitCode != 0)
        {
            // mklink itself failed (not a permissions gap, since junctions need none):
            // e.g. not running on Windows, or the temp volume does not support junctions.
            // Skip rather than fail: this test verifies defense in depth, not the base
            // authorization check.
            _output.WriteLine($"Skipped: mklink /J exited {mklink.ExitCode}: {mklink.StandardError.ReadToEnd()}");
            TryDelete(scanRoot);
            return;
        }

        _junctionPath = linkPath;

        try
        {
            var target = Path.Combine(linkPath, "frame.fits");

            Assert.Throws<UnauthorizedPathException>(() => writer.WriteAllBytes(target, new byte[] { 1 }));
        }
        finally
        {
            TryDelete(scanRoot);
        }
    }

    [Fact]
    public void BeginExport_WriteToOtherPath_Throws()
    {
        var writer = CreateWriter();
        var destination = Path.Combine(_appDataRoot, "export.json");
        var otherPath = Path.Combine(_appDataRoot, "other.json");

        using var export = writer.BeginExport(destination);

        Assert.Throws<UnauthorizedPathException>(() => export.WriteAllBytes(otherPath, new byte[] { 1 }));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void BeginExport_WriteAllTextToOtherPath_Throws()
    {
        var writer = CreateWriter();
        var destination = Path.Combine(_appDataRoot, "export.txt");
        var otherPath = Path.Combine(_appDataRoot, "other.txt");

        using var export = writer.BeginExport(destination);

        Assert.Throws<UnauthorizedPathException>(() => export.WriteAllText(otherPath, "contents"));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void BeginExport_WriteAfterDispose_Throws()
    {
        var writer = CreateWriter();
        var destination = Path.Combine(_appDataRoot, "export.json");

        var export = writer.BeginExport(destination);
        export.Dispose();

        Assert.Throws<ObjectDisposedException>(() => export.WriteAllBytes(destination, new byte[] { 1 }));
    }

    [Fact]
    public void BeginExport_WriteToDestination_Succeeds()
    {
        var writer = CreateWriter();
        var destination = Path.Combine(_appDataRoot, "export.json");
        var bytes = new byte[] { 1, 2, 3, 4 };

        using (var export = writer.BeginExport(destination))
        {
            export.WriteAllBytes(destination, bytes);
        }

        Assert.True(File.Exists(destination));
        Assert.Equal(bytes, File.ReadAllBytes(destination));
    }

    // Review item 7, phase 4 review item 4. PathConfinement.IsUnderOrEqual is the one
    // containment rule for AppWriter, ScanFilterConfig, FileWalker and OrphanPruner, so its
    // edge cases matter well beyond AppWriter itself.
    [Theory]
    [InlineData(@"C:\Astro2\file.fits", @"C:\Astro", false)] // sibling with a shared string prefix, not actually nested
    [InlineData(@"C:\Astro\file.fits", @"C:\Astro", true)]
    [InlineData(@"C:\Astro\", @"C:\Astro", true)] // trailing separator on the candidate
    // FIXER LIST 1: a trailing separator on the root no longer makes the rule asymmetric.
    [InlineData(@"C:\Astro", @"C:\Astro\", true)]
    [InlineData(@"C:\Astro\file.fits", @"C:\Astro\", true)]
    [InlineData(@"C:\Astro2\file.fits", @"C:\Astro\", false)]
    [InlineData(@"c:\astro\file.fits", @"C:\Astro", true)] // mixed case, Windows is case-insensitive
    // Drive and share roots keep their trailing separator: "C:\" trimmed to "C:" would mean
    // the current directory on C:, an entirely different location.
    [InlineData(@"C:\", @"C:\", true)]
    [InlineData(@"C:\Astro\file.fits", @"C:\", true)]
    [InlineData(@"D:\Astro\file.fits", @"C:\", false)]
    [InlineData(@"\\nas\share\astro\file.fits", @"\\nas\share", true)]
    [InlineData(@"\\nas\share\astro\file.fits", @"\\nas\share\astro", true)] // UNC root
    [InlineData(@"\\nas\share\other\file.fits", @"\\nas\share\astro", false)] // UNC sibling
    public void IsUnderOrEqual_CoversPathConfinementEdgeCases(string candidate, string root, bool expected)
    {
        Assert.Equal(expected, PathConfinement.IsUnderOrEqual(root, candidate));
    }

    // Phase 8 Task 4: the two read-only members the thumbnail cache resolves its root through, so
    // the cache never composes a path under the cache root itself. Neither adds write capability.

    [Fact]
    public void ResolveThumbnailPath_UnderTheCacheRoot_IsAccepted()
    {
        var writer = CreateWriter();

        var resolved = writer.ResolveThumbnailPath("frames/abc.jpg");

        Assert.Equal(Path.Combine(_thumbnailCacheRoot, "frames", "abc.jpg"), resolved);
    }

    [Fact]
    public void ResolveThumbnailPath_TraversalEscapingTheCacheRoot_IsRefused()
    {
        var writer = CreateWriter();

        Assert.Throws<UnauthorizedPathException>(
            () => writer.ResolveThumbnailPath(Path.Combine("..", "escaped.jpg")));
    }

    // The app data root is a separate authorized root for writing, but it is not the cache root:
    // resolving a cache-relative path must never land there.
    [Fact]
    public void ResolveThumbnailPath_PathUnderAppDataButOutsideTheCacheRoot_IsRefused()
    {
        var writer = CreateWriter();

        Assert.Throws<UnauthorizedPathException>(
            () => writer.ResolveThumbnailPath(Path.Combine(_appDataRoot, "frames", "abc.jpg")));
    }

    [Fact]
    public void ResolveThumbnailPath_FollowsTheReparsePointRule()
    {
        var writer = CreateWriter();
        var outsideRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(outsideRoot);
        var linkPath = Path.Combine(_thumbnailCacheRoot, "link");

        using var mklink = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{linkPath}\" \"{outsideRoot}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        })!;
        mklink.WaitForExit();

        if (mklink.ExitCode != 0)
        {
            _output.WriteLine($"Skipped: mklink /J exited {mklink.ExitCode}: {mklink.StandardError.ReadToEnd()}");
            TryDelete(outsideRoot);
            return;
        }

        _junctionPath = linkPath;

        try
        {
            Assert.Throws<UnauthorizedPathException>(() => writer.ResolveThumbnailPath("link/thumb.jpg"));
        }
        finally
        {
            TryDelete(outsideRoot);
        }
    }

    [Fact]
    public void EnumerateThumbnailFiles_MissingSubdirectory_IsEmpty()
    {
        var writer = CreateWriter();

        Assert.Empty(writer.EnumerateThumbnailFiles("previews", "*.jpg"));
    }

    [Fact]
    public void EnumerateThumbnailFiles_ReturnsLengthAndLastWriteTime()
    {
        var writer = CreateWriter();
        var target = Path.Combine(_thumbnailCacheRoot, "previews", "one.jpg");
        writer.WriteAllBytes(target, new byte[] { 1, 2, 3, 4, 5 });

        var entry = Assert.Single(writer.EnumerateThumbnailFiles("previews", "*.jpg"));

        Assert.Equal(target, entry.Path);
        Assert.Equal(5, entry.Length);
        Assert.Equal(File.GetLastWriteTimeUtc(target), entry.LastWriteUtc);
    }

    [Fact]
    public void EnumerateThumbnailFiles_DoesNotRecurse()
    {
        var writer = CreateWriter();
        writer.WriteAllBytes(Path.Combine(_thumbnailCacheRoot, "previews", "top.jpg"), new byte[] { 1 });
        writer.WriteAllBytes(Path.Combine(_thumbnailCacheRoot, "previews", "nested", "deep.jpg"), new byte[] { 1 });

        var entry = Assert.Single(writer.EnumerateThumbnailFiles("previews", "*.jpg"));

        Assert.Equal("top.jpg", Path.GetFileName(entry.Path));
    }

    [Fact]
    public void EnumerateThumbnailFiles_HonoursTheSearchPattern()
    {
        var writer = CreateWriter();
        writer.WriteAllBytes(Path.Combine(_thumbnailCacheRoot, "previews", "one.jpg"), new byte[] { 1 });
        writer.WriteAllBytes(Path.Combine(_thumbnailCacheRoot, "previews", "notes.txt"), new byte[] { 1 });

        var entry = Assert.Single(writer.EnumerateThumbnailFiles("previews", "*.jpg"));

        Assert.Equal("one.jpg", Path.GetFileName(entry.Path));
    }

    // The staging writer, the fourth root. Every case stays under the per-test
    // temp root; the staging root is a fabricated folder inside it.
    private string StagingRoot() => Path.Combine(_appDataRoot, "staging");

    [Fact]
    public void BeginStagingCopy_DriveRoot_Throws()
    {
        var writer = CreateWriter();

        Assert.Throws<UnauthorizedPathException>(() => writer.BeginStagingCopy(Path.GetPathRoot(_appDataRoot)!));
    }

    [Fact]
    public void StagingWriter_CreatesNewFilesAndDirectoriesUnderTheRoot()
    {
        using var staging = CreateWriter().BeginStagingCopy(StagingRoot());
        var directory = Path.Combine(StagingRoot(), "Night1", "Light");

        staging.CreateDirectory(directory);
        using (var stream = staging.CreateNewFile(Path.Combine(directory, "frame.fits")))
        {
            stream.Write(new byte[] { 1, 2, 3 });
        }

        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(directory, "frame.fits")));
    }

    [Fact]
    public void StagingWriter_PathOutsideTheRoot_Throws()
    {
        using var staging = CreateWriter().BeginStagingCopy(StagingRoot());
        var escaping = Path.Combine(StagingRoot(), "..", "escaped.fits");
        var escapingDirectory = Path.Combine(StagingRoot(), "..", "escaped");

        Assert.Throws<UnauthorizedPathException>(() => staging.CreateNewFile(escaping));
        Assert.Throws<UnauthorizedPathException>(() => staging.CreateDirectory(escapingDirectory));
        Assert.False(File.Exists(Path.GetFullPath(escaping)));
        Assert.False(Directory.Exists(Path.GetFullPath(escapingDirectory)));
    }

    [Fact]
    public void StagingWriter_ReparsePointBetweenRootAndLeaf_Throws()
    {
        var outsideRoot = Path.Combine(_thumbnailCacheRoot, "outside");
        Directory.CreateDirectory(outsideRoot);
        Directory.CreateDirectory(StagingRoot());
        var linkPath = Path.Combine(StagingRoot(), "link");

        using var mklink = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{linkPath}\" \"{outsideRoot}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        })!;
        mklink.WaitForExit();
        if (mklink.ExitCode != 0)
        {
            _output.WriteLine($"Skipped: mklink /J exited {mklink.ExitCode}: {mklink.StandardError.ReadToEnd()}");
            return;
        }
        _junctionPath = linkPath;

        using var staging = CreateWriter().BeginStagingCopy(StagingRoot());

        Assert.Throws<UnauthorizedPathException>(() => staging.CreateNewFile(Path.Combine(linkPath, "frame.fits")));
        Assert.Throws<UnauthorizedPathException>(() => staging.CreateDirectory(Path.Combine(linkPath, "sub")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outsideRoot));
    }

    [Fact]
    public void StagingWriter_CreateNewFileOnAnExistingPath_ThrowsAndLeavesTheBytes()
    {
        Directory.CreateDirectory(StagingRoot());
        var existing = Path.Combine(StagingRoot(), "frame.fits");
        File.WriteAllBytes(existing, new byte[] { 9, 8, 7 });
        using var staging = CreateWriter().BeginStagingCopy(StagingRoot());

        Assert.Throws<IOException>(() => staging.CreateNewFile(existing));
        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(existing));
    }

    [Fact]
    public void StagingWriter_AfterDispose_EveryMemberThrows()
    {
        var staging = CreateWriter().BeginStagingCopy(StagingRoot());
        staging.Dispose();

        Assert.Throws<ObjectDisposedException>(() => staging.CreateDirectory(Path.Combine(StagingRoot(), "a")));
        Assert.Throws<ObjectDisposedException>(() => staging.CreateNewFile(Path.Combine(StagingRoot(), "a.fits")));
        Assert.False(Directory.Exists(StagingRoot()));
    }

    [Fact]
    public void StagingWriter_AlternateDataStreamPath_Throws()
    {
        Directory.CreateDirectory(StagingRoot());
        var existing = Path.Combine(StagingRoot(), "frame.fits");
        File.WriteAllBytes(existing, new byte[] { 9, 8, 7 });
        using var staging = CreateWriter().BeginStagingCopy(StagingRoot());

        Assert.Throws<UnauthorizedPathException>(() => staging.CreateNewFile(existing + ":extra"));
        Assert.Throws<UnauthorizedPathException>(() => staging.CreateDirectory(Path.Combine(StagingRoot(), "a:b")));
        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(existing));
    }

    [Fact]
    public void StagingWriter_RootWithTrailingSeparator_ConfinesTheSame()
    {
        using var staging = CreateWriter().BeginStagingCopy(StagingRoot() + Path.DirectorySeparatorChar);
        Directory.CreateDirectory(StagingRoot());

        using (staging.CreateNewFile(Path.Combine(StagingRoot(), "inside.fits")))
        {
        }

        Assert.True(File.Exists(Path.Combine(StagingRoot(), "inside.fits")));
        Assert.Throws<UnauthorizedPathException>(() => staging.CreateNewFile(StagingRoot() + "2" + Path.DirectorySeparatorChar + "sibling.fits"));
    }

    [Fact]
    public void StagingWriter_CaseVariantPathUnderTheRoot_IsAccepted()
    {
        Directory.CreateDirectory(StagingRoot());
        using var staging = CreateWriter().BeginStagingCopy(StagingRoot());

        using (staging.CreateNewFile(Path.Combine(StagingRoot().ToUpperInvariant(), "upper.fits")))
        {
        }

        Assert.True(File.Exists(Path.Combine(StagingRoot(), "upper.fits")));
    }

    [Theory]
    [InlineData(@"\\nas\share")]
    [InlineData(@"\\nas\share\")]
    public void BeginStagingCopy_ShareRoot_Throws(string root)
    {
        Assert.Throws<UnauthorizedPathException>(() => CreateWriter().BeginStagingCopy(root));
    }
}
