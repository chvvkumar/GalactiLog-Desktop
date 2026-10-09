using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GalactiLog.App.ViewModels.CustomColumns;
using GalactiLog.App.ViewModels.TargetDetail;

namespace GalactiLog.App.Controls;

/// <summary>
/// Spec 12.15's one custom column cell editor. The markup chooses the kind; this file owns the two
/// things a cell that sits inside a clickable row has to do itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>The control swallows the click.</b> Spec 12.15: "Clicking an editor does not open the row."
/// The dashboard target row and the ledger night row are both clickable, so a press that came from
/// one of the three editors stops here. A press on the cell's padding is not from an editor and
/// still reaches the row, which is what keeps the swallow from being over-broad.
/// </para>
/// <para>
/// <b>The keyboard table.</b> Enter from an editor stops here: a row drawn as a <c>Button</c> claims
/// it, and a text box that does not accept return would otherwise open the target the moment the
/// user pressed Enter to commit. Enter on the text kind commits first; Tab and a click away commit
/// through <c>LostFocus</c>, which is one mechanism for both rather than a Tab branch of its own.
/// Escape on the text kind reverts and is marked handled; on the other two kinds it is not, so a
/// combo box popup still closes on its own terms. Everything else in the table is the platform's,
/// which the cases confirm rather than re-implement.
/// </para>
/// <para>
/// <b>Space is never swallowed on the text kind</b> (ruling C25), because there the key is also a
/// character and a handled key down costs the character. Nothing in this file may treat a key that
/// types something as a key to be intercepted; if a row must not act on a space typed into a cell,
/// that guard belongs on the row, which is the thing that knows what activating it means.
/// </para>
/// </remarks>
public partial class CustomCellEditor : UserControl
{
    /// <summary>
    /// Whether the text kind draws the column's name as its watermark. True by default, which is what
    /// the dashboard target row, the dashboard night expander and the session pane's rig line need:
    /// none of them has a heading over the cell, so the watermark is the only thing naming the column
    /// on an empty cell. A table cell sets it false, because it does have a heading, one row above,
    /// and repeating the name in every empty cell under it is noise: the empty cell shows the table's
    /// missing-value dash instead (spec.md item 6), which the placeholder ink already draws faint.
    /// </summary>
    /// <remarks>A styled property rather than a class, because it carries a value the markup sets per
    /// surface and the automation name is deliberately not affected: a screen reader still hears the
    /// column and the night on a table cell under a dash.</remarks>
    public static readonly StyledProperty<bool> ShowWatermarkProperty =
        AvaloniaProperty.Register<CustomCellEditor, bool>(nameof(ShowWatermark), defaultValue: true);

    public CustomCellEditor() => InitializeComponent();

    /// <inheritdoc cref="ShowWatermarkProperty"/>
    public bool ShowWatermark
    {
        get => GetValue(ShowWatermarkProperty);
        set => SetValue(ShowWatermarkProperty, value);
    }

    // The watermark follows both the cell and the surface's choice. Assigned here rather than bound in
    // the markup because the two sources are a property of the DataContext and a property of this
    // control, and this control declares no style element of any kind (ControlStyleScanTest), so a
    // style with a setter is not available either. A recycled row raises the DataContext change, which
    // is why this is not a constructor-time assignment.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ShowWatermarkProperty || change.Property == DataContextProperty)
        {
            TextEditor.Watermark = !ShowWatermark ? MetricText.Missing
                : DataContext is CustomValueViewModel cell ? cell.Label
                : null;
        }
    }

    private void OnEditorPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (EditorOf(e.Source) is not null)
        {
            e.Handled = true;
        }
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (EditorOf(e.Source) is not { } editor || DataContext is not CustomValueViewModel cell)
        {
            return;
        }

        if (e.Key is Key.Enter)
        {
            if (ReferenceEquals(editor, TextEditor))
            {
                // Commits and keeps focus. The write is fire and forget: AutosaveField owns the
                // chain and surfaces its own failure, and the cell surfaces a refusal.
                _ = cell.FlushAsync();
            }

            e.Handled = true;
            return;
        }

        // Ruling C25. Space is marked handled for the check box and the list, and NEVER for the text
        // box, where the key is also a character: the Win32 backend drops the character message that
        // follows a key down the application handled, so swallowing Space here swallowed the space
        // as well and "ab cd" was typed in as "abcd". Task 5a's launched look found it.
        if (e.Key is Key.Space && !ReferenceEquals(editor, TextEditor))
        {
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Escape && ReferenceEquals(editor, TextEditor))
        {
            cell.CancelEdit();
            e.Handled = true;
        }
    }

    private void OnTextEditorLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CustomValueViewModel cell)
        {
            _ = cell.FlushAsync();
        }
    }

    // Which of the three editors an event came from, or null when it came from the cell's padding.
    private Control? EditorOf(object? source)
    {
        for (var visual = source as Visual; visual is not null && !ReferenceEquals(visual, this);
             visual = visual.GetVisualParent())
        {
            if (ReferenceEquals(visual, CheckEditor)
                || ReferenceEquals(visual, TextEditor)
                || ReferenceEquals(visual, ChoiceEditor))
            {
                return (Control)visual;
            }
        }

        return null;
    }
}
