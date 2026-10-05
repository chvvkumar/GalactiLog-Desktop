using System.Net;

namespace GalactiLog.Core.Tests.Targets;

// Minimal stub HttpMessageHandler for HttpClient.Send-based clients -- no network in tests.
// Overrides the protected synchronous Send(HttpRequestMessage, CancellationToken), the method
// HttpClient.Send ultimately calls.
internal sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }
    public bool WasInvoked { get; private set; }

    public static FakeHttpMessageHandler Returning(HttpStatusCode statusCode, string body)
        => new(_ => new HttpResponseMessage(statusCode) { Content = new StringContent(body) });

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        WasInvoked = true;
        LastRequest = request;
        return respond(request);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(Send(request, cancellationToken));
}

// A body with no Content-Length header (TryComputeLength false) that can produce far more
// than any sane response-size cap, for exercising a true streaming cap rather than the
// Content-Length pre-check fast path.
internal sealed class UnboundedContent(long totalBytes) : HttpContent
{
    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => throw new NotSupportedException("Test content is read via the sync HttpContent.ReadAsStream path only.");

    protected override Stream CreateContentReadStream(CancellationToken cancellationToken) => new RepeatingStream(totalBytes);

    private sealed class RepeatingStream(long totalBytes) : Stream
    {
        private long _remaining = totalBytes;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0)
            {
                return 0;
            }
            var n = (int)Math.Min(count, _remaining);
            Array.Clear(buffer, offset, n);
            _remaining -= n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
