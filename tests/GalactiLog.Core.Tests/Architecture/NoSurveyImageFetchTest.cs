using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace GalactiLog.Core.Tests.Architecture;

// Enforces design-spec 19.2's explicit non-goal: "Downloading survey images (SkyView, DSS) for
// reference thumbnails. Reference thumbnails come from the user's own frames (section 11.4)." That
// is a non-goal, not a deferral, so the check is structural rather than a convention a future
// reference-thumbnail change has to remember.
//
// A separate file from FileSafetyTest on purpose (Phase 8 collision map): that test owns the
// write-capable IO and SkiaSharp path scans, this one owns the survey scan, and neither has to
// wait for the other.
//
// Scans src/** AND tests/**: a test constant is the easiest place for a URL to creep in.
public class NoSurveyImageFetchTest
{
    // The web application's skyview.py builds
    // "https://<host>/current/cgi/pskcall" and the DSS archives are the other half of the same
    // feature. Matched case-insensitively, against source with its comment lines removed.
    private static readonly string[] SurveyTokens =
    {
        "skyview", "gsfc.nasa.gov", "pskcall", "archive.stsci.edu", "dss.stsci.edu", "/dss/",
        "digitized sky survey",
    };

    private static readonly string[] NetworkTypes = { "HttpClient", "HttpMessageHandler", "Uri" };

    [Fact]
    public void NoSurveyImageUrlAnywhereInTheSolution()
    {
        var failures = new List<string>();
        var scanned = 0;

        foreach (var file in SourceFiles())
        {
            // This file names every token it forbids, so it would fail its own scan. Excluded by
            // path, not by pattern: the exclusion is one named file, which is why it cannot be
            // widened into a loophole.
            if (Path.GetFileName(file).Equals("NoSurveyImageFetchTest.cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            scanned++;
            var text = StripCommentLines(File.ReadAllText(file));
            foreach (var token in SurveyTokens)
            {
                if (text.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"{file}: contains '{token}'");
                }
            }
        }

        Assert.True(scanned > 50, $"the scan found only {scanned} source files; the repository layout must have moved");
        Assert.True(
            failures.Count == 0,
            "design-spec 19.2 makes survey image downloads an explicit non-goal, and spec 11.4 says " +
            "reference thumbnails come from the user's own frames. A SkyView or DSS reference has " +
            "appeared:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void NoHttpClientInTheImagingPipeline()
    {
        var repoRoot = FindRepoRoot();
        var imaging = Path.Combine(repoRoot, "src", "GalactiLog.Core", "Imaging");
        Assert.True(Directory.Exists(imaging), $"the imaging pipeline was not found at {imaging}");

        var failures = new List<string>();
        foreach (var file in SourceFiles())
        {
            var inImagingPipeline = file.StartsWith(imaging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            var isThePass = Path.GetFileName(file).Equals("ReferenceThumbnailPass.cs", StringComparison.OrdinalIgnoreCase);
            if (!inImagingPipeline && !isThePass)
            {
                continue;
            }

            var text = StripCommentLines(File.ReadAllText(file));
            failures.AddRange(
                NetworkTypes
                    .Where(type => Regex.IsMatch(text, $@"\b{type}\b"))
                    .Select(type => $"{file}: mentions '{type}'"));
        }

        Assert.True(
            failures.Count == 0,
            "the thumbnail pipeline reads the user's own frames and reaches no network at all " +
            "(design-spec 11.4, 19.2):" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    private static IEnumerable<string> SourceFiles()
    {
        var repoRoot = FindRepoRoot();
        foreach (var area in new[] { "src", "tests" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(repoRoot, area), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    // Whole comment LINES only, and block comments. Not FileSafetyTest's "// to end of line", which
    // would cut a string literal at the "//" of "https://" and hide the very URL this test exists
    // to find. Prose that names what the port declines to do (the pass's own class comment says it
    // never fetches a DSS image) is the honest documentation of the rule, not a breach of it; a URL
    // in code is what this scan is for.
    private static string StripCommentLines(string source)
    {
        source = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return string.Join(
            Environment.NewLine,
            source.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
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
