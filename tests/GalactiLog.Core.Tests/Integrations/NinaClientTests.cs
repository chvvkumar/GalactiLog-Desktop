using System.Globalization;
using GalactiLog.Core.Integrations;
using Xunit;

namespace GalactiLog.Core.Tests.Integrations;

/// <summary>
/// The NINA protocol of design-spec 12.16, task2-clients.md cases 4 to 9 and 15 to 16. Every case
/// runs against <see cref="IntegrationStub"/> and asserts the requests that reached it, because
/// the protocol is a sequence and a dropped or extra leg is exactly what goes wrong.
/// </summary>
public class NinaClientTests
{
    private const string Coordinates = "/v2/api/framing/set-coordinates";
    private const string Rotation = "/v2/api/framing/set-rotation";

    // Case 4. A rotation call on a target that has no angle would move a reader's rotator for a
    // target the catalogue says nothing about.
    [Fact]
    public async Task NoPositionAngleSendsExactlyOneRequestAndReportsNoRotation()
    {
        await using var stub = IntegrationStub.Ok();
        using var http = new HttpClient();

        var result = await new NinaClient(http, TimeSpan.FromSeconds(60))
            .SendCoordinatesAsync(stub.BaseAddress, 10.5, 41.2, null);

        Assert.True(result.Ok);
        Assert.Equal(NinaRotation.None, result.Rotation);
        Assert.Null(result.FailureMessage);
        var request = Assert.Single(stub.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal(Coordinates, request.Path);
    }

    // Case 5. Two requests in order, and the 2 second wait between them: a rotation sent before
    // NINA's sky survey reload finishes is discarded, so a dropped wait is a rotation that silently
    // does nothing on the reader's machine.
    [Fact]
    public async Task PositionAngleSendsTheRotationSecondAfterTheWaitAndReportsItSent()
    {
        await using var stub = IntegrationStub.Ok();
        using var http = new HttpClient();

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var result = await new NinaClient(http, TimeSpan.FromSeconds(60))
            .SendCoordinatesAsync(stub.BaseAddress, 10.5, 41.2, 90.25);
        elapsed.Stop();

        // 1.9, not 2: Task.Delay fires on the system timer tick, which can land a few
        // milliseconds before the Stopwatch reads 2 s (seen once on the CI runner, 1.99 s). A
        // dropped wait reads well under 1 s, so the line still separates the two.
        Assert.True(
            elapsed.Elapsed >= TimeSpan.FromSeconds(1.9),
            "the rotation leg was sent without the 2 second wait of spec 12.16 step 5");
        Assert.True(result.Ok);
        Assert.Equal(NinaRotation.Sent, result.Rotation);
        Assert.Equal(2, stub.Requests.Count);
        Assert.Equal(Coordinates, stub.Requests[0].Path);
        Assert.Equal(Rotation, stub.Requests[1].Path);
        Assert.Equal("rotation=90.25", stub.Requests[1].Query);
    }

    // Case 6. The coordinates landed, so the action succeeded; the reader is told the rotation did
    // not, which is the one NINA behaviour the port declines to copy from the web.
    [Fact]
    public async Task ARotationFailureDoesNotFailTheSendAndLeavesTheMessageNull()
    {
        await using var stub = IntegrationStub.Start((recorded, _) =>
            Task.FromResult(recorded.Path == Rotation ? (500, "") : (200, "")));
        using var http = new HttpClient();

        var result = await new NinaClient(http, TimeSpan.FromSeconds(60))
            .SendCoordinatesAsync(stub.BaseAddress, 10.5, 41.2, 90.0);

        Assert.True(result.Ok);
        Assert.Equal(NinaRotation.Failed, result.Rotation);
        Assert.Null(result.FailureMessage);
        Assert.NotNull(result.Failure);
    }

    // Case 7. The fixed sentence and nothing else: an exception's own text carries the internal
    // address, and a message built from it would put that address on the reader's screen.
    [Fact]
    public async Task ACoordinateFailureReportsTheFixedSentenceAndLeaksNoAddress()
    {
        await using var stub = IntegrationStub.Start((_, _) => Task.FromResult((500, "")));
        using var http = new HttpClient();

        var result = await new NinaClient(http, TimeSpan.FromSeconds(60))
            .SendCoordinatesAsync(stub.BaseAddress, 10.5, 41.2, null);

        Assert.False(result.Ok);
        Assert.Equal(IntegrationMessages.NinaFailed, result.FailureMessage);
        Assert.NotNull(result.Failure);
        Assert.DoesNotContain(stub.BaseAddress, result.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("500", result.FailureMessage, StringComparison.Ordinal);
    }

    // Case 8, the NINA half. The client is the fail-closed second enforcement point of the URL
    // rule: a stored URL that reached the document some other way makes no call at all.
    [Fact]
    public async Task AnUnparseableUrlMakesNoCallAndReportsTheUrlSentence()
    {
        await using var stub = IntegrationStub.Ok();
        using var http = new HttpClient();

        var result = await new NinaClient(http, TimeSpan.FromSeconds(60))
            .SendCoordinatesAsync("ftp://nowhere", 10.5, 41.2, 90.0);

        Assert.False(result.Ok);
        Assert.Equal(IntegrationMessages.BadUrl, result.FailureMessage);
        Assert.Equal(NinaRotation.None, result.Rotation);
        Assert.Empty(stub.Requests);
    }

    // Case 9. A decimal comma would put a second argument separator inside a query value, which is
    // a defect no machine running an English culture ever sees.
    [Fact]
    public async Task NumbersAreInvariantUnderACommaCulture()
    {
        var restore = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            await using var stub = IntegrationStub.Ok();
            using var http = new HttpClient();

            await new NinaClient(http, TimeSpan.FromSeconds(60))
                .SendCoordinatesAsync(stub.BaseAddress, 10.5, 41.2, null);

            Assert.Equal("RAangle=10.5&DecAngle=41.2", Assert.Single(stub.Requests).Query);
        }
        finally
        {
            CultureInfo.CurrentCulture = restore;
        }
    }

    // A 302 from a configured instance would re-send the target's coordinates to whatever host the
    // response names, so the shared handler does not follow one.
    [Fact]
    public async Task ARedirectIsNotFollowedAndReportsTheFixedSentence()
    {
        await using var elsewhere = IntegrationStub.Ok();
        await using var stub = IntegrationStub.Start((_, _) =>
            Task.FromResult((302, elsewhere.BaseAddress + Coordinates)));
        using var http = new HttpClient(IntegrationHttp.NewHandler(), disposeHandler: true);

        var result = await new NinaClient(http, TimeSpan.FromSeconds(60))
            .SendCoordinatesAsync(stub.BaseAddress, 10.5, 41.2, null);

        Assert.False(result.Ok);
        Assert.Equal(IntegrationMessages.NinaFailed, result.FailureMessage);
        Assert.Empty(elsewhere.Requests);
    }

    // Case 15, the NINA half. Setting HttpClient.Timeout would rewrite the catalogue resolver's
    // own 15 second budget for the rest of the process's life.
    [Fact]
    public async Task TheSharedHttpClientTimeoutIsUnchanged()
    {
        await using var stub = IntegrationStub.Ok();
        using var http = new HttpClient();
        var before = http.Timeout;

        await new NinaClient(http, TimeSpan.FromSeconds(60))
            .SendCoordinatesAsync(stub.BaseAddress, 10.5, 41.2, null);

        Assert.Equal(before, http.Timeout);
    }

    // Case 16. Both bounds, on one wedged host: the client's own bound, and the caller's token
    // cutting in sooner. An unbounded wait here freezes the job until the process ends; the bound
    // below is shortened from production's 5 seconds only so this case proves the cutoff without
    // waiting for it.
    [Fact]
    public async Task AWedgedHostIsBoundedAtFiveSecondsAndByTheCallersToken()
    {
        var requestTimeout = TimeSpan.FromMilliseconds(500);
        await using var stub = IntegrationStub.Start(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromMinutes(1), token);
            return (200, "");
        });
        using var http = new HttpClient();
        var client = new NinaClient(http, requestTimeout);

        var ownBound = System.Diagnostics.Stopwatch.StartNew();
        var bounded = await client.SendCoordinatesAsync(
            stub.BaseAddress, 10.5, 41.2, null);
        ownBound.Stop();

        Assert.False(bounded.Ok);
        Assert.Equal(IntegrationMessages.NinaFailed, bounded.FailureMessage);
        // Lower bound carries 50 ms of slack for the same timer-tick reason as the rotation wait
        // case above (CI read 498 ms against a 500 ms timeout). The 15 s ceiling is one quarter of
        // the stub's one minute wedge, so load cannot cross it while an unbounded request still fails.
        Assert.InRange(ownBound.Elapsed, requestTimeout - TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(15));

        // A 5 second bound under a 2.5 second ceiling, so load cannot stretch a genuine 100 ms
        // cancellation into a failure while a client that ignores the token still fails at 5 seconds.
        var slowClient = new NinaClient(http, TimeSpan.FromSeconds(5));
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var callerBound = System.Diagnostics.Stopwatch.StartNew();
        var cancelled = await slowClient.SendCoordinatesAsync(
            stub.BaseAddress, 10.5, 41.2, null, caller.Token);
        callerBound.Stop();

        Assert.False(cancelled.Ok);
        Assert.True(
            callerBound.Elapsed < TimeSpan.FromSeconds(2.5),
            "the caller's token did not cancel the request before the client's own bound");
    }
}
