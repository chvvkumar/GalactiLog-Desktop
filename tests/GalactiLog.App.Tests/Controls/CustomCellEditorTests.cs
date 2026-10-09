using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Controls;
using GalactiLog.App.Tests.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.CustomColumns;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.CustomColumnTestFactory;

namespace GalactiLog.App.Tests.Controls;

// Phase 20 Task 3, brief section 7 cases 19 to 25. Spec 12.15's one cell editor as the control
// rather than as the view-model: the kind it realizes, the click and the keys it swallows so the
// row behind it does not open, the refusal it carries on both the tooltip and the help text, the
// style it declares nowhere, and the rig label row template that has to draw exactly what it drew
// before this phase on a night with no rig-scope column.
//
// The row probe is a Button, which is the shape the dashboard target row already takes
// (TargetListViewTests line 415 asserts the one Button carrying a TargetRowViewModel), wrapped in a
// container that watches every press on its way past. What the probe records is whether an
// UNHANDLED press survived the cell, because that is the one thing a clickable row acts on: a
// Button's own press handling, a row's PointerPressed handler and a row's Tapped all ignore a press
// already marked handled. Counting the Button's Click alone would be vacuous here, since a pointer
// captured inside the cell never completes a click on the Button whatever this control does.
public class CustomCellEditorTests
{
    private const double Gutter = 24d;

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    [AvaloniaFact]
    public void EachType_RealizesItsOwnControlAndOnlyThatOne()
    {
        var (_, check) = Show(Factory.Cell(Factory.Boolean()));
        Assert.True(Editor<CheckBox>(check).IsVisible);
        Assert.False(Editor<TextBox>(check).IsVisible);
        Assert.False(Editor<ComboBox>(check).IsVisible);

        var (_, text) = Show(Factory.Cell(Factory.Text()));
        Assert.True(Editor<TextBox>(text).IsVisible);
        Assert.False(Editor<CheckBox>(text).IsVisible);
        Assert.False(Editor<ComboBox>(text).IsVisible);

        var (_, choice) = Show(Factory.Cell(Factory.Dropdown("High", "Low")));
        Assert.True(Editor<ComboBox>(choice).IsVisible);
        Assert.False(Editor<CheckBox>(choice).IsVisible);
        Assert.False(Editor<TextBox>(choice).IsVisible);
    }

    [AvaloniaFact]
    public void APointerPressOnTheEditor_DoesNotReachTheRowBehindIt()
    {
        // Spec 12.15: "Clicking an editor does not open the row." All three kinds, because none of
        // the three Avalonia editors marks the press handled on its own: without this control's own
        // handler the press arrives at the row unhandled and the row opens under the cell.
        foreach (var cell in new[]
                 {
                     Factory.Cell(Factory.Boolean()),
                     Factory.Cell(Factory.Text()),
                     Factory.Cell(Factory.Dropdown("High", "Low")),
                 })
        {
            using var scope = cell;
            var (window, editor, row) = HostInARow(cell);
            var control = cell.IsCheckBox ? Editor<CheckBox>(editor)
                : cell.IsTextBox ? Editor<TextBox>(editor)
                : (Control)Editor<ComboBox>(editor);

            Click(window, At(window, control, 0.5d, 0.5d));

            Assert.Equal(1, row.Presses);
            Assert.Equal(0, row.Unhandled);
            Assert.Equal(0, row.Opens);
        }
    }

    [AvaloniaFact]
    public void ThePaddingAroundTheEditor_StillReachesTheRow()
    {
        // The other half, so the swallow is not over-broad: the cell's own gutter is not an editor
        // and a press there opens the row exactly as a press on the rest of the row does.
        var (window, editor, row) = HostInARow(Factory.Cell(Factory.Boolean()));

        Click(window, At(window, editor, 0d, 0d) + new Point(2d, 2d));

        Assert.Equal(1, row.Presses);
        Assert.Equal(1, row.Unhandled);
    }

    [AvaloniaFact]
    public async Task SpaceOnTheCheckBox_TogglesAndWrites()
    {
        var log = new Factory.WriteLog();
        using var cell = Factory.Cell(Factory.Boolean(), write: log.Write);
        var (window, editor, row) = HostInARow(cell);

        Focus(Editor<CheckBox>(editor));
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        await Factory.SettleAsync(cell);

        // A behaviour pin, not evidence for this control's Space branch: Avalonia's toggle button
        // marks Space handled in its own class handler before the key reaches this control's root,
        // so the key never leaves the cell whatever this file does. The arm that does falsify the
        // branch is the text box one below.
        Assert.True(cell.IsChecked);
        Assert.Single(log.Calls);
        Assert.Equal(0, row.UnhandledKeys);
    }

    [AvaloniaFact]
    public void SpaceOnTheTextBox_TypesASpaceAndIsNotSwallowed()
    {
        // Ruling C25, and the defect Task 5a's launched look found: "ab cd" was typed in as "abcd".
        // A handled key down costs the character, because the Win32 backend drops the character
        // message that follows one, so Space on the text kind must leave this control unhandled.
        //
        // The headless backend cannot reproduce the platform's coupling: its key press raises only
        // the key events and its text input is a separate call, so a handled key down costs it
        // nothing and the old form of this case passed against the defect. What it CAN express is
        // the branch itself, which is the whole of the fix: the key leaves the cell unhandled. The
        // character is asserted as well, delivered the way the platform delivers it, so the case
        // also fails if the text stops arriving for some other reason.
        var delay = new FakeDelay();
        using var cell = Factory.Cell(Factory.Text(), stored: "ab", delay: delay.Delay);
        var (window, editor, row) = HostInARow(cell);

        var box = Editor<TextBox>(editor);
        Focus(box);
        box.CaretIndex = box.Text?.Length ?? 0;

        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);

        // The branch. One unhandled Space left the cell, which is what the platform needs in order
        // to turn it into a character at all.
        Assert.Equal(1, row.UnhandledKeys);

        window.KeyTextInput(" ");
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyTextInput("cd");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("ab cd", cell.Text!.Text);

        // And the row did not activate on it. This is a reading of what the row does with an
        // unhandled Space, not a claim about this control: the cell no longer intercepts the key, so
        // whether a row acts on one is the row's own business and its own case.
        Assert.Equal(0, row.Opens);
    }

    [AvaloniaFact]
    public void EnterOnTheTextBox_IsStillSwallowed()
    {
        // The other half of ruling C25: Enter types nothing, so swallowing it costs nothing and it
        // still has to stop here, or the row opens the moment the user presses Enter to commit.
        var delay = new FakeDelay();
        using var cell = Factory.Cell(Factory.Text(), delay: delay.Delay);
        var (window, editor, row) = HostInARow(cell);

        Focus(Editor<TextBox>(editor));
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, row.UnhandledKeys);
    }

    [AvaloniaFact]
    public async Task EnterOnTheTextBox_CommitsAndKeepsFocus()
    {
        var delay = new FakeDelay();
        var log = new Factory.WriteLog();
        using var cell = Factory.Cell(Factory.Text(), write: log.Write, delay: delay.Delay);
        var (window, editor, row) = HostInARow(cell);

        var box = Editor<TextBox>(editor);
        Focus(box);
        cell.Text!.Text = "typed";

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        // FlushAsync, not SettleAsync: the flush cancels the debounce window that PendingWrite
        // points at, so settling alone completes on the cancellation while the write is still
        // running on another thread, and the assertion below would be a race rather than a wait
        // (review P2-3).
        await cell.FlushAsync().WaitAsync(Budget);

        // Committed without waiting the window out, focus kept, and the key never left the cell: a
        // TextBox that does not accept return leaves Enter to bubble, and a row drawn as a Button
        // claims it.
        Assert.Equal("typed", Assert.Single(log.Calls).Value);
        Assert.True(box.IsFocused);
        Assert.Equal(0, row.UnhandledKeys);
    }

    [AvaloniaFact]
    public async Task EscapeOnTheTextBox_RevertsAndWritesNothing()
    {
        var delay = new FakeDelay();
        var log = new Factory.WriteLog();
        using var cell = Factory.Cell(
            Factory.Text(), stored: "kept", write: log.Write, delay: delay.Delay);
        var (window, editor, _) = HostInARow(cell);

        Focus(Editor<TextBox>(editor));
        cell.Text!.Text = "abandoned";

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("kept", cell.Text.Text);

        // Releasing every parked window afterwards still writes nothing: the pending write is
        // cancelled rather than merely superseded.
        delay.Release();
        await Factory.SettleAsync(cell);
        Assert.Empty(log.Calls);
    }

    [AvaloniaFact]
    public async Task TabOffTheTextBox_Commits()
    {
        var delay = new FakeDelay();
        var log = new Factory.WriteLog();
        using var cell = Factory.Cell(Factory.Text(), write: log.Write, delay: delay.Delay);
        var (_, editor, row) = HostInARow(cell);

        Focus(Editor<TextBox>(editor));
        cell.Text!.Text = "typed then tabbed away from";

        // Tab moves on, which is the loss of focus the commit hangs on: one mechanism for Tab and
        // for a click away rather than a branch each.
        Focus(row.Button);
        await cell.FlushAsync().WaitAsync(Budget);

        Assert.Equal("typed then tabbed away from", Assert.Single(log.Calls).Value);
    }

    [AvaloniaFact]
    public async Task TheEditor_CarriesItsRefusalOnBothTheTooltipAndTheHelpText()
    {
        var refusal = new CustomWriteResult(
            CustomWriteStatus.NotABoolean, CustomColumnMessages.NotABoolean);
        var log = new Factory.WriteLog { Answer = _ => refusal };

        // The dispatcher post rather than the inline one, because this is the case whose write
        // reaches a bound property: a refusal published off the UI thread is a binding update off
        // the UI thread.
        using var cell = Factory.Cell(
            Factory.Boolean(), write: log.Write, post: action => Dispatcher.UIThread.Post(action));
        var (_, editor) = Show(cell);
        var box = Editor<CheckBox>(editor);

        Assert.Null(ToolTip.GetTip(box));
        Assert.DoesNotContain("refused", box.Classes);

        cell.IsChecked = true;
        await Factory.SettleAsync(cell);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(CustomColumnMessages.NotABoolean, ToolTip.GetTip(box));
        Assert.Equal(CustomColumnMessages.NotABoolean, AutomationProperties.GetHelpText(box));
        Assert.Contains("refused", box.Classes);

        // And the automation name is the column's, not the control's default.
        Assert.Equal(cell.AutomationName, AutomationProperties.GetName(box));
    }

    [Fact]
    public void TheEditor_DeclaresNoStyleOfItsOwn()
    {
        // The same rule ControlStyleScanTest enforces over every view, failing here by name rather
        // than in a census of every file: the one style this control needs is the refused-cell ink
        // in Theme/Controls.axaml, which that walk skips outright.
        var markup = File.ReadAllText(Path.Combine(
            SourceScan.SrcRoot(), "GalactiLog.App", "Controls", "CustomCellEditor.axaml"));

        Assert.DoesNotContain("<Style", markup, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void RigLabelRowTemplate_DrawsNothingExtraWithNoCells()
    {
        // The case that protects every session pane pin on a library with no rig-scope column.
        var row = RigLabelRowViewModel.For("Askar FMA180 / ASI2600MC", 22);
        var (_, host) = ShowTemplated(row);

        Assert.Empty(host.GetVisualDescendants().OfType<CustomCellEditor>());

        // And the strip itself is not drawn at all, which is the load-bearing half: a visible strip
        // with no items still takes the row's own 8 pixel spacing.
        Assert.DoesNotContain(
            host.GetVisualDescendants().OfType<ItemsControl>(), strip => strip.IsEffectivelyVisible);

        // Not a placeholder either: the row is the two runs it has always been.
        var texts = host.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible)
            .Select(block => block.Text)
            .ToList();
        Assert.Equal<string?[]>(["Askar FMA180 / ASI2600MC", "22 frames"], [.. texts]);
    }

    [AvaloniaFact]
    public void RigLabelRowTemplate_DrawsOneEditorPerCell()
    {
        // And the strip is not inert: the same template with cells draws one editor each, which is
        // what Task 6b fills.
        var row = RigLabelRowViewModel.For(
            "Askar FMA180 / ASI2600MC",
            22,
            [
                Factory.Cell(Factory.Boolean(scope: CustomColumnScope.Rig)),
                Factory.Cell(Factory.Text(scope: CustomColumnScope.Rig)),
            ]);

        var (_, host) = ShowTemplated(row);

        Assert.Equal(2, host.GetVisualDescendants().OfType<CustomCellEditor>().Count());
    }

    [AvaloniaFact]
    public void ByDefault_ATextCellsWatermarkIsTheColumnName()
    {
        // The three surfaces with no heading over the cell, the dashboard target row, the dashboard
        // night expander and the session pane's rig line, have nothing else naming the column on an
        // empty cell, so the default has to stay what it was.
        var (_, editor) = Show(Factory.Cell(Factory.Text("Notes tag")));

        Assert.True(editor.ShowWatermark);
        Assert.Equal("Notes tag", Editor<TextBox>(editor).Watermark);
    }

    [AvaloniaFact]
    public void WithTheWatermarkOff_TheCellShowsTheDashAndStillNamesItselfToAScreenReader()
    {
        // The Nights ledger's own row strip. It prints the column's name once in the header strip
        // directly above the rows, so every empty text cell under it repeated a word already on
        // screen, seven times on a seven night ledger (phase review target P3-7, from the capture).
        // Red against the tree before this property: the watermark below is "Notes tag".
        //
        // The second half is what makes the first half safe: the automation name is untouched, so a
        // screen reader still hears the column and the night on a cell with no watermark.
        var cell = Factory.Cell(Factory.Text("Notes tag"), subject: "2026-03-14");
        var (_, editor) = Show(cell);
        editor.ShowWatermark = false;
        Dispatcher.UIThread.RunJobs();

        var box = Editor<TextBox>(editor);
        Assert.Equal(MetricText.Missing, box.Watermark);
        Assert.Equal("Notes tag, 2026-03-14", AutomationProperties.GetName(box));
    }

    [AvaloniaFact]
    public void WithTheWatermarkOff_ARecycledCellKeepsTheDash()
    {
        // A row's editor is realized once and handed one cell after another as the list scrolls, so
        // the watermark follows the DataContext, not the construction. A failure here is an editor
        // that draws the previous cell's column name, or the name it suppressed coming back on the
        // second cell.
        var (_, editor) = Show(Factory.Cell(Factory.Text("Notes tag")));
        editor.ShowWatermark = false;
        editor.DataContext = Factory.Cell(Factory.Text("Another column"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(MetricText.Missing, Editor<TextBox>(editor).Watermark);

        editor.ShowWatermark = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Another column", Editor<TextBox>(editor).Watermark);
    }

    private static T Editor<T>(CustomCellEditor editor) where T : Control
        => editor.GetVisualDescendants().OfType<T>().First();

    private static void Focus(Control control)
    {
        control.Focus();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(Window window, Point point)
    {
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    // A point inside a control, as a fraction of its own bounds, in the window's coordinates.
    private static Point At(Window window, Control control, double x, double y)
    {
        var point = control.TranslatePoint(
            new Point(control.Bounds.Width * x, control.Bounds.Height * y), window);
        Assert.NotNull(point);
        return point.Value;
    }

    private static (Window Window, CustomCellEditor Editor) Show(CustomValueViewModel cell)
    {
        var editor = new CustomCellEditor
        {
            DataContext = cell,
            Padding = new Thickness(Gutter),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var window = new Window { Width = 600, Height = 200, Content = editor };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, editor);
    }

    private static (Window Window, CustomCellEditor Editor, RowProbe Row) HostInARow(
        CustomValueViewModel cell)
    {
        var editor = new CustomCellEditor
        {
            DataContext = cell,
            Padding = new Thickness(Gutter),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        // The gutter is hit testable here although the shipped control paints no background of its
        // own, so that a press on the cell's padding is a press this control could swallow and the
        // over-broad case below is a real question rather than a hit test that misses.
        editor.Background = Brushes.Transparent;

        var probe = new RowProbe(editor);
        var window = new Window { Width = 600, Height = 200, Content = probe.Button };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, editor, probe);
    }

    private static (Window Window, ContentControl Host) ShowTemplated(RigLabelRowViewModel row)
    {
        Assert.True(
            Application.Current!.TryGetResource("RigLabelRowTemplate", null, out var resource),
            "Theme/Controls.axaml no longer declares RigLabelRowTemplate.");

        var host = new ContentControl
        {
            ContentTemplate = Assert.IsAssignableFrom<IDataTemplate>(resource),
            Content = row,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var window = new Window { Width = 800, Height = 200, Content = host };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, host);
    }

    // The clickable row a cell sits on, with a watcher between the cell and the row: every press
    // that leaves the cell passes the watcher before the row sees it, and a press still unhandled
    // there is the one that opens the row.
    private sealed class RowProbe
    {
        public RowProbe(Control cell)
        {
            Watcher = new Border { Child = cell };
            Watcher.AddHandler(
                InputElement.PointerPressedEvent,
                OnPress,
                RoutingStrategies.Bubble,
                handledEventsToo: true);
            Watcher.AddHandler(
                InputElement.KeyDownEvent,
                OnKey,
                RoutingStrategies.Bubble,
                handledEventsToo: true);

            Button = new Button
            {
                Content = Watcher,
                Padding = new Thickness(Gutter),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            };

            Button.AddHandler(Avalonia.Controls.Button.ClickEvent, OnClick);
        }

        public Button Button { get; }

        public Border Watcher { get; }

        /// <summary>Every press that left the cell, handled or not.</summary>
        public int Presses { get; private set; }

        /// <summary>The presses still unhandled on the way out, which is what opens a row.</summary>
        public int Unhandled { get; private set; }

        /// <summary>The keys still unhandled on the way out, which is what a row acts on.</summary>
        public int UnhandledKeys { get; private set; }

        /// <summary>The row's own click.</summary>
        public int Opens { get; private set; }

        private void OnPress(object? sender, PointerPressedEventArgs e)
        {
            Presses++;
            if (!e.Handled)
            {
                Unhandled++;
            }
        }

        private void OnKey(object? sender, KeyEventArgs e)
        {
            if (!e.Handled)
            {
                UnhandledKeys++;
            }
        }

        // Click bubbles, and a CheckBox is itself a Button, so only the row's own click counts as
        // the row opening.
        private void OnClick(object? sender, RoutedEventArgs e)
        {
            if (ReferenceEquals(e.Source, Button))
            {
                Opens++;
            }
        }
    }
}
