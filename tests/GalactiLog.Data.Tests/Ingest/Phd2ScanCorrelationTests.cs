using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using GalactiLog.Core.Catalogs;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Settings;
using GalactiLog.Core.Targets;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GalactiLog.Data.Tests.Ingest;

// Task 5b: spec 10.3 step 5 item 5, the correlation's place in the scan. The pass itself is
// Phd2CorrelationTests' subject; everything here is the WIRING, which is the key gate, the
// cancellation gate, the phd2_correlate envelope, and the four events of spec 10.9 built from the
// result record and from nothing else.
//
// This is tests/**, so writing fixture guide logs here is fine; nothing the scan itself runs
// writes a byte to disk.
public class Phd2ScanCorrelationTests : IDisposable
{
    private readonly TestDatabaseHandle _db = TestDatabaseFactory.CreateMigratedDatabase();
    private readonly string _root = Directory.CreateTempSubdirectory("galactilog-phd2corr-").FullName;

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    // --- fixtures ----------------------------------------------------------------------------
    // The CSV header is compared byte for byte by the parser (spec 7.6), so it is written out in
    // full rather than assembled.

    private const string GuideCsvHeader =
        "Frame,Time,mount,dx,dy,RARawDistance,DECRawDistance,RAGuideDistance,DECGuideDistance," +
        "RADuration,RADirection,DECDuration,DECDirection,XStep,YStep,StarMass,SNR,ErrorCode";

    private static readonly DateOnly Night = new(2025, 3, 19);

    // One guiding section on Night, profile TestRig, with a pixel scale and two CSV rows. Two
    // samples is deliberately below the ten-sample gate: the wiring cases want a night the pass
    // really visits, not a fill, which Phd2CorrelationTests already pins.
    private const string DesktopLog = $"""
        PHD2 version 2.6.13 [Windows], Log version 2.5. Log enabled at 2025-03-19 21:30:00
        Guiding Begins at 2025-03-19 21:31:00
        Equipment Profile = TestRig
        Pixel scale = 1.50 arc-sec/px, Binning = 1, Focal length = 500 mm
        Camera = Test Camera
        Exposure = 500 ms
        {GuideCsvHeader}
        1,0.500,"Mount",0.10,0.20,0.400,0.300,0.100,0.100,10,W,12,N,0,0,12000.0,25.0,0
        2,1.000,"Mount",0.10,0.20,-0.400,-0.300,0.100,0.100,10,E,12,S,0,0,12000.0,25.0,0
        Guiding Ends at 2025-03-19 21:31:02
        Log closed at 2025-03-19 21:32:00

        """;

    // The ASIAIR shape: no Pixel scale line at all, one profile, on Night.
    private const string NoPixelScaleLog = $"""
        PHD2 version, Log version 2.5. Log enabled at 2025-03-19 21:30:00
        Guiding Begins at 2025-03-19 21:31:00
        Equipment Profile = AsiRig
        Camera = ASI120MM
        Exposure = 500 ms
        {GuideCsvHeader}
        1,0.500,"Mount",0.10,0.20,0.400,0.300,0.100,0.100,10,W,12,N,0,0,12000.0,25.0,0
        Guiding Ends at 2025-03-19 21:31:02
        Log closed at 2025-03-19 21:32:00

        """;

    // Two distinct unmapped profiles on the same night, which is spec 7.6 rig selection step 3:
    // nothing is attributed and both names go into phd2_correlation_unattributed. Both sections
    // sit INSIDE the seeded frame's exposure window on purpose: the session lookup is by time
    // overlap, so a second section a few minutes later would simply not be found and the night
    // would read as a single-profile one, which is not the case this fixture is for.
    private const string TwoProfileLog = $"""
        PHD2 version 2.6.13 [Windows], Log version 2.5. Log enabled at 2025-03-19 21:30:00
        Guiding Begins at 2025-03-19 21:31:00
        Equipment Profile = RigOne
        Pixel scale = 1.50 arc-sec/px, Binning = 1, Focal length = 500 mm
        Camera = Test Camera
        Exposure = 500 ms
        {GuideCsvHeader}
        1,0.500,"Mount",0.10,0.20,0.400,0.300,0.100,0.100,10,W,12,N,0,0,12000.0,25.0,0
        Guiding Ends at 2025-03-19 21:31:02
        Guiding Begins at 2025-03-19 21:31:30
        Equipment Profile = RigTwo
        Pixel scale = 1.50 arc-sec/px, Binning = 1, Focal length = 500 mm
        Camera = Test Camera
        Exposure = 500 ms
        {GuideCsvHeader}
        1,0.500,"Mount",0.10,0.20,0.400,0.300,0.100,0.100,10,W,12,N,0,0,12000.0,25.0,0
        Guiding Ends at 2025-03-19 21:31:32
        Log closed at 2025-03-19 21:40:00

        """;

    // A second file whose profile is named by no map entry, so with no global zone it resolves to
    // no zone at all and its session is stored with a null started_at_utc (ruling F1).
    private const string UnzonedLog = $"""
        PHD2 version 2.6.13 [Windows], Log version 2.5. Log enabled at 2025-03-18 21:30:00
        Guiding Begins at 2025-03-18 21:31:00
        Equipment Profile = RigZ
        Pixel scale = 1.50 arc-sec/px, Binning = 1, Focal length = 500 mm
        Camera = Test Camera
        Exposure = 500 ms
        {GuideCsvHeader}
        1,0.500,"Mount",0.10,0.20,0.400,0.300,0.100,0.100,10,W,12,N,0,0,12000.0,25.0,0
        Guiding Ends at 2025-03-18 21:31:02
        Log closed at 2025-03-18 21:32:00

        """;

    /// <summary>A guiding section that really fills a frame: 40 rows two seconds apart from
    /// offset 0.5, which puts 30 samples inside a sixty second exposure opening one second after
    /// the section starts and spans 58 of its 60 seconds. Both correlation gates (ten samples,
    /// fifty percent coverage) clear comfortably.</summary>
    private static string QualifyingLog(string profile, string start)
    {
        var text = new StringBuilder();
        text.AppendLine($"PHD2 version 2.6.13 [Windows], Log version 2.5. Log enabled at {start}");
        text.AppendLine($"Guiding Begins at {start}");
        text.AppendLine($"Equipment Profile = {profile}");
        text.AppendLine("Pixel scale = 1.50 arc-sec/px, Binning = 1, Focal length = 500 mm");
        text.AppendLine("Camera = Test Camera");
        text.AppendLine("Exposure = 500 ms");
        text.AppendLine(GuideCsvHeader);
        for (var i = 0; i < 40; i++)
        {
            var offset = (0.5 + (2 * i)).ToString("0.000", CultureInfo.InvariantCulture);
            var ra = i % 2 == 0 ? "0.400" : "-0.400";
            var dec = i % 2 == 0 ? "0.800" : "-0.800";
            text.AppendLine(
                $"{i + 1},{offset},\"Mount\",0.10,0.20,{ra},{dec},0.100,0.100,10,W,12,N,0,0,12000.0,25.0,0");
        }

        text.AppendLine("Log closed at 2025-03-20 01:00:00");
        return text.ToString();
    }

    // --- harness -----------------------------------------------------------------------------

    private const int FitsBlockSize = 2880;

    // The same minimal primary header ScanPipelineTests uses: NAXIS = 0, so one 2880-byte block is
    // a complete file as far as FitsHeaderReader is concerned (spec 6.1.5).
    private static byte[] MinimalFits(string telescope, string dateObs, double exposure)
    {
        var cards = new List<string>
        {
            "SIMPLE  =                    T",
            "BITPIX  =                   16",
            "NAXIS   =                    0",
            "IMAGETYP= 'LIGHT   '",
            "OBJECT  = 'M 31    '",
            $"TELESCOP= '{telescope,-8}'",
            $"DATE-OBS= '{dateObs}'",
            $"EXPTIME = {exposure.ToString("0.0", CultureInfo.InvariantCulture),20}",
            "END",
        };

        var text = new StringBuilder();
        foreach (var card in cards)
        {
            text.Append(card.PadRight(80));
        }

        var blocks = ((text.Length + FitsBlockSize) - 1) / FitsBlockSize;
        return Encoding.ASCII.GetBytes(text.ToString().PadRight(blocks * FitsBlockSize));
    }

    private void WriteLog(string name, string text)
        => File.WriteAllText(Path.Combine(_root, name), text);

    private GalactiLogContext OpenRead() => new(GalactiLogContextOptions.Create(_db.ConnectionString));

    /// <summary>One unfilled LIGHT frame on <see cref="Night"/>, outside the scan root so the
    /// frame side of the run neither discovers nor prunes it. It is what gives the correlation a
    /// rig and something to consider.</summary>
    private Guid SeedLightFrame(
        string telescope = "Newt 8", string? source = null, double? value = null)
    {
        using var context = new GalactiLogContext(
            GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true));
        var id = Guid.NewGuid();
        context.Images.Add(new Image
        {
            Id = id,
            FilePath = $@"C:\library\{id:N}.fits",
            FileName = $"{id:N}.fits",
            ImageType = "LIGHT",
            SessionDate = Night,
            CaptureDate = new DateTime(2025, 3, 19, 21, 31, 0, DateTimeKind.Unspecified),
            ExposureTime = 60,
            Telescope = telescope,
            GuidingRmsSource = source,
            GuidingRmsArcsec = value,
            GuidingRmsRaArcsec = value,
            GuidingRmsDecArcsec = value,
        });
        context.SaveChanges();
        return id;
    }

    private Image Read(Guid id)
    {
        using var context = OpenRead();
        return context.Images.Single(image => image.Id == id);
    }

    // RunPhd2Correlation catches a throw out of the pass and logs it at error, because spec 10.3
    // says nowhere that a correlation failure fails the scan. That is right for the product and it
    // would make every case below pass vacuously, so the coordinator is given a logger that keeps
    // what it was told and the cases assert on it.
    private readonly List<string> _errors = [];

    private sealed class RecordingLogger(List<string> errors) : ILogger<ScanCoordinator>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
            {
                errors.Add($"{formatter(state, exception)} :: {exception}");
            }
        }
    }

    private void AssertNothingWasSwallowed()
        => Assert.True(_errors.Count == 0, string.Join(" | ", _errors));

    // The store MakeCoordinator built, so a case can read back what the pass wrote to the general
    // document (spec 7.6's phd2_correlation_pending) without building a second one.
    private SettingsStore _settings = null!;

    private bool CorrelationPending => _settings.GetGeneral().Phd2CorrelationPending;

    /// <param name="correlationPending">Spec 7.6's durable obligation, armed for the cases that
    /// are about it. The setup save below moves guiding inputs, so the store sets the flag as it is
    /// meant to; this parameter is what puts it back to the state the case wants, and false is
    /// what every other case needs so that the widening it causes is not silently in play.</param>
    private ScanCoordinator MakeCoordinator(
        bool phd2Enabled = true, string timezone = "UTC", string? profileMapJson = null,
        bool correlationPending = false)
    {
        var settings = _settings = new SettingsStore(new SettingsRepository(_db.ConnectionString));
        settings.SaveGeneral(new GeneralSettings
        {
            ScanRoots = [_root],
            ScanFilters = ScanFilterConfig.Empty,
            Phd2ScanEnabled = phd2Enabled,
            ObserverTimezone = timezone,
            // Pinned rather than left to the machine: the frame's imaging night and the guiding
            // session's have to be derived on the same basis for any of these cases to mean
            // anything, and TimeZoneInfo.Local is not a basis a test may depend on.
            Timezone = "UTC",
            ObserverLongitude = 0,
            Phd2ProfileMap = profileMapJson is null
                ? null
                : JsonSerializer.Deserialize<JsonElement>(profileMapJson),
        });
        // The setup save above moves guiding inputs, so the store sets the obligation as it is
        // meant to. Disarming it goes through the one production door, because MutateGeneral no
        // longer clears the flag by design (fix-wave review P2-3): every save preserves it and
        // ClearCorrelationPendingIfUnchanged is the only member that discharges it.
        if (!correlationPending)
        {
            settings.ClearCorrelationPendingIfUnchanged(settings.GetGeneral());
        }
        var resolver = new TargetResolver(
            _db.ConnectionString, StaticCatalogLoader.ResolveCatalogsDirectory(),
            new CatalogCacheRepository(_db.ConnectionString),
            new SimbadClient(new ThrowingHandler()), new SesameClient(new ThrowingHandler()));
        return new ScanCoordinator(
            _db.ConnectionString, settings, resolver, new ScanRunRepository(_db.ConnectionString),
            new RecordingLogger(_errors));
    }

    // A clean "queried, found nothing" from both sources, the shape ScanPipelineTests uses. Not a
    // failure, so it never trips the circuit breaker, and no frame in these fixtures needs to
    // resolve to anything: the guiding columns are the subject and the target is not.
    //
    // Both members, not only the async one: TargetResolver reaches the catalogue clients through
    // the SYNCHRONOUS HttpClient.Send, which throws NotSupportedException on a handler that
    // overrides SendAsync alone.
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.RequestUri!.Host == "simbad.cds.unistra.fr"
                ? "::error::\nNo object found\n"
                : "<?xml version=\"1.0\"?><Sesame></Sesame>";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(Send(request, ct));
    }

    private List<ActivityEvent> EventsOfType(string eventType)
    {
        using var context = OpenRead();
        return context.ActivityEvents
            .Where(e => e.EventType == eventType)
            .OrderBy(e => e.Id)
            .ToList();
    }

    private static JsonElement Details(ActivityEvent row)
        => JsonDocument.Parse(row.Details!).RootElement.Clone();

    // --- brief 8.5 case 20: the trigger, and the details members come from the result ----------

    [Fact]
    public async Task AScan_WritesPhd2CorrelationComplete_WithTriggerScanAndTheResultsFigures()
    {
        // A failure looks like: a user with guide logs has nothing in the feed telling them the
        // correlation ran, or the event carries recomputed numbers that can drift from the pass's
        // own. Every member below is read straight off Phd2CorrelationResult.
        WriteLog("PHD2_GuideLog_2025-03-19_213000.txt", DesktopLog);
        SeedLightFrame();

        await MakeCoordinator().RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        AssertNothingWasSwallowed();
        var complete = Assert.Single(EventsOfType("phd2_correlation_complete"));
        var details = Details(complete);
        Assert.Equal("scan", details.GetProperty("trigger").GetString());
        Assert.Equal(1, details.GetProperty("nights").GetInt32());
        Assert.Equal(1, details.GetProperty("frames_considered").GetInt32());
        Assert.Equal(0, details.GetProperty("filled").GetInt32());
        Assert.Equal(0, details.GetProperty("cleared").GetInt32());

        // Two samples in a sixty second exposure: below the ten-sample gate, so the frame is
        // counted and left null rather than given a weak figure.
        Assert.Equal(1, details.GetProperty("below_gate").GetInt32());
        Assert.Equal("info", complete.Severity);
    }

    [Fact]
    public async Task AScan_RaisesThePhd2CorrelateEnvelope_WithTheNightCount()
    {
        // A failure looks like: the correlation runs with no job in the flyout and no phase in the
        // status bar, so a pass over a large corpus looks like the application has hung. The
        // registered job opens off this envelope (ruling F5).
        WriteLog("PHD2_GuideLog_2025-03-19_213000.txt", DesktopLog);
        SeedLightFrame();

        var coordinator = MakeCoordinator();
        var envelopes = new List<ScanProgress>();
        coordinator.ProgressChanged += (_, progress) => envelopes.Add(progress);

        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        // Review P3-2: without this line the case is satisfied by a pass that threw on its first
        // night, because the envelope it asserts is reported before any night is visited.
        AssertNothingWasSwallowed();
        var correlate = envelopes.Where(e => e.Task == ScanTaskNames.Phd2Correlate).ToList();
        Assert.NotEmpty(correlate);
        Assert.Contains(correlate, e => e.TotalSteps == 1);
    }

    // --- brief 8.5 case 21: the parent ---------------------------------------------------------

    [Fact]
    public async Task EveryCorrelationEventRaisedInsideAScan_CarriesTheScansParentId()
    {
        // A failure looks like: the Activity feed cannot collapse the scan and the correlation's
        // warnings float at the top level as if they were unrelated to it.
        WriteLog("PHD2_GuideLog_2025-03-19_213000.txt", TwoProfileLog);
        SeedLightFrame();

        await MakeCoordinator().RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        using var context = OpenRead();
        var started = context.ActivityEvents.Single(e => e.EventType == "scan_started");
        var correlation = context.ActivityEvents
            .ToList()
            .Where(e => e.EventType.StartsWith("phd2_correlation", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, correlation.Count);
        Assert.All(correlation, e => Assert.Equal(started.Id, e.ParentId));
    }

    // --- the three warnings, and none of them when its list is empty ---------------------------

    [Fact]
    public async Task ANightWithTwoUnmappedProfiles_WritesPhd2CorrelationUnattributed()
    {
        // A failure looks like: one of the two is picked arbitrarily, half the night's frames
        // carry another rig's guiding, and nothing in the feed says a choice was made.
        WriteLog("PHD2_GuideLog_2025-03-19_213000.txt", TwoProfileLog);
        SeedLightFrame();

        await MakeCoordinator().RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        var row = Assert.Single(EventsOfType("phd2_correlation_unattributed"));
        Assert.Equal("warning", row.Severity);
        var details = Details(row);
        Assert.Equal(
            ["RigOne", "RigTwo"],
            details.GetProperty("profiles").EnumerateArray().Select(p => p.GetString()!).ToArray());
        Assert.Equal(1, details.GetProperty("nights").GetInt32());
    }

    [Fact]
    public async Task ASessionWithNoPixelScale_MakesTheCorrelationWritePhd2PixelScaleMissing()
    {
        // A failure looks like: a reader who sees no guiding on an ASIAIR night is left to guess
        // why, which spec 7.6 says the port refuses to do.
        WriteLog("PHD2_GuideLog_2025-03-19_213000.txt", NoPixelScaleLog);
        SeedLightFrame();

        await MakeCoordinator().RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        // ONE row, not two. Spec 10.9 says this warning is raised once per pass, and a scan runs
        // two passes over one corpus: the guide-log ingest names AsiRig from the log it opened,
        // and the correlation that follows names only what the ingest did NOT, which here is
        // nothing at all. The survivor is the ingest's, identified by spec 10.9's session_count,
        // which the correlation has no count to give (report escalation 2).
        //
        // A failure looks like: one scan leaves two identical-looking warnings in the collapsed
        // entry and a reader counts their ASIAIR problem twice.
        AssertNothingWasSwallowed();
        var row = Assert.Single(EventsOfType("phd2_pixel_scale_missing"));
        var details = Details(row);
        Assert.Equal(1, details.GetProperty("session_count").GetInt32());
        Assert.Equal(
            ["AsiRig"],
            details.GetProperty("profiles").EnumerateArray().Select(p => p.GetString()!).ToArray());
    }

    [Fact]
    public async Task ARescanWhoseIngestOpenedNoLog_StillWritesTheCorrelationsOwnPixelScaleWarning()
    {
        // The subtraction must not become "drop the correlation's copy". The two lists are
        // genuinely different sets: the ingest names the profiles it met in the logs it actually
        // OPENED this run, and on a rescan the delta skip opens none, so its list is empty and
        // only the correlation has anything to say. That is the rescan that matters most, because
        // it is every scan after the first.
        //
        // A failure looks like: an ASIAIR user sees the warning once, ever, and every later scan
        // is silent about a corpus that still cannot be correlated.
        WriteLog("PHD2_GuideLog_2025-03-19_213000.txt", NoPixelScaleLog);
        var coordinator = MakeCoordinator();

        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);
        Assert.Single(EventsOfType("phd2_pixel_scale_missing"));

        // The frame arrives after the first scan, so the second scan's correlation visits the
        // night through the fill half of the union while the guide log is delta-skipped.
        SeedLightFrame();
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        AssertNothingWasSwallowed();
        var rows = EventsOfType("phd2_pixel_scale_missing");
        Assert.Equal(2, rows.Count);

        // The second is the correlation's: no session_count, and it named AsiRig although this
        // run's ingest named nobody.
        var second = Details(rows[1]);
        Assert.False(second.TryGetProperty("session_count", out _));
        Assert.Equal(
            ["AsiRig"],
            second.GetProperty("profiles").EnumerateArray().Select(p => p.GetString()!).ToArray());
    }

    [Fact]
    public async Task AnUnzonedSessionMetByAVisitedPass_WritesPhd2TimezoneUnset()
    {
        // Ruling F1: the session is skipped rather than read as if its wall clock were UTC, and
        // the user is told which profile to give a zone to. A failure looks like a frame stamped
        // phd2 with an RMS measured hours away from its exposure.
        WriteLog("PHD2_GuideLog_2025-03-19_213000.txt", DesktopLog);
        WriteLog("PHD2_GuideLog_2025-03-18_213000.txt", UnzonedLog);
        SeedLightFrame();

        // No global zone: TestRig carries its own, RigZ carries none and resolves to unset.
        var coordinator = MakeCoordinator(
            timezone: "", profileMapJson: """{"TestRig":{"timezone":"UTC"}}""");
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        // ONE row, not two, and it names RigZ. Spec 10.9's "once per pass, whatever the mix of
        // nights and profiles": the guide-log ingest named RigZ from the log it opened, and the
        // correlation that follows in the same scan names only what the ingest did not, which here
        // is nothing.
        //
        // A failure looks like: one scan leaves two identical-looking timezone warnings under the
        // same collapsed entry, so a reader cannot tell one unzoned profile from two.
        AssertNothingWasSwallowed();
        var row = Assert.Single(EventsOfType("phd2_timezone_unset"));
        var details = Details(row);
        Assert.Contains(
            "RigZ", details.GetProperty("profiles").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal("phd2_profile_map", details.GetProperty("setting").GetString());
    }

    [Fact]
    public async Task AScanWithNothingToWarnAbout_WritesTheCompleteEventAndNoWarning()
    {
        // Coordinator override 2: none when the list is empty. A warning that fires on every scan
        // interval whatever the corpus holds is noise a reader learns to ignore.
        WriteLog("PHD2_GuideLog_2025-03-19_213000.txt", DesktopLog);
        SeedLightFrame();

        await MakeCoordinator().RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.Single(EventsOfType("phd2_correlation_complete"));
        Assert.Empty(EventsOfType("phd2_correlation_unattributed"));
        Assert.Empty(EventsOfType("phd2_pixel_scale_missing"));
        Assert.Empty(EventsOfType("phd2_timezone_unset"));
    }

    // --- the ordering guarantee against ScanWriter (coordinator ruling, Task 5a review) --------

    [Fact]
    public async Task ARescanThatRewritesAFilledFrame_EndsWithThePhd2ValueBack()
    {
        // ScanWriter nulls a frame's phd2-sourced guiding when it rewrites an existing image row
        // from metadata carrying no CSV guiding. It self-heals only because the correlation runs
        // later in the SAME scan and visits that night, and that ordering is this file's to
        // guarantee.
        //
        // A failure looks like: a user rescans a library after touching a frame's file, and the
        // guiding column they had goes blank and stays blank, with nothing in the feed saying so
        // and no way to tell it from a night PHD2 never guided.
        //
        // The second scan's guide log is unchanged, so the delta skip never opens it and the
        // ingest reports NO nights at all. An empty explicit night set visits nothing; spec 7.6's
        // incremental mode visits the nights holding a LIGHT frame with no guiding RMS, which is
        // exactly this frame's. That is the branch this case exists to hold down.
        var frame = Path.Combine(_root, "light_m31.fits");
        File.WriteAllBytes(frame, MinimalFits("Newt 8", "2025-03-19T21:31:00", 60.0));
        WriteLog("PHD2_GuideLog_2025-03-19_213059.txt", QualifyingLog("TestRig", "2025-03-19 21:30:59"));

        var coordinator = MakeCoordinator(profileMapJson: """{"TestRig":{"telescope":"Newt 8"}}""");
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        double filledValue;
        using (var context = OpenRead())
        {
            var image = context.Images.Single();
            Assert.Equal("phd2", image.GuidingRmsSource);
            Assert.NotNull(image.GuidingRmsArcsec);
            filledValue = image.GuidingRmsArcsec!.Value;
        }

        // Move the frame's modification time well past the one second tolerance so the classifier
        // calls it changed and the writer rewrites its row. The guide log is left alone.
        File.SetLastWriteTimeUtc(frame, File.GetLastWriteTimeUtc(frame).AddHours(1));

        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        using var after = OpenRead();
        var rewritten = after.Images.Single();
        Assert.Equal("phd2", rewritten.GuidingRmsSource);
        Assert.Equal(filledValue, rewritten.GuidingRmsArcsec!.Value, 6);
    }

    [Fact]
    public async Task AScanThatIngestsOneNightsLogAndRewritesAnothersFrame_RestoresBothNights()
    {
        // The explicit night set is the nights THIS run ingested, and ScanWriter nulls a
        // phd2-sourced value on any image row it rewrites, whichever night that row belongs to.
        // With only the explicit pass, a run that re-read night A's log and rewrote a frame on
        // night B leaves B blank, and nothing brings it back until some later run happens to
        // ingest nothing at all. Spec 5.2 says the correlation later in the same scan visits that
        // night and restores it, so the night set the pass is given is the UNION of the nights this
        // run ingested and the nights holding a LIGHT frame with no guiding RMS at all, and the
        // pass is called once over it.
        //
        // A failure looks like: a user rescans after touching one frame, the guiding column on
        // that frame goes blank, and whether it ever comes back depends on whether some unrelated
        // guide log happened to change in the same run.
        var frame = Path.Combine(_root, "light_m31.fits");
        File.WriteAllBytes(frame, MinimalFits("Newt 8", "2025-03-19T21:31:00", 60.0));
        WriteLog("PHD2_GuideLog_nightB.txt", QualifyingLog("TestRig", "2025-03-19 21:30:59"));
        WriteLog("PHD2_GuideLog_nightA.txt", QualifyingLog("TestRig", "2025-03-18 21:30:59"));

        var coordinator = MakeCoordinator(profileMapJson: """{"TestRig":{"telescope":"Newt 8"}}""");
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        double filledValue;
        using (var context = OpenRead())
        {
            var image = context.Images.Single();
            Assert.Equal("phd2", image.GuidingRmsSource);
            filledValue = image.GuidingRmsArcsec!.Value;
        }

        // Night A's log changes, so the delta skip opens it and the ingest reports night A and
        // ONLY night A. Night B's log is left alone and is skipped unchanged. The frame, which is
        // night B's, is touched so the classifier calls it changed and the writer rewrites its row.
        WriteLog("PHD2_GuideLog_nightA.txt", QualifyingLog("RigA2", "2025-03-18 21:30:58"));
        File.SetLastWriteTimeUtc(frame, File.GetLastWriteTimeUtc(frame).AddHours(1));

        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        AssertNothingWasSwallowed();
        using var after = OpenRead();
        var rewritten = after.Images.Single();
        Assert.Equal("phd2", rewritten.GuidingRmsSource);
        Assert.Equal(filledValue, rewritten.GuidingRmsArcsec!.Value, 6);

        // One event per SCAN, because there is one call over one union and never a second pass to
        // merge: two scans leave exactly two rows, and `nights` on each is a count of nights
        // visited once.
        Assert.Equal(2, EventsOfType("phd2_correlation_complete").Count);
    }

    [Fact]
    public async Task ANightEnteringTheSetByTheFillRouteOnly_LeavesItsCsvValueUntouched()
    {
        // A night that enters the set only because something on it needs a fill is still visited
        // in EXPLICIT mode, so it is cleared before it is refilled. That is safe rather than
        // merely convenient: both the clear and the fill go through Phd2Correlation's one write
        // method, which carries `guiding_rms_source IS NULL OR guiding_rms_source = 'phd2'` inside
        // the update statement (ruling F6), so a csv value is unreachable from either write.
        //
        // A failure looks like: a user rescans, a frame on the same night happens to need a fill,
        // and a guiding figure measured beside the telescope by the CSV sidecar is emptied by a
        // clear it was never supposed to be in scope for, with no way to tell it happened.
        WriteLog("PHD2_GuideLog_2025-03-19.txt", QualifyingLog("TestRig", "2025-03-19 21:30:59"));
        var coordinator = MakeCoordinator(profileMapJson: """{"TestRig":{"telescope":"Newt 8"}}""");

        // Scan one ingests the log while the library holds no frame at all.
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        var csvFrame = SeedLightFrame(source: "csv", value: 9.0);
        var openFrame = SeedLightFrame();

        // Scan two: the log is unchanged, so the delta skip never opens it and the ingest reports
        // no night at all. The night reaches the pass through the fill half of the union and
        // nothing else, which is the route this case exists for.
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        AssertNothingWasSwallowed();

        var csv = Read(csvFrame);
        Assert.Equal("csv", csv.GuidingRmsSource);
        Assert.Equal(9.0, csv.GuidingRmsArcsec!.Value, 6);
        Assert.Equal(9.0, csv.GuidingRmsRaArcsec!.Value, 6);
        Assert.Equal(9.0, csv.GuidingRmsDecArcsec!.Value, 6);

        // The night really was visited, and really was in explicit mode: the other frame is filled
        // from the same session, and `cleared` is zero because the only frame carrying a value was
        // the csv one, which the predicate held out of the clear.
        var open = Read(openFrame);
        Assert.Equal("phd2", open.GuidingRmsSource);
        Assert.NotNull(open.GuidingRmsArcsec);

        var details = Details(EventsOfType("phd2_correlation_complete")[1]);
        Assert.Equal(0, details.GetProperty("cleared").GetInt32());
        Assert.Equal(1, details.GetProperty("filled").GetInt32());
    }

    // --- the two gates on running at all -------------------------------------------------------

    [Fact]
    public async Task AScanWithTheKeyOnAndNothingToCorrelate_OpensNoJob()
    {
        // The pass runs, because the key is on, and it has an empty union to visit. It reports one
        // terminal envelope with TotalSteps 0, and Task 4b's seam opens a sub-job only on an
        // envelope carrying TotalSteps > 0, so no job appears for a library with no guide log.
        //
        // A failure looks like: every scan interval leaves a finished "correlating" entry in the
        // job flyout for a user who has never had a guide log, which is noise they cannot turn off.
        var coordinator = MakeCoordinator();
        var envelopes = new List<ScanProgress>();
        coordinator.ProgressChanged += (_, progress) => envelopes.Add(progress);

        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        AssertNothingWasSwallowed();
        var correlate = envelopes.Where(e => e.Task == ScanTaskNames.Phd2Correlate).ToList();
        Assert.NotEmpty(correlate);
        Assert.All(correlate, e => Assert.Equal(0, e.TotalSteps));
    }

    [Fact]
    public async Task AScanWithTheKeyOff_RunsNoCorrelationAtAll()
    {
        // Spec 10.3: turning the key off is not a deletion, and nothing in phd2_* is read,
        // written or pruned. A failure looks like a clear running over a user who switched the
        // feature off.
        WriteLog("PHD2_GuideLog_2025-03-19_213000.txt", DesktopLog);
        SeedLightFrame();

        var coordinator = MakeCoordinator(phd2Enabled: false);
        var envelopes = new List<ScanProgress>();
        coordinator.ProgressChanged += (_, progress) => envelopes.Add(progress);

        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.Empty(EventsOfType("phd2_correlation_complete"));
        Assert.DoesNotContain(envelopes, e => e.Task == ScanTaskNames.Phd2Correlate);
    }

    [Fact]
    public async Task AScanCancelledInsideTheGuideLogIngest_RunsNoCorrelation()
    {
        // Phd2Ingest.Run returns on cancellation rather than throwing, so the pipeline's own
        // between-phases checkpoint sits AFTER this call. A failure looks like a clear and refill
        // over a night whose logs were only half read, which is exactly the partial view spec
        // 10.5 says a cancelled run must not act on.
        WriteLog("PHD2_GuideLog_2025-03-19_213000.txt", DesktopLog);
        SeedLightFrame();

        var coordinator = MakeCoordinator();
        using var cts = new CancellationTokenSource();
        coordinator.ProgressChanged += (_, progress) =>
        {
            if (progress.Task == ScanTaskNames.Phd2Ingest)
            {
                cts.Cancel();
            }
        };

        try
        {
            await coordinator.RunAsync(ScanTrigger.Manual, null, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // The run records itself cancelled; either shape is fine for this assertion.
        }

        Assert.Empty(EventsOfType("phd2_correlation_complete"));
    }

    // --- spec 10.9's phd2_correlation_failed, and the cancelled pass that writes neither ---------

    [Fact]
    public async Task ACorrelationThatThrows_WritesPhd2CorrelationFailed_AndLeavesTheScanComplete()
    {
        // Review P2-2. A throw used to leave one line in the application log and nothing else: no
        // row in the feed, and a phd2_correlate job that ScanStatusService closed SUCCEEDED off
        // the next phase's envelope. So a user whose guiding never filled saw a finished,
        // successful "Correlating PHD2 guiding" entry in the flyout and had no way at all to find
        // out what happened.
        //
        // The throw is real rather than injected: the table RederiveSessionTimes walks is dropped,
        // so the very first thing the correlation does fails, the way a corrupt or partly migrated
        // database would fail it.
        SeedLightFrame();
        using (var context = new GalactiLogContext(
            GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true)))
        {
            context.Database.ExecuteSqlRaw("DROP TABLE phd2_calibrations");
        }

        var coordinator = MakeCoordinator(correlationPending: true);
        var envelopes = new List<ScanProgress>();
        coordinator.ProgressChanged += (_, progress) => envelopes.Add(progress);

        var outcome = await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        // The frame scan is untouched: spec 10.9 says a failed correlation never fails it.
        Assert.Equal("complete", outcome.State);
        Assert.Single(EventsOfType("scan_complete"));

        var failed = Assert.Single(EventsOfType("phd2_correlation_failed"));
        Assert.Equal("error", failed.Severity);
        Assert.Equal("scan", failed.Category);
        var details = Details(failed);
        Assert.Equal("scan", details.GetProperty("trigger").GetString());
        Assert.Contains("phd2_calibrations", details.GetProperty("reason").GetString()!, StringComparison.Ordinal);

        // Parented to the run, like every other phd2_* row raised inside a scan.
        using (var context = OpenRead())
        {
            Assert.Equal(
                context.ActivityEvents.Single(e => e.EventType == "scan_started").Id, failed.ParentId);
        }

        // Exactly one of complete and failed per pass that ran to an end.
        Assert.Empty(EventsOfType("phd2_correlation_complete"));

        // The terminal envelope that makes the registered job end FAILED with the reason as its
        // summary. Its negative total is the one signal the Data layer has for the App layer's job
        // seam; ScanStatusServiceTests pins the other half.
        var terminal = Assert.Single(
            envelopes,
            e => e.Task == ScanTaskNames.Phd2Correlate
                && e.TotalSteps == Phd2CorrelationEvents.FailedEnvelopeTotalSteps);
        Assert.Contains("failed", terminal.Message, StringComparison.Ordinal);

        // And the obligation is still owed, because nothing completed.
        Assert.True(CorrelationPending);
    }

    [Fact]
    public async Task ACorrelationCancelledPartWay_WritesNeitherRow_AndLeavesTheObligationOwed()
    {
        // Review P2-4. A cancelled pass used to write phd2_correlation_complete at info, over
        // partial figures, beside the run's own scan_cancelled row: "PHD2 correlation filled 3
        // frame guiding values over 2 nights" describing a fraction of the night set the user
        // stopped. Cancellation is not a completion and it is not a failure either, so spec 10.9
        // has the pass write neither row and leave the flag standing.
        WriteLog("PHD2_GuideLog_2025-03-19_213000.txt", DesktopLog);
        SeedLightFrame();

        var coordinator = MakeCoordinator(correlationPending: true);
        using var cts = new CancellationTokenSource();
        coordinator.ProgressChanged += (_, progress) =>
        {
            // The correlation's own opening envelope, which is reported before its first night.
            if (progress.Task == ScanTaskNames.Phd2Correlate)
            {
                cts.Cancel();
            }
        };

        try
        {
            await coordinator.RunAsync(ScanTrigger.Manual, null, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // The run records itself cancelled; either shape is fine here.
        }

        Assert.Empty(EventsOfType("phd2_correlation_complete"));
        Assert.Empty(EventsOfType("phd2_correlation_failed"));
        Assert.True(CorrelationPending);
    }

    // --- spec 7.6, the durable obligation, on the scan path -------------------------------------

    [Fact]
    public async Task ACorrelationThatCompletesInsideAScan_ClearsThePendingFlag()
    {
        // The flag is cleared by whichever pass completes, not only by the out-of-scan re-run: a
        // user who re-maps a rig and then triggers a scan before the re-run gets the lease has had
        // the work done, and leaving the flag set would make every later start pay for a
        // corpus-wide pass that has nothing to do.
        WriteLog("PHD2_GuideLog_2025-03-19_213000.txt", DesktopLog);
        SeedLightFrame();

        var coordinator = MakeCoordinator(correlationPending: true);
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        AssertNothingWasSwallowed();
        Assert.Single(EventsOfType("phd2_correlation_complete"));
        Assert.False(CorrelationPending);
    }

    [Fact]
    public async Task AScanWithTheObligationOwed_VisitsTheNightsNoOtherHalfOfTheUnionReturns()
    {
        // Loss sequence 3 of the phase review, in miniature. A night whose frames all already
        // carry a phd2 value and whose guide log has not changed is returned by NEITHER half of
        // the ordinary night set: the ingest did not touch it, and it needs no fill. So a re-map
        // whose pass never reached that night leaves the old rig's guiding numbers on real frames
        // indefinitely. While the obligation is owed the pass widens to every invalidated night,
        // which is what finishes the job the interrupted pass started.
        //
        // A failure looks like: the user re-maps a rig, quits before the pass finishes, and the
        // nights it never reached keep the wrong telescope's guiding forever.
        var frame = Path.Combine(_root, "light_m31.fits");
        File.WriteAllBytes(frame, MinimalFits("Newt 8", "2025-03-19T21:31:00", 60.0));
        WriteLog("PHD2_GuideLog_2025-03-19.txt", QualifyingLog("TestRig", "2025-03-19 21:30:59"));

        // Scan one, with TestRig mapped to the frame's telescope: the frame fills.
        var coordinator = MakeCoordinator(profileMapJson: """{"TestRig":{"telescope":"Newt 8"}}""");
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);
        using (var context = OpenRead())
        {
            Assert.Equal("phd2", context.Images.Single().GuidingRmsSource);
        }

        // The re-map. TestRig now names a rig no frame on that night uses, so the correct end
        // state is an emptied guiding column. Nothing on disk changes, so on the next scan the
        // guide log is delta-skipped and the frame needs no fill: only the owed obligation can
        // put this night in front of the pass.
        var remapped = MakeCoordinator(
            profileMapJson: """{"TestRig":{"telescope":"RC8"}}""", correlationPending: true);
        await remapped.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        AssertNothingWasSwallowed();
        using var after = OpenRead();
        var image = after.Images.Single();
        Assert.Null(image.GuidingRmsSource);
        Assert.Null(image.GuidingRmsArcsec);
        Assert.False(CorrelationPending);
    }

    // --- review P2-5: the path back after a key-off rescan --------------------------------------

    [Fact]
    public async Task AKeyOffRescanThatBlanksAFilledFrame_RestoresItOnTheFirstScanAfterTheKeyGoesOn()
    {
        // Spec 5.2's ordering guarantee is conditional on general.phd2_scan_enabled, and the
        // Library tab's own checkbox text promises that turning the key off "deletes nothing".
        // That is true of the phd2_* tables and false of what the user is looking at: ScanWriter
        // nulls the four guiding columns of any image row it rewrites, and with the key off
        // nothing restores them. This case is the sentence's real content, which is that the path
        // back exists and costs no re-ingest and no file change.
        //
        // A failure looks like: a user turns the feature off, touches a frame, and a guiding
        // figure they had is gone for good even after they turn the feature back on.
        var frame = Path.Combine(_root, "light_m31.fits");
        File.WriteAllBytes(frame, MinimalFits("Newt 8", "2025-03-19T21:31:00", 60.0));
        WriteLog("PHD2_GuideLog_2025-03-19.txt", QualifyingLog("TestRig", "2025-03-19 21:30:59"));

        var map = """{"TestRig":{"telescope":"Newt 8"}}""";
        await MakeCoordinator(profileMapJson: map)
            .RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        double filledValue;
        using (var context = OpenRead())
        {
            var image = context.Images.Single();
            Assert.Equal("phd2", image.GuidingRmsSource);
            filledValue = image.GuidingRmsArcsec!.Value;
        }

        // The key goes off and the frame's file is touched, so the classifier calls it changed and
        // the writer rewrites its row from metadata that carries no CSV guiding.
        File.SetLastWriteTimeUtc(frame, File.GetLastWriteTimeUtc(frame).AddHours(1));
        await MakeCoordinator(phd2Enabled: false, profileMapJson: map)
            .RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        using (var context = OpenRead())
        {
            var blanked = context.Images.Single();
            Assert.Null(blanked.GuidingRmsSource);
            Assert.Null(blanked.GuidingRmsArcsec);
        }

        // The key goes back on. Nothing on disk changed, so the guide log is delta-skipped and the
        // ingest reports no night at all; the frame is now a LIGHT frame with no guiding RMS, so
        // the fill half of the union returns its night and the value comes back to the same
        // number from the same samples.
        await MakeCoordinator(profileMapJson: map)
            .RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        AssertNothingWasSwallowed();
        using var after = OpenRead();
        var restored = after.Images.Single();
        Assert.Equal("phd2", restored.GuidingRmsSource);
        Assert.Equal(filledValue, restored.GuidingRmsArcsec!.Value, 6);
    }

    // --- review 4b: the forced-prune percent, both sides, under a comma-decimal culture ---------

    [Fact]
    public async Task AForcedPruneUnderACommaDecimalCulture_FormatsBothPercentsInvariantly()
    {
        // Both orphan messages, the frame side's and the guide-log side's, interpolated a double
        // straight into the sentence, so on a German or French machine the Activity feed read
        // "66,7%" beside a details document whose JSON double always reads 66.7. One scan raises
        // both, so one case covers both sides, which is what the Task 4b review asked for ("fix
        // both sides or neither").
        var frame = Path.Combine(_root, "light_m31.fits");
        File.WriteAllBytes(frame, MinimalFits("Newt 8", "2025-03-19T21:31:00", 60.0));
        WriteLog("PHD2_GuideLog_2025-03-19.txt", QualifyingLog("TestRig", "2025-03-19 21:30:59"));

        // Two of three catalogued rows missing on each side, which is 66.7 percent, past the 50
        // percent safety limit, with the per-run override on.
        SeedMissingImage("gone1.fits");
        SeedMissingImage("gone2.fits");
        SeedMissingGuideLog("gone1.txt");
        SeedMissingGuideLog("gone2.txt");

        var previous = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            await MakeCoordinator(profileMapJson: """{"TestRig":{"telescope":"Newt 8"}}""")
                .RunAsync(
                    ScanTrigger.Manual, null, CancellationToken.None,
                    new ScanRunOptions(IncludeCalibration: false, ForceOrphanCleanup: true));
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = previous;
        }

        foreach (var eventType in new[] { "orphan_prune_forced", "phd2_orphan_prune_forced" })
        {
            var row = Assert.Single(EventsOfType(eventType));
            Assert.Contains("66.7%", row.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("66,7", row.Message, StringComparison.Ordinal);
        }
    }

    // --- the coordinator's silence ruling -------------------------------------------------------

    [Fact]
    public async Task AScanOfALibraryWithNoGuideLogs_WritesNoPhd2RowAtAll()
    {
        // COORDINATOR RULING, from fixer-p15a-e's escalation: a library with nothing to say about
        // guide logs stays silent in the feed. `phd2_scan_enabled` ships ON, so before the ruling
        // every scan of every library that has never seen PHD2 wrote two rows, "No PHD2 guide logs
        // found" and "PHD2 correlation filled 0 frame guiding values over 0 nights", at every scan
        // interval, for ever. `EndToEndScanTests`' seven-row assertion is the guard that caught it.
        //
        // A failure looks like a user who does not guide finding half their Activity feed is about
        // guiding, which is how a feed stops being read.
        var coordinator = MakeCoordinator();

        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        AssertNothingWasSwallowed();
        using var context = OpenRead();
        Assert.Empty(context.ActivityEvents
            .ToList()
            .Where(e => e.EventType.StartsWith("phd2_", StringComparison.Ordinal))
            .ToList());
    }

    [Fact]
    public async Task AScanThatFindsOneGuideLog_WritesPassCompleteOnceAndTheCorrelationRow()
    {
        // The other side of the ruling: silence is for a library with nothing to say, never for
        // one with guide logs in it. A failure looks like a PHD2 user losing the one row that
        // tells them the pass ran over their corpus.
        WriteLog("PHD2_GuideLog_2025-03-19_213000.txt", DesktopLog);
        SeedLightFrame();

        await MakeCoordinator().RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        AssertNothingWasSwallowed();
        Assert.Single(EventsOfType("phd2_pass_complete"));
        Assert.Single(EventsOfType("phd2_correlation_complete"));
    }

    // --- fix-wave review P1-1: a save that lands mid-pass is not discharged by that pass ---------

    [Fact]
    public async Task ASaveThatLandsWhileAScansCorrelationRuns_LeavesTheObligationOwed()
    {
        // Fix-wave review P1-1. The clear used to be unconditional, so a pass that began before a
        // save and finished after it discharged that save's obligation without ever having seen
        // it. For a telescope re-map nothing else recovers it: the re-derive moves no row, so no
        // later scan widens to InvalidatedNights, and the nights hold filled frames so the
        // unfilled query skips them. That is the phase review's loss sequence 3 re-opened inside
        // the mechanism built to close it.
        //
        // A failure looks like: the user re-maps a rig, saves again while the pass is running, and
        // the second change is lost the moment they quit.
        WriteLog("PHD2_GuideLog_2025-03-19_213000.txt", DesktopLog);
        SeedLightFrame();

        var coordinator = MakeCoordinator(correlationPending: true);

        // The save lands after the pipeline has taken its settings snapshot (its very first line)
        // and before the correlation's clear. Discovery does no database write of its own, so this
        // cannot contend with the pass's transactions.
        var saved = false;
        coordinator.ProgressChanged += (_, progress) =>
        {
            if (progress.Task == ScanTaskNames.Discovery && !saved)
            {
                saved = true;
                _settings.MutateGeneral(g => g with { ObserverLatitude = 51.4779 });
            }
        };

        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        AssertNothingWasSwallowed();
        Assert.True(saved, "the case proves nothing unless the save really landed mid-scan");
        Assert.Single(EventsOfType("phd2_correlation_complete"));

        // The pass completed and wrote its row, and the obligation the newer save recorded is
        // still standing, because this pass never saw that save.
        Assert.True(CorrelationPending);
    }

    /// <summary>An image row under the scan root whose file does not exist, so the walk reports it
    /// missing and the orphan guards count it.</summary>
    private void SeedMissingImage(string name)
    {
        using var context = new GalactiLogContext(
            GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true));
        var id = Guid.NewGuid();
        context.Images.Add(new Image
        {
            Id = id,
            FilePath = Path.Combine(_root, name),
            FileName = name,
            ImageType = "LIGHT",
            SessionDate = Night,
        });
        context.SaveChanges();
    }

    /// <summary>The same on the guide-log side: a <c>phd2_logs</c> row whose file is gone.
    /// </summary>
    private void SeedMissingGuideLog(string name)
    {
        using var context = new GalactiLogContext(
            GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true));
        context.Phd2Logs.Add(new Phd2Log
        {
            Id = Guid.NewGuid(),
            FilePath = Path.Combine(_root, name),
            FileSize = 10,
            FileMtime = 0,
            ParseStatus = "ok",
            ParsedAt = DateTime.UtcNow,
        });
        context.SaveChanges();
    }
}
