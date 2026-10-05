using System.Security.Cryptography;
using System.Text.Json;
using GalactiLog.App;
using GalactiLog.App.Services;
using GalactiLog.Core.Io;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Settings;
using GalactiLog.Core.Tests.Fixtures;
using GalactiLog.Data;
using GalactiLog.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using static GalactiLog.Cli.Tests.CliHostFixture;

namespace GalactiLog.Cli.Tests;

// TRACKING.md section 6 item 23, open since Phase 8: `galactilog scan` runs spec 11.4's reference
// thumbnail pass on its scan thread, decoding up to three frames per target and writing under the
// cache root, and no GalactiLog.Cli.Tests case covered it "because it needs a renderable fixture".
// This file builds one.
//
// Why ScanVerbTests' fixture cannot be reused: its LightFrame() is NAXIS = 0 with no pixel data at
// all, described in its own comment as "one 2880-byte block is the whole file". The pass decodes
// pixels, so that fixture produces nothing. The frames here carry a real pixel block built with
// FitsBuilder.Pixels.
//
// FILE SAFETY: every file this class creates lives in a temp folder it created and deletes.
// Nothing in src/** writes, moves or deletes any file; the pass writes through AppWriter into the
// thumbnail cache root and SkiaSharp is never handed a path (spec 2.1.2), and
// Scan_LeavesEveryFrameInTheScanRootByteIdentical is the assertion for the first half of that.
public sealed class ScanReferenceThumbnailTests
{
    // Spec 18.2: fixtures are generated in code, nothing is committed.
    //
    // 64 by 64 at BITPIX 16: small enough that four decodes and two JPEG encodes cost nothing in a
    // test run, large enough that the resample and the encode are real work rather than a
    // degenerate one-pixel path. The values are a deterministic gradient plus a few bright pixels,
    // because a flat field can make the autostretch produce a single-valued image, which is a
    // legitimate output and a useless assertion.
    private const int FrameSize = 64;

    private static float[,] Gradient(int seed)
    {
        var pixels = new float[FrameSize, FrameSize];
        for (var row = 0; row < FrameSize; row++)
        {
            for (var column = 0; column < FrameSize; column++)
            {
                pixels[row, column] = 100f + (row * 8f) + (column * 4f) + seed;
            }
        }

        // The few bright pixels. Far above the gradient's ceiling, so the histogram the stretch
        // reads has a tail and its black and white points cannot coincide.
        pixels[8, 8] = 30000f;
        pixels[32, 40] = 42000f;
        pixels[55, 17] = 51000f;
        return pixels;
    }

    // The header set ScanVerbTests uses, so these frames resolve offline through the shipped
    // catalogue exactly as that file's do and NoMatchHandler is never reached.
    private static byte[] LightFrame(string objectName, string captureUtc, int seed)
        => new FitsBuilder()
            .Card("SIMPLE", true)
            .Card("BITPIX", (long)16)
            .Card("NAXIS", (long)2)
            .Card("NAXIS1", (long)FrameSize)
            .Card("NAXIS2", (long)FrameSize)
            .Card("IMAGETYP", "LIGHT")
            .Card("OBJECT", objectName)
            .Card("DATE-OBS", captureUtc)
            .Card("EXPTIME", 300.0)
            .EndCard()
            .Pixels(16, FrameSize, FrameSize, Gradient(seed))
            .Build().ToArray();

    // A frame the header pass accepts and the pixel reader declines: BITPIX 64 is a valid header
    // value and an unsupported sample format (the same shape ThumbnailRendererTests uses for
    // Render_FitsBitpix64_IsASkip). It is ingested like any other LIGHT frame, so it becomes one of
    // its target's candidates, and it renders nothing, so the pass must fall through to the next.
    private static byte[] UndecodableLightFrame(string objectName, string captureUtc)
        => new FitsBuilder()
            .Card("SIMPLE", true)
            .Card("BITPIX", (long)64)
            .Card("NAXIS", (long)2)
            .Card("NAXIS1", (long)8)
            .Card("NAXIS2", (long)8)
            .Card("IMAGETYP", "LIGHT")
            .Card("OBJECT", objectName)
            .Card("DATE-OBS", captureUtc)
            .Card("EXPTIME", 300.0)
            .EndCard()
            .Pixels(-64, naxis1: 8, naxis2: 8, new float[8, 8])
            .Build().ToArray();

    private sealed class HostFixture : IDisposable
    {
        public string Root { get; }

        public string Frames { get; }

        /// <summary>Non-null only for the relocated-cache case; the fixture owns and deletes it.</summary>
        public string? CacheDirectory { get; }

        public IHost Host { get; }

        /// <param name="relocateCache">Writes general.thumbnail_cache_dir to a second temp
        /// directory before the scan, which is the one place the CLI exercises spec 11.3's
        /// relocatable cache root.</param>
        /// <param name="undecodableNewestFrame">Replaces M 31's newest frame with one the pixel
        /// reader declines, so the pass has to fall through to the next candidate.</param>
        public HostFixture(bool relocateCache = false, bool undecodableNewestFrame = false)
        {
            Root = Directory.CreateTempSubdirectory("galactilog-refthumb-").FullName;
            Frames = Directory.CreateTempSubdirectory("galactilog-refthumb-frames-").FullName;
            CacheDirectory = relocateCache
                ? Directory.CreateTempSubdirectory("galactilog-refthumb-cache-").FullName
                : null;

            // Four frames for M 31, so spec 11.4's three-frame walk has something to walk and is
            // not trivially satisfied by the only frame present, and one for M 42, so the case
            // covers more than one target and therefore more than one per-target cache key.
            // Dates ascend with the index, so frame 003 is the newest and is the one the pass
            // offers first.
            for (var i = 0; i < 4; i++)
            {
                var captureUtc = $"2025-03-1{i + 1}T02:00:00";
                var bytes = undecodableNewestFrame && i == 3
                    ? UndecodableLightFrame("M 31", captureUtc)
                    : LightFrame("M 31", captureUtc, seed: i * 17);
                File.WriteAllBytes(Path.Combine(Frames, $"m31_{i:D3}.fits"), bytes);
            }

            File.WriteAllBytes(
                Path.Combine(Frames, "m42_000.fits"),
                LightFrame("M 42", "2025-04-02T21:30:00", seed: 3));

            Host = AppHost.Build(Root, cliMode: true, httpHandlerOverride: new NoMatchHandler());

            Host.Services.GetRequiredService<SettingsStore>().SaveGeneral(new GeneralSettings
            {
                ScanRoots = [Frames],
                ScanFilters = ScanFilterConfig.Empty,
                ThumbnailCacheDir = CacheDirectory ?? "",
            });
        }

        /// <summary>The application's own writer, so a path assertion cannot pass while the
        /// application writes somewhere else.</summary>
        public AppWriter Writer => Host.Services.GetRequiredService<AppWriter>();

        public string ReferenceDirectory => Path.Combine(Writer.ThumbnailCacheRoot, "reference");

        public IReadOnlyList<string> ReferenceFiles
            => Directory.Exists(ReferenceDirectory)
                ? [.. Directory.GetFiles(ReferenceDirectory).Order(StringComparer.Ordinal)]
                : [];

        public string ConnectionString => DatabasePaths.BuildConnectionString(
            Path.Combine(Path.GetFullPath(Root), DatabasePaths.DatabaseFileName));

        public IReadOnlyList<(Guid Id, string PrimaryName, string? ReferenceThumbnailPath)> Targets()
        {
            using var context = new GalactiLogContext(GalactiLogContextOptions.Create(ConnectionString));
            return [.. context.Targets
                .OrderBy(target => target.PrimaryName)
                .Select(target => new ValueTuple<Guid, string, string?>(
                    target.Id, target.PrimaryName, target.ReferenceThumbnailPath))
                .ToList()];
        }

        public void Dispose()
        {
            Host.Dispose();
            Serilog.Log.CloseAndFlush();
            SqliteConnection.ClearAllPools();
            foreach (var directory in new[] { Root, Frames, CacheDirectory })
            {
                if (directory is not null && Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }
    }

    // ---- the pass runs, and writes one file per target ---------------------------------

    // The assertion TRACKING item 23 asks for.
    [Fact]
    public void Scan_RunsTheReferenceThumbnailPass_AndWritesAFilePerTarget()
    {
        using var fixture = new HostFixture();

        var (exitCode, stdout, _) = Run(fixture.Host, "scan");

        Assert.Equal(0, exitCode);
        Assert.Contains("Scan complete", stdout);

        var targets = fixture.Targets();
        Assert.Equal(2, targets.Count);

        var files = fixture.ReferenceFiles;
        Assert.Equal(2, files.Count);
        Assert.All(files, file => Assert.EndsWith(".jpg", file, StringComparison.Ordinal));

        // Keyed on the target id (spec 11.3), so the two files are the two targets' own.
        Assert.Equal(
            [.. targets.Select(target => target.Id.ToString("D")).Order(StringComparer.Ordinal)],
            [.. files.Select(file => Path.GetFileNameWithoutExtension(file)!).Order(StringComparer.Ordinal)]);

        // Real pixels, not an empty file: the encode produced a JPEG.
        Assert.All(files, file => Assert.True(new FileInfo(file).Length > 0, $"{file} is empty"));
    }

    [Fact]
    public void Scan_WritesReferenceThumbnailPathOnTheTargetRow()
    {
        using var fixture = new HostFixture();

        Assert.Equal(0, Run(fixture.Host, "scan", "--quiet").ExitCode);

        foreach (var (id, primaryName, referenceThumbnailPath) in fixture.Targets())
        {
            Assert.False(
                string.IsNullOrEmpty(referenceThumbnailPath),
                $"{primaryName} has no reference_thumbnail_path");

            // The stored value is the cache-relative path, and it names a file that exists.
            Assert.Equal($"reference/{id:D}.jpg", referenceThumbnailPath);
            Assert.True(File.Exists(fixture.Writer.ResolveThumbnailPath(referenceThumbnailPath!)));
        }
    }

    // The file-safety assertion: every path the pass produced resolves under the authorized
    // thumbnail cache root, through the application's own AppWriter rather than through a path
    // this test composed.
    [Fact]
    public void Scan_ReferenceThumbnailPath_IsUnderTheThumbnailCacheRoot()
    {
        using var fixture = new HostFixture();

        Assert.Equal(0, Run(fixture.Host, "scan", "--quiet").ExitCode);

        var cacheRoot = Path.GetFullPath(fixture.Writer.ThumbnailCacheRoot);
        foreach (var (_, _, referenceThumbnailPath) in fixture.Targets())
        {
            var absolute = fixture.Writer.ResolveThumbnailPath(referenceThumbnailPath!);
            Assert.StartsWith(cacheRoot, Path.GetFullPath(absolute), StringComparison.OrdinalIgnoreCase);
        }

        // And nothing landed in the scan root: the only directory under the app data root that
        // gained files is the cache.
        Assert.Empty(Directory.GetFiles(fixture.Frames, "*.jpg", SearchOption.AllDirectories));
    }

    // TRACKING hard rule 1 and spec 2.1: the application never deletes, moves or modifies a user
    // file. A scan that decodes four frames reads them and writes nothing back.
    [Fact]
    public void Scan_LeavesEveryFrameInTheScanRootByteIdentical()
    {
        using var fixture = new HostFixture();
        var before = Manifest(fixture.Frames);

        Assert.Equal(0, Run(fixture.Host, "scan", "--quiet").ExitCode);

        Assert.Equal(before, Manifest(fixture.Frames));
    }

    // Spec 11.3's relocatable cache root, which nothing else in Cli.Tests exercises.
    [Fact]
    public void Scan_ReferenceThumbnails_RespectTheConfiguredThumbnailCacheDir()
    {
        using var fixture = new HostFixture(relocateCache: true);

        Assert.Equal(0, Run(fixture.Host, "scan", "--quiet").ExitCode);

        Assert.NotNull(fixture.CacheDirectory);
        Assert.Equal(Path.GetFullPath(fixture.CacheDirectory!), Path.GetFullPath(fixture.Writer.ThumbnailCacheRoot));
        Assert.Equal(2, fixture.ReferenceFiles.Count);

        // Not under the app data default, which is where they would have gone had the setting
        // been ignored.
        var defaultReferenceDirectory = Path.Combine(fixture.Writer.AppDataRoot, "thumbnails", "reference");
        Assert.False(
            Directory.Exists(defaultReferenceDirectory) && Directory.GetFiles(defaultReferenceDirectory).Length > 0,
            "reference thumbnails were written under the app data root despite a configured cache directory");
    }

    // TRACKING item 17 and HANDOFF section 7: the cache serves an existing reference thumbnail as
    // a hit, so a rescan without force does not re-encode it. The CLI has never proved this.
    // LastWriteTimeUtc, not File.Exists: a rewrite would leave the file existing.
    [Fact]
    public void Scan_TwiceWithoutForce_DoesNotRewriteAnExistingReferenceThumbnail()
    {
        using var fixture = new HostFixture();

        Assert.Equal(0, Run(fixture.Host, "scan", "--quiet").ExitCode);
        var stamps = fixture.ReferenceFiles
            .ToDictionary(file => file, file => File.GetLastWriteTimeUtc(file), StringComparer.Ordinal);
        Assert.Equal(2, stamps.Count);

        Assert.Equal(0, Run(fixture.Host, "scan", "--quiet").ExitCode);

        Assert.Equal(
            stamps,
            fixture.ReferenceFiles.ToDictionary(
                file => file, File.GetLastWriteTimeUtc, StringComparer.Ordinal));
    }

    // Spec 10.4's task vocabulary: the CLI's non-quiet output reports the phase the pass runs in.
    [Fact]
    public void Scan_EmitsTheReferenceThumbnailProgressPhase()
    {
        using var fixture = new HostFixture();

        var (exitCode, stdout, _) = Run(fixture.Host, "scan");

        Assert.Equal(0, exitCode);
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .ToList();
        Assert.Contains(lines, line => line.StartsWith($"{ScanTaskNames.RefThumbnails} ", StringComparison.Ordinal));
    }

    // Spec 15's stdout contract, on a run that renders. A render logs, and in cliMode every log
    // line goes to stderr; this case is what proves the contract held when the pass ran.
    [Fact]
    public void Scan_WithJson_StillEmitsExactlyOneJsonObjectOnStdout()
    {
        using var fixture = new HostFixture();

        var (exitCode, stdout, _) = Run(fixture.Host, "scan", "--json");

        Assert.Equal(0, exitCode);

        // Parse throws on trailing content, so this is the "exactly one object" assertion.
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal("complete", document.RootElement.GetProperty("state").GetString());
        Assert.Equal(5, document.RootElement.GetProperty("completed").GetInt32());
        Assert.DoesNotContain(ScanTaskNames.RefThumbnails, stdout);
        Assert.StartsWith("{", stdout.TrimStart(), StringComparison.Ordinal);
        Assert.EndsWith("}", stdout.TrimEnd(), StringComparison.Ordinal);

        // The pass really ran on this invocation, or the case would prove nothing about a render.
        Assert.Equal(2, fixture.ReferenceFiles.Count);
    }

    // A failed thumbnail is not a failed scan (ReferenceThumbnailPass logs per target and counts
    // it), and the pass falls through to the target's next candidate frame.
    [Fact]
    public void Scan_OnAFrameThatCannotBeDecoded_StillExitsZero()
    {
        using var fixture = new HostFixture(undecodableNewestFrame: true);

        var (exitCode, stdout, _) = Run(fixture.Host, "scan", "--json", "--quiet");

        Assert.Equal(0, exitCode);

        // The undecodable frame was ingested like any other LIGHT frame, so it really is one of
        // M 31's candidates and this case is not passing because the frame was dropped earlier.
        // Task 8 review minor 3: read from the invocation whose exit code is asserted above, not
        // from a second scan run purely for its payload. One behaviour, one run, half the cost of
        // the most expensive case in this file.
        using (var document = JsonDocument.Parse(stdout))
        {
            Assert.Equal(5, document.RootElement.GetProperty("discovered").GetInt32());
        }

        using (var context = new GalactiLogContext(GalactiLogContextOptions.Create(fixture.ConnectionString)))
        {
            Assert.Equal(5, context.Images.Count());
        }

        // Both targets still have a thumbnail: M 31's newest frame declined and the next one
        // rendered.
        Assert.Equal(2, fixture.ReferenceFiles.Count);
        Assert.All(fixture.Targets(), target => Assert.False(string.IsNullOrEmpty(target.ReferenceThumbnailPath)));
    }

    // ---- helpers -----------------------------------------------------------------------

    // Name, length and content hash of every file under a directory, so "byte-identical" is an
    // equality of values rather than a walk with its own assertions.
    private static IReadOnlyList<(string Name, long Length, string Sha256)> Manifest(string directory)
        => [.. Directory
            .GetFiles(directory, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(file => (
                Name: Path.GetRelativePath(directory, file),
                Length: new FileInfo(file).Length,
                Sha256: Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))))];
}
