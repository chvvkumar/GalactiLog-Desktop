using System.Text.Json;
using GalactiLog.Cli;
using GalactiLog.Core.Tests.Fixtures;
using Xunit;

namespace GalactiLog.Cli.Tests;

// Task 8: exercises RunInspect/RunDumpHeaders against real fixture bytes written to a temp
// file (CliDispatcher takes a path, not a stream). This is tests/**, outside
// FileSafetyTest's src/** scan, so File.WriteAllBytes here is fine.
public class InspectAndDumpHeadersTests : IDisposable
{
    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private static readonly Func<IServiceProvider> Services = () => new NullServiceProvider();

    private readonly string _tempDir = Directory.CreateTempSubdirectory("galactilog-cli-tests-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string WriteFixture(string fileName, MemoryStream content)
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllBytes(path, content.ToArray());
        return path;
    }

    private static (int ExitCode, string Out, string Err) Run(params string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var outWriter = new StringWriter();
        var errorWriter = new StringWriter();
        Console.SetOut(outWriter);
        Console.SetError(errorWriter);
        try
        {
            CliDispatcher.TryRun(args, Services, out var exitCode);
            return (exitCode, outWriter.ToString(), errorWriter.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static FitsBuilder ValidMinimalFits() =>
        new FitsBuilder().Card("SIMPLE", true).Card("BITPIX", 16L).Card("NAXIS", 0L);

    // --- inspect ---

    [Fact]
    public void Inspect_ValidFitsFixture_ExitsZeroAndPrintsFieldLinesWithProvenance()
    {
        var path = WriteFixture("m31.fits", ValidMinimalFits()
            .Card("OBJECT", "M31")
            .Card("EXPTIME", 30.0)
            .Card("FILTER", "Ha")
            .Build());

        var (exitCode, stdout, stderr) = Run("inspect", path);

        Assert.Equal(0, exitCode);
        Assert.Contains("object_name = M31  (OBJECT)", stdout);
        Assert.Contains("exposure_time = 30  (EXPTIME)", stdout);
        Assert.Contains("filter_used = Ha  (FILTER)", stdout);
    }

    [Fact]
    public void Inspect_Json_ExitsZeroAndParsesWithProvenanceMap()
    {
        var path = WriteFixture("m31.fits", ValidMinimalFits()
            .Card("OBJECT", "M31")
            .Build());

        var (exitCode, stdout, _) = Run("inspect", path, "--json");

        Assert.Equal(0, exitCode);
        using var doc = JsonDocument.Parse(stdout);
        var provenance = doc.RootElement.GetProperty("provenance");
        Assert.Equal("OBJECT", provenance.GetProperty("object_name").GetString());
    }

    [Fact]
    public void Inspect_NonexistentPath_ExitsThreeWithFileNotFoundOnStderr()
    {
        var path = Path.Combine(_tempDir, "nonexistent.fits");

        var (exitCode, stdout, stderr) = Run("inspect", path);

        Assert.Equal(3, exitCode);
        Assert.Contains("file not found", stderr);
        Assert.Empty(stdout);
    }

    [Fact]
    public void Inspect_UnsupportedExtension_ExitsThree()
    {
        var path = Path.Combine(_tempDir, "notes.txt");
        File.WriteAllText(path, "not a frame");

        var (exitCode, _, stderr) = Run("inspect", path);

        Assert.Equal(3, exitCode);
        Assert.Contains("unsupported file extension", stderr);
    }

    [Fact]
    public void Inspect_RejectedFitsFixture_ExitsThreeWithRejectionReasonOnStderr()
    {
        // Missing SIMPLE card -> the reader rejects the file.
        var path = WriteFixture("bad.fits", new FitsBuilder().Card("BITPIX", 16L).Card("NAXIS", 0L).Build());

        var (exitCode, stdout, stderr) = Run("inspect", path);

        Assert.Equal(3, exitCode);
        Assert.False(string.IsNullOrWhiteSpace(stderr));
        Assert.Empty(stdout);
    }

    [Fact]
    public void Inspect_Quiet_SuppressesWarningsOnStderr()
    {
        // GAIN as a non-integer value triggers a MetadataExtractor warning.
        var path = WriteFixture("gain.fits", ValidMinimalFits()
            .Card("GAIN", 12.5)
            .Build());

        var (exitCode, _, stderr) = Run("inspect", path, "--quiet");

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);
    }

    [Fact]
    public void Inspect_LockedFile_ExitsThreeInsteadOfSeventy()
    {
        var path = WriteFixture("locked.fits", ValidMinimalFits().Build());

        // UserFiles.OpenRead uses FileShare.ReadWrite; a FileShare.None holder makes that
        // open fail with a sharing-violation IOException.
        using var lockHandle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var (exitCode, stdout, stderr) = Run("inspect", path);

        Assert.Equal(3, exitCode);
        Assert.False(string.IsNullOrWhiteSpace(stderr));
        Assert.Empty(stdout);
    }

    [Fact]
    public void Inspect_ValidXisfFixture_ExitsZeroWithExpectedFieldLines()
    {
        var path = WriteFixture("m31.xisf", new XisfBuilder()
            .Geometry(2, 2, 1)
            .SampleFormat("UInt16")
            .Pixels(new float[,] { { 1, 2 }, { 3, 4 } })
            .FitsKeyword("OBJECT", "M31")
            .Build());

        var (exitCode, stdout, _) = Run("inspect", path);

        Assert.Equal(0, exitCode);
        Assert.Contains("object_name = M31  (OBJECT)", stdout);
    }

    // --- dump-headers ---

    [Fact]
    public void DumpHeaders_TwoCommentCards_PrintsTwoSeparateLinesInFileOrder()
    {
        var path = WriteFixture("comments.fits", ValidMinimalFits()
            .Comment("first comment")
            .Comment("second comment")
            .Build());

        var (exitCode, stdout, _) = Run("dump-headers", path);

        Assert.Equal(0, exitCode);
        var lines = stdout.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.Contains("COMMENT = first comment", lines);
        Assert.Contains("COMMENT = second comment", lines);
        Assert.True(lines.IndexOf("COMMENT = first comment") < lines.IndexOf("COMMENT = second comment"));
    }

    [Fact]
    public void DumpHeaders_Json_CommentKeyIsTwoElementArray()
    {
        var path = WriteFixture("comments.fits", ValidMinimalFits()
            .Comment("first comment")
            .Comment("second comment")
            .Build());

        var (exitCode, stdout, _) = Run("dump-headers", path, "--json");

        Assert.Equal(0, exitCode);
        using var doc = JsonDocument.Parse(stdout);
        var comments = doc.RootElement.GetProperty("COMMENT");
        Assert.Equal(2, comments.GetArrayLength());
    }

    [Fact]
    public void DumpHeaders_NonexistentPath_ExitsThree()
    {
        var path = Path.Combine(_tempDir, "nonexistent.fits");

        var (exitCode, _, stderr) = Run("dump-headers", path);

        Assert.Equal(3, exitCode);
        Assert.Contains("file not found", stderr);
    }

    [Fact]
    public void DumpHeaders_ValidXisfFixture_PrintsFitsKeywordAndPropertyEntries()
    {
        var path = WriteFixture("m31.xisf", new XisfBuilder()
            .Geometry(2, 2, 1)
            .SampleFormat("UInt16")
            .Pixels(new float[,] { { 1, 2 }, { 3, 4 } })
            .FitsKeyword("OBJECT", "M31")
            .Property("Instrument:Filter:Name", "String", "Ha")
            .Build());

        var (exitCode, stdout, _) = Run("dump-headers", path);

        Assert.Equal(0, exitCode);
        Assert.Contains("OBJECT = M31", stdout);
        Assert.Contains("Instrument:Filter:Name = Ha", stdout);
    }

    // Review item 13: the text-mode loop skipped any field missing from provenance, so the
    // two derived source fields (eccentricity_source, guiding_rms_source) never printed
    // even when they carried a value.
    [Fact]
    public void Inspect_TextMode_PrintsDerivedSourceFieldsThatHaveNoProvenance()
    {
        var path = WriteFixture("ecc.fits", ValidMinimalFits()
            .Hierarch("ECCENTRICITY", "0.42")
            .Build());

        var (exitCode, stdout, _) = Run("inspect", path);

        Assert.Equal(0, exitCode);
        Assert.Contains("eccentricity_source = header", stdout);
        // No provenance entry of its own, so it prints with no trailing "(source)" suffix.
        Assert.DoesNotContain("eccentricity_source = header  (", stdout);
    }

    [Fact]
    public void Inspect_TextMode_StillOmitsFieldsWithNoValueAndNoProvenance()
    {
        var path = WriteFixture("bare.fits", ValidMinimalFits().Build());

        var (exitCode, stdout, _) = Run("inspect", path);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("eccentricity_source", stdout);
        Assert.DoesNotContain("guiding_rms_source", stdout);
    }

    [Fact]
    public void Inspect_TruncatedXisf_ExitsThreeWithoutStackTrace()
    {
        var path = Path.Combine(_tempDir, "tiny.xisf");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5 });

        var (exitCode, _, stderr) = Run("inspect", path);

        Assert.Equal(3, exitCode);
        Assert.Contains("not a valid XISF file", stderr);
        Assert.DoesNotContain("   at ", stderr);
    }
}
