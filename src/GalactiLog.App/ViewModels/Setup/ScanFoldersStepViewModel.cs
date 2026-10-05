using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Settings;
using Microsoft.Extensions.Logging;

namespace GalactiLog.App.ViewModels.Setup;

/// <summary>
/// One chosen folder in the setup wizard's step 1, with the shallow probe's count for it.
/// A top-level type rather than a nested one, so the view's <c>ItemTemplate</c> can name it in
/// <c>x:DataType</c> without nested-type syntax, exactly as <c>IntervalOption</c> does.
/// </summary>
public sealed partial class SetupScanFolderViewModel(string path) : ObservableObject
{
    /// <summary>The folder as the user chose it.</summary>
    public string Path { get; } = path;

    /// <summary>The probe's line for this folder: "Counting..." while it runs, then the count.
    /// </summary>
    [ObservableProperty]
    public partial string CountText { get; internal set; } = "Counting...";

    /// <summary>The probe's count, or null while it is still running.</summary>
    internal int? Count { get; set; }
}

/// <summary>
/// Design-spec 12.1's step 1: the list of chosen scan folders, a folder picker, a remove button
/// per row, and a live count of supported files found by the shallow probe. Persists
/// <c>general.scan_roots</c>.
/// </summary>
/// <remarks>
/// <para>
/// Next is disabled until at least one folder is chosen, which is spec 12.1's own Actions cell and
/// the roadmap's first named assertion for this row.
/// </para>
/// <para>
/// The nesting rule lives in <c>ScanFilterConfig.RefuseScanRoot</c>, for the reason spec 10.1
/// gives: each root carries its own confinement boundary, so a root nested inside another would
/// walk and ingest the same file twice. <c>ScanFilterConfig.Validate</c> enforces it on the write
/// path, and this step and the Settings Library tab both show its one sentence inline (FIXER LIST
/// F10, opened by the Task 5 review and narrowed by the Task 9 review's minor 2).
/// </para>
/// <para>
/// Nothing here touches the filesystem except through <see cref="SupportedFileProbe"/>, which
/// reads directory entries and opens nothing. The folder picker is an Avalonia storage-provider
/// call in the view, handed here as a plain string (HANDOFF.md section 5).
/// </para>
/// </remarks>
public sealed partial class ScanFoldersStepViewModel : SetupStepViewModel
{
    private readonly Func<string, CancellationToken, Task<int>> _probe;
    private readonly List<Task> _probes = [];

    /// <param name="probe">The shared shallow probe, normally
    /// <c>SupportedFileProbe.Count</c> on a background thread. One probe, two callers: this step
    /// and the Settings Library tab.</param>
    public ScanFoldersStepViewModel(
        Func<string, CancellationToken, Task<int>> probe,
        Action<Action>? post = null,
        ILogger? logger = null)
        : base(post, logger)
    {
        _probe = probe;
        Folders.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasFolders));
            RaiseCanAdvanceChanged();
            RefreshTotal();
        };
    }

    /// <inheritdoc />
    public override string Title => "Scan folders";

    /// <summary>The chosen folders, in the order they were added.</summary>
    public ObservableCollection<SetupScanFolderViewModel> Folders { get; } = [];

    /// <summary>False renders the "No folders chosen yet" empty state.</summary>
    public bool HasFolders => Folders.Count > 0;

    /// <summary>The refusal from the last <see cref="AddFolder"/>, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorMessage))]
    public partial string? ErrorMessage { get; private set; }

    public bool HasErrorMessage => ErrorMessage is not null;

    /// <summary>The total across every chosen folder, as one line.</summary>
    [ObservableProperty]
    public partial string TotalText { get; private set; } = "No folders chosen yet.";

    /// <summary>Spec 12.1's own rule: Next is disabled until at least one folder is chosen.
    /// </summary>
    public override bool CanAdvance => Folders.Count > 0;

    /// <summary>Every in-flight probe, so a test awaits the counts instead of sleeping.</summary>
    internal Task PendingProbes
    {
        get
        {
            lock (_probes)
            {
                return Task.WhenAll(_probes.ToArray());
            }
        }
    }

    /// <summary>
    /// Adds a folder, refusing a relative path, a duplicate, and a parent or child of a folder
    /// already in the list (spec 10.1).
    /// </summary>
    /// <returns>True when the folder was added.</returns>
    public bool AddFolder(string path)
    {
        var candidate = path?.Trim() ?? "";
        if (candidate.Length == 0)
        {
            return false;
        }

        // FIXER LIST F10 and the Task 9 review's minor 2: the rule and its wording are
        // ScanFilterConfig.RefuseScanRoot's, which is also what ScanFilterConfig.Validate enforces
        // on the write path and what the Settings Library tab shows. These folders become
        // general.scan_roots, so the same gesture is now refused with the same sentence on both
        // surfaces rather than two that disagree about what to call them.
        if (ScanFilterConfig.RefuseScanRoot(candidate, [.. Folders.Select(row => row.Path)]) is { } refusal)
        {
            ErrorMessage = refusal;
            return false;
        }

        ErrorMessage = null;
        var row = new SetupScanFolderViewModel(candidate);
        Folders.Add(row);
        StartProbe(row);
        return true;
    }

    /// <inheritdoc />
    public override void Load(GeneralSettings general)
    {
        Folders.Clear();
        foreach (var root in general.ScanRoots)
        {
            var row = new SetupScanFolderViewModel(root);
            Folders.Add(row);
            StartProbe(row);
        }
    }

    /// <inheritdoc />
    public override GeneralSettings Apply(GeneralSettings general)
    {
        // Captured on the UI thread, before the mutation is handed to the store: the mutation
        // itself runs inside the write gate and must not read an observable collection.
        var roots = Folders.Select(folder => folder.Path).ToArray();
        return general with { ScanRoots = roots };
    }

    [RelayCommand]
    private void RemoveFolder(SetupScanFolderViewModel row)
    {
        if (Folders.Remove(row))
        {
            ErrorMessage = null;
        }
    }

    // Off the UI thread, always: a probe on a disconnected share can block for seconds even
    // bounded. Linked to the wizard's lifetime, so closing it stops the count.
    private void StartProbe(SetupScanFolderViewModel row)
    {
        var token = Lifetime;
        var task = Task.Run(
            async () =>
            {
                int count;
                try
                {
                    count = await _probe(row.Path, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // A probe that fails is a count nobody gets, not a wizard that falls over.
                    Logger.LogWarning(ex, "Counting supported files under {Folder} failed", row.Path);
                    Post(() =>
                    {
                        if (!IsDisposed)
                        {
                            row.CountText = "Count unavailable";
                            row.Count = 0;
                            RefreshTotal();
                        }
                    });
                    return;
                }

                if (token.IsCancellationRequested)
                {
                    return;
                }

                Post(() =>
                {
                    if (IsDisposed)
                    {
                        return;
                    }

                    row.Count = count;
                    row.CountText = $"~{SupportedFileProbe.Format(count)} supported files";
                    RefreshTotal();
                });
            },
            token);

        lock (_probes)
        {
            _probes.RemoveAll(existing => existing.IsCompleted);
            _probes.Add(task);
        }
    }

    // Recomputes the total line from every row's current count.
    //
    // UI thread only, and it has three callers that are all on it: the CollectionChanged handler
    // above (raised from AddFolder and RemoveFolder, both user gestures), and the two Post
    // closures in StartProbe, which reach the UI thread through the step's post seam. It reads an
    // ObservableCollection and writes an observable property, so a second thread running it
    // concurrently would both enumerate a collection another thread is adding to and let a stale
    // "Counting..." line win the last write. A test that drives it with an inline post seam must
    // therefore await each probe before starting the next (Phase 10 fixer list code item 1).
    private void RefreshTotal()
    {
        if (Folders.Count == 0)
        {
            TotalText = "No folders chosen yet.";
            return;
        }

        if (Folders.Any(folder => folder.Count is null))
        {
            TotalText = "Counting supported files...";
            return;
        }

        // Each folder's figure is capped at the probe's ceiling, so a total built from capped
        // figures is itself a floor: "or more" carries through from any folder that hit it.
        var total = Folders.Sum(folder => folder.Count ?? 0);
        var atCeiling = Folders.Any(folder => folder.Count >= SupportedFileProbe.MaxCounted);
        var figure = atCeiling
            ? $"{total.ToString(System.Globalization.CultureInfo.CurrentCulture)} or more"
            : total.ToString(System.Globalization.CultureInfo.CurrentCulture);

        // Spec 12.1, fixer-list item 40: the label reads as an estimate rather than a promise the
        // first real scan then contradicts, because the shallow probe undercounts a folder that
        // nests its support files more than three levels down. The tilde and the trailing
        // "(estimate)" both say so; SupportedFileProbe's own ceiling wording is unchanged, and
        // "~1000 or more" tells a reader the truth twice, which is acceptable.
        TotalText = $"~{figure} supported files found in {Folders.Count} "
            + $"folder{(Folders.Count == 1 ? "" : "s")} (estimate).";
    }
}
