using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.App.Views.Settings;
using GalactiLog.Core.Settings;
using Xunit;

namespace GalactiLog.App.Tests.Views.Settings;

// Design-spec 18.3's view smoke tests for spec amendment 4c's External Tools settings tab (Phase
// 21 Task 4). No database: the tab is built directly with fakes, the shape every other Settings
// tab's own view test file uses (CustomColumnsTabViewTests.cs).
public class ExternalToolsTabViewTests
{
    private static string ViewPath =>
        Path.Combine(SourceScan.SrcRoot(), "GalactiLog.App", "Views", "Settings", "ExternalToolsTabView.axaml");

    private static Window Show(Control view)
    {
        var window = new Window { Width = 1000, Height = 700, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static ExternalToolsTabViewModel NewTab() => new(
        load: () => new GeneralSettings(),
        mutateGeneral: mutate => mutate(new GeneralSettings()),
        knownFilters: () => ["Ha", "OIII"],
        post: action => action());

    [AvaloniaFact]
    public void TheTab_ConstructsAndLaysOutNonZero()
    {
        var view = new ExternalToolsTabView { DataContext = NewTab() };
        var window = Show(view);
        Dispatcher.UIThread.RunJobs();

        Assert.True(view.Bounds.Width > 0);
        Assert.True(view.Bounds.Height > 0);
    }

    [Fact]
    public void TheTab_CarriesTheFourHelpGlyphsAndNoOthers()
    {
        var source = File.ReadAllText(ViewPath);
        var topics = Regex.Matches(source, @"<controls:HelpButton\s+Topic=""([^""]*)""")
            .Select(match => match.Groups[1].Value)
            .ToList();

        Assert.Equal(
            new[]
            {
                "settings.external-tools.astrobin-filters",
                "settings.external-tools.bortle",
                "settings.external-tools.nina",
                "settings.external-tools.stellarium",
            },
            topics);
    }

    [Fact]
    public void TheTab_DeclaresNoStyleOfItsOwn()
    {
        // The same source check ControlStyleScanTest itself uses over every view under src/, and
        // the shape CustomColumnsTabViewTests.TheTab_DeclaresNoStyleOfItsOwn already takes.
        var source = File.ReadAllText(ViewPath);
        Assert.DoesNotContain("<Style ", source);
    }

    // ---- phase-review P2 (:31): a rendered row proves the compiled binding, not an empty list -

    [AvaloniaFact]
    public async Task ARenderedInstanceRow_BindsTheRemoveCommandAndTheNotOfferedCaption()
    {
        var tab = NewTab();
        // The constructor's background load can still be in flight; without waiting for it, its
        // publish can clear the row added below out from under the visual tree this case reads.
        await tab.PendingLoad!;
        tab.AddNinaInstanceCommand.Execute(null);
        var row = tab.NinaInstances.Single();
        var view = new ExternalToolsTabView { DataContext = tab };
        var window = Show(view);
        Dispatcher.UIThread.RunJobs();

        var removeButton = view.GetVisualDescendants().OfType<Button>()
            .Single(button => ReferenceEquals(button.CommandParameter, row));
        Assert.NotNull(removeButton.Command);
        Assert.True(removeButton.Command!.CanExecute(row));

        var caption = view.GetVisualDescendants().OfType<TextBlock>()
            .Single(block => block.Text == "Not offered on the target page until it has a name and a URL."
                              && ReferenceEquals(block.DataContext, row));
        Assert.True(caption.IsVisible);

        // wave2-review P2 (:106): the caption hides once UrlError has something to say, so the
        // two lines never draw over one another.
        row.Url = "not a url";
        Dispatcher.UIThread.RunJobs();

        Assert.False(caption.IsVisible);
    }
}
