using GalactiLog.App.Views.TargetDetail.Parts;

namespace GalactiLog.App.Views.TargetDetail.Layouts;

/// <summary>The width rule of the region that holds the frames table, shared by every layout that
/// places one: the compact chrome under <see cref="WideWidth"/>, the wide chrome from it.</summary>
public static class FramesColumn
{
    public const double WideWidth = 800d;

    public static void Apply(FramesPart frames, double width) => frames.IsCompact = width < WideWidth;
}
