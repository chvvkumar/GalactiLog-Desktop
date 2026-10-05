using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace GalactiLog.Core.Tests.Architecture;

// Enforces spec 2.1.3: no write-capable filesystem call may occur anywhere in src/**
// except the single allowlisted choke point, AppWriter.cs. Plain text scan, not a Roslyn
// syntax walk, per this task's brief: a regex pass is enough for a codebase this size and
// avoids adding a new dependency for this check alone.
//
// Containment is per-pattern, not per-file: the write-capable IO patterns below are
// allowlisted only for AppWriter.cs, the Sqlite connection-string patterns (Phase 1
// fix pass, connection-string containment) are allowlisted only for DatabasePaths.cs, and the
// BeginExport pattern (Phase 10 Task 3) is allowlisted only for AppWriter.cs,
// DiagnosticsService.cs and WbppExportViewModel.cs, and the data root patterns (Phase 10 Task 9)
// are allowlisted only for AppWriter.cs, DataRootPointer.cs and AppDataRelocation.cs. The
// Startup shortcut type names (Phase 11 Task 3) are allowlisted only for
// VelopackStartupShortcut.cs. A file allowlisted for one group is still scanned against every
// other group.
public class FileSafetyTest
{
    private static readonly string[] BlanketForbidden =
    {
        @"File\.Delete\s*\(", @"File\.Move\s*\(", @"File\.Copy\s*\(", @"File\.Replace\s*\(",
        @"File\.WriteAll\w*\s*\(", @"File\.AppendAll\w*\s*\(", @"File\.Create\s*\(",
        @"File\.CreateText\s*\(", @"File\.OpenWrite\s*\(", @"File\.OpenText\s*\(",
        @"File\.Open\s*\(", @"Directory\.Delete\s*\(", @"Directory\.Move\s*\(",
        @"Directory\.CreateDirectory\s*\(",
        // Review fix-pass additions (Task 2 review, items 1/3/4):
        // Narrow this, never remove it: SkiaSharp SKPath.MoveTo and Avalonia geometry
        // MoveTo are drawing calls, not filesystem writes.
        @"(?<!Context|Path|path|context|[Gg]eometry|Figure)\.MoveTo\s*\(",
        @"(?:new\s+FileInfo|GetFileInfo)\s*\([^;]*\)\s*\.\s*(?:Delete|MoveTo|Create|CreateText)\s*\(",
        @"new\s+StreamWriter\s*\(", @"File\.AppendText\s*\(", @"File\.SetAttributes\s*\(",
        @"File\.SetLastWriteTime\w*\s*\(", @"File\.SetCreationTime\w*\s*\(",
        @"File\.SetLastAccessTime\w*\s*\(",
        @"using\s+static\s+System\.IO\.(?:File|Directory)\b",
        @"using\s+\w+\s*=\s*System\.IO\.(?:File|Directory)\s*;",
    };

    // Phase 1 fix pass: SQLite connection strings must all be built through
    // DatabasePaths.BuildConnectionString (design-spec 2.1.2's "no other connection string
    // is ever constructed"), never assembled ad hoc at a second call site. Structural, not
    // conventional: DatabasePaths.cs is the only allowlisted file for these two patterns.
    private static readonly string[] SqliteConnectionStringPatterns =
    {
        @"new\s+SqliteConnection\s*\(",
        @"SqliteConnectionStringBuilder",
    };

    // Phase 10 Task 3: AppWriter.BeginExport is the third authorized root (spec 2.1.1) and is
    // scoped to one path the user chose in a save dialog. Exactly three files may name it:
    // AppWriter.cs, which defines it, DiagnosticsService.cs, which writes the diagnostics bundle
    // (spec 16.3), and WbppExportViewModel.cs, which writes the WBPP export script (spec 12.13).
    // A fourth call site is a third export path and a finding. The exemption is decided on the
    // full path, like the two groups above; this list is the name half of the existence
    // assertion (review finding F1).
    //
    // A new constraint, not a new exception: it forbids a pattern that was not forbidden before
    // and loosens nothing. No existing entry in BlanketForbidden, SqliteConnectionStringPatterns,
    // SkiaPathBasedIoPatterns or any existing allowlist changes (coordinator ruling Q11).
    private static readonly string[] BeginExportPatterns = { @"\.BeginExport\s*\(" };

    private static readonly string[] BeginExportAllowlist =
        { "AppWriter.cs", "DiagnosticsService.cs", "WbppExportViewModel.cs" };

    // Phase 10 Task 9: the data location pointer is the one authorized write outside the two roots
    // and the export destination (spec 2.1.1), and CopyFileInto is the one copy into app data.
    // Exactly three files may name either: AppWriter.cs, which defines them, and the two Core.Io
    // callers. The exemption is decided on the full path, like the three groups above, and this
    // list is the name half of the existence assertion.
    //
    // A new constraint, not a new exception: it forbids two call patterns that were not forbidden
    // before and loosens nothing. No existing entry in BlanketForbidden,
    // SqliteConnectionStringPatterns, BeginExportPatterns, BeginExportAllowlist or
    // SkiaPathBasedIoPatterns changes, and File.Copy and File.WriteAll* remain allowlisted to
    // AppWriter.cs alone as they already are (coordinator rulings Q11 and Q9.2).
    private static readonly string[] DataRootPatterns =
        { @"\.WriteDataRootPointer\s*\(", @"\.CopyFileInto\s*\(" };

    private static readonly string[] DataRootAllowlist =
        { "AppWriter.cs", "DataRootPointer.cs", "AppDataRelocation.cs" };

    // Phase 11 Task 3: the Startup shortcut is the one write this phase performs outside the app
    // data root and the export destination (spec 2.1.1, 12.11). Velopack's COM-based shortcut API
    // writes it, so no System.IO pattern above can see it. Exactly one file in src/** may name the
    // two "legacy, stability not guaranteed" types the shortcut API is built on, Shortcuts and
    // ShellLink, and the enum only they take, ShortcutLocation: VelopackStartupShortcut.cs, which
    // is the one implementation of IStartupShortcut.
    //
    // VelopackLocator is deliberately not in this list (coordinator ruling on this task):
    // VelopackUpdateChecker.cs (Phase 10 Task 4) already reads VelopackLocator.Current for the
    // update channel, predating this task, and VelopackLocator is not one of the "legacy,
    // stability not guaranteed" types the Velopack documentation warns about. A new group's
    // pattern list is this task's to define, so leaving it out is not a loosened allowlist entry;
    // the design-spec 2.1.3 sentence that names it is amended by the docs fixer.
    //
    // A new constraint, not a new exception: it forbids type names that were not forbidden before
    // and loosens nothing. No existing entry in BlanketForbidden, SqliteConnectionStringPatterns,
    // BeginExportPatterns, BeginExportAllowlist, DataRootPatterns, DataRootAllowlist or
    // SkiaPathBasedIoPatterns changes (TRACKING section 3 item 1, coordinator ruling Q11).
    //
    // \bShortcuts\s*\( rather than \bShortcuts\b: the bare word can appear in ordinary English in
    // a comment, and while comments are stripped before this scan runs, the narrower pattern is
    // what the brief specifies and keeps the group from ever depending on comment-stripping alone.
    private static readonly string[] StartupShortcutPatterns =
    {
        @"\bShortcuts\s*\(", @"\bShortcutLocation\b", @"\bShellLink\b",
    };

    private static readonly string[] StartupShortcutAllowlist = { "VelopackStartupShortcut.cs" };

    // Wizard work: AppWriter.BeginStagingCopy is the fourth authorized root (spec 2.1.1), new files
    // only, and the export wizard is its one caller. A new constraint that loosens nothing.
    private static readonly string[] BeginStagingCopyPatterns = { @"\.BeginStagingCopy\s*\(" };

    private static readonly string[] BeginStagingCopyAllowlist = { "AppWriter.cs", "WbppExportViewModel.cs" };

    private static readonly string[] WriteCapableFileStreamTokens =
    {
        "FileAccess.Write", "FileAccess.ReadWrite", "FileMode.Create", "FileMode.CreateNew",
        "FileMode.Append", "FileMode.Truncate", "FileMode.OpenOrCreate",
    };

    // Phase 8 Task 3: SkiaSharp's own path-taking APIs. The scan above looks for System.IO
    // members, so an SKFileWStream("...") is invisible to it: Skia would open, create and
    // truncate a file with no System.IO call anywhere in the source. Design-spec 2.1.2 requires
    // thumbnails to be encoded into a MemoryStream and written by AppWriter, and states that
    // SkiaSharp is never given a path; this array is what makes that structural rather than a
    // convention a future encoder has to remember.
    //
    // No file is allowlisted for these, AppWriter.cs included: AppWriter writes bytes it is
    // handed, and has no business decoding or encoding an image itself.
    private static readonly string[] SkiaPathBasedIoPatterns =
    {
        // Both of these types exist only to take a path. Verified against the SkiaSharp 3.119.4
        // reference assembly: SKFileStream and SKFileWStream are the only SKFile* types it
        // declares, so this pair is the complete list rather than a sample of it.
        @"\bSKFileWStream\b", @"\bSKFileStream\b",
        // The path-taking overloads of the four byte-oriented entry points, each as a pair: the
        // string-literal form, and the twin that covers a path-named variable such as
        // SKBitmap.Decode(framePath).
        @"\bSKData\.Create\s*\(\s*""",
        @"\bSKData\.Create\s*\(\s*[^),]*\b\w*([Pp]ath|[Ff]ile[Nn]ame)\b",
        @"\bSKBitmap\.Decode\s*\(\s*""",
        @"\bSKBitmap\.Decode\s*\(\s*[^),]*\b\w*([Pp]ath|[Ff]ile[Nn]ame)\b",
        @"\bSKImage\.FromEncodedData\s*\(\s*""",
        @"\bSKImage\.FromEncodedData\s*\(\s*[^),]*\b\w*([Pp]ath|[Ff]ile[Nn]ame)\b",
        @"\bSKCodec\.Create\s*\(\s*""",
        @"\bSKCodec\.Create\s*\(\s*[^),]*\b\w*([Pp]ath|[Ff]ile[Nn]ame)\b",
    };

    [Fact]
    public void NoWriteCapableCallOutsideAppWriter()
    {
        var repoRoot = FindRepoRoot();
        var ioAllowlisted = Path.Combine(repoRoot, "src", "GalactiLog.Core", "Io", "AppWriter.cs");
        var sqliteAllowlisted = Path.Combine(repoRoot, "src", "GalactiLog.Data", "DatabasePaths.cs");
        Assert.True(File.Exists(ioAllowlisted), $"Allowlisted file not found on disk: {ioAllowlisted}");
        Assert.True(File.Exists(sqliteAllowlisted), $"Allowlisted file not found on disk: {sqliteAllowlisted}");

        // The BeginExport exemption is decided on the FULL path, exactly as the two groups above
        // decide theirs, so a second file named DiagnosticsService.cs or WbppExportViewModel.cs
        // anywhere else under src is still scanned (review finding F1). BeginExportAllowlist stays
        // the name list the existence assertion checks against, so a rename or deletion of any
        // allowlisted file fails here rather than silently widening the scan's blind spot.
        var beginExportAllowlisted = new[]
        {
            ioAllowlisted,
            Path.Combine(repoRoot, "src", "GalactiLog.App", "Services", "DiagnosticsService.cs"),
            Path.Combine(repoRoot, "src", "GalactiLog.App", "ViewModels", "TargetDetail", "Wbpp", "WbppExportViewModel.cs"),
        };
        foreach (var path in beginExportAllowlisted)
        {
            Assert.True(File.Exists(path), $"Allowlisted file not found on disk: {path}");
            Assert.Contains(Path.GetFileName(path), BeginExportAllowlist);
        }

        // Phase 10 Task 9, the same shape: the full path decides the exemption and the name list is
        // what the existence assertion checks against.
        var dataRootAllowlisted = new[]
        {
            ioAllowlisted,
            Path.Combine(repoRoot, "src", "GalactiLog.Core", "Io", "DataRootPointer.cs"),
            Path.Combine(repoRoot, "src", "GalactiLog.Core", "Io", "AppDataRelocation.cs"),
        };
        foreach (var path in dataRootAllowlisted)
        {
            Assert.True(File.Exists(path), $"Allowlisted file not found on disk: {path}");
            Assert.Contains(Path.GetFileName(path), DataRootAllowlist);
        }

        // Phase 11 Task 3, the same shape again: the full path decides the exemption and the name
        // list is what the existence assertion checks against.
        var startupShortcutAllowlisted = new[]
        {
            Path.Combine(repoRoot, "src", "GalactiLog.App", "Services", "VelopackStartupShortcut.cs"),
        };
        foreach (var path in startupShortcutAllowlisted)
        {
            Assert.True(File.Exists(path), $"Allowlisted file not found on disk: {path}");
            Assert.Contains(Path.GetFileName(path), StartupShortcutAllowlist);
        }

        var beginStagingCopyAllowlisted = new[]
        {
            ioAllowlisted,
            Path.Combine(repoRoot, "src", "GalactiLog.App", "ViewModels", "TargetDetail", "Wbpp", "WbppExportViewModel.cs"),
        };
        foreach (var path in beginStagingCopyAllowlisted)
        {
            Assert.True(File.Exists(path), $"Allowlisted file not found on disk: {path}");
            Assert.Contains(Path.GetFileName(path), BeginStagingCopyAllowlist);
        }

        var failures = new List<string>();
        var scannedFileCount = 0;

        foreach (var file in Directory.EnumerateFiles(Path.Combine(repoRoot, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            scannedFileCount++;
            var text = StripComments(File.ReadAllText(file));
            var isIoAllowlisted = string.Equals(file, ioAllowlisted, StringComparison.OrdinalIgnoreCase);
            var isSqliteAllowlisted = string.Equals(file, sqliteAllowlisted, StringComparison.OrdinalIgnoreCase);
            var isBeginExportAllowlisted = beginExportAllowlisted.Any(
                allowed => string.Equals(file, allowed, StringComparison.OrdinalIgnoreCase));
            var isDataRootAllowlisted = dataRootAllowlisted.Any(
                allowed => string.Equals(file, allowed, StringComparison.OrdinalIgnoreCase));
            var isStartupShortcutAllowlisted = startupShortcutAllowlisted.Any(
                allowed => string.Equals(file, allowed, StringComparison.OrdinalIgnoreCase));
            var isBeginStagingCopyAllowlisted = beginStagingCopyAllowlisted.Any(
                allowed => string.Equals(file, allowed, StringComparison.OrdinalIgnoreCase));

            if (!isIoAllowlisted)
            {
                foreach (var pattern in BlanketForbidden)
                {
                    foreach (Match m in Regex.Matches(text, pattern))
                    {
                        failures.Add($"{file}: forbidden call matching '{pattern}' at offset {m.Index}");
                    }
                }

                // Binds a FileInfo/DirectoryInfo instance to a variable name (via `new FileInfo(...)`,
                // `new DirectoryInfo(...)`, or `UserFiles.GetFileInfo(...)`), then flags any
                // write-capable member call on that variable name later in the file.
                foreach (var variable in Regex.Matches(text, @"(?:var|FileInfo|DirectoryInfo)\s+(\w+)\s*=\s*(?:new\s+(?:FileInfo|DirectoryInfo)|UserFiles\.GetFileInfo)\s*\(")
                    .Select(m => m.Groups[1].Value)
                    .Distinct())
                {
                    foreach (var suffix in new[] { "Delete", "MoveTo", "Create", "CreateText" })
                    {
                        if (Regex.IsMatch(text, $@"\b{Regex.Escape(variable)}\.{suffix}\s*\("))
                        {
                            failures.Add($"{file}: '{variable}.{suffix}(' looks like a FileInfo/DirectoryInfo write");
                        }
                    }

                    // Review fix-pass item 2: property-setter writes (attribute/timestamp
                    // mutation) on the same bound FileInfo/DirectoryInfo variable, not just
                    // method calls. The `[^=]` after `=` excludes `==` comparisons.
                    foreach (var suffix in new[]
                    {
                        "Attributes", "IsReadOnly", "LastWriteTime", "LastWriteTimeUtc",
                        "CreationTime", "CreationTimeUtc", "LastAccessTime", "LastAccessTimeUtc",
                    })
                    {
                        if (Regex.IsMatch(text, $@"\b{Regex.Escape(variable)}\.{suffix}\s*=[^=]"))
                        {
                            failures.Add($"{file}: '{variable}.{suffix} = ...' looks like a FileInfo/DirectoryInfo attribute/timestamp write");
                        }
                    }
                }

                // Review fix-pass item 2: the previous "new FileStream(...)" argument capture
                // truncated at nested parens and could miss write-capable tokens. Rather than
                // re-parse a balanced argument list, flag any write-capable token anywhere in
                // the file: these tokens (FileMode.Create, FileAccess.Write, etc.) have no
                // legitimate read-only use outside AppWriter.cs.
                foreach (var token in WriteCapableFileStreamTokens)
                {
                    foreach (Match m in Regex.Matches(text, Regex.Escape(token)))
                    {
                        failures.Add($"{file}: write-capable token '{token}' found at offset {m.Index}");
                    }
                }
            }

            if (!isSqliteAllowlisted)
            {
                foreach (var pattern in SqliteConnectionStringPatterns)
                {
                    foreach (Match m in Regex.Matches(text, pattern))
                    {
                        failures.Add($"{file}: forbidden Sqlite connection-string construction matching '{pattern}' at offset {m.Index}; route through DatabasePaths.BuildConnectionString");
                    }
                }
            }

            if (!isBeginExportAllowlisted)
            {
                foreach (var pattern in BeginExportPatterns)
                {
                    foreach (Match m in Regex.Matches(text, pattern))
                    {
                        failures.Add($"{file}: forbidden AppWriter.BeginExport call matching '{pattern}' at offset {m.Index}; the diagnostics bundle (design-spec 16.3) and the WBPP export script (design-spec 12.13) are the export paths");
                    }
                }
            }

            if (!isDataRootAllowlisted)
            {
                foreach (var pattern in DataRootPatterns)
                {
                    foreach (Match m in Regex.Matches(text, pattern))
                    {
                        failures.Add($"{file}: forbidden data root write matching '{pattern}' at offset {m.Index}; the data location pointer and the relocation copy have one writer each (design-spec 2.1.1, 17.2)");
                    }
                }
            }

            if (!isStartupShortcutAllowlisted)
            {
                foreach (var pattern in StartupShortcutPatterns)
                {
                    foreach (Match m in Regex.Matches(text, pattern))
                    {
                        failures.Add($"{file}: forbidden Startup shortcut type matching '{pattern}' at offset {m.Index}; VelopackStartupShortcut.cs is the one implementation of IStartupShortcut (design-spec 2.1.1, 12.11)");
                    }
                }
            }

            if (!isBeginStagingCopyAllowlisted)
            {
                foreach (var pattern in BeginStagingCopyPatterns)
                {
                    foreach (Match m in Regex.Matches(text, pattern))
                    {
                        failures.Add($"{file}: forbidden AppWriter.BeginStagingCopy call matching '{pattern}' at offset {m.Index}; the export wizard is the one staging copy (design-spec 2.1.1, 12.13)");
                    }
                }
            }
        }

        Assert.True(scannedFileCount > 0, "FileSafetyTest scanned zero files under src/** - the scan itself is broken.");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    // The roadmap's Phase 8 row 3 Verify line: "FileSafetyTest stays green with no SkiaSharp
    // path-based encode call anywhere". A separate [Fact] beside the scan above rather than a third
    // group inside it, because there is no allowlisted file here at all and the failure message
    // should name the rule it broke.
    [Fact]
    public void NoSkiaSharpPathBasedIoAnywhereInSrc()
    {
        var repoRoot = FindRepoRoot();
        var failures = new List<string>();
        var scannedFileCount = 0;

        foreach (var file in Directory.EnumerateFiles(Path.Combine(repoRoot, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            scannedFileCount++;
            var text = StripComments(File.ReadAllText(file));
            foreach (var pattern in SkiaPathBasedIoPatterns)
            {
                foreach (Match m in Regex.Matches(text, pattern))
                {
                    failures.Add(
                        $"{file}: SkiaSharp path-based IO matching '{pattern}' at offset {m.Index}; " +
                        "encode into a MemoryStream and write the bytes through AppWriter (design-spec 2.1.2)");
                }
            }
        }

        Assert.True(scannedFileCount > 0, "the SkiaSharp scan found zero files under src/** - the scan itself is broken.");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static string StripComments(string source)
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
        return dir?.FullName ?? throw new DirectoryNotFoundException("GalactiLog.sln not found above test output directory.");
    }
}
