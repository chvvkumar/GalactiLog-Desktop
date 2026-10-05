using GalactiLog.Core.Integrations;
using Xunit;

namespace GalactiLog.Core.Tests.Integrations;

/// <summary>Fix-wave review, fixwave-review.md AppHost.cs:516 (ruling B34). The holder alone,
/// beside AppHostTests.Build_DisposesTheIntegrationHttpClientWithTheHost which proves the
/// container reaches it through the host.</summary>
public class IntegrationHttpClientTests
{
    [Fact]
    public async Task Dispose_DisposesTheOwnedHttpClient()
    {
        var holder = new IntegrationHttpClient(new HttpClient());

        holder.Dispose();

        Assert.True(holder.IsDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => holder.Http.GetAsync("http://localhost/"));
    }
}
