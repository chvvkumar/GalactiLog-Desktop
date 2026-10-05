using Microsoft.EntityFrameworkCore;

namespace GalactiLog.Data;

// The one place that builds DbContextOptions<GalactiLogContext>, so every caller
// (repositories, ScanWriter, tests, the design-time factory) gets the same provider setup
// and pragma interceptor.
public static class GalactiLogContextOptions
{
    public static DbContextOptions<GalactiLogContext> Create(string connectionString, bool tracking = false)
    {
        var builder = new DbContextOptionsBuilder<GalactiLogContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(new PragmaConnectionInterceptor());

        if (!tracking)
        {
            builder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        }

        return builder.Options;
    }
}
