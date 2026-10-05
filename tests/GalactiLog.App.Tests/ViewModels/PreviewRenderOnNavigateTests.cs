using Avalonia.Headless.XUnit;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.Preview;
using GalactiLog.Core.Settings;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.PreviewModalViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

// Design-spec 11.5's "Render full preview on navigation" (PAR-011), bound to
// general.preview_render_on_navigate (spec 5.8.1).
//
// The default ships ON, by the coordinator override of 2026-09-17 against the spec table's false:
// the port has rendered the full preview on every navigation step since Phase 8 ruling Q18, and a
// false default would silently invert shipped behaviour. The checkbox is what turns it off, and
// every case below that assumed false in the brief is written against true instead.
//
// A step's render needs no cancellation of its own. GoTo disposes the previous slot pair before it
// loads the new one, and disposing a slot withdraws whatever it still had in flight, which is what
// SteppingAgainMidRender_WithdrawsTheFirstRender proves.
public class PreviewRenderOnNavigateTests
{
    [Fact]
    public void AFreshProfile_HasItOn()
    {
        Assert.True(new GeneralSettings().PreviewRenderOnNavigate);

        using var harness = Factory.Create();

        Assert.True(harness.ViewModel.RenderOnNavigate);
    }

    [Fact]
    public void Off_AStep_LoadsTheThumbnailOnly()
    {
        using var harness = Factory.Create(renderOnNavigate: false);

        harness.ViewModel.NextCommand.Execute(null);

        Assert.True(harness.ViewModel.Thumbnail.IsLoading);
        Assert.False(harness.ViewModel.Preview.IsLoading);
        Assert.Null(harness.ViewModel.Preview.PendingLoad);
        Assert.False(harness.ViewModel.IsRendering);
    }

    [Fact]
    public void On_AStep_LoadsThePreviewToo()
    {
        using var harness = Factory.Create(renderOnNavigate: true);

        harness.ViewModel.NextCommand.Execute(null);

        Assert.True(harness.ViewModel.Thumbnail.IsLoading);
        Assert.True(harness.ViewModel.Preview.IsLoading);
        Assert.NotNull(harness.ViewModel.Preview.PendingLoad);
        Assert.True(harness.ViewModel.IsRendering);
    }

    [Fact]
    public void TheOpen_AlwaysRendersThePreview()
    {
        // Spec 11.5's Open row is unchanged: the modal renders at general.preview_resolution on
        // open whatever the flag says. A modal that opened to a thumbnail is a different defect.
        using var harness = Factory.Create(renderOnNavigate: false);

        Assert.Equal(
            [(harness.ViewModel.Current.FilePath, ThumbnailKind.Frame),
             (harness.ViewModel.Current.FilePath, ThumbnailKind.Preview)],
            harness.SlotRequests);
        Assert.True(harness.ViewModel.Preview.IsLoading);
        Assert.True(harness.ViewModel.IsRendering);
    }

    [Fact]
    public void TheFlag_IsReadOnEachStep_NotCapturedAtOpen()
    {
        // The case that catches the obvious implementation: a bool field set in the constructor
        // passes every other case in this file. The stored value is changed behind the
        // view-model's back, so nothing but a per-step read can see it.
        using var harness = Factory.Create(renderOnNavigate: true);

        harness.StoredRenderOnNavigate = false;
        harness.ViewModel.NextCommand.Execute(null);

        Assert.False(harness.ViewModel.Preview.IsLoading);
        Assert.Null(harness.ViewModel.Preview.PendingLoad);

        harness.StoredRenderOnNavigate = true;
        harness.ViewModel.NextCommand.Execute(null);

        Assert.True(harness.ViewModel.Preview.IsLoading);
    }

    [Fact]
    public void TogglingIt_WritesTheGeneralDocument()
    {
        using var harness = Factory.Create(renderOnNavigate: true);

        harness.ViewModel.RenderOnNavigate = false;

        Assert.Equal([false], harness.RenderOnNavigateWrites);
        Assert.False(harness.StoredRenderOnNavigate);
        Assert.False(harness.ViewModel.RenderOnNavigate);

        harness.ViewModel.RenderOnNavigate = true;

        Assert.Equal([false, true], harness.RenderOnNavigateWrites);
        Assert.True(harness.StoredRenderOnNavigate);
    }

    [Fact]
    public void SettingItToWhatItAlreadyIs_WritesNothing()
    {
        using var harness = Factory.Create(renderOnNavigate: true);

        harness.ViewModel.RenderOnNavigate = true;

        Assert.Empty(harness.RenderOnNavigateWrites);
    }

    [AvaloniaFact]
    public async Task SteppingAgainMidRender_WithdrawsTheFirstRender()
    {
        // No cancellation was added for this: GoTo already disposes the previous pair, and
        // disposing a slot withdraws whatever it still had in flight. The web's requestZoom has no
        // AbortController and can paint the previous frame's blob into the new index.
        using var harness = Factory.Create(renderOnNavigate: true);
        var first = harness.ViewModel.Current.FilePath;
        var abandoned = harness.ViewModel.Preview;

        harness.ViewModel.NextCommand.Execute(null);
        var second = harness.ViewModel.Current.FilePath;
        var arriving = harness.ViewModel.Preview;

        // The held-open first render is released after the step.
        harness.Probe.Complete(first, preview: true);
        await abandoned.PendingLoad!.WaitAsync(Factory.Budget);

        Assert.Null(abandoned.Image);
        Assert.Null(harness.ViewModel.Preview.Image);

        // The image that does land belongs to the frame on screen.
        harness.Probe.Complete(second, preview: true);
        await harness.SettlePreviewAsync();

        Assert.NotSame(abandoned, arriving);
        Assert.Same(arriving, harness.ViewModel.Preview);
        Assert.NotNull(harness.ViewModel.Preview.Image);
        Assert.Same(harness.ViewModel.Preview.Image, harness.ViewModel.DisplayImage);
    }

    [Fact]
    public void TogglingItOn_RendersTheCurrentFrame()
    {
        // Coordinator ruling Q13: match the web's
        // "if (!zoomed() && !loading()) requestZoom()". A reader who ticks the box is asking to
        // see detail now, and waiting for the next step would read as a control that did nothing.
        using var harness = Factory.Create(renderOnNavigate: false);
        harness.ViewModel.NextCommand.Execute(null);
        Assert.False(harness.ViewModel.Preview.IsLoading);

        harness.ViewModel.RenderOnNavigate = true;

        Assert.True(harness.ViewModel.Preview.IsLoading);
        Assert.NotNull(harness.ViewModel.Preview.PendingLoad);
        Assert.True(harness.ViewModel.IsRendering);
    }

    [AvaloniaFact]
    public async Task TogglingItOn_WithAPreviewAlreadyOnScreen_RequestsNoSecondRender()
    {
        using var harness = Factory.Create(renderOnNavigate: false);
        var path = harness.ViewModel.Current.FilePath;
        harness.Probe.Complete(path, preview: true);
        await harness.SettlePreviewAsync();
        Assert.Equal(1, harness.Probe.StartedCount(preview: true, path));

        harness.ViewModel.RenderOnNavigate = true;

        Assert.Equal(1, harness.Probe.StartedCount(preview: true, path));
    }

    [Fact]
    public void ItChangesNoZoomOrFitBehaviour()
    {
        // Spec 11.5's closing sentence: the zoom range, the fit gesture and the section 11.3 cache
        // are unaffected. The two gestures are asserted here at both flag settings so the branch
        // cannot have reached them.
        foreach (var flag in (bool[])[true, false])
        {
            using var harness = Factory.Create(renderOnNavigate: flag);

            harness.ViewModel.Zoom(-300, 40, 40);
            var zoomed = harness.ViewModel.Scale;
            var beforePan = harness.ViewModel.OffsetX;
            harness.ViewModel.Pan(60, 60);

            Assert.True(zoomed > 1d);
            Assert.Equal(beforePan + 60d, harness.ViewModel.OffsetX);

            harness.ViewModel.FitCommand.Execute(null);

            Assert.Equal(1d, harness.ViewModel.Scale);
            Assert.Equal(0d, harness.ViewModel.OffsetX);
            Assert.Equal(0d, harness.ViewModel.OffsetY);

            // The step still resets the transform, whichever way the flag is set.
            harness.ViewModel.Zoom(-300, 40, 40);
            harness.ViewModel.NextCommand.Execute(null);

            Assert.Equal(1d, harness.ViewModel.Scale);
        }
    }

    [Fact]
    public void ASecondWriter_RepaintsTheCheckbox()
    {
        // Phase review P3-6. The getter reads the live document, so a write made anywhere else is
        // already in effect on the next navigation step; before this, the only raise was the
        // checkbox's own setter, so an open modal kept drawing the old state until it was clicked.
        // Nothing else writes general.preview_render_on_navigate today; a Settings row for it
        // would be the second writer.
        using var harness = Factory.Create(renderOnNavigate: true);

        List<string?> raised = [];
        harness.ViewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        harness.StoredRenderOnNavigate = false;
        harness.RaiseGeneralChanged();

        Assert.Contains(nameof(PreviewModalViewModel.RenderOnNavigate), raised);
        Assert.False(harness.ViewModel.RenderOnNavigate);

        // And the modal lets go of the process-wide store when it closes.
        harness.ViewModel.Dispose();
        Assert.False(harness.HasGeneralChangedSubscriber);
    }

    [Fact]
    public void TheTwoDelegates_ArePassedTogetherOrNotAtAll()
    {
        // Task 7 review P3. They were independently optional, so a caller supplying only the
        // getter got a checkbox that raised, rendered and then snapped back to the document's
        // unchanged value on the next read, and a caller supplying only the setter wrote a value
        // the box never showed. Latent, because AppHost passes both; refused now rather than
        // silently half wired.
        var shell = new ShellIntegration(
            copyText: _ => Task.CompletedTask,
            start: _ => null);

        Assert.Throws<ArgumentException>(() => new PreviewModalViewModel(
            Factory.Frames(3),
            0,
            createSlot: (_, _) => throw new InvalidOperationException("not reached"),
            shell,
            getHeaders: _ => null,
            getRenderOnNavigate: () => true));

        Assert.Throws<ArgumentException>(() => new PreviewModalViewModel(
            Factory.Frames(3),
            0,
            createSlot: (_, _) => throw new InvalidOperationException("not reached"),
            shell,
            getHeaders: _ => null,
            setRenderOnNavigate: _ => { }));
    }

    [Fact]
    public void TheFlag_AddsNoCommand()
    {
        // TRACKING section 6 item 13: RelayCommand.Execute ignores CanExecute, so a new command
        // would need its own guard in its body. This task adds none; the checkbox is a two-way
        // bound property.
        Assert.Null(typeof(PreviewModalViewModel).GetProperty("RenderOnNavigateCommand"));
        Assert.Null(typeof(PreviewModalViewModel).GetProperty("ToggleRenderOnNavigateCommand"));
    }
}
