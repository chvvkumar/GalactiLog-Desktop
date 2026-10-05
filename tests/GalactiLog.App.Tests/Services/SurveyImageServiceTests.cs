using System.Net;
using System.Net.Http.Headers;
using GalactiLog.App.Services;
using GalactiLog.Core.Io;
using GalactiLog.Core.Settings;
using GalactiLog.Core.Survey;
using Xunit;

namespace GalactiLog.App.Tests.Services;

// Unit A cases 12 to 18 and the Wave 1 fixer's cases: the survey image service over a stub handler, a temp AppWriter and a real
// JobRegistry with an inline post. No request leaves the process.
public sealed class SurveyImageServiceTests : IDisposable
{
    private static readonly Guid TargetId = Guid.Parse("7d1c2a3b-0000-4000-8000-000000000001");
    private static readonly SurveyView View = new(Surveys.DefaultId, 10.5, 41.25, 1.5);

    private readonly string _appDataRoot = NewTempDirectory();
    private readonly string _cacheRoot = NewTempDirectory();
    private readonly string _secondCacheRoot = NewTempDirectory();
    private readonly AppWriter _writer;
    private readonly JobRegistry _jobs = new(action => action());
    private readonly StubHandler _stub = new();
    private readonly HttpClient _http;
    private GeneralSettings _general = new() { SurveyDownloadsEnabled = true };

    public SurveyImageServiceTests()
    {
        _writer = new AppWriter(_appDataRoot, _cacheRoot, Path.Combine(_appDataRoot, "datapath.json"));
        _http = new HttpClient(_stub);
    }

    public void Dispose()
    {
        _http.Dispose();
        foreach (var root in new[] { _appDataRoot, _cacheRoot, _secondCacheRoot })
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; a leftover temp directory does not fail a test.
            }
        }
    }

    private static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"galactilog-survey-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private SurveyImageService Create() => new(new Hips2FitsClient(_http), _writer, _jobs, () => _general);

    private string FolderUnder(string root) => Path.Combine(root, "survey", TargetId.ToString("D"));

    private string PathOf(SurveyView view, string? root = null) =>
        Path.Combine(FolderUnder(root ?? _cacheRoot), view.CacheKey() + ".jpg");

    private static string[] Listing(string folder) =>
        Directory.Exists(folder) ? [.. Directory.GetFiles(folder).Order(StringComparer.Ordinal)] : [];

    // Case 12. A failure is a request to the host, or a cached image drawn, with the switch off.
    [Fact]
    public async Task SwitchOff_ReadsNothingRequestsNothingAndDeletesNothing()
    {
        _general = new GeneralSettings { SurveyDownloadsEnabled = false };
        Directory.CreateDirectory(FolderUnder(_cacheRoot));
        File.WriteAllBytes(PathOf(View), [1, 2, 3]);
        var service = Create();

        var plain = await service.GetAsync(TargetId, View, refresh: false, CancellationToken.None);
        var refreshed = await service.GetAsync(TargetId, View, refresh: true, CancellationToken.None);

        Assert.Equal(new SurveyImageResult(SurveyImageOutcome.SwitchOff, null), plain);
        Assert.Equal(new SurveyImageResult(SurveyImageOutcome.SwitchOff, null), refreshed);
        Assert.Equal(0, _stub.Requests);
        Assert.True(File.Exists(PathOf(View)));
        Assert.Empty(_jobs.Recent);
    }

    // Case 13. A failure is a refetch on every open, or the file somewhere other than its path.
    [Fact]
    public async Task AMissIsFetchedOnceAndTheSameViewIsThenAHit()
    {
        _stub.Jpeg = [0xFF, 0xD8, 7];
        var service = Create();

        var first = await service.GetAsync(TargetId, View, refresh: false, CancellationToken.None);
        var second = await service.GetAsync(TargetId, View, refresh: false, CancellationToken.None);

        Assert.Equal(SurveyImageOutcome.Image, first.Outcome);
        Assert.Equal(SurveyImageOutcome.Image, second.Outcome);
        Assert.Equal(_stub.Jpeg, second.Jpeg);
        Assert.Equal(1, _stub.Requests);
        Assert.Equal(new[] { PathOf(View) }, Listing(FolderUnder(_cacheRoot)));
        Assert.Single(_jobs.Recent);
    }

    // Case 14. A failure is a network call the job monitor never sees, or one it cannot cancel.
    [Fact]
    public async Task AFetchRegistersOneCancellableJobAndAHitRegistersNone()
    {
        JobViewModel? running = null;
        var cancellableWhileRunning = false;
        _stub.OnRequest = () =>
        {
            running = _jobs.Running.Single();
            cancellableWhileRunning = running.CanCancel;
        };
        var service = Create();

        await service.GetAsync(TargetId, View, refresh: false, CancellationToken.None);
        await service.GetAsync(TargetId, View, refresh: false, CancellationToken.None);

        Assert.NotNull(running);
        Assert.Equal("survey_image_fetch", running.Kind);
        Assert.Equal("Sky view image", running.Title);
        Assert.True(cancellableWhileRunning);
        var finished = Assert.Single(_jobs.Recent);
        Assert.Equal(JobResult.Succeeded, finished.Result);
    }

    // Case 15. A failure is an empty or partial JPEG left behind and later served as a hit.
    [Fact]
    public async Task FailedAndCancelledFetchesLeaveTheFolderAsItWas()
    {
        var folder = FolderUnder(_cacheRoot);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "other.jpg"), [1]);
        var before = Listing(folder);
        var service = Create();

        _stub.Status = HttpStatusCode.NotFound;
        var failed = await service.GetAsync(TargetId, View, refresh: false, CancellationToken.None);
        Assert.Equal(new SurveyImageResult(SurveyImageOutcome.Failed, null), failed);
        Assert.Equal(before, Listing(folder));

        _stub.Status = HttpStatusCode.OK;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var stopped = await service.GetAsync(TargetId, View, refresh: false, cancelled.Token);
        Assert.Equal(new SurveyImageResult(SurveyImageOutcome.Cancelled, null), stopped);
        Assert.Equal(before, Listing(folder));

        Assert.Equal(new JobResult?[] { JobResult.Cancelled, JobResult.Failed }, _jobs.Recent.Select(job => job.Result));
    }

    // Case 16. A failure is an unbounded folder, the wrong file of a tie dropped, or the image
    // just written deleted. The twenty older files are dated after the write on purpose, so the
    // new file is the oldest by time and survives only by the never-the-file-just-written rule.
    [Fact]
    public async Task TheCapKeepsTwentyDropsTheOldestByTimeThenPathAndNeverTheNewFile()
    {
        var folder = FolderUnder(_cacheRoot);
        Directory.CreateDirectory(folder);
        var future = DateTime.UtcNow.AddDays(1);
        for (var i = 0; i < 20; i++)
        {
            var seeded = Path.Combine(folder, $"seed{i:D2}.jpg");
            File.WriteAllBytes(seeded, [1]);
            // seed00 and seed01 share the oldest time, so the path decides between them.
            File.SetLastWriteTimeUtc(seeded, future.AddMinutes(Math.Max(i, 1)));
        }
        var service = Create();

        await service.GetAsync(TargetId, View, refresh: false, CancellationToken.None);

        var left = Listing(folder);
        Assert.Equal(20, left.Length);
        Assert.Contains(PathOf(View), left);
        Assert.DoesNotContain(Path.Combine(folder, "seed00.jpg"), left);
        Assert.Contains(Path.Combine(folder, "seed01.jpg"), left);
    }

    // Case 17. A failure is Refresh serving the stale file.
    [Fact]
    public async Task RefreshOnAHitRefetchesAndOverwrites()
    {
        var service = Create();
        _stub.Jpeg = [0xFF, 0xD8, 1];
        await service.GetAsync(TargetId, View, refresh: false, CancellationToken.None);

        _stub.Jpeg = [0xFF, 0xD8, 2];
        var refreshed = await service.GetAsync(TargetId, View, refresh: true, CancellationToken.None);

        Assert.Equal(2, _stub.Requests);
        Assert.Equal(_stub.Jpeg, refreshed.Jpeg);
        Assert.Equal(new[] { PathOf(View) }, Listing(FolderUnder(_cacheRoot)));
        Assert.Equal(_stub.Jpeg, File.ReadAllBytes(PathOf(View)));
    }

    // Case 18. A failure is a survey write outside the cache root, or one that stays at the old
    // root after the root moves at runtime.
    [Fact]
    public async Task EveryWriteLandsUnderTheCurrentThumbnailCacheRoot()
    {
        var service = Create();
        await service.GetAsync(TargetId, View, refresh: false, CancellationToken.None);

        _writer.ThumbnailCacheRoot = _secondCacheRoot;
        var moved = View.Zoomed(1);
        await service.GetAsync(TargetId, moved, refresh: false, CancellationToken.None);

        Assert.Equal(new[] { PathOf(View) }, Listing(FolderUnder(_cacheRoot)));
        Assert.Equal(new[] { PathOf(moved, _secondCacheRoot) }, Listing(FolderUnder(_secondCacheRoot)));
        Assert.Empty(Directory.GetFiles(_appDataRoot, "*", SearchOption.AllDirectories));
    }

    // A failure is a monitor cancel that reaches no fetch, so the image is drawn and cached anyway.
    [Fact]
    public async Task AMonitorCancelStopsTheFetchAndWritesNothing()
    {
        _stub.OnRequest = () => _jobs.Running.Single().CancelCommand.Execute(null);
        var service = Create();

        var result = await service.GetAsync(TargetId, View, refresh: false, CancellationToken.None);

        Assert.Equal(new SurveyImageResult(SurveyImageOutcome.Cancelled, null), result);
        Assert.Equal(JobResult.Cancelled, Assert.Single(_jobs.Recent).Result);
        Assert.Empty(Listing(FolderUnder(_cacheRoot)));
    }

    // C26. A failure is a failed Refresh that removed the cached image, so the next open has none.
    [Fact]
    public async Task AFailedRefreshLeavesTheOldBytesReadable()
    {
        var service = Create();
        var old = _stub.Jpeg;
        await service.GetAsync(TargetId, View, refresh: false, CancellationToken.None);

        _stub.Status = HttpStatusCode.InternalServerError;
        _stub.Jpeg = [0xFF, 0xD8, 9];
        var refreshed = await service.GetAsync(TargetId, View, refresh: true, CancellationToken.None);
        var reopened = await service.GetAsync(TargetId, View, refresh: false, CancellationToken.None);

        Assert.Equal(SurveyImageOutcome.Failed, refreshed.Outcome);
        Assert.Equal(SurveyImageOutcome.Image, reopened.Outcome);
        Assert.Equal(old, reopened.Jpeg);
        Assert.Equal(old, File.ReadAllBytes(PathOf(View)));
        Assert.Equal(2, _stub.Requests);
    }

    // A failure is a refused cache write escaping the service, losing an image that was fetched.
    [Fact]
    public async Task ACacheWriteRefusedAfterTheRootMovesStillReturnsTheImage()
    {
        _stub.OnRequest = () => _writer.ThumbnailCacheRoot = _secondCacheRoot;
        var service = Create();

        var result = await service.GetAsync(TargetId, View, refresh: false, CancellationToken.None);

        Assert.Equal(SurveyImageOutcome.Image, result.Outcome);
        Assert.Equal(_stub.Jpeg, result.Jpeg);
        Assert.Equal(JobResult.Succeeded, Assert.Single(_jobs.Recent).Result);
        Assert.Empty(Directory.GetFiles(_cacheRoot, "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(_secondCacheRoot, "*", SearchOption.AllDirectories));
    }

    // A failure is the settings load running on the caller's thread, which is the UI thread in the
    // Sky view; the caller here is a dedicated thread, so only a pool thread reads true.
    [Fact]
    public async Task TheSwitchIsReadOffTheCallingThread()
    {
        bool? readOnPool = null;
        var service = new SurveyImageService(new Hips2FitsClient(_http), _writer, _jobs, () =>
        {
            readOnPool = Thread.CurrentThread.IsThreadPoolThread;
            return _general;
        });
        Task<SurveyImageResult>? pending = null;
        var caller = new Thread(() => pending = service.GetAsync(TargetId, View, refresh: false, CancellationToken.None));
        caller.Start();
        caller.Join();

        await pending!;

        Assert.True(readOnPool);
    }

    // Answers every request with Status and Jpeg as image/jpeg, counting what reached it.
    private sealed class StubHandler : HttpMessageHandler
    {
        public int Requests;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xD9];
        public Action? OnRequest;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Requests);
            OnRequest?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            var content = new ByteArrayContent(Jpeg);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            return Task.FromResult(new HttpResponseMessage(Status) { Content = content });
        }
    }
}
