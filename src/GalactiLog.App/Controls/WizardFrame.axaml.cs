using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace GalactiLog.App.Controls;

/// <summary>
/// The chrome every wizard shares: the step header with a help slot, a progress rail, the current
/// step's body, the step error and a footer with Back and Next. The hosting window supplies the
/// step DataTemplates, the help glyph and its own footer buttons.
/// </summary>
public partial class WizardFrame : UserControl
{
    /// <summary>Beside the step header, normally the host's own <c>HelpButton</c> bound to the
    /// wizard's <c>HelpTopicId</c>.</summary>
    public static readonly StyledProperty<object?> HelpSlotProperty =
        AvaloniaProperty.Register<WizardFrame, object?>(nameof(HelpSlot));

    /// <summary>Below the progress rail: a host's own line about the wizard.</summary>
    public static readonly StyledProperty<object?> HeaderExtraProperty =
        AvaloniaProperty.Register<WizardFrame, object?>(nameof(HeaderExtra));

    public WizardFrame()
    {
        InitializeComponent();
    }

    public object? HelpSlot
    {
        get => GetValue(HelpSlotProperty);
        set => SetValue(HelpSlotProperty, value);
    }

    public object? HeaderExtra
    {
        get => GetValue(HeaderExtraProperty);
        set => SetValue(HeaderExtraProperty, value);
    }

    /// <summary>
    /// The footer grid's children, so a host's buttons join Back and Next in one row with
    /// <c>Grid.Column</c> placing them: Back is column 2, Next is column 3.
    /// </summary>
    public Avalonia.Controls.Controls FooterExtra => this.GetControl<Grid>("Footer").Children;

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
