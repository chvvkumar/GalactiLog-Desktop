using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.Tests.Views.TargetDetail.Parts;
using GalactiLog.App.Tests.ViewModels.Mosaics;
using GalactiLog.App.ViewModels.Mosaics;
using GalactiLog.App.Views.Mosaics;
using GalactiLog.Core.Io;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.Views.Mosaics;

// Phase 19A Task 4, spec 12.17's arranger as a view: the plan's verify list for ArrangerView over
// a detail page of four panels in a 1280 by 720 window. The view holds no rule, so these assert
// the bindings and the pointer bookkeeping; ArrangerViewModelTests holds the arithmetic.
public sealed class ArrangerViewTests
{
    private sealed class Mounted(MosaicDetailViewModel page, Window window, ArrangerView view) : IDisposable
    {
        public MosaicDetailViewModel Page { get; } = page;

        public Window Window { get; } = window;

        public ArrangerView View { get; } = view;

        public ArrangerViewModel Arranger => Page.Arranger;

        public ItemsControl Items => View.FindControl<ItemsControl>("TileItems")!;

        public Control Container(int index) => Items.ContainerFromIndex(index)!;

        public Button Button(string text)
            => View.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == text);

        public Slider Slider(string name)
            => View.GetVisualDescendants().OfType<Slider>().Single(slider => AutomationProperties.GetName(slider) == name);

        public void Dispose()
        {
            Window.Close();
            Page.Dispose();
        }
    }

    // The page's reads and the arranger's frame read post to the UI thread, so nothing a bound
    // control watches changes on the pool.
    private static async Task<Mounted> Mount(FakeMosaic mosaic, int panels = 4, Func<MosaicsBackend, MosaicsBackend>? backend = null)
    {
        for (var index = 0; index < panels; index++)
        {
            mosaic.AddPanel($"Panel {index + 1}");
        }

        var page = new MosaicDetailViewModel(
            mosaic.Id, backend is null ? mosaic.Backend() : backend(mosaic.Backend()), new AppWriter(Path.GetTempPath()), post: action => Dispatcher.UIThread.Post(action));
        await page.PendingLoad;
        Dispatcher.UIThread.RunJobs();
        await page.Arranger.PendingFrames;
        Dispatcher.UIThread.RunJobs();

        var detail = new MosaicDetailView { DataContext = page };
        var window = new Window { Width = 1280, Height = 720, Content = detail };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return new Mounted(page, window, detail.GetVisualDescendants().OfType<ArrangerView>().Single());
    }

    private static (double Left, double Top) At(Control container) => (Canvas.GetLeft(container), Canvas.GetTop(container));

    [AvaloniaFact]
    public async Task FourTiles_RenderInRowTwo_AtTheAutoLayout()
    {
        using var mounted = await Mount(new FakeMosaic());
        var view = mounted.View;

        Assert.Equal(2, Grid.GetRow(view));
        Assert.True(view.Bounds.Height > 100);
        Assert.Equal(4, mounted.Items.ItemCount);
        Assert.Equal((0d, 0d), At(mounted.Container(0)));
        Assert.Equal((254d, 0d), At(mounted.Container(1)));
        Assert.Equal((0d, 164d), At(mounted.Container(2)));
        Assert.Equal((254d, 164d), At(mounted.Container(3)));

        // Fit ran once the viewport had a size: the readout is no longer 100%.
        Assert.NotEqual(1d, mounted.Arranger.Zoom);
        Assert.Equal(mounted.Arranger.ZoomText, view.FindControl<TextBlock>("ZoomReadout")!.Text);

        foreach (var text in new[] { "Rotate CW", "Flip H", "Fit", "-", "+", "0", "Reset all", "Labels" })
        {
            Assert.True(mounted.Button(text).IsEffectivelyVisible, text);
        }

        Assert.False(mounted.Button("Rotate CW").IsEffectivelyEnabled);
        Assert.False(mounted.Button("Flip H").IsEffectivelyEnabled);
        Assert.False(mounted.Slider("Tile opacity").IsEffectivelyEnabled);
        Assert.Equal("Zoom out", AutomationProperties.GetName(mounted.Button("-")));
        Assert.Equal("Zoom in", AutomationProperties.GetName(mounted.Button("+")));
        Assert.Equal("Reset rotation", AutomationProperties.GetName(mounted.Button("0")));
        Assert.Equal("Rotate the selected tile 90° clockwise. Right-click a tile for the same.", ToolTip.GetTip(mounted.Button("Rotate CW")));

        var texts = view.GetVisualDescendants().OfType<TextBlock>().Where(block => block.IsEffectivelyVisible).Select(block => block.Text).ToList();
        Assert.Contains("Panels", texts);
        Assert.Contains("Click a tile to select it, then rotate or flip", texts);
        Assert.Contains("Panel 1", texts);

        // With no available filter the Filter group keeps its place, faded and inert.
        var filterGroup = view.FindControl<Control>("FilterGroup")!;
        Assert.Equal(0, filterGroup.Opacity);
        Assert.False(filterGroup.IsHitTestVisible);
        Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(filterGroup));

        // The tile's name reaches the automation tree, not only the attached property.
        var tileRoot = mounted.Container(0).GetVisualDescendants().OfType<Control>().First(control => control.Name == "Tile");
        var peer = ControlAutomationPeer.CreatePeerForElement(tileRoot);
        Assert.Equal("Panel 1, no thumbnail", peer.GetName());
        Assert.True(peer.IsControlElement());
    }

    // Follow-up 1: the tile follows its thumbnail. A tile with an image is the image's aspect at
    // 250 wide with no surface band behind it; the empty tile keeps 160 and the surface ink; both
    // carry the 1 pixel border inside their footprint.
    [AvaloniaFact]
    public async Task ATileWithAnImage_IsItsAspect_OverNoSurface_AndEveryTileHasAThinBorder()
    {
        using var thumbs = new ThumbnailKit.Thumbnails(_ => ThumbnailKit.Synthetic(100, 100));
        var mosaic = new FakeMosaic();
        var first = mosaic.AddPanel("Panel 1");
        mosaic.AddPanel("Panel 2");
        mosaic.Frames = new PanelFrameSet(
            ["Ha"], "Ha",
            new Dictionary<Guid, IReadOnlyDictionary<string, BestFrame>>
            {
                [first] = new Dictionary<string, BestFrame>(StringComparer.OrdinalIgnoreCase) { ["Ha"] = new(Guid.NewGuid(), "a.fits", "Ha", 1) },
            });
        using var mounted = await Mount(mosaic, panels: 0, backend: backend => backend with { ThumbnailFor = thumbs.Create });
        thumbs.PumpUntil(() => mounted.Arranger.Tiles[0].HasImage);

        Control Part(int index, string name) => mounted.Container(index).GetVisualDescendants().OfType<Control>().First(control => control.Name == name);
        Assert.True(mounted.View.TryFindResource("ColorBgSurface", mounted.View.ActualThemeVariant, out var surface));
        Assert.True(mounted.View.TryFindResource("ColorBorderDefault", mounted.View.ActualThemeVariant, out var border));

        Assert.Equal(250, Part(0, "Tile").Bounds.Height, 3);
        Assert.Equal(ArrangerViewModel.TileHeight, Part(1, "Tile").Bounds.Height, 3);
        for (var index = 0; index < 2; index++)
        {
            var content = (Border)Part(index, "TileContent");
            Assert.Equal(new Thickness(1), content.BorderThickness);
            Assert.Same(border, content.BorderBrush);
            Assert.Equal(0, Assert.IsAssignableFrom<ISolidColorBrush>(content.Background).Color.A);
        }

        // The surface ink sits behind the empty tile only.
        Assert.False(Part(0, "TileSurface").IsVisible);
        var empty = (Border)Part(1, "TileSurface");
        Assert.True(empty.IsVisible);
        Assert.Same(surface, empty.Background);
    }

    [AvaloniaFact]
    public async Task Selection_EnablesTheTileControls_AndMarksTheTile()
    {
        using var mounted = await Mount(new FakeMosaic());
        var tile = mounted.Arranger.Tiles[1];

        mounted.Arranger.Select(tile);
        Dispatcher.UIThread.RunJobs();

        Assert.True(mounted.Button("Rotate CW").IsEffectivelyEnabled);
        Assert.True(mounted.Button("Flip H").IsEffectivelyEnabled);
        Assert.True(mounted.Slider("Tile opacity").IsEffectivelyEnabled);
        var root = mounted.Container(1).GetVisualDescendants().OfType<Control>().First(control => control.Name == "Tile");
        Assert.True(mounted.Container(1).GetVisualDescendants().OfType<Border>().First(border => border.Name == "Outline").IsVisible);
        Assert.False(mounted.Container(0).GetVisualDescendants().OfType<Border>().First(border => border.Name == "Outline").IsVisible);
        Assert.Equal("Panel 2, selected, no thumbnail", AutomationProperties.GetName(root));

        // The hint fades out and keeps its place, so the toolbar does not reflow under a press.
        var hint = mounted.View.FindControl<TextBlock>("Hint")!;
        Assert.Equal(0, hint.Opacity);
        Assert.True(hint.IsVisible);
        Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(hint));

        // The opacity fades the tile's content and not its outline.
        mounted.Slider("Tile opacity").Value = 50;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0.5, mounted.Container(1).GetVisualDescendants().OfType<Border>().First(border => border.Name == "TileContent").Opacity, 3);
        Assert.Equal(1, mounted.Container(1).GetVisualDescendants().OfType<Border>().First(border => border.Name == "Outline").Opacity);
    }

    [AvaloniaFact]
    public async Task Dragging_ThroughTheViewModel_MovesTheContainer()
    {
        using var mounted = await Mount(new FakeMosaic());
        var tile = mounted.Arranger.Tiles[0];

        mounted.Arranger.Select(tile);
        mounted.Arranger.BeginDrag(tile, 10, 10);
        mounted.Arranger.Drag(tile, 110, 60);
        mounted.Arranger.EndDrag(tile);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal((100d, 50d), At(mounted.Container(0)));
        Assert.Equal(tile.ZIndex, mounted.Container(0).ZIndex);
        Assert.True(mounted.Container(0).ZIndex > mounted.Container(1).ZIndex);
    }

    [AvaloniaFact]
    public async Task Dragging_WithThePointer_MovesTheTileUnderIt()
    {
        using var mounted = await Mount(new FakeMosaic());
        var tile = mounted.Arranger.Tiles[3];
        var zoom = mounted.Arranger.Zoom;
        var container = mounted.Container(3);
        var start = container.TranslatePoint(new Point(125, 80), mounted.Window)!.Value;

        mounted.Window.MouseDown(start, MouseButton.Left);
        mounted.Window.MouseMove(start + new Point(40, 20));
        mounted.Window.MouseUp(start + new Point(40, 20), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(tile, mounted.Arranger.Selected);
        Assert.Equal(254 + 40 / zoom, tile.X, 3);
        Assert.Equal(164 + 20 / zoom, tile.Y, 3);
        Assert.Equal((tile.X, tile.Y), At(container));

        // A click with no movement on the selected tile deselects it (the web's toggle).
        var now = container.TranslatePoint(new Point(125, 80), mounted.Window)!.Value;
        mounted.Window.MouseDown(now, MouseButton.Left);
        mounted.Window.MouseUp(now, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(mounted.Arranger.Selected);
    }

    // A right press on another tile while the left button drags one is not a selection.
    [AvaloniaFact]
    public async Task ARightPress_DuringALeftDrag_KeepsTheDraggedTileSelected()
    {
        using var mounted = await Mount(new FakeMosaic());
        var dragged = mounted.Arranger.Tiles[3];
        var start = mounted.Container(3).TranslatePoint(new Point(125, 80), mounted.Window)!.Value;
        var other = mounted.Container(0).TranslatePoint(new Point(125, 80), mounted.Window)!.Value;

        mounted.Window.MouseDown(start, MouseButton.Left);
        mounted.Window.MouseDown(other, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(dragged, mounted.Arranger.Selected);

        mounted.Window.MouseUp(other, MouseButton.Right);
        mounted.Window.MouseUp(start, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(dragged, mounted.Arranger.Selected);
    }

    [AvaloniaFact]
    public async Task EmptyCanvas_PansKeepingTheSelection_ClickDeselects_AndTheWheelZooms()
    {
        using var mounted = await Mount(new FakeMosaic());
        mounted.Arranger.Select(mounted.Arranger.Tiles[0]);
        var viewport = mounted.View.FindControl<Border>("Viewport")!;
        var empty = viewport.TranslatePoint(new Point(viewport.Bounds.Width - 5, viewport.Bounds.Height - 5), mounted.Window)!.Value;
        var (offsetX, offsetY) = (mounted.Arranger.OffsetX, mounted.Arranger.OffsetY);

        mounted.Window.MouseDown(empty, MouseButton.Left);
        mounted.Window.MouseMove(empty - new Point(30, 10));
        mounted.Window.MouseUp(empty - new Point(30, 10), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        // Spec 12.17: a drag on empty canvas pans and the selection stays.
        Assert.Same(mounted.Arranger.Tiles[0], mounted.Arranger.Selected);
        Assert.Equal(offsetX - 30, mounted.Arranger.OffsetX, 3);
        Assert.Equal(offsetY - 10, mounted.Arranger.OffsetY, 3);

        // A right release during a left press is not the left click's release: it does not
        // deselect. (The platform may drop the capture on it, which ends the gesture quietly.)
        mounted.Window.MouseDown(empty, MouseButton.Left);
        mounted.Window.MouseUp(empty, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(mounted.Arranger.Tiles[0], mounted.Arranger.Selected);
        mounted.Window.MouseUp(empty, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        // A press and release with no movement on empty canvas deselects.
        mounted.Window.MouseDown(empty, MouseButton.Left);
        mounted.Window.MouseUp(empty, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(mounted.Arranger.Selected);

        var zoom = mounted.Arranger.Zoom;
        mounted.Window.MouseWheel(empty, new Vector(0, 0.25));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(zoom + ArrangerViewModel.ZoomStep, mounted.Arranger.Zoom, 6);
        mounted.Window.MouseWheel(empty, new Vector(0, 0));
        Assert.Equal(zoom + ArrangerViewModel.ZoomStep, mounted.Arranger.Zoom, 6);
    }

    [AvaloniaFact]
    public async Task TheContextMenu_RotatesAndFlipsItsTile()
    {
        using var mounted = await Mount(new FakeMosaic());
        var tile = mounted.Arranger.Tiles[2];
        var root = mounted.Container(2).GetVisualDescendants().OfType<Control>().First(control => control.Name == "Tile");
        var menu = root.ContextMenu!;
        menu.Open(root);
        Dispatcher.UIThread.RunJobs();
        var items = menu.Items.OfType<MenuItem>().ToList();

        Assert.Equal(new[] { "Rotate CW", "Flip H" }, items.Select(item => item.Header as string));
        items[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        items[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(90, tile.Rotation);
        Assert.True(tile.FlipH);
        var turn = (TransformGroup)mounted.Container(2).GetVisualDescendants().OfType<LayoutTransformControl>().Single().LayoutTransform!;
        Assert.Equal(-1, ((ScaleTransform)turn.Children[0]).ScaleX);
        Assert.Equal(90, ((RotateTransform)turn.Children[1]).Angle);
        Assert.Contains(
            mounted.Container(2).GetVisualDescendants().OfType<TextBlock>(),
            block => block.Text == "90° · flipped" && block.IsEffectivelyVisible);

        // Labels unchecked hides every overlay.
        mounted.Arranger.ShowLabels = false;
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(
            mounted.Container(2).GetVisualDescendants().OfType<TextBlock>(),
            block => block.Text is "90° · flipped" or "Panel 3" && block.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task TheSliders_CarryTheSpecsBounds()
    {
        using var mounted = await Mount(new FakeMosaic());

        var rotation = mounted.Slider("Rotation");
        Assert.Equal((-180d, 180d, 1d), (rotation.Minimum, rotation.Maximum, rotation.TickFrequency));
        Assert.True(rotation.IsSnapToTickEnabled);
        var opacity = mounted.Slider("Tile opacity");
        Assert.Equal((20d, 100d, 5d), (opacity.Minimum, opacity.Maximum, opacity.TickFrequency));
        Assert.True(opacity.IsSnapToTickEnabled);
        Assert.Equal(100d, opacity.Value);

        rotation.Value = 45;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(45, mounted.Arranger.GlobalRotation);
        var canvas = (Canvas)mounted.Items.ItemsPanelRoot!;
        var group = (TransformGroup)canvas.RenderTransform!;
        var rotate = (RotateTransform)group.Children[0];
        Assert.Equal((45d, mounted.Arranger.RotationCentreX, mounted.Arranger.RotationCentreY), (rotate.Angle, rotate.CenterX, rotate.CenterY));
        Assert.Equal(mounted.Arranger.ViewMatrix, ((MatrixTransform)group.Children[1]).Matrix);
        Assert.Contains(mounted.View.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "45°");
    }

    [AvaloniaFact]
    public async Task TheFilterSelection_SurvivesAReplacedFilterList()
    {
        var mosaic = new FakeMosaic
        {
            Frames = new PanelFrameSet(["Ha", "OIII"], "Ha", new Dictionary<Guid, IReadOnlyDictionary<string, BestFrame>>()),
        };
        using var mounted = await Mount(mosaic);
        var combo = mounted.View.GetVisualDescendants().OfType<ComboBox>().Single();

        Assert.True(combo.IsEffectivelyVisible);
        Assert.Equal("Ha", combo.SelectedItem);
        Assert.Contains(
            mounted.Container(0).GetVisualDescendants().OfType<TextBlock>(),
            block => block.Text == "No Ha frames" && block.IsEffectivelyVisible);

        mosaic.Frames = new PanelFrameSet(["SII", "Ha", "OIII"], "SII", new Dictionary<Guid, IReadOnlyDictionary<string, BestFrame>>());
        mounted.Arranger.Apply(mosaic.Read()!);
        await mounted.Arranger.PendingFrames;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Ha", mounted.Arranger.SelectedFilter);
        Assert.Equal("Ha", combo.SelectedItem);
    }

    // The status captions keep their slots: showing or hiding one never moves the viewport, so a
    // captured drag or pan is not shifted under the pointer when Saving appears after a release.
    [AvaloniaFact]
    public async Task TheStatusCaptions_NeverReflowTheToolbar()
    {
        var mosaic = new FakeMosaic();
        using var mounted = await Mount(mosaic);
        var viewport = mounted.View.FindControl<Border>("Viewport")!;
        var toolbar = mounted.View.FindControl<WrapPanel>("Toolbar")!;
        var (top, height) = (viewport.Bounds.Y, toolbar.Bounds.Height);

        // At 1280 the toolbar may sit far from a wrap point, so the slots themselves are pinned
        // too: a caption whose slot keeps its size cannot reflow the toolbar at any width.
        var slots = new[] { "Hint", "LoadingCaption", "SaveSlot" }.Select(name => mounted.View.FindControl<Control>(name)!).ToList();
        var sizes = slots.Select(slot => slot.Bounds.Size).ToList();
        var isSaving = typeof(ArrangerViewModel).GetProperty(nameof(ArrangerViewModel.IsSaving))!;
        var saveError = typeof(ArrangerViewModel).GetProperty(nameof(ArrangerViewModel.SaveError))!;

        void Settled(string state)
        {
            Dispatcher.UIThread.RunJobs();
            Assert.True(top == viewport.Bounds.Y, $"{state}: the viewport moved from {top} to {viewport.Bounds.Y}");
            Assert.True(height == toolbar.Bounds.Height, $"{state}: the toolbar's height changed");
            Assert.Equal(sizes, slots.Select(slot => slot.Bounds.Size).ToList());
        }

        isSaving.SetValue(mounted.Arranger, true);
        Settled("saving");
        Assert.Equal(1, mounted.View.FindControl<TextBlock>("SavingCaption")!.Opacity);
        isSaving.SetValue(mounted.Arranger, false);
        saveError.SetValue(mounted.Arranger, ArrangerViewModel.SaveFailedText);
        Settled("failed");
        Assert.Equal(0, mounted.View.FindControl<TextBlock>("SavingCaption")!.Opacity);
        Assert.Equal(1, mounted.View.FindControl<TextBlock>("SaveFailure")!.Opacity);
        Assert.Equal(AccessibilityView.Default, AutomationProperties.GetAccessibilityView(mounted.View.FindControl<TextBlock>("SaveFailure")!));
        saveError.SetValue(mounted.Arranger, null);
        Settled("saved");
        Assert.Equal(0, mounted.View.FindControl<TextBlock>("SaveFailure")!.Opacity);
        Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(mounted.View.FindControl<TextBlock>("SaveFailure")!));
        mounted.Arranger.Select(mounted.Arranger.Tiles[0]);
        Settled("selected");
        mounted.Arranger.Select(null);
        Settled("deselected");

        // The first frame set can arrive mid-gesture: the Filter group appearing or going keeps
        // the toolbar's height.
        async Task Filters(string state, params string[] filters)
        {
            mosaic.Frames = new PanelFrameSet(filters, filters.FirstOrDefault(), new Dictionary<Guid, IReadOnlyDictionary<string, BestFrame>>());
            mounted.Arranger.Apply(mosaic.Read()!);
            await mounted.Arranger.PendingFrames;
            Settled(state);
            Assert.Equal(filters.Length > 0, mounted.Arranger.HasFilters);
        }

        await Filters("filters", "Ha", "OIII");
        Assert.Equal(1, mounted.View.FindControl<Control>("FilterGroup")!.Opacity);
        await Filters("no filters");
    }

    [Fact]
    public void TheView_WritesNoColourLiteral()
    {
        var text = File.ReadAllText(Path.Combine(SourceScan.SrcRoot(), "GalactiLog.App", "Views", "Mosaics", "ArrangerView.axaml"));

        Assert.DoesNotMatch(new Regex("#[0-9A-Fa-f]{6,8}"), text);
        Assert.DoesNotMatch(new Regex(@"FontSize\s*="), text);
        Assert.DoesNotMatch(new Regex("Classes=\"[^\"]*\\bprimary\\b"), text);
    }

    // The Fluent Slider's ink keys, aliased in every dictionary the way the ScrollBar and ComboBox
    // keys are, so a theme swap moves the slider with the rest.
    [AvaloniaTheory]
    [InlineData("avares://GalactiLog/Theme/Themes/CivilDusk.axaml")]
    [InlineData("avares://GalactiLog/Theme/Themes/Luminance.axaml")]
    [InlineData("avares://GalactiLog/Theme/Themes/DeepSky.axaml")]
    [InlineData("avares://GalactiLog/Theme/Themes/RedLight.axaml")]
    [InlineData("avares://GalactiLog/Theme/Themes/Atlas.axaml")]
    [InlineData("avares://GalactiLog/Theme/Themes/Logbook.axaml")]
    public void EveryTheme_AliasesTheFluentSliderKeys(string source)
    {
        (string Fluent, string Token)[] aliases =
        [
            ("SliderThumbBackground", "ColorAccent"),
            ("SliderThumbBackgroundPointerOver", "ColorAccentHover"),
            ("SliderThumbBackgroundPressed", "ColorAccentPressed"),
            ("SliderThumbBackgroundDisabled", "ColorTextTertiary"),
            ("SliderTrackValueFill", "ColorAccent"),
            ("SliderTrackValueFillPointerOver", "ColorAccentHover"),
            ("SliderTrackValueFillPressed", "ColorAccentPressed"),
            ("SliderTrackValueFillDisabled", "ColorTextTertiary"),
            ("SliderTrackFill", "ColorBorderEmphasis"),
            ("SliderTrackFillPointerOver", "ColorBorderEmphasis"),
            ("SliderTrackFillPressed", "ColorBorderEmphasis"),
            ("SliderTrackFillDisabled", "ColorTextTertiary"),
        ];

        // One loaded dictionary, compared by reference: an alias resolves to its token's own
        // brush, so a wrong alias whose token shares a colour still fails.
        var dictionary = new ResourceInclude((Uri?)null) { Source = new Uri(source) };
        foreach (var (fluent, token) in aliases)
        {
            Assert.Same(Resource(dictionary, token), Resource(dictionary, fluent));
        }
    }

    private static object Resource(ResourceInclude dictionary, string key)
    {
        Assert.True(dictionary.TryGetResource(key, ThemeVariant.Dark, out var value) && value is not null, $"Missing '{key}'");
        return value!;
    }
}
