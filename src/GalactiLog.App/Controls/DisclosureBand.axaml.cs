using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;

namespace GalactiLog.App.Controls;

/// <summary>The chevron band header row: a disclosure arm, a section label, a help slot and trailing
/// content, never the section body, which stays in the host's own markup.</summary>
public partial class DisclosureBand : UserControl
{
    /// <summary>The band's label, rendered in the <c>t-label section</c> tier.</summary>
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<DisclosureBand, string?>(nameof(Title));

    /// <summary>Which chevron arm is drawn. The band does not own the body, so this states the
    /// section's state and never governs it.</summary>
    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<DisclosureBand, bool>(
            nameof(IsExpanded),
            defaultBindingMode: BindingMode.TwoWay);

    /// <summary>What the chevron button runs. Null leaves the button inert rather than
    /// absent.</summary>
    public static readonly StyledProperty<ICommand?> ToggleCommandProperty =
        AvaloniaProperty.Register<DisclosureBand, ICommand?>(nameof(ToggleCommand));

    /// <summary>Column 1's content, normally the pane's own <c>HelpButton</c> for this section.
    /// A slot rather than a topic string: a glyph declared inside this control would be one markup
    /// site counted once for four placements, and spec 12.12's once-each census would fail on
    /// three ids.</summary>
    public static readonly StyledProperty<object?> HelpSlotProperty =
        AvaloniaProperty.Register<DisclosureBand, object?>(nameof(HelpSlot));

    /// <summary>Column 3's content at the band's trailing edge: the findings summary, the session
    /// count, the "Compare to" segment, or nothing.</summary>
    public static readonly StyledProperty<object?> TrailingContentProperty =
        AvaloniaProperty.Register<DisclosureBand, object?>(nameof(TrailingContent));

    public DisclosureBand() => InitializeComponent();

    /// <inheritdoc cref="TitleProperty"/>
    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <inheritdoc cref="IsExpandedProperty"/>
    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    /// <inheritdoc cref="ToggleCommandProperty"/>
    public ICommand? ToggleCommand
    {
        get => GetValue(ToggleCommandProperty);
        set => SetValue(ToggleCommandProperty, value);
    }

    /// <inheritdoc cref="HelpSlotProperty"/>
    public object? HelpSlot
    {
        get => GetValue(HelpSlotProperty);
        set => SetValue(HelpSlotProperty, value);
    }

    /// <inheritdoc cref="TrailingContentProperty"/>
    public object? TrailingContent
    {
        get => GetValue(TrailingContentProperty);
        set => SetValue(TrailingContentProperty, value);
    }
}
