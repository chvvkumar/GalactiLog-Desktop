using System.Net;
using System.Reflection;
using GalactiLog.Core.Targets;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace GalactiLog.App.Tests.Services;

/// <summary>
/// Phase 10 Task 8, <c>TRACKING.md</c> section 6 item 6: who owns a lifetime in
/// <see cref="AppHost.Build"/>, and what the container disposes when the host goes away.
/// </summary>
/// <remarks>
/// <para>
/// The item's original premise was that eleven instance-registered services had dead
/// <c>Dispose</c> paths. None of the eleven implemented <see cref="IDisposable"/>, so nothing was
/// leaking through them. The genuine undisposed resources were the two
/// <see cref="HttpClientHandler"/> instances the resolution stack built inline and the two
/// <see cref="HttpClient"/> instances wrapped around them, one pair per host. These cases are the
/// regression proof for the fix and, in
/// <see cref="AppHost_RegistersNoInstanceThatImplementsIDisposable"/>, the structural rule that
/// stops the item reopening.
/// </para>
/// <para>
/// FILE SAFETY: every directory this class creates is a temp directory it created and deletes.
/// Nothing in <c>src/**</c> writes, moves or deletes any file outside <c>AppWriter</c>.
/// </para>
/// </remarks>
public sealed class HostDisposalTests
{
    /// <summary>
    /// The <c>httpHandlerOverride</c> stub. It behaves the way a real handler does on disposal:
    /// <c>Dispose</c> latches, and a send afterwards throws
    /// <see cref="ObjectDisposedException"/>. That is what makes "the supplied handler is still
    /// usable after the host is gone" a real assertion rather than a vacuous one against a stub
    /// that ignores its own disposal.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public bool Disposed { get; private set; }

        public int Sends { get; private set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            Sends++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    request.RequestUri!.Host.Contains("simbad", StringComparison.OrdinalIgnoreCase)
                        ? "::error::\nnot found\n"
                        : "<?xml version=\"1.0\"?><Sesame></Sesame>"),
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Send(request, cancellationToken));

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// A host over its own temp app data root, like <c>AppHostTests.AppHostFixture</c>, plus the
    /// registration descriptors <see cref="AppHost.Build"/>'s internal seam hands out.
    /// </summary>
    private sealed class HostFixture : IDisposable
    {
        public string Root { get; }

        public IHost Host { get; }

        public IReadOnlyList<ServiceDescriptor> Descriptors { get; }

        public HostFixture(HttpMessageHandler? handler = null)
        {
            Root = Path.Combine(Path.GetTempPath(), "GalactiLogHostDisposal_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);

            // Serilog's Log.Logger is process-global and Build() overwrites it, so a previous
            // fixture's file sink would otherwise keep its log file open under a root this one
            // is about to delete. The same line AppHostTests' fixture carries, for the same
            // reason; production builds exactly one host per process.
            Serilog.Log.CloseAndFlush();

            IReadOnlyList<ServiceDescriptor> descriptors = [];
            Host = AppHost.Build(
                Root,
                cliMode: false,
                handler,
                services => descriptors = services);
            Descriptors = descriptors;
        }

        public void Dispose()
        {
            Host.Dispose();
            Serilog.Log.CloseAndFlush();
            SqliteConnection.ClearAllPools();
            DeleteRoot();
        }

        /// <summary>
        /// Best effort. <see cref="Host_Dispose_DisposesEveryContainerOwnedDisposable"/> resolves
        /// every registered page view-model, several of which start their own first read on the
        /// pool, and a read still holding a SQLite handle when this runs makes the delete throw.
        /// A leaked temp directory under <c>%TEMP%</c> is not worth turning into a test failure,
        /// and the cases above assert disposal through the objects themselves rather than through
        /// this.
        /// </summary>
        public void DeleteRoot()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    // ---- the resolution stack's HTTP clients -------------------------------------------

    [Fact]
    public void Host_Dispose_DisposesTheResolutionStackHttpClients()
    {
        var handler = new RecordingHandler();
        SimbadClient simbad;
        SesameClient sesame;

        using (var fixture = new HostFixture(handler))
        {
            // Resolving is what runs the factories. Registered as instances, these two were built
            // by Build() itself and the container never owned them.
            simbad = fixture.Host.Services.GetRequiredService<SimbadClient>();
            sesame = fixture.Host.Services.GetRequiredService<SesameClient>();

            // Both work before the host goes away, so the assertions below are about disposal and
            // not about a client that never worked.
            Assert.Null(simbad.QueryObject("M 31"));
            Assert.Null(sesame.Query("M 31"));
            Assert.Equal(2, handler.Sends);
        }

        Assert.Throws<ObjectDisposedException>(() => simbad.QueryObject("M 31"));
        Assert.Throws<ObjectDisposedException>(() => sesame.Query("M 31"));

        // The exception came from the disposed HttpClient, not from the handler: the handler was
        // supplied, so it was not this host's to dispose.
        Assert.False(handler.Disposed);
        Assert.Equal(2, handler.Sends);
    }

    /// <summary>
    /// Coordinator ruling Q25. <c>AppHost.Build</c> passes ONE <c>httpHandlerOverride</c> to BOTH
    /// clients, and <c>ScanVerbTests</c> and <c>ResolveVerbTests</c> keep using their stub after a
    /// host is disposed. A client that disposed a handler it did not create would make the next
    /// request through the other client, or through the next host, throw
    /// <see cref="ObjectDisposedException"/>, and it would present as an unrelated intermittent
    /// failure in a different test class.
    /// </summary>
    [Fact]
    public void Host_Dispose_DoesNotDisposeASuppliedHttpHandlerOverride()
    {
        var handler = new RecordingHandler();

        using (var fixture = new HostFixture(handler))
        {
            fixture.Host.Services.GetRequiredService<SimbadClient>();
            fixture.Host.Services.GetRequiredService<SesameClient>();
        }

        Assert.False(handler.Disposed);

        // Still usable, which is the part that matters: a flag nobody reads would not prove the
        // seam survived. A second host over the same handler is the real shape this protects, and
        // one send through it is the cheapest equivalent.
        using var client = new HttpClient(handler, disposeHandler: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://simbad.cds.unistra.fr/simbad/sim-script");
        using var response = client.Send(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, handler.Sends);
    }

    /// <summary>
    /// The production shape, which no other case here covers: with no override, each client builds
    /// its own handler and owns it. Asserted through the client rather than through the handler,
    /// because a handler this method never sees is exactly the one that used to leak.
    /// </summary>
    [Fact]
    public void Host_Dispose_WithNoHandlerOverride_DisposesTheClientsItBuiltItself()
    {
        SimbadClient simbad;
        SesameClient sesame;

        using (var fixture = new HostFixture())
        {
            simbad = fixture.Host.Services.GetRequiredService<SimbadClient>();
            sesame = fixture.Host.Services.GetRequiredService<SesameClient>();
        }

        // No network call is made: HttpClient checks its own disposal before it reaches a handler.
        Assert.Throws<ObjectDisposedException>(() => simbad.QueryObject("M 31"));
        Assert.Throws<ObjectDisposedException>(() => sesame.Query("M 31"));
    }

    [Fact]
    public void Host_Dispose_IsIdempotent()
    {
        var fixture = new HostFixture(new RecordingHandler());
        try
        {
            var simbad = fixture.Host.Services.GetRequiredService<SimbadClient>();

            fixture.Host.Dispose();
            fixture.Host.Dispose();

            // Task 8 review minor 2. "The second Dispose does not throw" was the whole assertion
            // and it was implicit, so a reader could not tell from the body what was proved. The
            // resolved client is read through the same convention probe the rest of this file
            // uses: disposed once, still disposed, and not re-disposed into a different state.
            Assert.True(DisposalSignal(simbad));
        }
        finally
        {
            Serilog.Log.CloseAndFlush();
            SqliteConnection.ClearAllPools();
            fixture.DeleteRoot();
        }
    }

    // ---- the structural rules ----------------------------------------------------------

    /// <summary>
    /// Types whose disposal this project cannot observe: they carry neither an
    /// <c>IsDisposed</c> member nor the codebase's <c>_disposed</c> field, and their
    /// <c>Dispose</c> only unsubscribes from events and cancels a token, which leaves no readable
    /// trace. The case below still asserts, in the null-signal branch, that no descriptor hands
    /// the container this very object, so it is one the container constructed and therefore
    /// disposes; what is skipped is only the per-object confirmation. The general form of that
    /// rule is <see cref="AppHost_RegistersNoInstanceThatImplementsIDisposable"/>.
    /// </summary>
    /// <remarks>
    /// Task 8 review minor 1: this comment used to claim an assertion that lived in a different
    /// case. The branch now makes it itself.
    /// </remarks>
    private static readonly HashSet<string> NoDisposalSignal =
    [
        // Dispose unsubscribes from ScanCoordinator.ProgressChanged/ScanFinished.
        "GalactiLog.App.Services.ScanStatusService",
        // Dispose stops the watcher and unhooks its settings subscription.
        "GalactiLog.App.Services.WatcherService",
    ];

    /// <summary>
    /// The verification bar's third clause: every <see cref="IDisposable"/> service the container
    /// is responsible for is disposed when the host is disposed. A walk over the registration
    /// descriptors, not a hand-written list, so a service added in a later phase is covered with
    /// no edit here.
    /// </summary>
    [Fact]
    public void Host_Dispose_DisposesEveryContainerOwnedDisposable()
    {
        var fixture = new HostFixture(new RecordingHandler());
        var resolved = new List<object>();
        try
        {
            foreach (var serviceType in OwnServiceTypes(fixture.Descriptors))
            {
                resolved.Add(fixture.Host.Services.GetRequiredService(serviceType));
            }

            var disposables = resolved.OfType<IDisposable>().Distinct().ToList();
            Assert.NotEmpty(disposables);

            // Nothing is disposed while the host is alive, or the assertion after Dispose would
            // pass for the wrong reason.
            foreach (var instance in disposables)
            {
                Assert.False(DisposalSignal(instance) ?? false, $"{instance.GetType()} reports disposed before the host was disposed");
            }

            fixture.Host.Dispose();

            foreach (var instance in disposables)
            {
                var signal = DisposalSignal(instance);
                if (signal is null)
                {
                    Assert.Contains(instance.GetType().FullName ?? instance.GetType().Name, NoDisposalSignal);

                    // Task 8 review minor 1. The skip is only of the per-object confirmation, not
                    // of the guarantee: no descriptor hands the container this object, so the
                    // container constructed it and therefore disposed it.
                    Assert.DoesNotContain(
                        fixture.Descriptors,
                        descriptor => ReferenceEquals(descriptor.ImplementationInstance, instance));
                    continue;
                }

                Assert.True(signal.Value, $"{instance.GetType()} was not disposed with the host");
            }
        }
        finally
        {
            fixture.Host.Dispose();
            Serilog.Log.CloseAndFlush();
            SqliteConnection.ClearAllPools();
            fixture.DeleteRoot();
        }
    }

    /// <summary>
    /// The one instance registration in the container that <c>AppHost.Build</c> does not make and
    /// cannot remove. <c>Host.CreateApplicationBuilder</c>'s logging baseline calls
    /// <c>AddEventSourceLogger</c>, which registers the process-wide
    /// <c>LoggingEventSource.Instance</c> by value. <c>EventSource</c> is
    /// <see cref="IDisposable"/>, its lifetime belongs to the runtime rather than to any one host,
    /// and disposing it with a host would break every other host in the process. Exempt by name
    /// so a GalactiLog registration of a framework disposable would still be caught.
    /// </summary>
    private static readonly HashSet<string> FrameworkInstanceExemptions =
    [
        "Microsoft.Extensions.Logging.EventSource.LoggingEventSource",
    ];

    /// <summary>
    /// The structural form of <c>TRACKING.md</c> section 6 item 6, and the most valuable case in
    /// this file. An instance registration hands the container an object it did not construct, and
    /// the container disposes only what it constructed, so a disposable type registered that way
    /// has a <c>Dispose</c> nothing ever calls. This walks the descriptors and fails the moment
    /// any future registration does it, instead of leaving the rule as a convention the next
    /// implementer has to remember (design-lessons rule 2).
    /// </summary>
    /// <remarks>
    /// <c>AppHost.Build</c>'s own exemption list is empty. Its seven instance registrations are
    /// the services it uses before a container exists, and none of them implements
    /// <see cref="IDisposable"/>; the comment block above them in <c>AppHost.cs</c> is the census
    /// and the record of what adding a <c>Dispose</c> to one of them would require. The one
    /// entry in <see cref="FrameworkInstanceExemptions"/> is not a registration this method makes.
    /// </remarks>
    [Fact]
    public void AppHost_RegistersNoInstanceThatImplementsIDisposable()
    {
        using var fixture = new HostFixture(new RecordingHandler());

        var offenders = fixture.Descriptors
            .Where(descriptor => descriptor.ImplementationInstance is not null)
            .Where(descriptor => !FrameworkInstanceExemptions.Contains(descriptor.ServiceType.FullName ?? ""))
            .Where(descriptor =>
                descriptor.ImplementationInstance is IDisposable or IAsyncDisposable
                || typeof(IDisposable).IsAssignableFrom(descriptor.ServiceType)
                || typeof(IAsyncDisposable).IsAssignableFrom(descriptor.ServiceType))
            .Select(descriptor => $"{descriptor.ServiceType.FullName} -> {descriptor.ImplementationInstance!.GetType().FullName}")
            .ToList();

        Assert.Empty(offenders);

        // The walk sees the registrations it is meant to police: the seven eager services are
        // instance registrations and are still there. Without this the case would pass on an empty
        // or mis-captured descriptor list.
        Assert.Contains(fixture.Descriptors, descriptor => descriptor.ImplementationInstance is not null);
    }

    // ---- helpers -----------------------------------------------------------------------

    /// <summary>
    /// The registered service types this solution owns: closed, non-delegate, and declared in a
    /// GalactiLog assembly. Framework registrations are excluded because their lifetimes are the
    /// host's contract, not this method's.
    /// </summary>
    private static IEnumerable<Type> OwnServiceTypes(IEnumerable<ServiceDescriptor> descriptors)
        => descriptors
            .Select(descriptor => descriptor.ServiceType)
            .Where(type => type.Assembly.GetName().Name?.StartsWith("GalactiLog", StringComparison.Ordinal) == true)
            .Where(type => !type.IsGenericTypeDefinition && !type.ContainsGenericParameters)
            .Where(type => !typeof(Delegate).IsAssignableFrom(type))
            .Distinct();

    /// <summary>
    /// Reads an object's disposal state through the convention this codebase uses everywhere: an
    /// <c>IsDisposed</c> member, or a private <c>bool _disposed</c> field. Null when the type
    /// carries neither, which is what <see cref="NoDisposalSignal"/> covers. A convention probe
    /// rather than a per-type list, so a disposable added in a later phase that follows the
    /// convention is asserted with no edit here.
    /// </summary>
    private static bool? DisposalSignal(object instance)
    {
        const BindingFlags Flags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        for (var type = instance.GetType(); type is not null && type != typeof(object); type = type.BaseType)
        {
            var property = type.GetProperty("IsDisposed", Flags);
            if (property is { PropertyType.FullName: "System.Boolean" } && property.GetMethod is { } getter)
            {
                return (bool)getter.Invoke(instance, null)!;
            }

            var field = type.GetField("_disposed", Flags);
            if (field is { FieldType.FullName: "System.Boolean" })
            {
                return (bool)field.GetValue(instance)!;
            }
        }

        return null;
    }
}
