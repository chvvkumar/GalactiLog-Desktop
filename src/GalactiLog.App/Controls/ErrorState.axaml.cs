using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace GalactiLog.App.Controls;

/// <summary>A load that failed: a message, an optional detail line, and a Retry button bound to
/// the host page's existing reload command. Replaces the grey caption failures, which read the
/// same as an empty state.</summary>
public partial class ErrorState : UserControl
{
    /// <summary>The one-line failure, in sentence case.</summary>
    public static readonly StyledProperty<string?> MessageProperty =
        AvaloniaProperty.Register<ErrorState, string?>(nameof(Message));

    /// <summary>An optional second line, such as what to check. Hidden when empty.</summary>
    public static readonly StyledProperty<string?> DetailProperty =
        AvaloniaProperty.Register<ErrorState, string?>(nameof(Detail));

    /// <summary>The host's reload. Null hides the Retry button.</summary>
    public static readonly StyledProperty<ICommand?> RetryCommandProperty =
        AvaloniaProperty.Register<ErrorState, ICommand?>(nameof(RetryCommand));

    public ErrorState() => InitializeComponent();

    /// <inheritdoc cref="MessageProperty"/>
    public string? Message
    {
        get => GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    /// <inheritdoc cref="DetailProperty"/>
    public string? Detail
    {
        get => GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    /// <inheritdoc cref="RetryCommandProperty"/>
    public ICommand? RetryCommand
    {
        get => GetValue(RetryCommandProperty);
        set => SetValue(RetryCommandProperty, value);
    }
}
