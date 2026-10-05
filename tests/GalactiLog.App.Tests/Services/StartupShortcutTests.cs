using System.Text.RegularExpressions;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using Xunit;

namespace GalactiLog.App.Tests.Services;

/// <summary>
/// Spec 12.11 behaviour 8's one <see cref="IStartupShortcut"/> implementation,
/// <see cref="VelopackStartupShortcut"/>.
/// </summary>
/// <remarks>
/// Nothing in this file constructs <see cref="VelopackStartupShortcut"/> with an installed
/// <see cref="BuildInfo"/>. Every case here builds the real type with
/// <c>new BuildInfo(version, sha, channel, isInstalled: false)</c>, and every case elsewhere in
/// this project that needs the seam binds <c>RecordingStartupShortcut</c> instead. A test that
/// created a real Startup shortcut would have modified the machine running the test suite, which
/// nothing in this project is allowed to do (HANDOFF section 5.2 note 9).
/// </remarks>
public class StartupShortcutTests
{
    private static BuildInfo NotInstalled()
        => new("1.0.0.0", "abc1234", "local", isInstalled: false);

    [Fact]
    public void NotInstalled_IsNotSupported()
    {
        var shortcut = new VelopackStartupShortcut(NotInstalled());

        Assert.False(shortcut.IsSupported);
    }

    [Fact]
    public void NotInstalled_Apply_DoesNothingAndReportsNoFailure()
    {
        var shortcut = new VelopackStartupShortcut(NotInstalled());

        // Neither call reaches Velopack's locator or its shortcut API: IsSupported is checked
        // first and guards everything below it.
        //
        // True rather than false (phase review finding P5): nothing was attempted, so nothing
        // failed. False is reserved for a change that was tried and refused, which is what lets
        // the General tab treat a false as the one thing it means instead of re-testing
        // IsSupported at its own call site.
        Assert.True(shortcut.Apply(true));
        Assert.True(shortcut.Apply(false));
    }

    [Fact]
    public void NotInstalled_Exists_IsNull()
    {
        var shortcut = new VelopackStartupShortcut(NotInstalled());

        Assert.Null(shortcut.Exists());
    }

    [Fact]
    public void MinimizedArgument_IsTheLiteralProgramMainParses()
    {
        // The first half of this case. The second half, that Program.cs references this constant
        // rather than repeating the literal, is Phase 11 Task 4's: at this task's entry Task 4
        // has not landed and Program.cs parses no arguments at all (see this task's report,
        // Deviations, for the SourceScan assertion Task 4 is expected to add).
        Assert.Equal("--minimized", VelopackStartupShortcut.MinimizedArgument);
    }

    /// <summary>
    /// Phase 11 Task 3 review, Minor: design-spec 15's "the CLI never touches the tray or the
    /// startup shortcut" had no test. Scans <c>src/GalactiLog.Cli/**</c>, the one project the CLI
    /// verbs live in, for either name: neither the interface nor its one implementation is
    /// reachable from a CLI path, structurally, because nothing under that project names them.
    /// </summary>
    [Fact]
    public void TheCliProjectNeverReferencesTheStartupShortcutSeam()
    {
        var cliRoot = Path.Combine(SourceScan.SrcRoot(), "GalactiLog.Cli");
        Assert.True(Directory.Exists(cliRoot), $"CLI project directory not found: {cliRoot}");

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(cliRoot, "*.cs", SearchOption.AllDirectories))
        {
            var text = SourceScan.StripComments(File.ReadAllText(file));
            if (Regex.IsMatch(text, @"\b(IStartupShortcut|VelopackStartupShortcut)\b"))
            {
                offenders.Add(file);
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>
    /// Phase review finding P5 and fixer list code item 4 (design-lessons rule 2): the one
    /// per-call-site correctness check Phase 11 added that nothing pinned. Four files may read
    /// <see cref="IStartupShortcut.IsSupported"/> and no fifth may.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>IStartupShortcut.cs</c> declares it; <c>VelopackStartupShortcut.cs</c> implements it and
    /// guards every member it has on it, which is what keeps the whole suite off the real Startup
    /// folder; <c>DiagnosticsService.cs</c> reads it because "not applicable" is a distinct
    /// reported value and must stay readable there (spec 12.11 behaviour 11); and
    /// <c>GeneralTabViewModel.cs</c> reads it exactly once, at construction, to decide whether the
    /// control is enabled and what reason sits beside it. That last one is an affordance, not a
    /// correctness gate: the gate that used to sit beside <c>Apply</c> in the same file is gone,
    /// because <c>Apply</c> now reports success for a build that had nothing to do.
    /// </para>
    /// <para>
    /// The pattern excludes <c>FrameReader.IsSupported(path)</c>, an unrelated Core static with
    /// the same name, by excluding both the qualified call and the method form; nothing on this
    /// seam is a method.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheStartupShortcutSupportFlag_IsReadOnlyWhereItIsOwnedOrReported()
    {
        Assert.Equal(
            [
                "DiagnosticsService.cs",
                "GeneralTabViewModel.cs",
                "IStartupShortcut.cs",
                "VelopackStartupShortcut.cs",
            ],
            SourceScan.FilesMatching(@"(?<!FrameReader\.)\bIsSupported\b(?!\s*\()"));
    }
}
