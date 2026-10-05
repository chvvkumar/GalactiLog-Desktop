using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.Preview;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.PreviewModalViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

// Phase 8 Task 7. Spec 11.5's preview modal, asserted where spec 18.3 says to assert it: at the
// view-model. Every shortcut is a command, so no test here synthesizes a key press, and the drag
// pan is asserted through Pan rather than a pointer gesture.
//
// Bitmaps need the headless platform, so every test that completes a render is an [AvaloniaFact];
// the rest are plain [Fact] and never complete one.
public class PreviewModalViewModelTests
{
    // --------------------------------------- spec 18.3: every shortcut, at the view-model level

    [Fact]
    public void EscapeCommand_RaisesCloseRequested()
    {
        using var harness = Factory.Create();

        harness.ViewModel.CloseCommand.Execute(null);

        Assert.Equal(1, harness.Closes);
    }

    [Fact]
    public void RightArrowCommand_AdvancesToTheNextFrame()
    {
        using var harness = Factory.Create();

        harness.ViewModel.NextCommand.Execute(null);

        Assert.Equal(1, harness.ViewModel.Index);
        Assert.Equal(harness.ViewModel.Frames[1], harness.ViewModel.Current);
    }

    [Fact]
    public void LeftArrowCommand_ReturnsToThePreviousFrame()
    {
        using var harness = Factory.Create(index: 2);

        harness.ViewModel.PreviousCommand.Execute(null);

        Assert.Equal(1, harness.ViewModel.Index);
        Assert.Equal(harness.ViewModel.Frames[1], harness.ViewModel.Current);
    }

    [Fact]
    public void ZeroKeyCommand_ResetsToFit()
    {
        using var harness = Factory.Create();
        harness.ViewModel.Zoom(-300, 40, 20);
        Assert.NotEqual(1d, harness.ViewModel.Scale);

        harness.ViewModel.FitCommand.Execute(null);

        Assert.Equal(1d, harness.ViewModel.Scale);
        Assert.Equal(0d, harness.ViewModel.OffsetX);
        Assert.Equal(0d, harness.ViewModel.OffsetY);
    }

    [Fact]
    public async Task HKeyCommand_TogglesTheHeaderPanel()
    {
        using var harness = Factory.Create();
        Assert.False(harness.ViewModel.IsHeaderPanelVisible);

        harness.ViewModel.ToggleHeaderPanelCommand.Execute(null);

        Assert.True(harness.ViewModel.IsHeaderPanelVisible);

        // Spec 12.4's panel, reused unchanged rather than re-rendered by a second type. Its read
        // runs off the calling thread, so the test joins it rather than sleeping.
        Assert.NotNull(harness.ViewModel.Headers);
        await harness.ViewModel.Headers!.PendingLoad!.WaitAsync(Factory.Budget);
        Assert.Equal([harness.ViewModel.Current.ImageId], harness.HeaderQueries);
    }

    [Fact]
    public void HKeyCommand_SecondPress_HidesThePanel()
    {
        using var harness = Factory.Create();

        harness.ViewModel.ToggleHeaderPanelCommand.Execute(null);
        harness.ViewModel.ToggleHeaderPanelCommand.Execute(null);

        Assert.False(harness.ViewModel.IsHeaderPanelVisible);
    }

    [Fact]
    public void CtrlECommand_RevealsTheCurrentFrameInExplorer()
    {
        using var harness = Factory.Create(index: 1, isWindows: true);

        harness.ViewModel.RevealInExplorerCommand.Execute(null);

        var info = Assert.Single(harness.Launched);
        Assert.Equal("explorer.exe", info.FileName);
        Assert.Equal($"/select,\"{harness.ViewModel.Current.FilePath}\"", info.Arguments);
    }

    [Fact]
    public void CtrlOCommand_OpensTheCurrentFrameWithTheDefaultApplication()
    {
        using var harness = Factory.Create(index: 1, isWindows: true);

        harness.ViewModel.OpenWithDefaultApplicationCommand.Execute(null);

        var info = Assert.Single(harness.Launched);
        Assert.Equal(harness.ViewModel.Current.FilePath, info.FileName);
        Assert.True(info.UseShellExecute);
    }

    [Fact]
    public async Task CtrlShiftCCommand_CopiesTheCurrentFramePath()
    {
        using var harness = Factory.Create(index: 2);

        await harness.ViewModel.CopyPathCommand.ExecuteAsync(null);

        Assert.Equal([harness.ViewModel.Current.FilePath], harness.Copies);
    }

    [Fact]
    public async Task EveryShortcutActsOnTheCurrentFrameAfterNavigation()
    {
        using var harness = Factory.Create(isWindows: true);
        harness.ViewModel.NextCommand.Execute(null);
        var expected = harness.ViewModel.Frames[1].FilePath;

        harness.ViewModel.RevealInExplorerCommand.Execute(null);
        harness.ViewModel.OpenWithDefaultApplicationCommand.Execute(null);
        await harness.ViewModel.CopyPathCommand.ExecuteAsync(null);
        harness.ViewModel.ToggleHeaderPanelCommand.Execute(null);
        await harness.ViewModel.Headers!.PendingLoad!.WaitAsync(Factory.Budget);

        Assert.Equal($"/select,\"{expected}\"", harness.Launched[0].Arguments);
        Assert.Equal(expected, harness.Launched[1].FileName);
        Assert.Equal([expected], harness.Copies);
        Assert.Equal([harness.ViewModel.Frames[1].ImageId], harness.HeaderQueries);
    }

    // ---------------------------------------- the roadmap's second clause: bounded navigation

    [Fact]
    public void Next_AtTheLastFrame_IsDisabled()
    {
        using var harness = Factory.Create(index: 2);

        Assert.False(harness.ViewModel.CanGoNext);
        Assert.False(harness.ViewModel.NextCommand.CanExecute(null));
    }

    [Fact]
    public void Next_AtTheLastFrame_DoesNothingWhenExecutedDirectly()
    {
        // RelayCommand.Execute does not consult CanExecute (TRACKING section 6 item 13), so the
        // bound is re-checked in the body. Without it this wraps to 0, which is the web behaviour
        // the spec overrides (ruling Q17).
        using var harness = Factory.Create(index: 2);

        harness.ViewModel.NextCommand.Execute(null);

        Assert.Equal(2, harness.ViewModel.Index);
    }

    [Fact]
    public void Previous_AtTheFirstFrame_IsDisabled()
    {
        using var harness = Factory.Create();

        Assert.False(harness.ViewModel.CanGoPrevious);
        Assert.False(harness.ViewModel.PreviousCommand.CanExecute(null));
    }

    [Fact]
    public void Previous_AtTheFirstFrame_DoesNothingWhenExecutedDirectly()
    {
        using var harness = Factory.Create();

        harness.ViewModel.PreviousCommand.Execute(null);

        Assert.Equal(0, harness.ViewModel.Index);
    }

    [Fact]
    public void Navigation_NeverWraps()
    {
        using var harness = Factory.Create();

        for (var i = 0; i < 10; i++)
        {
            harness.ViewModel.NextCommand.Execute(null);
        }
        Assert.Equal(2, harness.ViewModel.Index);

        for (var i = 0; i < 10; i++)
        {
            harness.ViewModel.PreviousCommand.Execute(null);
        }
        Assert.Equal(0, harness.ViewModel.Index);
    }

    [Fact]
    public void Navigation_IsBoundedByTheOriginatingList()
    {
        // Spec 11.5: "navigation is within the frame list the modal was opened from". A two-frame
        // list stays two frames wide however long the user holds the arrow key.
        using var harness = Factory.Create(frames: Factory.Frames(2));

        harness.ViewModel.NextCommand.Execute(null);
        harness.ViewModel.NextCommand.Execute(null);

        Assert.Equal(1, harness.ViewModel.Index);
        Assert.Equal("2 of 2", harness.ViewModel.PositionText);
    }

    [Fact]
    public void SingleFrameList_BothNavigationCommandsAreDisabled()
    {
        using var harness = Factory.Create(frames: Factory.Frames(1));

        Assert.False(harness.ViewModel.CanGoNext);
        Assert.False(harness.ViewModel.CanGoPrevious);
        Assert.Equal("1 of 1", harness.ViewModel.PositionText);
    }

    [Fact]
    public void Navigation_FollowsTheListOrderItWasGiven()
    {
        // The frame table hands over Rows in their current sort order, so stepping follows what
        // the user sees rather than capture order (ruling Q16).
        IReadOnlyList<PreviewFrameViewModel> sorted =
            [Factory.Frame(7), Factory.Frame(2), Factory.Frame(5)];
        using var harness = Factory.Create(frames: sorted);

        harness.ViewModel.NextCommand.Execute(null);
        Assert.Equal(sorted[1], harness.ViewModel.Current);

        harness.ViewModel.NextCommand.Execute(null);
        Assert.Equal(sorted[2], harness.ViewModel.Current);
    }

    // ------------------------------------------- the roadmap's third clause: the Windows guard

    [Fact]
    public void RevealInExplorer_IsDisabledOffWindows()
    {
        using var harness = Factory.Create(isWindows: false);

        Assert.False(harness.ViewModel.IsWindows);
        Assert.False(harness.ViewModel.RevealInExplorerCommand.CanExecute(null));
    }

    [Fact]
    public void RevealInExplorer_OffWindows_DoesNothingWhenExecutedDirectly()
    {
        using var harness = Factory.Create(isWindows: false);

        harness.ViewModel.RevealInExplorerCommand.Execute(null);

        Assert.Empty(harness.Launched);
    }

    [Fact]
    public void OpenWithDefaultApplication_IsDisabledOffWindows()
    {
        using var harness = Factory.Create(isWindows: false);

        Assert.False(harness.ViewModel.OpenWithDefaultApplicationCommand.CanExecute(null));
    }

    [Fact]
    public void OpenWithDefaultApplication_OffWindows_DoesNothingWhenExecutedDirectly()
    {
        using var harness = Factory.Create(isWindows: false);

        harness.ViewModel.OpenWithDefaultApplicationCommand.Execute(null);

        Assert.Empty(harness.Launched);
    }

    [Fact]
    public async Task CopyPath_IsNotPlatformGuarded()
    {
        // The clipboard is cross-platform. This pins that the guard was applied to the two
        // commands spec 19.2 names and not to a third (ruling Q20).
        using var harness = Factory.Create(isWindows: false);

        await harness.ViewModel.CopyPathCommand.ExecuteAsync(null);

        Assert.Equal([harness.ViewModel.Current.FilePath], harness.Copies);
    }

    // --------------------------------------------------------------------------- zoom and pan

    [Fact]
    public void Zoom_In_IncreasesTheScale()
    {
        using var harness = Factory.Create();

        // The web's sign convention: negative deltaY is scroll up is zoom in.
        harness.ViewModel.Zoom(-100, 0, 0);

        Assert.True(harness.ViewModel.Scale > 1d);
    }

    [Fact]
    public void Zoom_Out_DecreasesTheScale()
    {
        using var harness = Factory.Create();

        harness.ViewModel.Zoom(100, 0, 0);

        Assert.True(harness.ViewModel.Scale < 1d);
    }

    [Fact]
    public void Zoom_ClampsAtTheMinimum()
    {
        using var harness = Factory.Create();

        harness.ViewModel.Zoom(100_000, 0, 0);

        Assert.Equal(PreviewModalViewModel.MinZoom, harness.ViewModel.Scale);
    }

    [Fact]
    public void Zoom_ClampsAtTheMaximum()
    {
        using var harness = Factory.Create();

        harness.ViewModel.Zoom(-100_000, 0, 0);

        Assert.Equal(PreviewModalViewModel.MaxZoom, harness.ViewModel.Scale);
    }

    [Fact]
    public void Zoom_AtTheClamp_MakesNoChangeAtAll()
    {
        using var harness = Factory.Create();
        harness.ViewModel.Zoom(-100_000, 0, 0);
        harness.ViewModel.Pan(30, -40);
        var offsetX = harness.ViewModel.OffsetX;
        var offsetY = harness.ViewModel.OffsetY;

        harness.ViewModel.Zoom(-100_000, 250, 250);

        // The web's `if (newScale === oldScale) return`: the offsets must not drift while the
        // scale stands still, or the image walks off screen under a held wheel.
        Assert.Equal(PreviewModalViewModel.MaxZoom, harness.ViewModel.Scale);
        Assert.Equal(offsetX, harness.ViewModel.OffsetX);
        Assert.Equal(offsetY, harness.ViewModel.OffsetY);
    }

    [Fact]
    public void Zoom_IsCentredOnThePointer()
    {
        using var harness = Factory.Create();
        const double PointerX = 120d;
        const double PointerY = -80d;

        // The image point under the pointer, in image space, before and after.
        var beforeX = (PointerX - harness.ViewModel.OffsetX) / harness.ViewModel.Scale;
        var beforeY = (PointerY - harness.ViewModel.OffsetY) / harness.ViewModel.Scale;

        harness.ViewModel.Zoom(-200, PointerX, PointerY);

        var afterX = (PointerX - harness.ViewModel.OffsetX) / harness.ViewModel.Scale;
        var afterY = (PointerY - harness.ViewModel.OffsetY) / harness.ViewModel.Scale;

        // The point under the cursor stays under the cursor. This is the whole reason the web's
        // transform is ported literally rather than re-derived.
        Assert.Equal(beforeX, afterX, 1e-9);
        Assert.Equal(beforeY, afterY, 1e-9);
    }

    [Fact]
    public void Zoom_ReturningToFit_SnapsTheOffsetsToZero()
    {
        using var harness = Factory.Create();
        harness.ViewModel.Zoom(-300, 90, 60);
        Assert.NotEqual(0d, harness.ViewModel.OffsetX);

        harness.ViewModel.Zoom(300, 90, 60);

        // Exactly 1, not merely at or below it: FitTolerance is what pulls a return that lands a
        // few ulps out onto fit, and asserting <= 1 would pass with the snap deleted.
        Assert.Equal(1d, harness.ViewModel.Scale);
        Assert.Equal(0d, harness.ViewModel.OffsetX);
        Assert.Equal(0d, harness.ViewModel.OffsetY);
    }

    [Fact]
    public void Zoom_BelowFit_IsCentredAndNotPannable()
    {
        // Coordinator ruling on the spec's 0.1x floor: below fit the whole image is on screen, so
        // the state is centred and the drag does nothing. Fit comes back on double-click or 0.
        using var harness = Factory.Create();

        harness.ViewModel.Zoom(400, 100, 100);
        harness.ViewModel.Pan(50, 50);

        Assert.True(harness.ViewModel.Scale < 1d);
        Assert.Equal(0d, harness.ViewModel.OffsetX);
        Assert.Equal(0d, harness.ViewModel.OffsetY);
    }

    [Fact]
    public void Zoom_UsesTheWebWheelRate()
    {
        using var harness = Factory.Create();

        harness.ViewModel.Zoom(-100, 0, 0);

        // Math.exp(-deltaY * 0.0015), ported unchanged so a wheel notch feels the same as the web
        // application's.
        Assert.Equal(Math.Exp(100 * PreviewModalViewModel.WheelZoomRate), harness.ViewModel.Scale, 1e-12);
    }

    [Fact]
    public void Pan_WhileZoomedIn_MovesTheOffsets()
    {
        using var harness = Factory.Create();
        harness.ViewModel.Zoom(-300, 0, 0);

        harness.ViewModel.Pan(25, -15);

        Assert.Equal(25d, harness.ViewModel.OffsetX);
        Assert.Equal(-15d, harness.ViewModel.OffsetY);
    }

    [Fact]
    public void Pan_AtFit_DoesNothing()
    {
        using var harness = Factory.Create();

        harness.ViewModel.Pan(25, -15);

        Assert.Equal(0d, harness.ViewModel.OffsetX);
        Assert.Equal(0d, harness.ViewModel.OffsetY);
    }

    [Fact]
    public void Pan_WhileZoomedOut_DoesNothing()
    {
        using var harness = Factory.Create();
        harness.ViewModel.Zoom(400, 0, 0);

        harness.ViewModel.Pan(25, -15);

        Assert.Equal(0d, harness.ViewModel.OffsetX);
        Assert.Equal(0d, harness.ViewModel.OffsetY);
    }

    [Fact]
    public void Fit_ResetsScaleAndBothOffsets()
    {
        using var harness = Factory.Create();
        harness.ViewModel.Zoom(-300, 40, 40);
        harness.ViewModel.Pan(60, 60);

        harness.ViewModel.FitCommand.Execute(null);

        Assert.Equal(1d, harness.ViewModel.Scale);
        Assert.Equal(0d, harness.ViewModel.OffsetX);
        Assert.Equal(0d, harness.ViewModel.OffsetY);
    }

    [Fact]
    public void Navigation_ResetsTheTransform()
    {
        using var harness = Factory.Create();
        harness.ViewModel.Zoom(-300, 40, 40);
        harness.ViewModel.Pan(60, 60);

        harness.ViewModel.NextCommand.Execute(null);

        Assert.Equal(1d, harness.ViewModel.Scale);
        Assert.Equal(0d, harness.ViewModel.OffsetX);
        Assert.Equal(0d, harness.ViewModel.OffsetY);
    }

    // ------------------------------------------------------- rendering, error state, lifetime

    [Fact]
    public void Open_RequestsTheThumbnailAndThePreview()
    {
        using var harness = Factory.Create();

        // Ruling Q18: no manual trigger. Both are requested on open, and the thumbnail is what the
        // viewport shows until the preview arrives.
        Assert.Equal(
            [(harness.ViewModel.Current.FilePath, ThumbnailKind.Frame),
             (harness.ViewModel.Current.FilePath, ThumbnailKind.Preview)],
            harness.SlotRequests);
        Assert.True(harness.ViewModel.IsRendering);
    }

    [Fact]
    public void Open_RequestsThePreviewThroughThePreviewKind()
    {
        // ThumbnailKind.Preview is what routes the render to ThumbnailCache.EnsurePreview, which
        // is the one member that reads general.preview_resolution (0 meaning native). The
        // resolution itself is asserted there, in ThumbnailCacheTests, not here: a copy of it in
        // this view-model would be a second answer to "how big is a preview".
        using var harness = Factory.Create();

        Assert.Contains((harness.ViewModel.Current.FilePath, ThumbnailKind.Preview), harness.SlotRequests);
    }

    [AvaloniaFact]
    public async Task Preview_ShowsTheThumbnailUntilTheFullPreviewArrives()
    {
        using var harness = Factory.Create();
        var path = harness.ViewModel.Current.FilePath;

        harness.Probe.Complete(path, preview: false);
        await harness.SettleThumbnailAsync();

        Assert.NotNull(harness.ViewModel.Thumbnail.Image);
        Assert.Null(harness.ViewModel.Preview.Image);
        Assert.Same(harness.ViewModel.Thumbnail.Image, harness.ViewModel.DisplayImage);
        Assert.False(harness.ViewModel.ShowsPlaceholder);

        harness.Probe.Complete(path, preview: true);
        await harness.SettlePreviewAsync();

        Assert.Same(harness.ViewModel.Preview.Image, harness.ViewModel.DisplayImage);
        Assert.False(harness.ViewModel.IsRendering);
    }

    [AvaloniaFact]
    public async Task Preview_RenderFails_SetsErrorTextAndOffersRetry()
    {
        using var harness = Factory.Create();

        harness.Probe.CompleteWithNull(harness.ViewModel.Current.FilePath, preview: true);
        await harness.SettlePreviewAsync();

        // preview.py answers a render failure with a 500 and the web modal shows a Retry; the port
        // leaves a message and the command rather than an empty window.
        Assert.True(harness.ViewModel.HasError);
        Assert.NotNull(harness.ViewModel.ErrorText);
        Assert.True(harness.ViewModel.RetryCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Retry_RequestsThePreviewAgain()
    {
        using var harness = Factory.Create();
        var path = harness.ViewModel.Current.FilePath;
        harness.Probe.CompleteWithNull(path, preview: true);
        await harness.SettlePreviewAsync();
        Assert.Equal(1, harness.Probe.StartedCount(preview: true, path));

        harness.Probe.Reset(path, preview: true);
        harness.ViewModel.RetryCommand.Execute(null);
        harness.Probe.Complete(path, preview: true);
        await harness.SettlePreviewAsync();

        Assert.Equal(2, harness.Probe.StartedCount(preview: true, path));
        Assert.Null(harness.ViewModel.ErrorText);
        Assert.NotNull(harness.ViewModel.Preview.Image);
    }

    [AvaloniaFact]
    public async Task Preview_UnrenderableFrame_StillOpensTheHeaderPanel()
    {
        // Spec 6.2.6: "the preview modal then shows the frame's header panel with a placeholder in
        // place of the image".
        using var harness = Factory.Create();
        var path = harness.ViewModel.Current.FilePath;
        harness.Probe.CompleteWithNull(path, preview: true);
        harness.Probe.CompleteWithNull(path, preview: false);
        await harness.SettleAsync();

        harness.ViewModel.ToggleHeaderPanelCommand.Execute(null);
        await harness.ViewModel.Headers!.PendingLoad!.WaitAsync(Factory.Budget);

        Assert.True(harness.ViewModel.ShowsPlaceholder);
        Assert.True(harness.ViewModel.IsHeaderPanelVisible);
        Assert.NotNull(harness.ViewModel.Headers);
    }

    [AvaloniaFact]
    public async Task Navigation_DisposesThePreviousBitmaps()
    {
        using var harness = Factory.Create();
        var path = harness.ViewModel.Current.FilePath;
        harness.Probe.Complete(path, preview: false);
        harness.Probe.Complete(path, preview: true);
        await harness.SettleAsync();

        var previousThumbnail = harness.ViewModel.Thumbnail;
        var previousPreview = harness.ViewModel.Preview;
        var thumbnailBitmap = (Factory.TrackingBitmap)previousThumbnail.Image!;
        var previewBitmap = (Factory.TrackingBitmap)previousPreview.Image!;

        harness.ViewModel.NextCommand.Execute(null);

        // The C# equivalent of the web's URL.revokeObjectURL: stepping through five hundred frames
        // otherwise holds five hundred decoded bitmaps.
        Assert.True(thumbnailBitmap.IsDisposed);
        Assert.True(previewBitmap.IsDisposed);
        Assert.Null(previousThumbnail.Image);
        Assert.Null(previousPreview.Image);
        Assert.NotSame(previousPreview, harness.ViewModel.Preview);
    }

    [AvaloniaFact]
    public async Task Navigation_WithdrawsTheInFlightRequestForThePreviousFrame()
    {
        using var harness = Factory.Create();
        var first = harness.ViewModel.Current.FilePath;
        var abandoned = harness.ViewModel.Preview;

        harness.ViewModel.NextCommand.Execute(null);
        harness.Probe.Complete(first, preview: true);
        await abandoned.PendingLoad!.WaitAsync(Factory.Budget);

        // A completion for a frame the user has already left never becomes the modal's image.
        Assert.Null(abandoned.Image);
        Assert.Null(harness.ViewModel.Preview.Image);
    }

    [AvaloniaFact]
    public async Task Dispose_DisposesBothSlots()
    {
        using var harness = Factory.Create();
        var path = harness.ViewModel.Current.FilePath;
        harness.Probe.Complete(path, preview: false);
        harness.Probe.Complete(path, preview: true);
        await harness.SettleAsync();
        var thumbnailBitmap = (Factory.TrackingBitmap)harness.ViewModel.Thumbnail.Image!;
        var previewBitmap = (Factory.TrackingBitmap)harness.ViewModel.Preview.Image!;

        harness.ViewModel.Dispose();

        Assert.True(thumbnailBitmap.IsDisposed);
        Assert.True(previewBitmap.IsDisposed);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        using var harness = Factory.Create();

        harness.ViewModel.Dispose();
        harness.ViewModel.Dispose();

        Assert.Null(harness.ViewModel.Thumbnail.Image);
    }

    [Fact]
    public async Task HeaderPanel_IsBuiltOnFirstShowOnly()
    {
        using var harness = Factory.Create();

        harness.ViewModel.ToggleHeaderPanelCommand.Execute(null);
        var panel = harness.ViewModel.Headers;
        harness.ViewModel.ToggleHeaderPanelCommand.Execute(null);
        harness.ViewModel.ToggleHeaderPanelCommand.Execute(null);
        await panel!.PendingLoad!.WaitAsync(Factory.Budget);

        // The query runs once per frame, not once per toggle.
        Assert.Same(panel, harness.ViewModel.Headers);
        Assert.Equal([harness.ViewModel.Current.ImageId], harness.HeaderQueries);
    }

    [Fact]
    public async Task HeaderPanel_StaysOpenAcrossNavigation()
    {
        using var harness = Factory.Create();
        harness.ViewModel.ToggleHeaderPanelCommand.Execute(null);
        var first = harness.ViewModel.Headers;

        harness.ViewModel.NextCommand.Execute(null);
        await first!.PendingLoad!.WaitAsync(Factory.Budget);
        await harness.ViewModel.Headers!.PendingLoad!.WaitAsync(Factory.Budget);

        // A panel the user opened stays open across frames, which is what makes it useful for
        // comparing; the content is the new frame's.
        Assert.True(harness.ViewModel.IsHeaderPanelVisible);
        Assert.NotSame(first, harness.ViewModel.Headers);
        Assert.Equal(
            [harness.ViewModel.Frames[0].ImageId, harness.ViewModel.Frames[1].ImageId],
            harness.HeaderQueries);
    }

    [Fact]
    public async Task HeaderPanel_RendersTheDerivedMetricsOfThePreviewedFrame()
    {
        // Review item 1. FrameHeadersQuery returns raw_headers, provenance and the two FWHM values
        // only, so the Derived metrics section (spec 12.4, spec 7.3) is rendered from the frame's
        // read model. Handing the panel no row left the section empty and every provenance string
        // invisible.
        using var harness = Factory.Create(headers: _ => Factory.Headers());

        harness.ViewModel.ToggleHeaderPanelCommand.Execute(null);
        await harness.ViewModel.Headers!.PendingLoad!.WaitAsync(Factory.Budget);

        var panel = harness.ViewModel.Headers!;
        Assert.True(panel.HasDerivedMetrics);
        Assert.Contains(panel.DerivedMetrics, metric => metric.Label == "HFR" && metric.ValueText == "2.41");

        // Spec 7.3's provenance string, which is the half of the section that only exists when the
        // row and the provenance document are read together.
        Assert.Contains(panel.DerivedMetrics, metric => metric.Provenance == "HFR");
    }

    [Fact]
    public async Task HeaderPanel_ReadCompletingAfterDispose_PublishesNothing()
    {
        // Review item 2. The same lifetime guard FrameTableViewModel gives its rows' panels: a read
        // still in flight when the modal closes must drop its result rather than post it to a
        // dispatcher that may no longer be running.
        var release = new SemaphoreSlim(0);
        using var harness = Factory.Create(headers: _ =>
        {
            Assert.True(release.Wait(Factory.Budget));
            return Factory.Headers();
        });

        harness.ViewModel.ToggleHeaderPanelCommand.Execute(null);
        var panel = harness.ViewModel.Headers!;

        harness.ViewModel.Dispose();
        release.Release();
        await panel.PendingLoad!.WaitAsync(Factory.Budget);

        // Publish never ran: no headers, no derived metrics, and the spinner was never cleared.
        Assert.Empty(panel.Headers);
        Assert.Empty(panel.DerivedMetrics);
        Assert.True(panel.IsLoading);
    }

    // ------------------------------------------------------------------------- standing rules

    [Fact]
    public void ViewModel_HoldsNoBrushOtherThanImmutableSolidColorBrush()
    {
        foreach (var property in typeof(PreviewModalViewModel).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (typeof(IBrush).IsAssignableFrom(property.PropertyType))
            {
                Assert.True(
                    property.PropertyType == typeof(ImmutableSolidColorBrush),
                    $"{property.Name} is a brush that is not an ImmutableSolidColorBrush (spec 14).");
            }
        }
    }

    [Fact]
    public void ViewModel_HasNoManualStretchControl()
    {
        // Spec 11.5's last line stated as a test: "The preview is always autostretched. There are
        // no manual stretch controls in v1." v2's first manual control is then a deliberate change
        // and not a drift.
        string[] forbidden = ["stretch", "shadow", "midtone", "gamma", "highlight", "blackpoint"];

        var offenders = typeof(PreviewModalViewModel)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Select(member => member.Name)
            .Where(name => forbidden.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.True(offenders.Length == 0, $"Manual stretch controls are out of scope in v1: {string.Join(", ", offenders)}");
    }
}
