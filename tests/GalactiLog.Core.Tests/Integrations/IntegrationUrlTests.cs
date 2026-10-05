using GalactiLog.Core.Integrations;
using Xunit;

namespace GalactiLog.Core.Tests.Integrations;

/// <summary>
/// The URL rule of design-spec 12.16, task2-clients.md cases 1 to 3. The rule is the one gate
/// between a stored string and a base address, so a term dropped here is a whole class of
/// instances made unreachable or a whole class of schemes let through.
/// </summary>
public class IntegrationUrlTests
{
    // Case 1. Loopback and the private ranges are accepted deliberately: refusing them would
    // refuse http://localhost:1888, which is the ordinary NINA setup.
    [Theory]
    [InlineData("http://host:1888", "http://host:1888")]
    [InlineData("https://h/", "https://h")]
    [InlineData("http://localhost:1888", "http://localhost:1888")]
    [InlineData("http://127.0.0.1:1888", "http://127.0.0.1:1888")]
    [InlineData("http://192.168.0.10:1888", "http://192.168.0.10:1888")]
    public void TryParseAcceptsHttpAndHttpsWithAHost(string url, string expected)
    {
        Assert.True(IntegrationUrl.TryParse(url, out var baseAddress));
        Assert.Equal(expected, baseAddress);
    }

    // Case 2. Each of these fails a different term of the rule; a refusal returns "" and never
    // null, so a caller that ignored the bool still builds no request against a host.
    [Theory]
    [InlineData("ftp://h")]
    [InlineData("file:///c:/x")]
    [InlineData("/relative")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("http://")]
    [InlineData("   ")]
    public void TryParseRefusesEverythingElse(string? url)
    {
        Assert.False(IntegrationUrl.TryParse(url, out var baseAddress));
        Assert.Equal("", baseAddress);
    }

    // Case 3. Every trailing slash, not one: a base address that kept one puts a double slash in
    // every built request path.
    [Theory]
    [InlineData("http://h:1/")]
    [InlineData("http://h:1///")]
    [InlineData("http://h:1")]
    public void TryParseTrimsEveryTrailingSlash(string url)
    {
        Assert.True(IntegrationUrl.TryParse(url, out var baseAddress));
        Assert.Equal("http://h:1", baseAddress);
    }
}
