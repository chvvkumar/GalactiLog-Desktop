using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.VisualTree;
using GalactiLog.App.Tests.Views.TargetDetail.Parts;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Phd2;
using GalactiLog.Data.Queries;
using Cards = GalactiLog.App.Tests.TestSupport.SessionCardViewModelTestFactory;
using Factory = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;

namespace GalactiLog.App.Tests.TestSupport;

/// <summary>The night and the measurements the two layout view test files share.</summary>
internal static class TargetLayoutProbes
{
    // A night with 40 frames, a frame table, a metric chart and a guide graph, which the page
    // factory's own nights do not carry. guideFromFirstFrame: the guide log starts at the first frame.
    // expand: false returns the night before its detail loads, for a case that shows it first.
    public static Cards.Harness RealNight(Factory.Harness page, int findings = 0, bool guideFromFirstFrame = false, bool twoRigs = false, bool expand = true)
    {
        var date = Factory.LastSession;
        var frames = NightPartsTestKit.Frames(40, date);
        if (twoRigs)
        {
            frames = [.. frames.Select((frame, index) => frame with { Rig = index % 2 == 0 ? "Alpha / Cam" : "Bravo / Cam" })];
        }

        var holder = new TargetPageState();
        var night = Cards.Create(
            detail: findings == 0
                ? Cards.PopulatedDetail(sessionDate: date) with { Frames = frames }
                : Cards.PopulatedDetail(
                    sessionDate: date,
                    insights: [.. Enumerable.Range(1, findings).Select(n => new SessionInsight(
                        InsightLevel.Warning, "hfr_outliers", $"Finding number {n} is a sentence long enough to be trimmed."))]) with { Frames = frames },
            targetPage: holder,
            anyGuideLogs: true);
        night.FrameTableResult = NightPartsTestKit.Table(frames, holder);
        night.ChartResult = new SessionChartViewModel(night.Detail!, page.Selection);
        night.GuidingResult = new Phd2NightGuiding(new Phd2NightSummary { SessionCount = 1 }, [NightPartsTestKit.GuideSession(0)]);
        night.FramesResult = _ => guideFromFirstFrame
            ? NightPartsTestKit.GuideFrames() with { StartedAtUtc = frames[0].CaptureDate }
            : NightPartsTestKit.GuideFrames();
        if (!expand)
        {
            return night;
        }

        night.Card.IsExpanded = true;
        night.Settle();
        night.SettleGuiding().SettleFrames();
        return night;
    }

    // How far a visible descendant outside any scroll viewer reaches past the host's bottom edge.
    public static double Overflow(Control host)
    {
        var worst = double.NegativeInfinity;
        foreach (var child in host.GetVisualDescendants().OfType<Control>())
        {
            if (!child.IsEffectivelyVisible || child.Bounds.Height <= 0
                || child.GetVisualAncestors().TakeWhile(a => !ReferenceEquals(a, host)).OfType<ScrollViewer>().Any())
            {
                continue;
            }

            if (child.TranslatePoint(new Point(0, child.Bounds.Height), host) is { } bottom)
            {
                worst = Math.Max(worst, bottom.Y - host.Bounds.Height);
            }
        }

        return worst;
    }

    // A control's bounds in another visual's coordinates.
    public static Rect BoundsIn(Control control, Visual root)
        => new(control.TranslatePoint(default, root) ?? default, control.Bounds.Size);

    public static int VerticalBars(Control host)
        => host.GetVisualDescendants().Append(host).OfType<ScrollBar>()
            .Count(bar => bar.Orientation == Orientation.Vertical && bar.IsEffectivelyVisible
                && bar.Bounds is { Width: > 0, Height: > 0 });
}
