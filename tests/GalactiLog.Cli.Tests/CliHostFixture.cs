using System.Net;

namespace GalactiLog.Cli.Tests;

// Phase 10 Task 8, coordinator ruling Q23. ScanReferenceThumbnailTests is the second file in this
// project that needs BOTH the console-capture Run helper and an offline "queried, no match" HTTP
// stub, and copying them would have been 35 duplicated lines (NoMatchHandler 14, the two Run
// overloads 21). The ruling's threshold is about twenty, so the spine is extracted here and
// ScanVerbTests moves onto it instead of growing a second copy (design-lessons rule 1).
//
// Deliberately NOT a shared host fixture, although the ruling names the file for one: the two
// classes' fixtures differ in what they need (frame count and scan-root configuration against
// renderable frames and a relocatable thumbnail cache root) and nothing is duplicated between
// them. ResolveVerbTests and InspectAndDumpHeadersTests keep their own copies of Run: they belong
// to earlier phases with settled reviews and this task has no reason to open them.
//
// Used through `using static GalactiLog.Cli.Tests.CliHostFixture;`, so no call site spells the
// class name and ScanVerbTests' twelve Run(...) calls are unchanged.
internal static class CliHostFixture
{
    // Captures the process-global Console.Out/Console.Error around one CliDispatcher run and
    // restores them, so a verb's stdout contract (spec 15) is assertable. AssemblyInfo.cs disables
    // cross-collection parallelization for this project precisely because this is process-global.
    public static (int ExitCode, string Out, string Err) Run(Func<IServiceProvider> services, params string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var outWriter = new StringWriter();
        var errorWriter = new StringWriter();
        Console.SetOut(outWriter);
        Console.SetError(errorWriter);
        try
        {
            CliDispatcher.TryRun(args, services, out var exitCode);
            return (exitCode, outWriter.ToString(), errorWriter.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    public static (int ExitCode, string Out, string Err) Run(Microsoft.Extensions.Hosting.IHost host, params string[] args)
        => Run(() => host.Services, args);
}

// Both catalog services report a clean "queried, no match" rather than an error: every OBJECT in
// these fixtures resolves offline, so nothing should reach this at all, but a miss must not look
// like a network failure if anything ever does.
//
// One instance is handed to BOTH SimbadClient and SesameClient through AppHost.Build's
// httpHandlerOverride, and neither disposes it: ownsHandler is false for a supplied handler
// (coordinator ruling Q25), which is what lets a fixture reuse one across two clients and across
// hosts.
internal sealed class NoMatchHandler : HttpMessageHandler
{
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                request.RequestUri!.Host.Contains("simbad", StringComparison.OrdinalIgnoreCase)
                    ? "::error::\nnot found\n"
                    : "<?xml version=\"1.0\"?><Sesame></Sesame>"),
        };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(Send(request, cancellationToken));
}
