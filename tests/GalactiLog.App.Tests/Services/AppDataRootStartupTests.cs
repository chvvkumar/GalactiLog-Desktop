using GalactiLog.Core.Io;
using GalactiLog.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace GalactiLog.App.Tests.Services;

// Spec 17.2's startup resolution, the pending move, the legacy adoption and the unavailable-root
// refusal, driven through the real AppHost.Build (Phase 10 Task 9).
//
// HOW THESE STAY OFF THE REAL PROFILE. Every case passes AppHost.Build's two internal Task 9
// seams: a pointerPathOverride under this class's own temp directory, so the real
// %APPDATA%\GalactiLog\datapath.json is never read or written, and a folderOverride whose default
// and legacy roots are temp directories, so the real %LOCALAPPDATA%\GalactiLogData and
// %LOCALAPPDATA%\GalactiLog are never resolved, created, read or copied from. Every case also
// clears GALACTILOG_APPDATA for its duration and restores it afterwards, so a variable set on the
// machine cannot silently turn a pointer case into an environment case.
//
// AppHost.Build sets the process-wide Serilog.Log.Logger and this class clears the process-wide
// SQLite pool, exactly as AppHostTests does; AssemblyInfo.cs disables cross-collection
// parallelization for the whole assembly, so nothing else runs alongside.
public sealed class AppDataRootStartupTests : IDisposable
{
    private readonly string _scratch;
    private readonly string _pointerPath;
    private readonly AppDataRootFolders _folders;

    public AppDataRootStartupTests()
    {
        _scratch = Path.Combine(Path.GetTempPath(), "GalactiLogDataRootStartup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratch);
        _pointerPath = Path.Combine(_scratch, "roaming", "GalactiLog", "datapath.json");
        _folders = new AppDataRootFolders(Path.Combine(_scratch, "default"), Path.Combine(_scratch, "legacy"));
    }

    public void Dispose()
    {
        Serilog.Log.CloseAndFlush();
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_scratch))
            {
                Directory.Delete(_scratch, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup only; a leftover temp directory does not fail the run.
        }
    }

    // Clears GALACTILOG_APPDATA for the duration of a case, or sets it, and restores whatever the
    // machine had afterwards.
    private sealed class EnvironmentScope : IDisposable
    {
        private readonly string? _previous;

        public EnvironmentScope(string? value)
        {
            _previous = Environment.GetEnvironmentVariable(AppDataRootResolver.EnvironmentVariableName);
            Environment.SetEnvironmentVariable(AppDataRootResolver.EnvironmentVariableName, value);
        }

        public void Dispose()
            => Environment.SetEnvironmentVariable(AppDataRootResolver.EnvironmentVariableName, _previous);
    }

    private IHost Build(string? explicitOverride = null)
    {
        // Serilog's Log.Logger is process-global and Build overwrites it, so a previous host's
        // file sink would otherwise keep its log open under a root this class then deletes.
        Serilog.Log.CloseAndFlush();
        return AppHost.Build(explicitOverride, cliMode: false, httpHandlerOverride: null,
            inspectServices: null, pointerPathOverride: _pointerPath, folderOverride: _folders);
    }

    private void Release()
    {
        Serilog.Log.CloseAndFlush();
        SqliteConnection.ClearAllPools();
    }

    private string Folder(string name)
    {
        var path = Path.Combine(_scratch, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private DataRootPointer Pointer()
        => new(new AppWriter(Path.Combine(_scratch, "writer"), dataRootPointerPath: _pointerPath));

    private DataRootPointerDocument? StoredPointer() => DataRootPointer.Read(_pointerPath);

    private static string Database(string root) => Path.Combine(root, AppDataRelocation.DatabaseFileName);

    [Fact]
    public void Build_Pointer_OverridesTheDefaultRoot()
    {
        using var environment = new EnvironmentScope(null);
        var chosen = Folder("chosen");
        Pointer().RecordRoot(chosen);

        using (var host = Build())
        {
            Assert.Equal(Path.GetFullPath(chosen), host.Services.GetRequiredService<AppWriter>().AppDataRoot);
            Assert.Equal(
                AppDataRootSource.Pointer,
                host.Services.GetRequiredService<AppDataRootResolution>().Source);
        }

        Release();
        Assert.True(File.Exists(Database(chosen)));
        Assert.False(File.Exists(Database(_folders.DefaultRoot)));
    }

    [Fact]
    public void Build_ExplicitOverride_NeitherReadsNorWritesThePointer()
    {
        using var environment = new EnvironmentScope(null);
        var pointed = Folder("pointed");
        Pointer().RecordRoot(pointed);
        var before = File.ReadAllText(_pointerPath);
        var writtenAt = File.GetLastWriteTimeUtc(_pointerPath);
        var chosen = Folder("explicit");

        using (var host = Build(chosen))
        {
            Assert.Equal(Path.GetFullPath(chosen), host.Services.GetRequiredService<AppWriter>().AppDataRoot);
            Assert.Equal(
                AppDataRootSource.ExplicitOverride,
                host.Services.GetRequiredService<AppDataRootResolution>().Source);
            Assert.False(host.Services.GetRequiredService<RelocationOutcome>().Moved);
        }

        Release();
        Assert.Equal(before, File.ReadAllText(_pointerPath));
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(_pointerPath));
        Assert.False(File.Exists(Database(pointed)));
    }

    [Fact]
    public void Build_EnvironmentVariable_WinsOverThePointer()
    {
        var pointed = Folder("pointed");
        Pointer().RecordRoot(pointed);
        var before = File.ReadAllText(_pointerPath);
        var fromEnvironment = Folder("environment");

        using var environment = new EnvironmentScope(fromEnvironment);
        using (var host = Build())
        {
            Assert.Equal(
                Path.GetFullPath(fromEnvironment),
                host.Services.GetRequiredService<AppWriter>().AppDataRoot);
            Assert.Equal(
                AppDataRootSource.EnvironmentVariable,
                host.Services.GetRequiredService<AppDataRootResolution>().Source);
        }

        Release();
        Assert.Equal(before, File.ReadAllText(_pointerPath));
        Assert.False(File.Exists(Database(pointed)));
    }

    [Fact]
    public void Build_NoPointerAndNoLegacyData_RecordsTheDefaultRootInThePointer()
    {
        using var environment = new EnvironmentScope(null);
        Assert.False(File.Exists(_pointerPath));

        using (var host = Build())
        {
            Assert.Equal(
                Path.GetFullPath(_folders.DefaultRoot),
                host.Services.GetRequiredService<AppWriter>().AppDataRoot);
            Assert.Equal(AppDataRootSource.Default, host.Services.GetRequiredService<AppDataRootResolution>().Source);
            Assert.False(host.Services.GetRequiredService<RelocationOutcome>().Moved);
        }

        Release();
        Assert.Equal(Path.GetFullPath(_folders.DefaultRoot), StoredPointer()?.DataRoot);
    }

    [Fact]
    public void Build_PendingMove_CopiesTheDataAndPromotesThePointer()
    {
        using var environment = new EnvironmentScope(null);

        // The start that creates the library.
        using (var first = Build())
        {
            Assert.Equal(
                Path.GetFullPath(_folders.DefaultRoot),
                first.Services.GetRequiredService<AppWriter>().AppDataRoot);
        }

        Release();
        var destination = Folder("moved");
        Pointer().RequestMove(destination);

        // The next start performs the copy and continues on the new root.
        using (var second = Build())
        {
            Assert.Equal(
                Path.GetFullPath(destination),
                second.Services.GetRequiredService<AppWriter>().AppDataRoot);
            var outcome = second.Services.GetRequiredService<RelocationOutcome>();
            Assert.True(outcome.Moved, outcome.Failure);
            Assert.True(outcome.FilesCopied > 0);
        }

        Release();
        Assert.True(File.Exists(Database(destination)));
        // Ruling Q9.4: the source is left exactly as it was.
        Assert.True(File.Exists(Database(_folders.DefaultRoot)));

        var stored = StoredPointer();
        Assert.Equal(Path.GetFullPath(destination), stored?.DataRoot);
        Assert.Equal(Path.GetFullPath(_folders.DefaultRoot), stored?.PreviousRoot);
        Assert.Null(stored?.PendingRoot);
    }

    [Fact]
    public void Build_PendingMove_ThatFails_KeepsTheOldRootActive()
    {
        using var environment = new EnvironmentScope(null);

        using (var first = Build())
        {
            Assert.NotNull(first.Services.GetRequiredService<AppWriter>());
        }

        Release();

        // A destination that already holds a database is another library, and two catalogues are
        // never merged.
        var destination = Folder("occupied");
        File.WriteAllText(Database(destination), "another library");
        Pointer().RequestMove(destination);
        var beforeDataRoot = StoredPointer()?.DataRoot;

        using (var second = Build())
        {
            Assert.Equal(
                Path.GetFullPath(_folders.DefaultRoot),
                second.Services.GetRequiredService<AppWriter>().AppDataRoot);
            var outcome = second.Services.GetRequiredService<RelocationOutcome>();
            Assert.False(outcome.Moved);
            Assert.False(string.IsNullOrWhiteSpace(outcome.Failure));
        }

        Release();
        var stored = StoredPointer();
        Assert.Equal(beforeDataRoot, stored?.DataRoot);
        Assert.Equal(Path.GetFullPath(destination), stored?.PendingRoot);
        Assert.Equal("another library", File.ReadAllText(Database(destination)));
    }

    [Fact]
    public void Build_LegacyRootHoldingADatabase_IsAdoptedIntoTheDefaultRoot()
    {
        using var environment = new EnvironmentScope(null);

        // A library at the pre-Task-9 location. Built through an explicit override, which writes
        // no pointer, so the adoption below runs with no pointer present exactly as a first start
        // after the update does.
        Directory.CreateDirectory(_folders.LegacyRoot);
        using (var legacy = Build(_folders.LegacyRoot))
        {
            Assert.Equal(
                Path.GetFullPath(_folders.LegacyRoot),
                legacy.Services.GetRequiredService<AppWriter>().AppDataRoot);
        }

        Release();
        Assert.True(File.Exists(Database(_folders.LegacyRoot)));
        Assert.False(File.Exists(_pointerPath));

        using (var adopted = Build())
        {
            Assert.Equal(
                Path.GetFullPath(_folders.DefaultRoot),
                adopted.Services.GetRequiredService<AppWriter>().AppDataRoot);
            var outcome = adopted.Services.GetRequiredService<RelocationOutcome>();
            Assert.True(outcome.Moved, outcome.Failure);
            Assert.Equal(Path.GetFullPath(_folders.LegacyRoot), outcome.From);
        }

        Release();
        Assert.True(File.Exists(Database(_folders.DefaultRoot)));
        // Nothing was deleted from the legacy root.
        Assert.True(File.Exists(Database(_folders.LegacyRoot)));
        var stored = StoredPointer();
        Assert.Equal(Path.GetFullPath(_folders.DefaultRoot), stored?.DataRoot);
        Assert.Equal(Path.GetFullPath(_folders.LegacyRoot), stored?.PreviousRoot);
    }

    // Review finding I2: a pointer that exists and cannot be read is left exactly where it is. It
    // is the only record of the root the user chose, and overwriting it with the default root is
    // the second-empty-catalogue failure arriving through the malformed-pointer door.
    [Fact]
    public void Build_ATornPointerFile_IsLeftInPlaceAndTheDefaultRootIsUsed()
    {
        using var environment = new EnvironmentScope(null);
        Directory.CreateDirectory(Path.GetDirectoryName(_pointerPath)!);
        const string torn = "{\n  \"data_root\": \"D:\\\\Astro\\\\Galact";
        File.WriteAllText(_pointerPath, torn);

        using (var host = Build())
        {
            var resolution = host.Services.GetRequiredService<AppDataRootResolution>();
            Assert.Equal(AppDataRootSource.Default, resolution.Source);
            Assert.Equal(
                Path.GetFullPath(_folders.DefaultRoot),
                host.Services.GetRequiredService<AppWriter>().AppDataRoot);
            Assert.False(string.IsNullOrWhiteSpace(resolution.PointerWarning));
        }

        Release();
        Assert.Equal(torn, File.ReadAllText(_pointerPath));
    }

    // Review finding I3: a start that would copy a database another process still holds refuses
    // instead, and refusing leaves the pointer exactly as it was.
    [Fact]
    public void Build_PendingMove_WhileTheDatabaseIsHeldOpen_IsRefusedAndThePointerIsNotPromoted()
    {
        using var environment = new EnvironmentScope(null);

        using (var first = Build())
        {
            Assert.NotNull(first.Services.GetRequiredService<AppWriter>());
        }

        Release();
        var destination = Folder("moved");
        Pointer().RequestMove(destination);
        var beforeDataRoot = StoredPointer()?.DataRoot;

        // The share mode SQLite itself uses while a GalactiLog process has the catalogue open.
        using (var held = new FileStream(
            Database(_folders.DefaultRoot), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var second = Build())
        {
            Assert.Equal(
                Path.GetFullPath(_folders.DefaultRoot),
                second.Services.GetRequiredService<AppWriter>().AppDataRoot);
            var outcome = second.Services.GetRequiredService<RelocationOutcome>();
            Assert.False(outcome.Moved);
            Assert.Equal(AppDataRelocation.AlreadyRunningFailure, outcome.Failure);
        }

        Release();
        var stored = StoredPointer();
        Assert.Equal(beforeDataRoot, stored?.DataRoot);
        Assert.Equal(Path.GetFullPath(destination), stored?.PendingRoot);
        Assert.False(File.Exists(Database(destination)));
    }

    [Fact]
    public void Build_PointerNamingAMissingDirectory_ThrowsAppDataRootUnavailable()
    {
        using var environment = new EnvironmentScope(null);
        var missing = Path.Combine(_scratch, "disconnected", "GalactiLogData");
        Pointer().RecordRoot(missing);

        var thrown = Assert.Throws<AppDataRootUnavailableException>(() => Build());

        Assert.Equal(Path.GetFullPath(missing), thrown.Path);
        Assert.Equal(_pointerPath, thrown.PointerPath);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void Build_PointerNamingAMissingDirectory_CreatesNoDatabaseAnywhere()
    {
        using var environment = new EnvironmentScope(null);
        var missing = Path.Combine(_scratch, "disconnected", "GalactiLogData");
        Pointer().RecordRoot(missing);

        Assert.Throws<AppDataRootUnavailableException>(() => Build());

        Assert.False(File.Exists(Database(missing)));
        Assert.False(File.Exists(Database(_folders.DefaultRoot)));
        Assert.False(Directory.Exists(_folders.DefaultRoot));
    }

    [Fact]
    public void Build_Relocation_WritesTheDataRootMovedActivityEvent()
    {
        using var environment = new EnvironmentScope(null);

        using (var first = Build())
        {
            Assert.NotNull(first.Services.GetRequiredService<AppWriter>());
        }

        Release();
        var destination = Folder("moved");
        Pointer().RequestMove(destination);

        using (var second = Build())
        {
            Assert.True(second.Services.GetRequiredService<RelocationOutcome>().Moved);
        }

        Release();

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(
            DatabasePaths.BuildConnectionString(Database(destination))));
        var moved = context.ActivityEvents.Single(row => row.EventType == "data_root_moved");

        Assert.Equal("system", moved.Category);
        Assert.Equal("info", moved.Severity);
        Assert.NotNull(moved.Details);
        foreach (var key in new[] { "from", "to", "files_copied", "bytes_copied" })
        {
            Assert.Contains($"\"{key}\"", moved.Details!, StringComparison.Ordinal);
        }
    }

    // GalactiLog.Core does not reference GalactiLog.Data, so AppDataRelocation carries its own
    // copy of the database file name. This project references both, which makes it the one place
    // the two literals can be pinned equal.
    [Fact]
    public void DatabaseFileName_MatchesDatabasePaths()
    {
        Assert.Equal(DatabasePaths.DatabaseFileName, AppDataRelocation.DatabaseFileName);
    }
}
