using GalactiLog.Core.Wbpp;
using Xunit;

namespace GalactiLog.Core.Tests.Wbpp;

public class TickModelTests
{
    private static readonly DateOnly N = new(2026, 10, 18);
    private const string Date = @"W:\Astro\Incoming\2026\Date_2026-10-18";
    private const string Light = Date + @"\LIGHT";
    private const string Lights = Light + @"\ZWO ASI533MM Pro\Target_Pleiades\Angle_45.18";
    private const string Flat = Date + @"\FLAT";
    private const string FlatCam = Flat + @"\ZWO ASI533MM Pro";
    private const string Flats = FlatCam + @"\Angle_45.18";
    private const string Flats96 = FlatCam + @"\Angle_96.26";

    private static FolderLevel L(string path, int frames = 0) => new(path, path.Split('\\').Length - 2, frames, [], [], 0);
    private static TickNode T(string path, bool ticked, params TickNode[] children) => new(L(path), ticked, children);
    private static IReadOnlyList<string> Paths(IEnumerable<ChosenLevel> sources) => [.. sources.Select(s => s.Level.Path)];
    private static IReadOnlyList<string> Skipped(ChosenLevel source) => [.. (source.SkippedFolders ?? []).Select(l => l.Path)];

    [Fact]
    public void ATickedRoot_IsOneSource_WithNoExcludedDirectory()
    {
        var sources = TickModel.CopySources(N, [T(Date, true, T(Light, true, T(Lights, true)))]);
        var source = Assert.Single(sources);
        Assert.Equal(Date, source.Level.Path);
        Assert.Equal(N, source.Night);
        Assert.Null(source.SkippedFolders);
    }

    [Fact]
    public void AnUntickedRowUnderATickedRoot_IsAnExcludedDirectoryOfThatSource()
    {
        var sources = TickModel.CopySources(N, [T(Date, true, T(Light, true), T(Flat, false))]);
        var source = Assert.Single(sources);
        Assert.Equal([Flat], Skipped(source));
    }

    [Fact]
    public void AnUntickedRowUnderTickedRows_IsExcludedFromTheNearestTickedAncestorsSource()
    {
        var sources = TickModel.CopySources(N, [T(Date, true, T(Flat, true, T(FlatCam, true, T(Flats, true), T(Flats96, false))))]);
        var source = Assert.Single(sources);
        Assert.Equal([Flats96], Skipped(source));
    }

    [Fact]
    public void ATickedRowUnderAnUntickedRowUnderATickedRoot_IsItsOwnSource()
    {
        var sources = TickModel.CopySources(N, [T(Date, true, T(Light, true), T(Flat, false, T(FlatCam, false, T(Flats, true), T(Flats96, false))))]);
        Assert.Equal([Date, Flats], Paths(sources));
        Assert.Equal([Flat], Skipped(sources[0]));
        Assert.Null(sources[1].SkippedFolders);
    }

    [Fact]
    public void ATickedRowUnderAnUntickedRoot_IsASource_AndTheRootContributesNothing()
    {
        var sources = TickModel.CopySources(N, [T(Date, false, T(Light, false, T(Lights, true)), T(Flat, false, T(FlatCam, false, T(Flats, true))))]);
        Assert.Equal([Lights, Flats], Paths(sources));
        Assert.All(sources, s => Assert.Null(s.SkippedFolders));
    }

    [Fact]
    public void NothingTicked_YieldsNoSource()
        => Assert.Empty(TickModel.CopySources(N, [T(Date, false, T(Light, false), T(Flat, false))]));

    [Fact]
    public void SeveralRoots_AreWalkedInOrder()
        => Assert.Equal([Flat, Date], Paths(TickModel.CopySources(N, [T(Flat, true), T(Date, true)])));
}
