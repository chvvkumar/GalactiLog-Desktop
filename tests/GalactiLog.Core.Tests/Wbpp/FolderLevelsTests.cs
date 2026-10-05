using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GalactiLog.Core.Wbpp;
using Xunit;

namespace GalactiLog.Core.Tests.Wbpp;

// Task 2a: the folder level computation (task2.md section 7, the Core half).
//
// Every case here builds path strings directly. No case creates a directory or a file: the whole
// computation is pure string and dictionary work over rows the database already holds, and a case
// that seemed to need the disk would mean the design was wrong.
//
// The level table, the contamination counts, the default pick and the staging names are compared
// against the real wbpp_export.py through the golden file this repository carries
// (tests/Fixtures/golden/levels/levels.json, written by levels_oracle.py with
// PYTHONHASHSEED=0). The oracle's parity cases are single-leaf, so the Python's set ordering
// cannot show in them; the multi-leaf order, the Windows root shapes, the case-only difference,
// the byte figure and ruling R6's distinctness are the port's own and are asserted directly.
public class FolderLevelsTests
{
    private const string Root = @"D:\Astro";
    private const long FrameSize = 11_520;

    // ---------------------------------------------------------------------------------------
    // 7.1 The level table on a constructed tree
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ForSession_OnAConstructedTree_OrdersLevelsByDepthThenPathAndCountsTheSubtree()
    {
        var a = Frame(@"M31\2026-07-01\Ha\a.fits", 100);
        var b = Frame(@"M31\2026-07-01\Ha\b.fits", 200);
        var c = Frame(@"M31\2026-07-01\OIII\c.fits", 400);
        var index = Index(Cat(a, "m31", "M 31", Night(1)), Cat(b, "m31", "M 31", Night(1)), Cat(c, "m31", "M 31", Night(1)));

        var levels = FolderLevels.ForSession(Night(1), "m31", [a, b, c], index).Levels;

        Assert.Equal(
            [Under(@"M31"), Under(@"M31\2026-07-01"), Under(@"M31\2026-07-01\Ha"), Under(@"M31\2026-07-01\OIII")],
            levels.Select(l => l.Path));
        Assert.Equal([1, 2, 3, 3], levels.Select(l => l.DepthFromRoot));
        Assert.Equal([3, 3, 2, 1], levels.Select(l => l.FrameCount));
        Assert.Equal([700L, 700L, 300L, 400L], levels.Select(l => l.SubtreeBytes));
    }

    // ---------------------------------------------------------------------------------------
    // The oracle: parity with the real wbpp_export.py, no tolerance
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void AncestorChain_MatchesTheOracle()
    {
        var chains = Golden().GetProperty("chains");

        // The goldens live in the docs tree, where a later agent may regenerate them. Without this
        // the loop below walks an emptied or trimmed file and passes green, and W15's only
        // machine-checked parity vanishes with no red anywhere.
        Assert.Equal(2, chains.GetArrayLength());

        foreach (var chain in chains.EnumerateArray())
        {
            var path = Win(chain.GetProperty("path").GetString()!);
            var expected = chain.GetProperty("chain").EnumerateArray().Select(e => Win(e.GetString()!));
            Assert.Equal(expected, FolderLevels.AncestorChain(path, Root));
        }
    }

    [Fact]
    public void ForSession_MatchesTheOracleOnEveryParityCase()
    {
        var sessions = Golden().GetProperty("sessions");
        Assert.Equal(6, sessions.GetArrayLength());

        foreach (var session in sessions.EnumerateArray())
        {
            var name = session.GetProperty("name").GetString()!;
            var night = DateOnly.Parse(session.GetProperty("session_date").GetString()!, CultureInfo.InvariantCulture);
            var frames = session.GetProperty("files").EnumerateArray()
                .Select(f => new WbppFramePath(Guid.NewGuid(), Win(f.GetString()!), FrameSize))
                .ToList();
            var index = ContaminationIndex.Build(
                [Root],
                session.GetProperty("catalogue").EnumerateArray().Select(r => new WbppCataloguePath(
                    Win(r.GetProperty("path").GetString()!),
                    r.GetProperty("target").GetString()!,
                    r.GetProperty("target").GetString()!,
                    DateOnly.Parse(r.GetProperty("date").GetString()!, CultureInfo.InvariantCulture),
                    true,
                    FrameSize)));

            // The oracle's own target name doubles as the target key in these cases.
            var actual = FolderLevels.ForSession(night, TargetOf(session), frames, index);
            var expected = session.GetProperty("levels").EnumerateArray().ToList();

            Assert.True(expected.Count == actual.Levels.Count, $"level count, case {name}");
            for (var i = 0; i < expected.Count; i++)
            {
                var e = expected[i];
                var got = actual.Levels[i];
                Assert.Equal(Win(e.GetProperty("path").GetString()!), got.Path);
                Assert.Equal(e.GetProperty("depth_from_root").GetInt32(), got.DepthFromRoot);
                Assert.Equal(e.GetProperty("frame_count").GetInt32(), got.FrameCount);
                Assert.Equal(e.GetProperty("other_targets").EnumerateArray().Select(x => x.GetString()), got.OtherTargets);
                Assert.Equal(e.GetProperty("other_dates").EnumerateArray().Select(x => x.GetString()), got.OtherNights);
                Assert.Equal(e.GetProperty("is_contaminated").GetBoolean(), got.IsContaminated);
            }

            Assert.Equal(session.GetProperty("default_level_index").GetInt32(), actual.DefaultLevelIndex);
            Assert.Null(actual.Unavailable);
            Assert.Equal(frames.Count, actual.TotalFrameCount);
            Assert.Equal(0, actual.FramesWithoutRoot);
            Assert.Equal(Root, actual.ScanRoot);
        }
    }

    [Fact]
    public void StagingNames_MatchTheOracleOnEveryParityCase()
    {
        // Two of the golden file's staging entries are evidence of the Python's own answer on the
        // two inputs the port departs from (R6 and Q4); they are not parity cases and have their
        // own assertions below. Both counts are asserted, so neither a trimmed golden nor a rename
        // of the evidence scenarios can quietly empty this loop.
        var staging = Golden().GetProperty("staging");
        Assert.Equal(6, staging.GetArrayLength());

        var skipped = 0;
        var compared = 0;
        foreach (var scenario in staging.EnumerateArray())
        {
            var name = scenario.GetProperty("name").GetString()!;
            if (name.Contains("python_answer", StringComparison.Ordinal))
            {
                skipped++;
                continue;
            }

            compared++;
            var paths = scenario.GetProperty("paths").EnumerateArray().Select(p => WinAny(p.GetString()!)).ToList();
            var dates = scenario.GetProperty("dates").EnumerateArray()
                .Select(d => DateOnly.Parse(d.GetString()!, CultureInfo.InvariantCulture)).ToList();
            var chosen = paths.Select((p, i) => new ChosenLevel(dates[i], Level(p))).ToList();

            Assert.Equal(
                scenario.GetProperty("names").EnumerateArray().Select(n => n.GetString()),
                FolderLevels.StagingNames(chosen));
        }

        Assert.Equal(4, compared);
        Assert.Equal(2, skipped);
    }

    // ---------------------------------------------------------------------------------------
    // 7.2 Windows shapes
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void AncestorChain_UnderADriveRoot_KeepsTheRootsTrailingSeparator()
    {
        Assert.Equal(
            [@"D:\M31", @"D:\M31\2026-07-01"],
            FolderLevels.AncestorChain(@"D:\M31\2026-07-01\a.fits", @"D:\"));
    }

    [Fact]
    public void AncestorChain_UnderAUncShareRoot_StopsAtTheShare()
    {
        Assert.Equal(
            [@"\\server\share\M31", @"\\server\share\M31\2026-07-01"],
            FolderLevels.AncestorChain(@"\\server\share\M31\2026-07-01\a.fits", @"\\server\share"));
    }

    [Fact]
    public void AncestorChain_WithATrailingSeparatorOnTheRoot_IsIdenticalToWithout()
    {
        // Asserted against the expected chain and not only against the other call, so a defect that
        // empties both sides cannot pass.
        Assert.Equal(
            [@"D:\Astro\M31", @"D:\Astro\M31\2026-07-01"],
            FolderLevels.AncestorChain(@"D:\Astro\M31\2026-07-01\a.fits", @"D:\Astro\"));
        Assert.Equal(
            FolderLevels.AncestorChain(@"D:\Astro\M31\2026-07-01\a.fits", @"D:\Astro"),
            FolderLevels.AncestorChain(@"D:\Astro\M31\2026-07-01\a.fits", @"D:\Astro\"));
    }

    [Fact]
    public void RootOf_WithNestedScanRoots_AnswersTheDeeperOne()
    {
        // ScanFilterConfig.RefuseScanRoot forbids nested roots now, so this is a library that
        // predates that refusal. A regression to first-match would anchor the chain at the shallower
        // root, shifting every depth and adding a level, with nothing else going red.
        var frame = Frame(@"M31\2026-07-01\a.fits");
        var index = ContaminationIndex.Build(
            [Root, Under("M31")],
            [Cat(frame, "m31", "M 31", Night(1))]);

        Assert.Equal(Under("M31"), index.RootOf(frame.FilePath));
        Assert.Equal(
            [Under(@"M31\2026-07-01")],
            FolderLevels.ForSession(Night(1), "m31", [frame], index).Levels.Select(l => l.Path));
    }

    [Fact]
    public void Build_IsTheOneHomeOfScanRootNormalisation()
    {
        // No caller normalises the roots a second time, so every shape has to be answered here: a
        // blank and a whitespace entry dropped (Path.GetFullPath throws ArgumentException on one),
        // surrounding whitespace trimmed, a redundant segment folded, and a trailing separator
        // removed on an ordinary folder but kept on a drive root, which IS its separator.
        var frame = Frame(@"M31\2026-07-01\a.fits");
        var row = Cat(frame, "m31", "M 31", Night(1));

        Assert.Equal(Root, ContaminationIndex.Build(["", "   ", Root], [row]).RootOf(frame.FilePath));
        Assert.Equal(Root, ContaminationIndex.Build([$"  {Root}  "], [row]).RootOf(frame.FilePath));
        Assert.Equal(Root, ContaminationIndex.Build([Root + @"\"], [row]).RootOf(frame.FilePath));
        Assert.Equal(Root, ContaminationIndex.Build([Under(@"M31\..")], [row]).RootOf(frame.FilePath));

        // The drive root keeps its separator: "D:" alone names the current directory on D:.
        var atDrive = new WbppFramePath(Guid.NewGuid(), @"D:\M31\2026-07-01\a.fits", FrameSize);
        var drive = ContaminationIndex.Build(
            [@"D:\"], [new WbppCataloguePath(atDrive.FilePath, "m31", "M 31", Night(1), true, FrameSize)]);
        Assert.Equal(@"D:\", drive.RootOf(atDrive.FilePath));

        // A root spelled two ways is one root, so a night under it never reports SeveralScanRoots.
        var twoSpellings = ContaminationIndex.Build([Root + @"\", Root.ToLowerInvariant()], [row]);
        Assert.Null(FolderLevels.ForSession(Night(1), "m31", [frame], twoSpellings).Unavailable);
    }

    [Fact]
    public void AncestorChain_ForAFrameSittingInTheScanRoot_IsEmpty()
    {
        Assert.Empty(FolderLevels.AncestorChain(@"D:\Astro\a.fits", Root));
    }

    [Fact]
    public void AncestorChain_ForAFrameUnderNoPartOfTheRoot_IsEmpty()
    {
        Assert.Empty(FolderLevels.AncestorChain(@"E:\Other\M31\a.fits", Root));
    }

    [Fact]
    public void ForSession_OnPathsDifferingOnlyInCase_ReportsOneFolderUnderTheFirstSpelling()
    {
        var a = Frame(@"M31\Ha\a.fits");
        var b = new WbppFramePath(Guid.NewGuid(), Path.GetFullPath(@"D:\astro\m31\ha\b.fits"), FrameSize);
        var index = Index(Cat(a, "m31", "M 31", Night(1)), Cat(b, "m31", "M 31", Night(1)));

        var levels = FolderLevels.ForSession(Night(1), "m31", [a, b], index).Levels;

        Assert.Equal([Under(@"M31"), Under(@"M31\Ha")], levels.Select(l => l.Path));
        Assert.Equal([2, 2], levels.Select(l => l.FrameCount));
    }

    [Fact]
    public void ForSession_ForASiblingWhoseNameIsAPrefix_DoesNotAbsorbIt()
    {
        var inHa = Frame(@"M31\Ha\a.fits");
        var inHaOld = Frame(@"M31\Ha_old\b.fits");
        var index = Index(Cat(inHa, "m31", "M 31", Night(1)), Cat(inHaOld, "m31", "M 31", Night(1)));

        var levels = FolderLevels.ForSession(Night(1), "m31", [inHa, inHaOld], index).Levels;

        Assert.Equal(1, levels.Single(l => l.Path == Under(@"M31\Ha")).FrameCount);
        Assert.Equal(1, levels.Single(l => l.Path == Under(@"M31\Ha_old")).FrameCount);
    }

    [Fact]
    public void ForSession_ForAFolderNameHoldingADot_KeepsTheWholeNameInThePathAndTheStagingEntry()
    {
        var frame = Frame(@"Date_2026-07-13\LIGHT\Cam\M31\Angle_71.61\a.fits");
        var index = Index(Cat(frame, "m31", "M 31", Night(1)));

        var levels = FolderLevels.ForSession(Night(1), "m31", [frame], index).Levels;
        var leaf = levels[^1];

        Assert.Equal(Under(@"Date_2026-07-13\LIGHT\Cam\M31\Angle_71.61"), leaf.Path);
        Assert.Equal(5, leaf.DepthFromRoot);
        Assert.Equal(["Angle_71.61"], FolderLevels.StagingNames([new ChosenLevel(Night(1), leaf)]));
    }

    [Fact]
    public void Source_NamesNoExtensionMember()
    {
        var source = StripComments(File.ReadAllText(SourceFile));

        Assert.DoesNotContain("GetFileNameWithoutExtension", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ChangeExtension", source, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // 7.2a A frame under no scan root, and the drive and share names
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ForSession_WithOneRootlessFrame_KeepsTheNightAndCountsIt()
    {
        var kept = new[] { Frame(@"M31\Ha\a.fits"), Frame(@"M31\Ha\b.fits"), Frame(@"M31\Ha\c.fits") };
        var stale = new WbppFramePath(Guid.NewGuid(), Path.GetFullPath(@"E:\Removed\M31\Ha\d.fits"), FrameSize);
        var index = Index(kept.Select(f => Cat(f, "m31", "M 31", Night(1))).ToArray());

        var session = FolderLevels.ForSession(Night(1), "m31", [.. kept, stale], index);

        Assert.Null(session.Unavailable);
        Assert.Equal([Under(@"M31"), Under(@"M31\Ha")], session.Levels.Select(l => l.Path));
        Assert.Equal(1, session.FramesWithoutRoot);
        Assert.Equal(4, session.TotalFrameCount);
        Assert.Equal(3, session.Levels[^1].FrameCount);
    }

    [Fact]
    public void ForSession_WithEveryFrameRootless_ReportsNoScanRoot()
    {
        var frames = new[] { Frame(@"E:\Removed\a.fits", normalizeOnly: true), Frame(@"E:\Removed\b.fits", normalizeOnly: true) };
        var session = FolderLevels.ForSession(Night(1), "m31", frames, Index());

        Assert.Equal(LevelsUnavailable.NoScanRoot, session.Unavailable);
        Assert.Empty(session.Levels);
        Assert.Equal(2, session.FramesWithoutRoot);
        Assert.Equal(2, session.TotalFrameCount);
        Assert.Equal("", session.ScanRoot);
    }

    [Fact]
    public void ForSession_WithNoFrames_ReportsNoFrames()
    {
        var session = FolderLevels.ForSession(Night(1), "m31", [], Index());

        Assert.Equal(LevelsUnavailable.NoFrames, session.Unavailable);
        Assert.Empty(session.Levels);
        Assert.Equal(0, session.TotalFrameCount);
    }

    [Fact]
    public void ForSession_WithFramesUnderSeveralRoots_ReportsSeveralScanRoots()
    {
        var one = new WbppFramePath(Guid.NewGuid(), @"D:\Astro\M31\a.fits", FrameSize);
        var two = new WbppFramePath(Guid.NewGuid(), @"E:\Astro2\M31\b.fits", FrameSize);
        var index = ContaminationIndex.Build(
            [Root, @"E:\Astro2"],
            [new WbppCataloguePath(one.FilePath, "m31", "M 31", Night(1), true, FrameSize),
             new WbppCataloguePath(two.FilePath, "m31", "M 31", Night(1), true, FrameSize)]);

        var session = FolderLevels.ForSession(Night(1), "m31", [one, two], index);

        Assert.Equal(LevelsUnavailable.SeveralScanRoots, session.Unavailable);
        Assert.Empty(session.Levels);
    }

    [Fact]
    public void ForSession_WithEveryFrameInTheScanRootItself_ReportsFramesInRootItself()
    {
        var frame = Frame(@"a.fits");
        var session = FolderLevels.ForSession(Night(1), "m31", [frame], Index(Cat(frame, "m31", "M 31", Night(1))));

        Assert.Equal(LevelsUnavailable.FramesInRootItself, session.Unavailable);
        Assert.Empty(session.Levels);
        Assert.Equal(0, session.FramesWithoutRoot);
        Assert.Equal(1, session.FramesInRootItself);
    }

    [Fact]
    public void ForSession_WithSomeFramesInTheScanRootItself_KeepsItsLevelsAndStatesTheShortfall()
    {
        // Parity with the Python, which gives a root-sitting frame an empty ancestor chain and so
        // counts it under no level. The night keeps its levels; the count is what accounts for a
        // level count below the night's own TotalFrameCount, which nothing else would explain.
        var inRoot = Frame(@"a.fits");
        var deeper = new[] { Frame(@"M31\2026-07-01\Ha\b.fits"), Frame(@"M31\2026-07-01\Ha\c.fits") };
        var frames = new[] { inRoot, deeper[0], deeper[1] };
        var index = Index(frames.Select(f => Cat(f, "m31", "M 31", Night(1))).ToArray());

        var session = FolderLevels.ForSession(Night(1), "m31", frames, index);

        Assert.Null(session.Unavailable);
        Assert.Equal(3, session.Levels.Count);
        Assert.All(session.Levels, level => Assert.Equal(2, level.FrameCount));
        Assert.Equal(3, session.TotalFrameCount);
        Assert.Equal(0, session.FramesWithoutRoot);
        Assert.Equal(1, session.FramesInRootItself);
    }

    [Fact]
    public void ForSession_WhenTheTargetKeyIsTheSameGuidInAnotherCase_ReadsItsOwnLevelsAsClean()
    {
        // images.resolved_target_id is stored upper cased while a Guid prints lower cased, so a
        // caller holding either spelling must read the same answer. Under a purely ordinal
        // comparison the night would see its own target as another target and every level would
        // badge as contaminated.
        var key = Guid.NewGuid().ToString();
        var frames = new[] { Frame(@"M31\Ha\a.fits"), Frame(@"M31\Ha\b.fits") };
        var index = Index(frames.Select(f => Cat(f, key.ToUpperInvariant(), "M 31", Night(1))).ToArray());

        var session = FolderLevels.ForSession(Night(1), key.ToLowerInvariant(), frames, index);

        Assert.All(session.Levels, level => Assert.Empty(level.OtherTargets));
        Assert.All(session.Levels, level => Assert.False(level.IsContaminated));
        Assert.Equal(session.Levels.Count - 1, session.DefaultLevelIndex);
    }

    [Fact]
    public void ForSession_WhenTwoObjectKeysDifferOnlyInCase_ReadsThemAsDifferentTargets()
    {
        // resolved_target_id and the obj: concatenation are binary collated and the dashboard
        // groups by them, so obj:m31 and obj:M31 are two real groups. Folding the whole key would
        // hide each from the other's contamination list.
        var mine = Frame(@"M31\Ha\a.fits");
        var theirs = Frame(@"M31\OIII\b.fits");
        var index = Index(
            Cat(mine, "obj:m31", "m31", Night(1)),
            Cat(theirs, "obj:M31", "M31", Night(1)));

        var parent = FolderLevels.ForSession(Night(1), "obj:m31", [mine], index)
            .Levels.Single(level => level.Path == Under("M31"));

        Assert.Equal(["M31"], parent.OtherTargets);
        Assert.True(parent.IsContaminated);
    }

    [Fact]
    public void ForSession_WhenTheTargetKeyIsAGuidAndTheOccupantsAreNot_ReadsThemAsDifferentTargets()
    {
        var key = Guid.NewGuid().ToString();
        var mine = Frame(@"M31\Ha\a.fits");
        var theirs = Frame(@"M31\OIII\b.fits");
        var index = Index(Cat(mine, key, "M 31", Night(1)), Cat(theirs, "obj:other", "Other", Night(1)));

        var parent = FolderLevels.ForSession(Night(1), key, [mine], index)
            .Levels.Single(level => level.Path == Under("M31"));

        Assert.Equal(["Other"], parent.OtherTargets);
    }

    [Fact]
    public void StagingNames_ForADriveRootAndAShareRoot_AreTheDriveLetterAndTheShareName()
    {
        Assert.Equal(
            ["D", "share"],
            FolderLevels.StagingNames([
                new ChosenLevel(Night(1), Level(@"D:\")),
                new ChosenLevel(Night(2), Level(@"\\server\share"))]));
    }

    // The rename note compares a row's folder name against a staging entry name. The application's
    // row text reaches this member, so the two agree on a root path, where the old second spelling
    // answered the trimmed path itself.
    [Fact]
    public void BaseName_ForARootPath_IsTheDriveLetterOrTheShareNameAndNeverTheTrimmedPath()
    {
        Assert.Equal("D", FolderLevels.BaseName(@"D:\"));
        Assert.Equal("share", FolderLevels.BaseName(@"\\server\share"));
        Assert.Equal("M31", FolderLevels.BaseName(@"C:\lights\M31\"));
    }

    // ---------------------------------------------------------------------------------------
    // 7.3 Contamination counts
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ForSession_Contamination_ListsOtherTargetsAndNightsAndNeverItsOwn()
    {
        var index = Index(FixtureCatalogue());
        var night3 = FixtureFrames("m 31", 3);

        var levels = FolderLevels.ForSession(Night3, "m31", night3, index).Levels;
        var shared = levels.Single(l => l.Path == Under(@"M 31\2025-03-20"));
        var top = levels.Single(l => l.Path == Under(@"M 31"));
        var leaf = levels.Single(l => l.Path == Under(@"M 31\2025-03-20\LIGHT\Ha"));

        Assert.Equal(["M 33"], shared.OtherTargets);
        Assert.Empty(shared.OtherNights);
        Assert.True(shared.IsContaminated);

        Assert.Equal(["M 33"], top.OtherTargets);
        Assert.Equal(["2025-01-10", "2025-02-14"], top.OtherNights);
        Assert.DoesNotContain("2025-03-20", top.OtherNights);
        Assert.DoesNotContain("M 31", top.OtherTargets);

        Assert.Empty(leaf.OtherTargets);
        Assert.Empty(leaf.OtherNights);
        Assert.False(leaf.IsContaminated);
    }

    // ---------------------------------------------------------------------------------------
    // 7.4 The multi-leaf night, the port's own case (R1)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ForSession_OnAMultiLeafNight_OrdersTotallyAndDefaultsAboveTheSplit()
    {
        var frames = MultiLeafFrames();
        var index = Index(frames.Select(f => Cat(f, "m31", "M 31", Night(1))).ToArray());
        var date = @"Date_2026-07-01";

        var session = FolderLevels.ForSession(Night(1), "m31", frames, index);
        var shuffled = FolderLevels.ForSession(Night(1), "m31", [.. frames.Reverse()], index);

        Assert.Equal(session.Levels.Select(l => l.Path), shuffled.Levels.Select(l => l.Path));
        Assert.Equal(
            [
                Under(date),
                Under($@"{date}\LIGHT"),
                Under($@"{date}\LIGHT\CamA"),
                Under($@"{date}\LIGHT\CamB"),
                Under($@"{date}\LIGHT\CamA\M31"),
                Under($@"{date}\LIGHT\CamB\M31"),
                Under($@"{date}\LIGHT\CamA\M31\Angle_71.61"),
                Under($@"{date}\LIGHT\CamB\M31\Angle_8.20"),
            ],
            session.Levels.Select(l => l.Path));
        Assert.Equal([1, 2, 3, 3, 4, 4, 5, 5], session.Levels.Select(l => l.DepthFromRoot));

        // The deepest level still holding every frame is the LIGHT folder, above the split.
        Assert.Equal(Under($@"{date}\LIGHT"), session.Levels[session.DefaultLevelIndex].Path);
        Assert.Equal(session.DefaultLevelIndex, shuffled.DefaultLevelIndex);
    }

    [Fact]
    public void ForSession_OnAMultiLeafNightWhoseShallowLevelIsContaminated_StillDefaultsToIt()
    {
        var frames = MultiLeafFrames();
        var intruder = Frame(@"Date_2026-07-01\LIGHT\CamA\NGC7000\a.fits");
        var index = Index([
            .. frames.Select(f => Cat(f, "m31", "M 31", Night(1))),
            Cat(intruder, "ngc7000", "NGC 7000", Night(2))]);

        var session = FolderLevels.ForSession(Night(1), "m31", frames, index);
        var chosen = session.Levels[session.DefaultLevelIndex];

        Assert.Equal(Under(@"Date_2026-07-01\LIGHT"), chosen.Path);
        Assert.True(chosen.IsContaminated);
    }

    // ---------------------------------------------------------------------------------------
    // 7.5 The size figure is the folder's (R4), and a null propagates
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void SubtreeBytes_AreTheFoldersAndNotTheSessions()
    {
        var mine = new[] { Frame(@"M31\Ha\a.fits", 100), Frame(@"M31\Ha\b.fits", 100), Frame(@"M31\Ha\c.fits", 100) };
        var theirs = new[] { Frame(@"M31\Ha\x.fits", 500), Frame(@"M31\Ha\y.fits", 500) };
        var calibration = Frame(@"M31\Ha\dark.fits", 700);
        var index = Index([
            .. mine.Select(f => Cat(f, "m31", "M 31", Night(1))),
            .. theirs.Select(f => Cat(f, "ngc7000", "NGC 7000", Night(2))),
            Cat(calibration, "", "", null, isLight: false)]);

        var leaf = FolderLevels.ForSession(Night(1), "m31", mine, index).Levels.Single(l => l.Path == Under(@"M31\Ha"));

        Assert.Equal(3, leaf.FrameCount);
        Assert.Equal(2_000L, leaf.SubtreeBytes);
        Assert.Equal(2_000L, FolderLevels.SubtreeBytes(Under(@"M31\Ha"), index));
    }

    [Fact]
    public void SubtreeBytes_OnTheFixtureShape_AreTheCatalogueTotalsAndNotTheNightsOwn()
    {
        var index = Index(FixtureCatalogue());
        var levels = FolderLevels.ForSession(Night3, "m31", FixtureFrames("m 31", 3), index).Levels;

        Assert.Equal(322_560L, levels.Single(l => l.Path == Under(@"M 31\2025-03-20")).SubtreeBytes);
        Assert.Equal(529_920L, levels.Single(l => l.Path == Under(@"M 31")).SubtreeBytes);
        Assert.Equal(253_440L, levels.Single(l => l.Path == Under(@"M 31\2025-03-20\LIGHT\Ha")).SubtreeBytes);
    }

    [Fact]
    public void SubtreeBytes_ForAFolderNoRowSitsUnder_IsZeroAndNotNull()
    {
        Assert.Equal(0L, FolderLevels.SubtreeBytes(Under(@"Nothing\Here"), Index(FixtureCatalogue())));
    }

    [Fact]
    public void SubtreeBytes_WithOneUnknownSize_AreNullOnThatLevelAndAboveItOnly()
    {
        var known = Frame(@"M31\Ha\a.fits", 100);
        var unknown = Frame(@"M31\Ha\b.fits", null);
        var sibling = Frame(@"M31\OIII\c.fits", 400);
        var frames = new[] { known, unknown, sibling };
        var index = Index(frames.Select(f => Cat(f, "m31", "M 31", Night(1))).ToArray());

        var levels = FolderLevels.ForSession(Night(1), "m31", frames, index).Levels;

        Assert.Null(levels.Single(l => l.Path == Under(@"M31")).SubtreeBytes);
        Assert.Null(levels.Single(l => l.Path == Under(@"M31\Ha")).SubtreeBytes);
        Assert.Equal(400L, levels.Single(l => l.Path == Under(@"M31\OIII")).SubtreeBytes);
        Assert.Equal(3, levels.Single(l => l.Path == Under(@"M31")).FrameCount);
    }

    [Fact]
    public void Totals_AreNullWhenTheSelectionTouchesAnUnknownSizeAndANumberWhenItDoesNot()
    {
        var known = Frame(@"M31\Ha\a.fits", 100);
        var unknown = Frame(@"M31\Ha\b.fits", null);
        var sibling = Frame(@"M31\OIII\c.fits", 400);
        var frames = new[] { known, unknown, sibling };
        var index = Index(frames.Select(f => Cat(f, "m31", "M 31", Night(1))).ToArray());
        var levels = FolderLevels.ForSession(Night(1), "m31", frames, index).Levels;

        var poisoned = FolderLevels.Totals([new ChosenLevel(Night(1), levels.Single(l => l.Path == Under(@"M31\Ha")))], []);
        var clean = FolderLevels.Totals([new ChosenLevel(Night(1), levels.Single(l => l.Path == Under(@"M31\OIII")))], []);

        Assert.Null(poisoned.SizeBytes);
        Assert.Equal(2, poisoned.FrameCount);
        Assert.Equal(400L, clean.SizeBytes);
        Assert.Equal(1, clean.FrameCount);
        Assert.Equal(1, clean.FolderCount);
    }

    [Fact]
    public void Totals_DeductTheExcludedFramesAndTheirBytes()
    {
        var frames = new[] { Frame(@"M31\Ha\a.fits", 100), Frame(@"M31\Ha\b.fits", 100), Frame(@"M31\Ha\c.fits", 100) };
        var index = Index(frames.Select(f => Cat(f, "m31", "M 31", Night(1))).ToArray());
        var leaf = FolderLevels.ForSession(Night(1), "m31", frames, index).Levels.Single(l => l.Path == Under(@"M31\Ha"));

        var totals = FolderLevels.Totals([new ChosenLevel(Night(1), leaf)], [frames[0]]);

        Assert.Equal(2, totals.FrameCount);
        Assert.Equal(200L, totals.SizeBytes);
    }

    // ---------------------------------------------------------------------------------------
    // 7.6 Staging names, the two departures
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void StagingNames_ForTwoNightsWhoseLeavesDifferOnlyInCase_ArePrefixed()
    {
        // The Python's Counter is case sensitive and returns ["Ha", "HA"], which on Windows is one
        // staging folder holding both nights (Q4).
        Assert.Equal(
            ["2026-07-01_Ha", "2026-07-02_HA"],
            FolderLevels.StagingNames([
                new ChosenLevel(Night(1), Level(@"D:\a\Ha")),
                new ChosenLevel(Night(2), Level(@"D:\b\HA"))]));
    }

    [Fact]
    public void StagingNames_WhenTheDatePrefixItselfCollides_StayDistinct()
    {
        // The reviewer's own input. The Python answers
        // ['2026-07-01_Ha', '2026-07-02_Ha', '2026-07-01_Ha'], two operations into one folder.
        var names = FolderLevels.StagingNames([
            new ChosenLevel(Night(1), Level(@"D:\a\Ha")),
            new ChosenLevel(Night(2), Level(@"D:\b\Ha")),
            new ChosenLevel(Night(3), Level(@"D:\c\2026-07-01_Ha"))]);

        Assert.Equal(["2026-07-01_Ha", "2026-07-02_Ha", "2026-07-01_Ha_2"], names);
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void StagingNames_WhenTheSuffixedNameIsItselfTaken_MoveOnToTheNextNumber()
    {
        var names = FolderLevels.StagingNames([
            new ChosenLevel(Night(3), Level(@"D:\c\2026-07-01_Ha")),
            new ChosenLevel(Night(4), Level(@"D:\d\2026-07-01_Ha_2")),
            new ChosenLevel(Night(1), Level(@"D:\a\Ha")),
            new ChosenLevel(Night(2), Level(@"D:\b\Ha"))]);

        Assert.Equal(["2026-07-01_Ha", "2026-07-01_Ha_2", "2026-07-01_Ha_3", "2026-07-02_Ha"], names);
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // ---------------------------------------------------------------------------------------
    // 7.7 Excluded frames scoped to the chosen level
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ExcludedUnderLevels_DoNotDeductAFrameFromALevelThatNeverTouchesIt()
    {
        var inHa = Frame(@"M31\Ha\bad.fits");
        var inOiii = Frame(@"M31\OIII\bad.fits");
        var chosen = new[] { new ChosenLevel(Night(1), Level(Under(@"M31\Ha"))) };

        var kept = FolderLevels.ExcludedUnderLevels(chosen, Excluded((Night(1), [inHa, inOiii])));

        Assert.Equal([inHa], kept);
    }

    [Fact]
    public void ExcludedUnderLevels_TestEachNightAgainstItsOwnLevelAndCountEachFrameOnce()
    {
        var one = Frame(@"M31\2026-07-01\Ha\bad.fits");
        var two = Frame(@"M31\2026-07-02\Ha\bad.fits");
        var chosen = new[]
        {
            new ChosenLevel(Night(1), Level(Under(@"M31\2026-07-01\Ha"))),
            new ChosenLevel(Night(2), Level(Under(@"M31\2026-07-02\Ha"))),
        };

        var kept = FolderLevels.ExcludedUnderLevels(chosen, Excluded((Night(1), [one, two]), (Night(2), [one, two])));

        Assert.Equal([one, two], kept);
    }

    [Fact]
    public void ExcludedUnderLevels_ForANightWithNoChosenLevel_ContributeNothing()
    {
        var frame = Frame(@"M31\Ha\bad.fits");
        var chosen = new[] { new ChosenLevel(Night(1), Level(Under(@"M31\Ha"))) };

        var kept = FolderLevels.ExcludedUnderLevels(chosen, Excluded((Night(2), [frame])));

        Assert.Empty(kept);
    }

    // ---------------------------------------------------------------------------------------
    // 7.8 Copy operations and the longest prefix
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void CopyOperations_AssignAnExcludeToItsLongestContainingLevel()
    {
        var bad = Frame(@"M31\2026-07-01\Ha\bad.fits");
        var chosen = new[]
        {
            new ChosenLevel(Night(1), Level(Under(@"M31"))),
            new ChosenLevel(Night(2), Level(Under(@"M31\2026-07-01"))),
        };

        var ops = FolderLevels.CopyOperations(chosen, [bad]);

        Assert.Empty(ops[0].ExcludedRelativePaths);
        Assert.Equal([@"Ha\bad.fits"], ops[1].ExcludedRelativePaths);
        Assert.Equal([Under(@"M31"), Under(@"M31\2026-07-01")], ops.Select(o => o.SourcePath));
        Assert.Equal(["M31", "2026-07-01"], ops.Select(o => o.EntryName));
        Assert.Equal([Night(1), Night(2)], ops.Select(o => o.Night));
    }

    [Fact]
    public void CopyOperations_DropAnExcludeUnderNoChosenLevel()
    {
        var chosen = new[] { new ChosenLevel(Night(1), Level(Under(@"M31\Ha"))) };

        var ops = FolderLevels.CopyOperations(chosen, [Frame(@"M31\OIII\bad.fits")]);

        Assert.Empty(Assert.Single(ops).ExcludedRelativePaths);
    }

    // ---------------------------------------------------------------------------------------
    // 7.8a The longest destination path, ruling R7
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(242, 259)]
    [InlineData(243, 260)]
    [InlineData(244, 261)]
    public void LongestDestinationLength_IsTheExactArithmeticAroundTheWindowsCeiling(int stagingLength, int expected)
    {
        var level = Under(@"M31\Ha");
        var index = Index(Cat(Frame(@"M31\Ha\sub\name.fits"), "m31", "M 31", Night(1)));

        var actual = FolderLevels.LongestDestinationLength(
            new ChosenLevel(Night(1), Level(level)), StagingRootOfLength(stagingLength), "Ha", index);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void LongestDestinationLength_TakesTheLongestFileUnderTheLevelAndNotTheFirstOrTheLast()
    {
        var index = Index(
            Cat(Frame(@"M31\Ha\a.fits"), "m31", "M 31", Night(1)),
            Cat(Frame(@"M31\Ha\sub\name.fits"), "m31", "M 31", Night(1)),
            Cat(Frame(@"M31\Ha\b.fits"), "m31", "M 31", Night(1)));

        var actual = FolderLevels.LongestDestinationLength(
            new ChosenLevel(Night(1), Level(Under(@"M31\Ha"))), StagingRootOfLength(242), "Ha", index);

        Assert.Equal(259, actual);
    }

    // ---------------------------------------------------------------------------------------
    // 7.9 No second containment rule
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Source_UsesTheOneContainmentRuleAndNoHandRolledPathWork()
    {
        var source = StripComments(File.ReadAllText(SourceFile));

        Assert.Contains("PathConfinement.IsUnderOrEqual", source, StringComparison.Ordinal);
        Assert.DoesNotContain("StartsWith(", source, StringComparison.Ordinal);
        Assert.DoesNotContain(@"Split('\\'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Split('/'", source, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    private static readonly DateOnly Night3 = new(2025, 3, 20);

    private static string SourceFile
        => Path.Combine(FindRepoRoot(), "src", "GalactiLog.Core", "Wbpp", "FolderLevels.cs");

    private static DateOnly Night(int day) => new(2026, 7, day);

    private static string Under(string relative) => Path.Combine(Root, relative);

    private static WbppFramePath Frame(string relative, long? size = FrameSize, bool normalizeOnly = false)
        => new(Guid.NewGuid(), normalizeOnly ? Path.GetFullPath(relative) : Under(relative), size);

    private static WbppCataloguePath Cat(
        WbppFramePath frame, string targetKey, string targetName, DateOnly? night, bool isLight = true)
        => new(frame.FilePath, targetKey, targetName, night, isLight, frame.FileSize);

    private static FolderLevel Level(string path) => new(path, 1, 0, [], [], null);

    private static ContaminationIndex Index(params WbppCataloguePath[] rows)
        => ContaminationIndex.Build([Root], rows);

    private static IReadOnlyDictionary<DateOnly, IReadOnlyList<WbppFramePath>> Excluded(
        params (DateOnly Night, IReadOnlyList<WbppFramePath> Frames)[] entries)
        => entries.ToDictionary(e => e.Night, e => e.Frames);

    private static string StagingRootOfLength(int length) => @"D:\" + new string('s', length - 3);

    // The user's real tree shape: <root>\Date_<date>\LIGHT\<camera>\<target>\Angle_<n.nn>, with one
    // night's frames split across two cameras and two angle leaves (realdata-prep-report section 3.5).
    private static WbppFramePath[] MultiLeafFrames() =>
    [
        Frame(@"Date_2026-07-01\LIGHT\CamA\M31\Angle_71.61\a.fits"),
        Frame(@"Date_2026-07-01\LIGHT\CamA\M31\Angle_71.61\b.fits"),
        Frame(@"Date_2026-07-01\LIGHT\CamB\M31\Angle_8.20\c.fits"),
    ];

    // The verification fixture's own tree (fixtures/README.md), at 11,520 bytes a frame.
    private static WbppCataloguePath[] FixtureCatalogue() =>
    [
        .. FixtureFrames("m 31", 1).Select(f => Cat(f, "m31", "M 31", new DateOnly(2025, 1, 10))),
        .. FixtureFrames("m 31", 2).Select(f => Cat(f, "m31", "M 31", new DateOnly(2025, 2, 14))),
        .. FixtureFrames("m 31", 3).Select(f => Cat(f, "m31", "M 31", Night3)),
        .. FixtureFrames("m 33", 3).Select(f => Cat(f, "m33", "M 33", Night3)),
    ];

    private static WbppFramePath[] FixtureFrames(string target, int night) => (target, night) switch
    {
        ("m 31", 1) => [.. Enumerable.Range(0, 10).Select(i => Frame($@"M 31\2025-01-10\LIGHT\Ha\n1_{i}.fits"))],
        ("m 31", 2) => [.. Enumerable.Range(0, 8).Select(i => Frame($@"M 31\2025-02-14\LIGHT\OIII\n2_{i}.fits"))],
        ("m 31", 3) => [.. Enumerable.Range(0, 22).Select(i => Frame($@"M 31\2025-03-20\LIGHT\Ha\n3_{i}.fits"))],
        _ => [.. Enumerable.Range(0, 6).Select(i => Frame($@"M 31\2025-03-20\LIGHT\M33 $secondary\m33_{i}.fits"))],
    };

    private static JsonElement Golden()
    {
        var path = Path.Combine(
            FindRepoRoot(), "tests", "Fixtures", "golden", "levels", "levels.json");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    }

    // The oracle's cases name their own session's target through the catalogue: the pair whose date
    // is the session's date and whose paths include the session's files.
    private static string TargetOf(JsonElement session)
    {
        var first = session.GetProperty("files").EnumerateArray().First().GetString()!;
        return session.GetProperty("catalogue").EnumerateArray()
            .First(r => r.GetProperty("path").GetString() == first)
            .GetProperty("target").GetString()!;
    }

    private static string Win(string posix)
        => posix == "/root" ? Root : Root + posix["/root".Length..].Replace('/', '\\');

    private static string WinAny(string posix)
        => posix.StartsWith("/root", StringComparison.Ordinal) ? Win(posix) : @"D:" + posix.Replace('/', '\\');

    private static string StripComments(string source)
    {
        source = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
        source = Regex.Replace(source, @"//[^\r\n]*", "");
        return source;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GalactiLog.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new DirectoryNotFoundException("GalactiLog.sln not found above test output directory.");
    }
}
