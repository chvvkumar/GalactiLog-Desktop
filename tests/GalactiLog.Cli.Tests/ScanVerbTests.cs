using System.Text.Json;
using GalactiLog.App;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Settings;
using GalactiLog.Core.Tests.Fixtures;
using GalactiLog.Data;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using static GalactiLog.Cli.Tests.CliHostFixture;

namespace GalactiLog.Cli.Tests;

// Task 8: the real `scan` verb end to end through AppHost.Build + CliDispatcher (spec 15).
// Exit codes, the --json scan_runs payload, --quiet's stdout contract, the progress-line
// format, and cooperative cancellation.
//
// The real Console.CancelKeyPress wiring cannot be raised from inside a test process, so the
// cancellation case drives coordinator.Cancel() from a progress subscriber -- the same call
// the Ctrl+C handler makes, reaching the same code path. The handler's own two extra lines
// (e.Cancel = true, so the process is not killed outright) are covered by the phase's manual
// verification run, not here.
//
// FILE SAFETY: every file this class creates lives in a temp folder it created and deletes.
// Nothing in src/** writes, moves or deletes any file.
public sealed class ScanVerbTests
{
    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    // NoMatchHandler and the two Run(...) helpers below moved to CliHostFixture.cs when
    // ScanReferenceThumbnailTests became the second file needing them (Phase 10 Task 8, ruling
    // Q23: 35 duplicated lines against a threshold of about twenty). Nothing else in this file
    // changed, and no call site did: `using static CliHostFixture` keeps every Run(...) call and
    // `new NoMatchHandler()` spelled exactly as they were.
    private sealed class HostFixture : IDisposable
    {
        public string Root { get; }
        public string Frames { get; }
        public IHost Host { get; }

        public HostFixture(int frameCount = 4, bool configureScanRoots = true)
        {
            Root = Directory.CreateTempSubdirectory("galactilog-scan-verb-").FullName;
            Frames = Directory.CreateTempSubdirectory("galactilog-scan-frames-").FullName;
            for (var i = 0; i < frameCount; i++)
            {
                File.WriteAllBytes(Path.Combine(Frames, $"light_{i:D3}.fits"), LightFrame());
            }

            Host = AppHost.Build(Root, cliMode: true, httpHandlerOverride: new NoMatchHandler());

            if (configureScanRoots)
            {
                Host.Services.GetRequiredService<SettingsStore>().SaveGeneral(new GeneralSettings
                {
                    ScanRoots = [Frames],
                    ScanFilters = ScanFilterConfig.Empty,
                });
            }
        }

        public ScanCoordinator Coordinator => Host.Services.GetRequiredService<ScanCoordinator>();

        public string ConnectionString => DatabasePaths.BuildConnectionString(
            Path.Combine(Path.GetFullPath(Root), DatabasePaths.DatabaseFileName));

        public void Dispose()
        {
            Host.Dispose();
            Serilog.Log.CloseAndFlush();
            SqliteConnection.ClearAllPools();
            foreach (var directory in new[] { Root, Frames })
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }
    }

    // A minimal valid LIGHT frame: NAXIS = 0, so one 2880-byte block is the whole file.
    private static byte[] LightFrame() => new FitsBuilder()
        .Card("SIMPLE", true)
        .Card("BITPIX", (long)16)
        .Card("NAXIS", (long)0)
        .Card("IMAGETYP", "LIGHT")
        .Card("OBJECT", "M 31")
        .Card("DATE-OBS", "2025-03-15T02:00:00")
        .Card("EXPTIME", 300.0)
        .EndCard()
        .Build().ToArray();

    // ---- usage and missing-path exit codes -------------------------------------------

    // Both of these return before RunScan resolves any service, so a provider that resolves
    // nothing is enough -- and that is the point: a rejected request must not open the
    // database or create a scan_runs row.
    [Fact]
    public void Scan_RelativePathArgument_ExitsTwoWithUsageOnStderr()
    {
        var (exitCode, stdout, stderr) = Run(() => new NullServiceProvider(), "scan", "some\\relative\\path");

        Assert.Equal(2, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("scan root must be an absolute directory path", stderr);
        Assert.Contains("Usage: galactilog", stderr);
    }

    [Fact]
    public void Scan_PathThatDoesNotExist_ExitsThree()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"galactilog-missing-{Guid.NewGuid():N}");

        var (exitCode, stdout, stderr) = Run(() => new NullServiceProvider(), "scan", missing);

        Assert.Equal(3, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("scan root not found", stderr);
    }

    [Fact]
    public void Scan_PathThatIsAFileNotADirectory_ExitsThree()
    {
        using var fixture = new HostFixture(frameCount: 1);
        var file = Directory.GetFiles(fixture.Frames)[0];

        var (exitCode, _, stderr) = Run(() => new NullServiceProvider(), "scan", file);

        Assert.Equal(3, exitCode);
        Assert.Contains("scan root not found", stderr);
    }

    // ---- a real run -------------------------------------------------------------------

    [Fact]
    public void Scan_ConfiguredRoots_ExitsZero_AndIngestsEveryFrame()
    {
        using var fixture = new HostFixture(frameCount: 4);

        var (exitCode, stdout, _) = Run(fixture.Host, "scan");

        Assert.Equal(0, exitCode);
        Assert.Contains("Scan complete", stdout);

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(fixture.ConnectionString));
        Assert.Equal(4, context.Images.Count());
    }

    [Fact]
    public void Scan_PathArgument_OverridesConfiguredRoots()
    {
        // The host is built with no scan_roots at all, so the only way anything is found is
        // the positional argument being used as the run's root override.
        using var fixture = new HostFixture(frameCount: 3, configureScanRoots: false);

        var (exitCode, _, _) = Run(fixture.Host, "scan", fixture.Frames);

        Assert.Equal(0, exitCode);

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(fixture.ConnectionString));
        Assert.Equal(3, context.Images.Count());
    }

    [Fact]
    public void Scan_Json_EmitsTheScanRunsRecordAsOneObject()
    {
        using var fixture = new HostFixture(frameCount: 4);

        var (exitCode, stdout, _) = Run(fixture.Host, "scan", "--json");

        Assert.Equal(0, exitCode);

        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.Equal("complete", root.GetProperty("state").GetString());
        Assert.Equal("cli", root.GetProperty("trigger").GetString());
        Assert.Equal(4, root.GetProperty("discovered").GetInt32());
        Assert.Equal(4, root.GetProperty("new_files").GetInt32());
        Assert.Equal(0, root.GetProperty("changed_files").GetInt32());
        Assert.Equal(4, root.GetProperty("completed").GetInt32());
        Assert.Equal(0, root.GetProperty("failed").GetInt32());
        Assert.Equal(0, root.GetProperty("skipped_calibration").GetInt32());
        Assert.Equal(0, root.GetProperty("removed").GetInt32());

        // The guide-log pass's six figures. The fixture library holds no guide log, so all six
        // read zero; what this pins is that they are PRESENT, because a reader cannot tell "no
        // guide logs" from "this CLI predates the fields" when they are absent.
        Assert.Equal(0, root.GetProperty("phd2_found").GetInt32());
        Assert.Equal(0, root.GetProperty("phd2_ingested").GetInt32());
        Assert.Equal(0, root.GetProperty("phd2_failed").GetInt32());
        Assert.Equal(0, root.GetProperty("phd2_skipped_unchanged").GetInt32());
        Assert.Equal(0, root.GetProperty("phd2_empty").GetInt32());
        Assert.Equal(0, root.GetProperty("phd2_removed").GetInt32());

        // The payload is the scan_runs row column for column (spec 5.13) plus the three
        // pass-result fields that have no column (spec 15, carried item 65), asserted as an exact
        // key set so a column added to the row and forgotten here is a failure rather than a
        // silent omission.
        Assert.Equal(
            new[]
            {
                "changed_files", "completed", "discovered", "error_text", "failed", "finished_at",
                "id", "new_files", "phd2_empty", "phd2_failed", "phd2_found", "phd2_ingested",
                "phd2_removed", "phd2_skipped_unchanged", "removed", "skipped_calibration",
                "started_at", "state", "trigger",
            },
            root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());

        // It is the persisted row, not a reassembly of the in-memory outcome.
        var id = root.GetProperty("id").GetInt32();
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(fixture.ConnectionString));
        var run = context.ScanRuns.Single(r => r.Id == id);
        Assert.Equal("complete", run.State);
        Assert.NotNull(run.FinishedAt);

        // --json means exactly one object on stdout: no progress lines mixed in.
        Assert.DoesNotContain(ScanTaskNames.Discovery, stdout);
    }

    // ---- the guide-log pass's own tally (carried item 65) -----------------------------

    // The CSV header is compared byte for byte by the parser (spec 7.6), so it is written out in
    // full rather than assembled.
    private const string GuideCsvHeader =
        "Frame,Time,mount,dx,dy,RARawDistance,DECRawDistance,RAGuideDistance,DECGuideDistance," +
        "RADuration,RADirection,DECDuration,DECDirection,XStep,YStep,StarMass,SNR,ErrorCode";

    private const string GuideLog = $"""
        PHD2 version 2.6.13 [Windows], Log version 2.5. Log enabled at 2025-03-19 21:30:00
        Guiding Begins at 2025-03-19 21:31:00
        Pixel scale = 1.50 arc-sec/px, Binning = 1, Focal length = 500 mm
        Camera = Test Camera
        Exposure = 500 ms
        {GuideCsvHeader}
        1,0.500,"Mount",0.10,0.20,0.400,0.300,0.100,0.100,10,W,12,N,0,0,12000.0,25.0,0
        Guiding Ends at 2025-03-19 21:31:02
        Log closed at 2025-03-19 21:32:00

        """;

    // Discovered by name and opened, with no marker line anywhere, so the parser yields no run
    // and throws nothing: spec 5.15's `empty`, which is neither ingested nor failed.
    private const string EmptyGuideLog = "These are my observing notes.\nNothing to see here.\n";

    // Phase 15B fixer pass, fixer list item 6. AlreadyRunning no longer writes the six guide-log
    // zeros out by hand; the record's own defaults supply them, and this is what proves it. The
    // figures matter here rather than in Data because this verb is what prints all six: a
    // "pending" invocation reporting anything but zero would tell a reader a guide-log pass had
    // run when no scan_runs row was even created for it.
    [Fact]
    public void ScanRunOutcome_AlreadyRunning_CarriesSixZeroGuideLogCounters()
    {
        var outcome = ScanRunOutcome.AlreadyRunning;

        Assert.Null(outcome.RunId);
        Assert.Equal("pending", outcome.State);
        Assert.Equal(
            [0, 0, 0, 0, 0, 0],
            new[]
            {
                outcome.Phd2Found, outcome.Phd2Ingested, outcome.Phd2Failed,
                outcome.Phd2SkippedUnchanged, outcome.Phd2Empty, outcome.Phd2Removed,
            });
    }

    // Carried item 65. A library with empty guide logs used to read `found 2, ingested 1,
    // failed 0` and leave the reader one log short with no visible reason. The three fields that
    // close it come from the in-memory pass result rather than from the scan_runs row, which is
    // the whole point: no column.
    [Fact]
    public void Scan_Json_WithAnEmptyGuideLog_ReconcilesTheGuideLogCounters()
    {
        using var fixture = new HostFixture(frameCount: 1);
        File.WriteAllText(
            Path.Combine(fixture.Frames, "PHD2_GuideLog_2025-03-19_213000.txt"), GuideLog);
        File.WriteAllText(
            Path.Combine(fixture.Frames, "PHD2_GuideLog_2025-03-20_213000.txt"), EmptyGuideLog);

        var (exitCode, stdout, _) = Run(fixture.Host, "scan", "--json");

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        int Count(string name) => root.GetProperty(name).GetInt32();

        Assert.Equal(2, Count("phd2_found"));
        Assert.Equal(1, Count("phd2_ingested"));
        Assert.Equal(1, Count("phd2_empty"));
        Assert.Equal(0, Count("phd2_failed"));
        Assert.Equal(0, Count("phd2_skipped_unchanged"));
        Assert.Equal(0, Count("phd2_removed"));

        Assert.Equal(
            Count("phd2_found"),
            Count("phd2_ingested") + Count("phd2_skipped_unchanged")
                + Count("phd2_empty") + Count("phd2_failed"));

        // The three stored counters still come from the row, and the row still has no column for
        // the other three.
        var id = root.GetProperty("id").GetInt32();
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(fixture.ConnectionString));
        var run = context.ScanRuns.Single(r => r.Id == id);
        Assert.Equal(2, run.Phd2Found);
        Assert.Equal(1, run.Phd2Ingested);
        Assert.Equal(0, run.Phd2Failed);
    }

    // Spec 15: --quiet suppresses PROGRESS output. The run's result is not progress, so the
    // summary line still prints -- exactly one line, with no progress lines around it.
    [Fact]
    public void Scan_Quiet_SuppressesProgressLinesButStillPrintsTheSummary()
    {
        using var fixture = new HostFixture(frameCount: 4);

        var (exitCode, stdout, _) = Run(fixture.Host, "scan", "--quiet");

        Assert.Equal(0, exitCode);
        var line = Assert.Single(stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith("Scan complete:", line.TrimEnd('\r'));
        Assert.DoesNotContain(ScanTaskNames.Discovery, stdout);
        Assert.DoesNotContain(ScanTaskNames.Ingest, stdout);

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(fixture.ConnectionString));
        Assert.Equal(4, context.Images.Count());
    }

    // --quiet must not swallow the --json payload either: it is the verb's result.
    [Fact]
    public void Scan_QuietWithJson_StillEmitsTheJsonObject()
    {
        using var fixture = new HostFixture(frameCount: 2);

        var (exitCode, stdout, _) = Run(fixture.Host, "scan", "--json", "--quiet");

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal("complete", document.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public void Scan_Default_EmitsProgressLinesInTaskStepTotalMessageForm()
    {
        using var fixture = new HostFixture(frameCount: 4);

        var (exitCode, stdout, _) = Run(fixture.Host, "scan");

        Assert.Equal(0, exitCode);
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .ToList();

        // Every progress line is "<task> <step>/<total> <message>", and every task name comes
        // from the spec 10.4 vocabulary.
        var progressLines = lines.Where(l => !l.StartsWith("Scan ", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(progressLines);
        Assert.All(progressLines, line =>
        {
            var parts = line.Split(' ', 3);
            Assert.Contains(parts[0], ScanTaskNames.All);
            Assert.Matches(@"^\d+/\d+$", parts[1]);
            Assert.NotEmpty(parts[2]);
        });
        Assert.Contains(lines, l => l.StartsWith($"{ScanTaskNames.Ingest} ", StringComparison.Ordinal));
    }

    // Spec 15: "found nothing new" is exit 0, not an error.
    [Fact]
    public void Scan_SecondRunFindsNothingNew_StillExitsZero()
    {
        using var fixture = new HostFixture(frameCount: 4);

        Assert.Equal(0, Run(fixture.Host, "scan", "--quiet").ExitCode);

        var (exitCode, stdout, _) = Run(fixture.Host, "scan", "--json");

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal(0, document.RootElement.GetProperty("new_files").GetInt32());
        Assert.Equal(4, document.RootElement.GetProperty("discovered").GetInt32());
        Assert.Equal("complete", document.RootElement.GetProperty("state").GetString());
    }

    // ---- failure mapping (spec 15's exit-code table) ----------------------------------

    // A scan filter configuration that no longer validates against its own scan roots is a
    // rejected request, not a failed run: exit 2, the offending entry named, and no
    // scan_runs row, because validation happens before the row is created.
    [Fact]
    public void Scan_ScanFilterConfigurationFailsValidation_ExitsTwo_AndWritesNoScanRun()
    {
        using var fixture = new HostFixture(frameCount: 2);
        var strayPath = Path.Combine(Path.GetTempPath(), $"galactilog-stray-{Guid.NewGuid():N}");

        // Written straight to the settings row, bypassing SettingsStore.SaveGeneral: the
        // store rejects this configuration outright, so the only way it reaches a scan is
        // drift -- a scan root removed by an older version, or a hand-edited database. That
        // is exactly the case the scan verb has to survive, and it is why RunScan cannot
        // rely on the settings writer having already validated.
        var repository = fixture.Host.Services.GetRequiredService<SettingsRepository>();
        var row = repository.Load();
        row.General = JsonSerializer.Serialize(new GeneralSettings
        {
            ScanRoots = [fixture.Frames],
            ScanFilters = new ScanFilterConfig { IncludePaths = [strayPath] },
        });
        repository.Save(row);

        var (exitCode, stdout, stderr) = Run(fixture.Host, "scan");

        Assert.Equal(2, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("scan_filters.include_paths", stderr);
        Assert.Contains(strayPath, stderr);

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(fixture.ConnectionString));
        Assert.Empty(context.ScanRuns);
    }

    // A database write failing mid-run is exit 4 (the same code an unopenable database gets),
    // not 70. Injected by dropping the table the run's first activity write targets -- the
    // same injector ScanCoordinatorTests uses, through a real code path with no test seam in
    // production code.
    [Fact]
    public void Scan_DatabaseWriteFailsMidRun_ExitsFour_AndRecordsTheRunFailed()
    {
        using var fixture = new HostFixture(frameCount: 2);
        using (var injector = new GalactiLogContext(GalactiLogContextOptions.Create(fixture.ConnectionString, tracking: true)))
        {
            injector.Database.ExecuteSqlRaw("DROP TABLE activity_events");
        }

        var (exitCode, stdout, stderr) = Run(fixture.Host, "scan");

        Assert.Equal(4, exitCode);
        Assert.Empty(stdout);
        Assert.NotEmpty(stderr);

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(fixture.ConnectionString));
        var run = Assert.Single(context.ScanRuns.ToList());
        Assert.Equal("failed", run.State);
        Assert.False(string.IsNullOrEmpty(run.ErrorText));
    }

    // ---- one bad frame is one failed file, never a failed run (spec 10.3 step 3) -------

    // DATE-OBS at DateTime.MinValue with SITELONG supplying a longitude: spec 8.2's
    // imaging-night shift underflows and ScanRecordParser.Parse throws. Nothing here is
    // test-only, and the longitude rides in the header rather than in settings, so no real site
    // is named and general.use_imaging_night keeps its own default of true.
    //
    // Before the choke point in Parse, the throw reached CliDispatcher past every clause of spec
    // 15's exit-code table and the verb exited 70, "unhandled internal error", over one frame the
    // user did not write and cannot repair.
    private static byte[] ThrowingFrame() => new FitsBuilder()
        .Card("SIMPLE", true)
        .Card("BITPIX", (long)16)
        .Card("NAXIS", (long)0)
        .Card("IMAGETYP", "LIGHT")
        .Card("SITELONG", 0.0)
        .Card("DATE-OBS", "0001-01-01T00:00:00")
        .Card("EXPTIME", 300.0)
        .EndCard()
        .Build().ToArray();

    [Fact]
    public void Scan_OneFrameWhoseParseThrows_ExitsZero_AndTheSummaryReportsOneFailedFile()
    {
        using var fixture = new HostFixture(frameCount: 3);
        File.WriteAllBytes(Path.Combine(fixture.Frames, "throwing.fits"), ThrowingFrame());

        var (exitCode, stdout, _) = Run(fixture.Host, "scan", "--quiet");

        Assert.Equal(0, exitCode);
        var line = Assert.Single(stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('\r');
        Assert.StartsWith("Scan complete:", line);
        Assert.Contains("3 completed", line);
        Assert.Contains("1 failed", line);

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(fixture.ConnectionString));
        var run = Assert.Single(context.ScanRuns.ToList());
        Assert.Equal("complete", run.State);
        Assert.Equal(1, run.Failed);
        Assert.Null(run.ErrorText);
        Assert.Equal(3, context.Images.Count());
    }

    [Fact]
    public void Scan_Json_OneFrameWhoseParseThrows_CarriesFailedOne()
    {
        using var fixture = new HostFixture(frameCount: 3);
        File.WriteAllBytes(Path.Combine(fixture.Frames, "throwing.fits"), ThrowingFrame());

        var (exitCode, stdout, _) = Run(fixture.Host, "scan", "--json");

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.Equal("complete", root.GetProperty("state").GetString());
        Assert.Equal(4, root.GetProperty("discovered").GetInt32());
        Assert.Equal(4, root.GetProperty("new_files").GetInt32());
        Assert.Equal(3, root.GetProperty("completed").GetInt32());
        Assert.Equal(1, root.GetProperty("failed").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("error_text").ValueKind);
    }

    // ---- cancellation -----------------------------------------------------------------

    [Fact]
    public void Scan_CancelledDuringIngest_ExitsFive_AndRecordsTheRunCancelled()
    {
        using var fixture = new HostFixture(frameCount: 8);
        var coordinator = fixture.Coordinator;

        // The same call the Ctrl+C handler makes, on the first ingest event -- which the
        // coordinator raises unthrottled before the first file is parsed, so this is
        // deterministic rather than a race against the pipeline.
        void CancelOnFirstIngest(object? sender, ScanProgress p)
        {
            if (p.Task == ScanTaskNames.Ingest)
            {
                coordinator.Cancel();
            }
        }

        coordinator.ProgressChanged += CancelOnFirstIngest;
        try
        {
            var (exitCode, stdout, _) = Run(fixture.Host, "scan", "--json");

            Assert.Equal(5, exitCode);
            using var document = JsonDocument.Parse(stdout);
            Assert.Equal("cancelled", document.RootElement.GetProperty("state").GetString());
        }
        finally
        {
            coordinator.ProgressChanged -= CancelOnFirstIngest;
        }

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(fixture.ConnectionString));
        var run = Assert.Single(context.ScanRuns.ToList());
        Assert.Equal("cancelled", run.State);

        // A cancelled run never reaches orphan pruning, so nothing was removed.
        Assert.Equal(0, run.Removed);
    }
}
