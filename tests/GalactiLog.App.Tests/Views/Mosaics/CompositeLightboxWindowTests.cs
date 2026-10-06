using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.Mosaics;
using GalactiLog.App.Views.Mosaics;
using GalactiLog.Core.Io;
using GalactiLog.Core.Mosaics;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.Views.Mosaics;

// Phase 19B Task 4, design-spec 18.3's view smoke test for spec 12.17's composite lightbox: it lays
// out in Building and in Ready, Escape closes it, and the wheel over the image zooms. The rules
// themselves are asserted on the view model, in CompositeLightboxViewModelTests.
public class CompositeLightboxWindowTests
{
    private static readonly Guid Panel = Guid.NewGuid();
    private static readonly Guid Frame = Guid.NewGuid();

    private static CompositeRequest Request() => new(
        Guid.NewGuid(), "M 31", "Ha", [(Panel, "Panel 1")],
        new PanelFrameSet(["Ha"], "Ha", new Dictionary<Guid, IReadOnlyDictionary<string, BestFrame>>
        {
            [Panel] = new Dictionary<string, BestFrame>(StringComparer.OrdinalIgnoreCase) { ["Ha"] = new(Frame, "p.fits", "Ha", 1) },
        }),
        new Dictionary<Guid, PanelGeometry> { [Frame] = new(10, 44.3, 64, 15.0, 0, "West") });

    private static CompositeLightboxViewModel Page(Func<CancellationToken, CompositeResult> build)
        => new(Request(), new CompositeService(new JobRegistry(action => action()), (_, _, _, _) => { }, (_, _, _, ct) => build(ct)),
            new AppWriter(Path.GetTempPath()), post: action => Dispatcher.UIThread.Post(action));

    private static CompositeLightboxWindow Show(CompositeLightboxViewModel page)
    {
        var window = new CompositeLightboxWindow { DataContext = page };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void Building_ShowsTheSpinnerAndTheCaption_AndEscapeCloses()
    {
        using var gate = new ManualResetEventSlim();
        using var page = Page(ct =>
        {
            gate.Wait(ct);
            return new CompositeResult([1], 1, 1);
        });
        var window = Show(page);

        Assert.True(window.Bounds.Width > 0);
        Assert.Equal("M 31, Ha composite", window.Title);
        Assert.Equal("M 31, Ha composite", window.GetControl<TextBlock>("TitleText").Text);
        Assert.True(window.GetControl<StackPanel>("BuildingIndicator").IsVisible);
        Assert.Equal("Building the composite...", window.GetControl<TextBlock>("BuildingText").Text);
        Assert.False(window.GetControl<Border>("ErrorPanel").IsVisible);
        Assert.False(window.GetControl<Button>("DownloadButton").IsEffectivelyEnabled);

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public async Task Ready_ShowsTheImage_AndTheWheelZooms()
    {
        using var page = Page(_ => new CompositeResult([0x01, 0x02, 0x03, 0x04], 1, 1));
        await page.PendingBuild.WaitAsync(TimeSpan.FromSeconds(30));
        var window = Show(page);

        var image = window.GetControl<Image>("CompositeImage");
        Assert.NotNull(image.Source);
        Assert.Equal("Composite of M 31, Ha", Avalonia.Automation.AutomationProperties.GetName(image));
        Assert.False(window.GetControl<StackPanel>("BuildingIndicator").IsVisible);
        Assert.True(window.GetControl<Button>("DownloadButton").IsEffectivelyEnabled);

        var viewport = window.GetControl<Border>("Viewport");
        var centre = viewport.TranslatePoint(new Point(viewport.Bounds.Width / 2, viewport.Bounds.Height / 2), window)!.Value;
        window.MouseWheel(centre, new Vector(0, 1));
        Dispatcher.UIThread.RunJobs();

        Assert.True(page.Scale > 1d);
        window.Close();
    }
}
