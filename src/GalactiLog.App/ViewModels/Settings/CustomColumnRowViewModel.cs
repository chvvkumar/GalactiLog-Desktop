using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.ViewModels.Settings;

/// <summary>
/// One row of spec 12.15's Custom Columns table (<see cref="CustomColumnsTabViewModel"/>): the
/// column's own state plus the edit-in-place and two-press delete state
/// <c>MaintenanceActionViewModel</c>'s confirm already establishes for this application
/// (<c>MaintenanceActionViewModel.cs</c> lines 99, 104 and 129).
/// </summary>
/// <remarks>
/// Holds no repository and performs no write itself: every command calls back into the owning
/// <see cref="CustomColumnsTabViewModel"/> through a delegate, which is what lets the tab reload
/// the whole list from the catalogue after a write rather than have this row (or the tab) guess
/// at the new order or the new stored value (spec 12.15 section 4.4: the arrows do not reorder
/// their own collection optimistically). Type and scope are never editable once the column
/// exists (spec 12.15, departure 9, user choice 13); <see cref="TypeIsEnabled"/> and
/// <see cref="ScopeIsEnabled"/> are always false and exist so the view can bind a real
/// <c>IsEnabled</c> rather than simply never drawing a control there.
/// </remarks>
public sealed partial class CustomColumnRowViewModel : ObservableObject
{
    private readonly Action<CustomColumnRowViewModel> _armDelete;
    private readonly Func<CustomColumnRowViewModel, Task> _confirmDelete;
    private readonly Func<CustomColumnRowViewModel, Task> _save;
    private readonly Func<CustomColumnRowViewModel, bool, Task> _reorder;

    /// <summary>The tooltip both the Type and the Applies to controls carry while editing (spec
    /// 12.15, departure 9). One constant so the sentence is spelled once.</summary>
    public const string TypeScopeTooltip = "Type and scope are fixed when the column is created.";

    /// <param name="column">The definition this row shows.</param>
    /// <param name="armDelete">Normally <c>CustomColumnsTabViewModel.ArmDelete</c>: disarms every
    /// other row and arms this one, so two rows are never armed at once.</param>
    /// <param name="confirmDelete">Normally <c>CustomColumnsTabViewModel.DeleteRowAsync</c>. Runs
    /// off the UI thread and reloads the table on success.</param>
    /// <param name="save">Normally <c>CustomColumnsTabViewModel.SaveRowAsync</c>. Reads
    /// <see cref="EditName"/> and <see cref="EditOptions"/> at the moment it is called.</param>
    /// <param name="reorder">Normally <c>CustomColumnsTabViewModel.ReorderRowAsync</c>. The bool
    /// is <c>up</c>.</param>
    internal CustomColumnRowViewModel(
        CustomColumnDefinition column,
        Action<CustomColumnRowViewModel> armDelete,
        Func<CustomColumnRowViewModel, Task> confirmDelete,
        Func<CustomColumnRowViewModel, Task> save,
        Func<CustomColumnRowViewModel, bool, Task> reorder)
    {
        Column = column;
        _armDelete = armDelete;
        _confirmDelete = confirmDelete;
        _save = save;
        _reorder = reorder;
        Reseed();
    }

    /// <summary>The definition this row shows. Replaced wholesale on every reload
    /// (<see cref="ApplyColumn"/>): this row never patches a field of it.</summary>
    public CustomColumnDefinition Column { get; private set; }

    public Guid Id => Column.Id;

    public string Name => Column.Name;

    /// <summary>The three on-screen type words (user choice 16). The stored word is never shown.
    /// </summary>
    public string TypeLabel => Column.Type switch
    {
        CustomColumnType.Boolean => "Checkbox",
        CustomColumnType.Text => "Text",
        _ => "Dropdown",
    };

    /// <summary>The three on-screen scope words (user choice 17). "Night" is
    /// <see cref="CustomColumnScope.Session"/> everywhere else in this application, which is
    /// deliberate (spec 12.15).</summary>
    public string ScopeLabel => Column.Scope switch
    {
        CustomColumnScope.Target => "Target",
        CustomColumnScope.Session => "Night",
        _ => "Rig",
    };

    public bool IsDropdown => Column.Type == CustomColumnType.Dropdown;

    /// <summary>The stored options, in the column's own order. Empty for the other two types.
    /// </summary>
    public IReadOnlyList<string> Options => Column.Options;

    public int ValueCount => Column.ValueCount;

    /// <summary>Always false (spec 12.15, departure 9): the Type control is never editable, and
    /// this is what the view binds so a real disabled control carries <see cref="TypeScopeTooltip"/>
    /// rather than the row simply never drawing one.</summary>
    public bool TypeIsEnabled => false;

    /// <inheritdoc cref="TypeIsEnabled"/>
    public bool ScopeIsEnabled => false;

    /// <summary>Set by the owning tab after every reload: true for the first row in
    /// <c>display_order</c>, so its up arrow is disabled (spec 12.15 section 4.4).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    public partial bool IsFirst { get; set; }

    /// <inheritdoc cref="IsFirst"/>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    public partial bool IsLast { get; set; }

    [ObservableProperty]
    public partial bool IsEditing { get; private set; }

    /// <summary>The name being typed while <see cref="IsEditing"/>. Re-seeded from
    /// <see cref="Column"/> on <see cref="BeginEdit"/> and on <see cref="CancelEdit"/>, so a
    /// cancel always restores the stored spelling (required case 10).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string EditName { get; set; } = "";

    /// <summary>The options being edited while <see cref="IsEditing"/>. Empty and unused for the
    /// other two types.</summary>
    public ObservableCollection<string> EditOptions { get; } = [];

    [ObservableProperty]
    public partial string EditOptionText { get; set; } = "";

    /// <summary>The sentence when the last typed option was refused, null otherwise. Reused from
    /// <see cref="CustomColumnMessages"/> verbatim; this row composes no sentence of its own
    /// (design lesson 2).</summary>
    [ObservableProperty]
    public partial string? EditOptionError { get; set; }

    /// <summary>True between the first press of Delete and the second, or Cancel. Only one row in
    /// the table is ever armed (<see cref="ArmDelete"/>).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmDeleteCommand))]
    public partial bool ConfirmPending { get; private set; }

    /// <summary>Spec 12.15's delete sentence, verbatim, with <c>n</c> substituted from
    /// <see cref="ValueCount"/> (required case 13). Composed here rather than reused from
    /// <see cref="CustomColumnMessages"/> because it is not one of the repository's refusal
    /// sentences: it is asked before any write is attempted. Three branches, the same reason
    /// ruling C14 gave the option-removal refusal two: "all 0 of its values" and "all 1 of its
    /// values" both read as a defect.</summary>
    public string DeleteConfirmMessage => ValueCount switch
    {
        0 => "Delete this column? This cannot be undone.",
        1 => "Delete this column and its one value? This cannot be undone.",
        _ => $"Delete this column and all {ValueCount} of its values? This cannot be undone.",
    };

    // Shared by BeginEdit and CancelEdit: opening the editor and cancelling it both mean "show
    // what the catalogue holds", so there is one seam rather than two near-identical resets.
    private void Reseed()
    {
        EditName = Column.Name;
        EditOptions.Clear();
        foreach (var option in Column.Options)
        {
            EditOptions.Add(option);
        }

        EditOptionText = "";
        EditOptionError = null;
    }

    [RelayCommand]
    private void BeginEdit()
    {
        Reseed();
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        Reseed();
        IsEditing = false;
    }

    /// <summary>Adds one option chip, trimmed. A repeat (ordinal, case-insensitive, matching
    /// <c>CustomColumnRepository.NormalizeOptions</c>'s own comparison) is refused with the
    /// shared sentence rather than added twice (required case 6).</summary>
    [RelayCommand]
    private void AddEditOption()
    {
        var option = EditOptionText.Trim();
        EditOptionError = null;
        if (option.Length == 0)
        {
            return;
        }

        if (EditOptions.Contains(option, StringComparer.OrdinalIgnoreCase))
        {
            EditOptionError = CustomColumnMessages.DuplicateOption(option);
            return;
        }

        EditOptions.Add(option);
        EditOptionText = "";
        SaveCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void RemoveEditOption(string option)
    {
        EditOptions.Remove(option);
        SaveCommand.NotifyCanExecuteChanged();
    }

    private bool CanSave() => EditName.Trim().Length > 0 && (!IsDropdown || EditOptions.Count > 0);

    // TRACKING section 6 item 13: RelayCommand.Execute ignores CanExecute, so this body repeats
    // the guard (required case 3's sibling for this row, and design lesson 2: the repository
    // still validates behind it).
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!CanSave())
        {
            return;
        }

        await _save(this);
    }

    private bool CanMoveUp() => !IsFirst;

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private async Task MoveUpAsync()
    {
        if (IsFirst)
        {
            return;
        }

        await _reorder(this, true);
    }

    private bool CanMoveDown() => !IsLast;

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private async Task MoveDownAsync()
    {
        if (IsLast)
        {
            return;
        }

        await _reorder(this, false);
    }

    /// <summary>The first press (spec 12.15 section 4.3). Delegates to the tab so arming this row
    /// disarms every other one; this row does not know about its siblings.</summary>
    [RelayCommand]
    private void ArmDelete() => _armDelete(this);

    [RelayCommand]
    private void CancelDeleteConfirm() => ConfirmPending = false;

    private bool CanConfirmDelete() => ConfirmPending;

    [RelayCommand(CanExecute = nameof(CanConfirmDelete))]
    private async Task ConfirmDeleteAsync()
    {
        if (!CanConfirmDelete())
        {
            return;
        }

        await _confirmDelete(this);
    }

    /// <summary>Called by the owning tab only (<see cref="CustomColumnsTabViewModel.ArmDelete"/>).
    /// </summary>
    internal void SetConfirmPending(bool value) => ConfirmPending = value;
}
