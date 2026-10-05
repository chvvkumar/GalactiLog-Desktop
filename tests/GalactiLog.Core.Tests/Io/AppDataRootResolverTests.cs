using System;
using System.IO;
using GalactiLog.Core.Io;
using Xunit;

namespace GalactiLog.Core.Tests.Io;

// Spec 17.2's resolution order (Phase 10 Task 9).
//
// Every case builds its own temp directory and its own pointer path, and every case that resolves
// a default passes an AppDataRootFolders over temp directories. Nothing here reads or writes the
// machine's real %APPDATA%\GalactiLog\datapath.json, %LOCALAPPDATA%\GalactiLogData or
// %LOCALAPPDATA%\GalactiLog, and nothing here creates a directory under either.
public class AppDataRootResolverTests : IDisposable
{
    private readonly string _scratch;

    public AppDataRootResolverTests()
    {
        _scratch = Path.Combine(Path.GetTempPath(), "GalactiLogResolverTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratch);
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

    private string Folder(string name)
    {
        var path = Path.Combine(_scratch, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private string PointerPath() => Path.Combine(_scratch, "pointer", "datapath.json");

    private AppDataRootFolders Folders() => new(Folder("default"), Folder("legacy"));

    // Writes a pointer document by hand, which is what a previous run or a user edit leaves.
    private string WritePointer(string json)
    {
        var path = PointerPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        return path;
    }

    private static string Json(string? dataRoot, string? pendingRoot = null, string? previousRoot = null)
        => "{\n"
            + $"  \"data_root\": {Quote(dataRoot)},\n"
            + $"  \"pending_root\": {Quote(pendingRoot)},\n"
            + $"  \"previous_root\": {Quote(previousRoot)}\n"
            + "}";

    private static string Quote(string? value)
        => value is null ? "null" : "\"" + value.Replace("\\", "\\\\") + "\"";

    [Fact]
    public void Resolve_ExplicitOverride_WinsOverEnvironmentAndPointer()
    {
        var chosen = Folder("chosen");
        var environment = Folder("environment");
        var pointerRoot = Folder("pointer-root");
        var pointer = WritePointer(Json(pointerRoot));

        var resolution = AppDataRootResolver.Resolve(chosen, environment, pointer, Folders());

        Assert.Equal(Path.GetFullPath(chosen), resolution.Root);
        Assert.Equal(AppDataRootSource.ExplicitOverride, resolution.Source);
    }

    [Fact]
    public void Resolve_ExplicitOverride_CarriesNoPendingOrPreviousRoot()
    {
        var chosen = Folder("chosen");
        var pointer = WritePointer(Json(Folder("pointer-root"), Folder("pending"), Folder("previous")));

        var resolution = AppDataRootResolver.Resolve(chosen, null, pointer, Folders());

        Assert.Null(resolution.PendingRoot);
        Assert.Null(resolution.PreviousRoot);
        Assert.Null(resolution.PointerWarning);
    }

    [Fact]
    public void Resolve_EnvironmentVariable_WinsOverPointer()
    {
        var environment = Folder("environment");
        var pointer = WritePointer(Json(Folder("pointer-root"), Folder("pending"), Folder("previous")));

        var resolution = AppDataRootResolver.Resolve(null, environment, pointer, Folders());

        Assert.Equal(Path.GetFullPath(environment), resolution.Root);
        Assert.Equal(AppDataRootSource.EnvironmentVariable, resolution.Source);
        Assert.Null(resolution.PendingRoot);
        Assert.Null(resolution.PreviousRoot);
    }

    [Fact]
    public void Resolve_Pointer_WinsOverDefault()
    {
        var pointerRoot = Folder("pointer-root");
        var pending = Folder("pending");
        var previous = Folder("previous");
        var pointer = WritePointer(Json(pointerRoot, pending, previous));

        var resolution = AppDataRootResolver.Resolve(null, null, pointer, Folders());

        Assert.Equal(Path.GetFullPath(pointerRoot), resolution.Root);
        Assert.Equal(AppDataRootSource.Pointer, resolution.Source);
        Assert.Equal(Path.GetFullPath(pending), resolution.PendingRoot);
        Assert.Equal(Path.GetFullPath(previous), resolution.PreviousRoot);
    }

    [Fact]
    public void Resolve_NoPointerFile_ReturnsDefaultRoot()
    {
        var folders = Folders();

        var resolution = AppDataRootResolver.Resolve(null, null, PointerPath(), folders);

        Assert.Equal(Path.GetFullPath(folders.DefaultRoot), resolution.Root);
        Assert.Equal(AppDataRootSource.Default, resolution.Source);
        Assert.Null(resolution.PointerWarning);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("   ")]
    public void Resolve_MalformedPointerFile_ReturnsDefaultAndReportsTheReason(string contents)
    {
        var folders = Folders();
        var pointer = WritePointer(contents);

        var resolution = AppDataRootResolver.Resolve(null, null, pointer, folders);

        Assert.Equal(Path.GetFullPath(folders.DefaultRoot), resolution.Root);
        Assert.Equal(AppDataRootSource.Default, resolution.Source);
        Assert.False(string.IsNullOrWhiteSpace(resolution.PointerWarning));
    }

    [Theory]
    [InlineData("Astro")]
    [InlineData("")]
    [InlineData(@"C:\")]
    [InlineData(@"\\nas\share")]
    public void Resolve_PointerNamingARelativeOrDriveRootPath_ReturnsDefault(string value)
    {
        var folders = Folders();
        var pointer = WritePointer(Json(value));

        var resolution = AppDataRootResolver.Resolve(null, null, pointer, folders);

        Assert.Equal(Path.GetFullPath(folders.DefaultRoot), resolution.Root);
        Assert.Equal(AppDataRootSource.Default, resolution.Source);
        Assert.False(string.IsNullOrWhiteSpace(resolution.PointerWarning));
    }

    [Fact]
    public void Resolve_PointerNamingAMissingDirectory_ThrowsAndCreatesNothing()
    {
        var folders = Folders();
        var missing = Path.Combine(_scratch, "disconnected", "GalactiLogData");
        var pointer = WritePointer(Json(missing));

        var thrown = Assert.Throws<AppDataRootUnavailableException>(
            () => AppDataRootResolver.Resolve(null, null, pointer, folders));

        Assert.Equal(Path.GetFullPath(missing), thrown.Path);
        Assert.Equal(pointer, thrown.PointerPath);
        Assert.Equal(AppDataRootResolver.FolderDoesNotExist, thrown.Reason);
        Assert.False(Directory.Exists(missing));
        Assert.Contains(missing, thrown.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(pointer, thrown.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    // Moved verbatim from AppHostTests.IsUsableAppDataOverride_RejectsRelativeEmptyOrDriveRoot,
    // with the same five rows: the predicate moved to AppDataRootResolver and the test moved with
    // it.
    [Theory]
    [InlineData("Astro")] // relative
    [InlineData("")]
    [InlineData(null)]
    [InlineData(@"C:\")] // bare drive root
    [InlineData(@"\\nas\share")] // bare UNC share root
    public void IsUsableRoot_RejectsRelativeEmptyOrDriveRoot(string? value)
    {
        Assert.False(AppDataRootResolver.IsUsableRoot(value));
    }

    [Fact]
    public void IsUsableRoot_AcceptsAFullyQualifiedDirectory()
    {
        Assert.True(AppDataRootResolver.IsUsableRoot(Folder("usable")));
    }

    [Fact]
    public void DefaultRoot_IsNotTheLegacyRoot()
    {
        Assert.NotEqual(AppDataRootResolver.DefaultRoot(), AppDataRootResolver.LegacyRoot());
        Assert.EndsWith(AppDataRootResolver.DefaultRootFolderName, AppDataRootResolver.DefaultRoot(), StringComparison.Ordinal);
        Assert.EndsWith(AppDataRootResolver.LegacyRootFolderName, AppDataRootResolver.LegacyRoot(), StringComparison.Ordinal);
    }

    [Fact]
    public void SourceLabel_NamesEverySource()
    {
        Assert.Equal("override", AppDataRootResolver.SourceLabel(AppDataRootSource.ExplicitOverride));
        Assert.Equal("environment", AppDataRootResolver.SourceLabel(AppDataRootSource.EnvironmentVariable));
        Assert.Equal("pointer", AppDataRootResolver.SourceLabel(AppDataRootSource.Pointer));
        Assert.Equal("default", AppDataRootResolver.SourceLabel(AppDataRootSource.Default));
    }
}
