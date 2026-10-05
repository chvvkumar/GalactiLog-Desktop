using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using GalactiLog.App.ViewModels.Wizard;
using GalactiLog.Core.Io;
using GalactiLog.Core.Wbpp;

namespace GalactiLog.App.ViewModels.TargetDetail.Wbpp;

/// <summary>One step of the export wizard. Every step holds the one shared
/// <see cref="WbppExportViewModel"/>, which owns the state.</summary>
public abstract class WbppExportStepViewModel(WbppExportViewModel page, Action<Action>? post)
    : WizardStepViewModel(post)
{
    /// <summary>The six step topics in step order, the census source for the window's one bound
    /// help glyph.</summary>
    public static IReadOnlyList<string> HelpTopicIds { get; } =
    [
        "export.folders", "export.quality", "export.destination", "export.method", "export.review",
        "export.result",
    ];

    public WbppExportViewModel Page { get; } = page;
}

/// <summary>Step 1: the session rows and their level trees.</summary>
public sealed class FoldersStep : WbppExportStepViewModel
{
    public FoldersStep(WbppExportViewModel page, Action<Action>? post = null)
        : base(page, post) => Page.PropertyChanged += OnPageChanged;

    public override string Title => "Folders to copy";

    public override string HelpTopicId => HelpTopicIds[0];

    public override bool CanAdvance => !Page.IsLoading && Page.Sessions.Any(session => session.Chosen is not null);

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WbppExportViewModel.IsLoading) or nameof(WbppExportViewModel.Sessions))
        {
            RaiseCanAdvanceChanged();
        }
    }

    protected override void DisposeCore() => Page.PropertyChanged -= OnPageChanged;
}

/// <summary>Step 2: the quality panel. Always advanceable: the filter is optional.</summary>
public sealed class QualityStep(WbppExportViewModel page, Action<Action>? post = null)
    : WbppExportStepViewModel(page, post)
{
    public override string Title => "Quality filter (optional)";

    public override string HelpTopicId => HelpTopicIds[1];
}

/// <summary>Step 3: the staging folder, the subfolder box, the exclusions and Save as defaults.
/// </summary>
public sealed class DestinationStep : WbppExportStepViewModel
{
    public DestinationStep(WbppExportViewModel page, Action<Action>? post = null)
        : base(page, post) => Page.PropertyChanged += OnPageChanged;

    public override string Title => "Staging folder";

    public override string HelpTopicId => HelpTopicIds[2];

    public override bool CanAdvance => Page.IsGenerateAvailable;

    /// <summary>The subfolder box's label, naming the sanitized folder.</summary>
    public string SubfolderLabel => "Put this export in a subfolder named " + Page.SubfolderName;

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WbppExportViewModel.IsGenerateAvailable))
        {
            RaiseCanAdvanceChanged();
        }
    }

    protected override void DisposeCore() => Page.PropertyChanged -= OnPageChanged;
}

/// <summary>Step 4: Copy now, or Generate script in either flavour. Nothing persisted.
/// </summary>
public sealed partial class MethodStep(WbppExportViewModel page, Action<Action>? post = null)
    : WbppExportStepViewModel(page, post)
{
    public override string Title => "Copy or script";

    public override string HelpTopicId => HelpTopicIds[3];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCopyNow))]
    public partial bool IsScript { get; set; }

    /// <summary>The script flavour, preselected from the saved default OS.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPowerShell))]
    [NotifyPropertyChangedFor(nameof(IsBash))]
    public partial WbppScriptType ScriptType { get; set; } = page.DefaultScriptType;

    // Radio bindings: a radio's own uncheck writes false, which changes nothing here.
    public bool IsCopyNow
    {
        get => !IsScript;
        set
        {
            if (value)
            {
                IsScript = false;
            }
        }
    }

    public bool IsPowerShell
    {
        get => ScriptType == WbppScriptType.PowerShell;
        set
        {
            if (value)
            {
                ScriptType = WbppScriptType.PowerShell;
            }
        }
    }

    public bool IsBash
    {
        get => ScriptType == WbppScriptType.Bash;
        set
        {
            if (value)
            {
                ScriptType = WbppScriptType.Bash;
            }
        }
    }
}

/// <summary>Step 5: the summary, the blocks and warnings, and the progress of a running copy.
/// The commit itself is the wizard's, because the commit lock is.</summary>
public sealed partial class ReviewStep(WbppExportViewModel page, MethodStep method, Action<Action>? post = null)
    : WbppExportStepViewModel(page, post)
{
    /// <summary>The file-safety sentence in plain words.</summary>
    public const string FileSafetyText =
        "GalactiLog creates new files in the staging folder only. It never overwrites, deletes, "
        + "moves or renames anything, in your library or in the staging folder; a file already "
        + "there is skipped.";

    public override string Title => "Review";

    public override string HelpTopicId => HelpTopicIds[4];

    // Next never leaves this step: Commit does.
    public override bool CanAdvance => false;

    public bool IsCopy => !method.IsScript;

    public string NightsText => Page.NightCount == 1
        ? "1 night"
        : string.Create(CultureInfo.InvariantCulture, $"{Page.NightCount:N0} nights");

    public string MethodText => method.IsScript
        ? (method.ScriptType == WbppScriptType.Bash ? "Write a Bash script" : "Write a PowerShell script")
        : "Copy now, inside GalactiLog";

    public string DestinationText => Page.CopyDestination ?? "";

    [ObservableProperty]
    public partial IReadOnlyList<string> Blocks { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<string> Warnings { get; private set; } = [];

    [ObservableProperty]
    public partial string CommitLabel { get; private set; } = "";

    public bool IsBlocked => Blocks.Count > 0;

    [ObservableProperty]
    public partial bool IsCopying { get; set; }

    [ObservableProperty]
    public partial string ProgressText { get; set; } = "";

    [ObservableProperty]
    public partial double ProgressPercent { get; set; }

    public override void OnEntered()
    {
        var blocks = new List<string>();
        if (Page.CopyDestinationRefusal() is { } refusal)
        {
            blocks.Add(refusal);
        }

        if (Page.Totals is not { FrameCount: > 0 })
        {
            blocks.Add("The chosen folders hold no frames after the quality filter, so there is nothing to copy.");
        }

        Blocks = blocks;
        Warnings = blocks.Count > 0 ? [] : WarningsFor(Page.CopyDestination!);
        CommitLabel = method.IsScript ? "Write script" : CopyLabel(Page.Totals);

        OnPropertyChanged(nameof(IsBlocked));
        OnPropertyChanged(nameof(IsCopy));
        OnPropertyChanged(nameof(MethodText));
        OnPropertyChanged(nameof(DestinationText));
    }

    // The folder count is exact before the copy; the file count is not known until it enumerates.
    private static string CopyLabel(ExportTotals? totals)
    {
        var folders = totals?.FolderCount ?? 0;
        var label = folders == 1 ? "Copy 1 folder" : string.Create(CultureInfo.InvariantCulture, $"Copy {folders:N0} folders");
        return totals is not { } known ? label
            : known.FrameCount == 1 ? label + " (1 frame)"
            : label + string.Create(CultureInfo.InvariantCulture, $" ({known.FrameCount:N0} frames)");
    }

    private List<string> WarningsFor(string destination)
    {
        var warnings = new List<string>();
        if (Page.Totals?.SizeBytes is { } total)
        {
            try
            {
                var free = new DriveInfo(Path.GetPathRoot(destination)!).AvailableFreeSpace;
                if (free < total)
                {
                    warnings.Add("The staging drive has " + WbppPathText.Bytes(free) + " free, less than the "
                        + WbppPathText.Bytes(total) + " this export copies.");
                }
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                // An unanswerable free-space query hides the warning.
            }
        }

        var longest = Page.LongestDestinationLength;
        if (longest >= WbppExportViewModel.LongPathThreshold)
        {
            warnings.Add(string.Create(CultureInfo.InvariantCulture, $"The longest file this export creates is {longest} characters. ")
                + "The copy succeeds, but PixInsight or a script may refuse to open files at 260 "
                + "characters or more unless long paths are enabled. A shorter staging folder avoids it.");
        }

        if (ExistingFiles(destination) is > 0 and var existing)
        {
            warnings.Add(string.Create(CultureInfo.InvariantCulture, $"The destination already holds {existing:N0} ")
                + (existing == 1 ? "file" : "files")
                + ". A file of the same name is skipped and listed, never overwritten.");
        }

        return warnings;
    }

    // A read through UserFiles; an absent or unreadable folder counts as empty.
    // ponytail: synchronous walk on step entry; move off the UI thread if a staging folder ever holds a library's worth.
    private static int ExistingFiles(string folder)
    {
        var count = 0;
        var pending = new Stack<string>([folder]);
        while (pending.Count > 0)
        {
            try
            {
                foreach (var entry in UserFiles.EnumerateFileSystemEntries(pending.Pop()))
                {
                    if ((entry.Attributes & FileAttributes.Directory) == 0)
                    {
                        count++;
                    }
                    else if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        pending.Push(entry.FullName);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return count;
    }
}

/// <summary>Step 6: what the commit did, and its actions. The actions are the wizard's.
/// </summary>
public sealed partial class ResultStep(WbppExportViewModel page, Action<Action>? post = null)
    : WbppExportStepViewModel(page, post)
{
    public override string Title => "Result";

    public override string HelpTopicId => HelpTopicIds[5];

    /// <summary>The copy's result, or null for a script or before a commit.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCopyResult))]
    [NotifyPropertyChangedFor(nameof(IsScriptResult))]
    [NotifyPropertyChangedFor(nameof(OutcomeText))]
    [NotifyPropertyChangedFor(nameof(CountsText))]
    [NotifyPropertyChangedFor(nameof(DifferentSizePaths))]
    [NotifyPropertyChangedFor(nameof(FailureLines))]
    [NotifyPropertyChangedFor(nameof(PartialPaths))]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    [NotifyPropertyChangedFor(nameof(HasDifferentSize))]
    [NotifyPropertyChangedFor(nameof(HasFailures))]
    [NotifyPropertyChangedFor(nameof(HasPartials))]
    [NotifyPropertyChangedFor(nameof(ReportText))]
    public partial StagingCopyResult? Result { get; set; }

    public bool IsCopyResult => Result is not null;

    public bool IsScriptResult => Result is null;

    public string OutcomeText => OutcomeTextFor(Result);

    /// <summary>The outcome sentence for a copy result, or for the script when null.</summary>
    public static string OutcomeTextFor(StagingCopyResult? result) => result switch
    {
        null => "The script is written. Run it to copy the frames into the staging folder.",
        { Outcome: StagingOutcome.Cancelled } => "The copy was cancelled. Files already copied stay in the staging folder.",
        { Outcome: StagingOutcome.Aborted } r => "The copy stopped: " + r.AbortReason,
        _ => "Done. Open WBPP and use Add Directory on the staging folder.",
    };

    public string CountsText
    {
        get
        {
            if (Result is not { } r)
            {
                return "";
            }

            int Count(StagingSkipReason reason) => r.Skipped.Count(skip => skip.Reason == reason);
            return string.Create(
                CultureInfo.InvariantCulture,
                $"Copied {r.Copied:N0} files, {WbppPathText.Bytes(r.BytesCopied)}. Skipped {Count(StagingSkipReason.ExistsSameSize):N0} already present, "
                + $"{Count(StagingSkipReason.ExistsDifferentSize):N0} present but different size, "
                + $"{Count(StagingSkipReason.ReparsePoint):N0} linked folders not followed. {r.Failed.Count:N0} failed.");
        }
    }

    public IReadOnlyList<string> DifferentSizePaths => Result is { } r
        ? [.. r.Skipped.Where(skip => skip.Reason == StagingSkipReason.ExistsDifferentSize).Select(skip => skip.Path)]
        : [];

    public IReadOnlyList<string> FailureLines => Result is { } r
        ? [.. r.Failed.Select(failure => failure.SourcePath + ": " + failure.Message)]
        : [];

    /// <summary>Files in flight when the copy stopped, left with partial contents.</summary>
    public IReadOnlyList<string> PartialPaths => Result?.PartialPaths ?? [];

    public bool HasDifferentSize => DifferentSizePaths.Count > 0;

    public bool HasFailures => FailureLines.Count > 0;

    public bool HasPartials => PartialPaths.Count > 0;

    /// <summary>Save report is offered only when something was skipped or failed.</summary>
    public bool HasProblems => Result is { } r && (r.Skipped.Count > 0 || r.Failed.Count > 0);

    /// <summary>The text Save report writes.</summary>
    public string ReportText => Result is not { } r
        ? ""
        : string.Join(
            Environment.NewLine,
            [
                "Export for stacking: " + Page.TargetName,
                "Destination: " + Page.CopyDestination,
                OutcomeText,
                CountsText,
                "",
                .. r.Skipped.Select(skip => skip.Reason + ": " + skip.Path),
                .. FailureLines.Select(line => "Failed: " + line),
                .. PartialPaths.Select(path => "Partial: " + path),
            ]);
}
