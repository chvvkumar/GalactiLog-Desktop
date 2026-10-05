using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.Wizard;
using GalactiLog.Core.Io;
using GalactiLog.Core.Wbpp;
using Microsoft.Extensions.Logging;

namespace GalactiLog.App.ViewModels.TargetDetail.Wbpp;

/// <summary>
/// Spec 12.13's Export for stacking as a six-step wizard. Nothing writes before the review
/// step's one commit; while the commit runs Back, Next, Close and Escape refuse and only Cancel
/// acts.
/// </summary>
public sealed partial class WbppExportWizardViewModel : WizardViewModel<WbppExportStepViewModel>
{
    /// <summary>The copy's job kind in the status bar's registry.</summary>
    public const string CopyJobKind = "stacking_copy";

    private readonly JobRegistry? _jobs;
    private readonly Action<string>? _recordActivity;
    private readonly Func<string, Task> _copyText;
    private readonly Action<string>? _openFolder;
    private readonly Action<Action> _post;
    private CancellationTokenSource? _copyCancel;
    private bool _wroteSomething;
    private bool _disposed;

    /// <param name="page">The shared export state, disposed with the wizard.</param>
    /// <param name="copyText">Normally <c>ShellIntegration.CopyTextAsync</c>, for Copy path.</param>
    /// <param name="jobs">The status bar's job registry. Null in a case that does not
    /// assert it.</param>
    /// <param name="recordActivity">Records one activity row with the given message.</param>
    /// <param name="openFolder">Normally <c>ShellIntegration.OpenFolderInExplorer</c>, for Open folder.</param>
    /// <param name="post">How to reach the UI thread. Defaults to <c>UiPost.Default</c>.</param>
    public WbppExportWizardViewModel(
        WbppExportViewModel page,
        Func<string, Task> copyText,
        JobRegistry? jobs = null,
        Action<string>? recordActivity = null,
        Action<string>? openFolder = null,
        Action<Action>? post = null,
        ILogger? logger = null)
        : base(Build(page, post), logger)
    {
        Page = page;
        _copyText = copyText;
        _jobs = jobs;
        _recordActivity = recordActivity;
        _openFolder = openFolder;
        _post = post ?? UiPost.Default;
        Review.PropertyChanged += OnReviewChanged;
    }

    // The review refreshes its blocks in OnEntered, after GoTo has already raised Commit's
    // CanExecuteChanged against the previous visit's blocks.
    private void OnReviewChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReviewStep.IsBlocked))
        {
            CommitCommand.NotifyCanExecuteChanged();
        }
    }

    private static WbppExportStepViewModel[] Build(WbppExportViewModel page, Action<Action>? post)
    {
        var method = new MethodStep(page, post);
        return
        [
            new FoldersStep(page, post), new QualityStep(page, post), new DestinationStep(page, post),
            method, new ReviewStep(page, method, post), new ResultStep(page, post),
        ];
    }

    public WbppExportViewModel Page { get; }

    public MethodStep Method => (MethodStep)Steps[3];

    public ReviewStep Review => (ReviewStep)Steps[4];

    public ResultStep Result => (ResultStep)Steps[5];

    public bool IsReviewStep => CurrentStep == Review;

    public bool IsResultStep => CurrentStep == Result;

    /// <summary>The copy's own task, so a case awaits it rather than a timer.</summary>
    public Task? PendingCommit => CommitCommand.ExecutionTask;

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(StepIndex) or nameof(IsBusy) or nameof(IsCommitted))
        {
            OnPropertyChanged(nameof(IsReviewStep));
            OnPropertyChanged(nameof(IsResultStep));
            StartAnotherCommand.NotifyCanExecuteChanged();
            OpenFolderCommand.NotifyCanExecuteChanged();
            CommitCommand.NotifyCanExecuteChanged();
            CancelCopyCommand.NotifyCanExecuteChanged();
            CloseCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanCommit() => IsReviewStep && !IsBusy && !IsCommitted && !Review.IsBlocked;

    /// <summary>The one commit. Copies, or writes the script, per the method step.</summary>
    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async Task CommitAsync()
    {
        // RelayCommand.Execute ignores CanExecute (TRACKING item 13).
        Review.OnEntered();
        if (!CanCommit())
        {
            return;
        }

        StepError = null;
        IsCommitted = true;
        IsBusy = true;
        try
        {
            if (Method.IsScript)
            {
                var before = Page.ScriptPath;
                await Page.GenerateCommand.ExecuteAsync(Method.ScriptType).ConfigureAwait(true);
                if (_disposed)
                {
                    return;
                }

                if (Page.ScriptPath is null || Page.ScriptPath == before)
                {
                    // A cancelled save dialog or a refused write: nothing was written, so the lock
                    // is released and the review stays on screen.
                    StepError = Page.HasGenerateError ? Page.GenerateError : null;
                    ReleaseCommit();
                    return;
                }

                _wroteSomething = true;
                Result.Result = null;
            }
            else
            {
                var result = await RunCopyAsync().ConfigureAwait(true);
                if (_disposed)
                {
                    return;
                }

                Result.Result = result;
                _wroteSomething |= Result.Result.Copied > 0;
            }

            GoTo(Steps.Count - 1);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<StagingCopyResult> RunCopyAsync()
    {
        _copyCancel = new CancellationTokenSource();
        var cancel = _copyCancel;
        using var job = _jobs?.Begin(CopyJobKind, "Copy for stacking: " + Page.TargetName, cancel.Cancel);
        Review.IsCopying = true;
        var progress = new PostedProgress(_post, p =>
        {
            var text = string.Create(CultureInfo.InvariantCulture, $"Copying {p.FilesDone:N0} of {p.FilesTotal:N0}");
            var percent = p.BytesTotal > 0 ? 100d * p.BytesDone / p.BytesTotal : 0d;
            Review.ProgressText = text;
            Review.ProgressPercent = percent;
            job?.Report(text, percent);
        });

        StagingCopyResult result;
        try
        {
            result = await Page.RunCopyAsync(progress, cancel.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            result = new StagingCopyResult(StagingOutcome.Cancelled, 0, 0, [], [], [], null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UnauthorizedPathException)
        {
            Logger.LogWarning(ex, "The staging copy could not start");
            result = new StagingCopyResult(StagingOutcome.Aborted, 0, 0, [], [], [], ex.Message);
        }
        catch (Exception ex)
        {
            // Anything else still lands on the result step, so the wizard never stays locked.
            Logger.LogError(ex, "The staging copy failed");
            result = new StagingCopyResult(
                StagingOutcome.Aborted, 0, 0, [], [], [], "an unexpected error: " + ex.Message);
        }
        finally
        {
            Review.IsCopying = false;
            _copyCancel = null;
            cancel.Dispose();
        }

        job?.Finish(
            result.Outcome switch
            {
                StagingOutcome.Completed => JobResult.Succeeded,
                StagingOutcome.Cancelled => JobResult.Cancelled,
                _ => JobResult.Failed,
            },
            ResultStep.OutcomeTextFor(result));

        if (_disposed || result.Copied == 0)
        {
            return result;
        }

        try
        {
            _recordActivity?.Invoke(string.Create(
                CultureInfo.InvariantCulture,
                $"Export for stacking copied {result.Copied:N0} files to {Path.GetFileName(Page.CopyDestination)}"));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Recording the staging copy's activity row failed");
        }

        return result;
    }

    private bool CanCancelCopy() => IsBusy && _copyCancel is not null;

    /// <summary>Stops the copy; files in flight keep their partial contents and are listed.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCancelCopy))]
    private void CancelCopy() => _copyCancel?.Cancel();

    private bool CanClose() => !IsBusy;

    /// <summary>Closes the wizard, reporting whether anything was written. Refused while a commit
    /// runs.</summary>
    [RelayCommand(CanExecute = nameof(CanClose))]
    private void Close()
    {
        if (CanClose())
        {
            RequestClose(_wroteSomething);
        }
    }

    private bool CanStartAnother() => IsResultStep && !IsBusy;

    /// <summary>Back to step 1 over the same nights, the lock released, the result and any script
    /// dropped.</summary>
    [RelayCommand(CanExecute = nameof(CanStartAnother))]
    private void StartAnother()
    {
        if (!CanStartAnother())
        {
            return;
        }

        Result.Result = null;
        Page.WithdrawScript();
        ReleaseCommit();
        GoTo(0);
    }

    // Only an existing folder: a path the copy never created, or a file sitting there, is never
    // handed on.
    private bool CanOpenFolder()
        => IsResultStep && Page.CopyDestination is { } folder && UserFiles.DirectoryExists(folder);

    /// <summary>Explorer on the folder this export wrote into.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private void OpenFolder()
    {
        if (CanOpenFolder())
        {
            _openFolder?.Invoke(Page.CopyDestination!);
        }
    }

    [RelayCommand]
    private Task CopyPathAsync() => Page.CopyDestination is { } folder ? _copyText(folder) : Task.CompletedTask;

    [RelayCommand]
    private Task SaveReportAsync() => Result.HasProblems ? Page.SaveReportAsync(Result.ReportText) : Task.CompletedTask;

    protected override void DisposeCore()
    {
        _disposed = true;
        Review.PropertyChanged -= OnReviewChanged;
        _copyCancel?.Cancel();
        Page.Dispose();
    }

    // Progress reports arrive on the copier's pool threads; each is posted to the UI thread.
    private sealed class PostedProgress(Action<Action> post, Action<StagingProgress> apply) : IProgress<StagingProgress>
    {
        public void Report(StagingProgress value) => post(() => apply(value));
    }
}
