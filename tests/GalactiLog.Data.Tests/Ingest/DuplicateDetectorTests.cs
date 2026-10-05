using GalactiLog.Core.Targets;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.Data.Tests.Ingest;

// Spec 9.7's duplicate detection pass. The resolver is supplied as a delegate, so every Pass 1
// outcome is driven with no network and no catalogs directory (spec 18.2: "Network is never
// touched in tests").
//
// The trigram cases use the same short synthetic names TargetSearchQueryTests works out by hand,
// so the 0.4 threshold is asserted against exact figures rather than whatever the implementation
// happens to produce:
//   "ab"  (3 trigrams) vs "abc"   (4): intersection 2, union 5 -> 0.4      exactly at
//   "ab"  (3 trigrams) vs "abcd"  (5): intersection 2, union 6 -> 0.3333   just under
//   "abc" (4 trigrams) vs "abcde" (6): intersection 3, union 7 -> 0.42857  just over
public class DuplicateDetectorTests : IDisposable
{
    private readonly TestDatabaseHandle _db = TestDatabaseFactory.CreateMigratedDatabase();
    private static readonly DateOnly SessionDate = new(2025, 3, 15);

    public void Dispose() => _db.Dispose();

    private string Cs => _db.ConnectionString;

    private GalactiLogContext OpenRead() => new(GalactiLogContextOptions.Create(Cs));

    // ---- helpers -------------------------------------------------------------------------

    private static ResolvedIdentity Identity(string primaryName)
        => new(primaryName, null, null, null, null, null, []);

    private static TargetResolver.ResolutionResult Unresolved()
        => new(null, TargetResolver.ResolutionStage.Unresolved, null);

    private static TargetResolver.ResolutionResult MatchedExisting(Guid targetId, string identityName)
        => new(targetId, TargetResolver.ResolutionStage.Simbad, Identity(identityName));

    private static TargetResolver.ResolutionResult NewIdentity(string identityName)
        => new(null, TargetResolver.ResolutionStage.Simbad, Identity(identityName));

    private static TargetResolver.ResolutionResult NetworkFailure()
        => new(null, TargetResolver.ResolutionStage.Unresolved, null, TransientNetworkFailure: true);

    private void AddUnresolvedFrames(string rawHeaders, int count, string imageType = "LIGHT")
    {
        for (var i = 0; i < count; i++)
        {
            LibrarySeeder.AddFrame(Cs, targetId: null, SessionDate, image =>
            {
                image.RawHeaders = rawHeaders;
                image.ImageType = imageType;
            });
        }
    }

    private void AddNamedFrames(string objectName, int count, string imageType = "LIGHT")
        => AddUnresolvedFrames(LibrarySeeder.RawHeadersWithObject(objectName), count, imageType);

    // Every Pass 1 outcome is a function of the name alone, so the stub is a name -> answer map.
    // Names with no entry resolve to nothing, which is the trigram/orphan path.
    private sealed class ResolverStub
    {
        public Dictionary<string, Func<bool, TargetResolver.ResolutionResult>> Answers { get; } = new(StringComparer.Ordinal);

        public List<string> Calls { get; } = [];

        public Action? AfterCall { get; set; }

        public TargetResolver.ResolutionResult Resolve(string name, bool createIfMissing, CancellationToken ct)
        {
            Calls.Add(name);
            var answer = Answers.TryGetValue(name, out var supplier)
                ? supplier(createIfMissing)
                : Unresolved();
            AfterCall?.Invoke();
            return answer;
        }
    }

    private DuplicateDetector.DedupOutcome Run(ResolverStub stub, CancellationToken ct = default)
        => new DuplicateDetector(Cs, stub.Resolve).Run((_, _, _) => { }, ct);

    private List<MergeCandidate> Candidates()
    {
        using var context = OpenRead();
        return [.. context.MergeCandidates.OrderBy(candidate => candidate.SourceName)];
    }

    // ---- Pass 1 outcome 1: the name resolves onto an existing target ---------------------

    [Fact]
    public void Run_NameThatResolvesOntoAnExistingTarget_WritesASimbadCandidate()
    {
        var target = LibrarySeeder.AddTarget(Cs, "M 31");
        AddNamedFrames("M31_mosaic", 3);

        var stub = new ResolverStub();
        stub.Answers["M31_mosaic"] = _ => MatchedExisting(target.Id, "NGC 224");

        var outcome = Run(stub);

        var candidate = Assert.Single(Candidates());
        Assert.Equal("M31_mosaic", candidate.SourceName);
        Assert.Equal(3, candidate.SourceImageCount);
        Assert.Equal(target.Id, candidate.SuggestedTargetId);
        Assert.Equal("simbad", candidate.Method);
        Assert.Equal("pending", candidate.Status);
        Assert.Null(candidate.ResolvedAt);
        Assert.Equal(1, outcome.CandidatesWritten);
        Assert.Equal(0, outcome.TargetsCreated);
    }

    [Fact]
    public void Run_SimbadCandidate_ReasonTextMatchesTheSpecStringVerbatim()
    {
        // The identity carries a different primary name from the target row, so this also proves
        // the reason names the EXISTING target's primary_name, read back from `targets`.
        var target = LibrarySeeder.AddTarget(Cs, "M 31");
        AddNamedFrames("M31_mosaic", 1);

        var stub = new ResolverStub();
        stub.Answers["M31_mosaic"] = _ => MatchedExisting(target.Id, "NGC 224");

        Run(stub);

        Assert.Equal(
            "SIMBAD resolves \"M31_mosaic\" to the same object as \"M 31\"",
            Assert.Single(Candidates()).ReasonText);
    }

    [Fact]
    public void Run_SimbadCandidate_ScoresOne()
    {
        var target = LibrarySeeder.AddTarget(Cs, "M 31");
        AddNamedFrames("M31_mosaic", 1);

        var stub = new ResolverStub();
        stub.Answers["M31_mosaic"] = _ => MatchedExisting(target.Id, "M 31");

        Run(stub);

        Assert.Equal(1.0, Assert.Single(Candidates()).SimilarityScore);
    }

    // ---- Pass 1 outcome 2: it resolves, but no existing target matches -------------------

    // createIfMissing: false answers with an identity and no target; createIfMissing: true
    // actually inserts the target, exactly as TargetResolver would.
    private ResolverStub StubThatCreates(string name, string primaryName, out Func<Guid> createdId)
    {
        Guid? created = null;
        createdId = () => created ?? throw new InvalidOperationException("the target was never created");

        var stub = new ResolverStub();
        stub.Answers[name] = createIfMissing =>
        {
            if (!createIfMissing)
            {
                return NewIdentity(primaryName);
            }

            created ??= LibrarySeeder.AddTarget(Cs, primaryName).Id;
            return new TargetResolver.ResolutionResult(
                created, TargetResolver.ResolutionStage.Simbad, Identity(primaryName), Created: true);
        };
        return stub;
    }

    [Fact]
    public void Run_NameThatResolvesToANewIdentity_CreatesTheTargetAndAssignsTheFrames()
    {
        AddNamedFrames("M 33", 4);
        var stub = StubThatCreates("M 33", "M 33", out var createdId);

        var outcome = Run(stub);

        Assert.Equal(1, outcome.TargetsCreated);
        Assert.Equal(4, outcome.FramesAssigned);

        using var context = OpenRead();
        Assert.Equal(4, context.Images.Count(image => image.ResolvedTargetId == createdId()));
    }

    // Phase 7 Task 7's sixth ResolutionStage, which this switch must handle or throw
    // NotSupportedException and fail the whole scan. A solar-system match creates a user_defined
    // target, so it is outcome 2: the frames are assigned and no candidate is written.
    [Fact]
    public void Run_SolarSystemStage_CreatesTheTargetAndAssignsTheFramesLikeOutcome2()
    {
        AddNamedFrames("Jupiter", 3);

        Guid? created = null;
        var stub = new ResolverStub();
        stub.Answers["Jupiter"] = createIfMissing =>
        {
            if (!createIfMissing)
            {
                return new TargetResolver.ResolutionResult(
                    null, TargetResolver.ResolutionStage.SolarSystem, Identity("Jupiter"));
            }

            created ??= LibrarySeeder.AddTarget(Cs, "Jupiter", row =>
            {
                row.ObjectType = "Planet";
                row.UserDefined = true;
            }).Id;
            return new TargetResolver.ResolutionResult(
                created, TargetResolver.ResolutionStage.SolarSystem, Identity("Jupiter"), Created: true);
        };

        var outcome = Run(stub);

        Assert.Equal(1, outcome.TargetsCreated);
        Assert.Equal(3, outcome.FramesAssigned);
        Assert.Empty(Candidates());

        using var context = OpenRead();
        Assert.Equal(3, context.Images.Count(image => image.ResolvedTargetId == created));
    }

    // Review finding 3. A second spelling of the same body ("Sol" when a "Sun" target exists)
    // comes back SolarSystem WITH a TargetId, which has outcome 1's shape but not its meaning:
    // outcome 1 would write a candidate reading "SIMBAD resolves ... to the same object as", and
    // SIMBAD has never heard of this name. The frames are assigned directly instead.
    [Fact]
    public void Run_SolarSystemStage_LinkedToAnExistingTarget_AssignsTheFramesAndWritesNoCandidate()
    {
        AddNamedFrames("Sol", 2);
        var sun = LibrarySeeder.AddTarget(Cs, "Sun", row =>
        {
            row.ObjectType = "Sun";
            row.UserDefined = true;
        });

        var stub = new ResolverStub();
        stub.Answers["Sol"] = _ => new TargetResolver.ResolutionResult(
            sun.Id, TargetResolver.ResolutionStage.SolarSystem, Identity("Sun"));

        var outcome = Run(stub);

        Assert.Empty(Candidates());
        Assert.Equal(0, outcome.CandidatesWritten);
        Assert.Equal(0, outcome.TargetsCreated);
        Assert.Equal(2, outcome.FramesAssigned);

        using var context = OpenRead();
        Assert.Equal(2, context.Images.Count(image => image.ResolvedTargetId == sun.Id));
    }

    [Fact]
    public void Run_NameThatResolvesToANewIdentity_WritesNoCandidate()
    {
        AddNamedFrames("M 33", 4);
        var stub = StubThatCreates("M 33", "M 33", out _);

        var outcome = Run(stub);

        Assert.Empty(Candidates());
        Assert.Equal(0, outcome.CandidatesWritten);
    }

    [Fact]
    public void Run_Outcome2_AssignsANumericObjectCardsFrames()
    {
        // Phase 5 fix F1 at this statement: json_extract yields a SQLite number for an unquoted
        // numeric OBJECT card, so the assignment compares against the group-key expression rather
        // than against json_extract directly.
        AddUnresolvedFrames("""{"OBJECT":7331}""", 3);
        var stub = StubThatCreates("7331", "NGC 7331", out var createdId);

        var outcome = Run(stub);

        Assert.Equal(3, outcome.FramesAssigned);
        using var context = OpenRead();
        Assert.Equal(3, context.Images.Count(image => image.ResolvedTargetId == createdId()));
    }

    [Fact]
    public void Run_Outcome2_LeavesCalibrationFramesUnassigned()
    {
        AddNamedFrames("M 33", 2);
        AddNamedFrames("M 33", 5, imageType: "DARK");
        var stub = StubThatCreates("M 33", "M 33", out _);

        var outcome = Run(stub);

        Assert.Equal(2, outcome.FramesAssigned);
        using var context = OpenRead();
        Assert.Equal(5, context.Images.Count(image => image.ImageType == "DARK" && image.ResolvedTargetId == null));
    }

    // ---- Pass 1 outcome 3: trigram ------------------------------------------------------

    [Fact]
    public void Run_UnresolvableNameAboveThreshold_WritesATrigramCandidate()
    {
        var target = LibrarySeeder.AddTarget(Cs, "abcde");
        AddNamedFrames("abc", 2);

        Run(new ResolverStub());

        var candidate = Assert.Single(Candidates());
        Assert.Equal("trigram", candidate.Method);
        Assert.Equal(target.Id, candidate.SuggestedTargetId);
        Assert.Equal(3d / 7d, candidate.SimilarityScore, 10);
        Assert.Equal(2, candidate.SourceImageCount);
    }

    [Fact]
    public void Run_TrigramCandidate_ReasonTextTruncatesThePercentage()
    {
        // 3/7 is 0.428571..., which truncates to 42 and would round to 43. The web source writes
        // int(float(score) * 100), so 42 is the answer.
        LibrarySeeder.AddTarget(Cs, "abcde");
        AddNamedFrames("abc", 1);

        Run(new ResolverStub());

        Assert.Equal(
            "Name is 42% similar to \"abcde\"",
            Assert.Single(Candidates()).ReasonText);
    }

    [Fact]
    public void Run_TrigramCandidate_ScoresAgainstAliasesAsWellAsPrimaryNames()
    {
        var target = LibrarySeeder.AddTarget(Cs, "zzzzzz", t => t.Aliases = """["abcde"]""");
        AddNamedFrames("abc", 1);

        Run(new ResolverStub());

        var candidate = Assert.Single(Candidates());
        Assert.Equal("trigram", candidate.Method);
        Assert.Equal(target.Id, candidate.SuggestedTargetId);
    }

    [Fact]
    public void Run_TrigramCandidate_IgnoresMergedAwayTargets()
    {
        var keeper = LibrarySeeder.AddTarget(Cs, "zzzzzz");
        LibrarySeeder.AddTarget(Cs, "abcde", t => t.MergedIntoId = keeper.Id);
        AddNamedFrames("abc", 1);

        Run(new ResolverStub());

        Assert.Equal("orphan", Assert.Single(Candidates()).Method);
    }

    [Fact]
    public void Run_ScoreExactlyAtTheThreshold_IsAnOrphan()
    {
        // Strictly greater than 0.4: "ab" against "abc" scores exactly 0.4 and falls through.
        LibrarySeeder.AddTarget(Cs, "abc");
        AddNamedFrames("ab", 1);

        Run(new ResolverStub());

        var candidate = Assert.Single(Candidates());
        Assert.Equal("orphan", candidate.Method);
        Assert.Null(candidate.SuggestedTargetId);
    }

    // ---- Pass 1 outcome 4: orphan -------------------------------------------------------

    [Fact]
    public void Run_UnresolvableNameBelowThreshold_WritesAnOrphanCandidate()
    {
        LibrarySeeder.AddTarget(Cs, "abcd");
        AddNamedFrames("ab", 3);

        var outcome = Run(new ResolverStub());

        var candidate = Assert.Single(Candidates());
        Assert.Equal("orphan", candidate.Method);
        Assert.Null(candidate.SuggestedTargetId);
        Assert.Equal(0.0, candidate.SimilarityScore);
        Assert.Equal("pending", candidate.Status);
        Assert.Equal(3, candidate.SourceImageCount);
        Assert.Equal(1, outcome.OrphanCount);
    }

    [Fact]
    public void Run_OrphanCandidate_ReasonTextMatchesTheSpecStringVerbatim()
    {
        AddNamedFrames("something unknown", 1);

        Run(new ResolverStub());

        Assert.Equal("No match found in SIMBAD or existing targets", Assert.Single(Candidates()).ReasonText);
    }

    // ---- the name set -------------------------------------------------------------------

    private void AssertNameWithACandidateIsSkipped(string status)
    {
        LibrarySeeder.AddMergeCandidate(Cs, "M31_mosaic", candidate => candidate.Status = status);
        AddNamedFrames("M31_mosaic", 2);

        var stub = new ResolverStub();
        var outcome = Run(stub);

        Assert.Single(Candidates());
        Assert.Empty(stub.Calls);
        Assert.Equal(0, outcome.NamesExamined);
    }

    [Fact]
    public void Run_NameWithAPendingCandidate_IsSkipped() => AssertNameWithACandidateIsSkipped("pending");

    // The rule that stops a suggestion the user rejected coming back on the next scan.
    [Fact]
    public void Run_NameWithADismissedCandidate_IsSkipped() => AssertNameWithACandidateIsSkipped("dismissed");

    [Fact]
    public void Run_NameWithAnAcceptedCandidate_IsSkipped() => AssertNameWithACandidateIsSkipped("accepted");

    [Fact]
    public void Run_EmptyAndMissingObjectCards_AreNotNames()
    {
        AddUnresolvedFrames("""{"OBJECT":""}""", 2);
        AddUnresolvedFrames("{}", 2);
        AddNamedFrames("real name", 1);

        var outcome = Run(new ResolverStub());

        Assert.Equal("real name", Assert.Single(Candidates()).SourceName);
        Assert.Equal(1, outcome.NamesExamined);
    }

    [Fact]
    public void Run_SourceImageCount_IsTheFrameCountOfTheName()
    {
        AddNamedFrames("M 33", 5);
        AddNamedFrames("M 33", 2, imageType: "FLAT");

        Run(new ResolverStub());

        Assert.Equal(5, Assert.Single(Candidates()).SourceImageCount);
    }

    // ---- failure handling (ruling Q7) ---------------------------------------------------

    [Fact]
    public void Run_TransientNetworkFailure_WritesNoCandidateAndStopsThePass()
    {
        // Ordered by frame count descending, so "aaa" is the first name examined.
        AddNamedFrames("aaa", 5);
        AddNamedFrames("bbb", 1);

        // Two active targets sharing an alias, so this also proves Pass 2 still runs.
        var alpha = LibrarySeeder.AddTarget(Cs, "Alpha", t => t.Aliases = """["NGC 224"]""");
        LibrarySeeder.AddTarget(Cs, "Beta", t => t.Aliases = """["ngc 224"]""");

        var stub = new ResolverStub();
        stub.Answers["aaa"] = _ => NetworkFailure();

        var outcome = Run(stub);

        Assert.True(outcome.StoppedOnNetworkFailure);
        Assert.Equal(["aaa"], stub.Calls);

        var candidates = Candidates();
        Assert.DoesNotContain(candidates, candidate => candidate.SourceName is "aaa" or "bbb");
        var pass2 = Assert.Single(candidates);
        Assert.Equal("duplicate", pass2.Method);
        Assert.Equal(alpha.Id, pass2.SuggestedTargetId);
    }

    [Fact]
    public void Run_NonTransientFailureOnOneName_SkipsThatNameAndContinues()
    {
        AddNamedFrames("aaa", 5);
        AddNamedFrames("bbb", 1);

        var stub = new ResolverStub();
        stub.Answers["aaa"] = _ => throw new NonTransientCatalogException("400 Bad Request");

        var outcome = Run(stub);

        Assert.False(outcome.StoppedOnNetworkFailure);
        Assert.Equal(2, outcome.NamesExamined);
        Assert.Equal("bbb", Assert.Single(Candidates()).SourceName);
    }

    // Outcome 2's create call reaches the same clients as the first call, so both failure modes
    // have to behave there exactly as they do on the first one (Task 1 review, item 1).

    [Fact]
    public void Run_NonTransientFailureOnTheCreateCall_SkipsThatName()
    {
        AddNamedFrames("aaa", 5);
        AddNamedFrames("bbb", 1);

        var stub = new ResolverStub();
        stub.Answers["aaa"] = createIfMissing => createIfMissing
            ? throw new NonTransientCatalogException("400 Bad Request")
            : NewIdentity("Target A");

        var outcome = Run(stub);

        Assert.False(outcome.StoppedOnNetworkFailure);
        Assert.Equal(0, outcome.TargetsCreated);
        Assert.Equal(0, outcome.FramesAssigned);
        Assert.Equal(2, outcome.NamesExamined);

        // "aaa" is skipped without a candidate; "bbb" still gets its orphan row.
        Assert.Equal("bbb", Assert.Single(Candidates()).SourceName);
        using var context = OpenRead();
        Assert.Empty(context.Targets);
    }

    [Fact]
    public void Run_TransientNetworkFailureOnTheCreateCall_StopsThePass()
    {
        AddNamedFrames("aaa", 5);
        AddNamedFrames("bbb", 1);

        var stub = new ResolverStub();
        stub.Answers["aaa"] = createIfMissing => createIfMissing
            ? NetworkFailure()
            : NewIdentity("Target A");

        var outcome = Run(stub);

        Assert.True(outcome.StoppedOnNetworkFailure);
        Assert.Equal(0, outcome.TargetsCreated);
        Assert.Equal(0, outcome.FramesAssigned);
        Assert.Equal(["aaa", "aaa"], stub.Calls);
        Assert.Empty(Candidates());
    }

    [Fact]
    public void Run_Cancellation_StopsAndKeepsWhatItAlreadyWrote()
    {
        AddNamedFrames("aaa", 5);
        AddNamedFrames("bbb", 3);
        AddNamedFrames("ccc", 1);

        using var cts = new CancellationTokenSource();
        var stub = new ResolverStub { AfterCall = cts.Cancel };

        Assert.Throws<OperationCanceledException>(() => Run(stub, cts.Token));

        // The first name's decision was saved before the cancellation checkpoint of the second.
        var candidate = Assert.Single(Candidates());
        Assert.Equal("aaa", candidate.SourceName);
    }

    // ---- Pass 2 (ruling Q5) -------------------------------------------------------------

    [Fact]
    public void Pass2_TwoActiveTargetsSharingANormalizedName_ProposesAMerge()
    {
        var alpha = LibrarySeeder.AddTarget(Cs, "M 31");
        LibrarySeeder.AddTarget(Cs, "m  31");

        Run(new ResolverStub());

        var candidate = Assert.Single(Candidates());
        Assert.Equal("duplicate", candidate.Method);
        Assert.Equal("pending", candidate.Status);
        Assert.Equal(1.0, candidate.SimilarityScore);
        Assert.Equal("m  31", candidate.SourceName);
        Assert.Equal(alpha.Id, candidate.SuggestedTargetId);
    }

    [Fact]
    public void Pass2_TwoActiveTargetsSharingAnAlias_ProposesAMerge()
    {
        var alpha = LibrarySeeder.AddTarget(Cs, "Alpha", t => t.Aliases = """["NGC 224"]""");
        LibrarySeeder.AddTarget(Cs, "Beta", t => t.Aliases = """["ngc 224"]""");

        Run(new ResolverStub());

        var candidate = Assert.Single(Candidates());
        Assert.Equal("duplicate", candidate.Method);
        Assert.Equal("Beta", candidate.SourceName);
        Assert.Equal(alpha.Id, candidate.SuggestedTargetId);
    }

    [Fact]
    public void Pass2_SuggestedWinnerIsTheTargetWithTheMostFrames()
    {
        // Beta is alphabetically second but carries more LIGHT frames, so it wins. Alpha's five
        // calibration frames count for neither the tie-break nor source_image_count: a "duplicate"
        // row records the same quantity a Pass 1 row does (coordinator ruling on the Task 1
        // review), which without the filter would make Alpha the winner at 6 frames.
        var alpha = LibrarySeeder.AddTarget(Cs, "Alpha", t => t.Aliases = """["NGC 224"]""");
        var beta = LibrarySeeder.AddTarget(Cs, "Beta", t => t.Aliases = """["NGC 224"]""");
        LibrarySeeder.AddFrame(Cs, alpha.Id, SessionDate);
        for (var i = 0; i < 5; i++)
        {
            LibrarySeeder.AddFrame(Cs, alpha.Id, SessionDate, image => image.ImageType = "DARK");
        }
        LibrarySeeder.AddFrame(Cs, beta.Id, SessionDate);
        LibrarySeeder.AddFrame(Cs, beta.Id, SessionDate);

        Run(new ResolverStub());

        var candidate = Assert.Single(Candidates());
        Assert.Equal("Alpha", candidate.SourceName);
        Assert.Equal(1, candidate.SourceImageCount);
        Assert.Equal(beta.Id, candidate.SuggestedTargetId);
    }

    [Fact]
    public void Pass2_ReasonTextNamesTheSharedAliasAndTheWinner()
    {
        LibrarySeeder.AddTarget(Cs, "Alpha", t => t.Aliases = """["NGC 224"]""");
        LibrarySeeder.AddTarget(Cs, "Beta", t => t.Aliases = """["ngc 224"]""");

        Run(new ResolverStub());

        Assert.Equal(
            "Shares alias \"NGC 224\" with \"Alpha\"",
            Assert.Single(Candidates()).ReasonText);
    }

    [Fact]
    public void Pass2_DoesNotProposeANamePass1JustWrote()
    {
        LibrarySeeder.AddTarget(Cs, "Alpha", t => t.Aliases = """["NGC 224"]""");
        var beta = LibrarySeeder.AddTarget(Cs, "Beta", t => t.Aliases = """["ngc 224"]""");
        LibrarySeeder.AddFrame(Cs, targetId: null, SessionDate,
            image => image.RawHeaders = LibrarySeeder.RawHeadersWithObject("Beta"));

        Run(new ResolverStub());

        // Pass 1 wrote a trigram candidate for the unresolved "Beta" frames; Pass 2's loser is
        // also named "Beta" and must not write a second row for it.
        var candidate = Candidates().Single(row => row.SourceName == "Beta");
        Assert.Equal("trigram", candidate.Method);
        Assert.Equal(beta.Id, candidate.SuggestedTargetId);
    }

    [Fact]
    public void Pass2_IgnoresMergedAwayTargets()
    {
        var alpha = LibrarySeeder.AddTarget(Cs, "Alpha", t => t.Aliases = """["NGC 224"]""");
        LibrarySeeder.AddTarget(Cs, "Beta", t =>
        {
            t.Aliases = """["ngc 224"]""";
            t.MergedIntoId = alpha.Id;
        });

        Run(new ResolverStub());

        Assert.Empty(Candidates());
    }

    [Fact]
    public void Pass2_SingleTargetPerName_ProposesNothing()
    {
        LibrarySeeder.AddTarget(Cs, "Alpha", t => t.Aliases = """["NGC 224"]""");
        LibrarySeeder.AddTarget(Cs, "Beta", t => t.Aliases = """["NGC 7331"]""");

        Run(new ResolverStub());

        Assert.Empty(Candidates());
    }

    // ---- progress -----------------------------------------------------------------------

    [Fact]
    public void Run_ReportsProgressOncePerName()
    {
        AddNamedFrames("aaa", 5);
        AddNamedFrames("bbb", 3);
        AddNamedFrames("ccc", 1);

        var reports = new List<(int Step, int Total)>();
        new DuplicateDetector(Cs, new ResolverStub().Resolve)
            .Run((step, total, _) => reports.Add((step, total)), CancellationToken.None);

        Assert.Equal([(1, 3), (2, 3), (3, 3)], reports);
    }
}
