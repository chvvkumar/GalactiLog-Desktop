using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using GalactiLog.App.ViewModels.TargetDetail;

namespace GalactiLog.App.Views.TargetDetail.Parts;

public partial class FramesPart : UserControl
{
    public static readonly StyledProperty<bool> IsCompactProperty =
        AvaloniaProperty.Register<FramesPart, bool>(nameof(IsCompact));

    public static readonly StyledProperty<Control?> ToolbarItemProperty =
        AvaloniaProperty.Register<FramesPart, Control?>(nameof(ToolbarItem));

    private SessionCardViewModel? _card;

    public FramesPart()
    {
        InitializeComponent();

        // Their own value from the start: a first value equal to the inherited one raises no change,
        // and the buttons would never hear of the card.
        CompareToSegment.DataContext = null;
        FilterSegment.DataContext = null;
        Place();
    }

    /// <summary>True moves the Filter segment onto its own line under the tally and Compare to onto
    /// the toolbar line, so the chrome line keeps the tally alone (P24 R13).</summary>
    public bool IsCompact
    {
        get => GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    /// <summary>What the frame table draws on its toolbar's line: the Filter segment, or Compare to
    /// in the compact form.</summary>
    public Control? ToolbarItem
    {
        get => GetValue(ToolbarItemProperty);
        private set => SetValue(ToolbarItemProperty, value);
    }

    /// <summary>The height the chrome line and the compact form's filter line last measured at,
    /// which the pane reserves above the table's floor.</summary>
    public double ChromeHeight => FramesChrome.DesiredSize.Height + FilterLineHost.DesiredSize.Height;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsCompactProperty)
        {
            Place();
        }
        else if (change.Property == DataContextProperty)
        {
            // Both segments can sit in the table's toolbar, whose context is the table, so they keep the card's.
            var card = DataContext as SessionCardViewModel;
            CompareToSegment.DataContext = card;
            FilterSegment.DataContext = card;
            Watch(card);
        }
    }

    private void Watch(SessionCardViewModel? card)
    {
        if (_card is not null)
        {
            _card.PropertyChanged -= OnCardChanged;
        }

        _card = card;
        if (card is not null)
        {
            card.PropertyChanged += OnCardChanged;
        }

        ShowPills();
    }

    private void OnCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionCardViewModel.FrameTable))
        {
            Dispatcher.UIThread.Post(ShowPills);
        }
    }

    // Set from code: the bound row stayed empty on first load, and the cause was not found.
    private void ShowPills()
    {
        var table = _card?.FrameTable;
        RigPillRow.ItemsSource = table?.RigPills;
        RigPillRow.IsVisible = table?.IsMultiRig == true;
    }

    private static void Release(Control control)
    {
        switch (control.Parent)
        {
            case Panel panel:
                panel.Children.Remove(control);
                break;
            case ContentControl host:
                host.Content = null;
                break;
        }
    }

    // Every moving control leaves every host before any takes it, so no host keeps one.
    private void Place()
    {
        ToolbarItem = null;
        PillHost.Content = null;
        CompareHost.Content = null;
        FilterLineHost.Content = null;

        CompareHost.IsVisible = !IsCompact;
        FilterLineHost.IsVisible = IsCompact;
        if (IsCompact)
        {
            FilterLineHost.Content = FilterSegment;
            ToolbarItem = CompareToSegment;
            return;
        }

        CompareHost.Content = CompareToSegment;
        ToolbarItem = FilterSegment;
    }
}
