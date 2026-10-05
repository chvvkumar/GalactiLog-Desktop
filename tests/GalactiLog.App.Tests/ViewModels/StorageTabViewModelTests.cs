using GalactiLog.App.ViewModels.Settings;
using GalactiLog.Core.Io;
using GalactiLog.Core.Settings;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Design-spec 12.7's Storage tab, design-spec 11.3's cache rules, and FIXER item 20. Delegates
// throughout, so every case runs with lambdas, no database and no filesystem: the free-space read
// is a delegate here and DriveInfo only in production (design-spec 18.3).
public class StorageTabViewModelTests
{
    private sealed class FakeStore
    {
        public GeneralSettings Current { get; private set; } = new();

        public int Saves { get; private set; }

        public string? RefuseWith { get; set; }

        public GeneralSettings Get() => Current;

        public GeneralSettings Mutate(Func<GeneralSettings, GeneralSettings> mutate)
        {
            var next = mutate(Current);
            if (RefuseWith is { } message)
            {
                throw new SettingsValidationException(message);
            }

            Current = next;
            Saves++;
            return next;
        }
    }

    // Records what the tab asked about, so a case can assert that relocating the cache reads one
    // volume and does nothing else at all.
    private sealed class SpyVolume
    {
        public List<string> Asked { get; } = [];

        public (long Total, long Free)? Result { get; set; } = (1_000_000_000_000L, 412_000_000_000L);

        public bool Throws { get; set; }

        public (long Total, long Free)? Read(string path)
        {
            Asked.Add(path);
            return Throws ? throw new IOException("the volume is not ready") : Result;
        }
    }

    private static async Task<(StorageTabViewModel Tab, FakeStore Store, SpyVolume Volume)> CreateAsync(
        GeneralSettings? seed = null)
    {
        var store = new FakeStore();
        if (seed is not null)
        {
            store.Mutate(_ => seed);
        }

        var volume = new SpyVolume();
        var tab = new StorageTabViewModel(
            store.Get,
            store.Mutate,
            volumeSpace: volume.Read,
            defaultCacheRoot: () => @"C:\Users\Test\AppData\Local\GalactiLog\thumbnails",
            post: action => action());

        await Settle(tab).ConfigureAwait(false);
        return (tab, store, volume);
    }

    // The read, the write chain and the free-space probe are all off the calling thread; a test
    // awaits them and never blocks (TRACKING.md section 2 item 8).
    private static async Task Settle(StorageTabViewModel tab)
    {
        if (tab.PendingLoad is { } load)
        {
            await load.ConfigureAwait(false);
        }

        if (tab.PendingFreeSpace is { } freeSpace)
        {
            await freeSpace.ConfigureAwait(false);
        }

        await tab.PendingWrite.ConfigureAwait(false);
    }

    [Fact]
    public async Task ThumbnailCacheDir_RoundTrips()
    {
        var (tab, store, _) = await CreateAsync();

        tab.ThumbnailCacheDir = @"D:\Cache\GalactiLog";
        await Settle(tab);

        Assert.Null(tab.CacheDirError);
        Assert.Equal(@"D:\Cache\GalactiLog", store.Current.ThumbnailCacheDir);
    }

    [Fact]
    public async Task ThumbnailCacheDir_Empty_MeansTheDefaultLocation()
    {
        var (tab, store, _) = await CreateAsync(new GeneralSettings { ThumbnailCacheDir = @"D:\Cache\GalactiLog" });

        tab.ThumbnailCacheDir = "";
        await Settle(tab);

        Assert.Null(tab.CacheDirError);
        Assert.Equal("", store.Current.ThumbnailCacheDir);
    }

    [Fact]
    public async Task ThumbnailCacheDir_RelativePath_IsAnInlineError()
    {
        var (tab, store, _) = await CreateAsync();

        tab.ThumbnailCacheDir = @"cache\thumbs";
        await Settle(tab);

        Assert.Equal("The cache location must be empty or an absolute path.", tab.CacheDirError);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task ThumbnailCacheDir_BareDriveRoot_IsAnInlineError()
    {
        var (tab, store, _) = await CreateAsync();

        tab.ThumbnailCacheDir = @"D:\";
        await Settle(tab);

        Assert.Equal("The cache location must not be a bare drive or UNC share root.", tab.CacheDirError);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task ThumbnailCacheDir_ChangingIt_MovesNoFiles()
    {
        // Design-spec 11.3: "Changing it does not move existing files." Asserted by construction:
        // the only thing the tab does with either path is write the new one and read one volume's
        // free space. There is no copy, no move, and no enumeration of the old root.
        var (tab, store, volume) = await CreateAsync(new GeneralSettings { ThumbnailCacheDir = @"D:\Old" });
        volume.Asked.Clear();

        tab.ThumbnailCacheDir = @"E:\New";
        await Settle(tab);

        Assert.Equal(@"E:\New", store.Current.ThumbnailCacheDir);
        Assert.Equal([@"E:\New"], volume.Asked);
        Assert.Contains("does not move existing files", tab.RelocationNote);
    }

    [Fact]
    public async Task FreeSpace_IsReadOffTheUiThread()
    {
        // The read is DriveInfo in production and a disconnected network path can block inside it
        // for seconds, so it never runs on the calling thread. Awaited here, never blocked on.
        var store = new FakeStore();
        var gate = new TaskCompletionSource();
        var probeThread = 0;
        var callerThread = Environment.CurrentManagedThreadId;

        var tab = new StorageTabViewModel(
            store.Get,
            store.Mutate,
            volumeSpace: _ =>
            {
                probeThread = Environment.CurrentManagedThreadId;
                gate.TrySetResult();
                return (100L, 50L);
            },
            defaultCacheRoot: () => @"C:\Cache",
            post: action => action());

        await tab.PendingLoad!;
        await gate.Task;
        await tab.PendingFreeSpace!;

        Assert.NotEqual(callerThread, probeThread);
        Assert.Equal("50 B free of 100 B", tab.FreeSpaceText);
    }

    [Fact]
    public async Task FreeSpace_UnreadableDrive_ShowsTheUnavailableMessage_AndDoesNotThrow()
    {
        var store = new FakeStore();
        var tab = new StorageTabViewModel(
            store.Get,
            store.Mutate,
            volumeSpace: _ => null,
            defaultCacheRoot: () => @"C:\Cache",
            post: action => action());

        await Settle(tab);

        Assert.Equal("Free space unavailable", tab.FreeSpaceText);
    }

    [Fact]
    public async Task FreeSpace_AProbeThatThrows_DegradesRatherThanCrashing()
    {
        var (tab, _, volume) = await CreateAsync();
        volume.Throws = true;

        tab.ThumbnailCacheDir = @"Z:\Disconnected\Cache";
        await Settle(tab);

        Assert.Equal("Free space unavailable", tab.FreeSpaceText);
    }

    [Fact]
    public void ReadVolumeSpace_IsTheOneFreeSpaceRead_AndNeverThrows()
    {
        // The collision map's designated-owner row: Task 9's wizard calls these two rather than
        // growing a second free-space read, one of which would block the UI thread.
        Assert.Null(StorageTabViewModel.ReadVolumeSpace(""));
        Assert.Null(StorageTabViewModel.ReadVolumeSpace("relative/path"));
        Assert.Equal("Free space unavailable", StorageTabViewModel.FormatVolumeSpace(null));
        Assert.Equal(
            "1.5 GB free of 2.0 GB",
            StorageTabViewModel.FormatVolumeSpace((2_000_000_000L, 1_500_000_000L)));
    }

    [Theory]
    [InlineData(1600)]
    [InlineData(2400)]
    [InlineData(4000)]
    [InlineData(0)]
    public async Task PreviewResolution_RoundTrips_OverTheFourOptions(int pixels)
    {
        var (tab, store, _) = await CreateAsync();

        tab.SelectedPreviewResolution =
            StorageTabViewModel.PreviewResolutions.Single(option => option.Pixels == pixels);
        await Settle(tab);

        Assert.Equal(pixels, store.Current.PreviewResolution);
    }

    [Fact]
    public void PreviewResolution_ZeroMeansNative()
        => Assert.Equal(
            "Native",
            StorageTabViewModel.PreviewResolutions.Single(option => option.Pixels == 0).Label);

    [Fact]
    public async Task PreviewCacheMb_RoundTrips()
    {
        var (tab, store, _) = await CreateAsync();

        tab.PreviewCacheMbText = "4096";
        await Settle(tab);

        Assert.Equal(4096, store.Current.PreviewCacheMb);
        Assert.Null(tab.CacheMbNote);
    }

    [Fact]
    public async Task PreviewCacheMb_BelowOneHundred_IsClampedToOneHundred_WithAVisibleNote()
    {
        // FIXER item 20 and design-spec 5.8.1: "Values below 100 are treated as 100". The tab says
        // so rather than accepting a value the eviction sweep will not honour.
        var (tab, store, _) = await CreateAsync();

        tab.PreviewCacheMbText = "10";
        await Settle(tab);

        Assert.Equal(100, store.Current.PreviewCacheMb);
        Assert.Equal("100", tab.PreviewCacheMbText);
        Assert.Equal("Raised to the 100 MB minimum.", tab.CacheMbNote);
        Assert.True(tab.HasCacheMbNote);
    }

    [Fact]
    public async Task PreviewCacheMb_AboveTheMaximum_IsClamped()
    {
        var (tab, store, _) = await CreateAsync();

        tab.PreviewCacheMbText = "99999";
        await Settle(tab);

        Assert.Equal(51200, store.Current.PreviewCacheMb);
        Assert.Equal("51200", tab.PreviewCacheMbText);
        Assert.Equal("Lowered to the 51200 MB maximum.", tab.CacheMbNote);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("lots")]
    public async Task PreviewCacheMb_BlankOrNonNumeric_RevertsTheField(string typed)
    {
        var (tab, store, _) = await CreateAsync(new GeneralSettings { PreviewCacheMb = 2048 });

        tab.PreviewCacheMbText = typed;
        await Settle(tab);

        Assert.Equal("2048", tab.PreviewCacheMbText);
        Assert.Equal(2048, store.Current.PreviewCacheMb);
    }

    [Fact]
    public async Task ThumbnailWidth_RoundTrips()
    {
        var (tab, store, _) = await CreateAsync();

        tab.ThumbnailWidthText = "1200";
        await Settle(tab);

        Assert.Null(tab.ThumbnailWidthError);
        Assert.Equal(1200, store.Current.ThumbnailWidth);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public async Task ThumbnailWidth_ZeroOrNegative_IsAnInlineError(string typed)
    {
        var (tab, store, _) = await CreateAsync();

        tab.ThumbnailWidthText = typed;
        await Settle(tab);

        Assert.Equal("The thumbnail width must be greater than zero.", tab.ThumbnailWidthError);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task TheTabStatesThatRelocationMovesNothing_AndThatWidthChangesNeedNoPurge()
    {
        // The three design-spec 11.3 facts are on-screen text bound from these three properties,
        // so deleting one fails here rather than quietly leaving a confused user.
        var (tab, _, _) = await CreateAsync();

        Assert.Contains("does not move existing files", tab.RelocationNote);
        Assert.Contains("changes the cache key", tab.NoPurgeNote);
        Assert.Contains("nothing needs purging", tab.NoPurgeNote);
        Assert.Contains("below 100 MB are treated as 100 MB", tab.CacheFloorNote);
        Assert.Contains("previews only", tab.CacheFloorNote);
    }

    [Fact]
    public async Task AFailedSave_RollsTheControlBack()
    {
        var (tab, store, _) = await CreateAsync(new GeneralSettings { ThumbnailWidth = 800 });
        store.RefuseWith = "general.thumbnail_width is refused by this test";

        tab.ThumbnailWidthText = "1200";
        await Settle(tab);

        Assert.Equal("800", tab.ThumbnailWidthText);
        Assert.Equal("general.thumbnail_width is refused by this test", tab.ErrorMessage);
    }

    [Fact]
    public void ClampCacheMb_IsTheWebsOwnRule()
    {
        // Math.min(51200, Math.max(100, parsed)), verbatim.
        Assert.Equal(100, StorageTabViewModel.ClampCacheMb("1"));
        Assert.Equal(51200, StorageTabViewModel.ClampCacheMb("999999"));
        Assert.Equal(2048, StorageTabViewModel.ClampCacheMb("2048"));
        Assert.Null(StorageTabViewModel.ClampCacheMb(""));
        Assert.Null(StorageTabViewModel.ClampCacheMb("abc"));
        Assert.Equal(100, StorageTabViewModel.MinimumCacheMb);
        Assert.Equal(51200, StorageTabViewModel.MaximumCacheMb);
    }

    // ---- the data location (spec 12.7, 17.2, Phase 10 Task 9) ---------------------------------
    //
    // Delegates and constants throughout, like every case above: the move recorder is a lambda, no
    // pointer file is read or written, no directory is created, and no path used has to exist.

    private const string CurrentRoot = @"C:\AppData\GalactiLogData";

    private sealed class SpyMove
    {
        public List<string> Requests { get; } = [];

        public int Cancels { get; private set; }

        public string? Refusal { get; set; }

        public string? Request(string destination)
        {
            Requests.Add(destination);
            return Refusal;
        }

        public void Cancel() => Cancels++;
    }

    private static async Task<(StorageTabViewModel Tab, FakeStore Store, SpyMove Move)> CreateWithDataRootAsync(
        AppDataRootSource source = AppDataRootSource.Default,
        string? pendingRoot = null,
        string? previousRoot = null,
        RelocationOutcome? relocation = null,
        string? pointerWarning = null)
    {
        var store = new FakeStore();
        var move = new SpyMove();
        var tab = new StorageTabViewModel(
            store.Get,
            store.Mutate,
            volumeSpace: _ => (1_000L, 500L),
            defaultCacheRoot: () => CurrentRoot + @"\thumbnails",
            post: action => action(),
            dataRoot: () => new AppDataRootResolution(
                CurrentRoot, source, pendingRoot, previousRoot, pointerWarning),
            lastRelocation: () => relocation ?? RelocationOutcome.None,
            requestDataRootMove: move.Request,
            cancelDataRootMove: move.Cancel);

        await Settle(tab).ConfigureAwait(false);
        return (tab, store, move);
    }

    [Theory]
    [InlineData(AppDataRootSource.Default, "Default location")]
    [InlineData(AppDataRootSource.Pointer, "Chosen location")]
    [InlineData(AppDataRootSource.EnvironmentVariable, "Set by GALACTILOG_APPDATA")]
    [InlineData(AppDataRootSource.ExplicitOverride, "Set for this test run")]
    public async Task DataRoot_ShowsTheResolvedRootAndItsSource(AppDataRootSource source, string expected)
    {
        var (tab, _, _) = await CreateWithDataRootAsync(source);

        Assert.True(tab.HasDataLocation);
        Assert.Equal(CurrentRoot, tab.DataRootPath);
        Assert.Equal(expected, tab.DataRootSourceText);
    }

    [Fact]
    public async Task SetDataRoot_RecordsAPendingMoveAndTouchesNothingElse()
    {
        var (tab, store, move) = await CreateWithDataRootAsync();
        var destination = Path.Combine(Path.GetTempPath(), "GalactiLogStorageTabTests_" + Guid.NewGuid().ToString("N"));

        tab.SetDataRoot(destination);

        Assert.Equal([destination], move.Requests);
        Assert.Equal(destination, tab.PendingDataRoot);
        Assert.True(tab.HasPendingMove);
        Assert.Null(tab.DataRootError);
        Assert.Equal(0, store.Saves);
        Assert.False(Directory.Exists(destination));
    }

    [Theory]
    [InlineData("Astro")]
    [InlineData("")]
    public async Task SetDataRoot_ARelativePathOrDriveRoot_IsRefusedAtTheField(string value)
    {
        var (tab, _, move) = await CreateWithDataRootAsync();

        tab.SetDataRoot(value);

        Assert.Equal(StorageTabViewModel.DataRootMustBeAbsolute, tab.DataRootError);
        Assert.True(tab.HasDataRootError);
        Assert.Empty(move.Requests);

        tab.SetDataRoot(@"C:\");

        Assert.Equal(StorageTabViewModel.DataRootMustNotBeDriveRoot, tab.DataRootError);
        Assert.Empty(move.Requests);
    }

    [Fact]
    public async Task SetDataRoot_TheCurrentRoot_IsRefused()
    {
        var (tab, _, move) = await CreateWithDataRootAsync();

        tab.SetDataRoot(CurrentRoot);

        Assert.Equal(StorageTabViewModel.DataRootAlreadyCurrent, tab.DataRootError);
        Assert.Empty(move.Requests);
    }

    [Fact]
    public async Task SetDataRoot_AFolderInsideTheCurrentRoot_IsRefused()
    {
        var (tab, _, move) = await CreateWithDataRootAsync();

        tab.SetDataRoot(Path.Combine(CurrentRoot, "inside"));
        Assert.Equal(StorageTabViewModel.DataRootNested, tab.DataRootError);

        // The other direction: a folder the current root sits inside.
        tab.SetDataRoot(@"C:\AppData");
        Assert.Equal(StorageTabViewModel.DataRootNested, tab.DataRootError);

        Assert.Empty(move.Requests);
    }

    [Fact]
    public async Task SetDataRoot_ARefusalFromTheHost_IsShownAtTheField()
    {
        var (tab, _, move) = await CreateWithDataRootAsync();
        move.Refusal = StorageTabViewModel.DataRootHasDatabase;

        tab.SetDataRoot(@"D:\Astro\GalactiLogData");

        Assert.Equal(StorageTabViewModel.DataRootHasDatabase, tab.DataRootError);
        Assert.Null(tab.PendingDataRoot);
    }

    [Fact]
    public async Task CancelDataRootMove_ClearsThePendingRootAndCallsTheDelegate()
    {
        var (tab, _, move) = await CreateWithDataRootAsync(pendingRoot: @"D:\Astro\GalactiLogData");
        Assert.True(tab.HasPendingMove);
        Assert.True(tab.CancelDataRootMoveCommand.CanExecute(null));

        tab.CancelDataRootMoveCommand.Execute(null);

        Assert.Equal(1, move.Cancels);
        Assert.Null(tab.PendingDataRoot);
        Assert.False(tab.HasPendingMove);
        Assert.False(tab.CancelDataRootMoveCommand.CanExecute(null));
    }

    [Fact]
    public async Task PendingMoveNote_NamesTheDestinationAndTheCurrentRoot()
    {
        var (tab, _, _) = await CreateWithDataRootAsync(pendingRoot: @"D:\Astro\GalactiLogData");

        Assert.Contains(@"D:\Astro\GalactiLogData", tab.PendingMoveNote, StringComparison.Ordinal);
        Assert.Contains(CurrentRoot, tab.PendingMoveNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviousDataRootNote_NamesTheOldPathAndSaysNothingWasDeleted()
    {
        var (tab, _, _) = await CreateWithDataRootAsync(previousRoot: @"C:\AppData\GalactiLog");

        Assert.NotNull(tab.PreviousDataRootNote);
        Assert.Contains(@"C:\AppData\GalactiLog", tab.PreviousDataRootNote!, StringComparison.Ordinal);
        Assert.Contains("Nothing was deleted", tab.PreviousDataRootNote!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedRelocation_IsShownAsTheDataRootError()
    {
        var failed = new RelocationOutcome(
            false, @"C:\AppData\GalactiLogData", @"D:\Astro\GalactiLogData", 0, 0, [], "the drive is not ready");

        var (tab, _, _) = await CreateWithDataRootAsync(relocation: failed);

        Assert.Equal("the drive is not ready", tab.DataRootError);
        Assert.True(tab.HasDataRootError);
        // A failed move left no copy anywhere, so there is no previous location to name.
        Assert.Null(tab.PreviousDataRootNote);
    }

    [Fact]
    public async Task Constructor_WithNoDataRootDelegates_ConstructsAndShowsNoDataLocationSection()
    {
        var (tab, _, _) = await CreateAsync();

        Assert.False(tab.HasDataLocation);
        Assert.Equal("", tab.DataRootPath);
        Assert.Null(tab.PendingDataRoot);
        Assert.Null(tab.DataRootError);
    }

    // Review finding I2: the pointer was left on disk, so the tab names the fault the user would
    // repair rather than leaving it only in the log.
    [Fact]
    public async Task AnUnreadablePointer_IsShownAsTheDataRootError()
    {
        var (tab, _, _) = await CreateWithDataRootAsync(
            pointerWarning: "The data location pointer is empty or not valid JSON.");

        Assert.Equal("The data location pointer is empty or not valid JSON.", tab.DataRootError);
        Assert.True(tab.HasDataRootError);
    }

    // Review finding M7: the note on screen and the document on disk spell one folder one way.
    [Fact]
    public async Task SetDataRoot_StoresTheNormalizedSpelling()
    {
        var (tab, _, _) = await CreateWithDataRootAsync();

        tab.SetDataRoot(@"D:\Astro\GalactiLogData\");

        Assert.Equal(@"D:\Astro\GalactiLogData", tab.PendingDataRoot);
        Assert.Contains(@"D:\Astro\GalactiLogData the", tab.PendingMoveNote, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateDataRoot_AcceptsAnUnrelatedAbsoluteFolder()
    {
        Assert.Null(StorageTabViewModel.ValidateDataRoot(@"D:\Astro\GalactiLogData", CurrentRoot));
    }
}
