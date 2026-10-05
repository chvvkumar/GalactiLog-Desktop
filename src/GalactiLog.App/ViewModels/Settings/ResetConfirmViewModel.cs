using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.Data.Maintenance;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.Settings;

/// <summary>
/// Spec 12.7's "reset database with a typed confirmation". The user reads what will be deleted and
/// what will be kept, types a fixed phrase exactly, and only then can confirm. Confirming runs the
/// reset from inside this dialog, so the window cannot be dismissed while the delete is in flight.
/// </summary>
/// <remarks>
/// <para>
/// Port-only. The web application has no typed confirmation anywhere: its strongest gates are a
/// dialog with an itemized destructive message and an inline two-click confirm. Spec 12.7 asks for
/// a typed one, so the design is this port's (<c>questions.md</c> Q30, ruled as recommended).
/// </para>
/// <para>
/// The dialog is shown through <c>ModalHost</c>, the one modal host in the application
/// (<c>TRACKING.md</c> section 6 item 22); nothing here opens a window, and there is no second
/// host.
/// </para>
/// <para>
/// <b><c>RelayCommand.Execute</c> ignores <c>CanExecute</c></b> (<c>TRACKING.md</c> section 6 item
/// 13), so the phrase check is repeated in <see cref="Confirm"/>'s body. This is the single most
/// important application of that rule in the phase: a command that drops every row in the database
/// must not be reachable by a caller that skipped the predicate.
/// </para>
/// <para>
/// Cancel and the window's own close stay available until Confirm is pressed and are disabled
/// while the reset runs, matching spec 12.9's rule for the merge dialog and for the same reason: a
/// dialog abandoned mid-transaction would report that nothing happened.
/// </para>
/// </remarks>
public sealed partial class ResetConfirmViewModel : ObservableObject, IModalPageViewModel
{
    /// <summary>
    /// The phrase the user must type, compared with <see cref="StringComparison.Ordinal"/> and
    /// therefore case-sensitively. A constant so a test can name it without a magic string.
    /// Short enough to type, impossible to hit by accident, and not a word anyone would type into
    /// an adjacent field.
    /// </summary>
    public const string RequiredPhrase = "RESET";

    /// <summary>
    /// The comparison is exact: no trimming, no case folding, no normalization. A trailing space
    /// leaves Confirm disabled. Deliberate, and stated here because the alternative reads as a
    /// bug: the point of a typed confirmation is that the user typed exactly this, and trimming
    /// would make " reset " and "RESET" the same deliberate act.
    /// </summary>
    public bool PhraseMatches => string.Equals(Typed, RequiredPhrase, StringComparison.Ordinal);

    private readonly Func<CancellationToken, DatabaseReset.ResetOutcome> _reset;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;

    /// <param name="reset">Normally <c>DatabaseReset.Run</c>. A delegate so the dialog builds in a
    /// unit test with no database (design-spec 18.3), and so nothing but this one binding can
    /// reach the reset.</param>
    /// <param name="post">How to reach the UI thread. Defaults to <c>UiPost.Default</c>.</param>
    /// <param name="logger">Optional. A failed reset is logged and reported on the dialog, never
    /// rethrown onto the UI thread.</param>
    public ResetConfirmViewModel(
        Func<CancellationToken, DatabaseReset.ResetOutcome> reset,
        Action<Action>? post = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(reset);

        _reset = reset;
        _post = post ?? UiPost.Default;
        _logger = logger ?? NullLogger.Instance;
        Typed = "";
    }

    /// <summary>
    /// One line per table, keyed by the table's own name (review minor 4). Projected through
    /// <see cref="DatabaseReset.ClearedTables"/> and <see cref="DatabaseReset.KeptTables"/> below,
    /// so the itemization follows the action's order rather than running parallel to it: reordering
    /// either list reorders the text with it, and a table with no line here throws on construction
    /// instead of shifting every line after it onto the wrong row.
    /// </summary>
    private static readonly Dictionary<string, string> LineFor = new(StringComparer.Ordinal)
    {
        ["activity_events"] = "The activity log, including every scan's history",
        ["phd2_frames"] = "Every stored guiding sample",
        ["phd2_sessions"] = "Every guiding session read from a PHD2 guide log",
        ["phd2_calibrations"] = "Every guiding calibration read from a PHD2 guide log",
        ["phd2_logs"] = "The catalogue of PHD2 guide log files, so every one is read again on the next scan",
        ["custom_column_values"] = "Every value you typed into a column of your own",
        ["custom_columns"] = "Every column you defined yourself, so each one has to be created again",
        ["mosaic_panel_sessions"] = "Every night placed in a mosaic panel",
        ["mosaic_panels"] = "Every mosaic panel and its layout",
        ["mosaics"] = "Every mosaic, with its notes",
        ["mosaic_suggestions"] = "Mosaic suggestions, including the ones you dismissed",
        ["merge_manifests"] = "Merge manifests, so no merge can be undone afterwards",
        ["merge_candidates"] = "Merge suggestions",
        ["session_notes"] = "Session notes",
        ["target_catalog_memberships"] = "Target catalogue memberships",
        ["images"] = "Every catalogued frame",
        ["skipped_files"] = "The record of calibration frames a scan skipped, so every one is read again on the next scan",
        ["targets"] = "Every target",
        ["scan_runs"] = "Scan history",
        ["catalog_cache"] = "The catalogue lookup cache, including successful lookups",
        ["user_settings"] = "Your settings: library folders, observer location, display and storage preferences",
        ["openngc_catalog"] = "The shipped OpenNGC catalogue",
        ["static_catalog_entries"] = "The shipped static catalogue entries",
    };

    /// <summary>What the confirmation itemizes as deleted, in
    /// <see cref="DatabaseReset.ClearedTables"/>'s own order.</summary>
    public IReadOnlyList<string> ClearedItems { get; } = [.. DatabaseReset.ClearedTables.Select(Describe)];

    /// <summary>What the confirmation itemizes as kept, in
    /// <see cref="DatabaseReset.KeptTables"/>'s own order.</summary>
    public IReadOnlyList<string> KeptItems { get; } = [.. DatabaseReset.KeptTables.Select(Describe)];

    /// <summary>The line for one table. Throws rather than inventing text, so a table added to
    /// either list without a line fails loudly at construction.</summary>
    internal static string Describe(string table)
        => LineFor.TryGetValue(table, out var line)
            ? line
            : throw new KeyNotFoundException(
                $"The reset confirmation has no text for the table '{table}'.");

    /// <summary>Stated on the dialog: the thumbnail cache is not touched by a reset. It is files,
    /// not rows, and the frame-thumbnail card is what empties it.</summary>
    public string FilesUntouchedText =>
        "No file on disk is deleted. The database file itself is kept and emptied, and the "
        + "thumbnail cache is left exactly as it is.";

    public string PromptText => $"Type {RequiredPhrase} to enable the reset.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhraseMatches))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial string Typed { get; set; }

    /// <summary>True from the moment Confirm starts the delete until the dialog closes. The window
    /// refuses to close while it is set.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsResetting { get; private set; }

    /// <summary>The finished reset's counts as one sentence, or a failure line. Read by the
    /// caller after the dialog closes, the way <c>MergeDialogService</c> reads
    /// <c>ConfirmSummary</c>: the dialog closes as soon as the reset finishes, so the summary is
    /// shown by the surface that opened it.</summary>
    [ObservableProperty]
    public partial string? Summary { get; private set; }

    /// <summary>True when <see cref="Summary"/> is spec 12.10's failure line rather than an
    /// outcome or a refusal, so the surface that opened the dialog can colour it (review finding
    /// I4). Read in the caller's cleanup beside <see cref="Summary"/>.</summary>
    [ObservableProperty]
    public partial bool Failed { get; private set; }

    /// <summary>Spec 12.10's failure line for the reset. One neutral sentence, no exception
    /// text.</summary>
    internal const string FailureMessage =
        "The database could not be reset. See the log for details.";

    /// <summary>Raised with true when the reset ran, false when the user cancelled. The window
    /// answers it, the split <c>MergeDialogViewModel.CloseRequested</c> established.</summary>
    public event EventHandler<bool>? CloseRequested;

    /// <summary>The in-flight reset, so a test awaits it instead of sleeping.</summary>
    internal Task? PendingReset { get; private set; }

    /// <remarks>
    /// Deliberately takes no <see cref="CancellationToken"/>, unlike every other async command in
    /// this application. Two reasons, and they point the same way. The reset is not cancellable by
    /// design: it is one transaction, and a half-run reset is not a state this dialog may leave
    /// behind, which is also why the window refuses to close while it runs. And a token here would
    /// be the command's own, which <c>AsyncRelayCommand</c> cancels the moment a second
    /// <c>Execute</c> arrives: a caller that skipped <c>CanExecute</c> would then cancel the
    /// running reset rather than be turned away by the guard below, which is the opposite of what
    /// <c>TRACKING.md</c> section 6 item 13 asks for.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync()
    {
        // Repeated here because RelayCommand.Execute ignores CanExecute. Without this line a
        // caller that invoked the command directly would drop every row in the database having
        // typed nothing at all.
        if (!PhraseMatches || IsResetting)
        {
            return;
        }

        IsResetting = true;
        Summary = null;

        try
        {
            // No token on the Task.Run either: a token that is already cancelled when the work is
            // scheduled makes Task.Run skip the delegate entirely, which would report a reset that
            // silently did not happen.
            var run = Task.Run(() => _reset(CancellationToken.None));
            PendingReset = run;
            var outcome = await run.ConfigureAwait(false);
            var ran = outcome.Status == DatabaseReset.ResetStatus.Completed;
            // A refused lease is not a failure: it is the same informational refusal every other
            // maintenance card shows, so it is not coloured as one. It still leaves with a summary
            // (review finding I4), which is what stops the tab from reporting a cancel.
            _post(() => Finish(Describe(outcome), ran, failed: false));
        }
        catch (OperationCanceledException)
        {
            _post(() => Finish("The reset was cancelled.", ran: false, failed: false));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The database reset failed");
            _post(() => Finish(FailureMessage, ran: false, failed: true));
        }
    }

    private bool CanConfirm() => PhraseMatches && !IsResetting;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        // The same rule as Confirm's: Execute ignores CanExecute, and a cancel that fired while
        // the delete was in flight would close the window out from under it.
        if (IsResetting)
        {
            return;
        }

        CloseRequested?.Invoke(this, false);
    }

    private bool CanCancel() => !IsResetting;

    // Runs on the UI thread through the post seam. IsResetting is cleared BEFORE the close is
    // raised, so the window's own OnClosing guard does not block the dialog's own close: exactly
    // what MergeDialogWindow does with IsWriting.
    private void Finish(string summary, bool ran, bool failed)
    {
        Summary = summary;
        Failed = failed;
        IsResetting = false;
        CloseRequested?.Invoke(this, ran);
    }

    private static string Describe(DatabaseReset.ResetOutcome outcome)
    {
        if (outcome.Status == DatabaseReset.ResetStatus.ScanInProgress)
        {
            return ScanInProgressMessage;
        }

        var rows = outcome.RowsDeleted.ToString("N0", CultureInfo.InvariantCulture);
        return $"Database reset: {rows} row{(outcome.RowsDeleted == 1 ? "" : "s")} deleted from "
            + $"{outcome.TablesCleared} tables. Settings and catalogues were kept.";
    }

    /// <summary>What a run refused by the resolution lease reads.</summary>
    internal const string ScanInProgressMessage =
        "A scan is running. The reset is available when it finishes.";
}
