using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GalactiLog.App;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.Core.Phd2;
using GalactiLog.Core.Settings;
using GalactiLog.Core.Wbpp;
using GalactiLog.Data;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace GalactiLog.App.Tests.Services;

/// <summary>
/// Task 6b review P2-3 and P2-1: the two things only a real host can prove. Everything else about
/// the re-run is pinned against the runner's own seams in
/// <see cref="Phd2CorrelationRunnerTests"/>; these two cases exist because those seams are only as
/// good as what <c>AppHost</c> actually hands them.
/// </summary>
/// <remarks>
/// <para>
/// The census case in <c>JobRegistryCensusTest</c> hand-writes an equivalent subscription, so
/// deleting <c>AppHost</c>'s <c>Phd2GuidingInputsChanged +=</c> line leaves it green. These cases
/// go through <see cref="AppHost.Build"/> itself, so the subscription, the runner registration,
/// the lease delegate and the settings delegate are all on the path under test.
/// </para>
/// <para>
/// FILE SAFETY: the only directory either case touches is a temp directory it created and
/// deletes. Nothing under the user's profile is read or written, and <c>GALACTILOG_APPDATA</c> is
/// never consulted because the root is passed to <see cref="AppHost.Build"/> directly.
/// </para>
/// </remarks>
public sealed class Phd2ReRunHostWiringTests
{
    /// <summary>
    /// Review P2-3. A real settings save through the host's own <c>SettingsStore</c> has to reach
    /// the registered runner and put spec 10.9's row in the host's own database. A failure looks
    /// like the one subscription line being deleted, moved out of a block the GUI resolves, or
    /// pointed at something other than the runner, with every test in the suite still green and
    /// the feature dead in the shipped application.
    /// </summary>
    /// <remarks>
    /// The assertion is the activity row and not <c>JobRegistry.Recent</c>, deliberately: the
    /// host's registry posts through <c>UiPost.Default</c>, so without a dispatcher running its
    /// collections never fill. The row needs no dispatcher and proves more of the chain.
    /// </remarks>
    [Fact]
    public async Task ASettingsSaveOnTheRealHost_RunsTheCorrelationAndWritesItsActivityRow()
    {
        using var host = new Host();

        // Resolving ScanStatusService is what runs the factory that makes the subscription, and
        // it is what GUI startup does. Nothing else in this case touches it.
        host.Services.GetRequiredService<ScanStatusService>();

        var store = host.Services.GetRequiredService<SettingsStore>();
        store.MutateGeneral(general => general with
        {
            Phd2ProfileMap = Phd2Profiles.ToJson(new Dictionary<string, Phd2ProfileEntry>
            {
                ["Rig A"] = new() { Telescope = "Askar 120" },
            }),
        });

        await host.Services.GetRequiredService<Phd2CorrelationRunner>().InFlight;

        var row = Assert.Single(host.Read(context => context.ActivityEvents
            .Where(entry => entry.EventType == "phd2_correlation_complete")
            .ToList()));
        Assert.Null(row.ParentId);
        Assert.Contains("\"trigger\":\"settings_change\"", row.Details);
    }

    /// <summary>
    /// Review P2-1. The panel's constructor loads on a pool thread and calls back into the tab
    /// that has not been constructed yet, so without the reload that follows the tab's assignment
    /// the telescope picker can publish empty. A failure looks like a user who has grouped their
    /// telescopes opening the Equipment tab and finding that no profile can be mapped to any of
    /// them, with one log warning and nothing on screen.
    /// </summary>
    [AvaloniaFact]
    public void ThePanelOnTheRealHost_OffersTheCanonicalTelescopeNamesOfTheEditorAboveIt()
    {
        using var host = new Host();

        // One canonical telescope group, and one guiding session so the panel has a row whose
        // picker can be read. Both land before the tab is resolved, which is when the factory
        // under test runs.
        host.Services.GetRequiredService<SettingsStore>().SaveEquipment(new EquipmentSettings
        {
            Telescopes = { ["Askar FMA180"] = new EquipmentItemSettings() },
        });
        host.Seed(context =>
        {
            var log = new Phd2Log
            {
                Id = Guid.NewGuid(),
                FilePath = @"C:\logs\wiring.txt",
                ParseStatus = "ok",
                ParsedAt = DateTime.UtcNow,
            };
            context.Phd2Logs.Add(log);
            context.Phd2Sessions.Add(new Phd2Session
            {
                Id = Guid.NewGuid(),
                LogId = log.Id,
                RunIndex = 0,
                SectionIndex = 0,
                StartedAtLocal = new DateTime(2025, 3, 19, 21, 31, 0, DateTimeKind.Unspecified),
                StartedAtUtc = new DateTime(2025, 3, 20, 1, 31, 0, DateTimeKind.Utc),
                DurationS = 60,
                EquipmentProfile = "Rig A",
                GuideCamera = "ZWO ASI120MM Mini",
                FocalLengthMm = 400,
                PixelScaleArcsec = 1.5,
            });
        });

        var tab = host.Services.GetRequiredService<EquipmentTabViewModel>();
        var panel = tab.Phd2Profiles;
        Assert.NotNull(panel);

        // Both the tab and the panel read on the pool and publish through the dispatcher, and the
        // panel's picker cannot be right until the tab's own publish has put its groups on the
        // editor. Drain both until the picker settles rather than until the first publish lands:
        // a loop that stopped at the first row would pass on the racing first load's result.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (panel.Rows.Count > 0
                && panel.Rows[0].TelescopeOptions.Any(option => option.Value.Length > 0))
            {
                break;
            }

            Thread.Sleep(25);
        }

        var row = Assert.Single(panel.Rows);
        Assert.Contains("Askar FMA180", row.TelescopeOptions.Select(option => option.Value));
    }

    /// <summary>
    /// Phase 15B real-data finding 1, the blocker, at the one place it lived: the wiring. The
    /// picker's source was the grouping editor's canonical names alone, and a library whose reader
    /// has never created a telescope alias group has none.
    /// </summary>
    /// <remarks>
    /// A failure looks like the user's own library: two telescopes with hundreds of frames each,
    /// both listed under "Ungrouped (2)" three inches above a picker offering nothing but "Not
    /// mapped", no UI path out of it because "Group Selected" is disabled at one selected item, and
    /// therefore every guiding surface of this phase empty for ever. The case seeds NO equipment
    /// document at all, so a delegate narrowed back to <c>TelescopesEditor.Groups</c> fails it.
    /// </remarks>
    [AvaloniaFact]
    public void ThePanelOnTheRealHost_OffersADiscoveredTelescopeThatBelongsToNoAliasGroup()
    {
        using var host = new Host();

        // One frame carrying a telescope name and one guiding session, and no equipment document
        // whatever: the state of a fresh real library.
        host.Seed(context =>
        {
            context.Images.Add(NewLight(new DateOnly(2025, 3, 19), filled: false));

            var log = new Phd2Log
            {
                Id = Guid.NewGuid(),
                FilePath = @"C:\logs\ungrouped.txt",
                ParseStatus = "ok",
                ParsedAt = DateTime.UtcNow,
            };
            context.Phd2Logs.Add(log);
            context.Phd2Sessions.Add(new Phd2Session
            {
                Id = Guid.NewGuid(),
                LogId = log.Id,
                RunIndex = 0,
                SectionIndex = 0,
                StartedAtLocal = new DateTime(2025, 3, 19, 21, 31, 0, DateTimeKind.Unspecified),
                StartedAtUtc = new DateTime(2025, 3, 20, 1, 31, 0, DateTimeKind.Utc),
                DurationS = 60,
                EquipmentProfile = "Rig A",
            });
        });

        var tab = host.Services.GetRequiredService<EquipmentTabViewModel>();
        var panel = tab.Phd2Profiles;
        Assert.NotNull(panel);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (panel.Rows.Count > 0
                && panel.Rows[0].TelescopeOptions.Any(option => option.Value.Length > 0))
            {
                break;
            }

            Thread.Sleep(25);
        }

        // "Askar 120" is the telescope NewLight writes on the frame, and no group names it.
        Assert.Empty(tab.TelescopesEditor.Groups);
        var row = Assert.Single(panel.Rows);
        Assert.Contains("Askar 120", row.TelescopeOptions.Select(option => option.Value));
    }

    /// <summary>
    /// Spec 12.11 item 11a and spec 7.6's "The obligation survives a crash". A re-run that a
    /// settings save queued and that never finished leaves
    /// <c>general.phd2_correlation_pending</c> true, and GUI startup is what picks it up.
    /// </summary>
    /// <remarks>
    /// A failure looks like the phase review's loss sequence 3: the user re-maps a rig, the
    /// application closes before the pass has walked the whole guide-log history, and the nights
    /// it never reached keep the old rig's guiding numbers on real frames indefinitely, because
    /// nothing else ever puts those nights in front of the pass. The other failure this pins is
    /// the over-correction: exactly ONE pass, and only when the flag is set, because
    /// <c>InvalidatedNights</c> on a real corpus is the whole guide-log history and a pass at
    /// every start would make every launch pay for it.
    /// </remarks>
    [Fact]
    public async Task AStartWithTheObligationOwed_QueuesExactlyOneReRun_AndClearsIt()
    {
        using var host = new Host();

        // The obligation, as a save that moved a guiding input and was never honoured leaves it.
        var store = host.Services.GetRequiredService<SettingsStore>();
        store.MutateGeneral(general => general with { Phd2CorrelationPending = true });

        // GUI startup, which is what resolving ScanStatusService is.
        host.Services.GetRequiredService<ScanStatusService>();
        await host.Services.GetRequiredService<Phd2CorrelationRunner>().InFlight;

        Assert.Single(host.Read(context => context.ActivityEvents
            .Where(entry => entry.EventType == "phd2_correlation_complete")
            .ToList()));

        // Discharged, so the next start pays nothing.
        Assert.False(store.GetGeneral().Phd2CorrelationPending);
    }

    /// <summary>
    /// The other half: a start with nothing owed runs no pass at all.
    /// </summary>
    [Fact]
    public async Task AStartWithNothingOwed_QueuesNoReRun()
    {
        using var host = new Host();

        host.Services.GetRequiredService<ScanStatusService>();
        await host.Services.GetRequiredService<Phd2CorrelationRunner>().InFlight;

        Assert.Empty(host.Read(context => context.ActivityEvents
            .Where(entry => entry.EventType == "phd2_correlation_complete")
            .ToList()));
    }

    /// <summary>
    /// Fix-wave review P1-1, end to end and across a restart: a save that landed while a pass was
    /// running is still owed at the next start, and that start is what honours it.
    /// </summary>
    /// <remarks>
    /// This is the half that makes P1-1 a P1 rather than a P2. Without the compare-and-clear, the
    /// first pass discharges the newer save's obligation, the coalesced second pass is the only
    /// thing that would have honoured it, and closing the application before that pass finishes
    /// loses the change for good: the re-derive moves no row, so no later scan widens to
    /// <c>InvalidatedNights</c>, and the nights hold filled frames so the unfilled query skips
    /// them. The second host is a real restart over the same app data root, which is the only way
    /// to prove that what survives is the stored flag and not the in-process one.
    /// </remarks>
    [Fact]
    public async Task ASaveThatLandedDuringAPass_IsStillOwedAtTheNextStart()
    {
        var root = Path.Combine(Path.GetTempPath(), "GalactiLogPhd2Owed_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            using (var first = new Host(root, ownsRoot: false))
            {
                var store = first.Services.GetRequiredService<SettingsStore>();

                // Save 1 records the obligation. The pass that honours it takes the document as it
                // stands now.
                store.MutateGeneral(general => general with { ObserverTimezone = "UTC" });
                var observedByThePass = store.GetGeneral();

                // Save 2 lands while that pass is still running.
                store.MutateGeneral(general => general with { ObserverLatitude = 51.4779 });

                // The pass finishes and tries to discharge what it saw.
                store.ClearCorrelationPendingIfUnchanged(observedByThePass);
                Assert.True(
                    store.GetGeneral().Phd2CorrelationPending,
                    "a pass must not discharge a save it never saw");

                // The application closes before the coalesced second pass finishes.
            }

            using var second = new Host(root, ownsRoot: false);
            second.Services.GetRequiredService<ScanStatusService>();
            await second.Services.GetRequiredService<Phd2CorrelationRunner>().InFlight;

            Assert.Single(second.Read(context => context.ActivityEvents
                .Where(entry => entry.EventType == "phd2_correlation_complete")
                .ToList()));
            Assert.False(
                second.Services.GetRequiredService<SettingsStore>().GetGeneral().Phd2CorrelationPending);
        }
        finally
        {
            Serilog.Log.CloseAndFlush();
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// The phase review's loss sequence 3, with the forty nights reduced to three: a re-map, an
    /// application that closes with the pass part way through, and a restart that finishes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Night one stands for the nights the interrupted pass reached and nights two and three for
    /// the ones it never got to. That intermediate state, one night re-derived and the rest still
    /// carrying values the saved mapping no longer produces, is exactly what a pass killed part
    /// way leaves on disk: <c>Phd2Correlation.Run</c> commits one night per transaction, so the
    /// nights behind it are finished and the nights ahead of it are untouched. What the fix adds
    /// is the one thing that state used to be missing, which is a record that the work was owed.
    /// </para>
    /// <para>
    /// A failure looks like: nights two and three keep the old rig's guiding numbers forever.
    /// Nothing else reaches them. <c>GuidingInputsMoved</c> is false on every later save because
    /// the stored map now equals the saved one; the nights need no fill because their frames all
    /// carry a value; the delta skip never re-reads their unchanged logs.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AReMapInterruptedPartWay_IsFinishedAtTheNextStart_OnEveryNightItNeverReached()
    {
        var root = Path.Combine(Path.GetTempPath(), "GalactiLogPhd2Restart_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var nights = new[] { new DateOnly(2025, 3, 17), new DateOnly(2025, 3, 18), new DateOnly(2025, 3, 19) };

        try
        {
            // Session one. The three nights are filled under the old mapping, the user re-maps the
            // rig, and the pass gets through night one before the application closes.
            using (var first = new Host(root, ownsRoot: false))
            {
                first.Seed(context =>
                {
                    var log = new Phd2Log
                    {
                        Id = Guid.NewGuid(),
                        FilePath = Path.Combine(root, "PHD2_GuideLog.txt"),
                        ParseStatus = "ok",
                        ParsedAt = DateTime.UtcNow,
                    };
                    context.Phd2Logs.Add(log);

                    for (var i = 0; i < nights.Length; i++)
                    {
                        context.Phd2Sessions.Add(new Phd2Session
                        {
                            Id = Guid.NewGuid(),
                            LogId = log.Id,
                            RunIndex = 0,
                            SectionIndex = i,
                            StartedAtLocal = nights[i].ToDateTime(new TimeOnly(21, 31)),
                            DurationS = 600,
                            SessionDate = nights[i],
                            EquipmentProfile = "Rig A",
                            Telescope = "RC8",
                            PixelScaleArcsec = 1.5,
                        });

                        // Night one is already re-derived, which is what the pass that reached it
                        // left behind; nights two and three still carry RC8's numbers.
                        context.Images.Add(NewLight(nights[i], filled: i > 0));
                    }
                });

                first.Services.GetRequiredService<SettingsStore>().MutateGeneral(general => general with
                {
                    Phd2ProfileMap = Phd2Profiles.ToJson(new Dictionary<string, Phd2ProfileEntry>
                    {
                        // The zone is load-bearing from Phase 15B Task 2d onward, and it is what
                        // the case always meant: spec 7.6's clear-side hold-out keeps a night
                        // whose only guiding is unzoned, because nothing records the zone a stored
                        // value was derived under. With no zone on this profile the re-derive
                        // nulls every session_date, every night reads as unzoned-only, and the
                        // pass correctly clears nothing, which is a different rule under test.
                        // This case is about the nights an interrupted pass never reached.
                        ["Rig A"] = new() { Telescope = "Askar 120", Timezone = "UTC" },
                    }),
                });

                // The save recorded the obligation in the settings document, which is the whole of
                // what survives the process.
                Assert.True(
                    first.Services.GetRequiredService<SettingsStore>()
                        .GetGeneral().Phd2CorrelationPending);
            }

            // Session two: the restart. Nothing is saved, nothing is scanned, and nobody asks for
            // a re-run; GUI startup alone has to finish the work.
            using var second = new Host(root, ownsRoot: false);
            second.Services.GetRequiredService<ScanStatusService>();
            await second.Services.GetRequiredService<Phd2CorrelationRunner>().InFlight;

            var images = second.Read(context => context.Images.ToList());
            Assert.Equal(3, images.Count);
            Assert.All(images, image =>
            {
                Assert.Null(image.GuidingRmsSource);
                Assert.Null(image.GuidingRmsArcsec);
            });
            Assert.False(
                second.Services.GetRequiredService<SettingsStore>().GetGeneral().Phd2CorrelationPending);
        }
        finally
        {
            Serilog.Log.CloseAndFlush();
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Phase 15B fixer F2, with items 30 and 38. The host wires ONE handler to BOTH sources that
    /// can make an open page stale without a scan, and that handler drops the derived memos before
    /// it raises the notification the pages follow.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A failure looks like the flow this phase built dying silently in the shipped application
    /// with every view-model case still green: the reader reads "12 guiding sessions found but no
    /// PHD2 profile is mapped to a telescope", follows the page's own link into Settings, maps a
    /// profile, comes back to Statistics and reads the same sentence. The view-model half is
    /// pinned against the page's own seams; this case exists because those seams are only as good
    /// as what <c>AppHost</c> actually hands them.
    /// </para>
    /// <para>
    /// Both sources are exercised through the real host: a settings save raised through the host's
    /// own <c>SettingsStore</c>, and the correlation pass that save queues, which the host's own
    /// runner completes. The re-run's own completion is the second raise, and it is the one
    /// <c>task5b-review.md</c> P3-10 recorded as reaching nothing at all.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ASettingsSaveAndACompletedReRun_EachRaiseTheHostsOneDerivedDataNotification()
    {
        using var host = new Host();

        // GUI startup, which is what makes the subscriptions.
        host.Services.GetRequiredService<ScanStatusService>();

        var raised = 0;
        host.Services.GetRequiredService<AppHost.DerivedDataNotifier>().Changed +=
            (_, _) => Interlocked.Increment(ref raised);

        var store = host.Services.GetRequiredService<SettingsStore>();

        // Source one, on its own. A save that moves no guiding input queues no re-run, so this
        // figure is the settings side alone and cannot be confused with the pass side below.
        store.MutateGeneral(general => general with { DefaultPageSize = 25 });
        Assert.Equal(1, raised);

        // Source two, on top of it: a save that does move a guiding input. Three more raises, and
        // all three are the wiring working rather than noise: the save itself, the pass's own
        // discharge of general.phd2_correlation_pending, which is a general save in its own right
        // (and which queues nothing further, because it moves no guiding input), and the
        // completion. It is a real completion, which the first case in this file proves by its
        // phd2_correlation_complete row.
        store.MutateGeneral(general => general with
        {
            Phd2ProfileMap = Phd2Profiles.ToJson(new Dictionary<string, Phd2ProfileEntry>
            {
                ["Rig A"] = new() { Telescope = "Askar 120" },
            }),
        });
        await host.Services.GetRequiredService<Phd2CorrelationRunner>().InFlight;

        Assert.Equal(4, raised);
    }

    /// <summary>
    /// Phase 15B fixer item 34's wiring half. <c>Phd2NightQuery</c> memoizes the one "does this
    /// library carry any guide log at all" read that decides whether spec 12.4's Guiding band is
    /// built, and <c>AppHost.InvalidateDerivedCaches</c> is what drops it.
    /// </summary>
    /// <remarks>
    /// A failure looks like a reader whose library has just catalogued its first guide log finding
    /// the Guiding band still absent from every target page until they restart the application,
    /// because the memo this process took before the log existed is the one every card still asks.
    /// The memo itself and its counting case are the Data half; this proves the reset is reached
    /// from the host, which is the half no Data case can see.
    /// </remarks>
    [Fact]
    public void ASettingsSave_DropsTheGuideLogMemoThroughTheHost()
    {
        using var host = new Host();
        host.Services.GetRequiredService<ScanStatusService>();

        var query = host.Services.GetRequiredService<Phd2NightQuery>();

        // Read once with no guide log, which is what memoizes "this library has none".
        Assert.False(query.AnyGuideLogs());

        host.Seed(context => context.Phd2Logs.Add(new Phd2Log
        {
            Id = Guid.NewGuid(),
            FilePath = @"C:\logs\first.txt",
            ParseStatus = "ok",
            ParsedAt = DateTime.UtcNow,
        }));

        // Still the memo, because nothing has dropped it yet. This half is what makes the
        // assertion below mean something rather than pass on a query that never memoized.
        Assert.False(query.AnyGuideLogs());

        host.Services.GetRequiredService<SettingsStore>()
            .MutateGeneral(general => general with { ObserverTimezone = "UTC" });

        Assert.True(query.AnyGuideLogs());
    }

    /// <summary>
    /// Phase 16 phase review P2-2, coordinator ruling 1, the inert half: a save that moved nothing
    /// but a <c>wbpp_</c> key costs the rest of the application nothing at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A failure looks like the phase review's journey 3: the user tunes five quality thresholds
    /// in the export page's filter, and the Target detail page behind the modal drops every derived
    /// memo and rebuilds itself five times over, once per coalesced chip edit, and again
    /// synchronously inside the window's own cleanup lambda when the page closes.
    /// </para>
    /// <para>
    /// The guide-log memo is the observable half of "the handler resolved nothing": the only route
    /// from this event to that memo is <c>InvalidateDerivedCaches</c>, and every line of that
    /// member is a <c>GetRequiredService</c>. The case reads the memo before the writes, with a log
    /// row landing between, so a memo that survives is a memo nothing dropped rather than one that
    /// was never taken.
    /// </para>
    /// </remarks>
    [Fact]
    public void AQualityFilterSave_RaisesNothing_AndDropsNoDerivedMemo()
    {
        using var host = new Host();
        host.Services.GetRequiredService<ScanStatusService>();

        var raised = 0;
        host.Services.GetRequiredService<AppHost.DerivedDataNotifier>().Changed +=
            (_, _) => Interlocked.Increment(ref raised);

        var query = host.Services.GetRequiredService<Phd2NightQuery>();
        Assert.False(query.AnyGuideLogs());
        host.Seed(context => context.Phd2Logs.Add(new Phd2Log
        {
            Id = Guid.NewGuid(),
            FilePath = @"C:\logs\quality.txt",
            ParseStatus = "ok",
            ParsedAt = DateTime.UtcNow,
        }));
        Assert.False(query.AnyGuideLogs());

        // Exactly the write QualityPanelViewModel.Write performs, twice over: one coalesced chip
        // edit, then the flush its Dispose runs while the window is closing.
        var store = host.Services.GetRequiredService<SettingsStore>();
        foreach (var enabled in new[] { true, false })
        {
            store.MutateGeneral(general => general with
            {
                WbppQualityByRigDocument = WbppSettingsRead.WriteQualityForRig(
                    general.WbppQualityByRigDocument,
                    "Askar 120 + ASI2600",
                    new WbppQualityState(enabled, QualityBaseline.Session, [])),
            });
        }

        Assert.Equal(0, raised);
        Assert.False(query.AnyGuideLogs());
    }

    /// <summary>
    /// The other half of coordinator ruling 1: the handler fails closed. A <c>wbpp_</c> key does
    /// not shield a key beside it, and a key this build does not recognise is never inert.
    /// </summary>
    /// <remarks>
    /// A failure looks like the shape the ruling refused: a handler that reads a save's key names
    /// and decides which of them matter, which goes wrong the day a key is added and nobody
    /// remembers this line. The second save carries a <c>wbpp_</c>-spelled key that no member of
    /// <c>GeneralSettings</c> names, so a predicate written as a prefix rule rather than as the
    /// four named keys fails here.
    /// </remarks>
    [Fact]
    public void ASaveBesideAWbppKey_AndASaveOfAKeyThisBuildDoesNotKnow_BothRaise()
    {
        using var host = new Host();
        host.Services.GetRequiredService<ScanStatusService>();

        var raised = 0;
        host.Services.GetRequiredService<AppHost.DerivedDataNotifier>().Changed +=
            (_, _) => Interlocked.Increment(ref raised);

        var store = host.Services.GetRequiredService<SettingsStore>();

        store.MutateGeneral(general => general with
        {
            WbppExclusionsDocument = WbppSettingsRead.WriteExclusions(["_calibrated"]),
            DefaultPageSize = 25,
        });
        Assert.Equal(1, raised);

        // What a document written by a later build of GalactiLog carries, which GeneralSettings
        // preserves through ExtensionData rather than dropping.
        store.MutateGeneral(general => general with
        {
            ExtensionData = new Dictionary<string, JsonElement>
            {
                ["wbpp_a_key_from_a_later_build"] = JsonSerializer.SerializeToElement("on"),
            },
        });
        Assert.Equal(2, raised);
    }

    /// <summary>
    /// The inert list itself, at the predicate rather than through a host: today it is exactly
    /// spec 5.8.1's four <c>wbpp_</c> keys, and every other key of the document is a move.
    /// </summary>
    [Fact]
    public void TheInertList_IsTheFourWbppKeys_AndNothingElse()
    {
        var before = new GeneralSettings();

        Assert.True(AppHost.OnlyInertKeysMoved(before, before with
        {
            WbppDefaultOsDocument = WbppSettingsRead.WriteDefaultOs("bash"),
            WbppStagingPathDocument = WbppSettingsRead.WriteStagingPath(@"Z:\staging"),
            WbppExclusionsDocument = WbppSettingsRead.WriteExclusions(["_calibrated"]),
            WbppQualityByRigDocument = WbppSettingsRead.WriteQualityForRig(
                null, "rig", new WbppQualityState(true, QualityBaseline.Session, [])),
        }));

        Assert.False(AppHost.OnlyInertKeysMoved(before, before with { ObserverName = "somebody" }));
        Assert.False(AppHost.OnlyInertKeysMoved(before, before with { ScanRoots = [@"Z:\library"] }));
    }

    /// <summary>
    /// The three arguments <c>AppHost</c> has to hand over for this phase's work to exist in the
    /// running application, each of which is right in every test and wrong in production when the
    /// line is missing, because every test supplies its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A source scan rather than a resolved instance, because none of the three is observable
    /// from outside the object it was handed to: a page's follow is a pair of delegates it never
    /// exposes, and a query's profile map is a captured field. The scan is the same shape
    /// <c>GuideGraphSource_HandlersAreThin</c> uses, over the stripped source so a comment naming
    /// one of them cannot satisfy it.
    /// </para>
    /// <para>
    /// A failure looks like the Statistics page, the Target detail page or the Analysis page never
    /// hearing the notification this file's first case proves the host raises (fixer F2), or the closed
    /// Guiding band's session count reading zero on every card because the query behind it was
    /// given no profile map (fixer item 33).
    /// </para>
    /// </remarks>
    [Fact]
    public void AppHostSource_HandsBothPagesTheNotification_AndTheDetailQueryTheProfileMap()
    {
        var code = SourceScan.StripComments(File.ReadAllText(
            Path.Combine(SourceScan.SrcRoot(), "GalactiLog.App", "AppHost.cs")));

        // Three pages follow, and each follow is a pair: the Statistics page, the Target detail
        // page and, from Phase 17, the Analysis page. The subscribe needle is guarded against
        // "unsubscribeDerivedDataChanged:", which contains it.
        Assert.Equal(3, Regex.Matches(code, @"(?<!un)subscribeDerivedDataChanged:").Count);
        Assert.Equal(3, Regex.Matches(code, @"unsubscribeDerivedDataChanged:").Count);

        // Four queries read the live profile map: the two guiding queries, the night list the
        // closed band's count rides on, and AnalysisQuery, whose guiding metrics are attributed
        // through the same map (ruling A10).
        Assert.Equal(
            4,
            Regex.Matches(code, @"settingsStore\.GetGeneral\(\)\.Phd2ProfileMap").Count);
        Assert.Contains("new TargetDetailQuery(", code, StringComparison.Ordinal);
    }

    private static Image NewLight(DateOnly night, bool filled)
    {
        var id = Guid.NewGuid();
        return new Image
        {
            Id = id,
            FilePath = $@"C:\library\{id:N}.fits",
            FileName = $"{id:N}.fits",
            ImageType = "LIGHT",
            SessionDate = night,
            CaptureDate = night.ToDateTime(new TimeOnly(21, 31)),
            ExposureTime = 60,
            Telescope = "Askar 120",
            GuidingRmsSource = filled ? "phd2" : null,
            GuidingRmsArcsec = filled ? 0.9 : null,
            GuidingRmsRaArcsec = filled ? 0.6 : null,
            GuidingRmsDecArcsec = filled ? 0.6 : null,
        };
    }

    /// <summary>A real host over its own temp app data root, the shape
    /// <c>HostDisposalTests.HostFixture</c> and <c>AppHostTests.AppHostFixture</c> both use.
    /// </summary>
    private sealed class Host : IDisposable
    {
        private readonly string _root;
        private readonly bool _ownsRoot;
        private readonly IHost _host;

        public Host(string? root = null, bool ownsRoot = true)
        {
            _ownsRoot = ownsRoot;
            _root = root
                ?? Path.Combine(Path.GetTempPath(), "GalactiLogPhd2Wiring_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            // Serilog's Log.Logger is process-global and Build() overwrites it, so a previous
            // host's file sink would keep its log file open under a root this one is about to
            // delete. The same line both existing host fixtures carry.
            Serilog.Log.CloseAndFlush();
            _host = AppHost.Build(_root, cliMode: false);
        }

        public IServiceProvider Services => _host.Services;

        public void Seed(Action<GalactiLogContext> seed)
        {
            using var context = Open(tracking: true);
            seed(context);
            context.SaveChanges();
        }

        public T Read<T>(Func<GalactiLogContext, T> read)
        {
            using var context = Open(tracking: false);
            return read(context);
        }

        public void Dispose()
        {
            _host.Dispose();
            Serilog.Log.CloseAndFlush();
            SqliteConnection.ClearAllPools();

            // Best effort, the reason HostDisposalTests.DeleteRoot gives: a pool read still
            // holding a SQLite handle makes the delete throw, and a leaked temp directory is not
            // worth a test failure. A root the caller supplied is the caller's to delete: the
            // restart case builds two hosts over one root on purpose.
            try
            {
                if (_ownsRoot && Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        private GalactiLogContext Open(bool tracking) => new(GalactiLogContextOptions.Create(
            Services.GetRequiredService<DatabaseConnectionString>().Value, tracking));
    }
}
