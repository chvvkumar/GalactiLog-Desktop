using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using GalactiLog.Core.Metrics;
using GalactiLog.Core.Phd2;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using GalactiLog.Data.Tests.Phd2;
using GalactiLog.Data.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace GalactiLog.Data.Tests.Queries;

/// <summary>
/// <see cref="AnalysisQuery"/>, the port of <c>backend/app/api/analysis.py</c> at
/// <c>591234b</c>: the six figures, the two picker lists, the shared filter, the row order, the
/// PHD2 night join through the live profile map, and the plate scale count of user ruling U3.
/// </summary>
/// <remarks>
/// <para>
/// Every case builds its own frames rather than leaning on <c>LibrarySeeder</c>'s shared plan,
/// which five other query test classes assert exact counts against
/// (<c>collision-map-a.md</c> section 3). The seeder is used for nothing here and is not edited.
/// </para>
/// <para>
/// The real-data cases seed from <c>tests/Fixtures/realdata/inputs.json</c>, which
/// lives in the repository, and assert against <c>oracle.json</c> beside it. Nothing under
/// <c>C:\tmp</c> is read, so every one of them runs on a clean copy. Night, target and rig are
/// ordinals in both files, so no case names a rig, a target or a place.
/// </para>
/// </remarks>
public class AnalysisQueryTests(ITestOutputHelper output)
{
    private static readonly AnalysisFilter NoFilter =
        new(null, null, null, AnalysisGranularity.Frame, null, null);

    private static readonly AnalysisFilter BySession =
        NoFilter with { Granularity = AnalysisGranularity.Session };

    private static readonly DateOnly Night1 = new(2026, 1, 5);
    private static readonly DateOnly Night2 = new(2026, 1, 6);
    private static readonly DateOnly Night3 = new(2026, 2, 7);

    // ---- 7.1 the metric table ----------------------------------------------------------

    [Fact]
    public void TheMetricTable_MapsEveryKeyToTheColumnTheWebReads()
    {
        // Named rather than merely caught: the most likely defect is fwhm wired to median_fwhm,
        // which every OTHER reader of that table uses, while METRIC_MAP line 65 is Image.fwhm,
        // the N.I.N.A. Session Metadata CSV column already in arcseconds. Every figure on the
        // page moves if this one is wrong.
        Assert.Equal("fwhm", Column(AnalysisMetric.Fwhm));
        Assert.Equal("median_hfr", Column(AnalysisMetric.Hfr));
        Assert.Equal("guiding_rms_arcsec", Column(AnalysisMetric.GuidingRms));
        Assert.Equal("guiding_rms_ra_arcsec", Column(AnalysisMetric.GuidingRmsRa));
        Assert.Equal("guiding_rms_dec_arcsec", Column(AnalysisMetric.GuidingRmsDec));

        // The other sixteen column metrics read a column spelled like their key.
        foreach (var metric in AnalysisMetrics.X.Concat(AnalysisMetrics.Y))
        {
            Assert.NotEqual("", Column(metric));
            Assert.Equal(metric, AnalysisMetrics.Parse(AnalysisMetrics.Key(metric)));
        }

        // The oracle's own axis lists, in order.
        Assert.Equal(
            ["humidity", "wind_speed", "ambient_temp", "dew_point", "pressure",
             "cloud_cover", "sky_quality", "focuser_temp", "airmass", "sensor_temp"],
            AnalysisMetrics.X.Select(AnalysisMetrics.Key));
        Assert.Equal(
            ["hfr", "fwhm", "eccentricity", "guiding_rms", "guiding_rms_ra",
             "guiding_rms_dec", "detected_stars", "adu_mean", "adu_median", "adu_stdev"],
            AnalysisMetrics.Y.Select(AnalysisMetrics.Key));
        Assert.Equal(
            ["phd2_rms_total", "phd2_rms_ra", "phd2_rms_dec", "phd2_star_lost_pct", "phd2_snr_mean"],
            AnalysisMetrics.Phd2X.Select(AnalysisMetrics.Key));

        // Ruling A9: a stored value outside the set reads as null so the caller falls back to its
        // default, and nothing throws on a hand-edited display document.
        Assert.Null(AnalysisMetrics.Parse(null));
        Assert.Null(AnalysisMetrics.Parse(""));
        Assert.Null(AnalysisMetrics.Parse("HFR"));
        Assert.Null(AnalysisMetrics.Parse("median_hfr"));
    }

    [Fact]
    public void AMetricOutsideTheTable_RaisesArgumentOutOfRange_AndNotAnIndexError()
    {
        // The metric table is indexed by the enum's own ordinal, so a value outside it read the
        // array directly and raised IndexOutOfRangeException where every refusal on this page is
        // documented as ArgumentOutOfRangeException. AnalysisQuery's own guards test
        // Enum.IsDefined first and never reach it; AnalysisCache builds its discriminator from
        // Key BEFORE it delegates, so the cache is the path that shows the wrong exception type.
        Assert.Throws<ArgumentOutOfRangeException>(() => AnalysisMetrics.Key((AnalysisMetric)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => AnalysisMetrics.Key((AnalysisMetric)(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Column((AnalysisMetric)99));

        using var library = new Library();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => library.Cache.Distribution((AnalysisMetric)99, NoFilter));
    }

    [Fact]
    public void EveryTabButCorrelationRefusesAPhd2Metric()
    {
        using var library = new Library();

        // Lines 499, 579, 671 and 832 raise HTTP 400 for a metric outside METRIC_MAP. The port
        // has no HTTP layer, so the refusal is an exception; Task 4 never offers one in a picker.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => library.Query.Distribution(AnalysisMetric.Phd2RmsTotal, NoFilter));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => library.Query.BoxPlot(AnalysisMetric.Phd2SnrMean, BoxPlotGrouping.Filter, NoFilter));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => library.Query.TimeSeries(AnalysisMetric.Phd2RmsRa, NoFilter));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => library.Query.Compare(
                AnalysisMetric.Phd2RmsDec, CompareMode.Filter, "a", "b", null, null));

        // Line 366's guard allows any METRIC_MAP member as Y and only rejects a PHD2 one.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => library.Query.Correlation(AnalysisMetric.Hfr, AnalysisMetric.Phd2RmsTotal, NoFilter));
    }

    [Fact]
    public void TheQuerySource_WritesNothing_AndRoundsThroughTheOneRule()
    {
        // The structural equivalent of the file-scoped bans Phd2NightQueryTests and
        // GuidingStatsQueryTests already carry over their own files (collision-map-a.md 4.4):
        // the check lives at the one place rather than in a reviewer's memory.
        foreach (var file in new[]
        {
            "src/GalactiLog.Data/Queries/AnalysisQuery.cs",
            "src/GalactiLog.Data/Queries/AnalysisCache.cs",
        })
        {
            var source = SourceScan.Read(file);
            Assert.DoesNotContain("Math.Round", source, StringComparison.Ordinal);
            Assert.DoesNotContain("System.IO", source, StringComparison.Ordinal);
            Assert.DoesNotContain("SaveChanges", source, StringComparison.Ordinal);
            Assert.DoesNotContain("ExecuteUpdate", source, StringComparison.Ordinal);
            Assert.DoesNotContain("ExecuteDelete", source, StringComparison.Ordinal);

            // Phd2CorrelationTests:196 asserts the exact set of files that name this column.
            Assert.DoesNotContain("GuidingRmsSource", source, StringComparison.Ordinal);

            // Ruling A10: the stored PHD2 rig column is never projected and never read.
            Assert.DoesNotContain("session.Telescope", source, StringComparison.Ordinal);
            Assert.DoesNotContain("phd2_sessions.telescope", source, StringComparison.Ordinal);
        }
    }

    // ---- section 4, the row order ------------------------------------------------------

    [Fact]
    public void RowOrder_IsOrdinalOnId_AndNotCaseInsensitive()
    {
        using var library = new Library();

        // Two ids on ONE night whose ordinal and case-insensitive orders disagree: 'B' is 0x42
        // and 'a' is 0x61, so BINARY puts the uppercase one first, while OrdinalIgnoreCase and
        // most culture comparisons put "aaaa..." first. The reference library has only three
        // distinct session_date values, so the tie break decides essentially every row's
        // position and this is not a detail.
        library.AddFramesWithTextIds(
        [
            ("aaaaaaaa-0000-0000-0000-000000000001",
                library.Frame(frame => { frame.Humidity = 2; frame.MedianHfr = 20; })),
            ("BBBBBBBB-0000-0000-0000-000000000002",
                library.Frame(frame => { frame.Humidity = 1; frame.MedianHfr = 10; })),
        ]);

        var result = library.Query.Correlation(AnalysisMetric.Humidity, AnalysisMetric.Hfr, NoFilter);

        // Seen red by re-sorting the read rows with StringComparer.OrdinalIgnoreCase on the id,
        // which returns the 2/20 point first.
        Assert.Equal([1d, 2d], result.Points.Select(point => point.X));
    }

    // ---- 7.2 the shared filter, one component at a time --------------------------------

    [Fact]
    public void TheTelescopeAndCameraFilters_IncludeAliasedFrames()
    {
        using var library = new Library();
        library.MapTelescope("Canonical scope", "Raw scope");
        library.MapCamera("Canonical cam", "Raw cam");
        library.AddFrames(
        [
            library.Frame(frame =>
            {
                frame.Telescope = "Raw scope";
                frame.Camera = "Raw cam";
                frame.Humidity = 1;
                frame.MedianHfr = 1;
            }),
            library.Frame(frame =>
            {
                frame.Telescope = "Other scope";
                frame.Camera = "Other cam";
                frame.Humidity = 2;
                frame.MedianHfr = 2;
            }),
        ]);

        // expand_canonical (lines 159 and 163): the canonical name expands to every raw spelling,
        // so the aliased frame is INCLUDED.
        var byTelescope = library.Query.Correlation(
            AnalysisMetric.Humidity, AnalysisMetric.Hfr,
            NoFilter with { Telescope = "Canonical scope" });
        Assert.Equal([1d], byTelescope.Points.Select(point => point.X));

        var byCamera = library.Query.Correlation(
            AnalysisMetric.Humidity, AnalysisMetric.Hfr,
            NoFilter with { Camera = "Canonical cam" });
        Assert.Equal([1d], byCamera.Points.Select(point => point.X));

        // A canonical name with no aliases expands to itself alone.
        var unconfigured = library.Query.Correlation(
            AnalysisMetric.Humidity, AnalysisMetric.Hfr,
            NoFilter with { Telescope = "Other scope" });
        Assert.Equal([2d], unconfigured.Points.Select(point => point.X));
    }

    [Fact]
    public void TheFilterUsedFilter_ExcludesAliasedFrames_BecauseItDoesNotFold()
    {
        using var library = new Library();
        library.MapFilter("Ha", "H-alpha");
        library.AddFrames(
        [
            library.Frame(frame => { frame.FilterUsed = "Ha"; frame.Humidity = 1; frame.MedianHfr = 1; }),
            library.Frame(frame => { frame.FilterUsed = "H-alpha"; frame.Humidity = 2; frame.MedianHfr = 2; }),
        ]);

        // Line 165 is `Image.filter_used == filter_used` with no alias expansion at all, so the
        // aliased frame is EXCLUDED. That is asymmetric with the box plot's own By Filter
        // grouping, which does fold, and the asymmetry is the web's: spec 12.14 states it.
        //
        // Seen red by folding the selection through AliasMap.ExpandFilter "for consistency",
        // which returns both points and silently widens every Analysis query relative to the web.
        var result = library.Query.Correlation(
            AnalysisMetric.Humidity, AnalysisMetric.Hfr, NoFilter with { FilterUsed = "Ha" });
        Assert.Equal([1d], result.Points.Select(point => point.X));

        // The same library, grouped by filter, folds the two into ONE group of four values.
        library.AddFrames(
        [
            library.Frame(frame => { frame.FilterUsed = "Ha"; frame.MedianHfr = 3; }),
            library.Frame(frame => { frame.FilterUsed = "H-alpha"; frame.MedianHfr = 4; }),
        ]);
        var folded = library.Query.BoxPlot(AnalysisMetric.Hfr, BoxPlotGrouping.Filter, NoFilter);
        Assert.Equal("Ha", Assert.Single(folded.Groups).GroupName);
        Assert.Equal(4, folded.Groups[0].Count);
    }

    [Fact]
    public void TheDateBounds_AreInclusiveAtBothEnds_AndReadSessionDate()
    {
        using var library = new Library();
        library.AddFrames(
        [
            library.Frame(frame => { frame.SessionDate = Night1; frame.Humidity = 1; frame.MedianHfr = 1; }),
            library.Frame(frame => { frame.SessionDate = Night2; frame.Humidity = 2; frame.MedianHfr = 2; }),
            library.Frame(frame => { frame.SessionDate = Night3; frame.Humidity = 3; frame.MedianHfr = 3; }),
        ]);

        // Lines 167 and 169, both inclusive, both on session_date and not capture_date.
        Assert.Equal(
            [2d, 3d],
            library.Query.Correlation(
                AnalysisMetric.Humidity, AnalysisMetric.Hfr, NoFilter with { From = Night2 })
                .Points.Select(point => point.X));

        Assert.Equal(
            [1d, 2d],
            library.Query.Correlation(
                AnalysisMetric.Humidity, AnalysisMetric.Hfr, NoFilter with { To = Night2 })
                .Points.Select(point => point.X));

        Assert.Equal(
            [2d],
            library.Query.Correlation(
                AnalysisMetric.Humidity, AnalysisMetric.Hfr,
                NoFilter with { From = Night2, To = Night2 })
                .Points.Select(point => point.X));
    }

    // ---- 7.3 granularity, and the key that has no rig ----------------------------------

    [Fact]
    public void SessionGranularity_GroupsOnNightAndTargetAndNothingElse()
    {
        using var library = new Library();
        var target = library.AddTarget("One");
        var other = library.AddTarget("Two");

        // A night where TWO RIGS imaged ONE target. Ruling A17: there is no rig in the key
        // (lines 421 and 523), so this is ONE point whose X and Y are the medians over both
        // rigs' frames. The roadmap's (session_date, rig) key is wrong and gives two.
        //
        // Seen red by adding i.telescope to the grouping key, which yields two points here.
        library.AddFrames(
        [
            library.Frame(frame => Sample(frame, Night1, target, "Rig A", 10, 100)),
            library.Frame(frame => Sample(frame, Night1, target, "Rig A", 20, 200)),
            library.Frame(frame => Sample(frame, Night1, target, "Rig B", 30, 300)),
            library.Frame(frame => Sample(frame, Night1, target, "Rig B", 40, 400)),
        ]);

        var oneNight = library.Query.Correlation(AnalysisMetric.Humidity, AnalysisMetric.Hfr, BySession);
        var point = Assert.Single(oneNight.Points);
        Assert.Equal(25d, point.X);
        Assert.Equal(250d, point.Y);
        Assert.Equal(Night1, point.Night);

        // Frame granularity over the same rows is one point per frame.
        Assert.Equal(
            4,
            library.Query.Correlation(AnalysisMetric.Humidity, AnalysisMetric.Hfr, NoFilter).Points.Count);

        // A second night with TWO TARGETS gives two points.
        library.AddFrames(
        [
            library.Frame(frame => Sample(frame, Night2, target, "Rig A", 1, 1)),
            library.Frame(frame => Sample(frame, Night2, other, "Rig A", 2, 2)),
        ]);
        Assert.Equal(
            3,
            library.Query.Correlation(AnalysisMetric.Humidity, AnalysisMetric.Hfr, BySession).Points.Count);

        // A third night whose frames carry no resolved target collapses to ONE point: a null
        // target id is a legal key and collects the night's unresolved frames together.
        library.AddFrames(
        [
            library.Frame(frame => Sample(frame, Night3, null, "Rig A", 5, 50)),
            library.Frame(frame => Sample(frame, Night3, null, "Rig B", 7, 70)),
        ]);

        var all = library.Query.Correlation(AnalysisMetric.Humidity, AnalysisMetric.Hfr, BySession);
        Assert.Equal(4, all.Points.Count);
        var unresolved = Assert.Single(all.Points, candidate => candidate.Night == Night3);
        Assert.Null(unresolved.TargetId);
        Assert.Equal(6d, unresolved.X);
        Assert.Equal(60d, unresolved.Y);
    }

    // ---- 7.4 an unreadable session_date empties nothing ---------------------------------

    [Fact]
    public void AnUnreadableSessionDate_EmptiesNothing_AndAppearsInNoResult()
    {
        using var library = new Library();
        var target = library.AddTarget("One");
        library.AddFrames(
        [
            library.Frame(frame => Sample(frame, Night1, target, "Rig A", 1, 11)),
            library.Frame(frame => Sample(frame, Night1, target, "Rig A", 2, 12)),
            library.Frame(frame => Sample(frame, Night1, target, "Rig A", 3, 13)),
            library.Frame(frame => Sample(frame, Night1, target, "Rig A", 4, 14)),
        ]);

        // One row with session_date = '', written through SQL because no writer in the
        // application can produce it. This is the shape TargetListingQuery shipped a defect for:
        // one bad row emptied the whole dashboard listing.
        //
        // Seen red by reading the column with DateOnly.ParseExact instead of SqlReaders.ReadDate,
        // which throws a FormatException out of every one of the six reads below.
        library.AddFrameWithBrokenSessionDate(frame => Sample(frame, Night1, target, "Rig A", 99, 99));

        var correlation = library.Query.Correlation(AnalysisMetric.Humidity, AnalysisMetric.Hfr, NoFilter);
        Assert.Equal(4, correlation.TotalCount);
        Assert.DoesNotContain(correlation.Points, point => point.X == 99d);

        Assert.Equal(4, library.Query.Distribution(AnalysisMetric.Hfr, NoFilter)!.Stats.Count);
        Assert.Equal(4, Assert.Single(library.Query.BoxPlot(AnalysisMetric.Hfr, BoxPlotGrouping.Target, NoFilter).Groups).Count);

        var series = library.Query.TimeSeries(AnalysisMetric.Hfr, NoFilter);
        Assert.Equal(4, Assert.Single(series.Points).FrameCount);

        var cell = Assert.Single(
            library.Query.Matrix(NoFilter).Cells,
            candidate => candidate.X == AnalysisMetric.Humidity && candidate.Y == AnalysisMetric.Hfr);
        Assert.Equal(4, cell.NPoints);

        var compare = library.Query.Compare(
            AnalysisMetric.Hfr, CompareMode.Equipment, "Rig A|||Cam", "Rig A|||Cam", null, null);
        Assert.Equal(4, compare.CountA);
    }

    // ---- 7.5 the PHD2 join follows the live profile map ---------------------------------

    [Fact]
    public void ThePhd2Join_ResolvesTheRigThroughTheLiveMap_WhereTheStoredColumnIsNull()
    {
        using var library = new Library();

        // The user's own library's state, measured: phd2_sessions.telescope is NULL on all 937
        // rows, because the port resolves the rig live and never writes that column. A verbatim
        // port of phd2_night_subquery keeps line 137's `telescope IS NOT NULL` and line 144's
        // group_by over the same column, and returns ZERO rows here.
        //
        // Seen red by projecting and grouping on the stored column, which returns no points at
        // all from this library and fails the second case below outright.
        library.MapProfile("profile-a", "Rig A");
        library.AddPhd2Session(session =>
        {
            session.EquipmentProfile = "profile-a";
            session.Telescope = null;
            session.SessionDate = Night1;
            session.FrameCount = 1024;
            session.RmsTotalArcsec = 0.5;
        });

        library.AddFrames(
        [
            library.Frame(frame => Sample(frame, Night1, null, "Rig A", 0, 1.5)),
            library.Frame(frame => Sample(frame, Night2, null, "Rig A", 0, 2.5)),
        ]);

        var joined = library.Query.Correlation(
            AnalysisMetric.Phd2RmsTotal, AnalysisMetric.Hfr, NoFilter);

        // Strict session_date equality (line 399): the Night2 frame has no night row and is
        // dropped, which is the LEFT JOIN plus `x IS NOT NULL` of lines 395 to 404.
        var point = Assert.Single(joined.Points);
        Assert.Equal(0.5, point.X);
        Assert.Equal(1.5, point.Y);
    }

    [Fact]
    public void AStalePhd2TelescopeColumn_LosesToTheLiveMap()
    {
        using var library = new Library();
        library.MapProfile("profile-a", "Rig A");
        library.AddPhd2Session(session =>
        {
            session.EquipmentProfile = "profile-a";

            // What the ingest wrote before the user remapped the profile. Nothing writes a newer
            // value back, so a reader that consults it shows the state from before the mapping.
            session.Telescope = "Rig B";
            session.SessionDate = Night1;
            session.FrameCount = 1024;
            session.RmsTotalArcsec = 0.5;
        });

        library.AddFrames(
        [
            library.Frame(frame => Sample(frame, Night1, null, "Rig A", 0, 1.5)),
            library.Frame(frame => Sample(frame, Night1, null, "Rig B", 0, 2.5)),
        ]);

        var result = library.Query.Correlation(AnalysisMetric.Phd2RmsTotal, AnalysisMetric.Hfr, NoFilter);

        // The MAP's rig, not the column's: the "Rig A" frame joins and the "Rig B" frame does not.
        var point = Assert.Single(result.Points);
        Assert.Equal(1.5, point.Y);
    }

    [Fact]
    public void ChangingTheProfileMap_MovesTheFrameToAnotherRigsNight()
    {
        using var library = new Library();
        library.MapProfile("profile-a", "Rig A");
        library.AddPhd2Session(session =>
        {
            session.EquipmentProfile = "profile-a";
            session.Telescope = null;
            session.SessionDate = Night1;
            session.FrameCount = 1024;
            session.RmsTotalArcsec = 0.5;
        });
        library.AddFrames([library.Frame(frame => Sample(frame, Night1, null, "Rig A", 0, 1.5))]);

        Assert.Single(library.Query.Correlation(
            AnalysisMetric.Phd2RmsTotal, AnalysisMetric.Hfr, NoFilter).Points);

        // No re-scan and no write to phd2_sessions: only the map moves.
        library.MapProfile("profile-a", "Rig B");

        Assert.Empty(library.Query.Correlation(
            AnalysisMetric.Phd2RmsTotal, AnalysisMetric.Hfr, NoFilter).Points);
    }

    [Fact]
    public void ASessionBelowMinFrames_CountsForStarLostAndSnr_ButNotForTheRms()
    {
        using var library = new Library();
        library.MapProfile("profile-a", "Rig A");

        // One session over the gate and one under it. Lines 113 to 118 gate the three RMS
        // figures on frame_count >= MIN_FRAMES; lines 128 to 135 gate the other two on nothing.
        library.AddPhd2Session(session =>
        {
            session.EquipmentProfile = "profile-a";
            session.SessionDate = Night1;
            session.FrameCount = 1000;
            session.RmsTotalArcsec = 0.5;
            session.DropCount = 10;
            session.SnrMean = 20;
        });
        library.AddPhd2Session(session =>
        {
            session.EquipmentProfile = "profile-a";
            session.SessionDate = Night1;
            session.FrameCount = Phd2Metrics.MinFrames - 1;
            session.RmsTotalArcsec = 99;
            session.DropCount = 1;
            session.SnrMean = 1000;
        });

        // A second night whose only session is under the gate: its RMS is null, so its frames
        // are dropped from the plot entirely.
        library.AddPhd2Session(session =>
        {
            session.EquipmentProfile = "profile-a";
            session.SessionDate = Night2;
            session.FrameCount = Phd2Metrics.MinFrames - 1;
            session.RmsTotalArcsec = 3;
            session.DropCount = 0;
            session.SnrMean = 5;
        });

        library.AddFrames(
        [
            library.Frame(frame => Sample(frame, Night1, null, "Rig A", 0, 1.5)),
            library.Frame(frame => Sample(frame, Night2, null, "Rig A", 0, 2.5)),
        ]);

        var rms = Assert.Single(library.Query.Correlation(
            AnalysisMetric.Phd2RmsTotal, AnalysisMetric.Hfr, NoFilter).Points);
        Assert.Equal(0.5, rms.X);
        Assert.Equal(1.5, rms.Y);

        // 100.0 * (10 + 1) / (1000 + 99), every session counted, no gate.
        var starLost = library.Query.Correlation(
            AnalysisMetric.Phd2StarLostPct, AnalysisMetric.Hfr, NoFilter);
        Assert.Equal(2, starLost.Points.Count);
        Assert.Equal(100.0 * 11 / 1099, starLost.Points[0].X);

        // (20 * 1000 + 1000 * 99) / (1000 + 99), every session with a non-null snr_mean.
        var snr = library.Query.Correlation(AnalysisMetric.Phd2SnrMean, AnalysisMetric.Hfr, NoFilter);
        Assert.Equal((20d * 1000 + 1000d * 99) / 1099, snr.Points[0].X);
    }

    // ---- 7.5a the shapes the user's own library has -------------------------------------

    [Fact]
    public void AMetricWithNoValueAtAll_RendersEveryEmptyStateAndThrowsNothing()
    {
        using var library = new Library();
        var target = library.AddTarget("One");
        library.AddFrames(Enumerable.Range(0, 12)
            .Select(i => library.Frame(frame =>
            {
                Sample(frame, Night1, target, "Rig A", 40 + i, 1.0 + (i * 0.1));

                // sky_quality is 0 of 416 on the user's own library, the only metric with no
                // value at all, so this empty state is the common path and not hypothetical.
                frame.SkyQuality = null;
            })));

        var correlation = library.Query.Correlation(
            AnalysisMetric.SkyQuality, AnalysisMetric.Hfr, NoFilter);
        Assert.Empty(correlation.Points);
        Assert.Equal(0, correlation.TotalCount);
        Assert.Null(correlation.Trend);
        Assert.Null(correlation.XStats);
        Assert.Null(correlation.YStats);
        Assert.Equal(CorrelationQuality.TooFewPoints, correlation.Quality);

        Assert.Null(library.Query.Distribution(AnalysisMetric.SkyQuality, NoFilter));
        Assert.Empty(library.Query.BoxPlot(AnalysisMetric.SkyQuality, BoxPlotGrouping.Target, NoFilter).Groups);
        Assert.Empty(library.Query.TimeSeries(AnalysisMetric.SkyQuality, NoFilter).Points);

        // The whole sky_quality row of the matrix is ten cells of no points and no value.
        var row = library.Query.Matrix(NoFilter).Cells
            .Where(cell => cell.X == AnalysisMetric.SkyQuality)
            .ToList();
        Assert.Equal(10, row.Count);
        Assert.All(row, cell =>
        {
            Assert.Equal(0, cell.NPoints);
            Assert.Null(cell.PearsonR);
        });
    }

    [Fact]
    public void AWholeNightWithNoWeather_IsDroppedAndNothingSaysSo()
    {
        using var library = new Library();
        var target = library.AddTarget("One");

        // The 2026-09-08 shape: that night's sidecars carry no weather block at all, so six of
        // the ten X metrics are null across all 158 of its frames.
        //
        // Seen red by applying the humidity non-null predicate to the frame COUNT rather than to
        // the rows, which reads five session groups here instead of four.
        library.AddFrames(
        [
            library.Frame(frame => { Sample(frame, Night1, target, "Rig A", 40, 1.0); }),
            library.Frame(frame => { Sample(frame, Night1, target, "Rig A", 42, 1.1); }),
            library.Frame(frame => { Sample(frame, Night2, target, "Rig A", 44, 1.2); }),
            library.Frame(frame => { Sample(frame, Night2, target, "Rig A", 46, 1.3); }),
            library.Frame(frame => { Sample(frame, Night3, target, "Rig A", 0, 1.4); frame.Humidity = null; }),
            library.Frame(frame => { Sample(frame, Night3, target, "Rig A", 0, 1.5); frame.Humidity = null; }),
        ]);

        Assert.Equal(
            2,
            library.Query.Correlation(AnalysisMetric.Humidity, AnalysisMetric.Hfr, BySession).Points.Count);
        Assert.Equal(
            3,
            library.Query.Correlation(AnalysisMetric.Hfr, AnalysisMetric.Hfr, BySession).Points.Count);

        // The time series of humidity has two points where the time series of HFR has three, and
        // nothing in either result says a night was dropped.
        Assert.Equal(2, library.Query.TimeSeries(AnalysisMetric.Humidity, NoFilter).Points.Count);
        Assert.Equal(3, library.Query.TimeSeries(AnalysisMetric.Hfr, NoFilter).Points.Count);
    }

    [Fact]
    public void ExactZeroesAmongRealValues_SurviveEveryFilter()
    {
        using var library = new Library();

        // Six frames carry median_hfr exactly 0.0 on the user's own library, all failed frames
        // NINA still wrote. Nothing in analysis.py filters them, so nothing here does either.
        library.AddFrames(Enumerable.Range(0, 10)
            .Select(i => library.Frame(frame =>
            {
                Sample(frame, Night1, null, "Rig A", 40 + i, i < 6 ? 0d : 1.5 + i);
                frame.FilterUsed = "L";
            })));

        var distribution = library.Query.Distribution(AnalysisMetric.Hfr, NoFilter)!;
        Assert.Equal(10, distribution.Stats.Count);
        Assert.Equal(0d, distribution.Stats.Min);

        // The six survive into the compare box plot too, and set the group's minimum to 0.0.
        var compare = library.Query.Compare(
            AnalysisMetric.Hfr, CompareMode.Filter, "L", "L", null, null);
        Assert.Equal(CompareState.Ok, compare.State);
        Assert.Equal(0d, compare.GroupA!.Stats.Min);

        var box = Assert.Single(library.Query.BoxPlot(AnalysisMetric.Hfr, BoxPlotGrouping.Filter, NoFilter).Groups);
        Assert.Equal(10, box.Count);
    }

    // ---- 7.6 the matrix ------------------------------------------------------------------

    [Fact]
    public void TheMatrix_IsOneHundredCells_XOuterAndYInner()
    {
        using var library = new Library();
        library.AddFrames([library.Frame(frame => Sample(frame, Night1, null, "Rig A", 40, 1.5))]);

        var cells = library.Query.Matrix(NoFilter).Cells;

        // Ruling A26: ten by ten and only ten by ten. The five PHD2 metrics join the Correlation
        // X list and nothing else, which is what keeps this at a hundred.
        Assert.Equal(100, cells.Count);
        Assert.Equal((AnalysisMetric.Humidity, AnalysisMetric.Hfr), (cells[0].X, cells[0].Y));
        Assert.Equal((AnalysisMetric.Humidity, AnalysisMetric.Fwhm), (cells[1].X, cells[1].Y));
        Assert.Equal((AnalysisMetric.Humidity, AnalysisMetric.Eccentricity), (cells[2].X, cells[2].Y));
        Assert.Equal((AnalysisMetric.SensorTemp, AnalysisMetric.AduStdev), (cells[99].X, cells[99].Y));
        Assert.DoesNotContain(cells, cell => AnalysisMetrics.Phd2X.Contains(cell.X));
    }

    [Fact]
    public void TheMatrix_GatesAtExactlyTenPairedRows()
    {
        using var library = new Library();

        // Nine paired humidity and HFR rows, plus one row carrying HFR alone so the pair count
        // and the row count are different numbers.
        library.AddFrames(Enumerable.Range(0, 9)
            .Select(i => library.Frame(frame => Sample(frame, Night1, null, "Rig A", 40 + i, 1.0 + (i * 0.1)))));
        library.AddFrames([library.Frame(frame =>
        {
            Sample(frame, Night1, null, "Rig A", 0, 9.9);
            frame.Humidity = null;
        })]);

        // Seen red by writing the gate `> 10` instead of `>= 10`, which leaves the ten row case
        // below null.
        var nine = Cell(library, AnalysisMetric.Humidity, AnalysisMetric.Hfr);
        Assert.Equal(9, nine.NPoints);
        Assert.Null(nine.PearsonR);

        library.AddFrames([library.Frame(frame => Sample(frame, Night1, null, "Rig A", 49, 1.9))]);

        var ten = Cell(library, AnalysisMetric.Humidity, AnalysisMetric.Hfr);
        Assert.Equal(10, ten.NPoints);
        Assert.NotNull(ten.PearsonR);
    }

    [Theory]
    // The six literals oracle.json's matrix_constant_column publishes, plus the four the seam
    // reviewer measured. The list IS the case: a single literal is the defect, because a gate
    // built on the computational form sum(x*x) - sum(x)^2/n answers differently depending on
    // which constant the column holds (it reads 7.45e-09 for 999.99 and exactly 0.0 for 1013.2).
    [InlineData(12.7)]
    [InlineData(3.14159)]
    [InlineData(1013.2)]
    [InlineData(999.99)]
    [InlineData(0.3)]
    [InlineData(-10.0)]
    [InlineData(0.1)]
    [InlineData(1.0)]
    [InlineData(20.5)]
    [InlineData(1024.0)]
    public void TheMatrix_AnsersNoValueForAConstantColumn_AtEveryLiteral(double constant)
    {
        using var library = new Library();
        library.AddFrames(Enumerable.Range(0, 40)
            .Select(i => library.Frame(frame =>
            {
                Sample(frame, Night1, null, "Rig A", constant, 1.0 + (i * 0.01));
            })));

        // Seen red by calling Analysis.PearsonR instead of Analysis.MatrixPearson, which answers
        // exactly 0.0 here and renders as 0.00 in a coloured square, the reading spec 12.14 says
        // must not ship.
        var cell = Cell(library, AnalysisMetric.Humidity, AnalysisMetric.Hfr);
        Assert.Equal(40, cell.NPoints);
        Assert.Null(cell.PearsonR);
    }

    // ---- 7.7 the box plot's four groupings ------------------------------------------------

    [Fact]
    public void TheBoxPlot_ByEquipment_UsesTheExactSeparator_AndDropsAFrameMissingEitherName()
    {
        using var library = new Library();
        library.MapTelescope("Scope", "Raw scope");
        library.AddFrames(Enumerable.Range(0, 4)
            .Select(i => library.Frame(frame =>
            {
                Sample(frame, Night1, null, "Raw scope", 0, 1.0 + i);
                frame.Camera = "Cam";
            }))
            .Concat(Enumerable.Range(0, 4)
                .Select(i => library.Frame(frame =>
                {
                    Sample(frame, Night1, null, "Scope", 0, 2.0 + i);
                    frame.Camera = null;
                }))));

        // Line 621's separator is " + ", space plus space, exactly, and lines 619 to 620 drop a
        // frame missing either half.
        var group = Assert.Single(library.Query.BoxPlot(
            AnalysisMetric.Hfr, BoxPlotGrouping.Equipment, NoFilter).Groups);
        Assert.Equal("Scope + Cam", group.GroupName);
        Assert.Equal(4, group.Count);
    }

    [Fact]
    public void TheBoxPlot_ByMonth_ReadsCaptureDate_AndNotSessionDate()
    {
        using var library = new Library();

        // The web's own asymmetry (line 597 against line 167): the month grouping reads
        // capture_date while the date range filter reads session_date, so a frame captured after
        // UTC midnight on the last night of a month lands in the NEXT month while its own night
        // lands in the previous one. Spec 12.14 keeps it.
        //
        // Seen red by grouping on session_date, which puts all eight frames in 2026-01.
        library.AddFrames(Enumerable.Range(0, 4)
            .Select(i => library.Frame(frame =>
            {
                frame.SessionDate = new DateOnly(2026, 1, 31);
                frame.CaptureDate = new DateTime(2026, 1, 31, 23, 30, 0);
                frame.MedianHfr = 1.0 + i;
            }))
            .Concat(Enumerable.Range(0, 4)
                .Select(i => library.Frame(frame =>
                {
                    frame.SessionDate = new DateOnly(2026, 1, 31);

                    // After midnight, so a different calendar month from its own night.
                    frame.CaptureDate = new DateTime(2026, 2, 1, 0, 30, 0);
                    frame.MedianHfr = 5.0 + i;
                }))));

        var groups = library.Query.BoxPlot(AnalysisMetric.Hfr, BoxPlotGrouping.Month, NoFilter).Groups;
        Assert.Equal(["2026-01", "2026-02"], groups.Select(group => group.GroupName));
        Assert.All(groups, group => Assert.Equal(4, group.Count));

        // And a frame whose capture_date strftime cannot parse is dropped rather than grouped
        // under an empty month.
        library.AddFrameWithBrokenCaptureDate(frame => frame.MedianHfr = 9);
        Assert.Equal(2, library.Query.BoxPlot(AnalysisMetric.Hfr, BoxPlotGrouping.Month, NoFilter).Groups.Count);
    }

    [Fact]
    public void TheBoxPlot_ByTarget_MergesTwoIdsThatShareAPrimaryName()
    {
        using var library = new Library();
        // Two ids that resolve to ONE primary name. The unique index on targets.primary_name is
        // filtered on `merged_into_id IS NULL`, so this is the real shape a merge leaves behind:
        // the merged row keeps its own id on the frames that were catalogued under it and its
        // name is the survivor's.
        var first = library.AddTarget("Shared");
        var second = library.AddMergedTarget("Shared", first);

        library.AddFrames(Enumerable.Range(0, 3)
            .Select(i => library.Frame(frame => Sample(frame, Night1, first, "Rig A", 0, 1.0 + i)))
            .Concat(Enumerable.Range(0, 3)
                .Select(i => library.Frame(frame => Sample(frame, Night1, second, "Rig A", 0, 4.0 + i)))));

        // Lines 638 to 646: the merge is by NAME, not by id, so two ids resolving to one primary
        // name become one group whose count is the sum. Either id alone would be under the four
        // value gate and would be dropped.
        var group = Assert.Single(library.Query.BoxPlot(
            AnalysisMetric.Hfr, BoxPlotGrouping.Target, NoFilter).Groups);
        Assert.Equal("Shared", group.GroupName);
        Assert.Equal(6, group.Count);
    }

    [Fact]
    public void TheBoxPlot_DropsAGroupOfThree_KeepsAGroupOfFour_AndOrdersOrdinally()
    {
        using var library = new Library();

        // Names whose ordinal and culture orders differ: ordinal puts "_x" first (0x5F) then "B"
        // (0x42)... in fact ordinal is "B" (0x42), "_x" (0x5F), "a" (0x61), while the current
        // culture sorts "a", "B", "_x" on an en-US machine.
        //
        // Seen red by dropping StringComparer.Ordinal for the default comparer, which reorders
        // the three groups and gives a different answer on the user's machine than on a
        // reviewer's.
        foreach (var (name, count) in new[] { ("a", 4), ("B", 4), ("_x", 4), ("three", 3) })
        {
            library.AddFrames(Enumerable.Range(0, count)
                .Select(i => library.Frame(frame =>
                {
                    Sample(frame, Night1, null, "Rig A", 0, 1.0 + i);
                    frame.FilterUsed = name;
                })));
        }

        var groups = library.Query.BoxPlot(AnalysisMetric.Hfr, BoxPlotGrouping.Filter, NoFilter).Groups;

        // Lines 650 to 652: a group under four values is dropped with no row and no notice.
        Assert.Equal(3, groups.Count);
        Assert.DoesNotContain(groups, group => group.GroupName == "three");
        Assert.Equal(["B", "_x", "a"], groups.Select(group => group.GroupName));
    }

    // ---- 7.7a DistinctPlateScales, user ruling U3 -----------------------------------------

    [Fact]
    public void DistinctPlateScales_CountsTheDistinctNonNullScalesTheQueryRead()
    {
        using var library = new Library();
        var target = library.AddTarget("One");

        // Two rigs whose plate scales differ, the user's own shape (0.9892... and 2.9377...).
        library.AddFrames(Enumerable.Range(0, 6)
            .Select(i => library.Frame(frame =>
            {
                Sample(frame, Night1, target, "Rig A", 40 + i, 1.0 + (i * 0.1));
                frame.Camera = "Cam A";
                frame.ArcsecPerPixel = 0.9892301020408163;
            }))
            .Concat(Enumerable.Range(0, 6)
                .Select(i => library.Frame(frame =>
                {
                    Sample(frame, Night1, target, "Rig B", 50 + i, 2.0 + (i * 0.1));
                    frame.Camera = "Cam B";
                    frame.ArcsecPerPixel = 2.937713636363636;
                }))));

        AssertPlateScales(library, NoFilter, 2);

        // The filter bar narrowed to one telescope and camera.
        var oneRig = NoFilter with { Telescope = "Rig A", Camera = "Cam A" };
        AssertPlateScales(library, oneRig, 1);

        // Case 3, the one that matters: frames with a NULL plate scale join the single-scale
        // selection and the answer is STILL 1, not 2. The tempting implementation counts null as
        // a distinct value and shows the "pick one rig" warning forever on a library with one
        // telescope and a few frames whose headers lacked XPIXSZ.
        //
        // Seen red by adding ReadNullableDouble's result to the set unfiltered, which reads 2
        // here; and by counting the scales before the non-null metric predicate, which moves the
        // two-scale and one-scale answers instead.
        library.AddFrames(Enumerable.Range(0, 3)
            .Select(i => library.Frame(frame =>
            {
                Sample(frame, Night1, target, "Rig A", 60 + i, 3.0 + (i * 0.1));
                frame.Camera = "Cam A";
                frame.ArcsecPerPixel = null;
            })));
        AssertPlateScales(library, oneRig, 1);

        // No scale at all anywhere.
        using var bare = new Library();
        var bareTarget = bare.AddTarget("One");
        bare.AddFrames(Enumerable.Range(0, 6)
            .Select(i => bare.Frame(frame =>
            {
                Sample(frame, Night1, bareTarget, "Rig A", 40 + i, 1.0 + (i * 0.1));
                frame.ArcsecPerPixel = null;
            })));
        AssertPlateScales(bare, NoFilter, 0);
    }

    // ---- 7.8 Compare -----------------------------------------------------------------------

    [Fact]
    public void Compare_OnHfr_ConvertsToArcsecondsAndRoundsTheMedianToThree()
    {
        using var library = new Library();
        SeedCompareRigs(library, scaleB: 2.0);

        var result = library.Query.Compare(
            AnalysisMetric.Hfr, CompareMode.Equipment, "Rig A|||Cam A", "Rig B|||Cam B", null, null);

        Assert.Equal(CompareState.Ok, result.State);
        Assert.NotNull(result.GroupA);
        Assert.NotNull(result.GroupB);
        Assert.True(result.Comparable);

        // Group A: HFR 1.0 to 1.3, scale 1.0, arcsec median 1.15. Group B: HFR 1.0 to 1.3,
        // scale 2.0, arcsec median 2.3. Three decimals here, not six (lines 911 and 912).
        Assert.Equal(1.15, result.MedianArcsecA);
        Assert.Equal(2.3, result.MedianArcsecB);
        Assert.Equal(
            "Rig A|||Cam A has 50% lower median (arcsec) than Rig B|||Cam B (N=4 vs N=4)",
            result.Verdict);

        // Both names are carried whatever the state, and the pixel boxes are over the pixel
        // values, which are identical in the two groups.
        Assert.Equal("Rig A|||Cam A", result.NameA);
        Assert.Equal(result.GroupA!.Box.Median, result.GroupB!.Box.Median);
    }

    [Fact]
    public void Compare_WithThreePlateScaledFramesInOneGroup_IsNotComparable_AndHasNoVerdict()
    {
        using var library = new Library();
        SeedCompareRigs(library, scaleB: 2.0);
        library.ClearOnePlateScale("Rig B");

        var result = library.Query.Compare(
            AnalysisMetric.Hfr, CompareMode.Equipment, "Rig A|||Cam A", "Rig B|||Cam B", null, null);

        // Frames with no plate scale still contribute to both box plots and both stats cards
        // (line 841's own comment says so), and only to those.
        Assert.Equal(CompareState.Ok, result.State);
        Assert.Equal(4, result.GroupB!.Stats.Count);
        Assert.False(result.Comparable);
        Assert.Null(result.MedianArcsecA);
        Assert.Null(result.MedianArcsecB);

        // Null exactly when Comparable is false: the web builds a sentence here that its own
        // frontend discards and composes again itself.
        Assert.Null(result.Verdict);
    }

    [Fact]
    public void Compare_OnANonPixelMetric_IsComparableWithNoArcsecondMedians()
    {
        using var library = new Library();
        SeedCompareRigs(library, scaleB: 2.0);

        // Ruling A18: _PIXEL_METRICS holds hfr alone and is read at line 843 and nowhere else.
        // fwhm is already in arcseconds, so scaling it would be a double conversion.
        var result = library.Query.Compare(
            AnalysisMetric.Fwhm, CompareMode.Equipment, "Rig A|||Cam A", "Rig B|||Cam B", null, null);

        Assert.True(result.Comparable);
        Assert.Null(result.MedianArcsecA);
        Assert.Null(result.MedianArcsecB);
        Assert.NotNull(result.Verdict);
        Assert.DoesNotContain("arcsec", result.Verdict!, StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_WithAGroupThatDoesNotSplitOnTheSeparator_ReadsNoRowForThatGroup()
    {
        using var library = new Library();
        SeedCompareRigs(library, scaleB: 2.0);
        var b = "Rig B" + CompareGroups.EquipmentSeparator + "Cam B";

        // DEPARTURE from line 860's `if len(parts) == 2`, task 6 review escalation 4. The web
        // falls through to a filter with NO equipment predicate, so the group silently becomes the
        // whole library: both groups then read the same rows and the tab prints "Both groups have
        // identical median values", a confident, plausible, wrong answer with no empty state, no
        // exception and no log line. The port fails closed instead.
        //
        // Input one, a separator that drifted between the App's picker and this query, which is
        // now impossible because CompareGroups.EquipmentSeparator is the one home for it.
        var drifted = library.Query.Compare(
            AnalysisMetric.Hfr, CompareMode.Equipment, "Rig A###Cam A", b, null, null);
        Assert.Equal(0, drifted.CountA);
        Assert.Equal(CompareState.GroupAShort, drifted.State);

        // Input two, and it is the trust boundary: a canonical telescope name that itself carries
        // the separator splits into three parts. The name came out of the user's own file headers.
        var embedded = library.Query.Compare(
            AnalysisMetric.Hfr,
            CompareMode.Equipment,
            "Rig" + CompareGroups.EquipmentSeparator + "A" + CompareGroups.EquipmentSeparator + "Cam A",
            b,
            null,
            null);
        Assert.Equal(0, embedded.CountA);

        // Both malformed is the no-rows state and never eight rows read twice.
        var both = library.Query.Compare(
            AnalysisMetric.Hfr, CompareMode.Equipment, "Rig A", "Rig B", null, null);
        Assert.Equal(CompareState.NoRows, both.State);
        Assert.Equal(0, both.CountA);
        Assert.Equal(0, both.CountB);

        // The well-formed pair still reads its own four rows each, so the guard closed the hole
        // and nothing else: the shared filter bar is not applied here at all (ruling A17), the
        // signature taking only the dates.
        //
        // Seen red by handing Compare the shared filter, which empties a comparison the user set
        // up correctly the moment the bar names one rig.
        var wellFormed = library.Query.Compare(
            AnalysisMetric.Hfr,
            CompareMode.Equipment,
            "Rig A" + CompareGroups.EquipmentSeparator + "Cam A",
            b,
            null,
            null);
        Assert.Equal(4, wellFormed.CountA);
        Assert.Equal(4, wellFormed.CountB);
    }

    // ---- 7.7b BoxPlotResult.RowCount ---------------------------------------------------------

    [Fact]
    public void TheBoxPlot_RowCount_CountsEveryRowRead_IncludingThoseOfGroupsItLaterDrops()
    {
        using var library = new Library();

        // Thirty rows across twelve groups, every one of them under the four value gate, which is
        // spec 12.14's "Box plot with no group left": rows matched and nothing can be drawn. The
        // tab cannot tell that from "no row matches the filters" without this figure, and the two
        // rows call for opposite actions.
        var names = Enumerable.Range(0, 12).Select(i => $"G{i:00}").ToList();
        foreach (var (name, count) in names.Select((name, i) => (name, i < 6 ? 3 : 2)))
        {
            library.AddFrames(Enumerable.Range(0, count)
                .Select(i => library.Frame(frame =>
                {
                    Sample(frame, Night1, null, "Rig A", 0, 1.0 + i);
                    frame.FilterUsed = name;
                })));
        }

        var dropped = library.Query.BoxPlot(AnalysisMetric.Hfr, BoxPlotGrouping.Filter, NoFilter);

        Assert.Empty(dropped.Groups);
        Assert.Equal(30, dropped.RowCount);

        // Counted BEFORE the grouping key and never after, exactly as DistinctPlateScales is, so
        // the figure does not move with the grouping the reader picked. By Target drops every one
        // of these rows for want of a resolved target and still reports the same thirty.
        var byTarget = library.Query.BoxPlot(AnalysisMetric.Hfr, BoxPlotGrouping.Target, NoFilter);
        Assert.Empty(byTarget.Groups);
        Assert.Equal(30, byTarget.RowCount);
    }

    [Fact]
    public void TheBoxPlot_RowCount_IsZeroWhenNoRowMatches_AndCountsTheRowsBehindADrawnGroup()
    {
        using var library = new Library();

        Assert.Equal(0, library.Query.BoxPlot(AnalysisMetric.Hfr, BoxPlotGrouping.Filter, NoFilter).RowCount);

        // Four rows in one group that is drawn, plus three in one that is dropped: the figure is
        // over every row read and not over the rows the boxes ended up holding.
        foreach (var (name, count) in new[] { ("kept", 4), ("dropped", 3) })
        {
            library.AddFrames(Enumerable.Range(0, count)
                .Select(i => library.Frame(frame =>
                {
                    Sample(frame, Night1, null, "Rig A", 0, 1.0 + i);
                    frame.FilterUsed = name;
                })));
        }

        var result = library.Query.BoxPlot(AnalysisMetric.Hfr, BoxPlotGrouping.Filter, NoFilter);

        Assert.Equal("kept", Assert.Single(result.Groups).GroupName);
        Assert.Equal(7, result.RowCount);
    }

    [Theory]
    [InlineData(3, 10, CompareState.GroupAShort)]
    [InlineData(10, 3, CompareState.GroupBShort)]
    [InlineData(3, 3, CompareState.BothShort)]
    [InlineData(0, 0, CompareState.NoRows)]
    [InlineData(4, 4, CompareState.Ok)]
    public void Compare_NeverReturnsNull_AndNamesWhichGroupIsShort(int a, int b, CompareState expected)
    {
        using var library = new Library();
        Seed(library, "A", a);
        Seed(library, "B", b);

        // Ruling S7: the web's HTTP 400 (line 883) becomes a state with both RAW counts, because
        // 12.14's States table requires the tab to say WHICH group is short and to tell that
        // apart from "no row matches the filters", which is its own row in the same table.
        //
        // Seen red by collapsing the four thin states into one, which makes GroupAShort and
        // NoRows indistinguishable and leaves the tab with nothing to print.
        var result = library.Query.Compare(AnalysisMetric.Hfr, CompareMode.Filter, "A", "B", null, null);

        Assert.Equal(expected, result.State);
        Assert.Equal(a, result.CountA);
        Assert.Equal(b, result.CountB);

        // The names are carried whatever the state, so the tab can name a group whose box it
        // cannot draw.
        Assert.Equal("A", result.NameA);
        Assert.Equal("B", result.NameB);

        if (expected == CompareState.Ok)
        {
            Assert.NotNull(result.GroupA);
            Assert.NotNull(result.GroupB);
        }
        else
        {
            Assert.Null(result.GroupA);
            Assert.Null(result.GroupB);
            Assert.Null(result.Verdict);
            Assert.False(result.Comparable);
        }

        static void Seed(Library library, string filter, int count)
            => library.AddFrames(Enumerable.Range(0, count)
                .Select(i => library.Frame(frame =>
                {
                    Sample(frame, Night1, null, "Rig A", 0, 1.0 + (i * 0.1));
                    frame.FilterUsed = filter;
                    frame.ArcsecPerPixel = 1.0;
                })));
    }

    // ---- 7.9 the cap boundary, end to end --------------------------------------------------

    [Fact]
    public void AtFiveThousandAndOnePoints_TheTrendAndBothStatsCardsCoverAllOfThem()
    {
        using var library = new Library();
        library.AddFrames(Enumerable.Range(0, 5001)
            .Select(i => library.Frame(frame =>
                Sample(frame, Night1, null, "Rig A", 40 + (i % 97) * 0.25, 1.0 + (i % 53) * 0.03))));

        var result = library.Query.Correlation(AnalysisMetric.Humidity, AnalysisMetric.Hfr, NoFilter);

        Assert.Equal(5001, result.TotalCount);
        Assert.Equal(5000, result.SampledCount);
        Assert.Equal(5000, result.Points.Count);

        // The trend and the two stats cards are computed over ALL 5,001, before any downsampling,
        // which lines 451 to 455 say in a comment of their own. Computing them over the sampled
        // points is the single most tempting simplification in this method and it changes every
        // figure on the tab.
        //
        // Seen red by moving the Downsample call above the Trend call, which fails the two
        // equalities below.
        var xs = Enumerable.Range(0, 5001).Select(i => 40 + (i % 97) * 0.25).ToList();
        var ys = Enumerable.Range(0, 5001).Select(i => 1.0 + (i % 53) * 0.03).ToList();

        // Field by field, not record by record: a positional record compares its two band list
        // members by reference, so two TrendLine instances holding equal bands are never equal.
        var expected = Analysis.Trend(xs, ys)!;
        Assert.Equal(expected.Slope, result.Trend!.Slope);
        Assert.Equal(expected.Intercept, result.Trend.Intercept);
        Assert.Equal(expected.RSquared, result.Trend.RSquared);
        Assert.Equal(expected.PearsonR, result.Trend.PearsonR);
        Assert.Equal(expected.SpearmanRho, result.Trend.SpearmanRho);
        Assert.Equal(
            expected.ConfidenceUpper.Select(band => (band.X, band.Y)),
            result.Trend.ConfidenceUpper.Select(band => (band.X, band.Y)));
        Assert.Equal(Analysis.Summary(xs), result.XStats);
        Assert.Equal(Analysis.Summary(ys), result.YStats);

        // A count regression guard and NOT a boundary proof: with the cap written >= instead of
        // >, Downsample at exactly 5,000 computes step 1.0 and returns the same 5,000 points in
        // the same order, so both assertions below still hold. The > against >= boundary is
        // observable only at the Core level, where task2.md case 7.9 pins it with Assert.Same.
        library.DeleteOneFrame();
        var atCap = library.Query.Correlation(AnalysisMetric.Humidity, AnalysisMetric.Hfr, NoFilter);
        Assert.Equal(5000, atCap.TotalCount);
        Assert.Equal(5000, atCap.SampledCount);
    }

    // ---- the two picker lists ---------------------------------------------------------------

    [Fact]
    public void TheFilterList_IsRawStoredValues_AndTheEquipmentListComesFromTheStatsMemo()
    {
        using var library = new Library();
        library.MapFilter("Ha", "H-alpha");
        library.AddFrames(
        [
            library.Frame(frame => { frame.FilterUsed = "H-alpha"; frame.Telescope = "T"; frame.Camera = "C"; }),
            library.Frame(frame => { frame.FilterUsed = "Ha"; frame.Telescope = "T"; frame.Camera = "C"; }),
            library.Frame(frame => { frame.FilterUsed = null; frame.Telescope = "T"; frame.Camera = "C"; }),
        ]);

        // Lines 335 to 348: raw stored values, ordered by the value, nulls excluded, and NOT
        // folded through the alias map. The box plot's own By Filter grouping DOES fold, so the
        // two lists differ on this library, which is the web's behaviour and not a defect.
        Assert.Equal(["H-alpha", "Ha"], library.Query.Filters());

        var combination = Assert.Single(library.Query.EquipmentCombinations());
        Assert.Equal("T", combination.Telescope);
        Assert.Equal("C", combination.Camera);
        Assert.False(combination.Grouped);
    }

    // ---- 7.1 and 7.5a, the real library ------------------------------------------------------

    [Fact]
    public void RealLibrary_Correlation_AgreesWithTheOracleOnEveryFigure()
    {
        using var library = new Library();
        var rows = RealData.Seed(library);

        // The exported id sequence this class read, recorded in the report: the seeded ids are
        // sequential in inputs.json's own row order, which IS the port order
        // (ORDER BY session_date, id), so the port and the oracle walk one list.
        output.WriteLine($"port order rows: {rows}");

        var frame = library.Query.Correlation(
            AnalysisMetric.Humidity, AnalysisMetric.Hfr, NoFilter);
        RealData.AssertCorrelation(frame, "correlation_humidity_hfr_frame_port_order");

        var session = library.Query.Correlation(
            AnalysisMetric.Humidity, AnalysisMetric.Hfr, BySession);
        RealData.AssertCorrelation(session, "correlation_humidity_hfr_session_port_order");

        // Two of the three nights carry two targets, so the five session groups are not five
        // nights; humidity is null across one whole night, so its session count is four.
        Assert.Equal(4, session.Points.Count);

        var phd2 = library.Query.Correlation(
            AnalysisMetric.Phd2RmsTotal, AnalysisMetric.Hfr, NoFilter);
        RealData.AssertCorrelation(phd2, "correlation_phd2_rms_total_hfr_frame_port_order");

        // S8: the PHD2 X metrics are a five valued step function over 416 frames, so the scatter
        // is five vertical stacks and the flagging finds seven outliers.
        Assert.Equal(5, phd2.Points.Select(point => point.X).Distinct().Count());
        Assert.Equal(7, phd2.Points.Count(point => point.Outlier));

        // U3: both plate scales are in the unfiltered point set.
        Assert.Equal(2, frame.DistinctPlateScales);
        Assert.Equal(2, phd2.DistinctPlateScales);
    }

    [Fact]
    public void RealLibrary_DistributionAndBoxPlotAndTimeSeries_AgreeWithTheOracle()
    {
        using var library = new Library();
        RealData.Seed(library);

        var histogram = library.Query.Distribution(AnalysisMetric.Hfr, NoFilter)!;
        RealData.AssertDistribution(histogram, "distribution_hfr_frame_port_order");

        // Defect D1, the departure user ruling U1 took, with the web's own figure recorded beside
        // the shipped one so a later reader can see exactly what was departed from and by how
        // much: the web's accumulated last edge is 2.6244999999999994 rather than v_max 2.6245,
        // so the single frame at the maximum falls in no bin at all and its counts sum to 415 of
        // 416. The port's sum to 416 and the last bin reads 1 where the web's reads 0.
        Assert.Equal(415, RealData.Figure("distribution_hfr_frame_port_order", "bin_count_sum"));
        Assert.Equal(416, histogram.Bins.Sum(bin => bin.Count));
        Assert.Equal(1, histogram.Bins[^1].Count);
        Assert.Equal(histogram.Stats.Max, histogram.Bins[^1].BinEnd);

        RealData.AssertDistribution(
            library.Query.Distribution(AnalysisMetric.Hfr, BySession)!, "distribution_hfr_session_port_order");

        RealData.AssertBoxPlot(
            library.Query.BoxPlot(AnalysisMetric.Hfr, BoxPlotGrouping.Filter, NoFilter),
            "boxplot_hfr_by_filter_port_order");
        RealData.AssertBoxPlot(
            library.Query.BoxPlot(AnalysisMetric.Hfr, BoxPlotGrouping.Month, NoFilter),
            "boxplot_hfr_by_month_port_order");
        RealData.AssertBoxPlot(
            library.Query.BoxPlot(AnalysisMetric.Hfr, BoxPlotGrouping.Target, NoFilter),
            "boxplot_hfr_by_target_port_order");

        // The oracle names the equipment groups by rig ordinal, while the port's key is the
        // "telescope + camera" pair line 621 builds, so this one compares the figures by
        // position and pins the two constructed keys beside them.
        var equipment = library.Query.BoxPlot(AnalysisMetric.Hfr, BoxPlotGrouping.Equipment, NoFilter);
        Assert.Equal(["rig 1 + cam 1", "rig 2 + cam 2"], equipment.Groups.Select(group => group.GroupName));
        RealData.AssertBoxes(equipment.Groups, "boxplot_hfr_by_equipment_port_order", compareNames: false);

        RealData.AssertTimeSeries(
            library.Query.TimeSeries(AnalysisMetric.Hfr, NoFilter), "timeseries_hfr_port_order");
        RealData.AssertTimeSeries(
            library.Query.TimeSeries(AnalysisMetric.Humidity, NoFilter), "timeseries_humidity_port_order");
    }

    [Fact]
    public void RealLibrary_MatrixAndCompare_AgreeWithTheOracle()
    {
        using var library = new Library();
        RealData.Seed(library);

        RealData.AssertMatrix(library.Query.Matrix(NoFilter), "matrix_10x10_port_order");

        // S5, and it is the row that matters most: raw pixel HFR says rig 2 is the better rig
        // (median 1.3607 against 1.7913) and the arcsecond conversion says the opposite (3.997
        // against 1.772). A port that forgot the conversion would report the user's own two rigs
        // backwards, and every other tab would still look right.
        var rigs = library.Query.Compare(
            AnalysisMetric.Hfr, CompareMode.Equipment, "rig 1|||cam 1", "rig 2|||cam 2", null, null);
        Assert.Equal(CompareState.Ok, rigs.State);
        Assert.True(rigs.Comparable);
        Assert.Equal(1.772, rigs.MedianArcsecA);
        Assert.Equal(3.997, rigs.MedianArcsecB);
        Assert.Equal(1.7913, rigs.GroupA!.Box.Median);
        Assert.Equal(1.3607, rigs.GroupB!.Box.Median);

        // User ruling U2's 56, with the web's own 126 recorded beside it: the web divides by the
        // first median whatever the sentence says, so it prints "126% lower", which is not a
        // thing. oracle.json's pct_web_rounded is 126.0 and its pct_u2_rounded is 56.0.
        Assert.Equal(
            "rig 1|||cam 1 has 56% lower median (arcsec) than rig 2|||cam 2 (N=308 vs N=108)",
            rigs.Verdict);

        var fwhm = library.Query.Compare(
            AnalysisMetric.Fwhm, CompareMode.Equipment, "rig 1|||cam 1", "rig 2|||cam 2", null, null);
        Assert.True(fwhm.Comparable);
        Assert.Null(fwhm.MedianArcsecA);
        Assert.Equal(3.301721, fwhm.GroupA!.Box.Median);
        Assert.Equal(6.978465, fwhm.GroupB!.Box.Median);
        Assert.Equal(
            "rig 1|||cam 1 has 53% lower median than rig 2|||cam 2 (N=301 vs N=108)",
            fwhm.Verdict);

        // Filter mode: the group string IS the name, so the oracle's own sentence matches to the
        // character, with U2's 50 where the web's own figure is 100.
        var filters = library.Query.Compare(
            AnalysisMetric.Hfr, CompareMode.Filter, "Ha", "Oiii", null, null);
        Assert.Equal(1.944, filters.MedianArcsecA);
        Assert.Equal(3.879, filters.MedianArcsecB);
        Assert.Equal(54, filters.CountA);
        Assert.Equal(79, filters.CountB);
        Assert.Equal(
            "Ha has 50% lower median (arcsec) than Oiii (N=54 vs N=79)",
            filters.Verdict);
    }

    // ---- 7.11 two hundred thousand rows, timed -------------------------------------------

    [Fact]
    public void TwoHundredThousandRows_StayInsideTheBudgets()
    {
        using var library = new Library();
        library.AddFramesFast(200_000, (frame, i) =>
        {
            // A mix of LIGHT and calibration, so the image_type predicate is under real pressure.
            frame.ImageType = i % 10 == 0 ? "DARK" : "LIGHT";
            frame.SessionDate = Night1.AddDays(i % 400);
            frame.CaptureDate = frame.SessionDate!.Value.ToDateTime(new TimeOnly(22, 0)).AddSeconds(i % 3600);
            frame.Telescope = i % 2 == 0 ? "Rig A" : "Rig B";
            frame.Camera = i % 2 == 0 ? "Cam A" : "Cam B";
            frame.ArcsecPerPixel = i % 2 == 0 ? 0.62 : 1.58;
            frame.MedianHfr = 1.0 + (i % 211) * 0.01;

            // Sparsely populated, so the non-null predicates and the matrix's per-pair gating are
            // under real pressure rather than reading a full table.
            frame.Humidity = i % 3 == 0 ? 40 + (i % 57) * 0.5 : null;
            frame.Fwhm = i % 5 == 0 ? 2.0 + (i % 31) * 0.05 : null;
            frame.Eccentricity = i % 7 == 0 ? 0.3 + (i % 11) * 0.01 : null;
            frame.Airmass = 1.0 + (i % 23) * 0.02;
            frame.SensorTemp = -10;
        });

        var frame = BestOfThree("Correlation, frame granularity", () =>
            library.Query.Correlation(AnalysisMetric.Humidity, AnalysisMetric.Hfr, NoFilter));
        var session = BestOfThree("Correlation, session granularity", () =>
            library.Query.Correlation(AnalysisMetric.Humidity, AnalysisMetric.Hfr, BySession));
        var matrix = BestOfThree("Matrix", () => library.Query.Matrix(NoFilter));
        var series = BestOfThree("TimeSeries", () => library.Query.TimeSeries(AnalysisMetric.Hfr, NoFilter));

        // One further full scan of the filtered rows, which is what the hundred statement shape
        // (one corr() per pair) would pay a hundred times over. The matrix budget is asserted to
        // exclude that shape rather than merely to be a number.
        var scan = BestOfThree("One full scan of the filtered rows", library.FullScan);
        output.WriteLine($"one hundred scans: {scan * 100} ms, the shape the matrix budget excludes");

        // All three budgets are MULTIPLES of this case's own one scan figure, measured in the same
        // case on the same machine under the same load, and not absolute milliseconds. An absolute
        // figure measures the machine as much as the code: the same three measurements are 20 to
        // 30 per cent apart between two runs on one machine with other agents active, so an
        // absolute budget either flakes or excludes nothing. The ratios are stable across both
        // readings taken so far (1.08, 1.05 and 1.63, then 1.08, 1.12 and 1.58). The absolute
        // figures stay in output.WriteLine above as the baseline for the next phase.
        //
        // Four scans is also what ties the matrix budget to the defect it excludes: the hundred
        // statement shape, one corr() per pair, costs one hundred scans, so a budget of four
        // excludes it by a factor of twenty five whatever the machine does that afternoon.
        Assert.True(
            frame < scan * 3,
            $"Correlation at frame granularity took {frame} ms against a {scan} ms scan, which is "
            + "three scans or more.");
        Assert.True(
            session < scan * 3,
            $"Correlation at session granularity took {session} ms against a {scan} ms scan, "
            + "which is three scans or more.");
        Assert.True(
            matrix < scan * 4,
            $"Matrix took {matrix} ms against a {scan} ms scan, which is four scans or more: the "
            + "hundred statement shape costs one hundred scans and this budget exists to exclude "
            + "it. That is an escalation, not a loosening.");

        // TimeSeries carries no asserted budget: it names no defect and could not be shown to
        // exclude one, which is what a budget is for. The figure is recorded as a baseline.
        output.WriteLine($"TimeSeries baseline: {series} ms, unasserted");
    }

    private long BestOfThree(string label, Action run)
    {
        var best = long.MaxValue;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var clock = Stopwatch.StartNew();
            run();
            clock.Stop();
            output.WriteLine($"{label}, attempt {attempt + 1}: {clock.ElapsedMilliseconds} ms");
            best = Math.Min(best, clock.ElapsedMilliseconds);
        }

        output.WriteLine($"{label}, best of three: {best} ms");
        return best;
    }

    // ---- shared helpers -------------------------------------------------------------------

    // AnalysisMetrics.Column is internal, which is the point: nothing outside the query has any
    // business with a column name. This project already sees the Data assembly's internals.
    private static string Column(AnalysisMetric metric) => AnalysisMetrics.Column(metric);

    private static void Sample(
        Image frame, DateOnly night, Guid? target, string telescope, double x, double y)
    {
        frame.SessionDate = night;
        frame.CaptureDate = night.ToDateTime(new TimeOnly(22, 0));
        frame.ResolvedTargetId = target;
        frame.Telescope = telescope;
        frame.Camera = "Cam";
        frame.Humidity = x;
        frame.MedianHfr = y;
        frame.Fwhm = y;
        frame.ArcsecPerPixel = 1.0;
    }

    private static MatrixCell Cell(Library library, AnalysisMetric x, AnalysisMetric y)
        => Assert.Single(
            library.Query.Matrix(NoFilter).Cells,
            cell => cell.X == x && cell.Y == y);

    private static void AssertPlateScales(Library library, AnalysisFilter filter, int expected)
    {
        // FIVE results carry it (ruling S4): the four an earlier draft named, plus Time Series,
        // whose field would otherwise have been declared and never populated.
        Assert.Equal(expected, library.Query.Correlation(AnalysisMetric.Humidity, AnalysisMetric.Hfr, filter).DistinctPlateScales);
        Assert.Equal(expected, library.Query.Distribution(AnalysisMetric.Hfr, filter)!.DistinctPlateScales);
        Assert.Equal(expected, library.Query.BoxPlot(AnalysisMetric.Hfr, BoxPlotGrouping.Filter, filter).DistinctPlateScales);
        Assert.Equal(expected, library.Query.TimeSeries(AnalysisMetric.Hfr, filter).DistinctPlateScales);
        Assert.Equal(expected, library.Query.Matrix(filter).DistinctPlateScales);
    }

    private static void SeedCompareRigs(Library library, double scaleB)
    {
        library.AddFrames(Enumerable.Range(0, 4)
            .Select(i => library.Frame(frame =>
            {
                Sample(frame, Night1, null, "Rig A", 0, 1.0 + (i * 0.1));
                frame.Camera = "Cam A";
                frame.ArcsecPerPixel = 1.0;
            }))
            .Concat(Enumerable.Range(0, 4)
                .Select(i => library.Frame(frame =>
                {
                    Sample(frame, Night1, null, "Rig B", 0, 1.0 + (i * 0.1));
                    frame.Camera = "Cam B";
                    frame.ArcsecPerPixel = scaleB;
                }))));
    }

    // ---- the real library, from the repository ------------------------------------------

    /// <summary>
    /// The user's own 416 LIGHT frames and five PHD2 night rows, seeded from
    /// <c>tests/Fixtures/realdata/inputs.json</c> and asserted against
    /// <c>oracle.json</c> beside it, both read from the repository so every case runs on a clean
    /// copy. <c>C:\tmp\p17-real</c> is never read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Night, target and rig are ORDINALS in both files, so a case seeds "rig 1" and
    /// "target 2" and asserts figures and structure, never a rig, target, filter or place. The
    /// three night ordinals become three dates in the months <c>inputs.json</c> records, so the
    /// month boundaries and the box plot's month groups are the oracle's own strings.
    /// </para>
    /// <para>
    /// Ids are sequential in the file's row order, which IS the port order
    /// (<c>ORDER BY session_date, id</c>, <c>id</c> ordinal), so the port and the oracle walk one
    /// list and the parity statement is honest: the port's figures equal the Python's figures
    /// OVER THE SAME SEQUENCE OF ROWS. Comparing against a run of the real web server over a
    /// PostgreSQL that returned some other order would be comparing two different inputs and
    /// calling the difference a defect. The assertions read the <c>_port_order</c> scenarios;
    /// <c>oracle.json</c>'s own <c>order_agreement</c> reports that all 16 pairs agree on every
    /// figure, so either member would serve.
    /// </para>
    /// <para>
    /// The five PHD2 night rows are seeded as ONE session per (night, rig) at a frame count of
    /// 1024 carrying the night's own unrounded figure, because
    /// <c>sqrt(((v * v) * 1024) / 1024)</c> is exactly <c>v</c> in IEEE 754 at these magnitudes:
    /// the power of two makes both the scaling and the division exact and the square root of a
    /// square recovers its argument. The weighted accumulation over 937 real sessions is
    /// <c>Phd2Metrics</c>'s own and Phase 15B pins it; what this case pins is the join, the rig
    /// resolution and the plumbing of the figure onto the X axis.
    /// </para>
    /// </remarks>
    private static class RealData
    {
        private static readonly DateOnly[] Nights =
            [new(2026, 7, 1), new(2026, 7, 2), new(2026, 9, 1)];

        private static readonly JsonElement Inputs = Load("inputs.json");

        private static readonly JsonElement Scenarios = Load("oracle.json").GetProperty("scenarios");

        public static int Seed(Library library)
        {
            var rows = Inputs.GetProperty("rows");
            var columns = Inputs.GetProperty("columns").EnumerateArray().Select(c => c.GetString()!).ToList();

            for (var ordinal = 1; ordinal <= 3; ordinal++)
            {
                library.AddTarget(TargetId(ordinal), $"target {ordinal}");
            }

            var frames = new List<Image>(rows.GetArrayLength());
            foreach (var row in rows.EnumerateArray())
            {
                var night = Nights[row.GetProperty("night").GetInt32() - 1];
                var rig = row.GetProperty("rig").GetInt32();

                frames.Add(library.Frame(frame =>
                {
                    frame.SessionDate = night;
                    frame.CaptureDate = night.ToDateTime(new TimeOnly(22, 0));
                    frame.ResolvedTargetId = TargetId(row.GetProperty("target").GetInt32());
                    frame.Telescope = $"rig {rig}";
                    frame.Camera = $"cam {rig}";
                    frame.FilterUsed = row.GetProperty("filter").GetString();
                    frame.ArcsecPerPixel = Number(row, "arcsec_per_pixel");
                    frame.ExposureTime = Number(row, "exposure_time");
                    frame.Humidity = Number(row, "humidity");
                    frame.WindSpeed = Number(row, "wind_speed");
                    frame.AmbientTemp = Number(row, "ambient_temp");
                    frame.DewPoint = Number(row, "dew_point");
                    frame.Pressure = Number(row, "pressure");
                    frame.CloudCover = Number(row, "cloud_cover");
                    frame.SkyQuality = Number(row, "sky_quality");
                    frame.FocuserTemp = Number(row, "focuser_temp");
                    frame.Airmass = Number(row, "airmass");
                    frame.SensorTemp = Number(row, "sensor_temp");
                    frame.MedianHfr = Number(row, "median_hfr");
                    frame.Fwhm = Number(row, "fwhm");
                    frame.Eccentricity = Number(row, "eccentricity");
                    frame.GuidingRmsArcsec = Number(row, "guiding_rms_arcsec");
                    frame.GuidingRmsRaArcsec = Number(row, "guiding_rms_ra_arcsec");
                    frame.GuidingRmsDecArcsec = Number(row, "guiding_rms_dec_arcsec");
                    frame.DetectedStars = (int?)Number(row, "detected_stars");
                    frame.AduMean = Number(row, "adu_mean");
                    frame.AduMedian = Number(row, "adu_median");
                    frame.AduStdev = Number(row, "adu_stdev");
                }));
            }

            Assert.Equal(28, columns.Count);
            library.AddFrames(frames);

            foreach (var entry in Inputs.GetProperty("phd2_nights").EnumerateArray())
            {
                var rig = entry.GetProperty("rig").GetInt32();
                var night = Nights[entry.GetProperty("night").GetInt32() - 1];
                library.MapProfile($"profile {rig}", $"rig {rig}");
                library.AddPhd2Session(session =>
                {
                    session.EquipmentProfile = $"profile {rig}";

                    // The state the user's own library is in: the stored column is NULL on every
                    // row, because the port resolves the rig live and never writes it back.
                    session.Telescope = null;
                    session.SessionDate = night;
                    session.FrameCount = 1024;
                    session.RmsTotalArcsec = entry.GetProperty("phd2_rms_total").GetDouble();
                    session.RmsRaArcsec = entry.GetProperty("phd2_rms_ra").GetDouble();
                    session.RmsDecArcsec = entry.GetProperty("phd2_rms_dec").GetDouble();
                });
            }

            return frames.Count;
        }

        public static void AssertCorrelation(CorrelationResult result, string scenario)
        {
            var expected = Scenarios.GetProperty(scenario);

            Assert.Equal(expected.GetProperty("total_count").GetInt32(), result.TotalCount);
            Assert.Equal(expected.GetProperty("sampled_count").GetInt32(), result.SampledCount);
            Assert.Equal(
                expected.GetProperty("outlier_count").GetInt32(),
                result.Points.Count(point => point.Outlier));

            AssertStats(expected.GetProperty("x_stats"), result.XStats);
            AssertStats(expected.GetProperty("y_stats"), result.YStats);
            AssertTrend(expected.GetProperty("trend"), result.Trend);

            Assert.Equal(
                expected.GetProperty("target_names").EnumerateObject()
                    .Select(entry => entry.Value.GetString()!).Order(),
                result.TargetNames.Values.Order());

            if (expected.TryGetProperty("points", out var all))
            {
                AssertPoints(all, result.Points);
                return;
            }

            // Point arrays longer than twelve are not published in full: the scenario carries the
            // first and last five beside the counts.
            AssertPoints(expected.GetProperty("points_sample_first_5"), result.Points.Take(5).ToList());
            AssertPoints(
                expected.GetProperty("points_sample_last_5"),
                result.Points.Skip(result.Points.Count - 5).ToList());
        }

        public static void AssertDistribution(DistributionResult result, string scenario)
        {
            var expected = Scenarios.GetProperty(scenario);

            AssertStats(expected.GetProperty("stats"), result.Stats);
            Assert.Equal(expected.GetProperty("skewness").GetDouble(), result.Skewness);

            // The port ships user ruling U1, so it is u1_bins that must match: every value is
            // counted and the last bin's upper edge is v_max. The web's own figure is recorded
            // beside it, which is what makes this a departure and not a silent improvement: its
            // bin_count_sum is 415 of 416, because the accumulated last edge 2.6244999999999994
            // is not 2.6245, so the frame at the maximum falls in no bin at all.
            var u1 = expected.GetProperty("u1_bins").EnumerateArray().ToList();
            Assert.Equal(u1.Count, result.Bins.Count);
            for (var i = 0; i < u1.Count; i++)
            {
                Assert.Equal(u1[i].GetProperty("bin_start").GetDouble(), result.Bins[i].BinStart);
                Assert.Equal(u1[i].GetProperty("bin_end").GetDouble(), result.Bins[i].BinEnd);
                Assert.Equal(u1[i].GetProperty("count").GetInt32(), result.Bins[i].Count);
            }

            // U1's whole point: the counts sum to every value read. The web's own sum is recorded
            // beside it and can only be smaller, because its accumulated last edge either equals
            // v_max or falls just short of it and loses the frame sitting on the maximum.
            Assert.Equal(
                expected.GetProperty("u1_bin_count_sum").GetInt32(),
                result.Bins.Sum(bin => bin.Count));
            Assert.Equal(
                expected.GetProperty("value_count").GetInt32(),
                result.Bins.Sum(bin => bin.Count));
            Assert.True(
                expected.GetProperty("bin_count_sum").GetInt32()
                    <= expected.GetProperty("u1_bin_count_sum").GetInt32());
        }

        public static void AssertBoxPlot(BoxPlotResult result, string scenario)
            => AssertBoxes(result.Groups, scenario, compareNames: true);

        public static void AssertBoxes(
            IReadOnlyList<BoxPlot> groups, string scenario, bool compareNames)
        {
            var expected = Scenarios.GetProperty(scenario).GetProperty("groups").EnumerateArray().ToList();
            Assert.Equal(expected.Count, groups.Count);

            for (var i = 0; i < expected.Count; i++)
            {
                if (compareNames)
                {
                    Assert.Equal(expected[i].GetProperty("group_name").GetString(), groups[i].GroupName);
                }

                Assert.Equal(expected[i].GetProperty("min").GetDouble(), groups[i].Min);
                Assert.Equal(expected[i].GetProperty("q1").GetDouble(), groups[i].Q1);
                Assert.Equal(expected[i].GetProperty("median").GetDouble(), groups[i].Median);
                Assert.Equal(expected[i].GetProperty("q3").GetDouble(), groups[i].Q3);
                Assert.Equal(expected[i].GetProperty("max").GetDouble(), groups[i].Max);
                Assert.Equal(expected[i].GetProperty("count").GetInt32(), groups[i].Count);
                Assert.Equal(
                    expected[i].GetProperty("outliers").EnumerateArray().Select(v => v.GetDouble()),
                    groups[i].Outliers);
            }
        }

        public static void AssertTimeSeries(TimeSeriesResult result, string scenario)
        {
            var expected = Scenarios.GetProperty(scenario);
            var points = expected.GetProperty("points").EnumerateArray().ToList();
            var multiTarget = expected.GetProperty("multi_target_nights").EnumerateArray()
                .Select(v => v.GetString()!)
                .ToHashSet(StringComparer.Ordinal);

            Assert.Equal(points.Count, result.Points.Count);
            for (var i = 0; i < points.Count; i++)
            {
                var ordinal = points[i].GetProperty("date").GetString()!;
                Assert.Equal(Nights[int.Parse(ordinal, CultureInfo.InvariantCulture) - 1], result.Points[i].Date);
                Assert.Equal(points[i].GetProperty("value").GetDouble(), result.Points[i].Value);
                Assert.Equal(points[i].GetProperty("frame_count").GetInt32(), result.Points[i].FrameCount);

                // Defect D4, the one corrected figure on this tab: the web takes an arbitrary
                // member of a Python set, so its own answer on a two target night is not a figure
                // and cannot be asserted. What the port answers is the rule: no name and a count
                // of two, and the view reads "Mixed".
                if (multiTarget.Contains(ordinal))
                {
                    Assert.Equal(2, result.Points[i].TargetCount);
                    Assert.Null(result.Points[i].TargetName);
                }
                else
                {
                    Assert.Equal(1, result.Points[i].TargetCount);
                    Assert.Equal(points[i].GetProperty("target_name").GetString(), result.Points[i].TargetName);
                }
            }

            Assert.Equal(expected.GetProperty("ma_7").GetArrayLength(), result.Ma7.Count);
            Assert.Equal(expected.GetProperty("ma_30").GetArrayLength(), result.Ma30.Count);

            Assert.Equal(
                expected.GetProperty("month_boundaries").EnumerateArray()
                    .Select(v => Nights[int.Parse(v.GetString()!, CultureInfo.InvariantCulture) - 1]),
                result.MonthBoundaries);

            // The oracle publishes the LIST of distinct plate scales; the port's field is that
            // list's COUNT (ruling S4, user ruling U3).
            Assert.Equal(
                expected.GetProperty("distinct_plate_scales").GetArrayLength(),
                result.DistinctPlateScales);
        }

        public static void AssertMatrix(MatrixResult result, string scenario)
        {
            var expected = Scenarios.GetProperty(scenario).GetProperty("cells").EnumerateArray().ToList();

            Assert.Equal(100, expected.Count);
            Assert.Equal(expected.Count, result.Cells.Count);

            for (var i = 0; i < expected.Count; i++)
            {
                Assert.Equal(
                    expected[i].GetProperty("x_metric").GetString(),
                    AnalysisMetrics.Key(result.Cells[i].X));
                Assert.Equal(
                    expected[i].GetProperty("y_metric").GetString(),
                    AnalysisMetrics.Key(result.Cells[i].Y));
                Assert.Equal(expected[i].GetProperty("n_points").GetInt32(), result.Cells[i].NPoints);

                var r = expected[i].GetProperty("pearson_r");
                if (r.ValueKind == JsonValueKind.Null)
                {
                    Assert.Null(result.Cells[i].PearsonR);
                }
                else
                {
                    Assert.Equal(r.GetDouble(), result.Cells[i].PearsonR);
                }
            }
        }

        private static void AssertPoints(JsonElement expected, IReadOnlyList<CorrelationPoint> actual)
        {
            var points = expected.EnumerateArray().ToList();
            Assert.Equal(points.Count, actual.Count);

            for (var i = 0; i < points.Count; i++)
            {
                Assert.Equal(points[i].GetProperty("x").GetDouble(), actual[i].X);
                Assert.Equal(points[i].GetProperty("y").GetDouble(), actual[i].Y);
                Assert.Equal(
                    Nights[int.Parse(points[i].GetProperty("date").GetString()!, CultureInfo.InvariantCulture) - 1],
                    actual[i].Night);
                Assert.Equal(points[i].GetProperty("outlier").GetBoolean(), actual[i].Outlier);

                var target = points[i].GetProperty("target_id");
                Assert.Equal(
                    target.ValueKind == JsonValueKind.Null
                        ? null
                        : TargetId(int.Parse(target.GetString()!, CultureInfo.InvariantCulture)),
                    actual[i].TargetId);
            }
        }

        private static void AssertStats(JsonElement expected, SummaryStats? actual)
        {
            Assert.NotNull(actual);
            Assert.Equal(expected.GetProperty("count").GetInt32(), actual!.Count);
            Assert.Equal(expected.GetProperty("min").GetDouble(), actual.Min);
            Assert.Equal(expected.GetProperty("max").GetDouble(), actual.Max);
            Assert.Equal(expected.GetProperty("mean").GetDouble(), actual.Mean);
            Assert.Equal(expected.GetProperty("median").GetDouble(), actual.Median);
            Assert.Equal(expected.GetProperty("std_dev").GetDouble(), actual.StdDev);
        }

        private static void AssertTrend(JsonElement expected, TrendLine? actual)
        {
            Assert.NotNull(actual);
            Assert.Equal(expected.GetProperty("slope").GetDouble(), actual!.Slope);
            Assert.Equal(expected.GetProperty("intercept").GetDouble(), actual.Intercept);
            Assert.Equal(expected.GetProperty("r_squared").GetDouble(), actual.RSquared);
            Assert.Equal(expected.GetProperty("pearson_r").GetDouble(), actual.PearsonR);
            Assert.Equal(expected.GetProperty("spearman_rho").GetDouble(), actual.SpearmanRho);

            foreach (var (name, band) in new[]
            {
                ("confidence_upper", actual.ConfidenceUpper),
                ("confidence_lower", actual.ConfidenceLower),
            })
            {
                var points = expected.GetProperty(name).EnumerateArray().ToList();
                Assert.Equal(points.Count, band.Count);
                for (var i = 0; i < points.Count; i++)
                {
                    Assert.Equal(points[i].GetProperty("x").GetDouble(), band[i].X);
                    Assert.Equal(points[i].GetProperty("y").GetDouble(), band[i].Y);
                }
            }
        }

        /// <summary>One scalar the oracle publishes, for a case that records the web's own figure
        /// beside the shipped one.</summary>
        public static int Figure(string scenario, string field)
            => Scenarios.GetProperty(scenario).GetProperty(field).GetInt32();

        private static double? Number(JsonElement row, string column)
        {
            var value = row.GetProperty(column);
            return value.ValueKind == JsonValueKind.Null ? null : value.GetDouble();
        }

        private static Guid TargetId(int ordinal)
            => Guid.ParseExact($"30000000-0000-0000-0000-{ordinal:D12}", "D");

        private static JsonElement Load(string name)
            => JsonDocument
                .Parse(System.IO.File.ReadAllText(
                    SourceScan.SourceFile($"tests/Fixtures/realdata/{name}")))
                .RootElement.Clone();
    }

    // ---- the harness -------------------------------------------------------------------

    internal sealed class Library : IDisposable
    {
        private readonly TestDatabaseHandle _db = TestDatabaseFactory.CreateMigratedDatabase();
        private readonly SettingsStore _settings;
        private readonly AliasMapCache _aliases;
        private readonly Guid _logId = Guid.NewGuid();
        private int _frameIndex;
        private int _sectionIndex;

        public Library()
        {
            _settings = new SettingsStore(new SettingsRepository(_db.ConnectionString));
            _aliases = new AliasMapCache(_settings);

            var connection = new DatabaseConnectionString(_db.ConnectionString);
            var guiding = new GuidingStatsQuery(connection, _aliases, ProfileMap);
            Query = new AnalysisQuery(
                connection, _aliases, new StatsCache(new StatsQuery(connection, _aliases, guiding)), ProfileMap);
            Cache = new AnalysisCache(Query);

            using var context = Phd2SchemaTests.Open(_db);
            context.Phd2Logs.Add(Phd2SchemaTests.Log(_logId, @"C:\Astro\guide\PHD2_GuideLog_0001.txt"));
            context.SaveChanges();
        }

        public AnalysisQuery Query { get; }

        public AnalysisCache Cache { get; }

        /// <summary>One frame with the harness's defaults and a sequential id, so
        /// <c>ORDER BY i.session_date, i.id</c> follows the order the case created them in.
        /// </summary>
        public Image Frame(Action<Image>? configure = null)
        {
            var index = _frameIndex++;
            var image = new Image
            {
                Id = Guid.ParseExact($"00000000-0000-0000-0000-{index:D12}", "D"),
                FilePath = $@"C:\Fixture\{index:D7}.fits",
                FileName = $"{index:D7}.fits",
                ImageType = "LIGHT",
                SessionDate = Night1,
                CaptureDate = Night1.ToDateTime(new TimeOnly(22, 0)),
                ExposureTime = 300d,
            };
            configure?.Invoke(image);
            return image;
        }

        public void AddFrames(IEnumerable<Image> frames)
            => Insert(frames.Select(frame => ((object?)frame.Id, frame, (object?)frame.CaptureDate)));

        /// <summary>Frames whose <c>id</c> is written as raw TEXT rather than through a
        /// <see cref="Guid"/>, so a case can seed two ids whose ordinal and case-insensitive
        /// orders disagree. A <see cref="Guid"/> normalises its own spelling, so this is the only
        /// way to seed that shape.</summary>
        public void AddFramesWithTextIds(IEnumerable<(string Id, Image Frame)> frames)
            => Insert(frames.Select(entry => ((object?)entry.Id, entry.Frame, (object?)entry.Frame.CaptureDate)));

        public void AddFrameWithBrokenSessionDate(Action<Image> configure)
        {
            var frame = Frame(configure);
            Insert([((object?)frame.Id, frame, (object?)frame.CaptureDate)]);

            // The newest row is the one just written: ids are sequential in creation order. Named
            // by position rather than by literal, because the provider chooses the stored
            // spelling of a Guid and a literal in this statement would have to guess its case.
            Execute("UPDATE images SET session_date = '' WHERE id = (SELECT id FROM images ORDER BY id DESC LIMIT 1);");
        }

        public void AddFrameWithBrokenCaptureDate(Action<Image> configure)
        {
            var frame = Frame(configure);

            // capture_date IS NOT NULL is satisfied and strftime cannot parse it, which is the
            // one shape that produces a null month key.
            Insert([((object?)frame.Id, frame, "not a timestamp")]);
        }

        public void DeleteOneFrame()
            => Execute("DELETE FROM images WHERE id = (SELECT id FROM images ORDER BY id DESC LIMIT 1);");

        public void ClearOnePlateScale(string telescope)
            => Execute(
                "UPDATE images SET arcsec_per_pixel = NULL WHERE id = "
                + $"(SELECT id FROM images WHERE telescope = '{telescope}' ORDER BY id LIMIT 1);");

        public void AddFramesFast(int count, Action<Image, int> configure)
        {
            var frames = new List<Image>(count);
            for (var i = 0; i < count; i++)
            {
                var index = i;
                frames.Add(Frame(frame => configure(frame, index)));
            }

            AddFrames(frames);
        }

        /// <summary>One full scan of the filtered LIGHT rows, the unit the matrix budget's
        /// hundred statement defect is measured in.</summary>
        public void FullScan()
        {
            using var context = new GalactiLogContext(GalactiLogContextOptions.Create(_db.ConnectionString));
            context.Database.OpenConnection();
            using var command = ((SqliteConnection)context.Database.GetDbConnection()).CreateCommand();
            command.CommandText =
                """
                SELECT i.humidity, i.median_hfr
                FROM images i
                WHERE i.image_type = 'LIGHT' AND i.capture_date IS NOT NULL
                ORDER BY i.session_date, i.id;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                _ = reader.IsDBNull(0);
                _ = reader.IsDBNull(1);
            }
        }

        public Guid AddTarget(string primaryName)
        {
            var target = new Target { Id = Guid.NewGuid(), PrimaryName = primaryName, Aliases = "[]" };
            using var context = new GalactiLogContext(
                GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true));
            context.Targets.Add(target);
            context.SaveChanges();
            return target.Id;
        }

        public Guid AddMergedTarget(string primaryName, Guid mergedInto)
        {
            var target = new Target
            {
                Id = Guid.NewGuid(),
                PrimaryName = primaryName,
                Aliases = "[]",
                MergedIntoId = mergedInto,
            };
            using var context = new GalactiLogContext(
                GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true));
            context.Targets.Add(target);
            context.SaveChanges();
            return target.Id;
        }

        public Guid AddTarget(Guid id, string primaryName)
        {
            var target = new Target { Id = id, PrimaryName = primaryName, Aliases = "[]" };
            using var context = new GalactiLogContext(
                GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true));
            context.Targets.Add(target);
            context.SaveChanges();
            return id;
        }

        public void AddPhd2Session(Action<Phd2Session> configure)
        {
            using var context = Phd2SchemaTests.Open(_db);
            var session = Phd2SchemaTests.Session(Guid.NewGuid(), _logId);
            session.SectionIndex = _sectionIndex++;
            configure(session);
            context.Phd2Sessions.Add(session);
            context.SaveChanges();
        }

        public void MapTelescope(string canonical, params string[] rawSpellings)
            => _settings.SaveEquipment(_settings.GetEquipment() with
            {
                Telescopes = new Dictionary<string, EquipmentItemSettings>(_settings.GetEquipment().Telescopes)
                {
                    [canonical] = new() { Aliases = rawSpellings },
                },
            });

        public void MapCamera(string canonical, params string[] rawSpellings)
            => _settings.SaveEquipment(_settings.GetEquipment() with
            {
                Cameras = new Dictionary<string, EquipmentItemSettings>(_settings.GetEquipment().Cameras)
                {
                    [canonical] = new() { Aliases = rawSpellings },
                },
            });

        public void MapFilter(string canonical, params string[] rawSpellings)
        {
            var filters = _settings.GetFilters();
            filters[canonical] = new FilterSetting { Aliases = rawSpellings };
            _settings.SaveFilters(filters);
        }

        public void MapProfile(string profile, string? telescope)
            => _settings.MutateGeneral(general => general with
            {
                Phd2ProfileMap = Phd2Profiles.ToJson(
                    Phd2Profiles.SetTelescope(general.Phd2ProfileMap, profile, telescope)),
            });

        public void Dispose()
        {
            _aliases.Dispose();
            _db.Dispose();
        }

        private JsonElement? ProfileMap() => _settings.GetGeneral().Phd2ProfileMap;

        private void Execute(string sql)
        {
            using var context = new GalactiLogContext(GalactiLogContextOptions.Create(_db.ConnectionString));
            context.Database.OpenConnection();
            using var command = ((SqliteConnection)context.Database.GetDbConnection()).CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        // One prepared INSERT in one transaction rather than a context per frame: the timing case
        // seeds 200,000 rows and the real-data case 416, and an EF round trip per row would
        // dominate both. Test-only, and it writes nothing outside its own temporary database.
        private void Insert(IEnumerable<(object? Id, Image Frame, object? CaptureDate)> rows)
        {
            using var context = new GalactiLogContext(GalactiLogContextOptions.Create(_db.ConnectionString));
            context.Database.OpenConnection();
            var connection = (SqliteConnection)context.Database.GetDbConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();

            var columns = new[]
            {
                "id", "file_path", "file_name", "capture_date", "session_date", "resolved_target_id",
                "exposure_time", "filter_used", "image_type", "telescope", "camera",
                "arcsec_per_pixel", "sensor_temp", "median_hfr", "eccentricity", "fwhm",
                "detected_stars", "guiding_rms_arcsec", "guiding_rms_ra_arcsec",
                "guiding_rms_dec_arcsec", "adu_stdev", "adu_mean", "adu_median",
                "focuser_temp", "airmass", "ambient_temp", "dew_point", "humidity", "pressure",
                "wind_speed", "cloud_cover", "sky_quality",
            };

            command.CommandText =
                $"INSERT INTO images ({string.Join(", ", columns)}) VALUES "
                + $"({string.Join(", ", columns.Select((_, i) => $"@p{i}"))});";

            var parameters = columns
                .Select((_, i) => command.Parameters.Add(new SqliteParameter($"@p{i}", DBNull.Value)))
                .ToArray();

            foreach (var (id, frame, captureDate) in rows)
            {
                object?[] values =
                [
                    id, frame.FilePath, frame.FileName, captureDate, frame.SessionDate,
                    frame.ResolvedTargetId, frame.ExposureTime, frame.FilterUsed, frame.ImageType,
                    frame.Telescope, frame.Camera, frame.ArcsecPerPixel, frame.SensorTemp,
                    frame.MedianHfr, frame.Eccentricity, frame.Fwhm, frame.DetectedStars,
                    frame.GuidingRmsArcsec, frame.GuidingRmsRaArcsec, frame.GuidingRmsDecArcsec,
                    frame.AduStdev, frame.AduMean, frame.AduMedian, frame.FocuserTemp,
                    frame.Airmass, frame.AmbientTemp, frame.DewPoint, frame.Humidity,
                    frame.Pressure, frame.WindSpeed, frame.CloudCover, frame.SkyQuality,
                ];

                for (var i = 0; i < values.Length; i++)
                {
                    parameters[i].Value = values[i] ?? DBNull.Value;
                }

                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }
}
