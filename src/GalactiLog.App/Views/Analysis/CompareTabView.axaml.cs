using Avalonia.Controls;

namespace GalactiLog.App.Views.Analysis;

/// <summary>
/// Spec 12.14's Compare tab body, bound to
/// <see cref="ViewModels.Analysis.CompareTabViewModel"/>.
/// </summary>
/// <remarks>
/// No code beyond loading the markup, like the other four tab bodies. An earlier shape walked the
/// visual ancestors for the page's view-model and handed the tab the filter bar's two lists on
/// attach; the tab's constructor takes them now, so this body names no view-model but its own.
/// </remarks>
public partial class CompareTabView : UserControl
{
    public CompareTabView() => InitializeComponent();
}
