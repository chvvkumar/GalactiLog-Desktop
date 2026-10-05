using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GalactiLog.App.ViewModels.Settings;

/// <summary>Spec 12.7's risk chip, ported from the web's <c>MaintenanceSection</c>: four cards,
/// three levels.</summary>
public enum MaintenanceRisk
{
    /// <summary>Recomputes something. No row and no file is lost.</summary>
    Safe,

    /// <summary>Rewrites assignments or replaces cache files. Nothing the application cannot
    /// produce again.</summary>
    Moderate,

    /// <summary>Deletes rows that cannot be recomputed.</summary>
    Destructive,
}

/// <summary>
/// One card on the Maintenance tab: a title, a description, a risk chip, one or more buttons, a
/// running state with a progress line, and a summary line.
/// </summary>
/// <remarks>
/// <para>
/// One shared card type, not six hand-rolled ones (design-lessons rule 1, applied at the first
/// occurrence because there are six of them on one tab). Everything specific to an action is
/// constructor data; the orchestration (one action at a time, the scan gate, the activity events)
/// lives in <see cref="MaintenanceTabViewModel"/>, which is the only thing that builds one of
/// these.
/// </para>
/// <para>
/// The card holds no collaborator and performs no work: its buttons carry commands the tab built,
/// so the card is pure state and a test can drive it directly.
/// </para>
/// </remarks>
public sealed partial class MaintenanceActionViewModel : ObservableObject
{
    /// <param name="token">Spec 10.9's <c>action</c> detail value for this card, one of
    /// <see cref="MaintenanceTabViewModel"/>'s six constants.</param>
    /// <param name="title">The card's heading.</param>
    /// <param name="description">What the action does, and where it matters, what it cannot do
    /// and why.</param>
    /// <param name="risk">The chip.</param>
    /// <param name="requiresConfirm">True gives the card the web's inline two-click confirm: the
    /// first press arms <see cref="ConfirmPending"/> and the second runs. Used by rebuild targets,
    /// which rewrites every frame assignment. The reset action uses a typed confirmation in a
    /// modal instead, which is a stronger gate and not this flag.</param>
    /// <param name="emitsItsOwnEvents">True when the action writes spec 10.9's
    /// <c>rebuild_started</c> and <c>rebuild_complete</c> itself, which only reset database does
    /// and only because its own delete removes anything written before it. The tab emits neither
    /// for such a card. Known at construction rather than discovered from a result, because the
    /// tab now emits <c>rebuild_started</c> before the work begins (review finding I3).</param>
    internal MaintenanceActionViewModel(
        string token,
        string title,
        string description,
        MaintenanceRisk risk,
        bool requiresConfirm = false,
        bool emitsItsOwnEvents = false)
    {
        Token = token;
        Title = title;
        Description = description;
        Risk = risk;
        RequiresConfirm = requiresConfirm;
        EmitsItsOwnEvents = emitsItsOwnEvents;
    }

    /// <inheritdoc cref="MaintenanceActionViewModel(string, string, string, MaintenanceRisk, bool, bool)"/>
    public bool EmitsItsOwnEvents { get; }

    /// <summary>Spec 10.9's <c>action</c> detail value. Also the key a test names a card by.</summary>
    public string Token { get; }

    public string Title { get; }

    public string Description { get; }

    public MaintenanceRisk Risk { get; }

    /// <summary>The chip's text: "Safe", "Moderate" or "Destructive", the web's own three
    /// words.</summary>
    public string RiskText => Risk switch
    {
        MaintenanceRisk.Safe => "Safe",
        MaintenanceRisk.Moderate => "Moderate",
        _ => "Destructive",
    };

    public bool IsDestructive => Risk == MaintenanceRisk.Destructive;

    /// <summary>The card's buttons, in the order the spec's row lists them.</summary>
    public ObservableCollection<MaintenanceButtonViewModel> Buttons { get; } = [];

    /// <summary>True when this card uses the inline two-click confirm.</summary>
    public bool RequiresConfirm { get; }

    /// <summary>True between the first press and the second. Clearing it is what "Cancel" does.
    /// </summary>
    [ObservableProperty]
    public partial bool ConfirmPending { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    /// <summary>The running action's progress line, or null.</summary>
    [ObservableProperty]
    public partial string? Progress { get; private set; }

    /// <summary>The last run's outcome as one sentence, or null. Named counts, the way
    /// <c>UnresolvedNamesViewModel.RetrySummary</c> does it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary))]
    public partial string? Summary { get; private set; }

    /// <summary>True when <see cref="Summary"/> reports a failure rather than an outcome
    /// (spec 12.10's failure-line rule), so the view can colour it.</summary>
    [ObservableProperty]
    public partial bool Failed { get; private set; }

    public bool HasSummary => !string.IsNullOrEmpty(Summary);

    /// <summary>Clears the inline confirm. A command on the card rather than on the tab, because
    /// it changes nothing but this card's own state.</summary>
    [RelayCommand]
    private void CancelConfirm() => ConfirmPending = false;

    /// <summary>Puts the card into its running state. Called by the tab, never by the view.</summary>
    internal void Begin()
    {
        ConfirmPending = false;
        IsRunning = true;
        Progress = null;
        Summary = null;
        Failed = false;
    }

    /// <summary>Publishes one progress line. Called through the tab's post seam, so it is always
    /// on the UI thread.</summary>
    internal void ReportProgress(string? message) => Progress = message;

    /// <summary>Takes the card out of its running state and publishes the outcome line.</summary>
    internal void Finish(string? summary, bool failed = false)
    {
        IsRunning = false;
        Progress = null;
        Summary = summary;
        Failed = failed;
    }

    /// <summary>Re-evaluates every button's <c>CanExecute</c>. The tab calls it when the gate
    /// moves.</summary>
    internal void NotifyButtons()
    {
        foreach (var button in Buttons)
        {
            button.NotifyCanExecuteChanged();
        }
    }
}

/// <summary>
/// One button on one card. Built by <see cref="MaintenanceTabViewModel"/>, which supplies both the
/// body and the gate.
/// </summary>
/// <remarks>
/// <c>AsyncRelayCommand</c> so the view's press completes with the work rather than before it, and
/// so a test awaits <c>ExecutionTask</c> instead of sleeping. <c>Execute</c> ignores
/// <c>CanExecute</c> (<c>TRACKING.md</c> section 6 item 13), so every body the tab supplies
/// repeats its guard; the <paramref name="canRun"/> predicate here only greys the button.
/// </remarks>
public sealed class MaintenanceButtonViewModel
{
    internal MaintenanceButtonViewModel(string label, bool isDestructive, Func<Task> run, Func<bool> canRun)
    {
        Label = label;
        IsDestructive = isDestructive;
        Command = new AsyncRelayCommand(run, canRun);
    }

    public string Label { get; }

    /// <summary>True for a button that deletes something, so the view can style it apart.</summary>
    public bool IsDestructive { get; }

    public AsyncRelayCommand Command { get; }

    internal void NotifyCanExecuteChanged() => Command.NotifyCanExecuteChanged();
}
