using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Queries;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GalactiLog.Data.Tests.Ingest;

// Spec 11.4's reference thumbnail pass. The render is a recording lambda, so no test here decodes
// a frame, writes a JPEG or touches the filesystem at all: what is under test is which frame is
// offered for which target, what is stored, when it commits, and what an interrupted run keeps.
//
// NO TEST HERE FETCHES ANYTHING. Spec 19.2 makes survey image downloads an explicit non-goal;
// NoSurveyImageFetchTest is the structural proof and this class is the behavioural one.
public class ReferenceThumbnailPassTests : IDisposable
{
    private static readonly DateOnly Night = new(2025, 3, 15);

    private readonly TestDatabaseHandle _db = TestDatabaseFactory.CreateMigratedDatabase();

    public void Dispose() => _db.Dispose();

    // Every call the render delegate received, in order, with the force flag it was given.
    private readonly ConcurrentQueue<(Guid TargetId, string FramePath, bool Force)> _calls = new();

    private ReferenceThumbnailSourcesQuery Query => new(new DatabaseConnectionString(_db.ConnectionString));

    // The render: returns the cache-relative path the real ThumbnailCache.EnsureReference would,
    // unless the test's policy says otherwise. Null is spec 6.2.6's "these pixels do not decode".
    private ReferenceThumbnailPass MakePass(Func<Guid, string, string?>? render = null)
        => new(
            _db.ConnectionString,
            (targetId, framePath, force, _) =>
            {
                _calls.Enqueue((targetId, framePath, force));
                return render is null ? RelativePathFor(targetId) : render(targetId, framePath);
            },
            Query.Get);

    private static string RelativePathFor(Guid targetId) => $"reference/{targetId:D}.jpg";

    // One target with one eligible frame per named path. Names are zero-padded so the query's
    // ORDER BY primary_name is the creation order, which is what the commit-cadence and
    // interruption tests count against.
    private Guid AddTarget(int index, params string[] framePaths)
    {
        var name = "T" + index.ToString("D2", CultureInfo.InvariantCulture);
        var target = LibrarySeeder.AddTarget(_db.ConnectionString, name);
        var hour = 23;
        foreach (var path in framePaths.Length == 0 ? [$@"C:\Astro\{name}.fits"] : framePaths)
        {
            LibrarySeeder.AddFrame(_db.ConnectionString, target.Id, Night, image =>
            {
                image.FilePath = path;
                image.CaptureDate = Night.ToDateTime(new TimeOnly(hour, 0));
            });
            hour--;
        }

        return target.Id;
    }

    private IReadOnlyList<Guid> AddTargets(int count)
        => [.. Enumerable.Range(0, count).Select(index => AddTarget(index))];

    private string? StoredPath(Guid targetId)
    {
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(_db.ConnectionString));
        return context.Targets.Single(target => target.Id == targetId).ReferenceThumbnailPath;
    }

    private static void Ignore(int step, int total, string message)
    {
        _ = step;
        _ = total;
        _ = message;
    }

    // ------------------------------------------------------------------ the ordinary outcome

    [Fact]
    public void Run_GeneratesOneThumbnailPerEligibleTarget()
    {
        var targets = AddTargets(3);

        var outcome = MakePass().Run(force: false, Ignore, CancellationToken.None);

        Assert.Equal(new ReferenceThumbnailPass.ReferenceThumbnailOutcome(3, 3, 0, Cancelled: false), outcome);
        Assert.All(targets, targetId => Assert.NotNull(StoredPath(targetId)));
    }

    [Fact]
    public void Run_StoresTheReturnedRelativePath()
    {
        var targetId = AddTarget(0);

        MakePass((_, _) => "reference/whatever-the-cache-said.jpg").Run(force: false, Ignore, CancellationToken.None);

        // The path the cache returned, verbatim and relative: spec 5.3's column is "relative to
        // the thumbnail cache root", and nothing here rebuilds it.
        Assert.Equal("reference/whatever-the-cache-said.jpg", StoredPath(targetId));
    }

    [Fact]
    public void Run_PassesTheTargetsMostRecentLightFramePath()
    {
        var targetId = AddTarget(0, @"C:\Astro\newest.fits", @"C:\Astro\older.fits");

        MakePass().Run(force: false, Ignore, CancellationToken.None);

        var call = Assert.Single(_calls);
        Assert.Equal(targetId, call.TargetId);
        Assert.Equal(@"C:\Astro\newest.fits", call.FramePath);
    }

    [Fact]
    public void Run_NotForced_SkipsTargetsThatAlreadyHaveAPath()
    {
        var done = AddTarget(0);
        var todo = AddTarget(1);
        MakePass().Run(force: false, Ignore, CancellationToken.None);
        _calls.Clear();

        // Nothing changed, so the second run has nothing to do for either target.
        var outcome = MakePass().Run(force: false, Ignore, CancellationToken.None);

        Assert.Equal(0, outcome.Total);
        Assert.Empty(_calls);
        Assert.NotNull(StoredPath(done));
        Assert.NotNull(StoredPath(todo));
    }

    // The pass offers every target to the render and forwards force to it. Whether new pixels
    // appear is the cache's half of the contract and is asserted there
    // (ThumbnailCacheTests.EnsureReference_Forced_RendersAgainOverTheExistingFile): with a stub
    // render this test can only prove the offer and the flag.
    [Fact]
    public void Run_Forced_OffersEveryTargetToTheRendererWithForce()
    {
        AddTargets(2);
        MakePass().Run(force: false, Ignore, CancellationToken.None);
        _calls.Clear();

        var outcome = MakePass().Run(force: true, Ignore, CancellationToken.None);

        Assert.Equal(2, outcome.Total);
        Assert.Equal(2, outcome.Generated);
        Assert.Equal(2, _calls.Count);
        Assert.All(_calls, call => Assert.True(call.Force, "the pass did not forward force to the render"));
    }

    // --------------------------------------------------------- rejection for pixel reading (Q13)

    [Fact]
    public void Run_RenderReturnsNull_CountsAFailureAndLeavesThePathNull()
    {
        var targetId = AddTarget(0);

        var outcome = MakePass((_, _) => null).Run(force: false, Ignore, CancellationToken.None);

        Assert.Equal(new ReferenceThumbnailPass.ReferenceThumbnailOutcome(1, 0, 1, Cancelled: false), outcome);
        Assert.Null(StoredPath(targetId));
    }

    [Fact]
    public void Run_RenderReturnsNull_TriesTheNextFrame()
    {
        // Questions.md Q14 as ruled: at most three frames per target, newest first, stopping at
        // the first that renders. One corrupt file does not cost a target its thumbnail.
        var targetId = AddTarget(0, @"C:\Astro\corrupt.fits", @"C:\Astro\good.fits", @"C:\Astro\older.fits");

        var outcome = MakePass((_, framePath) =>
            framePath.EndsWith(@"corrupt.fits", StringComparison.Ordinal) ? null : "reference/second.jpg")
            .Run(force: false, Ignore, CancellationToken.None);

        Assert.Equal(1, outcome.Generated);
        Assert.Equal(0, outcome.Failed);
        Assert.Equal("reference/second.jpg", StoredPath(targetId));
        Assert.Equal(
            [@"C:\Astro\corrupt.fits", @"C:\Astro\good.fits"],
            _calls.Select(call => call.FramePath).ToList());
    }

    [Fact]
    public void Run_EveryFrameFails_LeavesNullAndCountsOneFailure()
    {
        // The bound: three attempts, then the target keeps a null path and is picked up again by
        // the next run. A failure is counted per target, not per attempt.
        var targetId = AddTarget(0, @"C:\Astro\a.fits", @"C:\Astro\b.fits", @"C:\Astro\c.fits", @"C:\Astro\d.fits");

        var outcome = MakePass((_, _) => null).Run(force: false, Ignore, CancellationToken.None);

        Assert.Equal(new ReferenceThumbnailPass.ReferenceThumbnailOutcome(1, 0, 1, Cancelled: false), outcome);
        Assert.Null(StoredPath(targetId));
        Assert.Equal(ReferenceThumbnailSourcesQuery.MaxFramesPerTarget, _calls.Count);
    }

    [Fact]
    public void Run_RenderThrows_CountsAFailureAndContinues()
    {
        var thrower = AddTarget(0);
        var healthy = AddTarget(1);

        var outcome = MakePass((targetId, _) => targetId == thrower
            ? throw new IOException("the frame went away")
            : RelativePathFor(targetId))
            .Run(force: false, Ignore, CancellationToken.None);

        // One bad frame must not fail a scan that has already ingested successfully.
        Assert.Equal(new ReferenceThumbnailPass.ReferenceThumbnailOutcome(2, 1, 1, Cancelled: false), outcome);
        Assert.Null(StoredPath(thrower));
        Assert.NotNull(StoredPath(healthy));
    }

    // ------------------------------------------------------------------- the commit contract

    [Fact]
    public void Run_CommitsEveryTenTargets()
    {
        // The roadmap's named assertion. The eleventh render opens its own read connection and
        // asserts the first ten paths are already visible to it, which is only true if the pass
        // committed at ten rather than at the end.
        var targets = AddTargets(12);
        var visibleAtEleven = new List<string?>();

        MakePass((_, _) =>
        {
            if (_calls.Count == ReferenceThumbnailPass.CommitChunk + 1)
            {
                visibleAtEleven.AddRange(targets.Take(ReferenceThumbnailPass.CommitChunk).Select(StoredPath));
            }

            return "reference/x.jpg";
        }).Run(force: false, Ignore, CancellationToken.None);

        Assert.Equal(ReferenceThumbnailPass.CommitChunk, visibleAtEleven.Count);
        Assert.All(visibleAtEleven, path => Assert.Equal("reference/x.jpg", path));
    }

    [Fact]
    public void Run_InterruptedAfterFifteenTargets_KeepsTheFirstFifteen()
    {
        // The roadmap's Verify line: an interrupted pass keeps what it produced. Cancellation is
        // requested from inside the fifteenth render, so that target still completes and the loop
        // breaks before the sixteenth.
        var targets = AddTargets(20);
        using var cts = new CancellationTokenSource();

        var outcome = MakePass((targetId, _) =>
        {
            if (_calls.Count == 15)
            {
                cts.Cancel();
            }

            return RelativePathFor(targetId);
        }).Run(force: false, Ignore, cts.Token);

        Assert.True(outcome.Cancelled);
        Assert.Equal(15, outcome.Generated);
        Assert.All(targets.Take(15), targetId => Assert.NotNull(StoredPath(targetId)));
        Assert.All(targets.Skip(15), targetId => Assert.Null(StoredPath(targetId)));
    }

    [Fact]
    public void Run_CancelledInsideARender_KeepsWhatTheChunkHadAlreadyGenerated()
    {
        // The renderer takes the token and throws out of it, which is the common way a cancelled
        // scan reaches this pass. Rethrowing would escape Run before its trailing SaveChanges and
        // drop every path generated since the last commit: here, three of them, inside one chunk.
        var targets = AddTargets(5);
        using var cts = new CancellationTokenSource();

        var outcome = MakePass((targetId, _) =>
        {
            if (_calls.Count == 4)
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            }

            return RelativePathFor(targetId);
        }).Run(force: false, Ignore, cts.Token);

        Assert.True(outcome.Cancelled);
        Assert.Equal(3, outcome.Generated);

        // Not counted as a render failure: the target was interrupted, not rejected.
        Assert.Equal(0, outcome.Failed);
        Assert.All(targets.Take(3), targetId => Assert.NotNull(StoredPath(targetId)));
        Assert.All(targets.Skip(3), targetId => Assert.Null(StoredPath(targetId)));
    }

    [Fact]
    public void Run_Cancelled_ReportsCancelledAndDoesNotThrow()
    {
        AddTargets(4);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var outcome = MakePass().Run(force: false, Ignore, cts.Token);

        // A break, not a throw: the coordinator's own checkpoint between phases records the run
        // as cancelled.
        Assert.True(outcome.Cancelled);
        Assert.Equal(0, outcome.Generated);
        Assert.Empty(_calls);
    }

    [Fact]
    public void Run_Cancelled_NextRunContinuesRatherThanStartingOver()
    {
        var targets = AddTargets(20);
        using var cts = new CancellationTokenSource();
        MakePass((targetId, _) =>
        {
            if (_calls.Count == 15)
            {
                cts.Cancel();
            }

            return RelativePathFor(targetId);
        }).Run(force: false, Ignore, cts.Token);
        _calls.Clear();

        var second = MakePass().Run(force: false, Ignore, CancellationToken.None);

        // The queue is the nullness of the column, so the second run sees only the five the first
        // one did not reach.
        Assert.Equal(5, second.Total);
        Assert.Equal(
            targets.Skip(15).ToList(),
            _calls.Select(call => call.TargetId).ToList());
    }

    // ------------------------------------------------------------------------------ progress

    [Fact]
    public void Run_ReportsProgressEveryFiveTargetsAndOnTheLast()
    {
        AddTargets(12);
        var steps = new List<(int Step, int Total)>();

        MakePass().Run(force: false, (step, total, _) => steps.Add((step, total)), CancellationToken.None);

        Assert.Equal([(5, 12), (10, 12), (12, 12)], steps);
    }

    [Fact]
    public void Run_NoEligibleTargets_ReturnsZeroAndDoesNothing()
    {
        var outcome = MakePass().Run(force: false, (_, _, _) => Assert.Fail("nothing to report"), CancellationToken.None);

        Assert.Equal(new ReferenceThumbnailPass.ReferenceThumbnailOutcome(0, 0, 0, Cancelled: false), outcome);
        Assert.Empty(_calls);
    }

    // ------------------------------------------------------------------- no survey fetch (19.2)

    [Fact]
    public void Run_NeverOpensAnHttpClient()
    {
        // Asserted by construction rather than by behaviour: the type holds nothing that could
        // reach the network and takes nothing that could carry one in. The web application's
        // generate_reference_thumbnails downloads a DSS image from NASA SkyView; spec 19.2 makes
        // that an explicit non-goal, "not deferred, not planned".
        var networkShaped = new[] { typeof(HttpClient), typeof(HttpMessageHandler), typeof(Uri) };

        var fieldTypes = typeof(ReferenceThumbnailPass)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(field => field.FieldType);
        var parameterTypes = typeof(ReferenceThumbnailPass)
            .GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType);

        Assert.All(
            fieldTypes.Concat(parameterTypes),
            type => Assert.DoesNotContain(type, networkShaped));
    }
}
