using Avalonia.Controls;

namespace GalactiLog.App.Views.Settings;

/// <summary>Design-spec 12.7's Filters tab. No code-behind handlers: the colour swatch's text
/// commit lives on <see cref="GroupingEditorView"/>, which this view hosts through
/// <c>Editor</c>.</summary>
public partial class FiltersTabView : UserControl
{
    public FiltersTabView() => InitializeComponent();
}
