using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Controls;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.Views.TargetDetail.Layouts;
using GalactiLog.App.Views.TargetDetail.Parts;
using Xunit;
using Cards = GalactiLog.App.Tests.TestSupport.SessionCardViewModelTestFactory;
using Factory = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;
using Rows = GalactiLog.App.Tests.ViewModels.NightFilterMatrixViewModelTests;

namespace GalactiLog.App.Tests.Views.TargetDetail.Layouts;

// Spec 9.3: every layout carries every part, on screen or one click away.
public sealed class LayoutParityCensusTests
{
    [AvaloniaFact]
    public void EveryLayout_ShowsEveryPartOrDeclaresTheControlThatOpensIt()
    {
        // A failure looks like a layout other than Question Modes still censused.
        Assert.Equal(["modes"], TargetLayoutRegistry.All.Select(r => r.Key));
        var failures = TargetLayoutRegistry.All.SelectMany(Census).ToList();
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [AvaloniaFact]
    public void Census_AnEmptyPartInABorderedSizedHost_IsReportedHidden()
    {
        var part = new ContentControl { Content = new Border { BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray } };
        TargetPartHost.Show(new Border { Height = 300, BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray, Child = part });

        Assert.True(part.Bounds is { Width: > 0, Height: > 0 });
        Assert.False(IsShown(part));
    }

    [AvaloniaFact]
    public void Census_AnOpenerThatIsHiddenOrOpensSomethingElse_IsReported()
    {
        var current = TargetLayoutRegistry.Default;
        // IntegrationBarsPart to CompareNightsButton fails only if each opener runs alone:
        // IntegrationButton would otherwise open it.
        foreach (var (part, opener) in new[]
        {
            (typeof(TrendChartPart), "GradingTally"),
            (typeof(TrendChartPart), "IntegrationButton"),
            (typeof(IntegrationBarsPart), "CompareNightsButton"),
        })
        {
            var map = new Dictionary<Type, string>(current.OneClickAway) { [part] = opener };
            var failures = Census(current with { OneClickAway = map });

            Assert.Contains(failures, f => f.Contains(part.Name) && f.Contains($"'{opener}'"));
        }
    }

    // A closed part keeps a sized root, empty panels and bare borders in a host that carries a
    // size, so a part is shown only when a leaf that draws content inside it is on screen.
    internal static bool IsShown(Control part)
        => part.GetVisualDescendants().Append(part).OfType<Control>().Any(
            e => IsContentLeaf(e) && e.IsEffectivelyVisible && HasArea(e));

    private static bool IsContentLeaf(Control e) => e switch
    {
        TextBlock t => !string.IsNullOrWhiteSpace(t.Text) || t.Inlines is { Count: > 0 },
        Shape or Image => true,
        // An editable field is its part's content whether or not it holds text.
        TextBox => true,
        NightStrip or GuideGraph or MetricChartView or AltitudeArc or CalendarHeatmap => true,
        _ => false,
    };

    private static bool HasArea(Control e) => e.Bounds is { Width: > 0, Height: > 0 };

    // An Expander opener is the section itself: its one click is the header toggle (R17).
    private static ICommand? OpenCommand(Control opener)
        => opener switch
        {
            Button b => b.Command,
            DisclosureBand d => d.ToggleCommand,
            Expander e => new RelayCommand(() => e.IsExpanded = !e.IsExpanded),
            _ => null,
        };

    private static List<string> Census(TargetLayout row)
    {
        var failures = new List<string>();
        var hidden = new List<(Type Part, string Opener)>();
        Mount(row, view =>
        {
            foreach (var part in TargetLayoutRegistry.Parts)
            {
                var found = Find(view, part);
                if (found.Count != 1)
                {
                    failures.Add($"layout '{row.Key}': {found.Count} elements of {part.Name}, expected exactly one");
                    continue;
                }

                if (IsShown(found[0])) continue;

                if (!row.OneClickAway.TryGetValue(part, out var opener))
                    failures.Add($"layout '{row.Key}': {part.Name} is hidden and has no OneClickAway entry");
                else
                    hidden.Add((part, opener));
            }
        });

        // Each opener runs alone on a fresh view, so no other opener can show a part mapped to it.
        foreach (var group in hidden.GroupBy(h => h.Opener, h => h.Part))
        {
            var opener = group.Key;
            var ran = false;
            Mount(row, view =>
            {
                var control = view.NamedOrNull<Control>(opener);
                var command = control is null ? null : OpenCommand(control);
                var parameter = control is Button b ? b.CommandParameter : null;
                string? fault =
                    control is null ? $"is one click away through '{opener}', which is not in the tree"
                    : !control.IsEffectivelyVisible || !HasArea(control) ? $"opener '{opener}' is not on screen"
                    : command is null ? $"opener '{opener}' has no command"
                    : !control.IsEffectivelyEnabled || !command.CanExecute(parameter) ? $"opener '{opener}' is disabled"
                    : null;
                if (fault is not null)
                {
                    failures.AddRange(group.Select(p => $"layout '{row.Key}': {p.Name} {fault}"));
                    return;
                }

                command!.Execute(parameter);
                ran = true;
            }, view =>
            {
                if (!ran) return;
                foreach (var part in group)
                {
                    if (Find(view, part) is not [var found] || !IsShown(found))
                        failures.Add($"layout '{row.Key}': {part.Name} is still hidden after opener '{opener}' ran");
                }
            });
        }

        return failures;
    }

    // Logical as well as visual: a hidden scroll viewer has no template yet, so its content is a
    // logical child only.
    private static List<Control> Find(Control view, Type part)
        => view.GetVisualDescendants().OfType<Control>().Concat(view.GetLogicalDescendants().OfType<Control>())
            .Where(e => e.GetType() == part).Distinct().ToList();

    // The fixture is full because every part needs content to be shown at all; the night
    // filter rows give the Compare and Integration tables theirs.
    private static void Mount(TargetLayout row, Action<Control> act, Action<Control>? afterSettle = null)
    {
        using var harness = Factory.Create(
            get: _ => Factory.PopulatedDetail() with
            {
                NightFilters =
                [
                    Rows.Row(Factory.LastSession, "Ha", 3_600d, 12, [(300d, 12)], hfr: 2.1d),
                    Rows.Row(Factory.FirstSession, "OIII", 1_800d, 6, [(300d, 6)], hfr: 2.4d),
                ],
            },
            fullNight: true,
            post: action => Dispatcher.UIThread.Post(action)).Settle();
        var view = (Control)Activator.CreateInstance(row.ViewType)!;
        view.DataContext = harness.ViewModel;
        var window = new Window { Width = 1600, Height = 900, Content = view };
        window.Show();
        // Through the dispatcher and until no load is left, as the layout cases settle, so a load a
        // drain starts is joined too and no publish reaches a bound part from a pool thread.
        void Settle() => TargetPartHost.SettleLoads(harness.ViewModel, []);
        Settle();
        Assert.NotNull(harness.ViewModel.SelectedSession);
        act(view);
        if (afterSettle is not null)
        {
            Settle();
            afterSettle(view);
        }

        Settle();
        window.Close();
    }
}
