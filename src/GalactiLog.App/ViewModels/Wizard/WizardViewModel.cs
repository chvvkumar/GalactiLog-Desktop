using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.Wizard;

/// <summary>
/// What <c>Controls.WizardFrame</c> binds. Non-generic because a compiled binding's
/// <c>x:DataType</c> cannot name an open generic, and <see cref="WizardViewModel{TStep}"/> is
/// invariant in its step type.
/// </summary>
public interface IWizardViewModel
{
    string StepHeader { get; }

    WizardStepViewModel CurrentStep { get; }

    /// <summary>One entry per step, true for the step on screen.</summary>
    IReadOnlyList<bool> StepRail { get; }

    string? StepError { get; }

    bool HasStepError { get; }

    bool IsLastStep { get; }

    IAsyncRelayCommand NextCommand { get; }

    IRelayCommand BackCommand { get; }
}

/// <summary>
/// A modal stepper: an ordered list of steps, Next and Back, a busy flag, and a commit lock. A
/// derived wizard adds its own finishing actions and decides what an advance does through
/// <see cref="OnAdvancingAsync"/>.
/// </summary>
public abstract partial class WizardViewModel<TStep> : ObservableObject, IWizardViewModel, IModalPageViewModel, IDisposable
    where TStep : WizardStepViewModel
{
    private bool _disposed;

    protected WizardViewModel(IReadOnlyList<TStep> steps, ILogger? logger = null)
    {
        Steps = steps;
        Logger = logger ?? NullLogger.Instance;

        foreach (var step in Steps)
        {
            step.PropertyChanged += OnStepPropertyChanged;
        }
    }

    /// <summary>Raised when the wizard asks its window to close, with the dialog result.</summary>
    public event EventHandler<bool>? CloseRequested;

    public IReadOnlyList<TStep> Steps { get; }

    /// <summary>The step being shown, zero based.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentStep))]
    [NotifyPropertyChangedFor(nameof(StepHeader))]
    [NotifyPropertyChangedFor(nameof(HelpTopicId))]
    [NotifyPropertyChangedFor(nameof(StepRail))]
    [NotifyPropertyChangedFor(nameof(IsLastStep))]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyCanExecuteChangedFor(nameof(BackCommand))]
    public partial int StepIndex { get; private set; }

    /// <summary>True while an advance or a derived wizard's own work is in flight.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyCanExecuteChangedFor(nameof(BackCommand))]
    public partial bool IsBusy { get; protected set; }

    /// <summary>True once the wizard has committed its one action; Back and Next refuse until
    /// <see cref="ReleaseCommit"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyCanExecuteChangedFor(nameof(BackCommand))]
    public partial bool IsCommitted { get; protected set; }

    /// <summary>The failed advance's message, a step's <c>BeforeAdvance</c> message, or null.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStepError))]
    public partial string? StepError { get; protected set; }

    public bool HasStepError => StepError is not null;

    public TStep CurrentStep => Steps[StepIndex];

    WizardStepViewModel IWizardViewModel.CurrentStep => CurrentStep;

    /// <summary>The header: "Step n of m: title".</summary>
    public string StepHeader => $"Step {StepIndex + 1} of {Steps.Count}: {CurrentStep.Title}";

    public string HelpTopicId => CurrentStep.HelpTopicId;

    public IReadOnlyList<bool> StepRail => [.. Steps.Select((_, index) => index == StepIndex)];

    public bool CanGoNext => !IsBusy && !IsCommitted && StepIndex < Steps.Count - 1 && CurrentStep.CanAdvance;

    public bool CanGoBack => !IsBusy && !IsCommitted && StepIndex > 0;

    public bool IsLastStep => StepIndex == Steps.Count - 1;

    /// <summary>The wizard's logger, never null.</summary>
    protected ILogger Logger { get; }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextAsync()
    {
        // TRACKING item 13: RelayCommand.Execute ignores CanExecute, so the rule is repeated here.
        if (!CanGoNext)
        {
            return;
        }

        var step = CurrentStep;
        if (step.BeforeAdvance() is { } message)
        {
            StepError = message;
            return;
        }

        bool advance;
        IsBusy = true;
        try
        {
            advance = await OnAdvancingAsync(step).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }

        if (!advance)
        {
            return;
        }

        StepError = null;
        GoTo(StepIndex + 1);
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        if (!CanGoBack)
        {
            return;
        }

        StepError = null;
        GoTo(StepIndex - 1);
    }

    /// <summary>Runs on Next after the step's own gate and before the move. False stays on the
    /// step; the override sets <see cref="StepError"/> to say why.</summary>
    protected virtual Task<bool> OnAdvancingAsync(TStep step) => Task.FromResult(true);

    protected void GoTo(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Steps.Count);
        StepIndex = index;
        CurrentStep.OnEntered();
    }

    protected void ReleaseCommit() => IsCommitted = false;

    protected void RequestClose(bool result) => CloseRequested?.Invoke(this, result);

    private void OnStepPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WizardStepViewModel.CanAdvance))
        {
            return;
        }

        OnPropertyChanged(nameof(CanGoNext));
        NextCommand.NotifyCanExecuteChanged();
    }

    protected virtual void DisposeCore()
    {
    }

    /// <summary>Disposes every step, which cancels whatever a step still has in flight.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeCore();
        foreach (var step in Steps)
        {
            step.PropertyChanged -= OnStepPropertyChanged;
            step.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
