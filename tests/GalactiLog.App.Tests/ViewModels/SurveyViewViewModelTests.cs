using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Preview;
using GalactiLog.Core.Io;
using GalactiLog.Core.Survey;
using Microsoft.Extensions.Logging;
using Xunit;
using TrackingBitmap = GalactiLog.App.Tests.TestSupport.PreviewModalViewModelTestFactory.TrackingBitmap;

namespace GalactiLog.App.Tests.ViewModels;

/// <summary>The Sky view window's collaborators, all recorded: fetches, survey writes, debounce
/// waits the test releases by hand, and posts the test may hold back.</summary>
internal sealed class SurveyViewRig
{
    public static readonly SurveyTarget M31 = new(
        Guid.Parse("6f1d7a52-3c8e-4b0a-9d2e-1a5b7c9e0f13"), "M 31", 10.6847, 41.2690, 178);

    private readonly Lock _gate = new();
    private readonly List<(SurveyView View, bool Refresh, CancellationToken Token, bool OnUiThread)> _fetches = [];
    private int _testThread;
    private readonly List<Action> _posted = [];

    public List<string> Saved { get; } = [];
    public List<TaskCompletionSource> Delays { get; } = [];
    public List<TimeSpan> DelaySpans { get; } = [];
    public List<(byte Tag, TrackingBitmap Bitmap)> Decoded { get; } = [];
    public RecordingLogger Logger { get; } = new();
    public string? Stored { get; set; }
    public bool QueuePosts { get; set; }
    public Exception? SaveFailure { get; set; }

    /// <summary>The nth fetch's answer, 1-based. Default: an image tagged with its call number.
    /// </summary>
    public Func<int, Task<SurveyImageResult>> Respond { get; set; } = n => Task.FromResult(Image((byte)n));

    public IReadOnlyList<(SurveyView View, bool Refresh, CancellationToken Token, bool OnUiThread)> Fetches
    {
        get { lock (_gate) { return [.. _fetches]; } }
    }

    public int PostedCount
    {
        get { lock (_gate) { return _posted.Count; } }
    }

    public static SurveyImageResult Image(byte tag) => new(SurveyImageOutcome.Image, [tag]);

    public TrackingBitmap DecodedFrom(byte tag) => Decoded.Single(entry => entry.Tag == tag).Bitmap;

    public SurveyViewViewModel Build()
    {
        _testThread = Environment.CurrentManagedThreadId;
        return Create();
    }

    private SurveyViewViewModel Create() => new(
        M31,
        (view, refresh, token) =>
        {
            int n;
            lock (_gate)
            {
                _fetches.Add((view, refresh, token, Environment.CurrentManagedThreadId == _testThread));
                n = _fetches.Count;
            }

            return Respond(n);
        },
        () => Stored,
        id =>
        {
            Saved.Add(id);
            if (SaveFailure is not null)
            {
                throw SaveFailure;
            }
        },
        decode: bytes =>
        {
            var bitmap = new TrackingBitmap();
            lock (_gate) { Decoded.Add((bytes[0], bitmap)); }
            return bitmap;
        },
        delay: (span, token) =>
        {
            DelaySpans.Add(span);
            var wait = new TaskCompletionSource();
            token.Register(() => wait.TrySetCanceled(token));
            Delays.Add(wait);
            return wait.Task;
        },
        post: action =>
        {
            lock (_gate)
            {
                if (QueuePosts)
                {
                    _posted.Add(action);
                    return;
                }
            }

            action();
        },
        logger: Logger);

    public void RunPosts()
    {
        List<Action> due;
        lock (_gate)
        {
            due = [.. _posted];
            _posted.Clear();
        }

        foreach (var action in due)
        {
            action();
        }
    }

    public async Task WaitForPosts(int count)
    {
        for (var waited = 0; PostedCount < count; waited += 10)
        {
            Assert.True(waited < 5000, $"expected {count} posted results, saw {PostedCount}");
            await Task.Delay(10);
        }
    }

    public async Task WaitForFetches(int count)
    {
        for (var waited = 0; Fetches.Count < count; waited += 10)
        {
            Assert.True(waited < 5000, $"expected {count} fetches, saw {Fetches.Count}");
            await Task.Delay(10);
        }
    }

    public void ReleaseDelays()
    {
        foreach (var wait in Delays)
        {
            wait.TrySetResult();
        }
    }
}

// Unit C's view-model cases (brief section 4). Bitmaps need the headless platform, so every case
// is an [AvaloniaFact]; every wait is a released gate, never wall time.
public class SurveyViewViewModelTests
{
    private static readonly SurveyView Opening = SurveyView.Initial(SurveyViewRig.M31, Surveys.DefaultId);

    // Case 1. A failure is a window that opens on a remembered pan or the wrong survey.
    [AvaloniaFact]
    public async Task Open_FetchesTheInitialViewInTheStoredSurvey_AndTitlesTheTarget()
    {
        var rig = new SurveyViewRig { Stored = "P/2MASS/color" };
        using var page = rig.Build();
        await page.Settled;

        var fetch = Assert.Single(rig.Fetches);
        Assert.Equal(SurveyView.Initial(SurveyViewRig.M31, "P/2MASS/color"), fetch.View);
        Assert.False(fetch.Refresh);
        Assert.Equal("Sky view: M 31", page.Title);
        Assert.Same(rig.DecodedFrom(1), page.Image);
    }

    // Case 2. A failure is a bad stored key reaching the request, or a write on open.
    [AvaloniaFact]
    public async Task Open_WithAnUnknownStoredSurvey_UsesDss2Color_AndWritesNothing()
    {
        var rig = new SurveyViewRig { Stored = "P/not-a-survey" };
        using var page = rig.Build();
        await page.Settled;

        Assert.Equal(Surveys.DefaultId, Assert.Single(rig.Fetches).View.SurveyId);
        Assert.Equal("DSS2 Color", page.SelectedSurvey.Label);
        Assert.Empty(rig.Saved);
    }

    // Case 3. A failure is a survey change that also resets the view, or writes twice.
    [AvaloniaFact]
    public async Task ChoosingASurvey_SavesItOnce_AndFetchesTheSameCentreAndField()
    {
        var rig = new SurveyViewRig();
        using var page = rig.Build();
        await page.Settled;
        page.CommitPan(0.1, -0.05);
        await page.Settled;
        var before = page.View;

        page.SelectedSurvey = Surveys.All[1];
        await page.Settled;

        Assert.Equal(["P/DSS2/red"], rig.Saved);
        Assert.Equal(before with { SurveyId = "P/DSS2/red" }, rig.Fetches[^1].View);
    }

    // Question 14. A failure is a refused settings write that also swallows the fetch.
    [AvaloniaFact]
    public async Task ChoosingASurvey_WhenTheWriteThrows_LogsAWarning_AndStillFetches()
    {
        var rig = new SurveyViewRig { SaveFailure = new IOException("refused") };
        using var page = rig.Build();
        await page.Settled;

        page.SelectedSurvey = Surveys.All[1];
        await page.Settled;

        Assert.Equal("P/DSS2/red", rig.Fetches[^1].View.SurveyId);
        Assert.Contains(rig.Logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    // Case 4. A failure is one request per notch, or a field that is not the clamp's.
    [AvaloniaFact]
    public async Task ThreeZoomInputs_GiveOneFetch_AtTheFieldDividedByOnePointFiveCubed()
    {
        var rig = new SurveyViewRig();
        using var page = rig.Build();
        await page.Settled;

        page.Zoom(1);
        page.Zoom(1);
        page.Zoom(1);

        Assert.Single(rig.Fetches);
        Assert.Equal(3.375, page.Scale, 9);
        Assert.True(page.IsLoading);
        Assert.Equal(Enumerable.Repeat(TimeSpan.FromMilliseconds(400), 3), rig.DelaySpans);

        rig.ReleaseDelays();
        await page.Settled;

        Assert.Equal(2, rig.Fetches.Count);
        Assert.Equal(Opening.Fov / 3.375, rig.Fetches[1].View.Fov, 9);
        Assert.Equal(1d, page.Scale);
    }

    // Question 10. A failure is a clamped step that still scales the drawn image or waits.
    [AvaloniaFact]
    public async Task AZoomStepTheClampSwallows_NeitherScalesNorWaits()
    {
        var rig = new SurveyViewRig();
        using var page = rig.Build();
        await page.Settled;
        page.Zoom(-3);
        rig.ReleaseDelays();
        await page.Settled;
        Assert.Equal(SurveyView.MaxField, page.View.Fov);
        var delays = rig.Delays.Count;

        page.Zoom(-1);

        Assert.Equal(1d, page.Scale);
        Assert.Equal(delays, rig.Delays.Count);
    }

    // Case 5, Tier 1. A failure is an old image landing over a new one.
    [AvaloniaFact]
    public async Task ANewView_CancelsTheOneInFlight_AndItsLateResultIsNotDrawn()
    {
        var late = new TaskCompletionSource<SurveyImageResult>();
        var rig = new SurveyViewRig { QueuePosts = true };
        rig.Respond = n => n == 1 ? late.Task : Task.FromResult(SurveyViewRig.Image((byte)n));
        using var page = rig.Build();
        await rig.WaitForFetches(1);

        page.CommitPan(0.25, 0);
        await rig.WaitForPosts(1);
        rig.RunPosts();

        Assert.True(rig.Fetches[0].Token.IsCancellationRequested);
        Assert.Same(rig.DecodedFrom(2), page.Image);

        late.SetResult(SurveyViewRig.Image(9));
        await rig.WaitForPosts(1);
        rig.RunPosts();

        Assert.Same(rig.DecodedFrom(2), page.Image);
        Assert.True(rig.DecodedFrom(9).IsDisposed);
    }

    // Case 6, Tier 1. A failure is a frozen window for the length of the fetch.
    [AvaloniaFact]
    public async Task TheFetch_RunsOffTheUiThread_AndTheStateLandsThroughPost()
    {
        var rig = new SurveyViewRig { QueuePosts = true };
        using var page = rig.Build();
        await page.Settled;

        Assert.False(Assert.Single(rig.Fetches).OnUiThread);
        Assert.Null(page.Image);
        Assert.True(page.IsLoading);

        rig.RunPosts();

        Assert.Same(rig.DecodedFrom(1), page.Image);
        Assert.False(page.IsLoading);
    }

    // Case 7. A failure is a blank viewport on every pan, or a bar that never clears.
    [AvaloniaFact]
    public async Task Loading_ShowsTheBar_AndKeepsTheOldImage_UntilTheResultLands()
    {
        var second = new TaskCompletionSource<SurveyImageResult>();
        var rig = new SurveyViewRig();
        rig.Respond = n => n == 2 ? second.Task : Task.FromResult(SurveyViewRig.Image((byte)n));
        using var page = rig.Build();
        await page.Settled;
        Assert.False(page.IsLoading);

        page.DragBy(40, 0);
        page.CommitPan(0.1, 0);

        Assert.True(page.IsLoading);
        Assert.Same(rig.DecodedFrom(1), page.Image);
        Assert.Equal(40d, page.OffsetX);

        second.SetResult(SurveyViewRig.Image(2));
        await page.Settled;

        Assert.False(page.IsLoading);
        Assert.Same(rig.DecodedFrom(2), page.Image);
        Assert.True(rig.DecodedFrom(1).IsDisposed);
        Assert.Equal(0d, page.OffsetX);
    }

    // Case 8. A failure is a cached image drawn with the switch off, or two sentences at once.
    [AvaloniaFact]
    public async Task SwitchOffAndFailed_EachShowTheirOwnSentence_AndNoImage()
    {
        var rig = new SurveyViewRig();
        rig.Respond = n => Task.FromResult(n switch
        {
            1 => SurveyViewRig.Image(1),
            2 => new SurveyImageResult(SurveyImageOutcome.SwitchOff, null),
            _ => new SurveyImageResult(SurveyImageOutcome.Failed, null),
        });
        using var page = rig.Build();
        await page.Settled;
        Assert.False(page.HasSentence);

        page.RefreshCommand.Execute(null);
        await page.Settled;

        Assert.Null(page.Image);
        Assert.True(rig.DecodedFrom(1).IsDisposed);
        Assert.Equal(SurveyMessages.SwitchOff, page.Sentence);
        Assert.True(page.HasSentence);

        page.RefreshCommand.Execute(null);
        await page.Settled;

        Assert.Null(page.Image);
        Assert.Equal(SurveyMessages.LoadFailed, page.Sentence);
    }

    // C25. A failure is a refused cache path escaping the window instead of the error sentence.
    [AvaloniaFact]
    public async Task AFetchThatThrows_ShowsTheLoadFailedSentence()
    {
        var rig = new SurveyViewRig
        {
            Respond = _ => throw new UnauthorizedPathException("survey"),
        };
        using var page = rig.Build();
        await page.Settled;

        Assert.Null(page.Image);
        Assert.Equal(SurveyMessages.LoadFailed, page.Sentence);
        Assert.False(page.IsLoading);
    }

    // Brief section 3. A failure is an error sentence left over a monitor cancel's kept image.
    [AvaloniaFact]
    public async Task AMonitorCancel_KeepsTheImage_WithNoSentence_AndLoadingOff()
    {
        var rig = new SurveyViewRig();
        rig.Respond = n => Task.FromResult(
            n == 1 ? SurveyViewRig.Image(1) : new SurveyImageResult(SurveyImageOutcome.Cancelled, null));
        using var page = rig.Build();
        await page.Settled;

        page.RefreshCommand.Execute(null);
        await page.Settled;

        Assert.Same(rig.DecodedFrom(1), page.Image);
        Assert.False(page.HasSentence);
        Assert.False(page.IsLoading);
    }

    // Case 9. A failure is Refresh replacing the wrong file, or Reset leaving the survey.
    [AvaloniaFact]
    public async Task Refresh_RefetchesTheCurrentView_AndReset_FetchesInitialInTheCurrentSurvey()
    {
        var rig = new SurveyViewRig();
        using var page = rig.Build();
        await page.Settled;
        page.SelectedSurvey = Surveys.All[1];
        await page.Settled;
        page.CommitPan(0.2, 0.1);
        await page.Settled;
        var panned = page.View;

        page.RefreshCommand.Execute(null);
        await page.Settled;

        Assert.Equal(panned, rig.Fetches[^1].View);
        Assert.True(rig.Fetches[^1].Refresh);

        page.ResetCommand.Execute(null);
        await page.Settled;

        Assert.Equal(SurveyView.Initial(SurveyViewRig.M31, "P/DSS2/red"), rig.Fetches[^1].View);
        Assert.False(rig.Fetches[^1].Refresh);
    }

    // Case 10. A failure is a stale credit.
    [AvaloniaFact]
    public async Task TheCaption_NamesTheChosenSurvey()
    {
        var rig = new SurveyViewRig();
        using var page = rig.Build();
        await page.Settled;
        Assert.Equal("Image: CDS hips2fits, DSS2 Color", page.Caption);

        page.SelectedSurvey = Surveys.All[1];

        Assert.Equal("Image: CDS hips2fits, DSS2 Red", page.Caption);
    }

    // Case 11, Tier 1. A failure is a fetch finishing into a closed window, or an answer that
    // beats the cancel setting an image on the disposed page and leaking its bitmap.
    [AvaloniaFact]
    public async Task Dispose_CancelsTheFetchInFlight_AndALateAnswerWritesNothing()
    {
        var late = new TaskCompletionSource<SurveyImageResult>();
        var rig = new SurveyViewRig { QueuePosts = true, Respond = _ => late.Task };
        var page = rig.Build();
        await rig.WaitForFetches(1);

        page.Dispose();

        Assert.True(rig.Fetches[0].Token.IsCancellationRequested);

        late.SetResult(SurveyViewRig.Image(7));
        await rig.WaitForPosts(1);
        rig.RunPosts();

        Assert.Null(page.Image);
        Assert.True(rig.DecodedFrom(7).IsDisposed);
    }

    // Tier 1. A failure is the loading bar drawn over the stale "did not load" sentence.
    [AvaloniaFact]
    public async Task Refresh_AfterAFailure_ClearsTheSentenceWhileLoading()
    {
        var pending = new TaskCompletionSource<SurveyImageResult>();
        var rig = new SurveyViewRig();
        rig.Respond = n => n == 1 ? Task.FromResult(new SurveyImageResult(SurveyImageOutcome.Failed, null)) : pending.Task;
        using var page = rig.Build();
        await page.Settled;
        Assert.Equal(SurveyMessages.LoadFailed, page.Sentence);

        page.RefreshCommand.Execute(null);

        Assert.True(page.IsLoading);
        Assert.Null(page.Sentence);
        Assert.False(page.HasSentence);
        pending.SetResult(SurveyViewRig.Image(2));
        await page.Settled;
    }

    // Tier 1. A failure is a null from the combo binding held in the property, so the caption and
    // Reset throw, or the restore saving or fetching.
    [AvaloniaFact]
    public async Task ANullSurvey_FromTheBinding_RestoresThePreviousSurvey()
    {
        var rig = new SurveyViewRig();
        using var page = rig.Build();
        await page.Settled;
        page.SelectedSurvey = Surveys.All[1];
        await page.Settled;

        page.SelectedSurvey = null!;

        Assert.Same(Surveys.All[1], page.SelectedSurvey);
        Assert.Equal("Image: CDS hips2fits, DSS2 Red", page.Caption);
        Assert.Equal(["P/DSS2/red"], rig.Saved);
        Assert.Equal(2, rig.Fetches.Count);

        page.ResetCommand.Execute(null);
        await page.Settled;

        Assert.Equal(SurveyView.Initial(SurveyViewRig.M31, "P/DSS2/red"), rig.Fetches[^1].View);
    }
}
