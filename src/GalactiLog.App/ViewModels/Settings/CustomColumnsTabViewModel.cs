using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.Settings;

/// <summary>One on-screen type choice (user choice 16): the stored enum and its label. The three
/// spellings here are the only ones this phase's UI shows; the stored word comes from
/// <see cref="CustomColumnSlug.Word(CustomColumnType)"/>, never from this label.</summary>
public sealed record CustomColumnTypeOption(CustomColumnType Value, string Label);

/// <summary>One on-screen scope choice (user choice 17). <c>"Night"</c> maps to
/// <see cref="CustomColumnScope.Session"/>, which is deliberate: the rest of the application says
/// Night and never Session.</summary>
public sealed record CustomColumnScopeOption(CustomColumnScope Value, string Label);

/// <summary>
/// Spec 12.15's Custom Columns settings tab: a create form over the three column types and three
/// scopes, and a table of every defined column with its own edit-in-place, reorder and two-press
/// delete.
/// </summary>
/// <remarks>
/// Takes its five repository operations as delegates, never <c>CustomColumnRepository</c> itself,
/// so the tab builds and drives in a unit test with no database (spec 18.3). Every one of them
/// runs inside a <c>Task.Run</c> and publishes back through <see cref="_post"/>; there is no
/// <c>ConfigureAwait(false)</c> anywhere in this file, matching the reason
/// <c>EquipmentTabViewModel.SaveAsync</c> carries the same rule (the generated
/// <c>AsyncRelayCommand</c> raises its own property and <c>CanExecute</c> notifications when the
/// awaited task completes, and those must run on the dispatcher).
/// <para>
/// The tab composes no refusal sentence of its own (design lesson 2): every write answers a
/// <see cref="CustomWriteResult"/>, whose non-null <c>Message</c> is shown verbatim in
/// <see cref="ErrorMessage"/>. The only sentence this file assembles itself is
/// <see cref="CustomColumnRowViewModel.DeleteConfirmMessage"/>, which is asked before any write is
/// attempted and is therefore not one of the repository's refusals.
/// </para>
/// <para>
/// Every write reloads the whole list from <c>load</c> on success rather than patch the affected
/// row or reorder <see cref="Rows"/> itself (spec 12.15 section 4.4), so the table can never show
/// an order, a name or a value the catalogue does not hold, and a failed write leaves
/// <see cref="Rows"/> completely untouched: there is no optimistic state to unwind.
/// </para>
/// </remarks>
public sealed partial class CustomColumnsTabViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<CustomColumnDefinition>> _load;
    private readonly Func<string, CustomColumnType, CustomColumnScope, IReadOnlyList<string>, CustomWriteResult> _create;
    private readonly Func<Guid, string, IReadOnlyList<string>, CustomWriteResult> _update;
    private readonly Func<Guid, bool, CustomWriteResult> _reorder;
    private readonly Func<Guid, CustomWriteResult> _delete;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;

    // Review P2-1: the same shape EquipmentTabViewModel.cs:544-586 uses over its own Load, shared
    // across both the load and the write paths here because both touch Rows. Incremented by
    // whichever of RefreshAsync or RunWriteAsync is issued; PublishLoad and PublishWrite each drop
    // their answer, untouched, when a newer call has already been issued by the time theirs comes
    // back, so a slow load cannot land after a faster write (or the reverse) and overwrite it.
    private int _generation;

    /// <param name="load">Normally <c>CustomColumnRepository.List</c>. Called off the UI thread.
    /// </param>
    /// <param name="create">Normally <c>CustomColumnRepository.Create</c>.</param>
    /// <param name="update">Normally <c>CustomColumnRepository.Update</c>.</param>
    /// <param name="reorder">Normally <c>CustomColumnRepository.Reorder</c>.</param>
    /// <param name="delete">Normally <c>CustomColumnRepository.Delete</c>.</param>
    /// <param name="post">How to reach the UI thread. Defaults to <c>UiPost.Default</c>.</param>
    /// <param name="logger">Optional. A failed load or write is logged, never rethrown on the UI
    /// thread (required case 21).</param>
    public CustomColumnsTabViewModel(
        Func<IReadOnlyList<CustomColumnDefinition>> load,
        Func<string, CustomColumnType, CustomColumnScope, IReadOnlyList<string>, CustomWriteResult> create,
        Func<Guid, string, IReadOnlyList<string>, CustomWriteResult> update,
        Func<Guid, bool, CustomWriteResult> reorder,
        Func<Guid, CustomWriteResult> delete,
        Action<Action>? post = null,
        ILogger? logger = null)
    {
        _load = load;
        _create = create;
        _update = update;
        _reorder = reorder;
        _delete = delete;
        _post = post ?? UiPost.Default;
        _logger = logger ?? NullLogger.Instance;

        TypeOptions =
        [
            new CustomColumnTypeOption(CustomColumnType.Boolean, "Checkbox"),
            new CustomColumnTypeOption(CustomColumnType.Text, "Text"),
            new CustomColumnTypeOption(CustomColumnType.Dropdown, "Dropdown"),
        ];
        ScopeOptions =
        [
            new CustomColumnScopeOption(CustomColumnScope.Target, "Target"),
            new CustomColumnScopeOption(CustomColumnScope.Session, "Night"),
            new CustomColumnScopeOption(CustomColumnScope.Rig, "Rig"),
        ];
        SelectedType = TypeOptions[0];
        SelectedScope = ScopeOptions[0];

        // Section 4 of the brief, and the reason FilterPanelViewModel.cs:158 gives for the same
        // choice: publishing the first load can raise a property change nothing has attached a
        // handler to yet from inside a constructor. RefreshAsync is called by the owning view's
        // OnAttachedToVisualTree instead (CustomColumnsTabView.axaml.cs), which is also what lets
        // a rebuilt view (a test that constructs the view twice over one view-model) reload rather
        // than trust a state that may be stale.
    }

    /// <summary>The three on-screen type choices, in the create form's own order.</summary>
    public IReadOnlyList<CustomColumnTypeOption> TypeOptions { get; }

    /// <summary>The three on-screen scope choices, in the create form's own order.</summary>
    public IReadOnlyList<CustomColumnScopeOption> ScopeOptions { get; }

    /// <summary>Every defined column, in <c>display_order</c>. Rebuilt wholesale on every load and
    /// every successful write; never mutated in place.</summary>
    public ObservableCollection<CustomColumnRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    public partial bool LoadFailed { get; private set; }

    public string LoadFailedMessage => "The custom columns could not be loaded.";

    /// <summary>Spec 12.15's empty-state sentence, verbatim (required case 15). Hidden while the
    /// first load is in flight or failed, so it never flashes ahead of the real rows.</summary>
    public bool ShowEmptyState => !IsLoading && !LoadFailed && Rows.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorMessage))]
    public partial string? ErrorMessage { get; private set; }

    public bool HasErrorMessage => ErrorMessage is not null;

    /// <summary>True while a load or a write is in flight. The view disables the whole form and
    /// table on it; commands still repeat their own guards (TRACKING section 6 item 13), so this
    /// is a convenience, not the only gate.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial bool IsBusy { get; private set; }

    // ---- the create form -----------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CreateDisabledReason))]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial string NewName { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOptionsEditor))]
    [NotifyPropertyChangedFor(nameof(CreateDisabledReason))]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial CustomColumnTypeOption SelectedType { get; set; }

    [ObservableProperty]
    public partial CustomColumnScopeOption SelectedScope { get; set; }

    /// <summary>Required case 5: the options editor is a wrap of chips over a text box, shown only
    /// while <see cref="SelectedType"/> is Dropdown.</summary>
    public bool ShowOptionsEditor => SelectedType.Value == CustomColumnType.Dropdown;

    public ObservableCollection<string> NewOptions { get; } = [];

    [ObservableProperty]
    public partial string NewOptionText { get; set; } = "";

    /// <summary>The sentence when the last typed option was refused, reused verbatim from
    /// <see cref="CustomColumnMessages"/> (design lesson 2: this file composes no sentence of its
    /// own).</summary>
    [ObservableProperty]
    public partial string? NewOptionError { get; set; }

    /// <summary>Why Add column cannot be pressed right now, or null when it can. Bound to the
    /// button's tooltip (spec 12.15 section 4.1's table), so the reason is never composed twice.
    /// </summary>
    public string? CreateDisabledReason
    {
        get
        {
            if (NewName.Trim().Length == 0)
            {
                return CustomColumnMessages.EmptyName;
            }

            if (SelectedType.Value == CustomColumnType.Dropdown && NewOptions.Count == 0)
            {
                return CustomColumnMessages.DropdownWithNoOptions;
            }

            return null;
        }
    }

    private bool CanCreate() => !IsBusy && CreateDisabledReason is null;

    /// <summary>Adds one option chip to the create form, trimmed, with a case-insensitive repeat
    /// refused by the shared sentence (required case 6, mirroring
    /// <c>CustomColumnRepository.NormalizeOptions</c>'s own comparison).</summary>
    [RelayCommand]
    private void AddNewOption()
    {
        var option = NewOptionText.Trim();
        NewOptionError = null;
        if (option.Length == 0)
        {
            return;
        }

        if (NewOptions.Contains(option, StringComparer.OrdinalIgnoreCase))
        {
            NewOptionError = CustomColumnMessages.DuplicateOption(option);
            return;
        }

        NewOptions.Add(option);
        NewOptionText = "";
        OnPropertyChanged(nameof(CreateDisabledReason));
        CreateCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void RemoveNewOption(string option)
    {
        NewOptions.Remove(option);
        OnPropertyChanged(nameof(CreateDisabledReason));
        CreateCommand.NotifyCanExecuteChanged();
    }

    // TRACKING section 6 item 13: RelayCommand.Execute ignores CanExecute, so a blank name or an
    // optionless dropdown is refused here too, not only by the button's CanExecute (required
    // cases 3 and 4).
    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        if (!CanCreate())
        {
            return;
        }

        var name = NewName;
        var type = SelectedType.Value;
        var scope = SelectedScope.Value;
        // Only a dropdown carries options, so only a dropdown sends any: the chips the user typed
        // while the type was Dropdown are kept on the hidden editor, in case the type comes back to
        // it, and are not submitted for a type that cannot hold them. Without this the repository
        // refuses the whole create with a sentence about an option editor that is no longer on
        // screen.
        IReadOnlyList<string> options = type == CustomColumnType.Dropdown ? NewOptions.ToList() : [];

        await RunWriteAsync(() => _create(name, type, scope, options), ClearCreateForm);
    }

    private void ClearCreateForm()
    {
        NewName = "";
        SelectedType = TypeOptions[0];
        SelectedScope = ScopeOptions[0];
        NewOptions.Clear();
        NewOptionText = "";
        NewOptionError = null;
        OnPropertyChanged(nameof(CreateDisabledReason));
    }

    // ---- the table -------------------------------------------------------------------------

    internal Task SaveRowAsync(CustomColumnRowViewModel row)
        => RunWriteAsync(() => _update(row.Id, row.EditName, [.. row.EditOptions]));

    internal Task DeleteRowAsync(CustomColumnRowViewModel row)
        => RunWriteAsync(() => _delete(row.Id));

    internal Task ReorderRowAsync(CustomColumnRowViewModel row, bool up)
        => RunWriteAsync(() => _reorder(row.Id, up));

    /// <summary>Disarms every other row and arms <paramref name="row"/>, so two rows are never
    /// armed at once (required case 14).</summary>
    internal void ArmDelete(CustomColumnRowViewModel row)
    {
        foreach (var other in Rows)
        {
            if (!ReferenceEquals(other, row))
            {
                other.SetConfirmPending(false);
            }
        }

        row.SetConfirmPending(true);
    }

    // ---- loading and writing -----------------------------------------------------------------

    internal Task? PendingLoad { get; private set; }

    internal Task? PendingWrite { get; private set; }

    /// <summary>Reads the whole list. Called by the owning view on its first attach, never from
    /// this constructor (see the constructor's own remarks).</summary>
    public Task RefreshAsync()
    {
        IsLoading = true;
        LoadFailed = false;
        var generation = ++_generation;
        var task = Task.Run(() =>
        {
            try
            {
                var definitions = _load();
                _post(() => PublishLoad(generation, definitions));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Loading the custom columns failed");
                _post(() => PublishLoad(generation, null));
            }
        });
        PendingLoad = task;
        return task;
    }

    private void PublishLoad(int generation, IReadOnlyList<CustomColumnDefinition>? definitions)
    {
        // Review P2-1: a newer load or write has already been issued, so this answer is stale and
        // is dropped whole, exactly as EquipmentTabViewModel.Publish drops a superseded load.
        if (generation != _generation)
        {
            return;
        }

        IsLoading = false;
        LoadFailed = definitions is null;
        if (definitions is not null)
        {
            RebuildRows(definitions);
        }
    }

    /// <summary>The one path every write in this file takes: run <paramref name="write"/> off the
    /// UI thread, reload on success (never on a refusal, and never by patching
    /// <see cref="Rows"/>), and publish both back through one post (required case 21: nothing here
    /// lets an exception leave the tab unusable).</summary>
    private async Task RunWriteAsync(Func<CustomWriteResult> write, Action? afterSuccess = null)
    {
        IsBusy = true;
        var generation = ++_generation;
        var task = Task.Run(() =>
        {
            try
            {
                var result = write();
                var definitions = result.Ok ? _load() : null;
                _post(() => PublishWrite(generation, result, definitions, afterSuccess));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A custom column write failed");
                _post(() => PublishWrite(generation, null, null, null));
            }
        });
        PendingWrite = task;
        await task;
    }

    private void PublishWrite(
        int generation, CustomWriteResult? result, IReadOnlyList<CustomColumnDefinition>? definitions, Action? afterSuccess)
    {
        // Review P2-1: the write half of the same guard. A write that settles after a newer load
        // or write was issued is dropped whole rather than allowed to overwrite what that newer
        // call already published.
        if (generation != _generation)
        {
            return;
        }

        IsBusy = false;
        if (result is null)
        {
            ErrorMessage = "The custom columns could not be saved. See the log for details.";
            return;
        }

        if (!result.Ok)
        {
            // The typed form (or the row being edited) is left exactly as it was (required case
            // 8): only the message changes.
            ErrorMessage = result.Message;
            return;
        }

        ErrorMessage = null;
        afterSuccess?.Invoke();
        if (definitions is not null)
        {
            RebuildRows(definitions);
        }
    }

    private void RebuildRows(IReadOnlyList<CustomColumnDefinition> definitions)
    {
        Rows.Clear();
        for (var index = 0; index < definitions.Count; index++)
        {
            var row = new CustomColumnRowViewModel(
                definitions[index], ArmDelete, DeleteRowAsync, SaveRowAsync, ReorderRowAsync)
            {
                IsFirst = index == 0,
                IsLast = index == definitions.Count - 1,
            };
            Rows.Add(row);
        }

        OnPropertyChanged(nameof(ShowEmptyState));
    }
}
