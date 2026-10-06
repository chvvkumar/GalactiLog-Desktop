using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GalactiLog.App.ViewModels.TargetDetail;

namespace GalactiLog.App.ViewModels.Mosaics;

/// <summary>The ink of a tile's deficit badge (spec 12.17's arranger): the view maps
/// <see cref="Success"/>, <see cref="Warning"/> and <see cref="Error"/> to <c>ColorSuccess</c>,
/// <c>ColorWarning</c> and <c>ColorError</c>; <see cref="None"/> shows no badge.</summary>
public enum DeficitBand
{
    None,
    Success,
    Warning,
    Error,
}

/// <summary>
/// One tile of spec 12.17's arranger: a panel's position on the canvas, its quarter turn and flip,
/// its overlays (label, integration, state badge, deficit badge), its selection and its thumbnail.
/// </summary>
/// <remarks>
/// <see cref="ArrangerViewModel"/> owns every tile and is the only writer of its state; the view
/// binds and never sets. The tile owns its <see cref="Thumbnail"/> slot and disposes it when
/// re-pointed, which withdraws the slot's request, so a superseded request never lands.
/// </remarks>
public sealed partial class TileViewModel : ObservableObject, IDisposable
{
    /// <summary>The caption of a tile whose thumbnail could not be rendered (spec 12.17).</summary>
    public const string NoThumbnailText = "No thumbnail";

    private readonly Action _loadingChanged;
    private string? _framePath;

    // False until the arranger first says which frame the tile shows, so a tile does not read as
    // the empty tile while the frame set is still being read.
    private bool _resolved;

    internal TileViewModel(Guid panelId, string label, Action loadingChanged)
    {
        PanelId = panelId;
        Label = label;
        IntegrationText = "";
        _loadingChanged = loadingChanged;
    }

    /// <summary>The panel's id; a preview tile has an id of its own.</summary>
    public Guid PanelId { get; }

    /// <summary>The panel label, the bottom left overlay.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    public partial string Label { get; private set; }

    /// <summary>The tile's top left corner in canvas pixels at zoom 1; for an unplaced tile, the
    /// auto layout position it shows.</summary>
    [ObservableProperty]
    public partial double X { get; private set; }

    /// <inheritdoc cref="X"/>
    [ObservableProperty]
    public partial double Y { get; private set; }

    /// <summary>False while the panel has no stored position and follows the auto layout.</summary>
    public bool IsPlaced { get; private set; }

    /// <summary>0, 90, 180 or 270: the image's quarter turn.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateBadge))]
    public partial int Rotation { get; private set; }

    /// <summary>The image's horizontal flip.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateBadge))]
    public partial bool FlipH { get; private set; }

    /// <summary>"90°", "flipped", "90° · flipped", or null while the tile is neither rotated nor
    /// flipped (the top left overlay).</summary>
    public string? StateBadge
    {
        get
        {
            var turn = Rotation == 0 ? null : $"{Rotation}°";
            var flip = FlipH ? "flipped" : null;
            return turn is null ? flip : flip is null ? turn : turn + " · " + flip;
        }
    }

    /// <summary>The panel's integration in the integration formatter's form (bottom right).</summary>
    [ObservableProperty]
    public partial string IntegrationText { get; private set; }

    /// <summary>"-" and the deficit in the integration formatter's form, or null with no badge.</summary>
    [ObservableProperty]
    public partial string? DeficitText { get; private set; }

    /// <summary>The deficit badge's ink.</summary>
    [ObservableProperty]
    public partial DeficitBand Deficit { get; private set; }

    /// <summary>Draws the accent outline (the <c>selected</c> class).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    public partial bool IsSelected { get; private set; }

    /// <summary>The opacity of the tile's image, overlays and empty caption: 1 unless the tile is
    /// selected and faded. The outline stays at full strength.</summary>
    [ObservableProperty]
    public partial double Opacity { get; private set; } = 1;

    /// <summary>The stacking order: a higher value draws over a lower one. The view binds the
    /// container's <c>ZIndex</c> to it.</summary>
    [ObservableProperty]
    public partial int ZIndex { get; private set; }

    /// <summary>The best frame's thumbnail in the chosen filter, or null when the panel has no
    /// frame in it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage), nameof(IsEmpty), nameof(IsLoading), nameof(EmptyText), nameof(AutomationName))]
    public partial ThumbnailSlotViewModel? Thumbnail { get; private set; }

    /// <summary>The filter the tile's frame was chosen in, for the empty caption; null in the
    /// preview, which chooses over every filter.</summary>
    public string? Filter { get; private set; }

    /// <summary>True once the thumbnail has an image.</summary>
    public bool HasImage => Thumbnail?.Image is not null;

    /// <summary>True while the thumbnail is outstanding.</summary>
    public bool IsLoading => Thumbnail?.IsLoading == true;

    /// <summary>The empty tile: no frame in the filter, or a thumbnail that finished with no image.</summary>
    public bool IsEmpty => _resolved && !HasImage && !IsLoading;

    /// <summary>"No &lt;filter&gt; frames" with no frame in the filter, else "No thumbnail".</summary>
    public string EmptyText => Thumbnail is null && Filter is not null ? $"No {Filter} frames" : NoThumbnailText;

    /// <summary>"&lt;label&gt;", with ", selected" while selected and ", no frames in
    /// &lt;filter&gt;" or ", no thumbnail" while empty (spec 12.17's accessibility).</summary>
    public string AutomationName
    {
        get
        {
            var name = Label;
            if (IsSelected)
            {
                name += ", selected";
            }

            if (IsEmpty)
            {
                name += Thumbnail is null && Filter is not null ? $", no frames in {Filter}" : ", no thumbnail";
            }

            return name;
        }
    }

    internal void SetFigures(string label, double integrationSeconds, double deficitSeconds, double leaderSeconds)
    {
        Label = label;
        IntegrationText = MetricText.Integration(integrationSeconds);

        // Spec 12.17's deficit badge: none at 60 seconds or less, none while the leader is zero,
        // else banded by share of the leader at 0.8 and 0.4 (the web's DELTA thresholds).
        if (deficitSeconds <= 60 || leaderSeconds <= 0)
        {
            DeficitText = null;
            Deficit = DeficitBand.None;
            return;
        }

        var share = integrationSeconds / leaderSeconds;
        DeficitText = "-" + MetricText.Integration(deficitSeconds);
        Deficit = share >= 0.8 ? DeficitBand.Success : share >= 0.4 ? DeficitBand.Warning : DeficitBand.Error;
    }

    internal void SetPosition(double x, double y, bool placed)
    {
        X = x;
        Y = y;
        IsPlaced = placed;
    }

    internal void Place() => IsPlaced = true;

    internal void SetTransform(int rotation, bool flipH)
    {
        Rotation = rotation;
        FlipH = flipH;
    }

    internal void SetSelected(bool selected, double opacity)
    {
        IsSelected = selected;
        Opacity = selected ? opacity : 1;
    }

    internal void RaiseTo(int zIndex) => ZIndex = zIndex;

    /// <summary>Shows <paramref name="framePath"/> (null for none). The same frame keeps its slot;
    /// another disposes the old slot, withdrawing its request, and loads a new one.</summary>
    internal void SetFrame(string? framePath, string? filter, Func<string, ThumbnailSlotViewModel> slotFor)
    {
        _resolved = true;
        Filter = filter;
        if (Thumbnail is not null && string.Equals(framePath, _framePath, StringComparison.Ordinal))
        {
            RaiseThumbnailState();
            return;
        }

        var previous = Thumbnail;
        _framePath = framePath;
        if (previous is not null)
        {
            previous.PropertyChanged -= OnSlotChanged;
        }

        var slot = framePath is null ? null : slotFor(framePath);
        if (slot is not null)
        {
            slot.PropertyChanged += OnSlotChanged;
        }

        Thumbnail = slot;
        previous?.Dispose();
        slot?.Load();
        RaiseThumbnailState();
    }

    private void OnSlotChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, Thumbnail) &&
            e.PropertyName is nameof(ThumbnailSlotViewModel.Image) or nameof(ThumbnailSlotViewModel.IsLoading))
        {
            RaiseThumbnailState();
        }
    }

    private void RaiseThumbnailState()
    {
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(AutomationName));
        _loadingChanged();
    }

    public void Dispose()
    {
        if (Thumbnail is { } slot)
        {
            slot.PropertyChanged -= OnSlotChanged;
            Thumbnail = null;
            slot.Dispose();
        }
    }
}
