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
/// step's one commit; while the commit runs Back and Next refuse; during a copy Close hands the
/// copy to <see cref="StagingCopyService"/> and the status bar.
/// </summary>
public sealed partial class WbppExportWizardViewModel : WizardViewModel<WbppExportStepViewModel>
{
    private readonly StagingCopyService _copies;
    private readonly Func<string, Task> _copyText;
    private readonly Action<string>? _openFolder;
    private readonly Action<string>? _runScript;
    private readonly Action<Action> _post;
    private CancellationTokenSource? _copyCancel;
    private bool _wroteSomething;
    private bool _disposed;

    /// <param name="page">The shared export state, disposed with the wizard.</param>
    /// <param name="copyText">Normally <c>ShellIntegration.CopyTextAsync</c>, for Copy path.</param>
    /// <param name="copies">The app-lifetime owner of the copy, its job and its Activity row. Null
    /// gives a private one with neither, for a case that does not assert them.</param>
    /// <param name="openFolder">Normally <c>ShellIntegration.OpenFolderInExplorer</c>, for Open folder.</param>
    /// <param name="runScript">Normally <c>ShellIntegration.RunPowerShellScript</c>, for Run script.</param>
    /// <param name="post">How to reach the UI thread. Defaults to <c>UiPost.Default</c>.</param>
    public WbppExportWizardViewModel(
        WbppExportViewModel page,
        Func<string, Task> copyText,
        StagingCopyService? copies = null,
        Action<string>? openFolder = null,
        Action<string>? runScript = null,
        Action<Action>? post = null,
        ILogger? logger = null)
        : base(Build(page, post), logger)
    {
        Page = page;
        _copyText = copyText;
        _copies = copies ?? new StagingCopyService();
        _openFolder = openFolder;
        _runScript = runScript;
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

        // IsBusy is raised before the copy flips IsCopying, so Close would stay disabled.
        if (e.PropertyName == nameof(ReviewStep.IsCopying))
        {
            CloseCommand.NotifyCanExecuteChanged();
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
            RunScriptCommand.NotifyCanExecuteChanged();
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
            // Decision 10: a folder another copy is still filling is refused on the review step,
            // the lock released, the same shape as a refused script below. A script too: it copies
            // with -Force, so it would overwrite what the running copy writes.
            if (Page.CopyDestination is { } destination && _copies.RefusalFor(destination) is { } busy)
            {
                StepError = busy;
                ReleaseCommit();
                return;
            }

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
        // IsBusy's notification ran before the source existed, so Cancel would stay disabled.
        CancelCopyCommand.NotifyCanExecuteChanged();
        Review.IsCopying = true;
        Review.ProgressStats = "";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var progress = new PostedProgress(_post, p =>
        {
            // Closed mid-copy: the service carries on, and the window that read this is gone.
            if (_disposed)
            {
                return;
            }

            Review.ProgressText = cancel.IsCancellationRequested
                ? StagingCopyService.CancellingText
                : string.Create(CultureInfo.InvariantCulture, $"Copying {p.FilesDone:N0} of {p.FilesTotal:N0}");
            Review.ProgressStats = StagingCopyService.TransferStats(p, clock.Elapsed);
            Review.ProgressPercent = p.BytesTotal > 0 ? 100d * p.BytesDone / p.BytesTotal : 0d;
        });

        // The service owns the job, the exception mapping and the Activity row, so a copy that
        // outlives this window ends the same way as one watched to the end.
        try
        {
            return await _copies.StartAsync(Page.TargetName, Page.CopyDestination!, Page.RunCopyAsync, progress, cancel.Token)
                .ConfigureAwait(true);
        }
        finally
        {
            Review.IsCopying = false;
            _copyCancel = null;
            cancel.Dispose();
        }
    }

    private bool CanCancelCopy() => IsBusy && _copyCancel is not null;

    /// <summary>Stops the copy: no new file starts, and files in flight finish.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelCopy))]
    private void CancelCopy()
    {
        if (_copyCancel is { } cancel)
        {
            cancel.Cancel();
            Review.ProgressText = StagingCopyService.CancellingText;
        }
    }

    private bool CanClose() => !IsBusy || Review.IsCopying;

    /// <summary>Closes the wizard, reporting whether anything was written. Refused only while a
    /// script commit runs; during a copy it hands the copy to the status bar.</summary>
    [RelayCommand(CanExecute = nameof(CanClose))]
    private void Close()
    {
        if (CanClose())
        {
            RequestClose(_wroteSomething || Review.IsCopying);
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

    // Only the PowerShell flavour, and only from the result step: a Bash script has no shell
    // here, and ShellIntegration launches nothing unless the file still exists.
    private bool CanRunScript()
        => IsResultStep && Page.GeneratedScriptType == WbppScriptType.PowerShell && Page.ScriptPath is not null;

    /// <summary>PowerShell on the script this export wrote, in its own console window.</summary>
    [RelayCommand(CanExecute = nameof(CanRunScript))]
    private void RunScript()
    {
        if (CanRunScript())
        {
            _runScript?.Invoke(Page.ScriptPath!);
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
        Page.Dispose();
    }

    // Progress reports arrive on the copier's pool threads; each is posted to the UI thread.
    private sealed class PostedProgress(Action<Action> post, Action<StagingProgress> apply) : IProgress<StagingProgress>
    {
        public void Report(StagingProgress value) => post(() => apply(value));
    }
}
