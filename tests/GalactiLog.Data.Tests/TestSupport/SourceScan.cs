using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace GalactiLog.Data.Tests.TestSupport;

/// <summary>
/// The plain text pass over <c>src/**</c> that this project's structural assertions share: find
/// the repository root, strip comments, and name a source file to read.
/// </summary>
/// <remarks>
/// <para>
/// Modelled on <c>GalactiLog.App.Tests.TestSupport.SourceScan</c>, which is the house precedent,
/// and extracted here because the same <c>StripComments</c>, <c>SourceFile</c> and
/// <c>RepositoryRoot</c> trio had been written three times in this one project:
/// <c>Ingest/Phd2CorrelationTests.cs</c>, <c>Queries/Phd2NightQueryTests.cs</c> and
/// <c>Queries/GuidingStatsQueryTests.cs</c>. Design lesson 1, third occurrence
/// (task5a-review.md P3-2).
/// </para>
/// <para>
/// No Roslyn dependency: a regex pass is enough for a codebase this size, which is the ruling
/// <c>FileSafetyTest</c> already records.
/// </para>
/// </remarks>
internal static class SourceScan
{
    /// <summary>Removes block and line comments, so a rule named in a comment is not a match, and
    /// so a source-text absence assertion cannot be satisfied by a sentence about the thing it
    /// forbids.</summary>
    public static string StripComments(string source)
    {
        source = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(source, @"//[^\r\n]*", "");
    }

    /// <summary>An absolute path from a repository-relative one written with forward slashes.
    /// </summary>
    public static string SourceFile(string relative)
        => Path.Combine(RepositoryRoot(), relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The comment-stripped text of one repository-relative source file, which is what
    /// every caller of <see cref="SourceFile"/> in this project did by hand.</summary>
    public static string Read(string relative) => StripComments(File.ReadAllText(SourceFile(relative)));

    /// <summary>Every <c>.cs</c> file under <c>src/**</c>, skipping build output and the generated
    /// migrations.</summary>
    public static IEnumerable<string> SourceFiles()
        => Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GalactiLog.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("GalactiLog.sln not found above the test output directory.");
    }
}
