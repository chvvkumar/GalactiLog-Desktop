using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GalactiLog.App.ViewModels.TargetDetail;

namespace GalactiLog.App.Views.TargetDetail.Parts;

public partial class NightsLedgerPart : UserControl
{
    public static readonly StyledProperty<Control?> LitRowContentProperty =
        AvaloniaProperty.Register<NightsLedgerPart, Control?>(nameof(LitRowContent));

    public static readonly StyledProperty<bool> IsCompactProperty =
        AvaloniaProperty.Register<NightsLedgerPart, bool>(nameof(IsCompact));

    public static readonly StyledProperty<bool> IsCollapsedProperty =
        AvaloniaProperty.Register<NightsLedgerPart, bool>(nameof(IsCollapsed));

    /// <summary>The column bound here keeps its declared figure, the parameter, unless the bound
    /// flag (compact or collapsed) is set, when it is zero.</summary>
    public static readonly IValueConverter Unless = new FuncValueConverter<bool, object?, double>(
        (hidden, figure) => hidden ? 0d : double.Parse((string)figure!, CultureInfo.InvariantCulture));

    /// <summary>A hidden column leaves its shared size group, because a group only ever grows and
    /// would hold the width the column gave up.</summary>
    public static readonly IValueConverter GroupUnless = new FuncValueConverter<bool, object?, string?>(
        (hidden, group) => hidden ? null : (string?)group);

    private ContentControl? _litSlot;

    public NightsLedgerPart()
    {
        InitializeComponent();
        NightsLedger.LayoutUpdated += (_, _) => PlaceLitRowContent();

        // Tunnelling, so a Ctrl or Shift press is handled before the ListBox reads it as a selection.
        NightsLedger.AddHandler(PointerPressedEvent, OnLedgerRowPointerPressed, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// P25 R1's Explorer gestures on a ledger row: Ctrl toggles the row's check, Shift checks the
    /// range from the lit row to the pressed row; neither moves the lit row. A plain press and a
    /// press on the box reach the <c>ListBox</c> and the box untouched.
    /// </summary>
    private void OnLedgerRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var modifiers = e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift);
        if (modifiers == KeyModifiers.None
            || !e.GetCurrentPoint(NightsLedger).Properties.IsLeftButtonPressed
            || e.Source is not Visual source
            || source.FindAncestorOfType<CheckBox>(includeSelf: true) is not null
            || source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is not SessionCardViewModel pressed
            || DataContext is not TargetDetailViewModel page)
        {
            return;
        }

        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            pressed.IsChecked = !pressed.IsChecked;
        }
        else
        {
            var from = page.SelectedSession is { } lit ? page.Sessions.IndexOf(lit) : -1;
            var to = page.Sessions.IndexOf(pressed);
            if (from < 0)
            {
                from = to;
            }

            for (var index = Math.Min(from, to); index <= Math.Max(from, to); index++)
            {
                page.Sessions[index].IsChecked = true;
            }
        }

        e.Handled = true;
    }

    /// <summary>Drawn under the lit row, inside the ledger's shared size scope.</summary>
    public Control? LitRowContent
    {
        get => GetValue(LitRowContentProperty);
        set => SetValue(LitRowContentProperty, value);
    }

    /// <summary>True drops the Frames and Filters columns.</summary>
    public bool IsCompact
    {
        get => GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    /// <summary>True keeps the box and the date only; the layout sets it with <see cref="IsCompact"/>.</summary>
    public bool IsCollapsed
    {
        get => GetValue(IsCollapsedProperty);
        set => SetValue(IsCollapsedProperty, value);
    }

    /// <summary>The header's chevron; the layout gives it the sidebar's toggle.</summary>
    public Button CollapseToggle => CollapseChevron;

    /// <summary>Scrolls the ledger to the night.</summary>
    public void ScrollToNight(SessionCardViewModel card) => NightsLedger.ScrollIntoView(card);

    // An always-selected list picks its first row when the rows arrive, before the bound night
    // does, and writes that row to the page; so it is always-selected only once the page is in.
    protected override void OnDataContextBeginUpdate()
    {
        NightsLedger.SelectionMode = SelectionMode.Single;
        base.OnDataContextBeginUpdate();
    }

    protected override void OnDataContextEndUpdate()
    {
        base.OnDataContextEndUpdate();
        NightsLedger.SelectionMode = SelectionMode.AlwaysSelected;
        if (NightsLedger.SelectedIndex == -1 && NightsLedger.ItemCount > 0)
        {
            NightsLedger.SelectedIndex = 0;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsCompactProperty)
        {
            PseudoClasses.Set(":compact", IsCompact);
        }
        else if (change.Property == IsCollapsedProperty)
        {
            PseudoClasses.Set(":collapsed", IsCollapsed);
        }
        else if (change.Property == LitRowContentProperty)
        {
            PlaceLitRowContent();
        }
    }

    // A control has one parent, so the content moves to the lit row's slot rather than being bound
    // from every row's template; containers are realised and recycled during layout.
    private void PlaceLitRowContent()
    {
        var content = LitRowContent;
        var slot = content is not null
            && NightsLedger.SelectedItem is { } night
            && NightsLedger.ContainerFromItem(night) is { } row
                ? row.GetVisualDescendants().OfType<ContentControl>().FirstOrDefault(c => c.Classes.Contains("lit-slot"))
                : null;
        if (ReferenceEquals(slot, _litSlot) && ReferenceEquals(slot?.Content, content))
        {
            return;
        }

        if (_litSlot is not null)
        {
            _litSlot.Content = null;
        }

        _litSlot = slot;
        if (slot is not null)
        {
            slot.Content = content;
        }
    }

    /// <summary>
    /// Spec 12.4: checking a night does not select it. A <c>CheckBox</c> inside a
    /// <c>ListBoxItem</c> would otherwise hand the press on to the item and light the row, so the
    /// press stops here and the box's own toggle is all the click does.
    /// </summary>
    /// <remarks>
    /// Avalonia's <c>Button.OnPointerPressed</c> already marks the left button handled, so this is
    /// belt and braces rather than the only guard. It is here because the rule is a stated one
    /// and a future theme or a middle-click path must not be able to lose it quietly.
    /// </remarks>
    private void OnNightCheckBoxPointerPressed(object? sender, PointerPressedEventArgs e)
        => e.Handled = true;
}
