using System.Net;
using GalactiLog.Core.Targets;
using Xunit;

namespace GalactiLog.Core.Tests.Targets;

public class SimbadClientTests
{
    // Marker style matches the real fixture in backend/tests/test_simbad.py
    // ("::data::\nNGC  7000|HII|314.75|44.37\n"): the marker is bare on its own line, with no
    // metadata token glued to it. A metadata token glued onto the marker line (e.g.
    // "::data::object:1") would itself survive the tilde/colon-only filter below and be
    // mistaken for the data row -- that shape does not occur in real SIMBAD output.
    [Fact]
    public void ParseObjectResponse_ParsesWellFormedResult()
    {
        var result = SimbadClient.ParseObjectResponse("::data::\nM 31|G,LIN|10.6847|41.269\n");

        Assert.NotNull(result);
        Assert.Equal("M 31", result!.MainId);
        Assert.Equal("G,LIN", result.ObjectType);
        Assert.Equal(10.6847, result.Ra);
        Assert.Equal(41.269, result.Dec);
    }

    [Fact]
    public void ParseObjectResponse_ErrorMarkerReturnsNull()
    {
        var result = SimbadClient.ParseObjectResponse("::error::\nno object found\n");

        Assert.Null(result);
    }

    [Fact]
    public void ParseObjectResponse_FewerThanFourFieldsReturnsNull()
    {
        var result = SimbadClient.ParseObjectResponse("::data::\nM 31|G,LIN\n");

        Assert.Null(result);
    }

    [Fact]
    public void ParseObjectResponse_SkipsTildeAndColonOnlyLines()
    {
        var body = "::data::\n~ a comment line\n:\nM 31|G,LIN|10.6847|41.269\n";

        var result = SimbadClient.ParseObjectResponse(body);

        Assert.NotNull(result);
        Assert.Equal("M 31", result!.MainId);
    }

    [Fact]
    public void ParseObjectResponse_BlankCoordinateFieldsYieldNull()
    {
        var result = SimbadClient.ParseObjectResponse("::data::\nM 31|G||41.269\n");

        Assert.NotNull(result);
        Assert.Null(result!.Ra);
        Assert.Equal(41.269, result.Dec);
    }

    [Fact]
    public void EscapeAdql_DoublesSingleQuotes()
    {
        Assert.Equal("O''Brien''s Nebula", SimbadClient.EscapeAdql("O'Brien's Nebula"));
    }

    [Fact]
    public void QueryAliases_MainIdWithSingleQuote_ProducesFullyEscapedQuery()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, "id\n");
        var client = new SimbadClient(handler);

        client.QueryAliases("O'Brien's Nebula");

        var requestUri = handler.LastRequest!.RequestUri!.ToString();
        var rawQueryParam = requestUri[(requestUri.IndexOf("query=", StringComparison.Ordinal) + "query=".Length)..];
        var decodedQuery = Uri.UnescapeDataString(rawQueryParam);

        Assert.Contains("main_id = 'O''Brien''s Nebula'", decodedQuery);
        // Only the doubled pairs (4 quote chars) plus the two literal delimiter quotes.
        Assert.Equal(6, decodedQuery.Count(c => c == '\''));
    }

    [Fact]
    public void ParseAliasResponse_SkipsHeaderAndStripsQuotes()
    {
        var tsv = "id\n\"NAME Andromeda Galaxy\"\n\"M 31\"\n";

        var aliases = SimbadClient.ParseAliasResponse(tsv);

        Assert.Equal(["NAME Andromeda Galaxy", "M 31"], aliases);
    }

    [Fact]
    public void ParseAliasResponse_HeaderOnlyYieldsEmpty()
    {
        var aliases = SimbadClient.ParseAliasResponse("id\n");

        Assert.Empty(aliases);
    }

    [Fact]
    public void QueryObject_NonTransientStatus_ThrowsNonTransientCatalogException()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.BadRequest, "");
        var client = new SimbadClient(handler);

        Assert.Throws<NonTransientCatalogException>(() => client.QueryObject("M 31"));
    }

    [Fact]
    public void QueryObject_TooManyRequestsStatus_DoesNotThrowNonTransient()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.TooManyRequests, "");
        var client = new SimbadClient(handler);

        var ex = Record.Exception(() => client.QueryObject("M 31"));

        Assert.NotNull(ex);
        Assert.IsNotType<NonTransientCatalogException>(ex);
        Assert.IsType<HttpRequestException>(ex);
    }

    [Fact]
    public void QueryObject_EmptyNameAfterSanitization_ReturnsNullWithoutRequest()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, "::error::\n");
        var client = new SimbadClient(handler);

        var result = client.QueryObject("\u0001\u0002\u0003");

        Assert.Null(result);
        Assert.False(handler.WasInvoked);
    }

    [Fact]
    public void QueryObject_DeclaredContentLengthOverCap_ThrowsNonTransientCatalogException()
    {
        var handler = new FakeHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("small body") };
            response.Content.Headers.ContentLength = 6 * 1024 * 1024;
            return response;
        });
        var client = new SimbadClient(handler);

        Assert.Throws<NonTransientCatalogException>(() => client.QueryObject("M 31"));
    }

    [Fact]
    public void QueryObject_UnboundedStreamWithoutContentLength_ThrowsNonTransientCatalogException()
    {
        // No Content-Length header at all (chunked-transfer shape); the real cap has to come
        // from reading the stream itself, not from trusting an attacker-supplied header.
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnboundedContent(50_000_000),
        });
        var client = new SimbadClient(handler);

        Assert.Throws<NonTransientCatalogException>(() => client.QueryObject("M 31"));
    }

    [Fact]
    public void QueryObject_NotFoundStatus_ThrowsNonTransientCatalogException()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.NotFound, "");
        var client = new SimbadClient(handler);

        Assert.Throws<NonTransientCatalogException>(() => client.QueryObject("M 31"));
    }

    [Fact]
    public void QueryObject_ServiceUnavailableStatus_ThrowsHttpRequestException()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.ServiceUnavailable, "");
        var client = new SimbadClient(handler);

        var ex = Record.Exception(() => client.QueryObject("M 31"));

        Assert.IsType<HttpRequestException>(ex);
    }

    [Fact]
    public void QueryObject_HandlerTimesOut_ThrowsTaskCanceledException()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new TaskCanceledException("simulated timeout"));
        var client = new SimbadClient(handler);

        Assert.Throws<TaskCanceledException>(() => client.QueryObject("M 31"));
    }

    [Fact]
    public void QueryObject_HandlerRefusesConnection_ThrowsHttpRequestException()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("simulated connection refused"));
        var client = new SimbadClient(handler);

        Assert.Throws<HttpRequestException>(() => client.QueryObject("M 31"));
    }
}
