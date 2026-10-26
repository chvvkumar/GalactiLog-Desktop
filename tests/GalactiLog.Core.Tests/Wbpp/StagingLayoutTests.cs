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
    public void TwoScanRoots_WithoutACollision_AreRelativeToTheirOwnRoot()
    {
        var plan = StagingLayout.Plan([S(N18, @"W:\Astro\Incoming\M31\LIGHT"), S(N19, @"X:\Archive\NGC7000\LIGHT", root: @"X:\Archive")]);

        Assert.Null(plan.StartFolder);
        Assert.Equal([@"M31\LIGHT", @"NGC7000\LIGHT"], Entries(plan));
    }

    [Fact]
    public void TheSameSourceFromTwoNights_IsOneOperation_WithBothNightsExcludedFiles()
    {
        var shared = Path.Combine(Root, "2026", "Date_2026-10-18", "LIGHT");
        var plan = StagingLayout.Plan([
            S(N18, shared, files: ["a.fits"], dirs: ["OIII"]),
            S(N19, shared, files: ["b.fits"], dirs: ["OIII", "Ha"])]);

        var op = Assert.Single(plan.Operations);
        Assert.Equal(N18, op.Night);
        Assert.Equal(Root, plan.StartFolder);
        Assert.Equal(@"2026\Date_2026-10-18\LIGHT", op.EntryName);
        Assert.Equal(["a.fits", "b.fits"], op.ExcludedRelativePaths);
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
