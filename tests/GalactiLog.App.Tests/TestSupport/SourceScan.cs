using System.Text.RegularExpressions;

namespace GalactiLog.App.Tests.TestSupport;

/// <summary>
/// The plain text pass over <c>src/**</c> that this project's structural assertions share:
/// find the repository root, strip comments, and census the files whose source matches a pattern.
/// </summary>
/// <remarks>
/// <para>
/// The same shape <c>FileSafetyTest</c> established in <c>GalactiLog.Core.Tests</c> and
/// <c>ExportWriterContainmentTests</c> repeated here for Phase 10 Task 3. Phase 10 Task 4 adds
/// three more scans (the Velopack type census, the <c>ExplicitChannel</c> scan and the About tab's
/// no-composed-path scan), which is the second occurrence in this project and therefore the point
/// at which the helper is extracted rather than copied a third time (design-lessons rule 1).
/// The phase close (review finding P4) folded <c>ExportWriterContainmentTests</c>' private copy
/// onto this one, so this is now the only architecture text-scan implementation in this project,
/// which is what the collision map's designated-owner row asks for.
/// </para>
/// <para>
/// No Roslyn dependency: a regex pass is enough for a codebase this size, which is the ruling
/// <c>FileSafetyTest</c> already records.
/// </para>
/// </remarks>
internal static class SourceScan
{
    /// <summary>The <c>src</c> directory of the checkout the tests were built from.</summary>
    public static string SrcRoot() => Path.Combine(FindRepoRoot(), "src");

    /// <summary>
    /// The file names under <c>src/**</c> whose source, with comments stripped, matches
    /// <paramref name="pattern"/>, sorted so the assertion reads as a census rather than as an
    /// order-dependent list.
    /// </summary>
    public static string[] FilesMatching(string pattern, RegexOptions options = RegexOptions.None)
    {
        var matches = new List<string>();
        foreach (var file in EnumerateSourceFiles())
        {
            if (Regex.IsMatch(StripComments(File.ReadAllText(file)), pattern, options))
            {
                matches.Add(Path.GetFileName(file));
            }
        }

        matches.Sort(StringComparer.Ordinal);
        return [.. matches];
    }

    /// <summary>Every <c>.cs</c> file under <c>src/**</c>, skipping build output.</summary>
    public static IEnumerable<string> EnumerateSourceFiles()
    {
        foreach (var file in Directory.EnumerateFiles(SrcRoot(), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            yield return file;
        }
    }

    /// <summary>
    /// Every <c>.axaml</c> file under <paramref name="project"/> (a directory name under
    /// <c>src</c>, or every project when null), skipping build output.
    /// </summary>
    /// <remarks>
    /// Phase 14A Task 2's help placement census is the third markup walk in this assembly, after
    /// <c>ControlStyleScanTest</c>'s and <c>FontSizeTokenTest</c>'s, which is where design lesson
    /// 1 says the spine is extracted rather than copied again. The two existing walks are left
    /// where they are: both are load-bearing rules this phase must not disturb, and folding them
    /// onto this helper is a fixer-pass job rather than one for a wave with four implementers in
    /// the tree.
    /// </remarks>
    public static IEnumerable<string> EnumerateMarkupFiles(string? project = null)
    {
        var root = project is null ? SrcRoot() : Path.Combine(SrcRoot(), project);
        foreach (var file in Directory.EnumerateFiles(root, "*.axaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            yield return file;
        }
    }

    /// <summary>Removes block and line comments, so a rule named in a comment is not a match.
    /// </summary>
    public static string StripComments(string source)
    {
        source = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
        source = Regex.Replace(source, @"//[^\r\n]*", "");
        return source;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GalactiLog.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("GalactiLog.sln was not found above the test assembly.");
    }
}
