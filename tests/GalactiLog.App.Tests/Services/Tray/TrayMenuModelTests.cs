using System.Text.RegularExpressions;
using GalactiLog.App.Services;
using GalactiLog.App.Services.Tray;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Tray;
using GalactiLog.Core.Settings;
using Xunit;

namespace GalactiLog.App.Tests.Services.Tray;

// Design-spec 12.11 behaviour 4 (ruling R30): the notification-area icon is the application's own
// Shell_NotifyIcon with a native menu. The Win32 half (NativeTrayIcon) is not observable off a
// real Windows shell and is left to the manual bar; what is observable here is the menu model it
// draws from: four items in spec order, a separator before Exit, each enabled by its command, and
// the id TrackPopupMenuEx hands back reaching the right command.
public class TrayMenuModelTests
{
    private sealed class Harness : IDisposable
    {
        public readonly ScanStatusService Status;
        public readonly StatusBarViewModel StatusBar;
        public readonly TrayIconViewModel Tray;
        public int Exits;

        public Harness()
        {
            Status = new ScanStatusService(ScanCoordinatorTestFactory.CreateBare(), action => action());
            StatusBar = new StatusBarViewModel(Status, () => { }, _ => Task.CompletedTask);
            var residency = new WindowResidencyService(
                () => new GeneralSettings(),
                post: action => action(),
                requestShutdown: () => { Exits++; return true; });
            Tray = new TrayIconViewModel(StatusBar, residency, post: action => action());
        }

        public void Dispose()
        {
            Tray.Dispose();
            StatusBar.Dispose();
            Status.Dispose();
        }
    }

    [Fact]
    public void Build_HasTheFourItemsInSpecOrder_WithTheSeparatorBeforeExit()
    {
        using var harness = new Harness();

        var entries = TrayMenuModel.Build(harness.Tray);

        // Spec 12.11 behaviour 4 and ruling Q11: four items plus one separator, nothing else.
        Assert.Equal(5, entries.Count);
        Assert.Equal(
            new[] { "Open", "Scan now", "Check for updates", "Exit" },
            entries.Where(entry => entry.Label is not null).Select(entry => entry.Label));
        Assert.Null(entries[3].Label);
        Assert.False(entries[3].Enabled);
        Assert.Equal(
            new[] { TrayMenuModel.OpenId, TrayMenuModel.ScanNowId, TrayMenuModel.CheckForUpdatesId, TrayMenuModel.ExitId },
            entries.Where(entry => entry.Label is not null).Select(entry => entry.Id));
    }

    [Fact]
    public void Build_GreysAnItemWhoseCommandCannotExecute()
    {
        using var harness = new Harness();

        // No update service: CheckForUpdates.CanExecute is false, which is the surface a build the
        // updater did not install gets (spec 12.11 behaviour 4, ruling Q4). Open and Exit are
        // always executable; Scan now follows the status bar's own command.
        var entries = TrayMenuModel.Build(harness.Tray);

        Assert.False(entries.Single(entry => entry.Id == TrayMenuModel.CheckForUpdatesId).Enabled);
        Assert.True(entries.Single(entry => entry.Id == TrayMenuModel.OpenId).Enabled);
        Assert.True(entries.Single(entry => entry.Id == TrayMenuModel.ExitId).Enabled);
        Assert.Equal(
            harness.Tray.ScanNowCommand.CanExecute(null),
            entries.Single(entry => entry.Id == TrayMenuModel.ScanNowId).Enabled);
    }

    [Fact]
    public void Run_DispatchesTheReturnedId_ToThatEntryOnly()
    {
        var ran = new List<int>();
        var entries = new List<TrayMenuEntry>
        {
            new(TrayMenuModel.OpenId, "Open", true, () => ran.Add(TrayMenuModel.OpenId)),
            new(TrayMenuModel.ScanNowId, "Scan now", false, () => ran.Add(TrayMenuModel.ScanNowId)),
            new(0, null, false, null),
            new(TrayMenuModel.ExitId, "Exit", true, () => ran.Add(TrayMenuModel.ExitId)),
        };

        // The id round trip: the integer TrackPopupMenuEx returns is the entry's own id.
        TrayMenuModel.Run(entries, TrayMenuModel.ExitId);
        Assert.Equal([TrayMenuModel.ExitId], ran);

        // 0 is a dismissed menu; a greyed entry is never run even if the shell returned its id.
        TrayMenuModel.Run(entries, 0);
        TrayMenuModel.Run(entries, TrayMenuModel.ScanNowId);
        TrayMenuModel.Run(entries, 99);
        Assert.Equal([TrayMenuModel.ExitId], ran);
    }

    [Fact]
    public void Build_ExitEntry_RunsTheExitCommand()
    {
        using var harness = new Harness();

        TrayMenuModel.Run(TrayMenuModel.Build(harness.Tray), TrayMenuModel.ExitId);

        Assert.Equal(1, harness.Exits);
    }

    [Fact]
    public void ClipToolTip_TruncatesAt127_AndLeavesShorterTextAlone()
    {
        Assert.Equal(127, TrayMenuModel.ToolTipMaxLength);
        Assert.Equal(127, TrayMenuModel.ClipToolTip(new string('x', 300)).Length);
        Assert.Equal("GalactiLog", TrayMenuModel.ClipToolTip("GalactiLog"));
        Assert.Equal(new string('x', 127), TrayMenuModel.ClipToolTip(new string('x', 127)));
    }

    // Source pins. The first is carried over from the XAML TrayIcon's tests: ruling R30 replaced
    // the declaration, not the wiring rule.
    [Fact]
    public void TheSingleInstanceActivation_ReachesTheSameResidencyService()
    {
        // One implementation of "bring the application up", shared by the icon's click, the tray
        // menu's Open and a second launch, with the exiting guard inside the service.
        var source = SourceScan.StripComments(
            File.ReadAllText(Path.Combine(SourceScan.SrcRoot(), "GalactiLog.App", "App.axaml.cs")));

        Assert.Contains("residency.ShowAndActivate", source, StringComparison.Ordinal);
        Assert.DoesNotContain("residency.IsExiting", source, StringComparison.Ordinal);
    }

    [Fact]
    public void App_DeclaresNoAvaloniaTrayIcon_AndBuildsTheNativeOneInsideTheServicesBlock()
    {
        // Ruling R30: Avalonia's managed tray popup misplaces the menu under DPI scaling, so the
        // declaration leaves App.axaml and the native icon is built where Services is non-null,
        // which the headless harness never is.
        var app = Path.Combine(SourceScan.SrcRoot(), "GalactiLog.App");
        var xaml = File.ReadAllText(Path.Combine(app, "App.axaml"));
        var source = SourceScan.StripComments(File.ReadAllText(Path.Combine(app, "App.axaml.cs")));

        Assert.DoesNotContain("TrayIcon.Icons", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("TrayIcon.GetIcons", source, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(@"if \(Services is \{ \} services\)[\s\S]*OperatingSystem\.IsWindows\(\)[\s\S]*new NativeTrayIcon\("),
            source);
    }
}
