using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.Core.Settings;
using GalactiLog.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GalactiLog.App.Tests;

/// <summary>
/// Fix-wave review P2-4 (R4). <c>SettingsStore.ReadDisplay</c> repairs a stored display
/// document one unreadable member at a time and logs a warning naming what it dropped
/// (<c>SettingsStore.RepairDisplay</c>), but that warning only reaches a reader if
/// <c>AppHost</c> actually hands the store a logger. Before this fix no call site did: the
/// store's optional trailing <c>ILogger</c> fell back to
/// <c>Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance</c> and every warning went
/// nowhere, so a reader's damaged display document opened the application silently on the
/// defaults with no trace anywhere.
/// </summary>
/// <remarks>
/// <c>AppHost.cs:184</c> constructs <c>SettingsStore</c> before <c>builder.Build()</c>, because
/// its <c>GetDisplay()</c> is read synchronously a few lines later
/// (<c>initialDisplay = settingsStore.GetDisplay()</c>), so no <c>IServiceProvider</c> exists
/// yet to resolve <c>ILogger&lt;SettingsStore&gt;</c> the way
/// <c>FrameHeadersQuery</c> and <c>TargetWriteRepository</c> do elsewhere in the same file. The
/// logger passed instead is built from <c>Log.Logger</c> directly (assigned at
/// <c>AppHost.cs:151</c>, well before the store's construction at <c>:184</c>, so the store is
/// never built before the logger exists), through <c>SerilogLoggerFactory</c>, the identical
/// bridge class <c>Serilog.Extensions.Hosting</c>'s <c>AddSerilog</c> uses to adapt
/// <c>Log.Logger</c> into the container's <c>ILoggerFactory</c>: no second logging route, just
/// the same one invoked before the container exists to resolve it from.
/// </remarks>
public sealed class SettingsStoreLoggerWiringTests
{
    /// <summary>
    /// The real-host case. Two starts over one temp app data root, the shape
    /// <c>Phd2ReRunHostWiringTests.ASaveThatLandedDuringAPass_IsStillOwedAtTheNextStart</c>
    /// uses: the first creates and migrates the database, the display document is then
    /// hand-edited exactly as launched-look step 32 describes, and the second start is the one
    /// under test, because <c>AppHost.Build</c> reads the display document exactly once, at
    /// startup, before anything else in this test touches it.
    /// </summary>
    [Fact]
    public void TheRealHost_RepairsAWrongTypedDisplayMemberOnStartup_LogsOneWarningNamingIt_AndKeepsTheOtherMembers()
    {
        var root = Path.Combine(Path.GetTempPath(), "GalactiLogSettingsStoreLoggerWiring_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            string connectionString;
            using (var first = AppHost.Build(root, cliMode: false))
            {
                connectionString = first.Services.GetRequiredService<DatabaseConnectionString>().Value;
            }
            Serilog.Log.CloseAndFlush();
            SqliteConnection.ClearAllPools();

            // Hand-edit the stored document: a wrong-typed target_page, beside a
            // dashboard.filter_panel_width and a columns list that are both NOT the documented
            // defaults, so a value that survives the repair can be told apart from a value
            // that merely fell back to it.
            using (var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString, tracking: true)))
            {
                var row = Assert.Single(context.UserSettings);
                row.Display = "{\"target_page\":5,\"dashboard\":{\"filter_panel_width\":350},"
                    + "\"columns\":{\"dashboard\":[\"name\",\"integration\"]}}";
                context.SaveChanges();
            }

            using var host = AppHost.Build(root, cliMode: false);

            var warnings = host.Services.GetRequiredService<LogRingBuffer>().Snapshot()
                .Where(entry => entry.Level == "Warning"
                    && entry.Message.Contains("stored display document", StringComparison.Ordinal))
                .ToList();
            var warning = Assert.Single(warnings);
            Assert.Contains("$.target_page", warning.Message, StringComparison.Ordinal);

            // The repaired document: target_page fell back to its default, and the two members
            // seeded above at non-default values survived it untouched.
            var display = host.Services.GetRequiredService<SettingsStore>().GetDisplay();
            Assert.Equal(new TargetPageSettings(), display.TargetPage);
            Assert.Equal(350, display.Dashboard.FilterPanelWidth);
            Assert.Equal(new[] { "name", "integration" }, display.ColumnsFor(DisplaySettings.DashboardTableId));
        }
        finally
        {
            Serilog.Log.CloseAndFlush();
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// The structural half: a source scan, because nothing about the argument is observable
    /// from outside <c>SettingsStore</c> once it is built, the same reason
    /// <c>AppHostSource_HandsBothPagesTheNotification_AndTheDetailQueryTheProfileMap</c> in
    /// <c>AppHostTests.cs</c> scans rather than resolves. A future edit that drops back to the
    /// one-argument constructor call compiles clean, because the parameter is optional, and
    /// would otherwise be caught by nothing at all.
    /// </summary>
    [Fact]
    public void AppHostSource_PassesALoggerToSettingsStore()
    {
        var code = SourceScan.StripComments(File.ReadAllText(
            Path.Combine(SourceScan.SrcRoot(), "GalactiLog.App", "AppHost.cs")));

        Assert.Contains("new SettingsStore(", code, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"new SettingsStore\([^,()]*\)", code);
    }
}
