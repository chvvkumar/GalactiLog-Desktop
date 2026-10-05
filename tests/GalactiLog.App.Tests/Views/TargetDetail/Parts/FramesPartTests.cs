using Xunit.Abstractions;
using GalactiLog.App.Views.TargetDetail;
using Avalonia.Controls.Diagnostics;
using Avalonia.Controls.Primitives;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia;
using GalactiLog.App.Controls;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.CustomColumns;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.ViewModels;
using GalactiLog.App.Views.TargetDetail.Parts;
using GalactiLog.Core.Help;
using GalactiLog.Core.Phd2;
using GalactiLog.Core.Settings;
using Xunit;
using static GalactiLog.App.Tests.Views.TargetDetail.Parts.ThumbnailKit;
using static GalactiLog.App.Tests.Views.TargetDetail.Parts.NightPartsTestKit;
using Cards = GalactiLog.App.Tests.TestSupport.SessionCardViewModelTestFactory;
using Page = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;
using Shapes = Avalonia.Controls.Shapes;

namespace GalactiLog.App.Tests.Views.TargetDetail.Parts;

// Content cases of FramesPart and its CompareToSegment: the grading chrome and the baseline segment.
public class FramesPartTests(ITestOutputHelper output)
{
    private static (FramesPart Host, Cards.Harness Harness) GradedPart(
        TargetPageSettings? stored = null, int frameCount = 12, double width = 1600, double height = 900)
        => Graded(card => new FramesPart { DataContext = card }, stored, frameCount, width, height);

    private static (CompareToSegment Host, Cards.Harness Harness) GradedSegment(TargetPageSettings? stored = null)
        => Graded(card => new CompareToSegment { DataContext = card }, stored);

    [AvaloniaFact]
    public void FramesPart_TheSegment_ReflectsTheStoredBaseline()
    {
        var fresh = GradedSegment();
        using (fresh.Harness)
        {
            Assert.True(fresh.Host.Named<ToggleButton>("CompareToSession").IsChecked);
            Assert.False(fresh.Host.Named<ToggleButton>("CompareToRig").IsChecked);
        }

        var stored = GradedSegment(new TargetPageSettings { GradingBaseline = "rig" });
        using (stored.Harness)
        {
            Assert.False(stored.Host.Named<ToggleButton>("CompareToSession").IsChecked);
            Assert.True(stored.Host.Named<ToggleButton>("CompareToRig").IsChecked);
        }
    }

    [AvaloniaFact]
    public void FramesPart_ClickingThisRig_FlipsTheBaseline()
    {
        var (segment, harness) = GradedSegment();
        using var scope = harness;

        // The command path, which is the programmatic way in. Since a review the buttons
        // themselves bind IsSessionBaseline and its negation two way and carry no Command, so the
        // command is driven from the card here; the case below covers the buttons.
        harness.Card.SetGradingBaselineCommand.Execute(GradingBaseline.Rig);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(GradingBaseline.Rig, harness.TargetPage.GradingBaseline);
        Assert.True(segment.Named<ToggleButton>("CompareToRig").IsChecked);
        Assert.False(segment.Named<ToggleButton>("CompareToSession").IsChecked);

        harness.Card.SetGradingBaselineCommand.Execute(GradingBaseline.Session);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(GradingBaseline.Session, harness.TargetPage.GradingBaseline);
        Assert.True(segment.Named<ToggleButton>("CompareToSession").IsChecked);
        Assert.False(segment.Named<ToggleButton>("CompareToRig").IsChecked);
    }

    /// <summary>
    /// The gap the case above leaves: that one drives the view-model and never
    /// touches the control, so it cannot see what an actual click does.
    /// </summary>
    /// <remarks>
    /// The defect this pins: a <c>ToggleButton</c> flips its own <c>IsChecked</c> through
    /// <c>SetCurrentValue</c> before it runs anything, so with the segment's original one-way
    /// binding plus a command, clicking the button that was already lit unchecked the control and
    /// then wrote the holder a value it already held. Nothing re-pushed the binding and the whole
    /// segment went dark. Raising the notifications unconditionally does not fix it either, which
    /// this case proved before the binding changed: a one-way binding does not re-push a source
    /// value that has not changed. The segment now binds one settable property and its negation two
    /// way, so the invariant below holds by construction rather than by remembering to notify.
    /// </remarks>
    [AvaloniaFact]
    public void FramesPart_ClickingTheCheckedButton_LeavesTheSegmentLit()
    {
        var (segment, harness) = GradedSegment();
        using var scope = harness;

        var session = segment.Named<ToggleButton>("CompareToSession");
        var rig = segment.Named<ToggleButton>("CompareToRig");

        AssertSegmentIsLit(harness, session, rig);
        Assert.True(session.IsChecked);

        // A real click, through the control, on the button that is already checked. This is the
        // click that used to leave the segment dark.
        Click(segment, session);
        AssertSegmentIsLit(harness, session, rig);

        // A real click on the other button.
        Click(segment, rig);
        AssertSegmentIsLit(harness, session, rig);

        // And the checked button again, on the other side of the segment.
        Click(segment, rig);
        AssertSegmentIsLit(harness, session, rig);

        Click(segment, session);
        AssertSegmentIsLit(harness, session, rig);
    }

    /// <summary>The segment's invariant, and the one a review found broken: exactly one of the
    /// two buttons is lit, and the lit one is the baseline the holder actually carries. Asserted
    /// after every click rather than at the end, so the click that breaks it is the one that
    /// fails.</summary>
    private static void AssertSegmentIsLit(
        TestSupport.SessionCardViewModelTestFactory.Harness harness,
        ToggleButton session,
        ToggleButton rig)
    {
        Assert.True(
            session.IsChecked != rig.IsChecked,
            $"the segment reads session {session.IsChecked} and rig {rig.IsChecked}, which is not exactly one lit");

        var baseline = harness.TargetPage.GradingBaseline;
        Assert.Equal(baseline == GradingBaseline.Session, session.IsChecked);
        Assert.Equal(baseline == GradingBaseline.Rig, rig.IsChecked);
        Assert.Equal(baseline == GradingBaseline.Session, harness.Card.IsSessionBaseline);
        Assert.Equal(baseline == GradingBaseline.Rig, harness.Card.IsRigBaseline);
    }

    private static void Click(Control host, Control target)
        => TargetPartHost.Click(Assert.IsAssignableFrom<Window>(host.GetVisualRoot()), target);

    [AvaloniaFact]
    public void FramesPart_TheLegend_CarriesTheAdvisorySentence()
    {
        // Spec 12.4 calls this sentence not optional chrome: the application writes no user file
        // and a page that colours frames red owes the reader that statement where the colour is.
        var (part, harness) = GradedPart();
        using var scope = harness;

        Assert.Contains("Grading is advisory. No frame is deleted or hidden.", VisibleTexts(part));

        // P24 R23: the two marked bands only; a failure looks like a better or neutral entry back.
        var legend = part.Named<WrapPanel>("GradingLegend");
        var labels = legend.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToList();
        Assert.DoesNotContain("better", labels);
        Assert.DoesNotContain("neutral", labels);
        Assert.Contains("watch", labels);
        Assert.Contains("reject", labels);

        // Two dots, one per marked band.
        Assert.Equal(2, legend.GetVisualDescendants().OfType<Shapes.Ellipse>().Count());
    }

    [AvaloniaFact]
    public void FramesPart_TheTallyCounts_RenderInTheBandInks()
    {
        var (part, harness) = GradedPart();
        using var scope = harness;

        Assert.True(Application.Current!.TryFindResource("ColorSuccess", out var success));
        Assert.True(Application.Current!.TryFindResource("ColorWarning", out var warning));
        Assert.True(Application.Current!.TryFindResource("ColorError", out var error));

        var good = part.Named<TextBlock>("TallyGoodCount");
        var watch = part.Named<TextBlock>("TallyWatchCount");
        var reject = part.Named<TextBlock>("TallyRejectCount");

        Assert.Equal(
            ((ISolidColorBrush)success!).Color,
            Assert.IsAssignableFrom<ISolidColorBrush>(good.Foreground).Color);
        Assert.Equal(
            ((ISolidColorBrush)warning!).Color,
            Assert.IsAssignableFrom<ISolidColorBrush>(watch.Foreground).Color);
        Assert.Equal(
            ((ISolidColorBrush)error!).Color,
            Assert.IsAssignableFrom<ISolidColorBrush>(reject.Foreground).Color);

        // And the three brushes carry the three colours spec 12.4's band table names: the *Value
        // keys are Color resources and the markup binds the brushes built from them.
        Assert.True(Application.Current!.TryFindResource("ColorSuccessValue", out var successValue));
        Assert.Equal((Color)successValue!, ((ISolidColorBrush)success).Color);
    }

    [AvaloniaFact]
    public void FramesPart_TheTallyAndLegend_AreAboveTheTable()
    {
        // Spec 12.4: "The three lines above the table are Auto rows of the Frames section itself
        // and the table is its starred row". FramesChrome is a StackPanel and the table is not
        // inside it, which is what keeps the table in a bounded parent.
        var (part, harness) = GradedPart();
        using var scope = harness;

        var section = part.Named<Grid>("FramesSection");
        var chrome = part.Named<StackPanel>("FramesChrome");
        var region = part.Named<ContentControl>("FrameTableRegion");

        // P24 R13: the compact form's filter line is the second Auto row.
        Assert.Equal(3, section.RowDefinitions.Count);
        Assert.False(section.RowDefinitions[0].Height.IsStar);
        Assert.False(section.RowDefinitions[1].Height.IsStar);
        Assert.True(section.RowDefinitions[2].Height.IsStar);
        Assert.Equal(0, Grid.GetRow(chrome));
        Assert.Equal(1, Grid.GetRow(part.Named<ContentControl>("FilterLineHost")));
        Assert.Equal(2, Grid.GetRow(region));

        // The table is not a descendant of the chrome, which is the sentence the brief says to
        // read twice: a StackPanel parent gives the table infinite height.
        Assert.DoesNotContain(
            chrome.GetVisualDescendants(),
            descendant => ReferenceEquals(descendant, region));

        // Both chrome lines really are above the table's top edge.
        var chromeBottom = chrome.TranslatePoint(new Point(0, chrome.Bounds.Height), section)!.Value.Y;
        var tableTop = region.TranslatePoint(new Point(0, 0), section)!.Value.Y;
        Assert.True(
            chromeBottom <= tableTop + 0.5d,
            $"the chrome ends at {chromeBottom} and the table starts at {tableTop}");
    }

    [AvaloniaFact]
    public void FramesPart_TheRigPillRow_IsOnTheToolbarLine_AboveTheLegendAndTheColumnHeader()
    {
        var (part, harness) = RigHost(card => new FramesPart { DataContext = card }, [RigA, RigB]);
        using var scope = harness;

        var toolbar = part.Named<Grid>("FrameTableToolbar");
        var legend = part.Named<WrapPanel>("GradingLegend");
        var pills = part.Named<ItemsControl>("RigPillRow");
        var header = part.Named<Control>("FrameTableHeader");

        // Spec 12.4's order, on three lines: the tally, then the rig pills on the toolbar's line
        // beside the title, then the legend, then the table (night pane round).
        Assert.Contains(toolbar.GetVisualDescendants(), descendant => ReferenceEquals(descendant, pills));
        Assert.True(pills.IsEffectivelyVisible);

        var legendTop = legend.TranslatePoint(new Point(0, 0), part)!.Value.Y;
        var legendBottom = legend.TranslatePoint(new Point(0, legend.Bounds.Height), part)!.Value.Y;
        var pillsBottom = pills.TranslatePoint(new Point(0, pills.Bounds.Height), part)!.Value.Y;
        var headerTop = header.TranslatePoint(new Point(0, 0), part)!.Value.Y;

        Assert.True(pillsBottom <= legendTop + 0.5d, $"the pills end at {pillsBottom}, below the legend's {legendTop}");
        Assert.True(legendBottom <= headerTop + 0.5d, $"the legend ends at {legendBottom}, below the column header's {headerTop}");

        // Two pills, both checked on open, and neither declares a ToggleButton style of its own.
        var buttons = pills.GetVisualDescendants().OfType<ToggleButton>().ToList();
        Assert.Equal(2, buttons.Count);
        Assert.All(buttons, button => Assert.True(button.IsChecked));
    }

    [AvaloniaFact]
    public void FramesPart_A200FrameSession_RealisesFarFewerThan200Rows()
    {
        // The acceptance check the Task 6 review named. The frame table has no viewport of its own
        // any more, so the pane's starred row is what bounds it; an Auto row or a StackPanel
        // anywhere between the pane and the table realises every row and brings back the 605 ms
        // stall TRACKING section 6 item 12 records.
        var date = Page.LastSession;
        var frames = Frames(200, date);
        using var harness = Cards.Create(
            detail: Cards.PopulatedDetail(sessionDate: date) with { Frames = frames });
        var table = Table(frames);
        harness.FrameTableResult = table;
        harness.Card.IsExpanded = true;
        harness.Settle();

        var pane = new FramesPart { DataContext = harness.Card };
        Show(pane);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(200, table.Rows.Count);
        Assert.Same(table, pane.Named<ContentControl>("FrameTableRegion").Content);

        var realised = pane.GetVisualDescendants()
            .OfType<Border>()
            .Count(border => border.Classes.Contains("frame-row"));

        Assert.InRange(realised, 1, 60);
    }

    [AvaloniaFact]
    public void FramesPart_ASingleRigNight_RendersExactlyAsBefore()
    {
        // The roadmap's Verify clause for this task, as a structural comparison rather than a
        // screenshot: one thumbnail box where a night used to have none, and nothing else new.
        var strip = RigHost(card => new SharpestFramePart { DataContext = card }, [RigA]);
        var filters = RigHost(card => new PerFilterTablePart { DataContext = card }, [RigA]);
        var ranges = RigHost(card => new RangesTablePart { DataContext = card }, [RigA]);
        var frames = RigHost(card => new FramesPart { DataContext = card }, [RigA]);
        using var stripScope = strip.Harness;
        using var filtersScope = filters.Harness;
        using var rangesScope = ranges.Harness;
        using var framesScope = frames.Harness;
        Dispatcher.UIThread.RunJobs();

        // One box, so the strip has no second one.
        Assert.Single(Placeholders(strip.Host));
        Assert.Single(Boxes(strip.Host));

        // No rig label row in either table.
        Assert.DoesNotContain(
            filters.Host.GetVisualDescendants().Concat(ranges.Host.GetVisualDescendants()).OfType<ContentControl>(),
            panel => panel.Name is "FilterRigLabelRow" or "RangeRigLabelRow" && panel.IsEffectivelyVisible);

        // The filter table still has exactly the row count a single-rig night had: one header plus
        // one filter row, and no block.
        Assert.Equal(2, TableRows(filters.Host, "FilterTable").Count);

        // No Rig column and no rig pill row.
        Assert.False(frames.Host.Named<ItemsControl>("RigPillRow").IsEffectivelyVisible);

        var table = frames.Host.Named<ContentControl>("FrameTableRegion")
            .GetVisualDescendants()
            .OfType<FrameTableView>()
            .Single();
        Assert.False(table.GetControl<TextBlock>("RigColumnHeader").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void FramesPart_TheRigColumn_IsDrawnOnlyOnAMultiRigNight()
    {
        var (pane, harness) = RigHost(card => new FramesPart { DataContext = card }, [RigA, RigB]);
        using var scope = harness;

        var view = pane.Named<ContentControl>("FrameTableRegion")
            .GetVisualDescendants()
            .OfType<FrameTableView>()
            .Single();

        var header = view.GetControl<TextBlock>("RigColumnHeader");
        Assert.True(header.IsEffectivelyVisible);
        Assert.Equal("Rig", header.Text);

        // The column takes the existing text column width rather than declaring a figure of its
        // own, which is what keeps one more column from becoming one more constant.
        Assert.Equal(90d, header.Bounds.Width, 1);
    }

    [AvaloniaFact]
    public void FramesPart_TheRigColumn_WidensTheRowByOneTextColumn()
    {
        // Fixer-list section 2 item 20: the frame table's row-end actions cell already sits off
        // the window edge on a narrow pane, and this task adds a column to the same row. The two
        // figures the item needs, measured at the pane's shipped 492 px allotment.
        var single = RigHost(card => new FramesPart { DataContext = card }, [RigA], width: 492, height: 620);
        var multi = RigHost(card => new FramesPart { DataContext = card }, [RigA, RigB], width: 492, height: 620);
        using var singleScope = single.Harness;
        using var multiScope = multi.Harness;

        var singleRow = WidestRow(single.Host);
        var multiRow = WidestRow(multi.Host);

        output.WriteLine(
            $"492 px pane: frame row {singleRow:0.##} px single-rig, {multiRow:0.##} px multi-rig, " +
            $"delta {multiRow - singleRow:0.##} px");

        Assert.Equal(90d, multiRow - singleRow, 1);

        // A single-rig night's row is exactly the width it was, which is the half of item 20 this
        // task must not make worse for the nights that are most of a library.
        Assert.True(singleRow > 0d);
    }

    private const string Advisory = "Grading is advisory. No frame is deleted or hidden.";

    // A real key press on the focused help glyph, through the window's key input.
    private static void PressHelp(FramesPart part)
    {
        var glyph = part.Named<HelpButton>("FramesHelp");
        glyph.Focus();
        ((Window)TopLevel.GetTopLevel(part)!).KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    private static Visual? OpenFlyoutHost(HelpButton glyph)
        => glyph.Flyout is { IsOpen: true } flyout ? ((IPopupHostProvider)flyout).PopupHost as Visual : null;

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void FramesPart_EveryChromeControl_IsShownEnabledAndAboveTheColumnHeader_InBothForms(bool compact)
    {
        // Red if either form drops, hides or disables a control the chrome held before, or draws one
        // below the column header; the legend and the sentence are on the page in both forms since
        // the night pane round moved them under the toolbar line.
        var (part, harness) = RigHost(card => new FramesPart { DataContext = card, IsCompact = compact }, [RigA, RigB], width: compact ? 708 : 1600);
        using var scope = harness;
        var table = harness.Card.FrameTable!;
        var headerTop = part.Named<Control>("FrameTableHeader").TranslatePoint(default, part)!.Value.Y;

        foreach (var name in new[]
        {
            "FramesChrome", "GradingTally", "TallyGoodCount", "TallyWatchCount", "TallyRejectCount", "TallyMeanScore",
            "TallyUngraded", "FramesHelp", "GradingLegend", "GradingAdvisory", "RigPillRow", "CompareToSession",
            "CompareToRig", "ShownCount", "CopySelectedPathsButton", "RevealSelectedButton", "FrameColumnPicker",
            "OutlierPillRow", "ClearFiltersButton",
        })
        {
            var control = part.Named<Control>(name);
            // Clear filters is disabled by design while nothing is filtered (P24 R6).
            Assert.True(control.IsEnabled || name == "ClearFiltersButton", $"{name} is disabled");
            var conditional = (name == "TallyMeanScore" && !table.HasMeanScore) || (name == "TallyUngraded" && !table.HasUngraded);
            if (conditional)
            {
                continue;
            }

            Assert.True(control.IsEffectivelyVisible && part.GetVisualDescendants().Contains(control), $"{name} is not shown, compact {compact}");
            var bottom = control.TranslatePoint(new Point(0, control.Bounds.Height), part)!.Value.Y;
            Assert.True(bottom <= headerTop + 0.5, $"{name} ends at {bottom}, below the column header's top {headerTop}");
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void FramesPart_TheLegend_IsUnderTheToolbarLineAndAboveTheColumnHeader_InBothForms(bool compact)
    {
        // Red if the legend is back on the tally line or behind the compact form's help glyph: the
        // night pane round put it under the toolbar line, with the outlier pills, and above the
        // column header, with the rows it explains.
        var (part, harness) = RigHost(card => new FramesPart { DataContext = card, IsCompact = compact }, [RigA, RigB], width: compact ? 708 : 1600);
        using var scope = harness;
        var legend = part.Named<WrapPanel>("GradingLegend");
        var toolbar = part.Named<Grid>("FrameTableToolbar");
        var header = part.Named<Control>("FrameTableHeader");

        Assert.True(legend.IsEffectivelyVisible, "the legend is hidden");
        Assert.Contains(part.GetVisualDescendants().OfType<FrameTableView>().Single(), legend.GetVisualAncestors());
        var toolbarBottom = toolbar.TranslatePoint(new Point(0, toolbar.Bounds.Height), part)!.Value.Y;
        var legendTop = legend.TranslatePoint(default, part)!.Value.Y;
        var legendBottom = legend.TranslatePoint(new Point(0, legend.Bounds.Height), part)!.Value.Y;
        var headerTop = header.TranslatePoint(default, part)!.Value.Y;
        Assert.True(legendTop >= toolbarBottom - 0.5, $"the legend starts at {legendTop}, above the toolbar's {toolbarBottom}, compact {compact}");
        Assert.True(legendBottom <= headerTop + 0.5, $"the legend ends at {legendBottom}, below the column header's {headerTop}, compact {compact}");
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void FramesPart_TheHelpGlyphsFlyoutHasNoLegend_AndTheLegendIsOnThePage(bool compact)
    {
        // Red if either form's help flyout carries the legend, or the legend and sentence leave the page.
        var (part, harness) = RigHost(card => new FramesPart { DataContext = card, IsCompact = compact }, [RigA, RigB], width: compact ? 708 : 1600);
        using var scope = harness;
        var glyph = part.Named<HelpButton>("FramesHelp");
        Assert.Contains(part.Named<Control>("GradingAdvisory"), part.GetVisualDescendants());

        PressHelp(part);
        var host = OpenFlyoutHost(glyph);
        Assert.NotNull(host);
        var texts = host!.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToList();
        Assert.Contains(HelpTopics.Get(glyph.Topic).Paragraph, texts);
        Assert.DoesNotContain(Advisory, texts);
        Assert.Contains(part.Named<Control>("GradingAdvisory"), part.GetVisualDescendants());
        PressHelp(part);
    }

    [AvaloniaFact]
    public void FramesPart_TheTableRebuiltTwiceAfterARescan_DrawsTheRigPillsInEachNewToolbar()
    {
        // Red if a table view built after the frames went null and came back cannot take the rig
        // pills (the old, detached view still holds them), so the build throws or the pills vanish.
        var (part, harness) = RigHost(card => new FramesPart { DataContext = card }, [RigA, RigB]);
        using var scope = harness;
        var frames = harness.Card.Detail!.Frames;
        var pills = part.Named<ItemsControl>("RigPillRow");
        var previous = part.GetVisualDescendants().OfType<FrameTableView>().Single();

        for (var pass = 1; pass <= 2; pass++)
        {
            harness.FrameTableResult = Table(frames, harness.TargetPage);
            harness.Card.Invalidate();
            harness.Settle();
            Dispatcher.UIThread.RunJobs();

            var view = part.GetVisualDescendants().OfType<FrameTableView>().Single();
            Assert.NotSame(previous, view);
            Assert.Same(view, pills.FindAncestorOfType<FrameTableView>());
            Assert.True(pills.IsEffectivelyVisible && pills.IsEffectivelyEnabled, $"the pills are hidden or disabled after rebuild {pass}");
            Assert.Equal(2, pills.GetVisualDescendants().OfType<ToggleButton>().Count());
            previous = view;
        }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void FramesPart_Compact_AtTwoRigsAnd708_KeepsTheTallyOnOneLineWithShortNames_AndTheLegendOnThePage(bool shortNames)
    {
        // Red if the compact form at 708 px with two short rig names and both outlier pills takes
        // more than one line for the chrome or more than two for the filter line (P24 R13), the
        // toolbar clips or overdraws anything, or the legend and the advisory sentence are off the
        // page. Long names are measured and recorded, not pinned.
        string[] rigs = shortNames ? ["Alpha / Cam", "Bravo / Cam"] : [RigA, RigB];
        var (part, harness, _) = FramesPartFilterTests.TwoRigHost(rigs, compact: true, width: 708);
        using var scope = harness;
        var chrome = part.Named<Control>("FrameTableHeader").TranslatePoint(default, part)!.Value.Y;
        var line = part.Named<Control>("FramesChrome").Bounds.Height;
        var toolbar = part.Named<Control>("FrameTableToolbar").Bounds.Height;
        var filterLine = part.Named<Control>("FilterLineHost").Bounds.Height;
        output.WriteLine($"compact, short names {shortNames}: chrome {chrome}, chrome line {line}, filter line {filterLine}, toolbar {toolbar} at {part.Bounds.Width} wide");
        if (shortNames)
        {
            Assert.True(line <= 30d, $"the chrome line is {line} tall, more than one line");
            Assert.True(FramesPartFilterTests.Pills(part).Count == 3 && part.Named<Control>("RigPillRow").IsEffectivelyVisible, "the fixture draws no pills");
            // Two pill lines of 26 with the 4 px line spacing and the host's 4 px margin.
            Assert.True(filterLine <= 60d, $"the filter line is {filterLine} tall, more than two lines");
            // The headless font misses Compare to's one line by 2 px at this width, so it wraps in its slot.
            NightPartsTestKit.AssertTheToolbarClipsNothing(part, mostLines: 3);
        }

        var glyph = part.Named<HelpButton>("FramesHelp");
        Assert.True(glyph.IsEffectivelyVisible && glyph.IsEffectivelyEnabled, "the help glyph is hidden or disabled");
        var advisory = part.Named<TextBlock>("GradingAdvisory");
        Assert.True(advisory.IsEffectivelyVisible && part.GetVisualDescendants().Contains(advisory), "the advisory sentence is off the page");
    }

    [AvaloniaFact]
    public void FramesPart_FlippingCompactTwice_WithTheTableRebuiltInEachForm_LeavesEachControlInOneHost()
    {
        // Red if a form change or a table rebuild leaves the rig pills, Compare to or the legend in
        // the wrong host, in two hosts, or throws on adopting them. P24 R13: the pills are on the
        // toolbar line in the wide form and on the filter line under the tally in the compact form.
        var (part, harness) = RigHost(card => new FramesPart { DataContext = card }, [RigA, RigB]);
        using var scope = harness;
        var frames = harness.Card.Detail!.Frames;
        var chrome = part.Named<StackPanel>("FramesChrome");
        var pills = part.Named<ItemsControl>("RigPillRow");
        var compare = part.Named<Control>("CompareToSegment");

        foreach (var compact in new[] { true, false, true, false })
        {
            part.IsCompact = compact;
            Dispatcher.UIThread.RunJobs();
            harness.FrameTableResult = Table(frames, harness.TargetPage);
            harness.Card.Invalidate();
            harness.Settle();
            Dispatcher.UIThread.RunJobs();

            var toolbar = part.Named<Grid>("FrameTableToolbar");
            bool In(Control host, Control control) => control.GetVisualAncestors().Contains(host);
            var filterLine = part.Named<ContentControl>("FilterLineHost");
            Assert.True(compact ? In(filterLine, pills) && !In(toolbar, pills) : In(toolbar, pills) && !In(filterLine, pills) && !In(chrome, pills),
                $"the rig pills are in the wrong host, compact {compact}");
            Assert.True(compact ? In(toolbar, compare) && !In(chrome, compare) : In(chrome, compare) && !In(toolbar, compare),
                $"Compare to is in the wrong host, compact {compact}");
            // The legend is the rebuilt table's own, under its toolbar, in both forms.
            var legend = part.Named<Control>("GradingLegend");
            Assert.True(In(toolbar.FindAncestorOfType<FrameTableView>()!, legend) && !In(chrome, legend), $"the legend is in the wrong host, compact {compact}");
            Assert.True(pills.IsEffectivelyVisible, $"the rig pills are hidden, compact {compact}");
            Assert.Equal(2, pills.GetVisualDescendants().OfType<ToggleButton>().Count());
            Assert.True(compare.IsEffectivelyVisible, $"Compare to is hidden, compact {compact}");
        }
    }

    [AvaloniaFact]
    public void FramesPart_OneRigWithNoWrap_TheChromeAboveTheColumnHeaderIsAtMostOneHundredPx()
    {
        // Red if the chrome above the column header takes more than three lines' height (the
        // tally line, the toolbar line and the legend line) on a single-rig night wide enough
        // that no line wraps.
        var (part, harness) = GradedPart();
        using var scope = harness;
        Assert.True(part.Named<Control>("GradingTally").IsEffectivelyVisible, "the fixture draws no tally");
        Assert.False(part.Named<Control>("RigPillRow").IsEffectivelyVisible, "the fixture has more than one rig");
        var chrome = part.Named<Control>("FrameTableHeader").TranslatePoint(default, part)!.Value.Y;
        output.WriteLine($"chrome above the column header {chrome} at {part.Bounds.Width} wide");
        Assert.True(chrome <= 100d, $"the chrome above the column header is {chrome} tall");
    }

    private static double WidestRow(Control pane)
        => pane.Named<ContentControl>("FrameTableRegion")
            .GetVisualDescendants()
            .OfType<Border>()
            .Where(border => border.Classes.Contains("frame-row"))
            .Select(border => border.Bounds.Width)
            .DefaultIfEmpty(0d)
            .Max();
}
