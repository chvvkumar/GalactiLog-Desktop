using System.Text.RegularExpressions;
using GalactiLog.App.Services;
using Xunit;

namespace GalactiLog.App.Tests.Services;

// Design-spec 12.1's shallow probe, with questions.md Q33's two limits. Most cases run against a
// real temp tree, because the probe's whole job is to read directory entries. The three that
// cannot be arranged on a real filesystem on any machine (an unreadable subtree, a thousand files,
// a cancellation mid-enumeration) drive the type's internal enumeration seam instead; the
// production constructor takes no seam at all.
public class SupportedFileProbeTests
{
    private sealed class TempTree : IDisposable
    {
        public TempTree()
        {
            Root = Path.Combine(Path.GetTempPath(), $"galactilog-probe-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string Dir(params string[] segments)
        {
            var path = Path.Combine([Root, .. segments]);
            Directory.CreateDirectory(path);
            return path;
        }

        public void File(string directory, string name)
            => System.IO.File.WriteAllText(Path.Combine(directory, name), "x");

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp tree is not a test failure.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public void Count_CountsTheFourSupportedExtensions_CaseInsensitively()
    {
        using var tree = new TempTree();
        tree.File(tree.Root, "a.fits");
        tree.File(tree.Root, "b.FIT");
        tree.File(tree.Root, "c.Fts");
        tree.File(tree.Root, "d.XISF");

        Assert.Equal(4, new SupportedFileProbe().Count(tree.Root));
    }

    [Fact]
    public void Count_IgnoresUnsupportedExtensions()
    {
        using var tree = new TempTree();
        tree.File(tree.Root, "keep.fits");
        tree.File(tree.Root, "notes.txt");
        tree.File(tree.Root, "preview.jpg");
        tree.File(tree.Root, "session.csv");
        tree.File(tree.Root, "noextension");

        Assert.Equal(1, new SupportedFileProbe().Count(tree.Root));
    }

    [Fact]
    public void Count_DescendsAtMostTheDepthLimit()
    {
        using var tree = new TempTree();
        tree.File(tree.Root, "depth0.fits");
        tree.File(tree.Dir("a"), "depth1.fits");
        tree.File(tree.Dir("a", "b"), "depth2.fits");
        tree.File(tree.Dir("a", "b", "c"), "depth3.fits");
        tree.File(tree.Dir("a", "b", "c", "d"), "depth4.fits");

        // MaxDepth is 3, so the four levels at and above it are counted and the fifth is not.
        Assert.Equal(3, SupportedFileProbe.MaxDepth);
        Assert.Equal(4, new SupportedFileProbe().Count(tree.Root));
    }

    [Fact]
    public void Count_StopsAtTheCountLimit()
    {
        var probe = new SupportedFileProbe(
            files: directory => Enumerable.Range(0, 5000).Select(index => $@"{directory}\frame{index}.fits"),
            directories: _ => []);

        Assert.Equal(SupportedFileProbe.MaxCounted, probe.Count(@"C:\anywhere"));
        Assert.Equal(1000, SupportedFileProbe.MaxCounted);
    }

    [Fact]
    public void Count_AtTheCeiling_RendersOrMore()
    {
        Assert.Equal("1000 or more", SupportedFileProbe.Format(SupportedFileProbe.MaxCounted));
        Assert.Equal("7", SupportedFileProbe.Format(7));
    }

    [Fact]
    public void Count_AnUnreadableSubdirectory_IsSkipped_AndTheWalkContinues()
    {
        const string root = @"C:\library";
        const string denied = @"C:\library\denied";
        const string readable = @"C:\library\readable";

        var probe = new SupportedFileProbe(
            files: directory => directory switch
            {
                denied => throw new UnauthorizedAccessException("denied"),
                readable => [@"C:\library\readable\a.fits", @"C:\library\readable\b.fits"],
                _ => [],
            },
            directories: directory => directory == root ? [denied, readable] : []);

        Assert.Equal(2, probe.Count(root));
    }

    [Fact]
    public void Count_AnUnlistableDirectory_IsSkipped_AndTheWalkContinues()
    {
        const string root = @"C:\library";

        var probe = new SupportedFileProbe(
            files: directory => directory == root ? [@"C:\library\a.fits"] : [],
            directories: _ => throw new UnauthorizedAccessException("no listing"));

        Assert.Equal(1, probe.Count(root));
    }

    [Fact]
    public void Count_AMissingFolder_ReturnsZero_AndDoesNotThrow()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"galactilog-missing-{Guid.NewGuid():N}");

        Assert.Equal(0, new SupportedFileProbe().Count(missing));
        Assert.Equal(0, new SupportedFileProbe().Count(@"\\no-such-host\no-such-share\astro"));
        Assert.Equal(0, new SupportedFileProbe().Count("   "));
    }

    [Fact]
    public void Count_IsCancellable()
    {
        using var source = new CancellationTokenSource();
        var probe = new SupportedFileProbe(
            files: directory => Endless(directory, source),
            directories: _ => []);

        Assert.Throws<OperationCanceledException>(() => probe.Count(@"C:\anywhere", source.Token));
    }

    // By construction. FileSafetyTest is the backstop over all of src/**; this asserts the rule the
    // brief names for this type, so a future edit that opens a file fails here with the rule it
    // broke.
    [Fact]
    public void Count_OpensNoFile()
    {
        var source = ReadProbeSource();

        Assert.DoesNotContain("UserFiles.OpenRead", source, StringComparison.Ordinal);
        Assert.DoesNotContain("UserFiles.ReadAll", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FileStream", source, StringComparison.Ordinal);
        Assert.DoesNotContain("StreamReader", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.CreateDirectory", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FrameReader.TryRead", source, StringComparison.Ordinal);
    }

    // Design-lessons rule 1: the probe's extension set is FileWalker's, reached through the same
    // call FileWalker makes, and not a second literal.
    [Fact]
    public void Count_UsesFileWalkersExtensionSet_NotASecondLiteral()
    {
        var source = StripComments(ReadProbeSource());

        Assert.Contains("FrameReader.IsSupported(", source, StringComparison.Ordinal);
        foreach (var extension in new[] { ".fits", ".fit", ".fts", ".xisf" })
        {
            Assert.DoesNotContain(extension, source, StringComparison.OrdinalIgnoreCase);
        }

        // The same call FileWalker uses, so the two cannot drift apart.
        var walker = StripComments(File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "GalactiLog.Core", "Scanning", "FileWalker.cs")));
        Assert.Contains("FrameReader.IsSupported(", walker, StringComparison.Ordinal);
    }

    // An endless sequence that cancels itself after a few entries, so the probe's own
    // ThrowIfCancellationRequested is what ends the walk rather than the sequence running out.
    private static IEnumerable<string> Endless(string directory, CancellationTokenSource source)
    {
        var index = 0;
        while (true)
        {
            if (index == 3)
            {
                source.Cancel();
            }

            yield return $@"{directory}\frame{index++}.fits";
        }
    }

    private static string ReadProbeSource()
        => File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "GalactiLog.App", "Services", "SupportedFileProbe.cs"));

    private static string StripComments(string source)
        => Regex.Replace(Regex.Replace(source, @"/\*[\s\S]*?\*/", ""), @"//.*?$", "", RegexOptions.Multiline);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GalactiLog.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("GalactiLog.sln not found.");
    }
}
