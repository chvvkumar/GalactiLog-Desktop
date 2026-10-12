using GalactiLog.App.Services.Telemetry;
using GalactiLog.Core.Settings;
using Xunit;

namespace GalactiLog.App.Tests.Services.Telemetry;

public sealed class TelemetryServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private static readonly GeneralSettings Ready = new()
    {
        TelemetryEnabled = true,
        TelemetryId = "0f8fad5b-d9cb-469f-a165-70867728950e",
    };

    private static string Ago(double hours) => Now.AddHours(-hours).ToString("yyyy-MM-ddTHH:mm:ssZ");

    [Fact]
    public void Disabled_DoesNotSend() => Assert.False(TelemetryService.ShouldSend(Ready with { TelemetryEnabled = false }, true, Now));

    [Fact]
    public void NoId_DoesNotSend() => Assert.False(TelemetryService.ShouldSend(Ready with { TelemetryId = null }, true, Now));

    [Fact]
    public void NotInstalled_DoesNotSend() => Assert.False(TelemetryService.ShouldSend(Ready, false, Now));

    [Fact]
    public void SentAnHourAgo_DoesNotSend()
        => Assert.False(TelemetryService.ShouldSend(Ready with { TelemetryLastSent = Ago(1) }, true, Now));

    [Fact]
    public void Sent25HoursAgo_Sends()
        => Assert.True(TelemetryService.ShouldSend(Ready with { TelemetryLastSent = Ago(25) }, true, Now));

    [Fact]
    public void NeverSent_Sends() => Assert.True(TelemetryService.ShouldSend(Ready, true, Now));

    [Fact]
    public void UnparsableLastSent_Sends()
        => Assert.True(TelemetryService.ShouldSend(Ready with { TelemetryLastSent = "not a date" }, true, Now));

    [Fact]
    public void FutureLastSent_Sends()
        => Assert.True(TelemetryService.ShouldSend(Ready with { TelemetryLastSent = Ago(-48) }, true, Now));

    [Fact]
    public void TheDailyStamp_IsInert_SoItDoesNotInvalidateDerivedData()
        => Assert.True(AppHost.OnlyInertKeysMoved(Ready, Ready with { TelemetryLastSent = Ago(0) }));
}
