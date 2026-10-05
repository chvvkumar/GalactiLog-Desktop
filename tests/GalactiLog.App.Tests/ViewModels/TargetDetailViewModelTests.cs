using Avalonia.Controls;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Microsoft.Extensions.Logging;
using Xunit;
using static GalactiLog.App.Tests.Views.TargetDetail.Parts.NightPartsTestKit;
using Factory = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

// Phase 6 Task 3. Roadmap Verify line: autosave fires once after the debounce window rather than
// per keystroke, rename sets name_locked, and the header block binds every listed field. Plain
// xunit facts with fake query delegates, fake shell seams, a fake debounce and an inline post
// seam: no window, no dispatcher, no database (design-spec 18.3).
public class TargetDetailViewModelTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    [Fact]
    public void Settle_ALoadPastTheBudget_FailsNamingTheBudget()
    {
        // A failure is a Settle that returns silently while the page load is still running.
        using var release = new ManualResetEventSlim(false);
        using var harness = Factory.Create(get: key =>
        {
            release.Wait(Budget);
            return Factory.PopulatedDetail();
        });

        try
        {
            var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => harness.Settle(TimeSpan.FromMilliseconds(1)));
            Assert.Contains("The page load did not finish within 00:00:00.0010000", failure.Message);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void Load_PublishesHeaderTotalsAndSessions()
    {
        using var harness = Factory.Create().Settle();
        var page = harness.ViewModel;

        Assert.False(page.IsLoading);
        Assert.False(page.IsMissing);
        Assert.Null(page.LastFailure);
        Assert.NotNull(page.Header);
        Assert.NotNull(page.Totals);
        Assert.Equal(2, page.Sessions.Count);

        // Newest session first (spec 12.4), in the order the query returned.
        Assert.Equal(
            [Factory.LastSession, Factory.FirstSession],
            page.Sessions.Select(card => card.SessionDate));
    }

    // F18 follow-up. The page is built on the UI thread from a row click and the query is a
    // synchronous SQLite read, so the read must not happen on the constructing thread. Proven by
    // parking the query and observing that the constructor returned anyway, not by comparing
    // thread ids: a thread-id comparison is only sound while the constructing thread stays
    // occupied, and keeping it occupied means a blocking wait that needs the pool to make progress
    // at the same time (TRACKING section 2 item 8). This form needs neither.
    [Fact]
    public async Task Load_RunsOffTheConstructingThread()
    {
        using var release = new ManualResetEventSlim(false);
        using var harness = Factory.Create(get: _ =>
        {
            release.Wait(Budget);
            return Factory.PopulatedDetail();
        });

        // The constructor returned while the query is parked. A synchronous read would have parked
        // the constructor itself, and nothing has published yet.
        Assert.False(harness.ViewModel.PendingLoad!.IsCompleted);
        Assert.Null(harness.ViewModel.Header);

        release.Set();
        await harness.ViewModel.PendingLoad!;

        Assert.Equal(1, harness.Loads);
        Assert.NotNull(harness.ViewModel.Header);
    }

    [Fact]
    public void Load_NullDetail_SetsIsMissing()
    {
        using var harness = Factory.Create(get: _ => null).Settle();
        var page = harness.ViewModel;

        // Ruling Q4: a stale key renders a callout naming the group, never a blank page and never
        // an exception on the UI thread.
        Assert.True(page.IsMissing);
        Assert.Null(page.Header);
        Assert.Null(page.Totals);
        Assert.Contains(Factory.ResolvedGroupKey, page.MissingText);

        // And every action is off, because there is nothing to act on.
        Assert.False(page.BeginRenameCommand.CanExecute(null));
        Assert.False(page.ReResolveCommand.CanExecute(null));
        Assert.False(page.CopyFrameListCommand.CanExecute(null));
        Assert.False(page.RevealFolderCommand.CanExecute(null));
    }

    [Fact]
    public void Load_Throwing_SetsLastFailure_AndLeavesThePageUsable()
    {
        var failure = new InvalidOperationException("database is locked");
        using var harness = Factory.Create(get: _ => throw failure).Settle();
        var page = harness.ViewModel;

        Assert.Same(failure, page.LastFailure);
        Assert.False(page.IsLoading);
        Assert.False(page.IsMissing);
        Assert.Contains(harness.Logger.Entries, entry => entry.Level == LogLevel.Warning);

        // Usable: the page did not throw on the calling thread, and a later load can still
        // succeed.
        Assert.NotNull(page.MissingText);
    }

    [Fact]
    public void Header_BindsEveryListedField()
    {
        using var harness = Factory.Create().Settle();
        var header = harness.ViewModel.Header!;

        Assert.Equal("M 31", header.Name);
        Assert.Equal(Factory.TargetId, header.TargetId);
        Assert.Equal(Factory.ResolvedGroupKey, header.GroupKey);
        Assert.Equal(["NGC 224", "Andromeda Galaxy"], header.Aliases);
        Assert.True(header.HasAliases);
        Assert.Equal("G", header.ObjectType);
        Assert.True(header.HasObjectType);
        Assert.Equal("Galaxy", header.ObjectCategory);
        Assert.Equal("And", header.Constellation);
        Assert.True(header.HasConstellation);

        // Sexagesimal from degrees: HH:mm:ss.s and +DD:MM:SS.
        Assert.Equal("00:42:44.3", header.RaText);
        Assert.Equal("+41:16:09", header.DecText);
        Assert.Equal("189.1' x 61.7'", header.SizeText);
        Assert.Equal("145 deg", header.PositionAngleText);
        Assert.Equal("3.44", header.VMagText);
        Assert.Equal("22.2", header.SurfaceBrightnessText);
        Assert.Equal("!!! Andromeda Gx; vB, eL, eE", header.SacDescription);
        Assert.True(header.HasSacDescription);
        Assert.Equal("Naked eye object", header.SacNotes);
        Assert.True(header.HasSacNotes);

        Assert.Equal(["Messier", "NGC"], header.CatalogMemberships.Select(badge => badge.CatalogName));
        Assert.Equal(["31", "224"], header.CatalogMemberships.Select(badge => badge.CatalogNumber));
        Assert.True(header.HasCatalogMemberships);

        Assert.False(header.NameLocked);
        Assert.False(header.IsUserDefined);
        Assert.False(header.HasReferenceThumbnail);
    }

    [Fact]
    public void Header_AbsentFields_ReportEmptyAndHideTheirRows()
    {
        var sparse = Factory.PopulatedHeader() with
        {
            Aliases = [],
            ObjectType = null,
            Constellation = null,
            Ra = null,
            Dec = null,
            SizeMajor = null,
            SizeMinor = null,
            PositionAngle = null,
            VMag = null,
            SurfaceBrightness = null,
            SacDescription = null,
            SacNotes = "   ",
            CatalogMemberships = [],
        };

        using var harness = Factory.Create(get: _ => Factory.PopulatedDetail(header: sparse)).Settle();
        var header = harness.ViewModel.Header!;

        Assert.False(header.HasAliases);
        Assert.False(header.HasObjectType);
        Assert.False(header.HasConstellation);
        Assert.False(header.HasRa);
        Assert.False(header.HasDec);
        Assert.False(header.HasSize);
        Assert.False(header.HasPositionAngle);
        Assert.False(header.HasVMag);
        Assert.False(header.HasSurfaceBrightness);
        Assert.False(header.HasSacDescription);
        Assert.False(header.HasSacNotes);
        Assert.False(header.HasCatalogMemberships);

        // Never null, so there is no empty case to render (Task 1 handoff).
        Assert.Equal("Galaxy", header.ObjectCategory);
    }

    [Fact]
    public void Header_SizeWithNoMinorAxis_RendersOneValue()
    {
        var header = Factory.PopulatedHeader() with { SizeMinor = null };

        using var harness = Factory.Create(get: _ => Factory.PopulatedDetail(header: header)).Settle();

        Assert.Equal("189.1'", harness.ViewModel.Header!.SizeText);
    }

    [Fact]
    public void Header_NegativeDeclination_CarriesItsSign()
    {
        var header = Factory.PopulatedHeader() with { Dec = -5.391666666666667d };

        using var harness = Factory.Create(get: _ => Factory.PopulatedDetail(header: header)).Settle();

        Assert.Equal("-05:23:30", harness.ViewModel.Header!.DecText);
    }

    [Fact]
    public void Header_UnresolvedGroup_ShowsTheObjectStringAndTheUnresolvedCategory()
    {
        using var harness = Unresolved().Settle();
        var header = harness.ViewModel.Header!;

        Assert.Equal("NGC 7331 field", header.Name);
        Assert.Equal(TargetListingCriteria.UnresolvedCategory, header.ObjectCategory);
        Assert.Null(header.TargetId);
        Assert.False(header.HasRa);
        Assert.False(header.HasCatalogMemberships);
    }
    [Fact]
    public void Totals_FormatEveryFigure_AndBothDisclosures()
    {
        using var harness = Factory.Create().Settle();
        var totals = harness.ViewModel.Totals!;

        // The frame count and the arcsecond mean are what the Details drawer binds. The integration
        // hours, the night count and the two session dates are constructor locals now, because the
        // log line is their only reader (coordinator round 2), and
        // Totals_CarryTheLogLineAndTheLedgerRow asserts all four inside the sentence the view
        // renders; the two joined name strings went with them and the filters are pinned as
        // swatches.
        Assert.Equal("148", totals.FrameCountText);
        Assert.Equal("1.85 arcsec", totals.AvgHfrArcsecText);

        // The five unit-carrying Avg*Text twins the retired card bound went with it (phase review
        // P2-5); the ledger's six unit-free figures are what the page renders and they are pinned
        // by Totals_CarryTheLogLineAndTheLedgerRow.
        Assert.Equal("2.34", totals.LedgerAvgHfrText);
        Assert.Equal("0.42", totals.LedgerAvgEccentricityText);
        Assert.Equal("1.90", totals.LedgerAvgFwhmText);
        Assert.Equal("0.45", totals.LedgerAvgGuidingRmsText);
        Assert.Equal("1,500", totals.LedgerAvgDetectedStarsText);

        // Spec 12.4's disclosure, which the drawer binds. The eccentricity twin is gone with the
        // card; its two facts are in LogLineDisclosureText, asserted below.
        Assert.True(totals.HasHfrArcsecDisclosure);
        Assert.Equal("23 frames without a plate scale", totals.HfrArcsecDisclosure);
        Assert.Contains("eccentricity pools the 143 frames measured by header", totals.LogLineDisclosureText);
    }

    [Fact]
    public void Totals_ZeroExclusionCounts_HideTheirDisclosures()
    {
        var totals = Factory.PopulatedTotals(hfrArcsecExcluded: 0, eccentricityExcluded: 0);

        using var harness = Factory.Create(get: _ => Factory.PopulatedDetail(totals: totals)).Settle();
        var projected = harness.ViewModel.Totals!;

        Assert.False(projected.HasHfrArcsecDisclosure);
        Assert.Equal("", projected.HfrArcsecDisclosure);
        Assert.False(projected.HasLogLineDisclosure);
        Assert.Equal("", projected.LogLineDisclosureText);
    }

    [Fact]
    public void Totals_EccentricitySourceNotRecorded_IsWordedNotBlank()
    {
        var totals = Factory.PopulatedTotals(eccentricitySource: null);

        using var harness = Factory.Create(get: _ => Factory.PopulatedDetail(totals: totals)).Settle();

        // Task 1 handoff: the modal source can be null while the figure is real, so the
        // disclosure needs a wording for it rather than an empty gap.
        Assert.Contains(
            "eccentricity pools the 143 frames measured by an unrecorded source",
            harness.ViewModel.Totals!.LogLineDisclosureText);
    }

    [Fact]
    public void Totals_AbsentMetrics_RenderEmptyRatherThanZero()
    {
        var totals = Factory.PopulatedTotals() with
        {
            AvgHfr = null,
            AvgHfrArcsec = null,
            AvgEccentricity = null,
            AvgFwhm = null,
            AvgGuidingRmsArcsec = null,
            AvgDetectedStars = null,
            FiltersUsed = [],
            Equipment = [],
        };

        using var harness = Factory.Create(get: _ => Factory.PopulatedDetail(totals: totals)).Settle();
        var projected = harness.ViewModel.Totals!;

        Assert.False(projected.HasAvgHfrArcsec);
        Assert.Equal("", projected.LedgerAvgHfrText);
        Assert.Equal("", projected.LedgerAvgEccentricityText);
        Assert.Equal("", projected.LedgerAvgFwhmText);
        Assert.Equal("", projected.LedgerAvgGuidingRmsText);
        Assert.Equal("", projected.LedgerAvgDetectedStarsText);
        Assert.Empty(projected.FilterSwatches);
    }

    [Fact]
    public void Notes_AutosaveFiresOnceAfterTheDebounceWindow_NotPerKeystroke()
    {
        using var harness = Factory.Create().Settle();
        var notes = harness.ViewModel.Notes;

        // The load adopted the stored note; the box is clean until the user types.
        Assert.Equal("an existing note", notes.Text);
        Assert.False(notes.IsDirty);

        notes.Text = "an existing note w";
        notes.Text = "an existing note with";
        notes.Text = "an existing note with more";
        harness.SettleNotes();

        // Roadmap Verify line: three keystrokes, one write, carrying the final text.
        var write = Assert.Single(harness.NoteWrites);
        Assert.Equal("an existing note with more", write.Notes);
    }

    [Fact]
    public void Notes_SaveTargetsTheTargetId()
    {
        using var harness = Factory.Create().Settle();

        harness.ViewModel.Notes.Text = "typed";
        harness.SettleNotes();

        Assert.Equal(Factory.TargetId, Assert.Single(harness.NoteWrites).TargetId);
    }

    [Fact]
    public void Notes_UnresolvedGroup_AreDisabled()
    {
        using var harness = Unresolved().Settle();

        // No targets row to key a note on, so the box is disabled and the write is a no-op even
        // if something reaches it (Task 1 handoff).
        Assert.False(harness.ViewModel.CanEditNotes);

        harness.ViewModel.Notes.Text = "typed anyway";
        harness.SettleNotes();

        Assert.Empty(harness.NoteWrites);
    }

    [Fact]
    public void Notes_ScanDrivenRefresh_AdoptsAnExternallyChangedNote()
    {
        var note = "an existing note";
        using var harness = Factory.Create(get: _ => Factory.PopulatedDetail(header: Factory.PopulatedHeader(note))).Settle();
        Assert.Equal("an existing note", harness.ViewModel.Notes.Text);

        note = "changed by the CLI";
        harness.ViewModel.Notes.Reseed(note);

        Assert.Equal("changed by the CLI", harness.ViewModel.Notes.Text);
    }

    [Fact]
    public async Task Rename_SetsNameLocked()
    {
        using var harness = Factory.Create().Settle();
        var page = harness.ViewModel;

        Assert.True(page.BeginRenameCommand.CanExecute(null));
        page.BeginRenameCommand.Execute(null);
        Assert.True(page.IsRenaming);
        Assert.Equal("M 31", page.RenameText);

        page.RenameText = "  Andromeda  ";
        await page.CommitRenameCommand.ExecuteAsync(null);

        // Roadmap Verify line: the rename delegate ran and the header reports NameLocked true.
        Assert.Equal((Factory.TargetId, "Andromeda"), Assert.Single(harness.Renames));
        Assert.Equal("Andromeda", page.Header!.Name);
        Assert.True(page.Header.NameLocked);
        Assert.False(page.IsRenaming);
        Assert.Null(page.RenameError);

        // Without a reload: spec 12.4's inline rename is not a page refresh.
        Assert.Equal(1, harness.Loads);
    }

    [Fact]
    public async Task Rename_NameTaken_KeepsEditModeAndReportsTheError()
    {
        using var harness = Factory.Create().Settle();
        harness.RenameResult = RenameOutcome.NameTaken;
        var page = harness.ViewModel;

        page.BeginRenameCommand.Execute(null);
        page.RenameText = "M 42";
        await page.CommitRenameCommand.ExecuteAsync(null);

        Assert.True(page.IsRenaming);
        Assert.NotNull(page.RenameError);
        Assert.Contains("M 42", page.RenameError);
        Assert.Equal("M 31", page.Header!.Name);
        Assert.False(page.Header.NameLocked);
    }

    [Fact]
    public async Task Rename_NotFound_ReportsTheError()
    {
        using var harness = Factory.Create().Settle();
        harness.RenameResult = RenameOutcome.NotFound;
        var page = harness.ViewModel;

        page.BeginRenameCommand.Execute(null);
        page.RenameText = "Andromeda";
        await page.CommitRenameCommand.ExecuteAsync(null);

        Assert.NotNull(page.RenameError);
        Assert.Equal("M 31", page.Header!.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rename_EmptyOrWhitespace_IsRefused(string name)
    {
        using var harness = Factory.Create().Settle();
        var page = harness.ViewModel;

        page.BeginRenameCommand.Execute(null);
        page.RenameText = name;

        Assert.False(page.CommitRenameCommand.CanExecute(null));

        // And the guard inside the command holds too, so a keyboard accelerator cannot bypass
        // the disabled button.
        await page.CommitRenameCommand.ExecuteAsync(null);
        Assert.Empty(harness.Renames);
        Assert.NotNull(page.RenameError);
        Assert.Equal("M 31", page.Header!.Name);
    }

    [Fact]
    public void Rename_Cancel_RestoresTheDisplayedName()
    {
        using var harness = Factory.Create().Settle();
        var page = harness.ViewModel;

        page.BeginRenameCommand.Execute(null);
        page.RenameText = "something the user changed their mind about";
        page.CancelRenameCommand.Execute(null);

        Assert.False(page.IsRenaming);
        Assert.Equal("M 31", page.RenameText);
        Assert.Equal("M 31", page.Header!.Name);
        Assert.Null(page.RenameError);
        Assert.Empty(harness.Renames);
    }

    [Fact]
    public void Rename_UnresolvedGroup_IsDisabled()
    {
        using var harness = Unresolved().Settle();

        Assert.False(harness.ViewModel.BeginRenameCommand.CanExecute(null));
        Assert.False(harness.ViewModel.CommitRenameCommand.CanExecute(null));
    }

    // FIXER LIST item 13: the action is offered again now that the re-enrichment writer exists
    // and it can genuinely change the page.
    [Fact]
    public void ReResolveAvailable_IsTrue()
    {
        Assert.True(TargetDetailViewModel.ReResolveAvailable);
    }

    [Fact]
    public async Task ReResolve_RunsOffTheUiThread_AndReportsTheOutcome()
    {
        using var harness = Factory.Create().Settle();
        using var release = new ManualResetEventSlim(false);
        harness.ReResolveRelease = release;
        var page = harness.ViewModel;

        Assert.True(page.ReResolveCommand.CanExecute(null));
        page.ReResolveCommand.Execute(null);

        // F18 follow-up: Execute returned while the delegate is parked, so the delegate is not
        // running on this thread. Resolve consults the network and the database; it must never run
        // on the UI thread. No blocking wait and no thread-id comparison.
        Assert.False(page.ReResolveCommand.ExecutionTask!.IsCompleted);

        release.Set();
        await page.ReResolveCommand.ExecutionTask!.WaitAsync(Budget);

        Assert.Equal(
            "M 31 resolved from Offline. Updated catalog id, object type.", page.ReResolveStatus);
    }

    [Fact]
    public async Task Dispose_WhileAReloadRefillsTheLedger_LeavesNoLiveCard()
    {
        // The reload a re-resolve starts publishes on a pool thread under the inline post. It is
        // parked after its first Add, inside ReplaceSessions, while the page is disposed here. A
        // failure is Dispose throwing on the collection, or a card left in Sessions or undisposed.
        using var harness = Factory.Create().Settle();
        var page = harness.ViewModel;
        var cards = page.Sessions.ToList();
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var parked = 0;
        page.Sessions.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add
                && Interlocked.Exchange(ref parked, 1) == 0)
            {
                entered.Set();
                release.Wait(Budget);
            }
        };

        await page.ReResolveCommand.ExecuteAsync(null);
        var reload = page.PendingLoad!;
        Assert.True(entered.Wait(Budget));

        try
        {
            page.Dispose();
        }
        finally
        {
            release.Set();
        }

        await reload.WaitAsync(Budget);

        Assert.Empty(page.Sessions);
        Assert.All(cards, card => Assert.True(card.IsDisposed));
    }

    [Fact]
    public async Task ReResolve_ExecutedTwice_RunsTheResolverOnce()
    {
        // Phase 7 fixer item 7 (phase finding 7): RelayCommand.Execute ignores CanExecute, so the
        // one-at-a-time rule needs a body guard. Without it the second click resolves the same
        // target again and the slower answer's status sentence overwrites the faster one's.
        using var harness = Factory.Create().Settle();
        using var release = new ManualResetEventSlim(false);
        harness.ReResolveRelease = release;
        var page = harness.ViewModel;

        page.ReResolveCommand.Execute(null);
        var first = page.ReResolveCommand.ExecutionTask!;
        Assert.True(harness.ReResolveEntered.Wait(Budget));

        // The second click lands while the first delegate is parked. It replaces the command's
        // ExecutionTask, which is why the first one is held onto above.
        page.ReResolveCommand.Execute(null);

        release.Set();
        await first.WaitAsync(Budget);

        Assert.Equal((Factory.TargetId, "M 31"), Assert.Single(harness.ReResolves));

        // And the guard is released, so a later click still works.
        harness.ReResolveRelease = null;
        await page.ReResolveCommand.ExecuteAsync(null);
        Assert.Equal(2, harness.ReResolves.Count);
    }

    // The widened delegate (Phase 7 Task 7): re-enrichment writes to a row, so it needs the id as
    // well as the name.
    [Fact]
    public async Task ReResolve_PassesTheTargetIdAndName()
    {
        using var harness = Factory.Create().Settle();

        await harness.ViewModel.ReResolveCommand.ExecuteAsync(null);

        Assert.Equal((Factory.TargetId, "M 31"), Assert.Single(harness.ReResolves));
    }

    [Fact]
    public async Task ReResolve_Enriched_ReloadsThePageAndNotifiesTheDashboard()
    {
        using var harness = Factory.Create().Settle();
        harness.ReResolveOutcome = (true, "M 31 resolved from Simbad. Updated object type.");
        var renamed = 0;
        harness.ViewModel.TargetRenamed += (_, _) => renamed++;

        var before = harness.Loads;
        await harness.ViewModel.ReResolveCommand.ExecuteAsync(null);
        harness.Settle();

        // The header block's own fields are what changed, so the page re-reads them, and the
        // dashboard row behind it is stale for the same reason a rename makes it stale (F10).
        Assert.Equal(before + 1, harness.Loads);
        Assert.Equal(1, renamed);
        Assert.Equal("M 31 resolved from Simbad. Updated object type.", harness.ViewModel.ReResolveStatus);
    }

    [Fact]
    public async Task ReResolve_Unchanged_DoesNotReload()
    {
        using var harness = Factory.Create().Settle();
        harness.ReResolveOutcome = (false, "M 31 resolved from Offline. Nothing changed.");
        var renamed = 0;
        harness.ViewModel.TargetRenamed += (_, _) => renamed++;

        var before = harness.Loads;
        await harness.ViewModel.ReResolveCommand.ExecuteAsync(null);

        Assert.Equal(before, harness.Loads);
        Assert.Equal(0, renamed);
        Assert.Equal("M 31 resolved from Offline. Nothing changed.", harness.ViewModel.ReResolveStatus);
    }

    [Fact]
    public async Task ReResolve_Suppressed_ReportsIt()
    {
        // Spec 5.3: a user-defined target suppresses catalog enrichment, which includes every
        // solar-system target the resolver creates from a name pattern.
        using var harness = Factory.Create().Settle();
        harness.ReResolveOutcome = (false, "Jupiter is user-defined, so catalog enrichment is suppressed.");

        var before = harness.Loads;
        await harness.ViewModel.ReResolveCommand.ExecuteAsync(null);

        Assert.Equal(before, harness.Loads);
        Assert.Equal(
            "Jupiter is user-defined, so catalog enrichment is suppressed.",
            harness.ViewModel.ReResolveStatus);
    }

    [Fact]
    public async Task ReResolve_StillUnresolved_ReportsIt()
    {
        using var harness = Factory.Create().Settle();
        harness.ReResolveOutcome = (false, "M 31 still resolves to nothing.");

        var before = harness.Loads;
        await harness.ViewModel.ReResolveCommand.ExecuteAsync(null);

        Assert.Equal(before, harness.Loads);
        Assert.Equal("M 31 still resolves to nothing.", harness.ViewModel.ReResolveStatus);
    }

    [Fact]
    public async Task ReResolve_Throwing_IsSurfacedNotRethrown()
    {
        using var harness = Factory.Create().Settle();
        harness.ReResolveThrows = new HttpRequestException("SIMBAD is unreachable");
        var page = harness.ViewModel;

        var before = harness.Loads;
        await page.ReResolveCommand.ExecuteAsync(null);

        Assert.NotNull(page.ReResolveStatus);
        Assert.Equal(before, harness.Loads);
        Assert.Contains(harness.Logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public void ReResolve_UnresolvedGroup_IsDisabled()
    {
        using var harness = Unresolved().Settle();

        Assert.False(harness.ViewModel.ReResolveCommand.CanExecute(null));
    }

    // Ruling on command guards: RelayCommand.Execute ignores CanExecute, so the body guards too.
    // An obj: group has no targets row to re-enrich.
    [Fact]
    public async Task ReResolve_IsDisabledForAnUnresolvedGroup()
    {
        using var harness = Unresolved().Settle();

        Assert.False(harness.ViewModel.ReResolveCommand.CanExecute(null));
        await harness.ViewModel.ReResolveCommand.ExecuteAsync(null);

        Assert.Empty(harness.ReResolves);
        Assert.Null(harness.ViewModel.ReResolveStatus);
    }

    [Fact]
    public async Task CopyFrameList_OpensTheDialogOverTheCheckedNights()
    {
        // P14A Task 4 (spec 12.4, PAR-006). Until this phase the command copied every frame path
        // of the whole target with no dialog (ruling Q11). It now opens the Copy Frame List dialog
        // over the nights the ledger's selection column has checked, and the dialog's own default
        // format renders the same string that command used to write.
        using var harness = Factory.Create().Settle();
        harness.ViewModel.Sessions[0].IsChecked = true;

        Assert.True(harness.ViewModel.CopyFrameListCommand.CanExecute(null));
        await harness.ViewModel.CopyFrameListCommand.ExecuteAsync(null);

        var opened = Assert.Single(harness.OpenedFrameLists);
        Assert.Equal(Factory.ResolvedGroupKey, opened.GroupKey);
        Assert.Equal(
            [harness.ViewModel.Sessions[0].SessionDate],
            opened.Nights.Select(night => night.Date));
        Assert.Empty(harness.Clipboard);
    }

    [Fact]
    public void CopyFrameList_NoCheckedNight_IsDisabled()
    {
        // Spec 12.4: "whose command is disabled while nothing is checked". The old rule was "the
        // target has frames", which is now the dialog's own concern.
        using var harness = Factory.Create(get: _ => Factory.PopulatedDetail(framePaths: [])).Settle();

        Assert.False(harness.ViewModel.CopyFrameListCommand.CanExecute(null));

        harness.ViewModel.Sessions[0].IsChecked = true;

        Assert.True(harness.ViewModel.CopyFrameListCommand.CanExecute(null));
    }

    [Fact]
    public void RevealFolder_LaunchesTheExplorerVerbWithTheResolvedFolder()
    {
        using var harness = Factory.Create().Settle();

        Assert.True(harness.ViewModel.RevealFolderCommand.CanExecute(null));
        harness.ViewModel.RevealFolderCommand.Execute(null);

        var info = Assert.Single(harness.Launched);
        Assert.Equal("explorer.exe", info.FileName);

        // Ruling Q10: the most recent frame by capture date, which is the last entry of the
        // capture-ordered list.
        Assert.Equal($"/select,\"{Factory.FramePaths[^1]}\"", info.Arguments);
    }

    [Fact]
    public void RevealFolder_NoFramePath_IsDisabled()
    {
        using var harness = Factory.Create(get: _ => Factory.PopulatedDetail(framePaths: [])).Settle();

        Assert.False(harness.ViewModel.RevealFolderCommand.CanExecute(null));
    }

    [Fact]
    public async Task ScanFinished_ReloadsThePage()
    {
        // The one App-layer subscriber to the coordinator is ScanStatusService (ruling Q7), and
        // its ScanFinished cannot be raised from outside it, so this drives a real (empty) scan
        // the way ScanStatusServiceTests does.
        using var settings = new SettingsFixture();
        settings.Save(general => general with { ScanRoots = [settings.Root] });
        var coordinator = ScanCoordinatorTestFactory.Create(settings);
        using var scanStatus = new ScanStatusService(coordinator, action => action());

        using var harness = Factory.Create(scanStatus: scanStatus).Settle();
        Assert.Equal(1, harness.Loads);

        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);
        harness.Settle();

        Assert.Equal(2, harness.Loads);
    }

    /// <summary>
    /// Phase 15B fixer F2, with items 30 and 38. An open page reloads when the host says the
    /// derived data behind it has been rewritten, keeps the selected night and every open card
    /// while it does, and unfollows when it is disposed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A failure looks like a reader mapping a PHD2 profile, or waiting out the correlation re-run
    /// that mapping queues, and finding the Guiding band on the night they are looking at still
    /// attributing its figures to the rig they just unmapped, until they navigate away and back.
    /// </para>
    /// <para>
    /// The selection and the card identity are asserted because a reload is only acceptable here
    /// if it costs the reader no more than a scan-driven one does, which is what
    /// <see cref="ScanFinished_KeepsEveryCard_AndReReadsOnlyTheExpandedOne"/> pins for the scan.
    /// </para>
    /// </remarks>
    [Fact]
    public void ADerivedDataNotification_ReloadsThePage_KeepsTheSelection_AndDisposeUnfollows()
    {
        var derived = new DerivedDataSource();
        var harness = Factory.Create(derivedData: derived).Settle();
        var page = harness.ViewModel;

        var chosen = page.Sessions[1];
        page.SelectedSession = chosen;
        chosen.IsExpanded = true;
        harness.SettleCards();
        Assert.Equal(1, harness.Loads);

        derived.Raise();
        harness.Settle().SettleCards();

        Assert.Equal(2, harness.Loads);

        // The same card object, still expanded, still the selection: a night that survived the
        // reload is carried rather than rebuilt, so an open Guiding band is still open.
        Assert.Same(chosen, page.Sessions[1]);
        Assert.Same(chosen, page.SelectedSession);
        Assert.True(chosen.IsExpanded);
        Assert.False(chosen.IsDisposed);

        harness.Dispose();
        Assert.False(derived.HasFollower);

        derived.Raise();
        Assert.Equal(2, harness.Loads);
    }

    [Fact]
    public async Task ScanFinished_KeepsEveryCard_AndReReadsOnlyTheExpandedOne()
    {
        // Review finding 3. A scan-driven reload used to dispose and rebuild every card, which
        // collapsed a card the user had open and discarded the detail it had already read. The
        // session dates are diffed instead: an unchanged night keeps its card and re-reads only
        // what that card already had.
        using var settings = new SettingsFixture();
        settings.Save(general => general with { ScanRoots = [settings.Root] });
        var coordinator = ScanCoordinatorTestFactory.Create(settings);
        using var scanStatus = new ScanStatusService(coordinator, action => action());

        using var harness = Factory.Create(scanStatus: scanStatus).Settle();
        var expanded = harness.ViewModel.Sessions[0];
        var collapsed = harness.ViewModel.Sessions[1];

        expanded.IsExpanded = true;
        harness.SettleCards();
        Assert.Single(harness.SessionQueries);

        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);
        harness.Settle().SettleCards();

        // The same two card objects, in the same order, with the expansion state intact.
        Assert.Equal(2, harness.ViewModel.Sessions.Count);
        Assert.Same(expanded, harness.ViewModel.Sessions[0]);
        Assert.Same(collapsed, harness.ViewModel.Sessions[1]);
        Assert.True(expanded.IsExpanded);
        Assert.False(expanded.IsDisposed);
        Assert.False(collapsed.IsDisposed);

        // Exactly one more query, the expanded card's. The collapsed card issued nothing, which
        // is the half of the assertion that keeps the refresh cheap on a target with 40 nights.
        Assert.Equal(2, harness.SessionQueries.Count);
        Assert.All(harness.SessionQueries, query => Assert.Equal(Factory.LastSession, query.SessionDate));
        Assert.NotNull(expanded.Detail);
        Assert.Null(collapsed.Detail);
    }

    [Fact]
    public async Task ScanFinished_PublishesTheChartOnce_AndNeverEmpty()
    {
        // Task 8 review finding 5. ReplaceSessions clears and refills the card collection and
        // refreshes each carried card, which drops that card's detail, so an unsuspended chart
        // published a transient empty series set mid-reload. The page brackets the whole diff, so
        // the reload itself is one publish and the empty state is never seen.
        //
        // P12 Task 4 changed the expected count from one to two, and only the count. The page now
        // opens on a night, so a reload re-reads that night's detail, and a card whose Detail
        // arrives is exactly what TargetChartViewModel.OnCardChanged rebuilds for (ruling Q20's
        // filter-split points). The second publish is that documented rebuild, it is non-empty,
        // and the defect this case pins, a transient empty publish inside the diff, is unchanged.
        //
        // P12 Task 5 made the count deterministic instead of tuning it again. With the default
        // inline post seam a card's expansion query publishes on the thread pool thread it ran on,
        // so whether the kept night's Detail arrives inside the page's update bracket or after it
        // is decided by the pool rather than by the code under test: the same build reported one,
        // two and three publishes depending only on how loaded the machine was. The queue below is
        // that post seam, drained explicitly, so a card can never publish in the middle of the
        // page's diff and the number means something again.
        using var settings = new SettingsFixture();
        settings.Save(general => general with { ScanRoots = [settings.Root] });
        var coordinator = ScanCoordinatorTestFactory.Create(settings);
        using var scanStatus = new ScanStatusService(coordinator, action => action());

        Queue<Action> posted = new();
        using var harness = Factory.Create(
            scanStatus: scanStatus,
            post: action =>
            {
                lock (posted)
                {
                    posted.Enqueue(action);
                }
            });

        Drain();

        var chart = harness.ViewModel.TargetChart;
        Assert.False(chart.IsEmpty);

        var publishes = 0;
        var sawEmpty = false;
        chart.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(chart.Series))
            {
                return;
            }

            publishes++;
            sawEmpty |= chart.IsEmpty;
        };

        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);
        Drain();

        Assert.Equal(2, harness.Loads);
        Assert.Equal(2, publishes);
        Assert.False(sawEmpty);
        Assert.False(chart.IsEmpty);

        // The cause of the second publish, named rather than assumed.
        Assert.NotNull(harness.ViewModel.SelectedSession!.Detail);
        return;

        // Joins whatever is in flight, then runs one queued publish, until nothing is left of
        // either. One action at a time, because running a page publish starts the card queries
        // whose own publishes have to be joined before the next one runs.
        void Drain()
        {
            for (var pass = 0; pass < 64; pass++)
            {
                harness.Settle().SettleCards();

                Action? next;
                lock (posted)
                {
                    next = posted.Count > 0 ? posted.Dequeue() : null;
                }

                if (next is null)
                {
                    return;
                }

                next();
            }

            Assert.Fail("the publish queue did not drain");
        }
    }

    [Fact]
    public async Task ScanFinished_AddsNewSessions_AndDisposesVanishedOnes()
    {
        // The other two arms of the same diff: a night the scan ingested gets a new card, and a
        // night it pruned away is disposed rather than leaked.
        var newSession = new DateOnly(2026, 1, 20);
        var loads = 0;

        using var settings = new SettingsFixture();
        settings.Save(general => general with { ScanRoots = [settings.Root] });
        var coordinator = ScanCoordinatorTestFactory.Create(settings);
        using var scanStatus = new ScanStatusService(coordinator, action => action());

        using var harness = Factory.Create(
            get: _ => Factory.PopulatedDetail(
                sessions: ++loads == 1
                    ? [Factory.Session(Factory.LastSession), Factory.Session(Factory.FirstSession)]
                    : [Factory.Session(newSession), Factory.Session(Factory.LastSession)]),
            scanStatus: scanStatus).Settle();

        var kept = harness.ViewModel.Sessions[0];
        var vanishing = harness.ViewModel.Sessions[1];
        Assert.Equal(Factory.FirstSession, vanishing.SessionDate);

        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);
        harness.Settle();

        Assert.Equal(
            [newSession, Factory.LastSession],
            harness.ViewModel.Sessions.Select(card => card.SessionDate));
        Assert.Same(kept, harness.ViewModel.Sessions[1]);
        Assert.False(kept.IsDisposed);
        Assert.True(vanishing.IsDisposed);
    }

    [Fact]
    public async Task ScanFinished_RepublishesTheCollapsedFieldsOfAKeptCard()
    {
        // Keeping the card must not keep its numbers: the newest night is exactly the one a scan
        // adds frames to, and a stale frame count on the card would disagree with the totals row
        // directly above it.
        var loads = 0;

        using var settings = new SettingsFixture();
        settings.Save(general => general with { ScanRoots = [settings.Root] });
        var coordinator = ScanCoordinatorTestFactory.Create(settings);
        using var scanStatus = new ScanStatusService(coordinator, action => action());

        using var harness = Factory.Create(
            get: _ => Factory.PopulatedDetail(
                sessions: [Factory.Session(Factory.LastSession) with { FrameCount = ++loads == 1 ? 74 : 91 }]),
            scanStatus: scanStatus).Settle();

        var card = Assert.Single(harness.ViewModel.Sessions);
        Assert.Equal("74", card.FrameCountText);

        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);
        harness.Settle();

        Assert.Same(card, Assert.Single(harness.ViewModel.Sessions));
        Assert.Equal(91, card.FrameCount);
        Assert.Equal("91", card.FrameCountText);
    }

    [Fact]
    public async Task Dispose_FlushesAPendingNoteAndUnsubscribes()
    {
        using var settings = new SettingsFixture();
        settings.Save(general => general with { ScanRoots = [settings.Root] });
        var coordinator = ScanCoordinatorTestFactory.Create(settings);
        using var scanStatus = new ScanStatusService(coordinator, action => action());

        var harness = Factory.Create(scanStatus: scanStatus).Settle();
        harness.ViewModel.Notes.Text = "typed and immediately navigated away from";

        // The debounce window is never released: the flush must not wait it out.
        harness.ViewModel.Dispose();

        Assert.Equal("typed and immediately navigated away from", Assert.Single(harness.NoteWrites).Notes);

        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        // Unsubscribed: a scan that finishes after the page closed must not reload it.
        Assert.Equal(1, harness.Loads);
    }

    [Fact]
    public void Dispose_DisposesEveryCard_AndClearsSessions()
    {
        // Phase review item 6. DisposeCards disposed the cards and left them bound to the view, so
        // a closed page still exposed session cards whose queries and notes fields were gone.
        var harness = Factory.Create().Settle();
        var cards = harness.ViewModel.Sessions.ToList();
        Assert.NotEmpty(cards);

        harness.ViewModel.Dispose();

        Assert.Empty(harness.ViewModel.Sessions);
        Assert.All(cards, card => Assert.True(card.IsDisposed));
    }

    // ---- Phase 7 Task 5: the merge action, the history region, the merged-away callout -----

    [Fact]
    public async Task Merge_OpensTheDialogForThisTarget()
    {
        using var harness = Factory.Create().Settle();

        await harness.ViewModel.MergeCommand.ExecuteAsync(null);

        // Spec 12.4's merge action: this target is the winner, and the dialog's own search box
        // chooses the loser.
        Assert.Equal(Factory.TargetId, Assert.Single(harness.OpenedMerges));
    }

    [Fact]
    public async Task Merge_ThatMerged_ReloadsThePageAndNotifiesTheDashboard()
    {
        using var harness = Factory.Create(withMergeHistory: true).Settle();
        harness.MergeResult = true;
        harness.History = [Factory.HistoryRow()];
        var renamed = 0;
        harness.ViewModel.TargetRenamed += (_, _) => renamed++;

        var before = harness.Loads;
        await harness.ViewModel.MergeCommand.ExecuteAsync(null);
        harness.Settle().SettleHistory();

        Assert.Equal(before + 1, harness.Loads);

        // FIXER LIST F10's event: a merge changes this target's name set and frame count, which
        // is exactly what the dashboard's listing carries.
        Assert.Equal(1, renamed);

        // And the history below the notes box gained the row the merge just wrote.
        Assert.Single(harness.MergeHistory!.Rows);
    }

    [Fact]
    public async Task Merge_ThatWasCancelled_DoesNotReload()
    {
        using var harness = Factory.Create().Settle();
        harness.MergeResult = false;
        var renamed = 0;
        harness.ViewModel.TargetRenamed += (_, _) => renamed++;

        var before = harness.Loads;
        await harness.ViewModel.MergeCommand.ExecuteAsync(null);

        Assert.Single(harness.OpenedMerges);
        Assert.Equal(before, harness.Loads);
        Assert.Equal(0, renamed);
    }

    [Fact]
    public void Merge_IsDisabledForAnUnresolvedGroup()
    {
        using var harness = Factory.Create(
            get: _ => Factory.PopulatedDetail(header: Factory.UnresolvedHeader()),
            groupKey: Factory.UnresolvedGroupKey,
            withMergeHistory: true).Settle();

        // An obj: group has no targets row to survive a merge, and has absorbed nothing, so it
        // gets neither the action nor the history.
        Assert.False(harness.ViewModel.MergeCommand.CanExecute(null));
        Assert.Null(harness.ViewModel.MergeHistory);
    }

    [Fact]
    public async Task MergeHistory_UndoneReloadsThePage()
    {
        using var harness = Factory.Create(withMergeHistory: true).Settle();
        harness.History = [Factory.HistoryRow()];
        harness.ViewModel.MergeHistory!.Reload();
        harness.SettleHistory();
        var renamed = 0;
        harness.ViewModel.TargetRenamed += (_, _) => renamed++;

        var before = harness.Loads;
        await harness.ViewModel.MergeHistory.UndoCommand.ExecuteAsync(harness.ViewModel.MergeHistory.Rows[0]);
        harness.Settle();

        // An undo moves frames back and restores the loser, so this page and the dashboard row
        // behind it are both stale.
        Assert.Equal(before + 1, harness.Loads);
        Assert.Equal(1, renamed);
    }

    [Fact]
    public void MergeHistory_IsDisposedWithThePage()
    {
        var harness = Factory.Create(withMergeHistory: true).Settle();
        var history = harness.ViewModel.MergeHistory;
        Assert.NotNull(history);

        harness.History = [Factory.HistoryRow()];
        harness.ViewModel.Dispose();

        // A leaked list keeps a query alive past the page, which is FIXER LIST item 9's defect:
        // a disposed list reads nothing, however often it is asked to.
        history!.Reload();
        harness.SettleHistory();
        Assert.Empty(history.Rows);
    }

    [Fact]
    public void MissingTarget_ThatWasMergedAway_NamesTheWinner()
    {
        var winnerId = Guid.Parse("40000000-0000-0000-0000-000000000000");
        using var harness = Factory.Create(get: _ => null, mergedInto: (winnerId, "M 31", "NGC 224")).Settle();
        var page = harness.ViewModel;

        // Ruling Q12: the probe runs only on the null path, and Get still returns null.
        Assert.Equal(Factory.ResolvedGroupKey, Assert.Single(harness.MergedIntoProbes.Distinct()));
        Assert.True(page.IsMissing);
        Assert.True(page.IsMergedAway);
        Assert.Equal("This target was merged into \"M 31\".", page.MergedAwayText);
        Assert.Equal("Open M 31", page.OpenMergedIntoText);

        // The generic stale-key callout gives way to the one that says what actually happened.
        Assert.False(page.ShowStaleKeyCallout);

        // Phase 7 FIXER item 18: the page has no header, so the title line showed nothing at all
        // and the user could not tell which target they had opened.
        Assert.Equal("NGC 224", page.Title);
    }

    [Fact]
    public void MissingTarget_ThatWasMergedAway_OffersToOpenTheWinner()
    {
        var winnerId = Guid.Parse("40000000-0000-0000-0000-000000000000");
        using var harness = Factory.Create(get: _ => null, mergedInto: (winnerId, "M 31", "NGC 224")).Settle();

        var opened = new List<string>();
        harness.ViewModel.OpenTargetRequested += (_, key) => opened.Add(key);

        Assert.True(harness.ViewModel.OpenMergedIntoCommand.CanExecute(null));
        harness.ViewModel.OpenMergedIntoCommand.Execute(null);

        // The winner's group key, which for a resolved target is its id.
        Assert.Equal(winnerId.ToString(), Assert.Single(opened));
    }

    [Fact]
    public void MissingTarget_ForAPrunedGroup_KeepsThePlainStaleKeyCallout()
    {
        using var harness = Factory.Create(get: _ => null).Settle();
        var page = harness.ViewModel;

        // The probe ran and answered "this key was not a merge", which is the other two cases of
        // ruling Q4: a pruned group and a key that never existed.
        Assert.NotEmpty(harness.MergedIntoProbes);
        Assert.True(page.IsMissing);
        Assert.False(page.IsMergedAway);
        Assert.True(page.ShowStaleKeyCallout);
        Assert.Contains(Factory.ResolvedGroupKey, page.MissingText);
        Assert.False(page.OpenMergedIntoCommand.CanExecute(null));
    }

    [Fact]
    public void LoadThatSucceeds_DoesNotProbeForAMerge()
    {
        using var harness = Factory.Create().Settle();

        // The companion is called only after Get returned null (ruling Q12): a page that loaded
        // takes no second round trip.
        Assert.Empty(harness.MergedIntoProbes);
        Assert.False(harness.ViewModel.IsMergedAway);
    }

    [Fact]
    public async Task MergedAwayCallout_Clears_WhenALaterLoadSucceeds()
    {
        TargetDetail? next = null;
        using var harness = Factory.Create(
            get: _ => next,
            withMergeHistory: true,
            mergedInto: (Guid.NewGuid(), "M 31", "NGC 224")).Settle();
        Assert.True(harness.ViewModel.IsMergedAway);

        // An undo brings the target back, and the page reloads on the history's Undone event; the
        // callout must not survive it.
        harness.History = [Factory.HistoryRow()];
        harness.ViewModel.MergeHistory!.Reload();
        harness.SettleHistory();

        next = Factory.PopulatedDetail();
        harness.MergedInto = null;
        await harness.ViewModel.MergeHistory.UndoCommand.ExecuteAsync(harness.ViewModel.MergeHistory.Rows[0]);
        harness.Settle();

        Assert.False(harness.ViewModel.IsMergedAway);
        Assert.False(harness.ViewModel.IsMissing);
        Assert.False(harness.ViewModel.ShowStaleKeyCallout);
    }

    // ---- the reference thumbnail slot (spec 12.4, Phase 8 Task 6) ------------------

    private const string FrameFixture = @"C:\Astro\M 31\frame.fits";

    // A slot over a worker whose render parks until the worker is disposed, so a load that is
    // started stays in flight: IsLoading then only goes false because something disposed the slot.
    private sealed class SlotHarness : IDisposable
    {
        // Parks every render until this harness is disposed, so a load that is started stays in
        // flight and nothing but disposal can clear the spinner.
        private readonly ManualResetEventSlim _parked = new();

        public SlotHarness()
            => Worker = new ThumbnailWorker(Park, Park, post: action => action());

        public ThumbnailWorker Worker { get; }

        public ThumbnailSlotViewModel? Slot { get; private set; }

        public ThumbnailSlotViewModel Create(string cacheRelativePath)
        {
            _ = cacheRelativePath;
            Slot = new ThumbnailSlotViewModel(
                FrameFixture, Worker, _ => null, post: action => action());
            return Slot;
        }

        public void Dispose()
        {
            _parked.Set();
            Worker.Dispose();
            _parked.Dispose();
        }

        private string? Park(string framePath, CancellationToken ct)
        {
            _ = framePath;
            _ = ct;
            _parked.Wait();
            return null;
        }
    }

    [Fact]
    public void Header_BuildsAReferenceThumbnailSlotWhenThePathIsSet()
    {
        using var slots = new SlotHarness();
        using var harness = Factory.Create(
            get: _ => Factory.PopulatedDetail(
                header: Factory.PopulatedHeader(referenceThumbnailPath: "reference/abc.jpg")),
            referenceSlot: slots.Create).Settle();

        Assert.True(harness.ViewModel.Header!.HasReferenceThumbnail);
        Assert.Same(slots.Slot, harness.ViewModel.Header.ReferenceThumbnail);

        // The STORED cache-relative path, not a frame path: the pass has already produced the file.
        Assert.Equal(["reference/abc.jpg"], harness.ReferenceThumbnailPaths);
    }

    [Fact]
    public void Header_BuildsNoSlotWhenThePathIsNull()
    {
        using var slots = new SlotHarness();
        using var harness = Factory.Create(referenceSlot: slots.Create).Settle();

        Assert.False(harness.ViewModel.Header!.HasReferenceThumbnail);
        Assert.Null(harness.ViewModel.Header.ReferenceThumbnail);
        Assert.Empty(harness.ReferenceThumbnailPaths);
    }

    [Fact]
    public void Dispose_DisposesTheReferenceThumbnailSlot()
    {
        using var slots = new SlotHarness();
        var harness = Factory.Create(
            get: _ => Factory.PopulatedDetail(
                header: Factory.PopulatedHeader(referenceThumbnailPath: "reference/abc.jpg")),
            referenceSlot: slots.Create).Settle();

        // A render that never returns, so nothing but disposal can clear the spinner. The slot
        // holds a decoded bitmap in the product, which is unmanaged memory.
        harness.ViewModel.Header!.ReferenceThumbnail!.Load();
        Assert.True(slots.Slot!.IsLoading);

        harness.ViewModel.Dispose();

        Assert.False(slots.Slot.IsLoading);
        slots.Slot.Load();
        Assert.False(slots.Slot.IsLoading);
    }

    private static Factory.Harness Unresolved() => Factory.Create(
        get: _ => Factory.PopulatedDetail(header: Factory.UnresolvedHeader()),
        groupKey: Factory.UnresolvedGroupKey);

    // ---- P12 Task 4: the selected night, the drawer and the responsive rule ------------------

    // Awaits the page's load and then every card's expansion query, so a test asserts against a
    // settled page without blocking. Nothing here asserts which thread anything ran on, but the
    // awaiting form is the roadmap's rule and costs nothing.
    private static async Task SettleAsync(Factory.Harness harness)
    {
        if (harness.ViewModel.PendingLoad is { } load)
        {
            await load;
        }

        foreach (var card in harness.ViewModel.Sessions.ToList())
        {
            if (card.PendingLoad is { } cardLoad)
            {
                await cardLoad;
            }
        }
    }

    [Fact]
    public async Task Load_SelectsTheNewestSession_AndIssuesItsDetailQueryOnce()
    {
        // The comp opens on the newest night, so the "was last night good" answer is on screen
        // with no click. Spec 12.4's "the session detail query is issued when a card expands, not
        // up front" survives that: exactly one night's detail is read, and it is the open one.
        using var harness = Factory.Create();
        await SettleAsync(harness);
        var page = harness.ViewModel;

        Assert.Same(page.Sessions[0], page.SelectedSession);
        Assert.Equal(Factory.LastSession, page.SelectedSession!.SessionDate);
        Assert.True(page.SelectedSession.IsExpanded);
        Assert.NotNull(page.SelectedSession.Detail);

        var query = Assert.Single(harness.SessionQueries);
        Assert.Equal(Factory.LastSession, query.SessionDate);

        Assert.False(page.Sessions[1].IsExpanded);
        Assert.Null(page.Sessions[1].Detail);
    }

    [Fact]
    public async Task SelectingAnotherSession_LoadsIt_AndLeavesTheFirstLoaded()
    {
        using var harness = Factory.Create();
        await SettleAsync(harness);
        var page = harness.ViewModel;
        var newest = page.Sessions[0];

        page.SelectedSession = page.Sessions[1];
        await SettleAsync(harness);

        Assert.True(page.Sessions[1].IsExpanded);
        Assert.NotNull(page.Sessions[1].Detail);

        // The outgoing night collapses but keeps its detail, so arrowing back is free.
        Assert.False(newest.IsExpanded);
        Assert.NotNull(newest.Detail);

        Assert.Equal(2, harness.SessionQueries.Count);
        Assert.Equal(
            [Factory.LastSession, Factory.FirstSession],
            harness.SessionQueries.Select(query => query.SessionDate));
    }

    [Fact]
    public async Task ReSelectingTheFirstSession_IssuesNoSecondQuery()
    {
        using var harness = Factory.Create();
        await SettleAsync(harness);
        var page = harness.ViewModel;

        page.SelectedSession = page.Sessions[1];
        await SettleAsync(harness);
        page.SelectedSession = page.Sessions[0];
        await SettleAsync(harness);

        // Two nights opened, two queries. Re-opening a night that already has its detail costs
        // nothing at all.
        Assert.Equal(2, harness.SessionQueries.Count);
        Assert.Same(page.Sessions[0], page.SelectedSession);
        Assert.True(page.Sessions[0].IsExpanded);
    }

    [Fact]
    public async Task AScanReload_KeepsTheSelectedNight_WhenItSurvives()
    {
        using var settings = new SettingsFixture();
        settings.Save(general => general with { ScanRoots = [settings.Root] });
        var coordinator = ScanCoordinatorTestFactory.Create(settings);
        using var scanStatus = new ScanStatusService(coordinator, action => action());

        using var harness = Factory.Create(scanStatus: scanStatus);
        await SettleAsync(harness);
        var page = harness.ViewModel;

        page.SelectedSession = page.Sessions[1];
        await SettleAsync(harness);
        Assert.Equal(Factory.FirstSession, page.SelectedSession!.SessionDate);

        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);
        await SettleAsync(harness);

        // The night the user was on is still the open one, and it is still the same card object.
        Assert.Equal(Factory.FirstSession, page.SelectedSession!.SessionDate);
        Assert.Same(page.Sessions[1], page.SelectedSession);
        Assert.True(page.SelectedSession.IsExpanded);
        Assert.False(page.Sessions[0].IsExpanded);
    }

    [Fact]
    public async Task AScanReload_FallsBackToTheNewest_WhenTheSelectedNightIsGone()
    {
        var newSession = new DateOnly(2026, 1, 20);
        var loads = 0;

        using var settings = new SettingsFixture();
        settings.Save(general => general with { ScanRoots = [settings.Root] });
        var coordinator = ScanCoordinatorTestFactory.Create(settings);
        using var scanStatus = new ScanStatusService(coordinator, action => action());

        using var harness = Factory.Create(
            get: _ => Factory.PopulatedDetail(
                sessions: ++loads == 1
                    ? [Factory.Session(Factory.LastSession), Factory.Session(Factory.FirstSession)]
                    : [Factory.Session(newSession)]),
            scanStatus: scanStatus);
        await SettleAsync(harness);
        var page = harness.ViewModel;

        page.SelectedSession = page.Sessions[1];
        await SettleAsync(harness);
        Assert.Equal(Factory.FirstSession, page.SelectedSession!.SessionDate);

        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);
        await SettleAsync(harness);

        // The night the scan pruned away took its card with it, so the newest night is open.
        Assert.Same(Assert.Single(page.Sessions), page.SelectedSession);
        Assert.Equal(newSession, page.SelectedSession!.SessionDate);
        Assert.True(page.SelectedSession.IsExpanded);
    }

    [Fact]
    public async Task ATargetWithNoSessions_SelectsNothing_AndDoesNotThrow()
    {
        using var harness = Factory.Create(get: _ => Factory.PopulatedDetail(sessions: []));
        await SettleAsync(harness);

        Assert.Empty(harness.ViewModel.Sessions);
        Assert.Null(harness.ViewModel.SelectedSession);
        Assert.Empty(harness.SessionQueries);
    }

    [Fact]
    public async Task Load_ComparesEveryNightAgainstTheTargetRow()
    {
        // R4's worse ink is wired at the page, because only the page holds both the night and the
        // row above it. The shared fixture's nights detect 1,490 stars against the target's mean
        // of 1,500, which is the one metric that reads worse; the rest are inside their last
        // decimal or better.
        using var harness = Factory.Create();
        await SettleAsync(harness);

        Assert.All(harness.ViewModel.Sessions, card =>
        {
            Assert.True(card.IsWorseStars);
            Assert.False(card.IsWorseHfr);
            Assert.False(card.IsWorseEccentricity);
            Assert.False(card.IsWorseFwhm);
            Assert.False(card.IsWorseGuidingRms);
        });
    }

    [Fact]
    public async Task Totals_CarryTheLogLineAndTheLedgerRow()
    {
        using var harness = Factory.Create();
        await SettleAsync(harness);

        var totals = harness.ViewModel.Totals!;

        // The log line is runs rather than one string, so the figures can take the figure class
        // while the connecting words stay secondary. LogLineText, which was those runs joined and
        // bound by no view, went with the retired card (phase review P2-5), so the sentence is
        // asserted here as the runs the view actually renders.
        Assert.Equal(
            "12.4 h over 2 nights, 148 light frames, 2024-01-05 to 2025-12-07.",
            string.Join(" ", totals.LogLineRuns.Select(run => run.Text)));
        Assert.Equal(
            ["12.4 h", "2", "148", "2024-01-05", "2025-12-07."],
            totals.LogLineRuns.Where(run => run.IsFigure).Select(run => run.Text));
        Assert.Equal("on ASI2600MM, RC8.", totals.LogLineEquipmentText);

        // Spec 12.4's two disclosures are both still on the page, joined into one sentence.
        Assert.True(totals.HasLogLineDisclosure);
        Assert.Contains("23 frames without a plate scale", totals.LogLineDisclosureText);
        Assert.Contains("measured by header", totals.LogLineDisclosureText);

        // Ruling Q10's first cell, and the unit-free ledger cells beside it.
        Assert.Equal("All 2 nights", totals.LedgerRowLabelText);
        Assert.Equal("12.4", totals.LedgerIntegrationText);
        Assert.Equal("2.34", totals.LedgerAvgHfrText);
        Assert.Equal("1,500", totals.LedgerAvgDetectedStarsText);

        Assert.Equal(["Ha", "OIII"], totals.FilterSwatches.Select(swatch => swatch.FilterName));

        // The brushes carry the one resolution path's ink, opaque. Assert.NotNull on a
        // non-nullable positional record parameter could never fail (Task 4 review P3-4), so what
        // is asserted is where the colour comes from: ChartSelectionViewModel.FilterTint, which is
        // this page's single filter-ink resolver, and not a brush built at the swatch.
        Assert.All(
            totals.FilterSwatches,
            swatch => Assert.Equal(byte.MaxValue, swatch.Brush.Color.A));
        Assert.All(
            totals.FilterSwatches,
            swatch => Assert.Equal(
                harness.Selection.FilterTint(swatch.FilterName).Color,
                swatch.Brush.Color));
    }

    [Fact]
    public void Totals_WithNoDisclosure_HideTheLine()
    {
        var totals = new TargetTotalsViewModel(
            Factory.PopulatedTotals(hfrArcsecExcluded: 0, eccentricityExcluded: 0));

        Assert.False(totals.HasLogLineDisclosure);
        Assert.Equal("", totals.LogLineDisclosureText);
    }

    [Fact]
    public void ToggleDetails_FlipsIsDetailsOpen()
    {
        using var harness = Factory.Create().Settle();
        var page = harness.ViewModel;

        // Closed on open: the catalogue text, the notes and the merge history are the page's
        // least frequent content.
        Assert.False(page.IsDetailsOpen);

        page.ToggleDetailsCommand.Execute(null);
        Assert.True(page.IsDetailsOpen);

        page.ToggleDetailsCommand.Execute(null);
        Assert.False(page.IsDetailsOpen);
    }

    [Fact]
    public void ApplyWidth_AtSixteenHundred_IsWide_AndInline()
    {
        using var harness = Factory.Create().Settle();
        var page = harness.ViewModel;

        page.ApplyWidth(1600d);

        Assert.True(page.IsWide);
        Assert.Equal(SplitViewDisplayMode.Inline, page.DetailsDisplayMode);
    }

    [Fact]
    public void ApplyWidth_BelowSixteenHundred_IsNarrow_AndOverlay()
    {
        using var harness = Factory.Create().Settle();
        var page = harness.ViewModel;

        page.ApplyWidth(1599d);

        Assert.False(page.IsWide);
        Assert.Equal(SplitViewDisplayMode.Overlay, page.DetailsDisplayMode);
    }

    [Fact]
    public async Task ScaleBar_TakesTheLargestStepUnderAThirdOfTheField()
    {
        // Ruling Q6: the bar measures the reference frame's field, not the object. A 4144 px
        // frame at 1 arcsec per pixel is a 69.1 arcminute field, a third of which is 23.0, so the
        // largest step on the ladder that fits is 15.
        using var harness = Factory.Create(get: _ => Factory.PopulatedDetail(
            header: Factory.PopulatedHeader(
                referenceArcsecPerPixel: 1.0d,
                referenceFrameWidthPixels: 4144)));
        await SettleAsync(harness);
        var header = harness.ViewModel.Header!;

        Assert.True(header.HasScaleBar);
        Assert.Equal("15'", header.ScaleBarText);
        Assert.Equal(15d / (4144d / 60d) * 128d, header.ScaleBarWidth, 6);
    }

    [Fact]
    public async Task ScaleBar_WithoutAPlateScale_IsAbsent()
    {
        using var harness = Factory.Create();
        await SettleAsync(harness);
        var header = harness.ViewModel.Header!;

        Assert.False(header.HasScaleBar);
        Assert.Equal(0d, header.ScaleBarWidth);
        Assert.Equal("", header.ScaleBarText);
    }

    [Fact]
    public void ScaleBar_ForAFieldUnderThreeArcminutes_IsAbsent()
    {
        // No step on the ladder is at most a third of the field, and inventing one below the
        // smallest would draw a bar a few pixels long.
        var (width, text) = TargetHeaderViewModel.ScaleBar(0.5d, 300);

        Assert.Equal(0d, width);
        Assert.Equal("", text);
    }

    // ---- Polish 2 ruling 2: a timezone or clock change on the Location tab reaches an open page

    [Fact]
    public void GeneralChanged_NewTimezone_RebuildsTheCardsInTheNewZone()
    {
        // A page loaded in UTC with coordinates, so the selected night has a first frame time and
        // an astronomical night band to move. Failure looks like the card still reading the
        // 24 hour UTC text and the band's dusk staying five hours later than Eastern's.
        var utc = new GeneralSettings
        {
            Timezone = "UTC",
            Use24HTime = true,
            ObserverLatitude = 40,
            ObserverLongitude = -75,
        };
        using var harness = Factory.Create(general: utc).Settle().SettleCards();
        var before = harness.ViewModel.SelectedSession!;
        var beforeText = before.FirstFrameTimeText;
        var beforeDusk = DuskOf(before.NightStrip!);
        Assert.NotEqual("", beforeText);

        harness.General = utc with { Timezone = "Eastern Standard Time", Use24HTime = false };
        harness.RaiseGeneralChanged();
        harness.Settle().SettleCards();

        var after = harness.ViewModel.SelectedSession!;
        Assert.True(after.Detail is not null, $"The rebuilt card loaded no detail. LastFailure: {after.LastFailure}");
        var eastern = SessionTimeFormat.Resolve("Eastern Standard Time");
        Assert.Equal(
            SessionTimeFormat.Format(before.Detail!.FirstFrameTime, eastern, false),
            after.FirstFrameTimeText);
        Assert.NotEqual(beforeText, after.FirstFrameTimeText);
        Assert.Equal(before.SessionDate, after.SessionDate);
        Assert.True(after.NightStrip!.HasBand);
        Assert.Equal(beforeDusk.AddHours(-5), DuskOf(after.NightStrip), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void GeneralChanged_SameTimezone_DoesNotReload()
    {
        // The storm rule: a general save that leaves the zone, the clock and the coordinates
        // alone costs no load and keeps the card instances. A failure is Loads counting up or a
        // new card instance selected.
        using var harness = Factory.Create().Settle().SettleCards();
        var before = harness.Loads;
        var card = harness.ViewModel.SelectedSession;

        harness.General = harness.General with { DefaultPageSize = 25 };
        harness.RaiseGeneralChanged();
        harness.Settle();

        Assert.Equal(before, harness.Loads);
        Assert.Same(card, harness.ViewModel.SelectedSession);
    }

    [Fact]
    public void GeneralChanged_NewTimezone_KeepsTheCheckedNight()
    {
        // The rebuilt card of a checked night is checked too. A failure is the rebuilt card
        // unchecked and SelectedNights empty after the zone change.
        using var harness = Factory.Create().Settle();
        var date = harness.ViewModel.Sessions[0].SessionDate;
        harness.ViewModel.Sessions[0].IsChecked = true;

        harness.General = harness.General with { Timezone = "Eastern Standard Time" };
        harness.RaiseGeneralChanged();
        harness.Settle();

        Assert.True(harness.ViewModel.Sessions.Single(card => card.SessionDate == date).IsChecked);
        Assert.Equal([date], harness.ViewModel.SelectedNights);
    }

    [Fact]
    public void GeneralChanged_ObserverZoneWhileTheDisplayFollowsIt_Reloads()
    {
        // Polish wave 3 ruling 1: an empty display timezone follows the observer's, so moving the
        // observer zone moves the display zone too. A failure is Loads staying put because the
        // compare tuple read the stored "" on both sides.
        var following = new GeneralSettings { Timezone = "", ObserverTimezone = "UTC", Use24HTime = true };
        using var harness = Factory.Create(general: following).Settle().SettleCards();
        var before = harness.Loads;

        harness.General = following with { ObserverTimezone = "Eastern Standard Time" };
        harness.RaiseGeneralChanged();
        harness.Settle();

        Assert.Equal(before + 1, harness.Loads);
    }
}
