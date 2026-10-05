using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using GalactiLog.App.ViewModels.Settings;

namespace GalactiLog.App.Views.Settings;

/// <summary>Code-behind for <see cref="GroupingEditorView"/>: a colour chosen in a swatch's
/// picker commits through <see cref="AliasGroupViewModel.TrySetColor"/>, which validates the
/// six-digit hex form and refuses anything else rather than writing a string <c>FilterColor</c>
/// would later have to guess at; an ungrouped row's picker commits through
/// <see cref="GroupingEditorViewModel.SetUngroupedColor"/>, which promotes the name to a one-name
/// group; and the rename box commits on blur too (not only Enter) and auto-focuses itself when a
/// rename begins (review minor 3, the web's <c>onBlur={commitRename}</c> and its focus
/// ref).</summary>
public partial class GroupingEditorView : UserControl
{
    public GroupingEditorView() => InitializeComponent();

    // P13 R2b. ColorView.Color is bound one way to the row's already-resolved swatch colour, so
    // the picker opens on what the swatch shows whether that colour is stored or seeded. Avalonia
    // raises ColorChanged for that inbound assignment too, which is why both handlers below start
    // by comparing against the swatch: without it, merely opening a flyout on a seeded filter
    // would write the seeded colour into the document, which is exactly what section 7.6 exists
    // to prevent. The picker's Color changes per drag step, so the commit path has to be cheap
    // and idempotent, and TrySetColor is both.
    private void OnGroupColorChanged(object? sender, ColorChangedEventArgs e)
    {
        if (sender is ColorView { DataContext: AliasGroupViewModel group }
            && group.SwatchBrush.Color != e.NewColor)
        {
            group.TrySetColor(Hex(e.NewColor));
        }
    }

    // Review P2-2. An ungrouped pick is committed once, when the flyout closes, because committing
    // it promotes the name to a one-name group and so removes the very row the open flyout is
    // attached to: a commit per ColorChanged kept the first pixel of a drag, dropped every later
    // change at SetUngroupedColor's coverage guard, and orphaned the open flyout. ColorChanged
    // only moves the row's own swatch here, so the drag stays live on screen.
    //
    // The row below is what says a pick actually happened. The inbound binding assignment raises
    // ColorChanged too, and its value is by definition the swatch's own colour, so opening a
    // flyout and closing it again records nothing and promotes nothing. One field is enough: one
    // flyout is open at a time.
    private DiscoveredNameViewModel? _pickedUngroupedRow;

    private void OnUngroupedColorChanged(object? sender, ColorChangedEventArgs e)
    {
        if (sender is ColorView { DataContext: DiscoveredNameViewModel row }
            && row.SwatchBrush.Color != e.NewColor)
        {
            row.Color = Hex(e.NewColor);
            _pickedUngroupedRow = row;
        }
    }

    private void OnUngroupedPickerClosed(object? sender, EventArgs e)
    {
        var picked = _pickedUngroupedRow;
        _pickedUngroupedRow = null;

        if (picked is { Color: { } hex } && DataContext is GroupingEditorViewModel editor)
        {
            editor.SetUngroupedColor(picked.Name, hex);
        }
    }

    // The six-digit form TrySetColor validates and the document stores. The picker runs with
    // alpha disabled, so there is no alpha byte to lose here.
    private static string Hex(Color color) => $"#{color.R:x2}{color.G:x2}{color.B:x2}";

    // Commits on blur, matching the web's onBlur={commitRename}: a user who clicks away from an
    // in-progress rename no longer loses it silently. Both CommitRenameCommand and
    // CancelRenameCommand set IsRenaming false themselves, which hides this box and so raises its
    // own LostFocus a moment later; guarding on group.IsRenaming still being true is what tells a
    // genuine external blur (commit) apart from that internal, already-handled one (Enter already
    // committed, or Escape already cancelled) -- without it, Escape's blur would re-commit
    // whatever text was left in the box.
    private void OnRenameBoxLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: AliasGroupViewModel { IsRenaming: true } group })
        {
            group.CommitRenameCommand.Execute(null);
        }
    }

    // Auto-focuses the rename box the moment it becomes visible (IsRenaming turns true), matching
    // the web's ref-based auto-focus. IsVisible is the property this template's binding flips, so
    // it is the one this handler watches rather than a second, view-model-side "just started
    // renaming" flag.
    private void OnRenameBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is TextBox box && e.Property == Visual.IsVisibleProperty && box.IsVisible)
        {
            box.Focus();
            box.SelectAll();
        }
    }
}
