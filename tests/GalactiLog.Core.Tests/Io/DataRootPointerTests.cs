using System;
using System.IO;
using System.Linq;
using GalactiLog.Core.Io;
using Xunit;

namespace GalactiLog.Core.Tests.Io;

// The data location pointer document (spec 17.2, Phase 10 Task 9).
//
// Every case constructs its AppWriter with an explicit dataRootPointerPath under its own temp
// directory, so AppWriter.DefaultDataRootPointerPath (the real %APPDATA%\GalactiLog\datapath.json)
// is never the destination of a write here.
public class DataRootPointerTests : IDisposable
{
    private readonly string _scratch;
    private readonly string _pointerPath;
    private readonly DataRootPointer _pointer;

    public DataRootPointerTests()
    {
        _scratch = Path.Combine(Path.GetTempPath(), "GalactiLogPointerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratch);
        // Two levels below the temp root, so the writer's own directory creation is exercised.
        _pointerPath = Path.Combine(_scratch, "roaming", "GalactiLog", "datapath.json");
        _pointer = new DataRootPointer(
            new AppWriter(Path.Combine(_scratch, "appdata"), dataRootPointerPath: _pointerPath));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_scratch))
            {
                Directory.Delete(_scratch, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup only.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Write_CreatesThePointerDirectoryAndFile()
    {
        Assert.False(File.Exists(_pointerPath));

        _pointer.RecordRoot(@"D:\Astro\GalactiLogData");

        Assert.True(File.Exists(_pointerPath));
        Assert.Equal(_pointerPath, _pointer.Path);
    }

    [Fact]
    public void RequestMove_WritesPendingRootAndLeavesDataRootAlone()
    {
        _pointer.RecordRoot(@"D:\Astro\GalactiLogData");

        _pointer.RequestMove(@"E:\Moved\GalactiLogData");

        var stored = DataRootPointer.Read(_pointerPath);
        Assert.NotNull(stored);
        Assert.Equal(@"D:\Astro\GalactiLogData", stored!.DataRoot);
        Assert.Equal(@"E:\Moved\GalactiLogData", stored.PendingRoot);
        Assert.Null(stored.PreviousRoot);
    }

    [Fact]
    public void CompleteMove_PromotesPendingAndRecordsThePreviousRoot()
    {
        _pointer.RecordRoot(@"D:\Astro\GalactiLogData");
        _pointer.RequestMove(@"E:\Moved\GalactiLogData");

        _pointer.CompleteMove(@"E:\Moved\GalactiLogData", @"D:\Astro\GalactiLogData");

        var stored = DataRootPointer.Read(_pointerPath);
        Assert.NotNull(stored);
        Assert.Equal(@"E:\Moved\GalactiLogData", stored!.DataRoot);
        Assert.Equal(@"D:\Astro\GalactiLogData", stored.PreviousRoot);
        Assert.Null(stored.PendingRoot);
    }

    [Fact]
    public void CancelMove_ClearsPendingAndChangesNothingElse()
    {
        _pointer.RecordRoot(@"D:\Astro\GalactiLogData");
        _pointer.CompleteMove(@"D:\Astro\GalactiLogData", @"C:\Old\GalactiLogData");
        _pointer.RequestMove(@"E:\Moved\GalactiLogData");

        _pointer.CancelMove();

        var stored = DataRootPointer.Read(_pointerPath);
        Assert.NotNull(stored);
        Assert.Null(stored!.PendingRoot);
        Assert.Equal(@"D:\Astro\GalactiLogData", stored.DataRoot);
        Assert.Equal(@"C:\Old\GalactiLogData", stored.PreviousRoot);
    }

    [Fact]
    public void RecordRoot_WritesDataRootAndKeepsPendingAndPrevious()
    {
        _pointer.CompleteMove(@"D:\Astro\GalactiLogData", @"C:\Old\GalactiLogData");
        _pointer.RequestMove(@"E:\Moved\GalactiLogData");

        _pointer.RecordRoot(@"F:\Elsewhere\GalactiLogData");

        var stored = DataRootPointer.Read(_pointerPath);
        Assert.NotNull(stored);
        Assert.Equal(@"F:\Elsewhere\GalactiLogData", stored!.DataRoot);
        Assert.Equal(@"E:\Moved\GalactiLogData", stored.PendingRoot);
        Assert.Equal(@"C:\Old\GalactiLogData", stored.PreviousRoot);
    }

    [Fact]
    public void Read_UnknownKeys_ArePreservedOnTheNextWrite()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_pointerPath)!);
        File.WriteAllText(
            _pointerPath,
            "{\n  \"data_root\": \"D:\\\\Astro\\\\GalactiLogData\",\n  \"future_key\": 1\n}");

        _pointer.RequestMove(@"E:\Moved\GalactiLogData");

        var text = File.ReadAllText(_pointerPath);
        Assert.Contains("future_key", text, StringComparison.Ordinal);
        var stored = DataRootPointer.Read(_pointerPath);
        Assert.NotNull(stored);
        Assert.NotNull(stored!.Extra);
        Assert.True(stored.Extra!.ContainsKey("future_key"));
    }

    // Review finding I2: the write goes to a sibling temp file and is then moved over the target,
    // so a reader never sees a half-written document and no staging file is left behind.
    [Fact]
    public void Write_LeavesNoStagingFileBesideThePointer()
    {
        _pointer.RecordRoot(@"D:\Astro\GalactiLogData");
        _pointer.RequestMove(@"E:\Moved\GalactiLogData");

        var directory = Path.GetDirectoryName(_pointerPath)!;

        Assert.Equal(
            [Path.GetFileName(_pointerPath)],
            Directory.EnumerateFiles(directory).Select(path => Path.GetFileName(path)!).ToArray());
        Assert.Equal(@"E:\Moved\GalactiLogData", DataRootPointer.Read(_pointerPath)!.PendingRoot);
    }

    [Fact]
    public void Read_MissingFile_ReturnsNull()
    {
        Assert.Null(DataRootPointer.Read(Path.Combine(_scratch, "absent", "datapath.json")));
    }

    [Fact]
    public void Read_MalformedJson_ReturnsNullAndDoesNotThrow()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_pointerPath)!);
        File.WriteAllText(_pointerPath, "{ not json");

        Assert.Null(DataRootPointer.Read(_pointerPath));
    }

    [Fact]
    public void Write_StoresFullPathsWithNoTrailingSeparator()
    {
        _pointer.RecordRoot(@"D:\Astro\");
        var withSeparator = DataRootPointer.Read(_pointerPath)!.DataRoot;

        _pointer.RecordRoot(@"D:\Astro");
        var withoutSeparator = DataRootPointer.Read(_pointerPath)!.DataRoot;

        Assert.Equal(withSeparator, withoutSeparator);
        Assert.Equal(@"D:\Astro", withoutSeparator);
    }
}
