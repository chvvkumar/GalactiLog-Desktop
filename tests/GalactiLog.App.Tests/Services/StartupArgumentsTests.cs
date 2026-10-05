using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using Xunit;

namespace GalactiLog.App.Tests.Services;

// Spec 12.11 behaviour 9 and spec 4.4 step 2 (Phase 11 Task 4). The parse Program.Main performs
// between Velopack's install hooks and the CLI dispatcher, extracted into a type so it can be
// asserted: Program.Main itself cannot be called from a test, so the rules that must stay in it
// are asserted over its source text in StartMinimizedTests instead.
public class StartupArgumentsTests
{
    [Fact]
    public void Parse_EmptyArgs_IsNotMinimizedAndKeepsNothing()
    {
        var startup = StartupArguments.Parse([]);

        Assert.False(startup.Minimized);
        Assert.Empty(startup.Remaining);
    }

    [Fact]
    public void Parse_MinimizedAlone_IsMinimizedAndLeavesNoRemainder()
    {
        var startup = StartupArguments.Parse([StartupArguments.MinimizedSwitch]);

        Assert.True(startup.Minimized);
        // The whole point of the parse: with nothing left, Program.Main's CLI branch is not taken
        // and the GUI starts, where before this task the dispatcher printed usage and exited 2.
        Assert.Empty(startup.Remaining);
    }

    [Fact]
    public void Parse_MinimizedUpperCase_IsRecognized()
    {
        var startup = StartupArguments.Parse([StartupArguments.MinimizedSwitch.ToUpperInvariant()]);

        Assert.True(startup.Minimized);
        Assert.Empty(startup.Remaining);
    }

    [Fact]
    public void Parse_MinimizedTwice_IsRemovedTwice()
    {
        var startup = StartupArguments.Parse(
            [StartupArguments.MinimizedSwitch, StartupArguments.MinimizedSwitch]);

        Assert.True(startup.Minimized);
        Assert.Empty(startup.Remaining);
    }

    // The case that keeps a CLI invocation a CLI invocation: a verb survives the parse, so
    // Program.Main still takes the CLI branch and CliDispatcher still answers (spec 15).
    [Fact]
    public void Parse_AVerbAndMinimized_KeepsTheVerbInTheRemainder()
    {
        var startup = StartupArguments.Parse(
            [StartupArguments.MinimizedSwitch, "scan", @"C:\x"]);

        Assert.True(startup.Minimized);
        Assert.Equal(["scan", @"C:\x"], startup.Remaining);
    }

    [Fact]
    public void Parse_AnUnknownArgument_IsLeftInTheRemainder()
    {
        var startup = StartupArguments.Parse(["--not-a-switch-this-application-knows"]);

        Assert.False(startup.Minimized);
        Assert.Equal(["--not-a-switch-this-application-knows"], startup.Remaining);
    }

    // One spelling, the one the Startup shortcut writes. Anything else is an argument the CLI
    // dispatcher gets to answer for, which is exit 2 and the usage text.
    [Theory]
    [InlineData("-m")]
    [InlineData("/minimized")]
    [InlineData("--minimised")]
    [InlineData("minimized")]
    public void Parse_DoesNotRecogniseAnAlternativeSpelling(string spelling)
    {
        var startup = StartupArguments.Parse([spelling]);

        Assert.False(startup.Minimized);
        Assert.Equal([spelling], startup.Remaining);
    }

    // Ruling Q8 (a)'s three worked inputs. The parse decides the BRANCH and nothing else: each of
    // these leaves something in Remaining, so Program.Main takes the CLI branch, and the branch
    // hands CliDispatcher the ORIGINAL array rather than this one. The three then behave exactly
    // as they did before this task: exit 2 with the usage text, a scan that keeps its positional
    // argument, and an unknown first token rather than a --help.
    // Program_HandsTheOriginalArgumentsToTheCliDispatcher is the other half of the rule.
    [Fact]
    public void Parse_MinimizedThenAVerb_LeavesTheCliBranchToBeTaken()
    {
        var startup = StartupArguments.Parse([StartupArguments.MinimizedSwitch, "scan", @"C:\x"]);

        Assert.NotEmpty(startup.Remaining);
    }

    [Fact]
    public void Parse_AVerbThenMinimized_LeavesTheCliBranchToBeTaken()
    {
        var startup = StartupArguments.Parse(["scan", @"C:\x", StartupArguments.MinimizedSwitch]);

        Assert.True(startup.Minimized);
        Assert.Equal(["scan", @"C:\x"], startup.Remaining);
        Assert.NotEmpty(startup.Remaining);
    }

    [Fact]
    public void Parse_MinimizedThenHelp_LeavesTheCliBranchToBeTaken()
    {
        var startup = StartupArguments.Parse([StartupArguments.MinimizedSwitch, "--help"]);

        Assert.True(startup.Minimized);
        Assert.Equal(["--help"], startup.Remaining);
        Assert.NotEmpty(startup.Remaining);
    }

    [Fact]
    public void MinimizedSwitch_IsTheLiteralTheStartupShortcutWrites()
    {
        Assert.Equal(VelopackStartupShortcut.MinimizedArgument, StartupArguments.MinimizedSwitch);
    }

    // Design-lessons rule 1 for this task, and the reason StartupArguments references Task 3's
    // constant instead of repeating the string: a shortcut that writes one spelling and a parser
    // that reads another is a Startup entry that silently stops working.
    [Fact]
    public void TheMinimizedLiteral_AppearsInExactlyOneSourceFile()
    {
        Assert.Equal(
            ["VelopackStartupShortcut.cs"],
            SourceScan.FilesMatching(@"""--minimized"""));
    }
}
