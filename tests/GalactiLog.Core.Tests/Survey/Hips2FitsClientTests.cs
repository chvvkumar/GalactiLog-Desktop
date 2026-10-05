using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using GalactiLog.Core.Survey;
using GalactiLog.Core.Tests.Targets;
using Xunit;

namespace GalactiLog.Core.Tests.Survey;

// Every case runs over a stub handler: no request leaves the process.
public class Hips2FitsClientTests
{
    private static readonly SurveyView View = new(Surveys.DefaultId, 10.5, 41.25, 1.5);

    private static HttpResponseMessage Answer(HttpStatusCode status, string mediaType, byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return new HttpResponseMessage(status) { Content = content };
    }

    // Case 9. A failure is the wrong host, path, method or query, or the bytes not handed back.
    [Fact]
    public async Task AJpegAnswerIsOkWithTheBytes()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xD9];
        var calls = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            calls++;
            return Answer(HttpStatusCode.OK, "image/jpeg", jpeg);
        });
        using var http = new HttpClient(handler);

        var result = await new Hips2FitsClient(http).FetchAsync(View, CancellationToken.None);

        Assert.Equal(Hips2FitsStatus.Ok, result.Status);
        Assert.Equal(jpeg, result.Jpeg);
        Assert.Equal(1, calls);
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal($"{Hips2FitsClient.Endpoint}?{View.QueryString()}", handler.LastRequest.RequestUri!.AbsoluteUri);
    }

    // Case 10. A failure is an error page, a redirect or a transport fault handed on as an image.
    [Fact]
    public async Task EveryOtherAnswerIsFailedWithNoBytes()
    {
        var answers = new Func<HttpRequestMessage, HttpResponseMessage>[]
        {
            _ => Answer(HttpStatusCode.NotFound, "image/jpeg", [1]),
            _ => Answer(HttpStatusCode.OK, "text/html", [1]),
            _ => Answer(HttpStatusCode.Found, "image/jpeg", [1]),
            _ => throw new HttpRequestException("refused"),
        };

        foreach (var answer in answers)
        {
            using var http = new HttpClient(new FakeHttpMessageHandler(answer));

            var result = await new Hips2FitsClient(http).FetchAsync(View, CancellationToken.None);

            Assert.Equal(Hips2FitsStatus.Failed, result.Status);
            Assert.Null(result.Jpeg);
        }
    }

    // Case 11. A failure is an unbounded hang, the bound reported as a cancel, or the shared
    // client's Timeout rewritten.
    [Fact]
    public async Task AWedgedHostIsBoundedAndTheCallersTokenCancelsSooner()
    {
        var bound = TimeSpan.FromMilliseconds(500);
        using var http = new HttpClient(new WedgedHandler());
        var timeoutBefore = http.Timeout;

        var ownBound = Stopwatch.StartNew();
        var bounded = await new Hips2FitsClient(http, bound).FetchAsync(View, CancellationToken.None);
        ownBound.Stop();

        Assert.Equal(Hips2FitsStatus.Failed, bounded.Status);
        Assert.Null(bounded.Jpeg);
        Assert.InRange(ownBound.Elapsed, bound - TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(15));

        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var callerBound = Stopwatch.StartNew();
        var cancelled = await new Hips2FitsClient(http, TimeSpan.FromSeconds(5)).FetchAsync(View, caller.Token);
        callerBound.Stop();

        Assert.Equal(Hips2FitsStatus.Cancelled, cancelled.Status);
        Assert.True(callerBound.Elapsed < TimeSpan.FromSeconds(2.5), "the caller's token did not end the request first");
        Assert.Equal(timeoutBefore, http.Timeout);
    }

    // A failure is a default bound other than 15 seconds, which no other case here pins because each passes its own.
    [Fact]
    public void TheDefaultRequestBoundIsFifteenSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), Hips2FitsClient.DefaultRequestTimeout);
    }

    private sealed class WedgedHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
