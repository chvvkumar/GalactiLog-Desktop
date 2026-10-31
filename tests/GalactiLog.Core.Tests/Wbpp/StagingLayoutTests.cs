using GalactiLog.Core.Wbpp;
using Xunit;

namespace GalactiLog.Core.Tests.Wbpp;

public class StagingLayoutTests
{
    private const string Root = @"W:\Astro\Incoming";
    private static readonly DateOnly N18 = new(2026, 10, 18), N19 = new(2026, 10, 19), N20 = new(2026, 10, 20), N21 = new(2026, 10, 21);

    private static string Lights(string date) => Path.Combine(Root, "2026", "Date_" + date, "LIGHT", "ZWO ASI533MM Pro", "Target_Pleiades", "Angle_45.18");
    private static string Flats(string date) => Path.Combine(Root, "2026", "Date_" + date, "FLAT", "ZWO ASI533MM Pro", "Angle_45.18");
    private static CopySource S(DateOnly night, string path, string root = Root, string[]? files = null, string[]? dirs = null)
        => new(night, root, path, files ?? [], dirs);
    private static IEnumerable<string> Entries(StagingPlan plan) => plan.Operations.Select(o => o.EntryName);

    [Fact]
    public void FourNightsUnderOneYearFolder_StartAtTheYear_AndKeepTheirDateFolders()
    {
        var plan = StagingLayout.Plan([S(N18, Lights("2026-10-18")), S(N19, Lights("2026-10-19")), S(N20, Lights("2026-10-20")), S(N21, Lights("2026-10-21"))]);

        Assert.Equal(@"W:\Astro\Incoming\2026", plan.StartFolder);
        Assert.Equal(
            [@"2026\Date_2026-10-18\LIGHT\ZWO ASI533MM Pro\Target_Pleiades\Angle_45.18",
             @"2026\Date_2026-10-19\LIGHT\ZWO ASI533MM Pro\Target_Pleiades\Angle_45.18",
             @"2026\Date_2026-10-20\LIGHT\ZWO ASI533MM Pro\Target_Pleiades\Angle_45.18",
             @"2026\Date_2026-10-21\LIGHT\ZWO ASI533MM Pro\Target_Pleiades\Angle_45.18"],
            Entries(plan));
        Assert.Equal([N18, N19, N20, N21], plan.Operations.Select(o => o.Night));
        Assert.All(plan.Operations, o => Assert.Null(o.DestinationSubfolder));
    }

    [Fact]
    public void TwoNightsWithFlats_KeepLightsAndFlatsUnderTheirDateFolders()
    {
        var plan = StagingLayout.Plan([S(N18, Lights("2026-10-18")), S(N19, Lights("2026-10-19")), S(N18, Flats("2026-10-18")), S(N19, Flats("2026-10-19"))]);

        Assert.Equal(@"W:\Astro\Incoming\2026", plan.StartFolder);
        Assert.Equal(
            [@"2026\Date_2026-10-18\LIGHT\ZWO ASI533MM Pro\Target_Pleiades\Angle_45.18",
             @"2026\Date_2026-10-19\LIGHT\ZWO ASI533MM Pro\Target_Pleiades\Angle_45.18",
             @"2026\Date_2026-10-18\FLAT\ZWO ASI533MM Pro\Angle_45.18",
             @"2026\Date_2026-10-19\FLAT\ZWO ASI533MM Pro\Angle_45.18"],
            Entries(plan));
    }

    [Fact]
    public void OneNightLightsOnly_FallsBackToTheScanRoot()
    {
        var plan = StagingLayout.Plan([S(N18, Lights("2026-10-18"))]);

        Assert.Equal(Root, plan.StartFolder);
        Assert.Equal(@"2026\Date_2026-10-18\LIGHT\ZWO ASI533MM Pro\Target_Pleiades\Angle_45.18", Assert.Single(plan.Operations).EntryName);

        var empty = StagingLayout.Plan([]);
        Assert.Null(empty.StartFolder);
        Assert.Empty(empty.Operations);
    }

    [Fact]
    public void OneNightWithFlats_StartsAtTheDateFolderTheyShare_AndKeepsIt()
    {
        var plan = StagingLayout.Plan([S(N18, Lights("2026-10-18")), S(N18, Flats("2026-10-18"))]);

        Assert.Equal(@"W:\Astro\Incoming\2026\Date_2026-10-18", plan.StartFolder);
        Assert.Equal(
            [@"Date_2026-10-18\LIGHT\ZWO ASI533MM Pro\Target_Pleiades\Angle_45.18", @"Date_2026-10-18\FLAT\ZWO ASI533MM Pro\Angle_45.18"],
            Entries(plan));
    }

    [Fact]
    public void TwoScanRoots_WithACollidingRelativePath_TakeTheNightPrefix_ThenASuffix()
    {
        var plan = StagingLayout.Plan([
            S(N18, @"W:\Astro\Incoming\M31\LIGHT"),
            S(N19, @"X:\Archive\M31\LIGHT", root: @"X:\Archive"),
            S(N20, @"X:\Archive\2026-10-18_M31\LIGHT", root: @"X:\Archive")]);

        Assert.Null(plan.StartFolder);
        Assert.Equal([@"2026-10-18_M31\LIGHT", @"2026-10-19_M31\LIGHT", @"2026-10-18_M31_2\LIGHT"], Entries(plan));
    }

    [Fact]
    public void TwoScanRoots_WithANestedRelativePath_Collide()
    {
        // M31 and M31\FLAT\Ha would write one folder tree, so both take their night.
        var twoNights = StagingLayout.Plan([
            S(N18, @"W:\Astro\Incoming\M31"),
            S(N19, @"X:\Archive\m31\FLAT\Ha", root: @"X:\Archive")]);
        Assert.Equal([@"2026-10-18_M31", @"2026-10-19_m31\FLAT\Ha"], Entries(twoNights));

        // One night on both: the prefix still nests, so the suffix pass separates them.
        var oneNight = StagingLayout.Plan([
            S(N18, @"W:\Astro\Incoming\M31\FLAT\Ha"),
            S(N18, @"X:\Archive\M31", root: @"X:\Archive")]);
        Assert.Equal([@"2026-10-18_M31\FLAT\Ha", @"2026-10-18_M31_2"], Entries(oneNight));
    }

    [Fact]
    public void TwoScanRoots_WithoutACollision_AreRelativeToTheirOwnRoot()
    {
        var plan = StagingLayout.Plan([S(N18, @"W:\Astro\Incoming\M31\LIGHT"), S(N19, @"X:\Archive\NGC7000\LIGHT", root: @"X:\Archive")]);

        Assert.Null(plan.StartFolder);
        Assert.Equal([@"M31\LIGHT", @"NGC7000\LIGHT"], Entries(plan));
    }

    [Fact]
    public void TheSameSourceFromTwoNights_IsOneOperation_ExcludingOnlyWhatBothNightsExclude()
    {
        var shared = Path.Combine(Root, "2026", "Date_2026-10-18", "LIGHT");
        var plan = StagingLayout.Plan([
            S(N18, shared, files: ["a.fits", "bad.fits"], dirs: ["OIII"]),
            S(N19, shared, files: ["b.fits", "bad.fits"], dirs: ["OIII", "Ha"])]);

        var op = Assert.Single(plan.Operations);
        Assert.Equal(N18, op.Night);
        Assert.Equal(Root, plan.StartFolder);
        Assert.Equal(@"2026\Date_2026-10-18\LIGHT", op.EntryName);
        // Each list is relative to its own night: a.fits is wanted by N19, b.fits by N18.
        Assert.Equal(["bad.fits"], op.ExcludedRelativePaths);
        Assert.Equal(["OIII"], op.ExcludedRelativeDirectories);
    }

    [Fact]
    public void ANestedSource_IsDropped_WhicheverOrderItArrivesIn()
    {
        var date = Path.Combine(Root, "2026", "Date_2026-10-18");

        foreach (var sources in new CopySource[][] { [S(N18, date), S(N19, Flats("2026-10-18"))], [S(N19, Flats("2026-10-18")), S(N18, date)] })
        {
            var op = Assert.Single(StagingLayout.Plan(sources).Operations);
            Assert.Equal(date, op.SourcePath);
            Assert.Equal(N18, op.Night);
            Assert.Equal(@"2026\Date_2026-10-18", op.EntryName);
        }
    }

    [Fact]
    public void ADroppedInnerSource_KeepsItsFrames_ThatTheOuterListsAsForeign()
    {
        // Night 18 at M31 lists night 19's Ha frames as foreign; night 19 at M31\Ha wants them.
        var m31 = Path.Combine(Root, "M31");
        var outer = S(N18, m31, files: [@"Ha\n19.fits", @"Ha\rej19.fits", @"OIII\rej18.fits"]);
        var inner = S(N19, Path.Combine(m31, "Ha"), files: ["n18.fits", "rej19.fits"]);

        foreach (var sources in new CopySource[][] { [outer, inner], [inner, outer] })
        {
            var op = Assert.Single(StagingLayout.Plan(sources).Operations);
            Assert.Equal(m31, op.SourcePath);
            Assert.Equal(N18, op.Night);
            // n19 is wanted by its own night, n18 by the outer; rej19 both nights leave out, and
            // OIII is covered by the outer alone.
            Assert.Equal([@"Ha\rej19.fits", @"OIII\rej18.fits"], op.ExcludedRelativePaths.Order(StringComparer.OrdinalIgnoreCase));
            Assert.Null(op.ExcludedRelativeDirectories);
        }
    }

    [Fact]
    public void ASharedFolder_LeavesOutAFileOnlyWhenEveryNightExcludesIt_ByFileOrDirectory()
    {
        // Night 19 unticks OIII, so its list names nothing there. Night 18 lists night 19's OIII
        // frame as foreign and its own rejected OIII frame.
        var m31 = Path.Combine(Root, "M31");
        var plan = StagingLayout.Plan([
            S(N18, m31, files: [@"OIII\n19.fits", @"OIII\rej18.fits", @"Ha\n19.fits"]),
            S(N19, m31, files: [@"Ha\n18.fits"], dirs: ["OIII"])]);

        var op = Assert.Single(plan.Operations);
        // Both OIII files are excluded by both nights; Ha\n19 is wanted by night 19, and OIII
        // itself is wanted by night 18 (its other OIII frames copy).
        Assert.Equal([@"OIII\n19.fits", @"OIII\rej18.fits"], op.ExcludedRelativePaths);
        Assert.Null(op.ExcludedRelativeDirectories);
    }

    [Fact]
    public void ANestedSourceUnderAnExcludedDirectoryOfTheOuter_IsItsOwnOperation()
    {
        // Part C's shape: the outer leaves FLAT out, so the inner tick copies it on its own.
        var date = Path.Combine(Root, "2026", "Date_2026-10-18");
        var plan = StagingLayout.Plan([S(N18, date, dirs: ["FLAT"]), S(N19, Flats("2026-10-18"))]);

        Assert.Equal(Root, plan.StartFolder);
        Assert.Equal([@"2026\Date_2026-10-18", @"2026\Date_2026-10-18\FLAT\ZWO ASI533MM Pro\Angle_45.18"], Entries(plan));
    }

    [Fact]
    public void MatchingIsCaseInsensitive_ForSourcesRootsAndTheSharedAncestor()
    {
        var same = StagingLayout.Plan([S(N18, Lights("2026-10-18")), S(N19, Lights("2026-10-18").ToLowerInvariant(), root: Root.ToLowerInvariant())]);
        Assert.Single(same.Operations);
        Assert.Equal(Root, same.StartFolder);

        var mixed = StagingLayout.Plan([S(N18, Lights("2026-10-18")), S(N19, Lights("2026-10-19").ToUpperInvariant())]);
        Assert.Equal(@"W:\Astro\Incoming\2026", mixed.StartFolder);
        Assert.Equal(
            [@"2026\Date_2026-10-18\LIGHT\ZWO ASI533MM Pro\Target_Pleiades\Angle_45.18",
             @"2026\DATE_2026-10-19\LIGHT\ZWO ASI533MM PRO\TARGET_PLEIADES\ANGLE_45.18"],
            Entries(mixed));
    }

    [Fact]
    public void ADriveRootScanRoot_IsTheStartFolder_AndEntriesAreRelativeToIt()
    {
        var one = StagingLayout.Plan([S(N18, @"D:\M31\LIGHT", root: @"D:\")]);
        Assert.Equal(@"D:\", one.StartFolder);
        Assert.Equal(@"M31\LIGHT", Assert.Single(one.Operations).EntryName);

        var two = StagingLayout.Plan([S(N18, @"D:\M31\LIGHT", root: @"D:\"), S(N19, @"D:\NGC7000\LIGHT", root: @"D:\")]);
        Assert.Equal(@"D:\", two.StartFolder);
        Assert.Equal([@"M31\LIGHT", @"NGC7000\LIGHT"], Entries(two));
    }

    [Fact]
    public void TwoRootsWhoseNamesSharePrefix_AreTwoRoots()
    {
        var plan = StagingLayout.Plan([S(N18, @"W:\Astro\M31\LIGHT", root: @"W:\Astro"), S(N19, @"W:\Astro2\M31\LIGHT", root: @"W:\Astro2")]);

        Assert.Null(plan.StartFolder);
        Assert.Equal([@"2026-10-18_M31\LIGHT", @"2026-10-19_M31\LIGHT"], Entries(plan));
    }

    [Fact]
    public void ATrailingSeparator_ChangesNothing()
    {
        var plan = StagingLayout.Plan([S(N18, Lights("2026-10-18") + @"\", root: Root + @"\")]);

        Assert.Equal(Root, Path.TrimEndingDirectorySeparator(plan.StartFolder!));
        Assert.Equal(@"2026\Date_2026-10-18\LIGHT\ZWO ASI533MM Pro\Target_Pleiades\Angle_45.18", Assert.Single(plan.Operations).EntryName);
    }

    [Fact]
    public void NightsInTwoYears_StartAtTheScanRoot()
    {
        var plan = StagingLayout.Plan([S(N18, Lights("2026-10-18")), S(N19, Lights("2026-10-19").Replace(@"\2026\", @"\2025\"))]);

        Assert.Equal(Root, plan.StartFolder);
        Assert.Equal(
            [@"2026\Date_2026-10-18\LIGHT\ZWO ASI533MM Pro\Target_Pleiades\Angle_45.18",
             @"2025\Date_2026-10-19\LIGHT\ZWO ASI533MM Pro\Target_Pleiades\Angle_45.18"],
            Entries(plan));
    }

    [Fact]
    public void Source_UsesTheOneContainmentRule()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "GalactiLog.Core", "Wbpp", "StagingLayout.cs"));
        Assert.Contains("PathConfinement.IsUnderOrEqual", source, StringComparison.Ordinal);
        Assert.DoesNotContain("StartsWith(", source, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GalactiLog.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new DirectoryNotFoundException("GalactiLog.sln not found above test output directory.");
    }
}
