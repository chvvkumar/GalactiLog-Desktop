using System.Diagnostics;
using GalactiLog.Core.Wbpp;
using Xunit;
using Xunit.Abstractions;

namespace GalactiLog.Core.Tests.Wbpp;

// The source-side lister over a real temp tree with fabricated names.
public sealed class SourceSubfoldersTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "source-subfolders-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;
    private string? _junction;

    public SourceSubfoldersTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            // Deletes the junction itself first, never what it targets.
            if (_junction is not null)
            {
                Directory.Delete(_junction, recursive: false);
            }
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup only.
        }
    }

    [Fact]
    public void Children_ListsDirectoriesOnly_InCaseInsensitiveNameOrder()
    {
        Directory.CreateDirectory(Path.Combine(_root, "b"));
        Directory.CreateDirectory(Path.Combine(_root, "A"));
        Directory.CreateDirectory(Path.Combine(_root, "c"));
        File.WriteAllText(Path.Combine(_root, "file.txt"), "");

        var children = SourceSubfolders.OnDisk(_root);

        Assert.Equal(
            [Path.Combine(_root, "A"), Path.Combine(_root, "b"), Path.Combine(_root, "c")],
            children);
    }

    [Fact]
    public void Children_IsEmptyForAFolderWithNoSubfolder_AndNullForOneThatCannotBeRead()
    {
        Assert.Empty(SourceSubfolders.OnDisk(_root)!);
        Assert.Null(SourceSubfolders.Children(_root, _ => throw new UnauthorizedAccessException()));
        Assert.Null(SourceSubfolders.Children(_root, _ => throw new IOException()));
        Assert.Null(SourceSubfolders.OnDisk(Path.Combine(_root, "missing")));
    }

    [Fact]
    public void Children_LeavesOutAReparsePointDirectory()
    {
        var real = Path.Combine(_root, "real");
        var outside = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(real);
        Directory.CreateDirectory(outside);
        var link = Path.Combine(_root, "linked");
        using var mklink = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{link}\" \"{outside}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        })!;
        mklink.WaitForExit();
        if (mklink.ExitCode != 0)
        {
            _output.WriteLine($"Skipped: the OS refused a junction, mklink /J exited {mklink.ExitCode}");
            return;
        }
        _junction = link;

        var children = SourceSubfolders.OnDisk(_root);

        Assert.Equal([outside, real], children);
    }
}
