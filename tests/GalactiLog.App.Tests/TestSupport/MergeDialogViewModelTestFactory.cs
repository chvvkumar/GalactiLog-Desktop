using System.Text.Json;
using GalactiLog.App.Tests.Services;
using GalactiLog.App.ViewModels.Merge;
using GalactiLog.Data;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.App.Tests.TestSupport;

// Phase 7 Task 4. The one place App.Tests builds a MergeDialogViewModel, so a constructor change
// is one edit rather than twenty. Same shape as TargetDetailViewModelTestFactory and
// TargetsTabViewModelTestFactory: no window, no dispatcher and no database, every collaborator a
// lambda, the debounce a FakeDelay, and the post seam running its closure inline.
//
// CreateOverDatabase is the exception, and it exists for exactly one assertion: the roadmap's
// "cancel writes nothing" needs a real MergeRepository behind the confirm delegate to prove that
// no manifest row, no moved frame and no candidate status change happened.
internal static class MergeDialogViewModelTestFactory
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    public static readonly Guid WinnerId = Guid.Parse("30000000-0000-0000-0000-000000000000");

    public static readonly Guid LoserId = Guid.Parse("40000000-0000-0000-0000-000000000000");

    /// <summary>One side of spec 12.9's comparison, every field populated.</summary>
    public static MergePreviewSide Side(
        Guid? targetId = null,
        string primaryName = "NGC 7331",
        string? catalogId = "NGC7331",
        IReadOnlyList<string>? aliases = null,
        string? objectType = "GiG,G",
        string objectCategory = "Galaxy",
        double? ra = 339.2670d,
        double? dec = 34.4158d,
        int frameCount = 148,
        double integrationSeconds = 44_400d,
        int sessionCount = 9,
        DateOnly? firstSession = null,
        DateOnly? lastSession = null) => new(
        targetId ?? WinnerId,
        primaryName,
        catalogId,
        aliases ?? ["Caldwell 30"],
        objectType,
        objectCategory,
        ra,
        dec,
        frameCount,
        integrationSeconds,
        sessionCount,
        firstSession ?? new DateOnly(2024, 8, 3),
        lastSession ?? new DateOnly(2025, 1, 17));

    /// <summary>An unresolved <c>OBJECT</c> side: no target row, so every catalog field is
    /// null.</summary>
    public static MergePreviewSide UnresolvedSide(string name = "ngc7331 mosaic", int frameCount = 12)
        => new(
            TargetId: null,
            name,
            CatalogId: null,
            Aliases: [],
            ObjectType: null,
            TargetListingCriteria.UnresolvedCategory,
            Ra: null,
            Dec: null,
            frameCount,
            frameCount * 300d,
            SessionCount: 2,
            new DateOnly(2024, 11, 2),
            new DateOnly(2024, 12, 6));

    public static MergePreview Preview(
        MergePreviewSide? winner = null,
        MergePreviewSide? loser = null,
        int? framesToMove = null,
        IReadOnlyList<DateOnly>? collidingDates = null,
        IReadOnlyList<string>? aliasesToAdd = null)
    {
        var loserSide = loser ?? Side(LoserId, "Deer Lick", "PGC69327", ["Deer Lick Group"], frameCount: 24);
        return new MergePreview(
            winner ?? Side(),
            loserSide,
            framesToMove ?? loserSide.FrameCount,
            collidingDates ?? [],
            aliasesToAdd ?? ["Deer Lick", "Deer Lick Group"]);
    }

    /// <summary>A resolved search result, which either side may be replaced with.</summary>
    public static TargetSearchResult TargetResult(string displayName = "M 31", Guid? targetId = null)
        => new(targetId ?? Guid.Parse("50000000-0000-0000-0000-000000000000"), null, displayName, "G", displayName, 0, 0.9d);

    /// <summary>An unresolved <c>OBJECT</c> search result, which may be chosen only as the
    /// loser.</summary>
    public static TargetSearchResult UnresolvedResult(string displayName = "ngc7331 mosaic")
        => new(null, displayName, displayName, null, displayName, 12, 0.8d);

    /// <summary>The counts spec 12.9's confirm reports: 148 frames, 3 aliases, 2 notes.</summary>
    public static MergeResult Merged() => new(
        MergeStatus.Merged,
        FramesMoved: 148,
        NotesRekeyed: 1,
        NotesAppended: 1,
        AliasesAdded: ["Deer Lick", "Deer Lick Group", "PGC 69327"],
        ManifestId: Guid.NewGuid());

    internal sealed class Harness : IDisposable
    {
        private readonly IDisposable? _database;

        internal Harness(IDisposable? database = null) => _database = database;

        public FakeDelay Delay { get; } = new();

        public RecordingLogger Logger { get; } = new();

        /// <summary>What the preview delegate returns. Assigning a new value and re-previewing is
        /// how a test observes a replaced side.</summary>
        public MergePreview? PreviewResult { get; set; }

        /// <summary>Set to throw from the preview delegate, so the failure path is
        /// observable.</summary>
        public bool PreviewThrows { get; set; }

        /// <summary>Every (winner, loser id, loser name) the preview was asked for, in
        /// order.</summary>
        public List<(Guid WinnerId, Guid? LoserId, string? LoserName)> Previews { get; } = [];

        /// <summary>The thread every preview ran on, so a test can prove the SQLite read left the
        /// UI thread. Empty unless <see cref="Create"/> was given an <c>onUiThread</c>
        /// reader.</summary>
        public List<bool> PreviewOnUiThread { get; } = [];

        /// <summary>Parks the preview delegate until a test releases it.</summary>
        public ManualResetEventSlim? PreviewRelease { get; init; }

        /// <summary>Parks both merge delegates until a test releases them, so a test can observe
        /// the dialog while a confirm is actually in flight (review finding 2).</summary>
        public ManualResetEventSlim? MergeRelease { get; set; }

        public List<(Guid WinnerId, Guid LoserId)> Merges { get; } = [];

        public List<(Guid WinnerId, string LoserName)> UnresolvedMerges { get; } = [];

        public MergeResult MergeResult { get; set; } = Merged();

        public List<string> Searches { get; } = [];

        public IReadOnlyList<TargetSearchResult> SearchResults { get; set; } = [];

        public List<bool> SearchOnUiThread { get; } = [];

        /// <summary>Every <c>CloseRequested</c> result, in order.</summary>
        public List<bool> Closes { get; } = [];

        /// <summary>Assigned by <see cref="Create"/> immediately after construction.</summary>
        public MergeDialogViewModel ViewModel { get; internal set; } = null!;

        /// <summary>Joins the in-flight preview, so a test asserts against a settled dialog
        /// instead of sleeping. The blocking wait lives here rather than in a test method, which
        /// is what xunit's own analyzer asks for. A test that asserts work ran off the UI thread
        /// awaits <c>PendingPreview</c> directly instead (TRACKING section 2 item 8).</summary>
        public Harness Settle()
        {
            try
            {
                ViewModel.PendingPreview?.Wait(Budget);
            }
            catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
            {
                // A preview cancelled by Dispose has settled: Task.Run with an already-cancelled
                // token faults the task rather than running the body.
            }

            return this;
        }

        public void Dispose()
        {
            ViewModel.Dispose();
            _database?.Dispose();
        }
    }

    /// <param name="request">What the dialog was opened for. Defaults to a candidate with both
    /// sides known.</param>
    /// <param name="preview">The first preview the dialog loads.</param>
    /// <param name="onUiThread">Reads whether the calling thread is the UI thread, so the
    /// off-thread assertions do not need a dispatcher reference inside this factory.</param>
    /// <param name="previewRelease">Parks the preview delegate until a test releases it, so a
    /// test can observe the dialog while its first preview is still in flight.</param>
    /// <param name="post">How the dialog reaches the UI thread. Defaults to running the closure
    /// inline.</param>
    public static Harness Create(
        MergeRequest? request = null,
        MergePreview? preview = null,
        Func<bool>? onUiThread = null,
        ManualResetEventSlim? previewRelease = null,
        Action<Action>? post = null)
    {
        var harness = new Harness
        {
            PreviewResult = preview ?? Preview(),
            PreviewRelease = previewRelease,
        };

        harness.ViewModel = new MergeDialogViewModel(
            request ?? new MergeRequest(WinnerId, LoserId, null, Guid.NewGuid()),
            (winnerId, loserId, loserName) =>
            {
                lock (harness.Previews)
                {
                    harness.Previews.Add((winnerId, loserId, loserName));
                    if (onUiThread is not null)
                    {
                        harness.PreviewOnUiThread.Add(onUiThread());
                    }
                }

                harness.PreviewRelease?.Wait(Budget);
                if (harness.PreviewThrows)
                {
                    throw new InvalidOperationException("the preview read failed");
                }

                return harness.PreviewResult;
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
            (winnerId, loserId) =>
            {
                lock (harness.Merges)
                {
                    harness.Merges.Add((winnerId, loserId));
                }

                harness.MergeRelease?.Wait(Budget);
                return harness.MergeResult;
            },
            (winnerId, loserName) =>
            {
                lock (harness.UnresolvedMerges)
                {
                    harness.UnresolvedMerges.Add((winnerId, loserName));
                }

                harness.MergeRelease?.Wait(Budget);
                return harness.MergeResult;
            },
            delay: harness.Delay.Delay,
            post: post ?? (action => action()),
            logger: harness.Logger);

        harness.ViewModel.CloseRequested += (_, merged) => harness.Closes.Add(merged);
        return harness;
    }

    /// <summary>
    /// A dialog over a real migrated database, a real <see cref="MergePreviewQuery"/> and a real
    /// <see cref="MergeRepository"/>. The roadmap's "cancel writes nothing" needs this: a
    /// recording lambda can only prove the delegate was not called, not that no row moved.
    /// </summary>
    public static (Harness Harness, MergeDatabase Database) CreateOverDatabase()
    {
        var database = new MergeDatabase();
        var connectionString = new DatabaseConnectionString(database.ConnectionString);
        var query = new MergePreviewQuery(connectionString);
        var repository = new MergeRepository(connectionString);
        var search = new TargetSearchQuery(connectionString);

        var harness = new Harness(database);
        harness.ViewModel = new MergeDialogViewModel(
            new MergeRequest(database.WinnerId, database.LoserId, null, database.CandidateId),
            query.Get,
            term => search.Search(term),
            repository.Merge,
            repository.MergeUnresolvedName,
            delay: harness.Delay.Delay,
            post: action => action(),
            logger: harness.Logger);

        harness.ViewModel.CloseRequested += (_, merged) => harness.Closes.Add(merged);
        return (harness.Settle(), database);
    }

    /// <summary>
    /// A migrated temp database holding a winner, a loser with frames and a session note on a
    /// colliding date, and one pending candidate. Owned by the harness that built it; the harness
    /// disposes it. Test-only plain file I/O, which <c>FileSafetyTest</c> does not scan.
    /// </summary>
    internal sealed class MergeDatabase : IDisposable
    {
        // F8: one App.Tests temp-database type (path, Pooling=False, Migrate, sidecar delete);
        // only the rows below are this fixture's own.
        private readonly TempDatabase _database = new("galactilog-merge-dialog");

        public MergeDatabase() => _database.Seed(context =>
        {
            context.Targets.Add(new Target
            {
                Id = WinnerId,
                PrimaryName = "NGC 7331",
                Aliases = JsonSerializer.Serialize(new[] { "Caldwell 30" }),
                ObjectType = "GiG,G",
            });
            context.Targets.Add(new Target
            {
                Id = LoserId,
                PrimaryName = "Deer Lick",
                Aliases = JsonSerializer.Serialize(new[] { "Deer Lick Group" }),
                ObjectType = "G",
            });

            foreach (var day in new[] { 3, 5 })
            {
                context.Images.Add(new Image
                {
                    Id = Guid.NewGuid(),
                    FilePath = $@"C:\GalactiLogFixture\merge\{day}.fits",
                    FileName = $"{day}.fits",
                    SessionDate = new DateOnly(2025, 1, day),
                    ResolvedTargetId = LoserId,
                    ImageType = "LIGHT",
                    ExposureTime = 300d,
                });
            }

            context.SessionNotes.Add(new SessionNote
            {
                Id = Guid.NewGuid(),
                TargetId = WinnerId,
                SessionDate = CollidingDate,
                Notes = "winner note",
                UpdatedAt = DateTime.UtcNow,
            });
            context.SessionNotes.Add(new SessionNote
            {
                Id = Guid.NewGuid(),
                TargetId = LoserId,
                SessionDate = CollidingDate,
                Notes = "loser note",
                UpdatedAt = DateTime.UtcNow,
            });

            context.MergeCandidates.Add(new MergeCandidate
            {
                Id = CandidateId,
                SourceName = "Deer Lick",
                SuggestedTargetId = WinnerId,
                Status = "pending",
                Method = "duplicate",
                SimilarityScore = 0.9d,
                CreatedAt = DateTime.UtcNow,
            });
        });

        /// <summary>The one date both targets carry a note for, so the dialog must list exactly
        /// it.</summary>
        public DateOnly CollidingDate => new(2025, 1, 3);

        public Guid CandidateId => Guid.Parse("60000000-0000-0000-0000-000000000000");

        public string ConnectionString => _database.ConnectionString;

        public Guid WinnerId => MergeDialogViewModelTestFactory.WinnerId;

        public Guid LoserId => MergeDialogViewModelTestFactory.LoserId;

        public int ManifestCount => Read(context => context.MergeManifests.Count());

        public int FramesOnLoser => Read(context => context.Images.Count(row => row.ResolvedTargetId == LoserId));

        public int FramesOnWinner => Read(context => context.Images.Count(row => row.ResolvedTargetId == WinnerId));

        public string CandidateStatus => Read(context => context.MergeCandidates.Single(row => row.Id == CandidateId).Status);

        public IReadOnlyList<string> WinnerAliases => Read(context =>
            JsonSerializer.Deserialize<string[]>(context.Targets.Single(row => row.Id == WinnerId).Aliases) ?? []);

        public Guid? LoserMergedIntoId => Read(context => context.Targets.Single(row => row.Id == LoserId).MergedIntoId);

        public void Dispose() => _database.Dispose();

        private T Read<T>(Func<GalactiLogContext, T> read) => _database.Read(read);
    }
}
