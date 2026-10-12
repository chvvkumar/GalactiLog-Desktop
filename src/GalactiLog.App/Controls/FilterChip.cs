using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace GalactiLog.App.Controls;

/// <summary>
/// One filter chip, the one shape a filter's configured colour takes behind a label: the target
/// list's Palette and session badges, the Statistics Filters cell, the filter panel's pills and the
/// target header's per-filter hours. Its template is Theme/Controls.axaml's FilterChip control
/// theme, so every call site is this element and a binding, and the four copies that drifted apart
/// (two of them without the contrast fallback) are one definition.
/// </summary>
/// <remarks>
/// The figures are the web app's frosted-glass badge (frontend <c>utils/filterStyles.ts</c>): an
/// 18 percent tint wash inside a 25 percent tint edge, a 6 percent white inset top highlight and a
/// soft 15 percent tinted drop shadow. They are derived here from <see cref="Tint"/>, so the
/// template binds brushes and holds no arithmetic. The label takes the tint only where it reads
/// against the surface (<c>TintInkConverter</c>, 3:1); otherwise the primary ink this control
/// hands down. The tint is user data, spec 14.5's one legitimate non-token colour.
/// </remarks>
public sealed class FilterChip : TemplatedControl
{
    /// <summary>The filter's configured colour. Null draws a chip with no tint at all.</summary>
    public static readonly StyledProperty<ISolidColorBrush?> TintProperty =
        AvaloniaProperty.Register<FilterChip, ISolidColorBrush?>(nameof(Tint));

    /// <summary>The filter's name.</summary>
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<FilterChip, string?>(nameof(Text));

    /// <summary>A figure after the name in the quiet ink (hours, a frame count). Null hides it.</summary>
    public static readonly StyledProperty<string?> DetailProperty =
        AvaloniaProperty.Register<FilterChip, string?>(nameof(Detail));

    /// <summary>Whether the wash is drawn. True by default; the filter panel's pill binds it to
    /// its selection, so a chosen filter reads as washed and an unchosen one as an outline.</summary>
    public static readonly StyledProperty<bool> IsWashedProperty =
        AvaloniaProperty.Register<FilterChip, bool>(nameof(IsWashed), true);

    public static readonly DirectProperty<FilterChip, IBrush?> WashProperty =
        AvaloniaProperty.RegisterDirect<FilterChip, IBrush?>(nameof(Wash), chip => chip.Wash);

    public static readonly DirectProperty<FilterChip, IBrush?> EdgeProperty =
        AvaloniaProperty.RegisterDirect<FilterChip, IBrush?>(nameof(Edge), chip => chip.Edge);

    public static readonly DirectProperty<FilterChip, BoxShadows> ShadowProperty =
        AvaloniaProperty.RegisterDirect<FilterChip, BoxShadows>(nameof(Shadow), chip => chip.Shadow);

    private IBrush? _wash;
    private IBrush? _edge;
    private BoxShadows _shadow;

    /// <inheritdoc cref="TintProperty"/>
    public ISolidColorBrush? Tint
    {
        get => GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    /// <inheritdoc cref="TextProperty"/>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <inheritdoc cref="DetailProperty"/>
    public string? Detail
    {
        get => GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    /// <inheritdoc cref="IsWashedProperty"/>
    public bool IsWashed
    {
        get => GetValue(IsWashedProperty);
        set => SetValue(IsWashedProperty, value);
    }

    /// <summary>The tint at 18 percent while <see cref="IsWashed"/>, otherwise null.</summary>
    public IBrush? Wash
    {
        get => _wash;
        private set => SetAndRaise(WashProperty, ref _wash, value);
    }

    /// <summary>The tint at 25 percent, the chip's 1 pixel edge.</summary>
    public IBrush? Edge
    {
        get => _edge;
        private set => SetAndRaise(EdgeProperty, ref _edge, value);
    }

    /// <summary>The inset top highlight, then the tinted drop shadow.</summary>
    public BoxShadows Shadow
    {
        get => _shadow;
        private set => SetAndRaise(ShadowProperty, ref _shadow, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TintProperty || change.Property == IsWashedProperty)
        {
            Derive();
        }
    }

    private void Derive()
    {
        if (Tint is not { } tint)
        {
            Wash = null;
            Edge = null;
            Shadow = default;
            return;
        }

        var colour = tint.Color;
        Wash = IsWashed ? new ImmutableSolidColorBrush(Alpha(colour, 0.18)) : null;
        Edge = new ImmutableSolidColorBrush(Alpha(colour, 0.25));
        Shadow = new BoxShadows(
            new BoxShadow { OffsetY = 1, Color = Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF), IsInset = true },
            [new BoxShadow { OffsetY = 2, Blur = 8, Color = Alpha(colour, 0.15) }]);
    }

    private static Color Alpha(Color colour, double alpha)
        => Color.FromArgb((byte)Math.Round(alpha * 255), colour.R, colour.G, colour.B);
}
