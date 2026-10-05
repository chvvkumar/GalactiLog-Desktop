using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.ViewModels.TargetDetail;

/// <summary>
/// One rig of a night (spec 12.4's "A multi-rig night", PAR-004) and its box in the night header's
/// thumbnail strip (PAR-008). A single-rig night has exactly one of these, so the strip has one box
/// and the markup carries no branch.
/// </summary>
/// <remarks>
/// <para>
/// This type owns spec 12.4's decode walk and nothing else about the picture. The ranking is
/// <c>SessionDetailQuery</c>'s, which hands over up to three absolute paths in ranked order; the
/// walk loads the first, and when that settles with no image it loads the second, then the third,
/// then stops and leaves the placeholder. A frame skipped this way is not marked, flagged or
/// written to: the walk is a read that did not work, not a verdict about the file.
/// </para>
/// <para>
/// The walk is on demand, not on construction (coordinator ruling, 2026-09-18, with the user's B1
/// ruling). The strip lives inside the "Night detail" section, which is closed on a fresh profile,
/// and a closed section must cost no render as well as no layout: the card calls
/// <see cref="StartThumbnail"/> when that section first opens, or at once if it is already open
/// when the night's rigs publish. Once started it stays started, so opening, closing and reopening
/// the section decodes nothing a second time and a cache hit costs nothing either way. This is
/// spec 11.4's own rule for a thumbnail that is not on screen.
/// </para>
/// <para>
/// Nothing here reads a file, opens a bitmap or names SkiaSharp. The slot factory is
/// <c>ThumbnailCache.EnsureFrame</c> through <c>ThumbnailWorker</c>, which already renders under
/// <c>frames/</c> at <c>general.thumbnail_width</c>, already goes through <c>AppWriter</c> and
/// already serves a cache hit without re-rendering (questions.md Q4).
/// </para>
/// </remarks>
public sealed partial class RigViewModel : ObservableObject, IDisposable
{
    /// <summary>Spec 12.4: "At most three frames are tried". The cap holds whatever the candidate
    /// list's length is, so a shorter list stops sooner and a longer one is still three.</summary>
    public const int MaxDecodeAttempts = 3;

    private readonly IReadOnlyList<string> _candidates;
    private readonly Func<string, ThumbnailSlotViewModel>? _createThumbnail;
    private readonly Action? _openPreview;

    private int _attempts;
    private bool _disposed;
    private bool _started;

    /// <param name="group">The rig group the session query returned.</param>
    /// <param name="createThumbnail">Builds a slot for one frame path, normally
    /// <c>AppHost</c>'s frame-thumbnail factory. Null leaves the box empty and the placeholder
    /// showing, which is what a test that is not about thumbnails wants.</param>
    /// <param name="openPreview">Spec 12.4: "Clicking a thumbnail opens the preview modal at that
    /// frame, inside the night's frame list". The card supplies it and routes it through the frame
    /// table's own select-and-preview seam, so there is no second preview entry point.</param>
    public RigViewModel(
        RigGroup group,
        Func<string, ThumbnailSlotViewModel>? createThumbnail = null,
        Action? openPreview = null)
    {
        ArgumentNullException.ThrowIfNull(group);

        Index = group.Index;
        Label = group.Label;
        FrameCount = group.FrameCount;
        FrameCountText = RigLabelRowViewModel.FramesText(group.FrameCount);
        IntegrationText = MetricText.Format(group.IntegrationSeconds / 3600d, "0.0");
        ReferenceImageId = group.ReferenceImageId;

        _candidates = group.ReferenceCandidates ?? [];
        _createThumbnail = createThumbnail;
        _openPreview = openPreview;
    }

    /// <summary>The rig's position in the night's first-capture order. The rig index every surface
    /// of the page reads, including section 13's dash table.</summary>
    public int Index { get; }

    /// <summary>The canonical <c>"{telescope} / {camera}"</c> label, the one spelling the frame
    /// rows, the insight prefixes and the pills all carry.</summary>
    public string Label { get; }

    /// <summary>Frames of this rig alone.</summary>
    public int FrameCount { get; }

    /// <summary>"22 frames", for the rig label row and the box's tooltip.</summary>
    public string FrameCountText { get; }

    /// <summary>The rig's own integration in hours, unit free: the unit lives in the label beside
    /// it, as every other figure on this page does.</summary>
    public string IntegrationText { get; }

    /// <summary>The frame the box is showing, or would show. Null when the rig has no candidate at
    /// all.</summary>
    public Guid? ReferenceImageId { get; }

    /// <summary>The slot the box binds its image and its spinner to. Null until the walk starts
    /// and whenever there is no factory to start it with.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    [NotifyPropertyChangedFor(nameof(ShowsPlaceholder))]
    public partial ThumbnailSlotViewModel? Thumbnail { get; private set; }

    /// <summary>True once a candidate has decoded. The box shows the image and hides the
    /// placeholder on this one flag.</summary>
    public bool HasThumbnail => Thumbnail?.Image is not null;

    /// <summary>Spec 12.4's placeholder: shown when nothing has decoded and nothing is in flight.
    /// Not a failure message, and it carries no retry.</summary>
    public bool ShowsPlaceholder => !HasThumbnail && Thumbnail?.IsLoading != true;

    /// <summary>
    /// Starts spec 12.4's decode walk, once. The card calls it when the "Night detail" section
    /// that holds the strip is opened, and at once when that section is already open as the
    /// night's rigs publish.
    /// </summary>
    /// <remarks>Idempotent by design, and not a retry: a second call after the walk has run its
    /// three candidates and left the placeholder does nothing, because spec 12.4's placeholder
    /// carries no retry.</remarks>
    public void StartThumbnail()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        LoadNextCandidate();
    }

    /// <summary>Whether <see cref="StartThumbnail"/> has been called. The flag a case asserting
    /// that a closed section decodes nothing reads.</summary>
    internal bool HasStarted => _started;

    /// <summary>How many candidates the walk has started. Three is the cap; a shorter candidate
    /// list stops at its own length.</summary>
    internal int Attempts => _attempts;

    /// <summary>The in-flight decode, so a test can await it rather than sleeping.</summary>
    internal Task? PendingLoad => Thumbnail?.PendingLoad;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DetachAndDisposeSlot();
    }

    /// <summary>Spec 12.4: a click opens the preview modal at this frame, inside the night's frame
    /// list, so the arrow keys step through the night from there.</summary>
    [RelayCommand]
    private void OpenPreview() => _openPreview?.Invoke();

    // The walk. One slot per candidate, because a slot is bound to one frame path for its
    // lifetime; the previous one is detached and disposed before the next is published, so a
    // superseded decode cannot assign over a newer image.
    private void LoadNextCandidate()
    {
        if (_disposed || _createThumbnail is null)
        {
            return;
        }

        if (_attempts >= MaxDecodeAttempts || _attempts >= _candidates.Count)
        {
            // Out of candidates or out of attempts. Whatever slot is on the box stays, showing its
            // placeholder; there is no retry (spec 12.4).
            return;
        }

        var path = _candidates[_attempts++];
        var slot = _createThumbnail(path);

        DetachAndDisposeSlot();
        Thumbnail = slot;
        slot.PropertyChanged += OnSlotChanged;
        slot.Load();
    }

    private void OnSlotChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ThumbnailSlotViewModel.Image)
            or nameof(ThumbnailSlotViewModel.IsLoading)))
        {
            return;
        }

        OnPropertyChanged(nameof(HasThumbnail));
        OnPropertyChanged(nameof(ShowsPlaceholder));

        // Settled with no image: the render or the decode came back empty, which is the frame this
        // walk skips. A slot that is still loading is left alone.
        if (!_disposed && Thumbnail is { Image: null, IsLoading: false })
        {
            LoadNextCandidate();
        }
    }

    private void DetachAndDisposeSlot()
    {
        if (Thumbnail is not { } slot)
        {
            return;
        }

        slot.PropertyChanged -= OnSlotChanged;
        slot.Dispose();
    }
}
