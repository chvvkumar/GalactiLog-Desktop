using System.Globalization;
using System.Net;
using System.Net.Http;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Integrations;
using GalactiLog.Core.Settings;
using GalactiLog.Core.Text;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Microsoft.Extensions.Logging;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;
using CardFactory = GalactiLog.App.Tests.TestSupport.SessionCardViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

// Phase 21 Task 5 (task5-target-page.md), spec 12.16. The page is built directly against the
// public constructor rather than through TargetDetailViewModelTestFactory.Create, which this
// unit's brief does not list among the files it may touch. A real NinaClient/StellariumClient
// over a fake HttpMessageHandler (no socket, no listener) stand in for the network, a real
// JobRegistry and a recorded activity delegate stand in for the registry and the database, so
// every case proves with no window and no database (design-spec 18.3).
public class TargetDetailIntegrationsTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<(string Method, string Url, string? Body)> Requests { get; } = [];

        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("true") };

        /// <summary>Parks every request on a thread-pool wait rather than on the calling thread,
        /// so a test can prove the command returns to its caller while a request is still in
        /// flight (case 10) with no risk of deadlocking the calling thread on itself.</summary>
        public ManualResetEventSlim? Park { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests)
            {
                Requests.Add((request.Method.Method, request.RequestUri!.ToString(), body));
            }

            if (Park is { } park)
            {
                await Task.Run(() => park.Wait(Budget), cancellationToken);
            }

            return Respond(request);
        }
    }

    private sealed class Harness : IDisposable
    {
        public RecordingLogger Logger { get; } = new();

        public List<string> Clipboard { get; } = [];

        public List<(string Severity, string EventType, string Message, object? Details, Guid? TargetId)>
            Activity { get; } = [];

        public List<(string GroupKey, DateOnly SessionDate)> SessionDetailCalls { get; } = [];

        /// <summary>What the page-level session detail delegate answers for a checked night the
        /// page has not opened. Keyed by date; a date with no entry answers null, which counts
        /// that night as failed.</summary>
        public Dictionary<DateOnly, SessionDetail?> UnopenedDetails { get; } = [];

        /// <summary>What a card loads on its own auto-expand (the newest session), keyed by date.
        /// </summary>
        public Dictionary<DateOnly, SessionDetail> OpenedDetails { get; } = [];

        public JobRegistry Jobs { get; } = new(post: action => action());

        public GeneralSettings General { get; set; } = new();

        /// <summary>The handlers the page has on <c>SettingsStore.GeneralChanged</c>, standing in
        /// for the store's event list, so a page that never unfollows is visible here.</summary>
        public List<EventHandler<GeneralSettings>> GeneralFollowers { get; } = [];

        /// <summary>The actions the page posted, when the harness was built to queue them rather
        /// than run them inline: what a UI thread would run, in order.</summary>
        public List<Action> Posted { get; } = [];

        public TargetDetailViewModel ViewModel { get; internal set; } = null!;

        /// <summary>Held by every posted closure, as the UI thread runs one at a time, so a card's
        /// publish from its load's pool thread cannot overlap the page's.</summary>
        public object UiThread { get; } = new();

        public void RaiseGeneralChanged()
        {
            foreach (var handler in GeneralFollowers.ToArray())
            {
                handler(this, General);
            }
        }

        public Harness Drain()
        {
            while (Posted.Count > 0)
            {
                var next = Posted[0];
                Posted.RemoveAt(0);
                lock (UiThread)
                {
                    next();
                }
            }

            return this;
        }

        public Harness Settle()
        {
            ViewModel.PendingLoad?.Wait(Budget);
            return this;
        }

        public Harness SettleCards()
        {
            foreach (var card in ViewModel.Sessions)
            {
                card.PendingLoad?.Wait(Budget);
            }

            return this;
        }

        public void Dispose() => ViewModel.Dispose();
    }

    /// <param name="configure">Runs before the page is constructed and its background load
    /// starts, so a test can seed <see cref="Harness.OpenedDetails"/> and
    /// <see cref="Harness.UnopenedDetails"/> with no race against that load.</param>
    private static Harness Build(
        TargetHeaderBlock? header = null,
        IReadOnlyList<SessionOverview>? sessions = null,
        NinaClient? ninaClient = null,
        StellariumClient? stellariumClient = null,
        GeneralSettings? general = null,
        Action<Harness>? configure = null,
        bool queuePosts = false)
    {
        var harness = new Harness();
        if (general is not null)
        {
            harness.General = general;
        }

        configure?.Invoke(harness);

        var resolvedHeader = header ?? Factory.PopulatedHeader();
        var resolvedSessions = sessions
            ?? [Factory.Session(Factory.LastSession), Factory.Session(Factory.FirstSession)];

        var selection = new ChartSelectionViewModel(
            new GraphSettings(),
            new GraphSettingsWriter(() => new GraphSettings(), _ => { }),
            () => new AliasMap(new Dictionary<string, FilterSetting>(), new EquipmentSettings()));

        var shell = new ShellIntegration(
            text =>
            {
                harness.Clipboard.Add(text);
                return Task.CompletedTask;
            },
            _ => null,
            harness.Logger);

        harness.ViewModel = new TargetDetailViewModel(
            Factory.ResolvedGroupKey,
            _ => Factory.PopulatedDetail(header: resolvedHeader, sessions: resolvedSessions),
            (headerBlock, overview, _) => new SessionCardViewModel(
                overview,
                headerBlock.GroupKey,
                (_, date) => harness.OpenedDetails.TryGetValue(date, out var detail)
                    ? detail
                    : CardFactory.PopulatedDetail(date),
                null,
                _ => null,
                _ => null,
                new DisplaySettings(),
                new GeneralSettings { Timezone = "UTC", Use24HTime = true },
                post: action =>
                {
                    lock (harness.UiThread)
                    {
                        action();
                    }
                },
                logger: harness.Logger),
            (_, _) => RenameOutcome.Renamed,
            (_, _) => { },
            (_, _, _) => Task.FromResult((false, "")),
            shell,
            selection,
            post: action =>
            {
                if (queuePosts)
                {
                    harness.Posted.Add(action);
                }
                else
                {
                    lock (harness.UiThread)
                    {
                        action();
                    }
                }
            },
            logger: harness.Logger,
            ninaClient: ninaClient,
            stellariumClient: stellariumClient,
            jobs: harness.Jobs,
            emitActivity: (severity, eventType, message, details, targetId) =>
                harness.Activity.Add((severity, eventType, message, details, targetId)),
            getGeneral: () => harness.General,
            getAliasMap: () => new AliasMap(new Dictionary<string, FilterSetting>(), new EquipmentSettings()),
            getSessionDetail: (key, date) =>
            {
                harness.SessionDetailCalls.Add((key, date));
                return harness.UnopenedDetails.GetValueOrDefault(date);
            },
            subscribeGeneralChanged: handler => harness.GeneralFollowers.Add(handler),
            unsubscribeGeneralChanged: handler => harness.GeneralFollowers.Remove(handler));

        return harness;
    }

    private static GeneralSettings WithNina(params IntegrationInstance[] instances)
        => new() { NinaInstancesDocument = IntegrationSettings.WriteInstances(instances) };

    private static GeneralSettings WithStellarium(params IntegrationInstance[] instances)
        => new() { StellariumInstancesDocument = IntegrationSettings.WriteInstances(instances) };

    private static double QueryValue(string url, string key)
    {
        var marker = key + "=";
        var start = url.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = url.IndexOf('&', start);
        var text = end < 0 ? url[start..] : url[start..end];
        return double.Parse(text, CultureInfo.InvariantCulture);
    }

    // ---- Case 1: offered instances, in the stored order, filtered by IsOffered ----------------

    [Fact]
    public void NinaSendItems_EqualsTheOfferedInstances_InStoredOrder()
    {
        var general = WithNina(
            new IntegrationInstance("Obsy1", "http://a.local", true),
            new IntegrationInstance("Obsy2", "http://b.local", true),
            new IntegrationInstance("Disabled", "http://c.local", false),
            new IntegrationInstance("", "http://d.local", true),
            new IntegrationInstance("BadUrl", "not-a-url", true));

        using var harness = Build(general: general).Settle();

        Assert.Equal(["Obsy1", "Obsy2"], harness.ViewModel.NinaSendItems.Select(i => i.Name));
    }

    // ---- Case 2: nothing configured leaves the menu exactly as before -------------------------

    [Fact]
    public void NothingConfigured_HidesBothSubmenusAndTheSendButton()
    {
        using var harness = Build(general: new GeneralSettings()).Settle();

        Assert.False(harness.ViewModel.HasNinaSendItems);
        Assert.False(harness.ViewModel.HasStellariumSendItems);
        Assert.False(harness.ViewModel.HasSendItems);
    }

    // ---- Case 3: no RA or no Dec disables every item with the fixed tooltip -------------------

    [Fact]
    public void NoCoordinates_DisablesEveryItem_WithTheFixedTooltip()
    {
        var general = WithNina(new IntegrationInstance("Obsy1", "http://a.local", true));
        using var harness = Build(
            header: Factory.PopulatedHeader() with { Ra = null },
            general: general).Settle();

        var item = Assert.Single(harness.ViewModel.NinaSendItems);
        Assert.False(item.IsEnabled);
        Assert.Equal("This target has no coordinates to send.", item.ToolTipText);
    }

    // ---- Case 4: the call carries the header's own RA, Dec and position angle -----------------

    [Fact]
    public async Task SendNina_CarriesTheHeadersOwnCoordinatesAndPositionAngle()
    {
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var nina = new NinaClient(http);
        var header = Factory.PopulatedHeader();
        using var harness = Build(header: header, ninaClient: nina, general: WithNina(
            new IntegrationInstance("Obsy1", "http://a.local", true))).Settle();

        await harness.ViewModel.NinaSendItems.Single().SendCommand.ExecuteAsync(null);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(header.Ra!.Value, QueryValue(handler.Requests[0].Url, "RAangle"));
        Assert.Equal(header.Dec!.Value, QueryValue(handler.Requests[0].Url, "DecAngle"));
        Assert.Equal(header.PositionAngle!.Value, QueryValue(handler.Requests[1].Url, "rotation"));
    }

    // ---- Case 4b: the reviewed night's own pointing and rotator angle win over the catalogue ----

    [Fact]
    public async Task SendNina_CarriesTheReviewedNightsReferenceFramePointing_AndItsRotatorAngle()
    {
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var nina = new NinaClient(http);
        var reference = Guid.NewGuid();
        var frames = new[]
        {
            FrameAt(Factory.LastSession, 0, ra: 10.5d, dec: 41.0d, rotator: 12.5d),
            FrameAt(Factory.LastSession, 1, ra: 10.75d, dec: 41.25d, rotator: 97.5d) with { ImageId = reference },
        };
        using var harness = Build(
            sessions: [Factory.Session(Factory.LastSession)],
            ninaClient: nina,
            general: WithNina(new IntegrationInstance("Obsy1", "http://a.local", true)),
            configure: h => h.OpenedDetails[Factory.LastSession] =
                CardFactory.PopulatedDetail(Factory.LastSession) with { Frames = frames, ReferenceImageId = reference })
            .Settle()
            .SettleCards();
        Assert.NotNull(harness.ViewModel.ReviewSession?.Detail);

        await harness.ViewModel.NinaSendItems.Single().SendCommand.ExecuteAsync(null);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(10.75d, QueryValue(handler.Requests[0].Url, "RAangle"));
        Assert.Equal(41.25d, QueryValue(handler.Requests[0].Url, "DecAngle"));
        Assert.Equal(97.5d, QueryValue(handler.Requests[1].Url, "rotation"));
    }

    [Fact]
    public async Task SlewStellarium_WithTheNightsOwnPointing_GoesToTheCoordinates_NotTheName()
    {
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var stellarium = new StellariumClient(http);
        var frames = new[] { FrameAt(Factory.LastSession, 0, ra: 10.5d, dec: 41.0d, rotator: null) };
        using var harness = Build(
            sessions: [Factory.Session(Factory.LastSession)],
            stellariumClient: stellarium,
            general: WithStellarium(new IntegrationInstance("Desk", "http://s.local", true)),
            configure: h => h.OpenedDetails[Factory.LastSession] =
                CardFactory.PopulatedDetail(Factory.LastSession) with { Frames = frames })
            .Settle()
            .SettleCards();

        await harness.ViewModel.StellariumSendItems.Single().SendCommand.ExecuteAsync(null);

        var slew = Assert.Single(handler.Requests, r => r.Url.EndsWith("/api/scripts/direct", StringComparison.Ordinal));
        Assert.Contains("10.500000d", Uri.UnescapeDataString(slew.Body!), StringComparison.Ordinal);
        Assert.Contains("41.000000d", Uri.UnescapeDataString(slew.Body!), StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, r => r.Url.EndsWith("/api/main/focus", StringComparison.Ordinal));
    }

    private static FrameRow FrameAt(DateOnly date, int index, double? ra, double? dec, double? rotator) => new(
        ImageId: Guid.NewGuid(),
        FilePath: $@"C:\Astro\M 31\frame_{index:0000}.fits",
        FileName: $"frame_{index:0000}.fits",
        CaptureDate: date.ToDateTime(new TimeOnly(21, 0)).AddMinutes(index * 5),
        FilterUsed: "Ha",
        ExposureTime: 300d,
        MedianHfr: 2.3d,
        Eccentricity: 0.4d,
        Fwhm: 1.9d,
        DetectedStars: 1490,
        GuidingRmsArcsec: 0.45d,
        GuidingRmsRaArcsec: null,
        GuidingRmsDecArcsec: null,
        GuidingRmsSource: null,
        AduMean: null,
        AduMedian: null,
        AduStdev: null,
        AduMin: null,
        AduMax: null,
        FocuserPosition: null,
        FocuserTemp: null,
        AmbientTemp: null,
        DewPoint: null,
        Humidity: null,
        Pressure: null,
        WindSpeed: null,
        WindDirection: null,
        WindGust: null,
        CloudCover: null,
        SkyQuality: null,
        Airmass: null,
        PierSide: null,
        RotatorPosition: rotator,
        SensorTemp: null,
        CameraGain: 100,
        Rig: "RC8 / ASI2600MM",
        IsHfrOutlier: false,
        IsEccentricityOutlier: false,
        RaDeg: ra,
        DecDeg: dec);

    // ---- Case 5: a null position angle sends no rotation and the summary says nothing ---------

    [Fact]
    public async Task SendNina_NullPositionAngle_SendsNoRotation_AndTheSummaryIsSilent()
    {
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var nina = new NinaClient(http);
        using var harness = Build(
            header: Factory.PopulatedHeader() with { PositionAngle = null },
            ninaClient: nina,
            general: WithNina(new IntegrationInstance("Obsy1", "http://a.local", true))).Settle();

        await harness.ViewModel.NinaSendItems.Single().SendCommand.ExecuteAsync(null);

        Assert.Single(handler.Requests);
        var job = Assert.Single(harness.Jobs.Recent);
        Assert.Equal("Sent to NINA: Obsy1", job.Summary);
    }

    // ---- Case 6: a failed rotation reports the fixed summary and a Warning log ----------------

    [Fact]
    public async Task SendNina_FailedRotation_ReportsTheFixedSummary_AndLogsAWarning()
    {
        var handler = new FakeHandler
        {
            Respond = request => request.RequestUri!.ToString().Contains("set-rotation")
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(HttpStatusCode.OK),
        };
        using var http = new HttpClient(handler);
        var nina = new NinaClient(http);
        using var harness = Build(ninaClient: nina, general: WithNina(
            new IntegrationInstance("Obsy1", "http://a.local", true))).Settle();

        await harness.ViewModel.NinaSendItems.Single().SendCommand.ExecuteAsync(null);

        var job = Assert.Single(harness.Jobs.Recent);
        Assert.Equal(JobResult.Succeeded, job.Result);
        Assert.Equal("Sent to NINA: Obsy1, rotation not applied", job.Summary);
        Assert.Contains(harness.Logger.Entries, e => e.Level == LogLevel.Warning);
    }

    // ---- Case 7: a client failure gives the fixed sentence, never the exception's own text ----

    [Fact]
    public async Task SendNina_ClientFailure_GivesTheFixedSentence_NeverTheExceptionText()
    {
        var handler = new FakeHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError),
        };
        using var http = new HttpClient(handler);
        var nina = new NinaClient(http);
        using var harness = Build(
            header: Factory.PopulatedHeader() with { PositionAngle = null },
            ninaClient: nina,
            general: WithNina(new IntegrationInstance("Obsy1", "http://a.local", true))).Settle();

        await harness.ViewModel.NinaSendItems.Single().SendCommand.ExecuteAsync(null);

        var job = Assert.Single(harness.Jobs.Recent);
        Assert.Equal(JobResult.Failed, job.Result);
        Assert.Equal(IntegrationMessages.NinaFailed, job.Summary);
        Assert.Contains(harness.Logger.Entries, e => e.Level == LogLevel.Error);
    }

    // ---- Case 8: two sends of two instances are two jobs and two lines -------------------------

    [Fact]
    public async Task TwoSendsOfTwoInstances_AreTwoJobs_AndTwoRecentLines()
    {
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var nina = new NinaClient(http);
        using var harness = Build(
            header: Factory.PopulatedHeader() with { PositionAngle = null },
            ninaClient: nina,
            general: WithNina(
                new IntegrationInstance("Obsy1", "http://a.local", true),
                new IntegrationInstance("Obsy2", "http://b.local", true))).Settle();

        foreach (var item in harness.ViewModel.NinaSendItems)
        {
            await item.SendCommand.ExecuteAsync(null);
        }

        Assert.Equal(2, harness.Jobs.Recent.Count);
        Assert.Equal(
            ["Sent to NINA: Obsy2", "Sent to NINA: Obsy1"],
            harness.Jobs.Recent.Select(j => j.Summary));
    }

    // ---- Case 9, Tier 1: one user_action event, instance never the URL, no parent_id ----------
    //
    // The activity delegate has no parameter for a category or a parent id, so no call site can
    // supply either: that is this case's structural proof for "no parent_id" (task5-report.md).

    [Fact]
    public async Task SendNina_WritesOneActivityEvent_InstanceNeverTheUrl()
    {
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var nina = new NinaClient(http);
        using var harness = Build(
            header: Factory.PopulatedHeader() with { PositionAngle = null },
            ninaClient: nina,
            general: WithNina(new IntegrationInstance("Obsy1", "http://a.local", true))).Settle();

        await harness.ViewModel.NinaSendItems.Single().SendCommand.ExecuteAsync(null);

        var written = Assert.Single(harness.Activity);
        Assert.Equal("info", written.Severity);
        Assert.Equal("nina_send", written.EventType);
        var details = written.Details!;
        var type = details.GetType();
        Assert.Equal("Obsy1", type.GetProperty("instance")!.GetValue(details));
        Assert.Null(type.GetProperty("url"));
        Assert.Null(type.GetProperty("Url"));
        Assert.True((bool)type.GetProperty("ok")!.GetValue(details)!);
        Assert.Equal("none", type.GetProperty("rotation")!.GetValue(details));
    }

    // ---- Case 10, Tier 1: the client runs off the calling thread, the result lands back --------
    //
    // Proven directly rather than by mutation, ruling B25. A send made synchronous would block on
    // the handler's park until the 30 second budget expired and then fail here, because
    // task.IsCompleted would be true the moment ExecuteAsync returned.

    [Fact]
    public async Task SendNina_ReturnsToItsCaller_WhileTheRequestIsStillInFlight()
    {
        using var park = new ManualResetEventSlim(false);
        var handler = new FakeHandler { Park = park };
        using var http = new HttpClient(handler);
        var nina = new NinaClient(http);
        using var harness = Build(
            header: Factory.PopulatedHeader() with { PositionAngle = null },
            ninaClient: nina,
            general: WithNina(new IntegrationInstance("Obsy1", "http://a.local", true))).Settle();

        var task = harness.ViewModel.NinaSendItems.Single().SendCommand.ExecuteAsync(null);

        Assert.False(task.IsCompleted);

        park.Set();
        await task.WaitAsync(Budget);

        Assert.True(task.IsCompletedSuccessfully);
        var job = Assert.Single(harness.Jobs.Recent);
        Assert.Equal(JobResult.Succeeded, job.Result);
    }

    // ---- Cases 6, 7 and 9 once over Stellarium (wave2-review Task 5) --------------------------
    //
    // The same three shapes the NINA cases above take, over the other client: the summary and the
    // route it took, the fixed failure sentence, and the one activity row.

    [Fact]
    public async Task SlewStellarium_FocusesByCatalogueName_AndReportsThatRoute()
    {
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var stellarium = new StellariumClient(http);
        using var harness = Build(
            stellariumClient: stellarium,
            general: WithStellarium(new IntegrationInstance("Sky1", "http://a.local", true))).Settle();

        await harness.ViewModel.StellariumSendItems.Single().SendCommand.ExecuteAsync(null);

        // The focus, then the field of view: "M 31" is a whole catalogue name, so one candidate.
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("/api/main/focus", handler.Requests[0].Url, StringComparison.Ordinal);
        Assert.Contains("/api/main/fov", handler.Requests[1].Url, StringComparison.Ordinal);

        var job = Assert.Single(harness.Jobs.Recent);
        Assert.Equal(JobResult.Succeeded, job.Result);
        Assert.Equal("Slewed Stellarium: Sky1", job.Summary);

        var written = Assert.Single(harness.Activity);
        var details = written.Details!;
        Assert.Equal("catalog_name", details.GetType().GetProperty("focus")!.GetValue(details));
    }

    [Fact]
    public async Task SlewStellarium_ClientFailure_GivesTheFixedSentence_NeverTheExceptionText()
    {
        var handler = new FakeHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError),
        };
        using var http = new HttpClient(handler);
        var stellarium = new StellariumClient(http);
        using var harness = Build(
            stellariumClient: stellarium,
            general: WithStellarium(new IntegrationInstance("Sky1", "http://a.local", true))).Settle();

        await harness.ViewModel.StellariumSendItems.Single().SendCommand.ExecuteAsync(null);

        var job = Assert.Single(harness.Jobs.Recent);
        Assert.Equal(JobResult.Failed, job.Result);
        Assert.Equal(IntegrationMessages.StellariumFailed, job.Summary);
        Assert.Contains(harness.Logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Equal("warning", Assert.Single(harness.Activity).Severity);
    }

    [Fact]
    public async Task SlewStellarium_WritesOneActivityEvent_InstanceNeverTheUrl()
    {
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var stellarium = new StellariumClient(http);
        using var harness = Build(
            stellariumClient: stellarium,
            general: WithStellarium(new IntegrationInstance("Sky1", "http://a.local", true))).Settle();

        await harness.ViewModel.StellariumSendItems.Single().SendCommand.ExecuteAsync(null);

        var written = Assert.Single(harness.Activity);
        Assert.Equal("info", written.Severity);
        Assert.Equal("stellarium_send", written.EventType);
        var details = written.Details!;
        var type = details.GetType();
        Assert.Equal("Sky1", type.GetProperty("instance")!.GetValue(details));
        Assert.Null(type.GetProperty("url"));
        Assert.Null(type.GetProperty("Url"));
        Assert.True((bool)type.GetProperty("ok")!.GetValue(details)!);
    }

    // ---- Ruling B32: a general save reaches an open page, through the page's own post ---------

    [Fact]
    public void GeneralChanged_DisablingAnInstance_EmptiesTheSubmenu_WithNoNavigation()
    {
        using var harness = Build(
            general: WithNina(new IntegrationInstance("Obsy1", "http://a.local", true)),
            queuePosts: true);
        harness.Settle().Drain();

        Assert.Single(harness.ViewModel.NinaSendItems);
        var follower = Assert.Single(harness.GeneralFollowers);

        harness.General = WithNina(new IntegrationInstance("Obsy1", "http://a.local", false));
        harness.RaiseGeneralChanged();

        // Routed, not applied on the raising thread: the bound collection is untouched until the
        // posted action runs.
        Assert.Single(harness.ViewModel.NinaSendItems);

        harness.Drain();

        Assert.Empty(harness.ViewModel.NinaSendItems);
        Assert.False(harness.ViewModel.HasNinaSendItems);
        Assert.False(harness.ViewModel.HasSendItems);

        // And the page lets the store's event go on Dispose.
        Assert.NotNull(follower);
        harness.ViewModel.Dispose();
        Assert.Empty(harness.GeneralFollowers);
    }

    // ---- Case 11: disabled at zero checked nights, label appends the count --------------------

    [Fact]
    public void AstroBinCsvCommand_DisabledAtZero_LabelAppendsTheCount()
    {
        using var harness = Build().Settle();
        var page = harness.ViewModel;

        Assert.False(page.AstroBinCsvCommand.CanExecute(null));
        Assert.Equal("AstroBin CSV", page.AstroBinCsvLabel);
        Assert.Equal("Select one or more nights first", page.NoNightCheckedHint);

        page.Sessions[0].IsChecked = true;

        Assert.True(page.AstroBinCsvCommand.CanExecute(null));
        Assert.Equal("AstroBin CSV (1)", page.AstroBinCsvLabel);
    }

    // ---- Case 12: the CSV equals AstroBinCsv.Build's own output, byte for byte ----------------

    [Fact]
    public async Task AstroBinCsvAsync_CopiesExactlyWhatAstroBinCsvBuildRenders()
    {
        var acquisitions = new List<FilterAcquisition>
        {
            new("Ha", 10, 300d, 100, -10.0d, 20.5d, 2.345d, 5.678d),
        };
        using var harness = Build(
            sessions: [Factory.Session(Factory.LastSession)],
            general: new GeneralSettings
            {
                AstroBinFilterIdsDocument = IntegrationSettings.WriteFilterId(null, "Ha", 7),
                AstroBinBortle = 4,
            },
            configure: h => h.OpenedDetails[Factory.LastSession] =
                CardFactory.PopulatedDetail(Factory.LastSession) with { FilterAcquisitions = acquisitions })
            .Settle()
            .SettleCards();

        harness.ViewModel.Sessions[0].IsChecked = true;

        await harness.ViewModel.AstroBinCsvCommand.ExecuteAsync(null);

        var expectedRows = acquisitions.ConvertAll(a => new AstroBinRow(
            Factory.LastSession, a.FilterName, a.FrameCount, a.ExposureTime, a.ModalGain,
            a.MedianSensorTemp, a.MedianSkyQuality, a.MedianFwhm, a.MedianAmbientTemp));
        var expected = AstroBinCsv.Build(
            expectedRows,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Ha"] = 7 },
            4,
            new AliasMap(new Dictionary<string, FilterSetting>(), new EquipmentSettings()));

        var copied = Assert.Single(harness.Clipboard);
        Assert.Equal(expected, copied);
    }

    // ---- Case 13: an unopened checked night is loaded and contributes its rows ----------------

    [Fact]
    public async Task AstroBinCsvAsync_LoadsAnUnopenedCheckedNight_AndContributesItsRows()
    {
        using var harness = Build(
            sessions: [Factory.Session(Factory.LastSession), Factory.Session(Factory.FirstSession)],
            configure: h => h.UnopenedDetails[Factory.FirstSession] =
                CardFactory.PopulatedDetail(Factory.FirstSession) with
                {
                    FilterAcquisitions = [new FilterAcquisition("Ha", 5, 60d, 50, -5d, 21d, 2d, 6d)],
                })
            .Settle()
            .SettleCards();

        // LastSession is the newest and auto-expands; FirstSession never does, so its card carries
        // no detail until the command loads it through the page-level delegate.
        var unopened = harness.ViewModel.Sessions.Single(c => c.SessionDate == Factory.FirstSession);
        Assert.Null(unopened.Detail);
        unopened.IsChecked = true;

        await harness.ViewModel.AstroBinCsvCommand.ExecuteAsync(null);

        Assert.Contains((Factory.ResolvedGroupKey, Factory.FirstSession), harness.SessionDetailCalls);
        var copied = Assert.Single(harness.Clipboard);
        Assert.Equal("AstroBin CSV copied: 1 rows from 1 nights.", harness.ViewModel.AstroBinCsvResultText);
        Assert.Contains("2024-01-05", copied);
    }

    // ---- Case 14: a night whose detail fails to load contributes no row, and is counted -------

    [Fact]
    public async Task AstroBinCsvAsync_AFailedNight_ContributesNoRow_AndIsCounted()
    {
        using var harness = Build(
            sessions: [Factory.Session(Factory.LastSession), Factory.Session(Factory.FirstSession)],
            configure: h => h.OpenedDetails[Factory.LastSession] =
                CardFactory.PopulatedDetail(Factory.LastSession) with
                {
                    FilterAcquisitions = [new FilterAcquisition("Ha", 5, 60d, 50, -5d, 21d, 2d, 6d)],
                })
            .Settle()
            .SettleCards();

        // FirstSession is never seeded into UnopenedDetails, so the page delegate answers null.
        foreach (var card in harness.ViewModel.Sessions)
        {
            card.IsChecked = true;
        }

        await harness.ViewModel.AstroBinCsvCommand.ExecuteAsync(null);

        // Spec 12.16: one sentence, the clause inside it, and the night that loaded nothing is not
        // one of the nights the rows came from.
        Assert.Equal(
            "AstroBin CSV copied: 1 rows from 1 nights, 1 nights could not be loaded.",
            harness.ViewModel.AstroBinCsvResultText);
    }

    // ---- Case 16: a checked night that yields no acquisition is not one of the nights ---------

    [Fact]
    public async Task AstroBinCsvAsync_ANightWithNoAcquisitions_IsNotCountedAmongTheNights()
    {
        using var harness = Build(
            sessions: [Factory.Session(Factory.LastSession), Factory.Session(Factory.FirstSession)],
            configure: h =>
            {
                h.OpenedDetails[Factory.LastSession] =
                    CardFactory.PopulatedDetail(Factory.LastSession) with
                    {
                        FilterAcquisitions = [new FilterAcquisition("Ha", 5, 60d, 50, -5d, 21d, 2d, 6d)],
                    };
                h.UnopenedDetails[Factory.FirstSession] =
                    CardFactory.PopulatedDetail(Factory.FirstSession) with { FilterAcquisitions = [] };
            })
            .Settle()
            .SettleCards();

        foreach (var card in harness.ViewModel.Sessions)
        {
            card.IsChecked = true;
        }

        await harness.ViewModel.AstroBinCsvCommand.ExecuteAsync(null);

        Assert.Equal(
            "AstroBin CSV copied: 1 rows from 1 nights.", harness.ViewModel.AstroBinCsvResultText);

        // The activity row carries the same figure the sentence does.
        var written = Assert.Single(harness.Activity);
        var details = written.Details!;
        Assert.Equal(1, details.GetType().GetProperty("nights")!.GetValue(details));
    }

    // ---- Case 15: the AstroBin CSV registers no job, whatever the clipboard does --------------
    //
    // ShellIntegration.CopyTextAsync logs a failed copy and never rethrows, so the page carries no
    // clipboard-failure branch to prove (fix-p21-d-report.md, ruling B26).

    [Fact]
    public async Task AstroBinCsvAsync_RegistersNoJob()
    {
        using var harness = Build(
            sessions: [Factory.Session(Factory.LastSession)],
            configure: h => h.OpenedDetails[Factory.LastSession] =
                CardFactory.PopulatedDetail(Factory.LastSession) with
                {
                    FilterAcquisitions = [new FilterAcquisition("Ha", 5, 60d, 50, -5d, 21d, 2d, 6d)],
                })
            .Settle()
            .SettleCards();

        harness.ViewModel.Sessions[0].IsChecked = true;

        await harness.ViewModel.AstroBinCsvCommand.ExecuteAsync(null);

        Assert.Single(harness.Clipboard);
        Assert.Empty(harness.Jobs.Running);
        Assert.Empty(harness.Jobs.Recent);
    }
}
