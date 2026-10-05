using System.Text.RegularExpressions;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.Data.Maintenance;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Spec 12.7's typed confirmation. The single most important test in this file is
// Confirm_ExecutedDirectlyWithTheWrongPhrase_DoesNothing: RelayCommand.Execute ignores CanExecute
// (TRACKING.md section 6 item 13), and a command that drops every row in the database must not be
// reachable by a caller that skipped the predicate.
public class ResetConfirmViewModelTests
{
    private sealed class Recorder
    {
        public int Calls;
        public int WorkThread;
        public ManualResetEventSlim? Gate;
        public Exception? Throw;
        public DatabaseReset.ResetOutcome Outcome = new(9, 123, true);

        public DatabaseReset.ResetOutcome Run(CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            WorkThread = Environment.CurrentManagedThreadId;
            Gate?.Wait(TimeSpan.FromSeconds(30));
            if (Throw is { } failure)
            {
                throw failure;
            }

            return Outcome;
        }
    }

    private static (ResetConfirmViewModel Page, Recorder Reset) Create()
    {
        var recorder = new Recorder();
        return (new ResetConfirmViewModel(recorder.Run, post: action => action()), recorder);
    }

    private static async Task ConfirmAsync(ResetConfirmViewModel page)
    {
        page.ConfirmCommand.Execute(null);
        if (page.ConfirmCommand.ExecutionTask is { } run)
        {
            await run;
        }
    }

    // ---- the phrase --------------------------------------------------------------------------

    [Fact]
    public void ThePhrase_IsAConstant()
    {
        Assert.Equal("RESET", ResetConfirmViewModel.RequiredPhrase);
    }

    [Fact]
    public void Confirm_IsDisabledUntilThePhraseIsTypedExactly()
    {
        var (page, _) = Create();

        Assert.False(page.ConfirmCommand.CanExecute(null));

        page.Typed = "RESE";
        Assert.False(page.ConfirmCommand.CanExecute(null));

        page.Typed = ResetConfirmViewModel.RequiredPhrase;
        Assert.True(page.ConfirmCommand.CanExecute(null));
        Assert.True(page.PhraseMatches);
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("Reset")]
    [InlineData("rEsEt")]
    public void Confirm_IsCaseSensitive(string typed)
    {
        var (page, _) = Create();
        page.Typed = typed;

        Assert.False(page.PhraseMatches);
        Assert.False(page.ConfirmCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(" RESET")]
    [InlineData("RESET ")]
    [InlineData(" RESET ")]
    [InlineData("RE SET")]
    public void Confirm_DoesNotIgnoreLeadingOrTrailingWhitespace(string typed)
    {
        // The choice, stated: the comparison is exact, with no trimming and no normalization. The
        // point of a typed confirmation is that the user typed exactly this.
        var (page, _) = Create();
        page.Typed = typed;

        Assert.False(page.PhraseMatches);
        Assert.False(page.ConfirmCommand.CanExecute(null));
    }

    [Fact]
    public async Task Confirm_ExecutedDirectlyWithTheWrongPhrase_DoesNothing()
    {
        var (page, reset) = Create();
        page.Typed = "reset";

        var closed = 0;
        page.CloseRequested += (_, _) => closed++;

        await ConfirmAsync(page);

        Assert.Equal(0, reset.Calls);
        Assert.Equal(0, closed);
        Assert.False(page.IsResetting);
        Assert.Null(page.Summary);
    }

    [Fact]
    public async Task Confirm_ExecutedTwice_RunsTheResetOnce()
    {
        var (page, reset) = Create();
        page.Typed = ResetConfirmViewModel.RequiredPhrase;
        reset.Gate = new ManualResetEventSlim(false);

        page.ConfirmCommand.Execute(null);
        var first = page.ConfirmCommand.ExecutionTask!;
        page.ConfirmCommand.Execute(null);

        reset.Gate.Set();
        await first;

        Assert.Equal(1, reset.Calls);
        reset.Gate.Dispose();
    }

    // ---- the run -----------------------------------------------------------------------------

    [Fact]
    public async Task Confirm_RunsTheResetOffTheUiThread_AndClosesWithTrue()
    {
        var (page, reset) = Create();
        page.Typed = ResetConfirmViewModel.RequiredPhrase;
        var caller = Environment.CurrentManagedThreadId;

        bool? closedWith = null;
        page.CloseRequested += (_, ran) => closedWith = ran;

        await ConfirmAsync(page);

        Assert.Equal(1, reset.Calls);
        Assert.NotEqual(caller, reset.WorkThread);
        Assert.True(closedWith);
        Assert.False(page.IsResetting);
        Assert.Contains("123 rows deleted from 9 tables", page.Summary!, StringComparison.Ordinal);
        Assert.Contains("Settings and catalogues were kept", page.Summary!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Confirm_RefusedByTheLease_SaysSoAndClosesWithFalse()
    {
        var (page, reset) = Create();
        reset.Outcome = new DatabaseReset.ResetOutcome(0, 0, false, DatabaseReset.ResetStatus.ScanInProgress);
        page.Typed = ResetConfirmViewModel.RequiredPhrase;

        bool? closedWith = null;
        page.CloseRequested += (_, ran) => closedWith = ran;

        await ConfirmAsync(page);

        // Review finding I4: the dialog closes with false, but the summary leaves with it, so the
        // surface that opened it reports the refusal rather than a cancel. A refused lease is not
        // a failure and is not coloured as one.
        Assert.Equal(ResetConfirmViewModel.ScanInProgressMessage, page.Summary);
        Assert.False(page.Failed);
        Assert.False(closedWith);
    }

    [Fact]
    public async Task Confirm_ThatThrows_ShowsAFailureLine_AndClosesWithFalse()
    {
        var (page, reset) = Create();
        reset.Throw = new InvalidOperationException("boom");
        page.Typed = ResetConfirmViewModel.RequiredPhrase;

        bool? closedWith = null;
        page.CloseRequested += (_, ran) => closedWith = ran;

        await ConfirmAsync(page);

        Assert.Equal(ResetConfirmViewModel.FailureMessage, page.Summary);
        Assert.True(page.Failed);
        Assert.False(page.IsResetting);
        Assert.False(closedWith);
    }

    // ---- cancel ------------------------------------------------------------------------------

    [Fact]
    public void Cancel_IsAvailableUntilConfirmIsPressed()
    {
        var (page, _) = Create();

        Assert.True(page.CancelCommand.CanExecute(null));

        bool? closedWith = null;
        page.CloseRequested += (_, ran) => closedWith = ran;
        page.CancelCommand.Execute(null);

        Assert.False(closedWith);
    }

    [Fact]
    public async Task Cancel_AndTheWindowClose_AreDisabledWhileTheResetRuns()
    {
        var (page, reset) = Create();
        page.Typed = ResetConfirmViewModel.RequiredPhrase;
        reset.Gate = new ManualResetEventSlim(false);

        var closes = 0;
        page.CloseRequested += (_, _) => closes++;

        page.ConfirmCommand.Execute(null);
        var run = page.ConfirmCommand.ExecutionTask!;
        await WaitFor(() => page.IsResetting, "the reset to start");

        // Greyed, and refused in the body as well: this is the flag ResetConfirmWindow.OnClosing
        // reads to refuse the title-bar close.
        Assert.False(page.CancelCommand.CanExecute(null));
        page.CancelCommand.Execute(null);
        Assert.Equal(0, closes);

        reset.Gate.Set();
        await run;

        // Cleared before the close is raised, so the reset's own close is never blocked.
        Assert.False(page.IsResetting);
        Assert.Equal(1, closes);
        reset.Gate.Dispose();
    }

    // ---- the itemized text -------------------------------------------------------------------

    [Fact]
    public void TheDialog_ItemizesWhatIsClearedAndWhatIsKept()
    {
        var (page, _) = Create();

        // Review minor 4: the lines are keyed off the table names and projected in the action's own
        // order, so reordering either list reorders the text with it rather than silently
        // describing the wrong rows. A table with no line throws on construction.
        Assert.Equal(
            DatabaseReset.ClearedTables.Select(ResetConfirmViewModel.Describe),
            page.ClearedItems);
        Assert.Equal(
            DatabaseReset.KeptTables.Select(ResetConfirmViewModel.Describe),
            page.KeptItems);
        Assert.Equal(DatabaseReset.ClearedTables.Count, page.ClearedItems.Count);
        Assert.Equal(DatabaseReset.KeptTables.Count, page.KeptItems.Count);
        Assert.Throws<KeyNotFoundException>(() => ResetConfirmViewModel.Describe("a_table_with_no_text"));

        Assert.Contains(page.ClearedItems, line => line.Contains("activity log", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(page.ClearedItems, line => line.Contains("Merge manifests", StringComparison.Ordinal));
        Assert.Contains(page.ClearedItems, line => line.Contains("catalogue lookup cache", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(page.KeptItems, line => line.Contains("settings", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(page.KeptItems, line => line.Contains("OpenNGC", StringComparison.Ordinal));

        Assert.Contains("No file on disk is deleted", page.FilesUntouchedText, StringComparison.Ordinal);
        Assert.Contains("RESET", page.PromptText, StringComparison.Ordinal);
    }

    // ---- the one modal host --------------------------------------------------------------------

    [Fact]
    public void ItUsesTheOneModalHost()
    {
        // By construction: the view-model opens nothing, and the production binding goes through
        // ModalHost.ShowAsync (TRACKING.md section 6 item 22). No second window host exists.
        var root = FindRepoRoot();
        var page = File.ReadAllText(Path.Combine(
            root, "src", "GalactiLog.App", "ViewModels", "Settings", "ResetConfirmViewModel.cs"));
        Assert.DoesNotContain("ShowDialog", page, StringComparison.Ordinal);
        Assert.DoesNotContain("new ResetConfirmWindow", page, StringComparison.Ordinal);

        // The wrapper is a service beside MergeDialogService and PreviewModalService, not a lambda
        // in AppHost (review escalation ruling), and it opens on the one modal host.
        var service = File.ReadAllText(Path.Combine(
            root, "src", "GalactiLog.App", "Services", "ResetConfirmDialogService.cs"));
        Assert.Contains("ModalHost", service, StringComparison.Ordinal);
        Assert.Contains("host.ShowAsync<bool>", service, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowDialog", service, StringComparison.Ordinal);

        var appHost = File.ReadAllText(Path.Combine(root, "src", "GalactiLog.App", "AppHost.cs"));
        Assert.DoesNotContain("ShowResetConfirmAsync", appHost, StringComparison.Ordinal);
        var registration = Regex.Match(appHost, @"new ResetConfirmDialogService\([\s\S]*?\)\);");
        Assert.True(registration.Success, "no ResetConfirmDialogService registration found in AppHost.cs");
        Assert.Contains("ModalHost", registration.Value, StringComparison.Ordinal);

        // Review finding I5: the reset's own binding drops the two in-process memos that would
        // otherwise serve pre-reset figures for the life of the process. FIXER LIST F22 made that
        // one named action, shared with the rebuild and retry bindings, rather than two pasted
        // lines per binding, so the assertion follows it.
        Assert.Contains("InvalidateDerivedCaches", registration.Value, StringComparison.Ordinal);

        var invalidate = Regex.Match(
            appHost,
            @"private static void InvalidateDerivedCaches\(IServiceProvider serviceProvider\)\s*\{[\s\S]*?\n    \}");
        Assert.True(invalidate.Success, "no InvalidateDerivedCaches method found in AppHost.cs");
        Assert.Contains("StatsCache", invalidate.Value, StringComparison.Ordinal);
        Assert.Contains("RigBaselinesCache", invalidate.Value, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GalactiLog.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("GalactiLog.sln not found.");
    }

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        for (var i = 0; i < 600 && !condition(); i++)
        {
            await Task.Delay(20);
        }

        Assert.True(condition(), $"timed out waiting for {what}");
    }
}
