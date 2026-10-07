using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Settings;

namespace GalactiLog.App.Views.TargetDetail.Layouts;

/// <summary>Layout C's three questions; the mode is the view's own and is never stored.</summary>
public enum TargetPageMode { NightReview, CompareNights, Integration }

/// <summary>Layout C, Question Modes (spec 6.1): the nights sidebar in every mode and the mode's
/// region beside it.</summary>
public partial class ModesLayoutView : TargetLayoutView
{
    public static readonly StyledProperty<TargetPageMode> ModeProperty =
        AvaloniaProperty.Register<ModesLayoutView, TargetPageMode>(nameof(Mode));

    private const string LayoutKey = "modes";

    /// <summary>The frames table's floor, the session pane's filter table minimum.</summary>
    public const double FramesFloor = 682d;

    /// <summary>The Compare table's largest share of its region's height.</summary>
    public const double CompareTableShare = 0.4d;

    /// <summary>The session chart's height with nothing stored, and what the handle's double click returns.</summary>
    public const double ChartHeightDefault = 180d;

    /// <summary>The least the handle lets the session chart be.</summary>
    public const double ChartFloor = 120d;

    public ModesLayoutView()
    {
        SelectMode = new RelayCommand<TargetPageMode>(mode =>
        {
            Mode = mode;
            ShowMode();
            // The shell's history observes the page's Mode, so a press is reported there; the
            // attach below never writes it, or every open would push a second entry.
            if (Page is { } page)
            {
                page.Mode = mode.ToString();
            }
        });
        InitializeComponent();
        NightReviewButton.Command = SelectMode;
        CompareNightsButton.Command = SelectMode;
        IntegrationButton.Command = SelectMode;

        UseSpine(LayoutKey, NightsLedgerPart, LanesRegion, LanesHandle, FramesRegion, FramesPart);
        SidebarHandle.Attach(LeftColumn, NightsLedgerPart, LayoutKey, () => Page?.TargetPage);
        NightsLedgerPart.CollapseToggle.Command = new RelayCommand(SidebarHandle.ToggleCollapsed);
        // The one handle sizes both: its cap on the lanes is applied as the chart's height less what
        // sits above the chart, so a drag trades chart height for frame rows directly.
        LanesRegion.PropertyChanged += (_, e) =>
        {
            if (e.Property == MaxHeightProperty)
            {
                FitChart();
            }
        };
        NightReviewRegion.SizeChanged += (_, _) => LanesHandle.Refresh();
        NightHeaderPart.SizeChanged += (_, _) => RefitLanes();
        NightMetricsSection.SizeChanged += (_, _) => RefitLanes();
        NightTimelinePart.SizeChanged += (_, _) => RefitLanes();
        CompareNightsRegion.SizeChanged += (_, e) => CompareTableHost.MaxHeight = Math.Floor(CompareTableShare * e.NewSize.Height);

        RightRegion.SizeChanged += (_, e) => ApplyForm(e.NewSize.Width);
        ApplyForm(double.PositiveInfinity);
        NightMetricsSection.PropertyChanged += (_, e) => OnSectionToggled(e, (state, open) => state with { NightMetricsOpen = open });
        ShowMode();
    }

    public TargetPageMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public RelayCommand<TargetPageMode> SelectMode { get; }

    /// <summary>The lanes region's height with nothing stored: down to the bottom of the chart at
    /// its default height, so the chart is whole at rest and the frame rows take the rest.</summary>
    public double LanesFloor => AboveTheChart + ChartHeightDefault;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ModeProperty)
        {
            ShowMode();
        }
    }

    // A toggle button flips itself on a click, so all three are set from Mode after every press.
    private void ShowMode()
    {
        NightReviewRegion.IsVisible = Mode == TargetPageMode.NightReview;
        CompareNightsRegion.IsVisible = Mode == TargetPageMode.CompareNights;
        IntegrationRegion.IsVisible = Mode == TargetPageMode.Integration;
        NightReviewButton.IsChecked = Mode == TargetPageMode.NightReview;
        CompareNightsButton.IsChecked = Mode == TargetPageMode.CompareNights;
        IntegrationButton.IsChecked = Mode == TargetPageMode.Integration;
        if (Mode == TargetPageMode.CompareNights && DataContext is TargetDetailViewModel page)
        {
            page.TargetChart.ShowAllSessions = true;
        }
    }

    // The frames fill the right column (P24 R22), so the shared width rule decides their form.
    private void ApplyForm(double rightWidth) => FramesColumn.Apply(FramesPart, rightWidth);

    // A section's disclosure is stored on change; applying the stored value leaves the record equal
    // and writes nothing. The value is read here, on the UI thread: the writer applies the change
    // again on its own thread, where a control read throws (R19).
    private void OnSectionToggled(AvaloniaPropertyChangedEventArgs e, Func<TargetLayoutState, bool, TargetLayoutState> change)
    {
        if (e.Property != Expander.IsExpandedProperty)
        {
            return;
        }

        var open = e.GetNewValue<bool>();
        Page?.TargetPage.SetLayout(LayoutKey, state => change(state, open));
    }

    protected override void OnPageAttached(TargetDetailViewModel page)
    {
        var stored = page.TargetPage.Layout(LayoutKey);
        SidebarHandle.Refresh();
        NightMetricsSection.IsExpanded = stored.NightMetricsOpen ?? true;
        // Back across a mode switch keeps the page and only writes its Mode, so the view follows
        // the page both on attach and for as long as it is attached.
        FollowPageMode(page);
        page.PropertyChanged += OnPageModeChanged;
    }

    protected override void OnReleasing()
    {
        if (Page is { } page)
        {
            page.PropertyChanged -= OnPageModeChanged;
        }

        SidebarHandle.Flush();
    }

    private void OnPageModeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TargetDetailViewModel.Mode) && sender is TargetDetailViewModel page)
        {
            FollowPageMode(page);
        }
    }

    private void FollowPageMode(TargetDetailViewModel page)
    {
        if (Enum.TryParse<TargetPageMode>(page.Mode, out var mode) && mode != Mode)
        {
            Mode = mode;
        }
    }

    // What sits above the chart, as a sum of heights rather than a bottom edge: the Session metrics
    // section's open body is never charged to the lanes (it scrolls, R2 and R15), and a sum does
    // not move while the body rewraps mid-drag, which a bottom edge did.
    private double AboveTheChart
        => NightHeaderPart.Bounds.Height + NightMetricsSection.Margin.Top + NightMetricsSection.HeaderHeight
            + NightTimelinePart.Margin.Top + NightTimelinePart.Bounds.Height + NightMetricsPart.Margin.Top;

    // A change above the chart moves the floor and the chart's share alike, whether or not the cap moves.
    private void RefitLanes()
    {
        LanesHandle.Refresh();
        FitChart();
    }

    // The chart takes the cap's remainder, floored so a stored value under it never squashes the
    // plot; whole pixels so the content never runs a fraction past the cap and draws a scroll bar.
    private void FitChart()
    {
        if (double.IsFinite(LanesRegion.MaxHeight))
        {
            NightMetricsPart.Height = Math.Max(ChartFloor, Math.Floor(LanesRegion.MaxHeight - AboveTheChart));
        }
    }

    protected override LanesHandle.Limits LanesLimits()
        => new(AboveTheChart + ChartFloor, NightReviewRegion.Bounds.Height - LanesHandle.Thickness - FramesMinHeight, LanesFloor);
}
