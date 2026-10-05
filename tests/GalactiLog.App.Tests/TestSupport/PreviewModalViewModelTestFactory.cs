using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Preview;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.TestSupport;

// Phase 8 Task 7. The one place App.Tests builds a PreviewModalViewModel, so a constructor change
// is one edit rather than forty. Same shape as MergeDialogViewModelTestFactory: no window, no
// dispatcher and no database, every collaborator a lambda, and the post seam running its closure
// inline.
//
// The worker is real, because ThumbnailSlotViewModel takes the concrete type; what is faked is the
// render behind it, through a per-(kind, path) gate so a request can be held pending while the
// modal is driven. Nothing here reads a JPEG: the decode is a delegate.
internal static class PreviewModalViewModelTestFactory
{
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    public static PreviewFrameViewModel Frame(int number, bool withRow = true)
    {
        var id = Guid.Parse($"00000000-0000-0000-0000-{number:000000000000}");
        var path = $@"C:\Astro\M 31\2025-12-07\frame_{number:0000}.fits";
        var name = $"frame_{number:0000}.fits";

        // Through PreviewFrameViewModel.From, not the constructor: From is the one place the frame
        // table's row and this type meet, and it is where spec 11.5's metadata strip is built. A
        // frame built the other way is exactly the caller that holds no row and therefore no strip.
        return withRow
            ? PreviewFrameViewModel.From(Rows(Row(id, path, name))[0])
            : new(id, path, name);
    }

    public static IReadOnlyList<PreviewFrameViewModel> Frames(int count)
        => [.. Rows([.. Enumerable.Range(1, count).Select(ReadModel)]).Select(PreviewFrameViewModel.From)];

    /// <summary>The frame table's own rows for a set of read models, which is what the preview
    /// modal is opened from. Built through <c>FrameTableViewModel</c> rather than by constructing
    /// rows directly, so a change to the row's construction is the table's problem and not this
    /// factory's.</summary>
    public static IReadOnlyList<FrameRowViewModel> Rows(params FrameRow[] frames)
    {
        var display = new DisplaySettings();
        foreach (var key in display.Groups.Keys.ToList())
        {
            display.Groups[key] = display.Groups[key] with { Enabled = true };
        }

        var table = new FrameTableViewModel(
            frames,
            display,
            new DisplayColumnWriter(() => display, _ => { }),
            new ShellIntegration(copyText: _ => Task.CompletedTask, start: _ => null),
            openPreview: null,
            new GeneralSettings { Timezone = "UTC", Use24HTime = true },
            getHeaders: _ => null);

        return [.. table.Rows];
    }

    private static FrameRow ReadModel(int number)
    {
        var id = Guid.Parse($"00000000-0000-0000-0000-{number:000000000000}");
        return Row(id, $@"C:\Astro\M 31\2025-12-07\frame_{number:0000}.fits", $"frame_{number:0000}.fits");
    }

    /// <summary>A frame read model with every metric the header panel's Derived metrics section
    /// renders, so the section is non-empty when a test asks for it.</summary>
    public static FrameRow Row(
        Guid imageId, string filePath, string fileName, FrameGrading? grading = null) => new(
        ImageId: imageId,
        FilePath: filePath,
        FileName: fileName,
        CaptureDate: new DateTime(2025, 12, 7, 20, 0, 0, DateTimeKind.Utc),
        FilterUsed: "Ha",
        ExposureTime: 300d,
        MedianHfr: 2.41d,
        Eccentricity: 0.42d,
        Fwhm: 1.9d,
        DetectedStars: 812,
        GuidingRmsArcsec: 0.55d,
        GuidingRmsRaArcsec: 0.31d,
        GuidingRmsDecArcsec: 0.45d,
        GuidingRmsSource: "PHD2",
        AduMean: 1200d,
        AduMedian: 1180d,
        AduStdev: 90d,
        AduMin: 300,
        AduMax: 65_000,
        FocuserPosition: 18_500,
        FocuserTemp: 4.2d,
        AmbientTemp: 3.1d,
        DewPoint: -1.4d,
        Humidity: 61d,
        Pressure: 1012d,
        WindSpeed: 2.3d,
        WindDirection: 190d,
        WindGust: 4.1d,
        CloudCover: 5d,
        SkyQuality: 21.3d,
        Airmass: 1.12d,
        PierSide: "East",
        RotatorPosition: 90d,
        SensorTemp: -10d,
        CameraGain: 100,
        Rig: "TS 130 / ASI2600MM",
        IsHfrOutlier: false,
        IsEccentricityOutlier: false,
        Grading: grading);

    /// <summary>A header read with a provenance entry, which is what spec 7.3 pairs each derived
    /// metric with.</summary>
    public static FrameHeaders Headers() => new(
        [new HeaderEntry("OBJECT", ["M 31"]), new HeaderEntry("COMMENT", ["one", "two"])],
        new Dictionary<string, string>(StringComparer.Ordinal) { ["median_hfr"] = "HFR" },
        MedianFwhm: 3.2d,
        Fwhm: 1.9d);

    /// <param name="renderOnNavigate">The general document's
    /// <c>preview_render_on_navigate</c> as the harness starts holding it. Defaults to the shipped
    /// default, which is on (coordinator override of 2026-09-17).</param>
    public static Harness Create(
        IReadOnlyList<PreviewFrameViewModel>? frames = null,
        int index = 0,
        bool? isWindows = null,
        Func<Guid, FrameHeaders?>? headers = null,
        bool renderOnNavigate = true)
        => new(frames ?? Frames(3), index, isWindows, headers, renderOnNavigate);

    internal sealed class Harness : IDisposable
    {
        private readonly List<ThumbnailSlotViewModel> _slots = [];

        public Harness(
            IReadOnlyList<PreviewFrameViewModel> frames,
            int index,
            bool? isWindows,
            Func<Guid, FrameHeaders?>? headers,
            bool renderOnNavigate = true)
        {
            StoredRenderOnNavigate = renderOnNavigate;
            Worker = new ThumbnailWorker(Probe.Frame, Probe.Preview, post: action => action());

            Shell = new ShellIntegration(
                copyText: text =>
                {
                    Copies.Add(text);
                    return Task.CompletedTask;
                },
                start: info =>
                {
                    Launched.Add(info);
                    return null;
                });

            ViewModel = new PreviewModalViewModel(
                frames,
                index,
                createSlot: (path, kind) =>
                {
                    var slot = new ThumbnailSlotViewModel(
                        path,
                        Worker,
                        _ => [0x01],
                        kind,
                        decode: _ => new TrackingBitmap(),
                        post: action => action());
                    _slots.Add(slot);
                    SlotRequests.Add((path, kind));
                    return slot;
                },
                Shell,
                getHeaders: id =>
                {
                    HeaderQueries.Add(id);
                    return headers?.Invoke(id);
                },
                isWindows: isWindows is null ? null : () => isWindows.Value,
                post: action => action(),
                // Spec 11.5's flag, through the two delegates AppHost wires to
                // SettingsStore.GetGeneral and MutateGeneral. The getter reads the field on every
                // call, so a test can change the stored value behind the view-model's back and
                // prove the flag is read on each step rather than captured at open.
                getRenderOnNavigate: () => StoredRenderOnNavigate,
                setRenderOnNavigate: value =>
                {
                    StoredRenderOnNavigate = value;
                    RenderOnNavigateWrites.Add(value);
                },
                // SettingsStore.GeneralChanged, as AppHost wires it (phase review P3-6). The
                // getter already reads the live document; this is what makes an open checkbox
                // show a write made anywhere else rather than waiting to be clicked.
                subscribeGeneralChanged: handler => GeneralChanged += handler,
                unsubscribeGeneralChanged: handler => GeneralChanged -= handler);

            ViewModel.CloseRequested += (_, _) => Closes++;
        }

        /// <summary>Stands in for <c>SettingsStore.GeneralChanged</c>, so a case can write the
        /// general document behind the modal's back and raise the event the store would.</summary>
        public event EventHandler<GeneralSettings>? GeneralChanged;

        /// <summary>Raises the store's event with the current stored flag, as a second writer
        /// would.</summary>
        public void RaiseGeneralChanged()
            => GeneralChanged?.Invoke(
                this,
                new GeneralSettings { PreviewRenderOnNavigate = StoredRenderOnNavigate });

        /// <summary>Whether anything is still subscribed to the store's event, which is what a
        /// disposed modal must leave behind.</summary>
        public bool HasGeneralChangedSubscriber => GeneralChanged is not null;

        public RenderProbe Probe { get; } = new();

        public ThumbnailWorker Worker { get; }

        public ShellIntegration Shell { get; }

        public PreviewModalViewModel ViewModel { get; }

        /// <summary>Every slot the modal asked for, in order, with the kind it asked for. Two per
        /// frame: spec 11.4's general.thumbnail_width thumbnail (default 800) and spec 11.5's
        /// preview.</summary>
        public List<(string Path, ThumbnailKind Kind)> SlotRequests { get; } = [];

        public List<ProcessStartInfo> Launched { get; } = [];

        public List<string> Copies { get; } = [];

        public List<Guid> HeaderQueries { get; } = [];

        /// <summary>The general document's <c>preview_render_on_navigate</c>, as the store would
        /// hold it. Settable so a test can change it without going through the view-model.
        /// </summary>
        public bool StoredRenderOnNavigate { get; set; }

        /// <summary>Every write the modal made through the setter delegate, in order, which is
        /// what proves the checkbox reaches <c>MutateGeneral</c>.</summary>
        public List<bool> RenderOnNavigateWrites { get; } = [];

        public int Closes { get; private set; }

        /// <summary>Awaits both of the current frame's loads. The coordinator addendum on Task 5:
        /// worker idle means "no render in flight", not "every slot updated", so this awaits the
        /// slots' own completions and never the worker's idle.</summary>
        public async Task SettleAsync()
        {
            await SettleThumbnailAsync();
            await SettlePreviewAsync();
        }

        /// <summary>Awaits the general.thumbnail_width thumbnail alone (default 800), for the
        /// tests that assert it is what the viewport shows until the preview arrives.</summary>
        public Task SettleThumbnailAsync() => AwaitPending(ViewModel.Thumbnail);

        public Task SettlePreviewAsync() => AwaitPending(ViewModel.Preview);

        public void Dispose()
        {
            ViewModel.Dispose();
            Worker.Dispose();
            foreach (var slot in _slots)
            {
                slot.Dispose();
            }
        }

        private static async Task AwaitPending(ThumbnailSlotViewModel slot)
        {
            Assert.NotNull(slot.PendingLoad);
            await slot.PendingLoad!.WaitAsync(Budget);
        }
    }

    // A bitmap that records its own disposal, the same shape ThumbnailSlotViewModelTests uses:
    // Avalonia's Bitmap is not sealed and its Dispose is virtual, so bitmap ownership can be
    // asserted without reading a JPEG. Needs the headless platform, so every test that completes a
    // render is an [AvaloniaFact].
    internal sealed class TrackingBitmap : Bitmap
    {
        public TrackingBitmap()
            : base(new MemoryStream([0x01, 0x02, 0x03, 0x04]))
        {
        }

        public bool IsDisposed { get; private set; }

        public override void Dispose()
        {
            IsDisposed = true;
            base.Dispose();
        }
    }

    // Keyed on the kind as well as the path: the modal asks for a thumbnail and a preview of the
    // same frame at the same moment, and a path-only gate would complete both at once.
    internal sealed class RenderProbe
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<(bool Preview, string Path), TaskCompletionSource<string?>> _gates = [];

        public ConcurrentQueue<(bool Preview, string Path)> Started { get; } = new();

        public string? Frame(string path, CancellationToken ct) => Render(preview: false, path, ct);

        public string? Preview(string path, CancellationToken ct) => Render(preview: true, path, ct);

        public void Complete(string path, bool preview)
        {
            lock (_gate)
            {
                GateLocked(preview, path).TrySetResult(
                    (preview ? "previews/" : "frames/") + path + ".jpg");
            }
        }

        public void CompleteWithNull(string path, bool preview)
        {
            lock (_gate)
            {
                GateLocked(preview, path).TrySetResult(null);
            }
        }

        /// <summary>Drops a completed gate so the same frame can be rendered a second time, which
        /// is what Retry does.</summary>
        public void Reset(string path, bool preview)
        {
            lock (_gate)
            {
                _gates.Remove((preview, path));
            }
        }

        public int StartedCount(bool preview, string path)
            => Started.Count(entry => entry.Preview == preview && entry.Path == path);

        private string? Render(bool preview, string path, CancellationToken ct)
        {
            TaskCompletionSource<string?> gate;
            lock (_gate)
            {
                Started.Enqueue((preview, path));
                gate = GateLocked(preview, path);
            }
            return gate.Task.WaitAsync(ct).GetAwaiter().GetResult();
        }

        private TaskCompletionSource<string?> GateLocked(bool preview, string path)
        {
            if (!_gates.TryGetValue((preview, path), out var gate))
            {
                gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _gates[(preview, path)] = gate;
            }
            return gate;
        }
    }
}
