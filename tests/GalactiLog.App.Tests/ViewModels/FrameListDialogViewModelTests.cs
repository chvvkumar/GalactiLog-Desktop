using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Metrics;
using GalactiLog.Core.Settings;
using GalactiLog.Core.Text;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

/// <summary>
/// Spec 12.4's Copy Frame List dialog (PAR-006): the mode and unmeasured truth table, the tally's
/// exact wording, the partial-load warning, the frame order, the three persisted choices, and the
/// roadmap's Verify clause that the dialog writes nothing.
/// </summary>
public class FrameListDialogViewModelTests
{
    private static readonly DateOnly Newer = new(2025, 12, 7);

    private static readonly DateOnly Older = new(2024, 1, 5);

    // A deviation applied equally to the three graded axes, so CombinedScore's weighted mean is
    // that deviation and BandForScore's ladder decides the band from one number. The mapping is
    // 50 - 16.7z: -2 gives 83.4 (better), 0 gives 50 (neutral), 1 gives 33.3 (watch) and 2 gives
    // 16.6 (reject).
    private static FrameGrading Graded(double z)
    {
        var grade = new MetricGrade(z, 2.1d);
        return new FrameGrading(
            SessionHfr: grade, RigHfr: grade,
            SessionEccentricity: grade, RigEccentricity: grade,
            SessionFwhm: grade, RigFwhm: grade,
            DetectedStars: grade,
            AduMedian: grade,
            GuidingRms: grade);
    }

    // Spec 12.4's unmeasured frame: the grading produced no score for it.
    private static FrameGrading Ungraded()
    {
        var none = new MetricGrade(null, null);
        return new FrameGrading(none, none, none, none, none, none, none, none, none);
    }

    private static FrameRow Frame(string name, FrameGrading? grading, DateOnly night)
        => PreviewModalViewModelTestFactory.Row(
            Guid.NewGuid(),
            $@"C:\Astro\M 31\{night:yyyy-MM-dd}\{name}",
            name,
            grading);

    private static SessionDetail Night(DateOnly date, params FrameRow[] frames)
        => SessionCardViewModelTestFactory.PopulatedDetail(date) with { Frames = frames };

    private sealed class Harness
    {
        public List<string> Clipboard { get; } = [];

        public List<(string GroupKey, DateOnly Date)> Queries { get; } = [];

        public List<bool> Closed { get; } = [];

        public Dictionary<DateOnly, SessionDetail?> Stored { get; } = [];

        public List<Func<DisplaySettings, DisplaySettings>> DisplayWrites { get; } = [];

        public TargetPageState TargetPage { get; set; } = null!;

        public FrameListDialogViewModel Page { get; set; } = null!;

        public Harness Settle()
        {
            Page.PendingLoad?.Wait(TimeSpan.FromSeconds(30));
            return this;
        }
    }

    private static Harness Open(
        IReadOnlyList<FrameListNight> nights,
        IReadOnlyDictionary<DateOnly, SessionDetail?>? stored = null,
        TargetPageSettings? settings = null)
    {
        var harness = new Harness();
        foreach (var pair in stored ?? new Dictionary<DateOnly, SessionDetail?>())
        {
            harness.Stored[pair.Key] = pair.Value;
        }

        harness.TargetPage = new TargetPageState(
            settings ?? new TargetPageSettings(),
            harness.DisplayWrites.Add);

        harness.Page = new FrameListDialogViewModel(
            TargetDetailViewModelTestFactory.ResolvedGroupKey,
            nights,
            (key, date) =>
            {
                lock (harness.Queries)
                {
                    harness.Queries.Add((key, date));
                }

                return harness.Stored.TryGetValue(date, out var detail) ? detail : null;
            },
            text =>
            {
                harness.Clipboard.Add(text);
                return Task.CompletedTask;
            },
            harness.TargetPage,
            post: action => action());

        harness.Page.CloseRequested += (_, result) => harness.Closed.Add(result);
        return harness.Settle();
    }

    private static Harness OpenOneNight(params FrameRow[] frames)
        => Open([new FrameListNight(Newer, Night(Newer, frames))]);

    [Theory]
    // Good mode keeps everything the grading did not reject.
    [InlineData(QualityBand.Better, FrameListMode.Good, true, true)]
    [InlineData(QualityBand.Neutral, FrameListMode.Good, true, true)]
    [InlineData(QualityBand.Watch, FrameListMode.Good, true, true)]
    [InlineData(QualityBand.Reject, FrameListMode.Good, true, false)]
    [InlineData(null, FrameListMode.Good, true, true)]
    [InlineData(null, FrameListMode.Good, false, false)]
    // Bad mode keeps the reject band only, and the box is ignored.
    [InlineData(QualityBand.Better, FrameListMode.Bad, true, false)]
    [InlineData(QualityBand.Neutral, FrameListMode.Bad, true, false)]
    [InlineData(QualityBand.Watch, FrameListMode.Bad, true, false)]
    [InlineData(QualityBand.Reject, FrameListMode.Bad, true, true)]
    [InlineData(null, FrameListMode.Bad, true, false)]
    [InlineData(null, FrameListMode.Bad, false, false)]
    public void TheModeAndUnmeasuredTruthTable(
        QualityBand? band,
        FrameListMode mode,
        bool includeUnmeasured,
        bool expected)
        => Assert.Equal(expected, FrameListDialogViewModel.Includes(band, mode, includeUnmeasured));

    [Fact]
    public void Bad_CopiesOnlyTheRejectBand()
    {
        var harness = OpenOneNight(
            Frame("better.fits", Graded(-2d), Newer),
            Frame("neutral.fits", Graded(0d), Newer),
            Frame("watch.fits", Graded(1d), Newer),
            Frame("reject.fits", Graded(2d), Newer));

        harness.Page.Mode = FrameListMode.Bad;

        Assert.Equal(["reject.fits"], harness.Page.SelectedPaths().Select(Path.GetFileName));
    }

    [Fact]
    public void Good_CopiesEverythingElse()
    {
        // task1-report.md departure 3: the watch band counts as good. The band exists to be looked
        // at, not to be excluded.
        var harness = OpenOneNight(
            Frame("better.fits", Graded(-2d), Newer),
            Frame("neutral.fits", Graded(0d), Newer),
            Frame("watch.fits", Graded(1d), Newer),
            Frame("reject.fits", Graded(2d), Newer));

        Assert.Equal(FrameListMode.Good, harness.Page.Mode);
        Assert.Equal(
            ["better.fits", "neutral.fits", "watch.fits"],
            harness.Page.SelectedPaths().Select(Path.GetFileName));
    }

    [Fact]
    public void Unmeasured_IsNeverBad()
    {
        // Both shapes of unmeasured: no grading at all, and a grading with no value on any axis.
        var harness = OpenOneNight(
            Frame("nograding.fits", null, Newer),
            Frame("noaxis.fits", Ungraded(), Newer),
            Frame("reject.fits", Graded(2d), Newer));

        harness.Page.Mode = FrameListMode.Bad;

        Assert.Equal(["reject.fits"], harness.Page.SelectedPaths().Select(Path.GetFileName));
    }

    [Fact]
    public void Unmeasured_JoinsTheGoodListOnlyWhileTheBoxIsChecked()
    {
        var harness = OpenOneNight(
            Frame("good.fits", Graded(0d), Newer),
            Frame("unmeasured.fits", null, Newer));

        Assert.True(harness.Page.IncludeUnmeasured);
        Assert.Equal(2, harness.Page.SelectedPaths().Count);

        harness.Page.IncludeUnmeasured = false;

        Assert.Equal(["good.fits"], harness.Page.SelectedPaths().Select(Path.GetFileName));
    }

    [Fact]
    public void TheIncludeUnmeasuredBox_IsIgnoredInBadMode()
    {
        var harness = OpenOneNight(
            Frame("unmeasured.fits", null, Newer),
            Frame("reject.fits", Graded(2d), Newer));

        harness.Page.Mode = FrameListMode.Bad;
        var withBox = harness.Page.SelectedPaths();
        harness.Page.IncludeUnmeasured = false;
        var withoutBox = harness.Page.SelectedPaths();

        Assert.Equal(withBox, withoutBox);
        Assert.Equal(["reject.fits"], withBox.Select(Path.GetFileName));

        // And the box is off the screen there, rather than greyed.
        Assert.False(harness.Page.IsIncludeUnmeasuredOffered);
    }

    [Fact]
    public void TheTally_MatchesTheSpecifiedWording()
    {
        var harness = OpenOneNight(
            Frame("better.fits", Graded(-2d), Newer),
            Frame("watch.fits", Graded(1d), Newer),
            Frame("reject.fits", Graded(2d), Newer));

        // Three frames, two good, one bad, none unmeasured, so no ", included" clause.
        Assert.Equal(
            "Good list: 2 of 3 frames. Graded 2 good, 1 bad, 0 unmeasured",
            harness.Page.TallyText);

        harness.Page.Mode = FrameListMode.Bad;

        Assert.Equal(
            "Bad list: 1 of 3 frames. Graded 2 good, 1 bad, 0 unmeasured",
            harness.Page.TallyText);
    }

    [Fact]
    public void TheTally_SaysIncluded_OnlyWhenAllThreeConditionsHold()
    {
        var harness = OpenOneNight(
            Frame("good.fits", Graded(0d), Newer),
            Frame("unmeasured.fits", null, Newer));

        // Good mode, box checked, u is not zero.
        Assert.Equal(
            "Good list: 2 of 2 frames. Graded 1 good, 0 bad, 1 unmeasured, included",
            harness.Page.TallyText);

        // Box cleared.
        harness.Page.IncludeUnmeasured = false;
        Assert.EndsWith("1 unmeasured", harness.Page.TallyText, StringComparison.Ordinal);

        // Bad mode, box checked.
        harness.Page.IncludeUnmeasured = true;
        harness.Page.Mode = FrameListMode.Bad;
        Assert.EndsWith("1 unmeasured", harness.Page.TallyText, StringComparison.Ordinal);

        // Good mode, box checked, u is zero.
        var graded = OpenOneNight(Frame("good.fits", Graded(0d), Newer));
        Assert.EndsWith("0 unmeasured", graded.Page.TallyText, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTally_EqualsTheFrameTablesOwnCounts()
    {
        // Spec 12.4: "the tally counts the same verdicts the frame table's bands carry, so the
        // dialog and the table cannot disagree". Both read FrameQuality over the same FrameGrading
        // values; the table folds better and neutral into one good count, which is what the
        // dialog's g is.
        var frames = new[]
        {
            Frame("better.fits", Graded(-2d), Newer),
            Frame("neutral.fits", Graded(0d), Newer),
            Frame("watch.fits", Graded(1d), Newer),
            Frame("reject.fits", Graded(2d), Newer),
            Frame("unmeasured.fits", null, Newer),
        };

        var harness = Open([new FrameListNight(Newer, Night(Newer, frames))]);

        var display = new DisplaySettings();
        using var table = new FrameTableViewModel(
            frames,
            display,
            new DisplayColumnWriter(() => display, value => display = value),
            new ShellIntegration(copyText: _ => Task.CompletedTask, start: _ => null),
            openPreview: null,
            new GeneralSettings { Timezone = "UTC", Use24HTime = true },
            getHeaders: _ => null);

        // The listed count is the good verdicts plus the unmeasured ones, because the box is
        // checked on a fresh profile; g, b and u are the table's own four counts folded the way
        // spec 12.4 folds them, with better and neutral already one count on the table.
        var good = table.GoodCount + table.WatchCount;
        Assert.Equal(
            $"Good list: {good + table.UngradedCount} of 5 frames. "
            + $"Graded {good} good, {table.RejectCount} bad, "
            + $"{table.UngradedCount} unmeasured, included",
            harness.Page.TallyText);

        // And the figures are the ones the case is about, not four zeroes.
        Assert.Equal(3, good);
        Assert.Equal(1, table.RejectCount);
        Assert.Equal(1, table.UngradedCount);
    }

    [Fact]
    public void APartiallyLoadedSelection_ShowsTheWarningAndListsTheFailedDates()
    {
        var harness = Open(
            [
                new FrameListNight(Newer, Night(Newer, Frame("a.fits", Graded(0d), Newer))),
                new FrameListNight(Older, null),
            ],
            new Dictionary<DateOnly, SessionDetail?> { [Older] = null });

        Assert.True(harness.Page.HasPartialLoad);
        Assert.Equal(1, harness.Page.FailedNightCount);
        Assert.Equal(2, harness.Page.NightCount);
        Assert.Equal([Older], harness.Page.FailedDates);
        Assert.Contains("1 of 2", harness.Page.PartialLoadText, StringComparison.Ordinal);
        Assert.Contains("2024-01-05", harness.Page.PartialLoadText, StringComparison.Ordinal);
        Assert.Contains("only the loaded nights", harness.Page.PartialLoadText, StringComparison.Ordinal);
    }

    [Fact]
    public void APartiallyLoadedSelection_StillCopiesTheLoadedNights()
    {
        var harness = Open(
            [
                new FrameListNight(Newer, Night(Newer, Frame("a.fits", Graded(0d), Newer))),
                new FrameListNight(Older, null),
            ],
            new Dictionary<DateOnly, SessionDetail?> { [Older] = null });

        Assert.Equal(["a.fits"], harness.Page.SelectedPaths().Select(Path.GetFileName));
        Assert.True(harness.Page.CopyCommand.CanExecute(null));
    }

    [Fact]
    public void AnAlreadyLoadedNight_IssuesNoSecondQuery()
    {
        var harness = Open(
            [
                new FrameListNight(Newer, Night(Newer, Frame("a.fits", Graded(0d), Newer))),
                new FrameListNight(Older, null),
            ],
            new Dictionary<DateOnly, SessionDetail?>
            {
                [Older] = Night(Older, Frame("b.fits", Graded(0d), Older)),
            });

        // Exactly one query, for the night the page had not loaded.
        var query = Assert.Single(harness.Queries);
        Assert.Equal(Older, query.Date);
        Assert.Equal(TargetDetailViewModelTestFactory.ResolvedGroupKey, query.GroupKey);
    }

    [Fact]
    public void TheFrames_AreInLedgerOrderThenCaptureOrder()
    {
        // Nights newest first, which is the ledger's own order at the call site, and each night's
        // frames in the order the query returned them, which is capture order.
        var harness = Open(
            [
                new FrameListNight(
                    Newer,
                    Night(Newer, Frame("n1.fits", Graded(0d), Newer), Frame("n2.fits", Graded(0d), Newer))),
                new FrameListNight(
                    Older,
                    Night(Older, Frame("o1.fits", Graded(0d), Older), Frame("o2.fits", Graded(0d), Older))),
            ]);

        Assert.Equal(
            ["n1.fits", "n2.fits", "o1.fits", "o2.fits"],
            harness.Page.SelectedPaths().Select(Path.GetFileName));
    }

    [Fact]
    public async Task Copy_WritesTheRenderedListToTheClipboard_AndCloses()
    {
        var harness = OpenOneNight(
            Frame("a.fits", Graded(0d), Newer),
            Frame("b.fits", Graded(0d), Newer));

        await harness.Page.CopyCommand.ExecuteAsync(null);

        Assert.Equal(
            FrameListFormats.Render(FrameListFormat.Paths, harness.Page.SelectedPaths()),
            Assert.Single(harness.Clipboard));
        Assert.True(Assert.Single(harness.Closed));
    }

    [Fact]
    public async Task Copy_RendersTheChosenFormat()
    {
        var harness = OpenOneNight(Frame("a.fits", Graded(0d), Newer));

        harness.Page.SelectedFormat = FrameListDialogViewModel.AllFormats
            .Single(option => option.Value == FrameListFormat.Explorer);
        await harness.Page.CopyCommand.ExecuteAsync(null);

        Assert.Equal("\"a.fits\"", Assert.Single(harness.Clipboard));
    }

    [Fact]
    public void Copy_IsDisabledOnAnEmptyList()
    {
        var harness = OpenOneNight(Frame("reject.fits", Graded(2d), Newer));

        // Good mode over a night that is all reject.
        Assert.Empty(harness.Page.SelectedPaths());
        Assert.False(harness.Page.CopyCommand.CanExecute(null));
    }

    [Fact]
    public async Task Copy_ExecutedDirectlyOnAnEmptyList_WritesNothing()
    {
        // TRACKING section 6 item 13: RelayCommand.Execute ignores CanExecute.
        var harness = OpenOneNight(Frame("reject.fits", Graded(2d), Newer));

        await harness.Page.CopyCommand.ExecuteAsync(null);

        Assert.Empty(harness.Clipboard);
        Assert.Empty(harness.Closed);
    }

    [Fact]
    public void TheThreeChoices_PersistThroughTheDisplayDocument()
    {
        var harness = OpenOneNight(Frame("a.fits", Graded(0d), Newer));

        harness.Page.Mode = FrameListMode.Bad;
        harness.Page.IncludeUnmeasured = false;
        harness.Page.SelectedFormat = FrameListDialogViewModel.AllFormats
            .Single(option => option.Value == FrameListFormat.Names);

        // The mutations are recorded rather than applied, the way the production writer applies
        // them to the document as loaded, so the shape is proved by applying them here.
        var document = new DisplaySettings();
        foreach (var mutation in harness.DisplayWrites)
        {
            document = mutation(document);
        }

        Assert.Equal("bad", document.TargetPage.FrameListMode);
        Assert.False(document.TargetPage.FrameListIncludeUnmeasured);
        Assert.Equal("names", document.TargetPage.FrameListFormat);
    }

    [Fact]
    public void AStoredProfile_OpensOnItsStoredChoices()
    {
        var harness = Open(
            [new FrameListNight(Newer, Night(Newer, Frame("a.fits", Graded(0d), Newer)))],
            settings: new TargetPageSettings
            {
                FrameListFormat = "explorer",
                FrameListMode = "bad",
                FrameListIncludeUnmeasured = false,
            });

        Assert.Equal(FrameListMode.Bad, harness.Page.Mode);
        Assert.False(harness.Page.IncludeUnmeasured);
        Assert.Equal(FrameListFormat.Explorer, harness.Page.Format);

        // Reading the document is not writing it back.
        Assert.Empty(harness.DisplayWrites);
    }

    [Fact]
    public void AFreshProfile_IsGoodModeIncludeUnmeasuredAndPaths()
    {
        var harness = OpenOneNight(Frame("a.fits", Graded(0d), Newer));

        Assert.Equal(FrameListMode.Good, harness.Page.Mode);
        Assert.True(harness.Page.IncludeUnmeasured);
        Assert.Equal(FrameListFormat.Paths, harness.Page.Format);
        Assert.True(harness.Page.IsGoodMode);
        Assert.True(harness.Page.IsIncludeUnmeasuredOffered);
    }

    [Fact]
    public async Task TheDialog_WritesNoFile()
    {
        // The roadmap's Verify clause, behaviourally. FileSafetyTest proves it structurally over
        // src/**; this runs a copy against a recording clipboard with a temp directory beside it
        // and asserts the directory is untouched.
        var root = Path.Combine(Path.GetTempPath(), "galactilog-frame-list-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var before = Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories);
            var harness = Open(
                [
                    new FrameListNight(
                        Newer,
                        Night(
                            Newer,
                            PreviewModalViewModelTestFactory.Row(
                                Guid.NewGuid(),
                                Path.Combine(root, "a.fits"),
                                "a.fits",
                                Graded(0d)))),
                ]);

            harness.Page.Mode = FrameListMode.Bad;
            harness.Page.Mode = FrameListMode.Good;
            await harness.Page.CopyCommand.ExecuteAsync(null);

            Assert.Single(harness.Clipboard);
            Assert.Equal(before, Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories));
            Assert.Empty(before);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Cancel_ClosesWithoutCopying()
    {
        var harness = OpenOneNight(Frame("a.fits", Graded(0d), Newer));

        harness.Page.CancelCommand.Execute(null);

        Assert.Empty(harness.Clipboard);
        Assert.False(Assert.Single(harness.Closed));
    }
}
