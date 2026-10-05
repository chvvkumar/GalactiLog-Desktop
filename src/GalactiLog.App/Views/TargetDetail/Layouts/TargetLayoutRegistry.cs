using GalactiLog.App.Views.TargetDetail.Parts;

namespace GalactiLog.App.Views.TargetDetail.Layouts;

public sealed record TargetLayout(
    string Key,
    Type ViewType,
    IReadOnlyDictionary<Type, string> OneClickAway);

public static class TargetLayoutRegistry
{
    public static IReadOnlyList<TargetLayout> All { get; } =
    [
        new("modes", typeof(ModesLayoutView), new Dictionary<Type, string>
        {
            // The parts of the two modes a fresh mount does not show; it opens on Night review.
            [typeof(TrendChartPart)] = "CompareNightsButton",
            [typeof(IntegrationBarsPart)] = "IntegrationButton",
            [typeof(CompareTablePart)] = "CompareNightsButton",
            [typeof(IntegrationTablesPart)] = "IntegrationButton",
        }),
    ];

    public static TargetLayout Default => All[0];

    public static IReadOnlyList<Type> Parts { get; } =
    [
        typeof(TargetHeaderPart),
        typeof(IntegrationBarsPart),
        typeof(TrendChartPart),
        typeof(NightsLedgerPart),
        typeof(NightHeaderPart),
        typeof(NightTimelinePart),
        typeof(NightMetricsPart),
        typeof(FramesPart),
        typeof(SharpestFramePart),
        typeof(PerFilterTablePart),
        typeof(RangesTablePart),
        typeof(CompareTablePart),
        typeof(IntegrationTablesPart),
    ];
}
