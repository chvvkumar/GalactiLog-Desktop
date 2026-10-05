using System.Net;
using GalactiLog.Core.Targets;
using Xunit;

namespace GalactiLog.Core.Tests.Targets;

public class SesameClientTests
{
    [Fact]
    public void ParseResponse_ParsesFirstResolverWithBothCoordinates()
    {
        const string xml = """
            <Sesame>
              <Target name="ngc 7000">
                <Resolver name="S=Simbad">
                  <oname>Incomplete</oname>
                  <jradeg>1.0</jradeg>
                </Resolver>
                <Resolver name="N=NED">
                  <oname>NGC 7000</oname>
                  <otype>HII</otype>
                  <jradeg>314.75</jradeg>
                  <jdedeg>44.37</jdedeg>
                </Resolver>
              </Target>
            </Sesame>
            """;

        var result = SesameClient.ParseResponse(xml, "ngc 7000");

        Assert.NotNull(result);
        Assert.Equal("NGC 7000", result!.MainId);
        Assert.Equal(314.75, result.Ra);
        Assert.Equal(44.37, result.Dec);
        Assert.Equal("HII", result.ObjectType);
        Assert.Equal("N=NED", result.ResolverName);
    }

    [Fact]
    public void ParseResponse_FallsBackToQueryNameWhenOnameMissing()
    {
        const string xml = """
            <Sesame>
              <Resolver name="V=VizieR">
                <jradeg>10.5</jradeg>
                <jdedeg>20.5</jdedeg>
              </Resolver>
            </Sesame>
            """;

        var result = SesameClient.ParseResponse(xml, "my query name");

        Assert.NotNull(result);
        Assert.Equal("my query name", result!.MainId);
    }

    [Fact]
    public void ParseResponse_CollectsAliasElements()
    {
        const string xml = """
            <Sesame>
              <Resolver name="N=NED">
                <jradeg>1.0</jradeg>
                <jdedeg>2.0</jdedeg>
                <alias>Alias One</alias>
                <alias>Alias Two</alias>
              </Resolver>
            </Sesame>
            """;

        var result = SesameClient.ParseResponse(xml, "query");

        Assert.NotNull(result);
        Assert.Equal(["Alias One", "Alias Two"], result!.Aliases);
    }

    [Fact]
    public void ParseResponse_NoResolverElements_ReturnsNull()
    {
        const string xml = "<Sesame><Target name=\"x\"></Target></Sesame>";

        var result = SesameClient.ParseResponse(xml, "query");

        Assert.Null(result);
    }

    [Fact]
    public void ParseResponse_MalformedXml_ReturnsNull()
    {
        const string xml = "<Sesame><Resolver name=\"N=NED\"><jradeg>1.0</jradeg>";

        var result = SesameClient.ParseResponse(xml, "query");

        Assert.Null(result);
    }

    [Fact]
    public void ParseResponse_EmptyOnameFallsBackToQueryName()
    {
        // Python's `findtext("oname") or object_name` also falls back on an empty element,
        // not just a missing one.
        const string xml = """
            <Sesame>
              <Resolver name="N=NED">
                <oname></oname>
                <jradeg>1.0</jradeg>
                <jdedeg>2.0</jdedeg>
              </Resolver>
            </Sesame>
            """;

        var result = SesameClient.ParseResponse(xml, "my query name");

        Assert.NotNull(result);
        Assert.Equal("my query name", result!.MainId);
    }

    [Fact]
    public void ParseResponse_DoctypeIsRejectedAsMalformed()
    {
        const string xml = """
            <?xml version="1.0"?>
            <!DOCTYPE Sesame [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <Sesame><Resolver name="N=NED"><jradeg>1.0</jradeg><jdedeg>2.0</jdedeg></Resolver></Sesame>
            """;

        var result = SesameClient.ParseResponse(xml, "query");

        Assert.Null(result);
    }

    [Fact]
    public void Query_ReplacesSpacesWithPlus()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, "<Sesame></Sesame>");
        var client = new SesameClient(handler);

        client.Query("North America Nebula");

        var requestUri = handler.LastRequest!.RequestUri!.ToString();
        Assert.EndsWith("/-ox/NV?North+America+Nebula", requestUri);
    }

    [Fact]
    public void Query_EscapesReservedCharactersInObjectName()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, "<Sesame></Sesame>");
        var client = new SesameClient(handler);

        client.Query("M31 #1 & Co");

        var requestUri = handler.LastRequest!.RequestUri!.ToString();
        var afterMarker = requestUri[(requestUri.IndexOf("/-ox/NV?", StringComparison.Ordinal) + "/-ox/NV?".Length)..];
        // Spaces become '+'; '#' and '&' stay percent-encoded so they cannot be mistaken for
        // a URL fragment or an extra query separator inside what is really the object name.
        Assert.DoesNotContain(' ', afterMarker);
        Assert.DoesNotContain('#', afterMarker);
        Assert.Equal("M31+%231+%26+Co", afterMarker);
    }

    [Fact]
    public void Query_NonTransientStatus_ThrowsNonTransientCatalogException()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.NotFound, "");
        var client = new SesameClient(handler);

        Assert.Throws<NonTransientCatalogException>(() => client.Query("M 31"));
    }

    [Fact]
    public void Query_BadRequestStatus_ThrowsNonTransientCatalogException()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.BadRequest, "");
        var client = new SesameClient(handler);

        Assert.Throws<NonTransientCatalogException>(() => client.Query("M 31"));
    }

    [Fact]
    public void Query_ServiceUnavailableStatus_ThrowsHttpRequestException()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.ServiceUnavailable, "");
        var client = new SesameClient(handler);

        var ex = Record.Exception(() => client.Query("M 31"));

        Assert.IsType<HttpRequestException>(ex);
    }

    [Fact]
    public void Query_HandlerTimesOut_ThrowsTaskCanceledException()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new TaskCanceledException("simulated timeout"));
        var client = new SesameClient(handler);

        Assert.Throws<TaskCanceledException>(() => client.Query("M 31"));
    }

    [Fact]
    public void Query_HandlerRefusesConnection_ThrowsHttpRequestException()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("simulated connection refused"));
        var client = new SesameClient(handler);

        Assert.Throws<HttpRequestException>(() => client.Query("M 31"));
    }

    [Fact]
    public void Query_TooManyRequestsStatus_DoesNotThrowNonTransient()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.TooManyRequests, "");
        var client = new SesameClient(handler);

        var ex = Record.Exception(() => client.Query("M 31"));

        Assert.NotNull(ex);
        Assert.IsNotType<NonTransientCatalogException>(ex);
        Assert.IsType<HttpRequestException>(ex);
    }

    [Fact]
    public void Query_DeclaredContentLengthOverCap_ThrowsNonTransientCatalogException()
    {
        var handler = new FakeHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<Sesame></Sesame>") };
            response.Content.Headers.ContentLength = 6 * 1024 * 1024;
            return response;
        });
        var client = new SesameClient(handler);

        Assert.Throws<NonTransientCatalogException>(() => client.Query("M 31"));
    }

    [Fact]
    public void Query_UnboundedStreamWithoutContentLength_ThrowsNonTransientCatalogException()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnboundedContent(50_000_000),
        });
        var client = new SesameClient(handler);

        Assert.Throws<NonTransientCatalogException>(() => client.Query("M 31"));
    }
}
