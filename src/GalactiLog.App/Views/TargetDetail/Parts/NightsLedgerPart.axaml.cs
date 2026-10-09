using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GalactiLog.App.Controls.Table;
using GalactiLog.App.ViewModels.TargetDetail;

namespace GalactiLog.App.Views.TargetDetail.Parts;

public partial class NightsLedgerPart : UserControl
{
    public static readonly StyledProperty<bool> IsCollapsedProperty =
        AvaloniaProperty.Register<NightsLedgerPart, bool>(nameof(IsCollapsed));

    private TargetDetailViewModel? _page;

    private double _titleLineWidth;

    private bool _isStacked;

    private (double Collapsed, double Open) _extents;

    public NightsLedgerPart()
    {
        InitializeComponent();
        LedgerViewport.LayoutUpdated += (_, _) => ReportExtents();

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

    /// <summary>True at the divider's stop: the box and the date only. The layout sets it.</summary>
    public bool IsCollapsed
    {
        get => GetValue(IsCollapsedProperty);
        set => SetValue(IsCollapsedProperty, value);
    }

    /// <summary>True when the column is narrower than the title line, and always at the stop: the
    /// chrome stacks one item per line. The layout sets it.</summary>
    public bool IsStacked
    {
        get => _isStacked;
        set
        {
            _isStacked = value;
            PseudoClasses.Set(":stacked", value);
        }
    }

    /// <summary>The title line's width in its one-line form (ruling R8).</summary>
    public double TitleLineWidth => _titleLineWidth;

    /// <summary>The header's chevron; the layout gives it the sidebar's toggle.</summary>
    public Button CollapseToggle => CollapseChevron;

    /// <summary>The date column's right edge: the divider's stop.</summary>
    public double CollapsedWidth => LedgerHeaderRow.ColumnDefinitions.Count < 2
        ? 0d
        : LedgerHeaderRow.ColumnDefinitions[0].ActualWidth + LedgerHeaderRow.ColumnDefinitions[1].ActualWidth;

    /// <summary>The table's own width, every custom column shown, and never narrower than the title
    /// line in its open form (ruling R8).</summary>
    public double OpenWidth => Math.Max(LedgerViewport.NaturalWidth, _titleLineWidth);

    /// <summary>Raised after a layout pass that moved <see cref="CollapsedWidth"/> or
    /// <see cref="OpenWidth"/>.</summary>
    public event EventHandler? ExtentsChanged;

    private TableColumns Columns => (TableColumns)Resources["NightsCols"]!;

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
        if (change.Property == IsCollapsedProperty)
        {
            PseudoClasses.Set(":collapsed", IsCollapsed);
        }
        else if (change.Property == FontSizeProperty || change.Property == FontFamilyProperty
            || change.Property == DataContextProperty)
        {
            if (change.Property == DataContextProperty && this.IsAttachedToVisualTree())
            {
                Follow(DataContext as TargetDetailViewModel);
            }

            Columns.Reset();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Follow(DataContext as TargetDetailViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Follow(null);
        base.OnDetachedFromVisualTree(e);
    }

    // A shared size group only grows, so a custom column switched off starts a new generation, or
    // the strip would keep the width of the column it no longer draws. Followed only while
    // attached, so a page kept by the history does not keep this view.
    private void Follow(TargetDetailViewModel? page)
    {
        if (_page is not null)
        {
            _page.PropertyChanged -= OnPageChanged;
        }

        _page = page;
        if (page is not null)
        {
            page.PropertyChanged += OnPageChanged;
        }
    }

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TargetDetailViewModel.LedgerCustomHeadings))
        {
            Columns.Reset();
        }
    }

    private void ReportExtents()
    {
        // The title line is measured in its one-line form only; stacked, it is one item per line.
        if (!IsStacked)
        {
            _titleLineWidth = LineWidth(LedgerTitle) + LineWidth(LedgerActions);
        }

        var extents = (CollapsedWidth, OpenWidth);
        if (extents != _extents)
        {
            _extents = extents;
            ExtentsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // A horizontal line's own width: its children measure unconstrained, while the panel's desired
    // size is clipped to the column it was given.
    private static double LineWidth(StackPanel line)
    {
        var shown = line.Children.Where(child => child.IsVisible).ToList();
        return shown.Sum(child => child.DesiredSize.Width) + line.Spacing * Math.Max(0, shown.Count - 1);
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
