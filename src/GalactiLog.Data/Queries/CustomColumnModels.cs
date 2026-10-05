using GalactiLog.Core.Settings;

namespace GalactiLog.Data.Queries;

/// <summary>One row of `custom_columns` as every surface reads it: the stored words already
/// parsed, the options already deserialized. Spec 5.19.</summary>
/// <param name="Options">The dropdown options in the user's own order, empty for the other two
/// types. Never null, so no caller guards.</param>
public sealed record CustomColumnDefinition(
    Guid Id,
    string Name,
    string Slug,
    CustomColumnType Type,
    CustomColumnScope Scope,
    IReadOnlyList<string> Options,
    int DisplayOrder,
    DateTime CreatedAt,
    int ValueCount);

/// <summary>The four key parts of one value slot (spec 5.20). Built by the four factories below
/// so no caller assembles a key by hand and no caller can put a date on a target-scope value.
/// </summary>
public readonly record struct CustomValueKey(
    Guid? TargetId,
    Guid? MosaicId,
    DateOnly? SessionDate,
    string? RigLabel)
{
    /// <summary>Spec 5.20's `target` scope key: the target only.</summary>
    public static CustomValueKey ForTarget(Guid targetId)
        => new(targetId, MosaicId: null, SessionDate: null, RigLabel: null);

    /// <summary>Spec 5.20's `session` scope key: the target plus the night.</summary>
    public static CustomValueKey ForSession(Guid targetId, DateOnly night)
        => new(targetId, MosaicId: null, night, RigLabel: null);

    /// <summary>Spec 5.20's `rig` scope key: the target, the night and the rig label (U3, per
    /// night and per rig, not per rig across the library).</summary>
    public static CustomValueKey ForRig(Guid targetId, DateOnly night, string rigLabel)
        => new(targetId, MosaicId: null, night, rigLabel);

    /// <summary>Spec 5.20's `mosaic` scope key (Phase 18): the mosaic only.</summary>
    public static CustomValueKey ForMosaic(Guid mosaicId)
        => new(TargetId: null, mosaicId, SessionDate: null, RigLabel: null);
}

/// <summary>One stored value, as the read methods return it.</summary>
public sealed record CustomValueRow(Guid ColumnId, CustomValueKey Key, string Value);

/// <summary>Why a write ended. Spec 12.15's validation table, one member per refusal.</summary>
public enum CustomWriteStatus
{
    Written,
    Deleted,
    EmptyName,
    NameTooLong,
    DuplicateName,
    DropdownWithNoOptions,
    DuplicateOption,
    OptionStillUsed,
    NotABoolean,
    NotAnOption,
    OptionlessDropdown,
    ValueTooLong,
    ScopeNotAvailable,
    ColumnNotFound,

    /// <summary>The key's four parts are not the ones the column's scope names (spec 5.20's key
    /// table, U3): a target-scope key carrying a night, a rig-scope key carrying no rig label, a
    /// target-keyed scope's key carrying a mosaic id, a mosaic-scope key carrying anything but its
    /// mosaic id. Like <see cref="ColumnNotFound"/> it is a programming error rather than a user
    /// path and carries a null message: no surface shows it, and a surface that builds its key
    /// through the four factories cannot reach it.</summary>
    KeyDoesNotMatchScope,

    /// <summary>Options were submitted for a column that is not a dropdown. Create and Update answer
    /// it alike, so one list of options means the same thing on both.</summary>
    OptionsOnNonDropdown,
}

/// <summary>What one repository write did. `Message` is null exactly when `Status` is `Written`
/// or `Deleted`; otherwise it is the sentence the surface shows verbatim, already composed with
/// its name, option, value or count.</summary>
public sealed record CustomWriteResult(
    CustomWriteStatus Status,
    string? Message,
    CustomColumnDefinition? Column = null)
{
    public bool Ok => Status is CustomWriteStatus.Written or CustomWriteStatus.Deleted;
}

/// <summary>Which way one custom filter narrows (spec 12.15's truth table).</summary>
public enum CustomFilterMode { Any, Yes, No, Contains, Equals }

/// <summary>One active custom filter, as `TargetListingCriteria` carries it. `Mode` `Any` never
/// reaches the criteria: the panel drops it, so a filter present in the list always contributes a
/// clause and `AnyFilterActive` cannot disagree with the SQL.</summary>
public sealed record CustomColumnFilter(string Slug, CustomFilterMode Mode, string? Text);

/// <summary>
/// Every sentence spec 12.15's "Validation, quoted" table names, composed once. The repository is
/// the only caller; no surface builds one of these strings (design lesson 2).
/// </summary>
/// <remarks>
/// The three composed sentences use a straight double quote around the interpolated part, exactly
/// as spec 12.15 prints them: <c>A column named "Priority" already exists.</c> No typographic
/// quotation mark appears anywhere in this phase's strings.
/// <para>
/// Ruling C14 governs <see cref="OptionStillUsed"/>: at one it reads "1 value still uses this
/// option. Change it first, or delete the column.", and at two or more it is the spec's approved
/// sentence. One branch, and the method's own summary states it.
/// </para>
/// </remarks>
public static class CustomColumnMessages
{
    public const string EmptyName = "Enter a name for the column.";
    public const string NameTooLong = "Keep the column name to 60 characters or fewer.";
    public const string DropdownWithNoOptions = "Add at least one option before adding a dropdown column.";
    public const string NotABoolean = "A checkbox column stores only yes or no.";
    public const string OptionlessDropdown = "This column has no options yet. Add one on the Custom Columns tab.";
    public const string ValueTooLong = "Keep the value to 500 characters or fewer.";
    public const string ScopeNotAvailable = "This column scope is not available yet.";
    public const string OptionsOnNonDropdown = "Only a dropdown column can have options.";

    /// <summary>What a cell shows when the write delegate threw rather than refusing. Spec 12.15's
    /// validation table has no sentence for it: every sentence there names a validation the
    /// repository performed, and a thrown write performed none. It lives here with the others so a
    /// surface still composes no sentence of its own.</summary>
    public const string CouldNotSave = "The value could not be saved. Try again.";

    /// <summary>What a cell shows when the column it edits is no longer in the catalogue. The write
    /// result carries no message of its own, because the repository answers
    /// <see cref="CustomWriteStatus.ColumnNotFound"/> where it can only be a programming error; a
    /// cell drawn before a database reset, or before a delete the surface has not heard about yet,
    /// reaches it as an ordinary user path, and a cell that reverts in silence looks like a working
    /// control that lost the reader's input. "Try again" would be the wrong advice here, which is why
    /// this is its own sentence rather than <see cref="CouldNotSave"/>.</summary>
    public const string ColumnGone = "This column no longer exists.";

    public static string DuplicateName(string name) => $"A column named \"{name}\" already exists.";

    public static string DuplicateOption(string option) => $"\"{option}\" is already an option.";

    /// <summary>Ruling C14: the refusal sentence reads properly at one, "1 value still uses this
    /// option. Change it first, or delete the column.", and the approved sentence at two or more.
    /// One branch, one case.</summary>
    public static string OptionStillUsed(int count)
        => count == 1
            ? "1 value still uses this option. Change it first, or delete the column."
            : $"{count} values still use this option. Change them first, or delete the column.";

    public static string NotAnOption(string value) => $"\"{value}\" is not one of this column's options.";
}
