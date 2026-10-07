using System.Text.Json;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.Data.Repositories;

/// <summary>
/// Spec 12.15's one custom-column write path. Every validation sentence lives here and no surface
/// carries a check of its own, so a fifth surface added later is validated by default (design
/// lesson 2).
/// </summary>
/// <remarks>
/// Takes the DI <see cref="DatabaseConnectionString"/> and opens its own short-lived tracking
/// context per call, matching <c>MergeRepository</c>, and each write method runs inside one
/// explicit <c>context.Database.BeginTransaction()</c>.
/// <para>
/// All synchronous, as every other repository in this namespace is. The caller runs them off the
/// UI thread and publishes through its own post seam.
/// </para>
/// </remarks>
public sealed class CustomColumnRepository(DatabaseConnectionString connectionString)
{
    /// <summary>
    /// Raised once after a definition write that really changed something: a create, a rename, an
    /// option edit, a reorder that moved a row, and a delete. A value write raises NOTHING, and
    /// neither does a refused write or a reorder that had nowhere to move.
    /// </summary>
    /// <remarks>
    /// The one mechanism by which an open surface learns that the column set moved. It is raised on
    /// whatever thread wrote, which is a pool thread for every write this application makes, so a
    /// subscriber that touches a bound collection posts to the UI thread itself; the composition
    /// root's own subscription does exactly that.
    /// <para>
    /// The event carries no payload. Every surface re-reads <see cref="List"/> for itself, because
    /// each one draws a different subset of it and a payload would be a second definition list to
    /// keep in step.
    /// </para>
    /// </remarks>
    public event EventHandler? Changed;

    /// <summary>
    /// Announces a definition change this repository did not write itself. The one caller is the
    /// database reset, which truncates <c>custom_columns</c> and <c>custom_column_values</c>
    /// directly and would otherwise leave every open surface holding cells for columns that are
    /// gone.
    /// </summary>
    public void NotifyDefinitionsChanged() => Changed?.Invoke(this, EventArgs.Empty);

    // ---- definitions -------------------------------------------------------------------

    /// <summary>Every column, ordered by <c>display_order</c>, then <c>created_at</c>, then
    /// <c>id</c>, each carrying its own stored-value count.</summary>
    /// <remarks>The third ordering term is this port's own. The web orders by two
    /// (<c>custom_columns.py</c> line 46); two rows sharing both would otherwise return in an
    /// unspecified order and the Settings tab's arrows would appear to do nothing every other
    /// press. A row whose <c>column_type</c> or <c>applies_to</c> does not parse is skipped: it is
    /// unreachable from any surface and exists only for a hand-edited catalogue.</remarks>
    public IReadOnlyList<CustomColumnDefinition> List()
    {
        using var context = Open();

        // ValueCount is a correlated scalar subquery, so the whole list is one round trip.
        var rows = context.CustomColumns
            .OrderBy(column => column.DisplayOrder)
            .ThenBy(column => column.CreatedAt)
            .ThenBy(column => column.Id)
            .Select(column => new
            {
                Column = column,
                ValueCount = context.CustomColumnValues.Count(value => value.ColumnId == column.Id),
            })
            .ToList();

        var definitions = new List<CustomColumnDefinition>(rows.Count);
        foreach (var row in rows)
        {
            if (Describe(row.Column, row.ValueCount) is { } definition)
            {
                definitions.Add(definition);
            }
        }

        return definitions;
    }

    /// <summary>Creates one column. The slug is derived here and never anywhere else.</summary>
    public CustomWriteResult Create(
        string name, CustomColumnType type, CustomColumnScope scope, IReadOnlyList<string> options)
    {
        using var context = Open();
        using var transaction = context.Database.BeginTransaction();

        // The order of these checks is the contract, because the first refusal wins.
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return Refused(CustomWriteStatus.EmptyName, CustomColumnMessages.EmptyName);
        }

        if (trimmed.Length > CustomColumnSlug.MaxNameLength)
        {
            return Refused(CustomWriteStatus.NameTooLong, CustomColumnMessages.NameTooLong);
        }

        // One read serving the duplicate-name test, the slug's `taken` predicate and the next
        // display_order.
        var existing = context.CustomColumns
            .Select(column => new { column.Name, column.Slug, column.DisplayOrder })
            .ToList();

        if (existing.Any(column => column.Name.Trim().Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            // Composed with the SUBMITTED name, not the stored one: the user is told what they
            // typed collides, not a spelling they never saw.
            return Refused(CustomWriteStatus.DuplicateName, CustomColumnMessages.DuplicateName(trimmed));
        }

        if (!IsOffered(scope))
        {
            // Unreachable from any surface: every scope the picker offers is accepted. It stays
            // because this is the choke point a scope this build does not know is refused at
            // (spec 12.15, design lesson 2).
            return Refused(CustomWriteStatus.ScopeNotAvailable, CustomColumnMessages.ScopeNotAvailable);
        }

        var (kept, duplicate) = NormalizeOptions(options);
        if (type == CustomColumnType.Dropdown && kept.Count == 0)
        {
            return Refused(CustomWriteStatus.DropdownWithNoOptions, CustomColumnMessages.DropdownWithNoOptions);
        }

        // Before the duplicate test, so a text column submitted with two equal options is told the
        // real reason rather than told about the duplicate.
        if (type != CustomColumnType.Dropdown && HasOptions(options))
        {
            return Refused(CustomWriteStatus.OptionsOnNonDropdown, CustomColumnMessages.OptionsOnNonDropdown);
        }

        if (duplicate is not null)
        {
            return Refused(CustomWriteStatus.DuplicateOption, CustomColumnMessages.DuplicateOption(duplicate));
        }

        var slugsTaken = existing.Select(column => column.Slug).ToHashSet(StringComparer.Ordinal);
        var row = new CustomColumn
        {
            Id = Guid.NewGuid(),
            Name = trimmed,
            Slug = CustomColumnSlug.Unique(trimmed, slugsTaken.Contains),
            ColumnType = CustomColumnSlug.Word(type),
            AppliesTo = CustomColumnSlug.Word(scope),
            // Null rather than "[]" for the other two types, so the column reads as "this type has
            // no options" rather than "this dropdown has none".
            DropdownOptions = type == CustomColumnType.Dropdown ? JsonSerializer.Serialize(kept) : null,
            DisplayOrder = existing.Count == 0 ? 0 : existing.Max(column => column.DisplayOrder) + 1,
            CreatedAt = DateTime.UtcNow,
        };

        context.CustomColumns.Add(row);
        context.SaveChanges();
        transaction.Commit();

        NotifyDefinitionsChanged();
        return new CustomWriteResult(CustomWriteStatus.Written, Message: null, Describe(row, valueCount: 0));
    }

    /// <summary>Renames a column and, on a dropdown, replaces its options. The slug, the type and
    /// the scope are never changed (spec 12.15, departure 1: the web re-slugs on every rename and
    /// orphans every stored <c>display.columns</c> entry at once). <paramref name="options"/> on a
    /// column that is not a dropdown is REFUSED, exactly as <see cref="Create"/> refuses it, and
    /// nothing is written; an empty list is not a list of options and renames as usual.</summary>
    public CustomWriteResult Update(Guid columnId, string name, IReadOnlyList<string> options)
    {
        using var context = Open();
        using var transaction = context.Database.BeginTransaction();

        var row = context.CustomColumns.SingleOrDefault(column => column.Id == columnId);
        if (row is null)
        {
            return ColumnNotFound();
        }

        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return Refused(CustomWriteStatus.EmptyName, CustomColumnMessages.EmptyName);
        }

        if (trimmed.Length > CustomColumnSlug.MaxNameLength)
        {
            return Refused(CustomWriteStatus.NameTooLong, CustomColumnMessages.NameTooLong);
        }

        var collides = context.CustomColumns
            .Where(column => column.Id != columnId)
            .Select(column => column.Name)
            .ToList()
            .Any(other => other.Trim().Equals(trimmed, StringComparison.OrdinalIgnoreCase));
        if (collides)
        {
            return Refused(CustomWriteStatus.DuplicateName, CustomColumnMessages.DuplicateName(trimmed));
        }

        var isDropdown = CustomColumnSlug.ParseType(row.ColumnType) == CustomColumnType.Dropdown;
        var kept = new List<string>();
        if (isDropdown)
        {
            var (normalized, duplicate) = NormalizeOptions(options);
            kept = normalized;
            if (kept.Count == 0)
            {
                return Refused(CustomWriteStatus.DropdownWithNoOptions, CustomColumnMessages.DropdownWithNoOptions);
            }

            if (duplicate is not null)
            {
                return Refused(CustomWriteStatus.DuplicateOption, CustomColumnMessages.DuplicateOption(duplicate));
            }

            // Departure 10: removing an option that stored values still hold is refused. The web
            // has no check at all and leaves those values outside their own option set. One count
            // over every removed option together, which is what the sentence says.
            var removed = ParseOptions(row.DropdownOptions)
                .Where(option => !kept.Contains(option, StringComparer.Ordinal))
                .ToList();
            if (removed.Count > 0)
            {
                var stillUsed = context.CustomColumnValues
                    .Count(value => value.ColumnId == columnId && removed.Contains(value.Value));
                if (stillUsed > 0)
                {
                    return Refused(CustomWriteStatus.OptionStillUsed, CustomColumnMessages.OptionStillUsed(stillUsed));
                }
            }
        }
        else if (HasOptions(options))
        {
            // Refused rather than ignored, and before the rename is written, so one list of options
            // means the same thing here as it does on Create and a caller is never told its whole
            // submission landed when half of it was dropped.
            return Refused(CustomWriteStatus.OptionsOnNonDropdown, CustomColumnMessages.OptionsOnNonDropdown);
        }

        row.Name = trimmed;
        if (isDropdown)
        {
            row.DropdownOptions = JsonSerializer.Serialize(kept);
        }

        context.SaveChanges();
        transaction.Commit();

        var valueCount = context.CustomColumnValues.Count(value => value.ColumnId == columnId);
        NotifyDefinitionsChanged();
        return new CustomWriteResult(CustomWriteStatus.Written, Message: null, Describe(row, valueCount));
    }

    /// <summary>Exchanges this column's <c>display_order</c> with its neighbour's, both rows in one
    /// transaction (departure 2). <paramref name="up"/> false moves it down. A first row moving up,
    /// a last row moving down, and a column not in the ordered list each write nothing and answer
    /// <c>Written</c> with a null message.</summary>
    /// <remarks>The web writes <c>display_order + 1</c> on the moved row alone
    /// (<c>CustomColumnsTab.tsx</c> lines 129 to 137), so two columns come to share an order and
    /// the arrows then stop moving anything. The swap keeps the orders dense and distinct.</remarks>
    public CustomWriteResult Reorder(Guid columnId, bool up)
    {
        using var context = Open();
        using var transaction = context.Database.BeginTransaction();

        var ordered = context.CustomColumns
            .OrderBy(column => column.DisplayOrder)
            .ThenBy(column => column.CreatedAt)
            .ThenBy(column => column.Id)
            .ToList();

        var index = ordered.FindIndex(column => column.Id == columnId);
        var neighbour = index + (up ? -1 : 1);
        if (index < 0 || neighbour < 0 || neighbour >= ordered.Count)
        {
            return Written();
        }

        (ordered[index].DisplayOrder, ordered[neighbour].DisplayOrder) =
            (ordered[neighbour].DisplayOrder, ordered[index].DisplayOrder);

        context.SaveChanges();
        transaction.Commit();

        // Only here, and not at the early return above: a first row asked to move up wrote nothing,
        // so there is nothing for a surface to re-read.
        NotifyDefinitionsChanged();
        return Written();
    }

    /// <summary>Deletes the column; the <c>column_id</c> cascade takes its values.</summary>
    /// <remarks>The slug is NOT removed from any <c>display.columns</c> list here. Spec 12.15: an
    /// inert slug is dropped on the next write of that list, which is <c>DisplayColumnWriter</c>'s
    /// business. This repository writes no settings document.</remarks>
    public CustomWriteResult Delete(Guid columnId)
    {
        using var context = Open();
        using var transaction = context.Database.BeginTransaction();

        var row = context.CustomColumns.SingleOrDefault(column => column.Id == columnId);
        if (row is null)
        {
            return ColumnNotFound();
        }

        context.CustomColumns.Remove(row);
        context.SaveChanges();
        transaction.Commit();

        NotifyDefinitionsChanged();
        return Written();
    }

    // ---- values ------------------------------------------------------------------------

    /// <summary>Spec 12.15's one value write. A null, empty or whitespace-only
    /// <paramref name="value"/> DELETES the row and answers <c>Deleted</c> (departure 6 and user
    /// choices 11 and 12), whatever the column's type. Otherwise it upserts on the column plus the
    /// four key parts.</summary>
    /// <remarks>
    /// The key's shape is checked against the column's scope before anything is read or written,
    /// and a disagreement is refused (<see cref="CustomWriteStatus.KeyDoesNotMatchScope"/>). Stated
    /// ceiling, deliberately not checked: <paramref name="key"/>'s target may be a merged-away one.
    /// A value written there can occupy a slot an unmerge is about to restore into, which throws
    /// <c>uq_custom_column_value</c> out of <c>MergeRepository.Unmerge</c>; that transaction rolls
    /// back, so nothing is corrupted, and no surface this phase ships reaches a merged-away
    /// target's cells.
    /// </remarks>
    public CustomWriteResult SetValue(Guid columnId, CustomValueKey key, string? value)
    {
        using var context = Open();
        using var transaction = context.Database.BeginTransaction();

        var column = context.CustomColumns.SingleOrDefault(row => row.Id == columnId);
        if (column is null)
        {
            // A programming error, not a user path: no surface shows this sentence, so there is
            // none.
            return ColumnNotFound();
        }

        var scope = CustomColumnSlug.ParseScope(column.AppliesTo);
        if (scope is not { } known || !IsOffered(known))
        {
            return Refused(CustomWriteStatus.ScopeNotAvailable, CustomColumnMessages.ScopeNotAvailable);
        }

        // The one key-shape guard, before anything is read or written and before the clear branch,
        // so a mis-shaped key cannot write, clear or count anywhere. A row whose key disagrees with
        // its column's scope is invisible to both reads yet still counts in ValueCount and in
        // Update's option census, so it would show the user a number they cannot account for and
        // could refuse an option removal they have no way to resolve. Six surfaces land on this one
        // method; the rule lives here and in no surface (design lesson 2).
        if (!KeyMatchesScope(known, key))
        {
            return KeyDoesNotMatchScope();
        }

        // The upsert slot is found before the clear branch, so a clear deletes the row it would
        // otherwise have written.
        var existing = context.CustomColumnValues.SingleOrDefault(row =>
            row.ColumnId == columnId
            && row.TargetId == key.TargetId
            && row.MosaicId == key.MosaicId
            && row.SessionDate == key.SessionDate
            && row.RigLabel == key.RigLabel);

        // Checked BEFORE the type validation, so clearing a dropdown through "Not set" and
        // clearing a text box both reach it. A boolean can never reach it: the check box always
        // sends `true` or `false`.
        if (string.IsNullOrWhiteSpace(value))
        {
            if (existing is not null)
            {
                context.CustomColumnValues.Remove(existing);
                context.SaveChanges();
                transaction.Commit();
            }

            return new CustomWriteResult(CustomWriteStatus.Deleted, Message: null);
        }

        var trimmed = value.Trim();
        var type = CustomColumnSlug.ParseType(column.ColumnType);
        var options = ParseOptions(column.DropdownOptions);

        switch (type)
        {
            case CustomColumnType.Boolean when trimmed != CustomColumnSlug.True && trimmed != CustomColumnSlug.False:
                return Refused(CustomWriteStatus.NotABoolean, CustomColumnMessages.NotABoolean);

            // Departure 4: the web skips its membership check when the option list is empty
            // (`custom_columns.py` line 154 requires `col.dropdown_options` to be truthy) and
            // stores anything sent. An option-less dropdown refuses every value instead.
            case CustomColumnType.Dropdown when options.Count == 0:
                return Refused(CustomWriteStatus.OptionlessDropdown, CustomColumnMessages.OptionlessDropdown);

            // Ordinal and case sensitive: an option "High" refuses "high".
            case CustomColumnType.Dropdown when !options.Contains(trimmed, StringComparer.Ordinal):
                return Refused(CustomWriteStatus.NotAnOption, CustomColumnMessages.NotAnOption(trimmed));

            case CustomColumnType.Text when trimmed.Length > CustomColumnSlug.MaxValueLength:
                return Refused(CustomWriteStatus.ValueTooLong, CustomColumnMessages.ValueTooLong);
        }

        var now = DateTime.UtcNow;
        if (existing is not null)
        {
            existing.Value = trimmed;
            existing.UpdatedAt = now;
        }
        else
        {
            // A unique-index violation on `uq_custom_column_value` is deliberately NOT caught. Two
            // writes to the same slot are serialized by the transaction, so a violation would mean
            // the key rule was broken upstream and swallowing it would hide that.
            context.CustomColumnValues.Add(new CustomColumnValue
            {
                Id = Guid.NewGuid(),
                ColumnId = columnId,
                TargetId = key.TargetId,
                MosaicId = key.MosaicId,
                SessionDate = key.SessionDate,
                RigLabel = key.RigLabel,
                Value = trimmed,
                UpdatedAt = now,
            });
        }

        context.SaveChanges();
        transaction.Commit();

        return Written();
    }

    /// <summary>Every target-scope value for a set of targets, for the dashboard page's rows. One
    /// round trip; an empty id set returns an empty list without issuing a command.</summary>
    public IReadOnlyList<CustomValueRow> TargetValues(IReadOnlyCollection<Guid> targetIds)
    {
        if (targetIds.Count == 0)
        {
            return [];
        }

        using var context = Open();
        var ids = targetIds.ToList();
        var targetWord = CustomColumnSlug.Word(CustomColumnScope.Target);

        // Joined on column_id and filtered by the OWNING COLUMN's scope, never on target_id alone:
        // a session-scope row carries the same target_id.
        var rows = from value in context.CustomColumnValues
                   join column in context.CustomColumns on value.ColumnId equals column.Id
                   where column.AppliesTo == targetWord
                       && value.TargetId != null
                       && ids.Contains(value.TargetId.Value)
                       && value.SessionDate == null
                       && value.RigLabel == null
                   select value;

        return Read(rows);
    }

    /// <summary>Every session-scope and rig-scope value of one target, for the Target detail page
    /// and for the dashboard's night expander. One round trip; the caller partitions by looking the
    /// column up in its own definition list.</summary>
    public IReadOnlyList<CustomValueRow> ValuesForTarget(Guid targetId)
    {
        using var context = Open();
        var sessionWord = CustomColumnSlug.Word(CustomColumnScope.Session);
        var rigWord = CustomColumnSlug.Word(CustomColumnScope.Rig);

        var rows = from value in context.CustomColumnValues
                   join column in context.CustomColumns on value.ColumnId equals column.Id
                   where value.TargetId == targetId
                       && (column.AppliesTo == sessionWord || column.AppliesTo == rigWord)
                   select value;

        return Read(rows);
    }

    /// <summary>Every mosaic-scope value for a set of mosaics, for the Mosaics table's rows
    /// (spec 12.15's mosaic scope). One round trip; an empty id set returns an empty list without
    /// issuing a command.</summary>
    public IReadOnlyList<CustomValueRow> ValuesForMosaics(IEnumerable<Guid> mosaicIds)
    {
        var ids = mosaicIds.ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        using var context = Open();
        var mosaicWord = CustomColumnSlug.Word(CustomColumnScope.Mosaic);

        // Joined on the owning column's scope, as TargetValues is, so a row written under another
        // scope never reaches a mosaic cell.
        var rows = from value in context.CustomColumnValues
                   join column in context.CustomColumns on value.ColumnId equals column.Id
                   where column.AppliesTo == mosaicWord
                       && value.MosaicId != null
                       && ids.Contains(value.MosaicId.Value)
                   select value;

        return Read(rows);
    }

    /// <summary>Every mosaic-scope value of one mosaic, for the mosaic detail page's header.</summary>
    public IReadOnlyList<CustomValueRow> ValuesForMosaic(Guid mosaicId) => ValuesForMosaics([mosaicId]);

    // ---- maintenance -------------------------------------------------------------------

    /// <summary>Spec 12.15's equipment rename: rewrites <c>rig_label</c> from the old canonical rig
    /// spelling to the new one, in one catalogue transaction. Returns the number of rows changed.
    /// Called from the Equipment tab's save, after <c>SaveEquipment</c> and beside the profile-map
    /// rewrite (spec 12.7).</summary>
    /// <remarks>
    /// Ruling C9: it takes the two rename maps rather than composed labels, reads the distinct
    /// stored labels itself, splits each on the exact separator <c>" / "</c> and applies both maps,
    /// so the split lives in one place and no caller composes labels. A label neither map changes
    /// is skipped.
    /// <para>
    /// The count returned is the number of rows this method wrote: those whose <c>rig_label</c>
    /// moved, plus those a fold deleted.
    /// </para>
    /// </remarks>
    public int RewriteRigLabels(
        IReadOnlyDictionary<string, string> telescopeRenames,
        IReadOnlyDictionary<string, string> cameraRenames)
    {
        if (telescopeRenames.Count == 0 && cameraRenames.Count == 0)
        {
            return 0;
        }

        using var context = Open();
        using var transaction = context.Database.BeginTransaction();

        var labels = context.CustomColumnValues
            .Where(value => value.RigLabel != null)
            .Select(value => value.RigLabel!)
            .Distinct()
            .ToList();

        var moves = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var label in labels)
        {
            var rewritten = RewriteLabel(label, telescopeRenames, cameraRenames);
            if (!string.Equals(rewritten, label, StringComparison.Ordinal))
            {
                moves[label] = rewritten;
            }
        }

        if (moves.Count == 0)
        {
            return 0;
        }

        // Every row is read before any label is assigned, and each row's new label is looked up by
        // its ORIGINAL label, so a rename chain (A to B while B goes to C) still lands once per
        // row rather than twice, and a swap lands each row on the other name exactly once.
        //
        // The rows read are those carrying a label that moves AND those already carrying a label
        // something else moves onto: a row of the second kind never moves, but it is what a fold
        // collides with.
        var touched = moves.Keys
            .Concat(moves.Values)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var rows = context.CustomColumnValues
            .Where(value => value.RigLabel != null && touched.Contains(value.RigLabel))
            .ToList();

        string Rewritten(CustomColumnValue row) => moves.GetValueOrDefault(row.RigLabel!, row.RigLabel!);

        // THE FOLD RULE, in plain words: the user can rename two telescopes, or two cameras, into
        // one name, and then two stored rig values that used to belong to different rigs belong to
        // the same one. When that happens the newest of them is kept and the others are deleted;
        // if two carry the same instant, the one with the higher id is kept, so the answer never
        // depends on the order the rows were read.
        //
        // Ruled by the coordinator rather than letting the unique index throw. The rollback that
        // would follow is not a one-time failure: the rename pairs are rebuilt from the loaded
        // names on every save, so the SAME collision would be retried and would fail again on every
        // later save of that tab, and those values would never reach a surface again.
        var folded = new HashSet<CustomColumnValue>();
        foreach (var slot in rows.GroupBy(row => (row.ColumnId, row.TargetId, row.MosaicId, row.SessionDate, Rewritten(row))))
        {
            foreach (var loser in slot
                .OrderByDescending(row => row.UpdatedAt)
                .ThenByDescending(row => row.Id)
                .Skip(1))
            {
                context.CustomColumnValues.Remove(loser);
                folded.Add(loser);
            }
        }

        // The deletes are flushed first, inside this same transaction, so no surviving row is moved
        // onto a slot the row it replaces still occupies. EF does not promise to order a delete
        // before an update of the same table, and uq_custom_column_value would catch the batch
        // mid-flight if it did not.
        if (folded.Count > 0)
        {
            context.SaveChanges();
        }

        var moved = 0;
        foreach (var row in rows)
        {
            var rewritten = Rewritten(row);
            if (!folded.Contains(row) && !string.Equals(rewritten, row.RigLabel, StringComparison.Ordinal))
            {
                row.RigLabel = rewritten;
                moved++;
            }
        }

        context.SaveChanges();
        transaction.Commit();

        return moved + folded.Count;
    }

    /// <summary>Spec 12.15's merge: moves the loser's values to the winner where the winner holds
    /// no value in the same slot, a slot being <c>(column_id, session_date, rig_label)</c>. Returns
    /// the ids moved, in <c>id</c> order, for the manifest.</summary>
    /// <remarks>Runs inside the caller's transaction: it takes the open context rather than opening
    /// its own, because a second connection inside that transaction would deadlock on the same
    /// file. A conflicting value stays on the loser (user choice 21, and the web's own rule at
    /// <c>target_merge.py</c> lines 77 to 99), which is what makes the winner's own value never
    /// overwritten and what makes the unmerge exact. The slot is not the four-part key:
    /// <c>target_id</c> is what is changing, and a mosaic-scope value has a null <c>target_id</c>,
    /// so it never moves (spec 12.15: it names no target).</remarks>
    public static IReadOnlyList<Guid> MoveValuesOnMerge(
        GalactiLogContext context, Guid winnerId, Guid loserId)
    {
        var winnerSlots = context.CustomColumnValues
            .Where(value => value.TargetId == winnerId)
            .Select(value => new { value.ColumnId, value.SessionDate, value.RigLabel })
            .ToList()
            .Select(slot => (slot.ColumnId, slot.SessionDate, slot.RigLabel))
            .ToHashSet();

        var moved = new List<Guid>();
        foreach (var value in context.CustomColumnValues
            .Where(row => row.TargetId == loserId)
            .OrderBy(row => row.Id)
            .ToList())
        {
            if (winnerSlots.Contains((value.ColumnId, value.SessionDate, value.RigLabel)))
            {
                continue;
            }

            // Re-keyed on the tracked entity rather than through ExecuteUpdate: the conflict test
            // needs the loser rows in memory anyway, and ExecuteUpdate issues before SaveChanges
            // flushes, which is the ordering caveat MergeRepository.Merge already records.
            value.TargetId = winnerId;
            moved.Add(value.Id);
        }

        return moved;
    }

    /// <summary>Reverses <see cref="MoveValuesOnMerge"/> from the manifest's recorded ids, and only
    /// from those: the winner may have acquired its own value in the same slot since the merge, so
    /// deriving the set from the loser's current values would be wrong. An id whose row no longer
    /// exists is skipped, which is what a column deleted since the merge leaves behind. Same
    /// transaction rule as <see cref="MoveValuesOnMerge"/>.</summary>
    public static int RestoreValuesOnUnmerge(
        GalactiLogContext context, IReadOnlyList<Guid> movedValueIds, Guid loserId)
    {
        if (movedValueIds.Count == 0)
        {
            return 0;
        }

        var ids = movedValueIds.ToList();
        var rows = context.CustomColumnValues.Where(value => ids.Contains(value.Id)).ToList();
        foreach (var row in rows)
        {
            // The loser's slot is free by construction: the merge only moved slots the winner did
            // not hold, so a restore can never collide with a value the winner acquired since.
            row.TargetId = loserId;
        }

        return rows.Count;
    }

    // ---- the pieces the methods share --------------------------------------------------

    /// <summary>Whether a submitted list really carries options. A list of empty or whitespace-only
    /// entries is not one, which is the same reading <see cref="NormalizeOptions"/> gives it, so the
    /// empty list a surface sends when it renames a column that is not a dropdown still renames it.
    /// </summary>
    private static bool HasOptions(IReadOnlyList<string> options)
        => options.Any(option => !string.IsNullOrWhiteSpace(option));

    /// <summary>The four scopes spec 12.15 offers. A stored word outside them parses to null and
    /// is refused before this is asked.</summary>
    private static bool IsOffered(CustomColumnScope scope)
        => scope is CustomColumnScope.Target or CustomColumnScope.Session or CustomColumnScope.Rig or CustomColumnScope.Mosaic;

    /// <summary>Spec 5.20's key table, read as a rule and failing closed: a part the scope does not
    /// name must be null, and a part it names must be set. The mosaic scope names the mosaic id
    /// alone; the other three name the target and never a mosaic. A rig label is judged set or
    /// unset and is never trimmed into shape: the label is composed by <c>SessionDetailQuery</c>,
    /// and a caller that sends a blank one has a defect of its own.</summary>
    private static bool KeyMatchesScope(CustomColumnScope scope, CustomValueKey key)
    {
        if (scope == CustomColumnScope.Mosaic)
        {
            return key.MosaicId is not null && key.TargetId is null && key.SessionDate is null && key.RigLabel is null;
        }

        if (key.MosaicId is not null || key.TargetId is null)
        {
            return false;
        }

        var hasNight = key.SessionDate is not null;
        var hasRig = !string.IsNullOrWhiteSpace(key.RigLabel);
        var noRig = key.RigLabel is null;

        return scope switch
        {
            CustomColumnScope.Target => !hasNight && noRig,
            CustomColumnScope.Session => hasNight && noRig,
            CustomColumnScope.Rig => hasNight && hasRig,
            _ => false,
        };
    }

    /// <summary>One <c>custom_columns</c> row as every surface reads it, or null when its stored
    /// <c>column_type</c> or <c>applies_to</c> word is outside the set. A null is a hand-edited row
    /// the reader skips rather than throws on.</summary>
    private static CustomColumnDefinition? Describe(CustomColumn row, int valueCount)
    {
        if (CustomColumnSlug.ParseType(row.ColumnType) is not { } type
            || CustomColumnSlug.ParseScope(row.AppliesTo) is not { } scope)
        {
            return null;
        }

        return new CustomColumnDefinition(
            row.Id, row.Name, row.Slug, type, scope, ParseOptions(row.DropdownOptions),
            row.DisplayOrder, row.CreatedAt, valueCount);
    }

    /// <summary>The stored options document. A null, an empty string and a malformed document all
    /// read as an empty list: spec 5.8.2's "a hand-edited document still opens" rule applies to
    /// this column too, so nothing is logged and nothing throws.</summary>
    private static IReadOnlyList<string> ParseOptions(string? document)
    {
        if (string.IsNullOrWhiteSpace(document))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(document) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The submitted options trimmed, with empties dropped, in the user's own order, plus
    /// the second of the first pair that is equal after trimming compared
    /// <see cref="StringComparison.OrdinalIgnoreCase"/>, in the caller's own spelling.</summary>
    /// <remarks>The whole list is normalized whether or not a duplicate was found, because the
    /// caller tests the empty list (refusal step 5) before the duplicate (step 6) and a list
    /// carrying a duplicate is not an empty one.</remarks>
    private static (List<string> Options, string? Duplicate) NormalizeOptions(IReadOnlyList<string> options)
    {
        var kept = new List<string>(options.Count);
        string? duplicate = null;

        foreach (var submitted in options)
        {
            var option = submitted.Trim();
            if (option.Length == 0)
            {
                continue;
            }

            if (kept.Contains(option, StringComparer.OrdinalIgnoreCase))
            {
                duplicate ??= option;
                continue;
            }

            kept.Add(option);
        }

        return (kept, duplicate);
    }

    /// <summary>One rig label rewritten through both maps. The spelling is
    /// <c>SessionDetailQuery</c>'s own, <c>"{telescope} / {camera}"</c> over the canonical names
    /// (line 418), the literal "Unknown" included: a rename never produces or consumes it. A label
    /// carrying no separator is left exactly as it is.</summary>
    private static string RewriteLabel(
        string label,
        IReadOnlyDictionary<string, string> telescopeRenames,
        IReadOnlyDictionary<string, string> cameraRenames)
    {
        const string Separator = " / ";

        // The first occurrence. A canonical equipment name that itself contains " / " is already
        // ambiguous in the label the query composes, so nothing here can resolve it and nothing
        // here makes it worse.
        var split = label.IndexOf(Separator, StringComparison.Ordinal);
        if (split < 0)
        {
            return label;
        }

        var telescope = label[..split];
        var camera = label[(split + Separator.Length)..];

        if (telescopeRenames.TryGetValue(telescope, out var renamedTelescope))
        {
            telescope = renamedTelescope;
        }

        if (cameraRenames.TryGetValue(camera, out var renamedCamera))
        {
            camera = renamedCamera;
        }

        return telescope + Separator + camera;
    }

    /// <summary>Materializes first and composes the key in memory: <c>CustomValueKey</c> is the
    /// caller's shape, not a column set, and nothing about it belongs in the command text.</summary>
    private static IReadOnlyList<CustomValueRow> Read(IQueryable<CustomColumnValue> rows)
        => [.. rows.ToList().Select(value => new CustomValueRow(
            value.ColumnId,
            new CustomValueKey(value.TargetId, value.MosaicId, value.SessionDate, value.RigLabel),
            value.Value))];

    private static CustomWriteResult Written()
        => new(CustomWriteStatus.Written, Message: null);

    private static CustomWriteResult Refused(CustomWriteStatus status, string message)
        => new(status, message);

    /// <summary>A programming error rather than a user path, so it carries no sentence: no surface
    /// shows it.</summary>
    private static CustomWriteResult ColumnNotFound()
        => new(CustomWriteStatus.ColumnNotFound, Message: null);

    /// <summary>Also a programming error and also without a sentence: a surface that builds its key
    /// through the four factories cannot reach it.</summary>
    private static CustomWriteResult KeyDoesNotMatchScope()
        => new(CustomWriteStatus.KeyDoesNotMatchScope, Message: null);

    private GalactiLogContext Open()
        => new(GalactiLogContextOptions.Create(connectionString.Value, tracking: true));
}
