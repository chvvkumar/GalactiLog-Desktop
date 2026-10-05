using Avalonia.Controls;
using Avalonia.Input;

namespace GalactiLog.App.Views.Settings;

/// <summary>Spec amendment 4c's External Tools settings tab. Every field commits on blur; this
/// view adds nothing but the Enter-clears-focus helper <c>StorageTabView</c>'s fields already
/// use, so Enter commits a field the same way blur does.</summary>
public partial class ExternalToolsTabView : UserControl
{
    public ExternalToolsTabView() => InitializeComponent();

    private void OnCommitFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && TopLevel.GetTopLevel(this)?.FocusManager is { } focus)
        {
            focus.ClearFocus();
            e.Handled = true;
        }
    }
}
