using System.Text.RegularExpressions;
using Xunit;

namespace GalactiLog.App.Tests.Views;

// Avalonia 11.3's Fluent ProgressBar runs its indeterminate animation on the process-wide clock
// for as long as IsIndeterminate is true, whether or not the bar is visible or even still in a
// window (AvaloniaUI/Avalonia #12922, #13139, #16648, open). A constant IsIndeterminate="True"
// therefore keeps the compositor awake at rest and roots the whole page the bar sits in after the
// page closes: the 2026-10-12 heap dumps showed every closed Target detail page retained through
// PageLoadingLine. Every indeterminate bar binds the flag to the state it reports instead.
public class IndeterminateProgressBarScanTest
{
    private static readonly Regex ConstantIndeterminate = new(
        @"IsIndeterminate\s*=\s*""\s*True\s*""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Fact]
    public void NoAxamlSetsIsIndeterminateToAConstantTrue()
    {
        var root = Path.Combine(FindRepoRoot(), "src");
        var offenders = new List<string>();
        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*.axaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            scanned++;
            foreach (Match match in ConstantIndeterminate.Matches(File.ReadAllText(file)))
            {
                offenders.Add($"{Path.GetRelativePath(root, file)}: {match.Value}");
            }
        }

        Assert.True(scanned > 0, $"No .axaml files were scanned under {root}.");
        Assert.True(
            offenders.Count == 0,
            "A constant IsIndeterminate keeps Fluent's progress animation on the global clock forever "
            + "and roots the page after it closes; bind it to the loading flag. Offenders:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
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
