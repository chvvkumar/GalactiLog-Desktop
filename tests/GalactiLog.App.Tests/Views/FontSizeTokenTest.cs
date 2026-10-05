using System.Text.RegularExpressions;
using Xunit;

namespace GalactiLog.App.Tests.Views;

// Task 8 review item 2. Scales.axaml's FontSize* keys are ratios (0.500 to 0.786), not point
// sizes, so binding one to a FontSize property renders text at well under a pixel. Three separate
// tasks have now shipped that bug (Task 7 item 1, Task 8 item 2 in two files), which makes it a
// convention nobody remembers rather than a mistake somebody made. This is the choke point: a
// plain text scan of every .axaml under src/, in the same shape as
// GalactiLog.Core.Tests FileSafetyTest, so a fourth recurrence fails the build instead of
// shipping. Phase 9 is where the ratio keys get a real mechanism (a converter or resolved point
// sizes); until then, nothing in src/ may bind FontSize to one.
public class FontSizeTokenTest
{
    // FontSize="{DynamicResource FontSizeCaption}", FontSize="{StaticResource FontSizeMicro}",
    // and the same through a property-element or a Setter. Matches the key name, not the markup
    // extension, so any binding form is caught.
    private static readonly Regex ForbiddenBinding = new(
        @"FontSize\s*=\s*""\s*\{\s*(?:Dynamic|Static)Resource\s+FontSize\w*\s*\}\s*""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ForbiddenSetter = new(
        @"Property\s*=\s*""[\w.]*FontSize""[^>]*Value\s*=\s*""\s*\{\s*(?:Dynamic|Static)Resource\s+FontSize\w*\s*\}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Fact]
    public void NoAxamlBindsFontSizeToAFontSizeRatioKey()
    {
        var viewsRoot = Path.Combine(FindRepoRoot(), "src");
        var offenders = new List<string>();
        var scanned = 0;

        foreach (var file in Directory.EnumerateFiles(viewsRoot, "*.axaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            scanned++;
            var text = File.ReadAllText(file);
            var relative = Path.GetRelativePath(viewsRoot, file);

            foreach (Match match in ForbiddenBinding.Matches(text))
            {
                offenders.Add($"{relative}: {match.Value.Trim()}");
            }

            foreach (Match match in ForbiddenSetter.Matches(text))
            {
                offenders.Add($"{relative}: {match.Value.Trim()}");
            }
        }

        Assert.True(scanned > 0, $"No .axaml files were scanned under {viewsRoot}.");
        Assert.True(
            offenders.Count == 0,
            "Scales.axaml's FontSize* keys are ratios, not point sizes; binding one to FontSize "
            + "renders sub-pixel text. Offenders:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
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
