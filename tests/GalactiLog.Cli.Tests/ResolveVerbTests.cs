using System.Net;
using System.Text.Json;
using GalactiLog.App;
using GalactiLog.Cli;
using GalactiLog.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace GalactiLog.Cli.Tests;

// Task 8: exercises the real `resolve` verb end to end through AppHost.Build + CliDispatcher
// (spec 15). Every test that reaches TargetResolver's online path substitutes a stub
// HttpMessageHandler via AppHost.Build's httpHandlerOverride -- no test in this file touches
// the real network. GalactiLog.Core.Tests' FakeHttpMessageHandler is internal to that
// assembly (Task 5), so a small local equivalent is used here instead, matching Task 7's own
// precedent (task7-report.md) rather than widening that file's visibility.
public sealed class ResolveVerbTests
{
    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Send(request, cancellationToken));
    }

    private static HttpResponseMessage TextResponse(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    // A handler shaped like a real SIMBAD "found" + SESAME "not consulted" pair, used by the
    // never-creates-a-target and identity-match tests. Routes by URL shape rather than host,
    // matching SimbadClient's own two distinct endpoints.
    private static StubHttpMessageHandler SimbadHitHandler(string mainId) => new(req =>
    {
        var path = req.RequestUri!.AbsolutePath;
        if (path.Contains("sim-script", StringComparison.Ordinal))
        {
            return TextResponse($"::data::\n{mainId}|G|10.6847|41.269\n");
        }
        if (path.Contains("sim-tap", StringComparison.Ordinal))
        {
            return TextResponse($"id\n\"{mainId}\"\n");
        }
        throw new InvalidOperationException($"unexpected request in test stub: {req.RequestUri}");
    });

    // Both SIMBAD and SESAME report a clean "queried, no match" (spec 9.2 step 6), the shape
    // task7-report.md documents: SIMBAD's marker is "::error::"; SESAME's is a Resolver-less
    // XML body. Never a literal HTTP 404 -- that is a genuine client error TargetResolver
    // re-throws rather than treats as "no match" (see task7-report.md Deviation 3).
    private static readonly StubHttpMessageHandler NoMatchHandler = new(req =>
        TextResponse(req.RequestUri!.Host.Contains("simbad", StringComparison.OrdinalIgnoreCase)
            ? "::error::\nnot found\n"
            : "<?xml version=\"1.0\"?><Sesame></Sesame>"));

    private static readonly StubHttpMessageHandler NeverInvokedHandler =
        new(_ => throw new InvalidOperationException("network must not be touched for an offline hit."));

    private static readonly StubHttpMessageHandler NetworkFailureHandler =
        new(_ => throw new HttpRequestException("simulated: network unreachable"));

    private sealed class HostFixture : IDisposable
    {
        public string Root { get; }
        public IHost Host { get; }

        public HostFixture(HttpMessageHandler handler)
        {
            Root = Directory.CreateTempSubdirectory("galactilog-resolve-verb-tests-").FullName;
            // No real retry backoff: the 1 s then 2 s schedule is CatalogCacheRepositoryTests'
            // subject, and a network failure here should cost three attempts, not three seconds.
            Host = AppHost.Build(Root, cliMode: true, httpHandlerOverride: handler, retryWaitOverride: (_, _) => { });
        }

        public string ConnectionString => DatabasePaths.BuildConnectionString(
            Path.Combine(Path.GetFullPath(Root), DatabasePaths.DatabaseFileName));

        public void Dispose()
        {
            Host.Dispose();
            Serilog.Log.CloseAndFlush();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private static (int ExitCode, string Out, string Err) Run(IHost host, params string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var outWriter = new StringWriter();
        var errorWriter = new StringWriter();
        Console.SetOut(outWriter);
        Console.SetError(errorWriter);
        try
        {
            CliDispatcher.TryRun(args, () => host.Services, out var exitCode);
            return (exitCode, outWriter.ToString(), errorWriter.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    // RunResolve's own usage check (args.Length != 1) returns before touching `services`, so
    // these two don't need a real host -- a NullServiceProvider factory is enough, exactly
    // like CliDispatcherTests' existing pattern for the other verbs' usage checks.
    private static (int ExitCode, string Out, string Err) RunWithNoHost(params string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var outWriter = new StringWriter();
        var errorWriter = new StringWriter();
        Console.SetOut(outWriter);
        Console.SetError(errorWriter);
        try
        {
            CliDispatcher.TryRun(args, () => new NullServiceProvider(), out var exitCode);
            return (exitCode, outWriter.ToString(), errorWriter.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [Fact]
    public void Resolve_OfflineName_ExitsZeroWithOfflineSource_AndNeverTouchesNetwork()
    {
        using var fixture = new HostFixture(NeverInvokedHandler);

        var (exitCode, stdout, _) = Run(fixture.Host, "resolve", "M 31");

        Assert.Equal(0, exitCode);
        Assert.Contains("source = offline", stdout);
    }

    [Fact]
    public void Resolve_JsonFlag_EmitsParsableObjectWithSourceField()
    {
        using var fixture = new HostFixture(NeverInvokedHandler);

        var (exitCode, stdout, _) = Run(fixture.Host, "resolve", "M 31", "--json");

        Assert.Equal(0, exitCode);
        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal("offline", doc.RootElement.GetProperty("source").GetString());
        Assert.True(doc.RootElement.TryGetProperty("primary_name", out _));
    }

    [Fact]
    public void Resolve_UnknownName_ExitsOneWithUnresolvedSource()
    {
        using var fixture = new HostFixture(NoMatchHandler);

        var (exitCode, stdout, _) = Run(fixture.Host, "resolve", "zzzz not a real object", "--json");

        Assert.Equal(1, exitCode);
        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal("unresolved", doc.RootElement.GetProperty("source").GetString());
    }

    // Phase 7 Task 7's sixth ResolutionStage. Without its arm in RunResolve's switch the
    // default would report a resolved name as "unresolved".
    [Fact]
    public void Resolve_SolarSystemName_ReportsTheSolarSystemSource()
    {
        using var fixture = new HostFixture(NoMatchHandler);

        var (exitCode, stdout, _) = Run(fixture.Host, "resolve", "Jupiter", "--json");

        Assert.Equal(0, exitCode);
        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal("solar_system", doc.RootElement.GetProperty("source").GetString());
        Assert.Equal("Jupiter", doc.RootElement.GetProperty("primary_name").GetString());

        // Spec 15: the verb is read-only with respect to targets, solar-system names included.
        using var connection = new SqliteConnection(fixture.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM targets";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    // Review fix: a miss is a result, not progress -- --quiet suppresses progress output only
    // (spec 15), so the "unresolved" line still prints, unconditionally, on stdout (matching
    // the --json branch), never on stderr.
    [Fact]
    public void Resolve_UnknownName_WithQuiet_StillPrintsUnresolvedLineOnStdout_StderrEmpty()
    {
        using var fixture = new HostFixture(NoMatchHandler);

        var (exitCode, stdout, stderr) = Run(fixture.Host, "resolve", "zzzz not a real object", "--quiet");

        Assert.Equal(1, exitCode);
        Assert.Contains("unresolved: zzzz not a real object", stdout);
        Assert.Empty(stderr);
    }

    [Fact]
    public void Resolve_NeverCreatesATarget()
    {
        using var fixture = new HostFixture(SimbadHitHandler("NGC 224"));

        // A fictitious name verified (task7-report.md) to have no offline hit, so this drives
        // the online path all the way to step 8's create decision -- exactly the case that
        // would insert a row under normal (createIfMissing: true) resolution.
        var (exitCode, _, _) = Run(fixture.Host, "resolve", "Zzyzx Nonexistent Blob 42");

        Assert.Equal(0, exitCode);

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(fixture.ConnectionString));
        Assert.Empty(context.Targets);
    }

    [Fact]
    public void Resolve_NameMatchingExistingTargetsIdentity_LeavesItsAliasesUnchanged()
    {
        using var fixture = new HostFixture(SimbadHitHandler("NGC 224"));

        // Seed a target whose catalog id matches what the stub SIMBAD hit above will resolve
        // to, with a fixed alias set. The resolve verb (dryRun: true) must find and report
        // this target (stage "simbad") without appending the incoming name as a new alias or
        // writing anything to `targets` -- TargetResolver's review-fix item 4.
        Guid targetId;
        string aliasesBefore;
        using (var seed = new GalactiLogContext(GalactiLogContextOptions.Create(fixture.ConnectionString, tracking: true)))
        {
            var target = new GalactiLog.Data.Entities.Target
            {
                Id = Guid.NewGuid(),
                PrimaryName = "NGC 224",
                CatalogId = "NGC 224",
                CatalogIdNormalized = "NGC 224",
                Aliases = "[\"NGC 224\"]",
            };
            seed.Targets.Add(target);
            seed.SaveChanges();
            targetId = target.Id;
            aliasesBefore = target.Aliases;
        }

        var (exitCode, stdout, _) = Run(fixture.Host, "resolve", "Zzyzx Nonexistent Blob 42");

        Assert.Equal(0, exitCode);
        Assert.Contains("source = simbad", stdout);

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(fixture.ConnectionString));
        var reloaded = Assert.Single(context.Targets);
        Assert.Equal(targetId, reloaded.Id);
        Assert.Equal(aliasesBefore, reloaded.Aliases);
    }

    [Fact]
    public void Resolve_NetworkFailure_ExitsOneWithinTimeBound()
    {
        using var fixture = new HostFixture(NetworkFailureHandler);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var (exitCode, _, _) = Run(fixture.Host, "resolve", "Horsehead Nebula");
        stopwatch.Stop();

        Assert.Equal(1, exitCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20),
            $"resolve took {stopwatch.Elapsed}, expected well under the 20s bar (design-spec 15 / roadmap verification).");
    }

    [Fact]
    public void Resolve_NoNameArgument_ExitsTwoWithUsageOnStderr()
    {
        var (exitCode, stdout, stderr) = RunWithNoHost("resolve");

        Assert.Equal(2, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("Usage: galactilog resolve <name>", stderr);
    }

    [Fact]
    public void Resolve_TooManyArguments_ExitsTwoWithUsageOnStderr()
    {
        var (exitCode, stdout, stderr) = RunWithNoHost("resolve", "a", "b");

        Assert.Equal(2, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("Usage: galactilog resolve <name>", stderr);
    }
}
