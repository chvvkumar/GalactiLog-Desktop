using GalactiLog.App.Services;
using GalactiLog.App.Tests.Services;
using GalactiLog.App.ViewModels.Merge;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;

namespace GalactiLog.App.Tests.TestSupport;

// Phase 7 Task 3. The one place App.Tests builds a TargetsTabViewModel, so Tasks 4, 5 and 6
// change one file rather than twenty. Same shape as TargetDetailViewModelTestFactory: no window,
// no dispatcher and no database, every collaborator a lambda, the debounce a FakeDelay, and the
// post seam running its closure inline.
internal static class TargetsTabViewModelTestFactory
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    public static readonly Guid WinnerId = Guid.Parse("20000000-0000-0000-0000-000000000000");

    public static readonly DateTime Created = new(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);

    /// <summary>A trigram candidate with every spec 12.9 field populated.</summary>
    public static MergeCandidateRow Candidate(
        string sourceName = "NGC7331 field",
        Guid? id = null,
        int frameCount = 12,
        Guid? suggestedTargetId = null,
        string? suggestedTargetName = "NGC 7331",
        double score = 0.875d,
        string method = "trigram",
        string? reasonText = "Name is 87% similar to \"NGC 7331\"",
        DateTime? createdAt = null) => new(
        id ?? Guid.NewGuid(),
        sourceName,
        frameCount,
        suggestedTargetId ?? WinnerId,
        suggestedTargetName,
        score,
        method,
        reasonText,
        createdAt ?? Created);

    /// <summary>An orphan candidate: no suggestion at all (spec 5.10).</summary>
    public static MergeCandidateRow Orphan(string sourceName = "Zzyzx Blob 42") => new(
        Guid.NewGuid(),
        sourceName,
        SourceImageCount: 1,
        SuggestedTargetId: null,
        SuggestedTargetName: null,
        SimilarityScore: 0d,
        Method: "orphan",
        ReasonText: "No match found in SIMBAD or existing targets",
        CreatedAt: Created);

    /// <summary>A resolved search result, which the edit-target dropdown can choose.</summary>
    public static TargetSearchResult TargetResult(string displayName = "M 31", Guid? targetId = null)
        => new(targetId ?? WinnerId, null, displayName, "G", displayName, 0, 0.9d);

    /// <summary>An unresolved OBJECT search result, which it cannot: there is no target id to
    /// point the candidate at.</summary>
    public static TargetSearchResult UnresolvedResult(string displayName = "Zzyzx Blob 42")
        => new(null, displayName, displayName, null, displayName, 4, 0.8d);

    internal sealed class Harness : IDisposable
    {
        public FakeDelay Delay { get; } = new();

        public RecordingLogger Logger { get; } = new();

        /// <summary>What the pending-candidate delegate returns. Assigning a new list and
        /// reloading is how a test observes a refreshed list.</summary>
        public IReadOnlyList<MergeCandidateRow> Pending { get; set; } = [];

        /// <summary>Thrown by the pending-candidate delegate while set, so a test can drive the
        /// failed-load state. Clear it and reload to recover.</summary>
        public Exception? LoadThrows { get; set; }

        /// <summary>How many times the candidate query ran. One per load, so a scan-driven
        /// refresh is observable.</summary>
        public int Loads;

        /// <summary>The thread every load ran on, so a test can prove the SQLite read left the
        /// UI thread. Empty unless <see cref="Create"/> was given an <c>onUiThread</c> reader.
        /// </summary>
        public List<bool> LoadOnUiThread { get; } = [];

        public List<Guid> Dismissed { get; } = [];

        public bool DismissResult { get; set; } = true;

        /// <summary>Every candidate the merge dialog seam was opened for (Task 4's seam).</summary>
        public List<MergeCandidateRow> OpenedMerges { get; } = [];

        /// <summary>What that seam completes with. False is Task 3's production registration.
        /// </summary>
        /// <summary>What the merge dialog returns: spec 12.9's count sentence when a merge
        /// happened, null when none did (Phase 7 FIXER item 17).</summary>
        public string? MergeResult { get; set; }

        public List<string> Searches { get; } = [];

        public IReadOnlyList<TargetSearchResult> SearchResults { get; set; } = [];

        /// <summary>The thread every search ran on, so a test can prove it left the UI thread.
        /// </summary>
        public List<bool> SearchOnUiThread { get; } = [];

        public List<(Guid CandidateId, Guid TargetId)> Retargets { get; } = [];

        public bool RetargetResult { get; set; } = true;

        public ScanStatusService? ScanStatus { get; init; }

        /// <summary>Task 5's merge history seam, when <see cref="Create"/> was asked for one.
        /// Null otherwise, which leaves that region empty.</summary>
        public MergeHistoryViewModel? MergeHistory { get; internal set; }

        /// <summary>Task 6's unresolved-name region, when <see cref="Create"/> was asked for one.
        /// Disposed with the harness, because the factory built it.</summary>
        public UnresolvedNamesViewModel? UnresolvedNames { get; internal set; }

        /// <summary>Task 6's rename history region, on the same terms.</summary>
        public RenameHistoryViewModel? RenameHistory { get; internal set; }

        /// <summary>Phase 14B Task 3's create target form, when <see cref="Create"/> was asked for
        /// one. Null leaves the section's form unavailable.</summary>
        public CreateTargetViewModel? CreateTarget { get; internal set; }

        /// <summary>Assigned by <see cref="Create"/> immediately after construction. The tab's own
        /// load runs on a background thread and touches only the recording members above, so
        /// nothing reads this before it is set.</summary>
        public TargetsTabViewModel ViewModel { get; internal set; } = null!;

        /// <summary>Joins the in-flight load, so a test asserts against a settled tab instead of
        /// sleeping. The blocking wait lives here rather than in a test method, which is what
        /// xunit's own analyzer asks for.</summary>
        public Harness Settle()
        {
            ViewModel.PendingLoad?.Wait(Budget);
            return this;
        }

        public void Dispose()
        {
            ViewModel.Dispose();

            // The tab does not own these two (they are container-owned singletons in production),
            // so the factory that built them disposes them.
            UnresolvedNames?.Dispose();
            RenameHistory?.Dispose();
        }
    }

    /// <param name="pending">The initial candidate list.</param>
    /// <param name="scanStatus">Passed straight through; null leaves the refresh unsubscribed.
    /// </param>
    /// <param name="onUiThread">Reads whether the calling thread is the UI thread, so the
    /// off-thread assertions do not need a dispatcher reference inside this factory. Null records
    /// nothing.</param>
    /// <param name="loadRelease">Parks the pending-candidate delegate until a test releases it,
    /// so a test can observe the tab while its <em>first</em> load is still in flight. Set before
    /// the view-model is constructed, which is the only way to catch that load.</param>
    /// <param name="post">How the tab reaches the UI thread. Defaults to running the closure
    /// inline, which is what every test that is not about marshalling wants.</param>
    /// <param name="mergeHistory">Task 5's merge history region. Null leaves it empty, which is
    /// what every test that is not about the history wants.</param>
    /// <param name="unresolvedNames">Task 6's unresolved-name region. Null leaves it empty.</param>
    /// <param name="renameHistory">Task 6's rename history region. Null leaves it empty.</param>
    /// <param name="createTarget">Phase 14B Task 3's create target form. Null leaves the section's
    /// form unavailable, which is what every test that is not about creation wants.</param>
    public static Harness Create(
        IReadOnlyList<MergeCandidateRow>? pending = null,
        ScanStatusService? scanStatus = null,
        Func<bool>? onUiThread = null,
        ManualResetEventSlim? loadRelease = null,
        Action<Action>? post = null,
        MergeHistoryViewModel? mergeHistory = null,
        UnresolvedNamesViewModel? unresolvedNames = null,
        RenameHistoryViewModel? renameHistory = null,
        CreateTargetViewModel? createTarget = null)
    {
        var harness = new Harness
        {
            ScanStatus = scanStatus,
            Pending = pending ?? [],
            MergeHistory = mergeHistory,
            UnresolvedNames = unresolvedNames,
            RenameHistory = renameHistory,
            CreateTarget = createTarget,
        };

        harness.ViewModel = new TargetsTabViewModel(
            () =>
            {
                Interlocked.Increment(ref harness.Loads);
                lock (harness.LoadOnUiThread)
                {
                    if (onUiThread is not null)
                    {
                        harness.LoadOnUiThread.Add(onUiThread());
                    }
                }

                loadRelease?.Wait(Budget);
                return harness.LoadThrows is null ? harness.Pending : throw harness.LoadThrows;
            },
            candidateId =>
            {
                lock (harness.Dismissed)
                {
                    harness.Dismissed.Add(candidateId);
                }

                return harness.DismissResult;
            },
            // In production this is AppHost's lambda over MergeDialogService.ShowAsync (Task 4),
            // which now returns the confirm summary rather than a bool (Phase 7 FIXER item 17).
            row =>
            {
                lock (harness.OpenedMerges)
                {
                    harness.OpenedMerges.Add(row);
                }

                return Task.FromResult(harness.MergeResult);
            },
            term =>
            {
                lock (harness.Searches)
                {
                    harness.Searches.Add(term);
                    if (onUiThread is not null)
                    {
                        harness.SearchOnUiThread.Add(onUiThread());
                    }
                }

                return harness.SearchResults;
            },
            (candidateId, targetId) =>
            {
                lock (harness.Retargets)
                {
                    harness.Retargets.Add((candidateId, targetId));
                }

                return harness.RetargetResult;
            },
            mergeHistory,
            unresolvedNames,
            renameHistory,
            scanStatus: scanStatus,
            delay: harness.Delay.Delay,
            post: post ?? (action => action()),
            logger: harness.Logger,
            createTarget: createTarget);

        return harness;
    }

    /// <summary>
    /// Spec 12.7's Settings page over a tab built here, for the shell tests that only need the
    /// rail's settings destination to be the real page. The page is not disposed by the shell
    /// (it is a DI singleton in production), so nothing here needs the harness back.
    /// </summary>
    public static SettingsViewModel CreateSettingsPage()
        => new(Create().Settle().ViewModel);

    /// <summary>
    /// Phase 14B Task 3's create target form over a delegate that always succeeds, for the view
    /// tests and for the tab tests that only need the section to render. A create that must be
    /// observed passes its own delegate.
    /// </summary>
    public static CreateTargetViewModel CreateForm(
        Func<CreateTargetRequest, CreateTargetResult>? create = null,
        Action? afterCreate = null)
        => new(
            create ?? (_ => new CreateTargetResult(CreateTargetOutcome.Created, WinnerId, null, 0, 0)),
            afterCreate,
            post: action => action());

    /// <summary>Task 6's unresolved-name region over a fixed list, settled, for the view tests and
    /// for the tab tests that only need the region to render.</summary>
    public static UnresolvedNamesViewModel UnresolvedNames(params UnresolvedNameRow[] rows)
        => UnresolvedNames(onLoad: null, rows);

    /// <summary>The same, counting its loads, for the tests that assert the tab reloads this
    /// region after an accepted merge or an undo.</summary>
    public static UnresolvedNamesViewModel UnresolvedNames(Action? onLoad, params UnresolvedNameRow[] rows)
    {
        var list = new UnresolvedNamesViewModel(
            () =>
            {
                onLoad?.Invoke();
                return rows;
            },
            (_, _) => new UnresolvedRetry.RetryOutcome(0, 0, 0, 0, 0, false),
            post: action => action(),
            // Phase 14B Task 3's create target row action. Bound to a no-op, so the button renders
            // enabled the way it does in production.
            createTarget: _ => { });
        list.PendingLoad?.Wait(Budget);
        return list;
    }

    /// <summary>Task 6's rename history region over a fixed list, settled.</summary>
    public static RenameHistoryViewModel RenameHistory(params RenameHistoryRow[] rows)
    {
        var list = new RenameHistoryViewModel(
            () => rows,
            new GeneralSettings { Timezone = "UTC", Use24HTime = true },
            post: action => action());
        list.PendingLoad?.Wait(Budget);
        return list;
    }

    /// <summary>One unresolved name with its frame count, for the two builders above.</summary>
    public static UnresolvedNameRow UnresolvedName(string name = "Zzyzx Blob 42", int frameCount = 12)
        => new(name, "obj:" + name, frameCount);

    /// <summary>One rename, as RenameHistoryQuery returns it.</summary>
    public static RenameHistoryRow Rename(
        string previousName = "M 31", string newName = "Andromeda", string? currentName = "Andromeda")
        => new(Created, WinnerId, previousName, newName, currentName);
}
