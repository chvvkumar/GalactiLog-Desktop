using GalactiLog.Core.Integrations;
using Xunit;

namespace GalactiLog.Core.Tests.Integrations;

/// <summary>
/// The Stellarium protocol of design-spec 12.16, task2-clients.md cases 8 and 10 to 15. The
/// candidate list, the encoding and the field of view leg are each a whole route, so a case here
/// fails as a slew that pointed at the wrong thing or reported a wrong view as success.
/// </summary>
public class StellariumClientTests
{
    private const string Focus = "/api/main/focus";
    private const string Script = "/api/scripts/direct";
    private const string Fov = "/api/main/fov";

    private static string FieldOf(string body, string field)
    {
        var fields = body.Split('&');
        var one = Assert.Single(fields, f => f.StartsWith(field + "=", StringComparison.Ordinal));
        return Uri.UnescapeDataString(one[(field.Length + 1)..]);
    }

    // Case 10. "M 31" matches the catalogue prefix over its whole length, so ruling B3 gives one
    // candidate; a second attempt after a success would move the view off the target again.
    [Fact]
    public async Task AWholeNameCatalogueMatchFocusesOnceAndReportsCatalogName()
    {
        await using var stub = IntegrationStub.Ok("true");
        using var http = new HttpClient();

        var result = await new StellariumClient(http, TimeSpan.FromSeconds(60)).SlewAsync(stub.BaseAddress, 10.5, 41.2, "M 31");

        Assert.True(result.Ok);
        Assert.Equal(StellariumFocus.CatalogName, result.Focus);
        Assert.Equal(2, stub.Requests.Count);
        Assert.Equal("POST", stub.Requests[0].Method);
        Assert.Equal(Focus, stub.Requests[0].Path);
        Assert.Equal("target=M%2031", stub.Requests[0].Body);
        Assert.Equal(Fov, stub.Requests[1].Path);
    }

    // Case 11. The web interpolates the raw name, so this body reaches Stellarium as two fields
    // and the focus silently addresses something else.
    [Fact]
    public async Task AnAmpersandInTheNameIsPercentEncodedIntoOneField()
    {
        await using var stub = IntegrationStub.Ok("false");
        using var http = new HttpClient();

        await new StellariumClient(http, TimeSpan.FromSeconds(60)).SlewAsync(stub.BaseAddress, 10.5, 41.2, "Sh2-101 & friends");

        var attempts = stub.Requests.Where(r => r.Path == Focus).ToList();
        Assert.Equal(2, attempts.Count);
        Assert.Equal("Sh2-101", FieldOf(attempts[0].Body, "target"));
        Assert.Equal("Sh2-101 & friends", FieldOf(attempts[1].Body, "target"));
        Assert.DoesNotContain("&", attempts[1].Body, StringComparison.Ordinal);
    }

    // Case 12. Both candidates answer false, so the coordinate script runs; six decimal places and
    // the literal shape are what Stellarium's own parser reads.
    [Fact]
    public async Task ExhaustedCandidatesFallThroughToTheCoordinateScript()
    {
        await using var stub = IntegrationStub.Ok("false");
        using var http = new HttpClient();

        var result = await new StellariumClient(http, TimeSpan.FromSeconds(60))
            .SlewAsync(stub.BaseAddress, 10.5, 41.2, "Sh2-101 nebula");

        Assert.True(result.Ok);
        Assert.Equal(StellariumFocus.Coordinates, result.Focus);
        var script = Assert.Single(stub.Requests, r => r.Path == Script);
        Assert.Equal(
            "core.moveToRaDecJ2000(\"10.500000d\", \"41.200000d\", 3);",
            FieldOf(script.Body, "code"));
    }

    // Case 13. No catalogue prefix means one candidate, not a duplicate of the same name.
    [Fact]
    public async Task ANameWithNoCataloguePrefixFocusesOnceAndReportsFullName()
    {
        await using var stub = IntegrationStub.Ok("true");
        using var http = new HttpClient();

        var result = await new StellariumClient(http, TimeSpan.FromSeconds(60))
            .SlewAsync(stub.BaseAddress, 10.5, 41.2, "  Andromeda Galaxy  ");

        Assert.True(result.Ok);
        Assert.Equal(StellariumFocus.FullName, result.Focus);
        var attempt = Assert.Single(stub.Requests, r => r.Path == Focus);
        Assert.Equal("Andromeda Galaxy", FieldOf(attempt.Body, "target"));
    }

    // Case 14, first half. The field of view is set on both routes: a slew that left the view at
    // the wrong scale did not do what the menu item says.
    [Theory]
    [InlineData("true", StellariumFocus.CatalogName)]
    [InlineData("false", StellariumFocus.Coordinates)]
    public async Task TheFieldOfViewIsSetOnBothRoutes(string focusBody, StellariumFocus expected)
    {
        await using var stub = IntegrationStub.Ok(focusBody);
        using var http = new HttpClient();

        var result = await new StellariumClient(http, TimeSpan.FromSeconds(60)).SlewAsync(stub.BaseAddress, 10.5, 41.2, "M 31");

        Assert.True(result.Ok);
        Assert.Equal(expected, result.Focus);
        var fov = Assert.Single(stub.Requests, r => r.Path == Fov);
        Assert.Equal("POST", fov.Method);
        Assert.Equal("fov=20", fov.Body);
    }

    // Case 14, second half.
    [Fact]
    public async Task AFieldOfViewFailureFailsTheSlew()
    {
        await using var stub = IntegrationStub.Start((recorded, _) =>
            Task.FromResult(recorded.Path == Fov ? (500, "") : (200, "true")));
        using var http = new HttpClient();

        var result = await new StellariumClient(http, TimeSpan.FromSeconds(60)).SlewAsync(stub.BaseAddress, 10.5, 41.2, "M 31");

        Assert.False(result.Ok);
        Assert.Equal(IntegrationMessages.StellariumFailed, result.FailureMessage);
        Assert.NotNull(result.Failure);
        Assert.DoesNotContain(stub.BaseAddress, result.FailureMessage, StringComparison.Ordinal);
    }

    // Case 8, the Stellarium half.
    [Fact]
    public async Task AnUnparseableUrlMakesNoCallAndReportsTheUrlSentence()
    {
        await using var stub = IntegrationStub.Ok("true");
        using var http = new HttpClient();

        var result = await new StellariumClient(http, TimeSpan.FromSeconds(60)).SlewAsync("/relative", 10.5, 41.2, "M 31");

        Assert.False(result.Ok);
        Assert.Equal(IntegrationMessages.BadUrl, result.FailureMessage);
        Assert.Empty(stub.Requests);
    }

    // A 302 from a configured instance would re-send the target's coordinates to whatever host the
    // response names, so the shared handler does not follow one.
    [Fact]
    public async Task ARedirectIsNotFollowedAndReportsTheFixedSentence()
    {
        await using var elsewhere = IntegrationStub.Ok("true");
        await using var stub = IntegrationStub.Start((recorded, _) =>
            Task.FromResult((302, elsewhere.BaseAddress + recorded.Path)));
        using var http = new HttpClient(IntegrationHttp.NewHandler(), disposeHandler: true);

        var result = await new StellariumClient(http, TimeSpan.FromSeconds(60)).SlewAsync(stub.BaseAddress, 10.5, 41.2, null);

        Assert.False(result.Ok);
        Assert.Equal(IntegrationMessages.StellariumFailed, result.FailureMessage);
        Assert.Empty(elsewhere.Requests);
    }

    // Case 15, the Stellarium half.
    [Fact]
    public async Task TheSharedHttpClientTimeoutIsUnchanged()
    {
        await using var stub = IntegrationStub.Ok("true");
        using var http = new HttpClient();
        var before = http.Timeout;

        await new StellariumClient(http, TimeSpan.FromSeconds(60)).SlewAsync(stub.BaseAddress, 10.5, 41.2, "M 31");

        Assert.Equal(before, http.Timeout);
    }

    // A blank name skips focus by name entirely rather than posting an empty target (spec 12.16
    // step 2, "only when a target name is known").
    [Fact]
    public async Task ABlankNameSkipsFocusByName()
    {
        await using var stub = IntegrationStub.Ok("true");
        using var http = new HttpClient();

        var result = await new StellariumClient(http, TimeSpan.FromSeconds(60)).SlewAsync(stub.BaseAddress, 10.5, 41.2, "   ");

        Assert.True(result.Ok);
        Assert.Equal(StellariumFocus.Coordinates, result.Focus);
        Assert.DoesNotContain(stub.Requests, r => r.Path == Focus);
    }
}
