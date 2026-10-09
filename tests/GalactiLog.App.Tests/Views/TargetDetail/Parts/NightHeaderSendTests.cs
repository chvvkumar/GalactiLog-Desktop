using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.Views.TargetDetail;
using GalactiLog.Core.Integrations;
using GalactiLog.Core.Settings;
using Xunit;
using static GalactiLog.App.Tests.TestSupport.TargetPartHost;

namespace GalactiLog.App.Tests.Views.TargetDetail.Parts;

// Spec 12.16, rulings B5 and B9, on the night heading's Send to button. Hosted on the whole page
// rather than on NightHeaderPart alone, because the button's bindings reach the layout view's
// DataContext for the page's own lists; the part alone would hide it.
public class NightHeaderSendTests
{
    [AvaloniaFact]
    public void NothingConfigured_HidesTheSendButton()
    {
        using var page = BuildPage();
        var view = new TargetDetailView { DataContext = page };
        Show(view);

        Assert.False(view.Named<Button>("SendButton").IsVisible);
    }

    [AvaloniaFact]
    public void OneOfferedNinaInstance_ShowsTheButton_WithTheNinaSubmenuOnly()
    {
        var general = new GeneralSettings
        {
            NinaInstancesDocument = IntegrationSettings.WriteInstances(
                [new IntegrationInstance("Obsy1", "http://a.local", true)]),
        };
        using var page = BuildPage(general);
        var view = new TargetDetailView { DataContext = page };
        Show(view);

        Assert.True(view.Named<Button>("SendButton").IsVisible);
        var item = MenuItemFromFlyout(view, "SendButton", "SendToNinaMenuItem");
        Assert.True(item.IsVisible);
        Assert.Same(page.NinaSendItems, item.ItemsSource);
        Assert.False(MenuItemFromFlyout(view, "SendButton", "SlewStellariumMenuItem").IsVisible);
    }

    // wave2-review: the one part of ruling B5 that can fail silently. A broken or unresolved
    // IntegrationSendItemTheme renders blank, inert leaf items and every other case still passes,
    // so this one opens the submenu and reads the generated container.
    [AvaloniaFact]
    public void TheGeneratedSubmenuItem_CarriesTheThemesHeaderAndCommand()
    {
        var general = new GeneralSettings
        {
            NinaInstancesDocument = IntegrationSettings.WriteInstances(
                [new IntegrationInstance("Obsy1", "http://a.local", true)]),
        };
        using var page = BuildPage(general);
        var view = new TargetDetailView { DataContext = page };
        Show(view);

        var item = MenuItemFromFlyout(view, "SendButton", "SendToNinaMenuItem");
        item.Open();
        Dispatcher.UIThread.RunJobs();

        var container = Assert.IsType<MenuItem>(item.ContainerFromIndex(0));
        Assert.Equal("Obsy1", container.Header);
        Assert.Same(page.NinaSendItems[0].SendCommand, container.Command);
    }
}
