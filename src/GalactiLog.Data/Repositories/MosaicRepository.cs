using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using GalactiLog.Core.Mosaics;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.Data.Repositories;

/// <summary>Spec 12.17's refusal sentences, composed once. The repository is the only place that
/// builds them; a surface shows the exception's message verbatim.</summary>
public static class MosaicMessages
{
    public const string EmptyName = "Enter a name for the mosaic.";
    public const string EmptyLabel = "Enter a panel label.";
    public const string NoPanelChecked = "Select at least one panel to accept.";

    /// <summary>What a surface shows when a write threw rather than being refused.</summary>
    public const string CouldNotSave = "The change could not be saved. Try again.";

    public static string DuplicateName(string name) => $"A mosaic named \"{name}\" already exists.";

    public static string DuplicateLabel(string label) => $"A panel named \"{label}\" already exists in this mosaic.";

    public static string TripleInPanel(DateOnly night, string target, string? frameLabel, string panelLabel)
        => $"{night.ToString(SqlReaders.DateFormat, CultureInfo.InvariantCulture)} of {target} ({frameLabel ?? "no label"}) is already in panel {panelLabel}.";
}

/// <summary>A refusal of spec 12.17, carrying its sentence as the message. Every mosaic write
/// refusal derives from it, so a surface catches one type.</summary>
public class MosaicWriteException(string message) : Exception(message);

/// <summary>A create or rename whose name another mosaic carries, compared case insensitively.</summary>
public sealed class DuplicateMosaicNameException(string name) : MosaicWriteException(MosaicMessages.DuplicateName(name));

/// <summary>A panel label another panel of the same mosaic carries, compared case insensitively.</summary>
public sealed class DuplicatePanelLabelException(string label) : MosaicWriteException(MosaicMessages.DuplicateLabel(label));

/// <summary>A panel delete while the panel still has an included night (spec 12.17).</summary>
public sealed class PanelNotEmptyException() : MosaicWriteException("Remove its included nights first.");

/// <summary>An include of a (target, night, frame label) triple that an included row of another
/// panel of the same mosaic holds (spec 5.24's one-triple rule, ruling R19a).</summary>
public sealed class NightAlreadyInMosaicException(DateOnly night, string target, string? frameLabel, string panelLabel)
    : MosaicWriteException(MosaicMessages.TripleInPanel(night, target, frameLabel, panelLabel));

/// <summary>One pending suggestion as the Mosaics page lists it (spec 5.25), its JSON columns
/// parsed. <see cref="Panels"/> is the authoritative entry list of <c>session_dates</c>.</summary>
public sealed record MosaicSuggestionRow(
    Guid Id,
    string SuggestedName,
    string BaseName,
    IReadOnlyList<SuggestionPanel> Panels,
    string Confidence,
    string DiscoverySource,
    SuggestionGeometry? Geometry,
    IReadOnlyList<string> Flags,
    string DedupSignature,
    DateTime CreatedAt);

/// <summary>What the add panel form wrote (spec 12.17): the panel, the triples it included and
/// the triples it skipped because another panel of the mosaic includes them.</summary>
public sealed record PanelAddResult(Guid PanelId, int Included, int Skipped);

/// <summary>
/// Spec 12.17's one mosaic write path, and the one place the one-triple-per-mosaic rule of spec
/// 5.24 is checked: every write that sets a row <c>included</c> passes through
/// <see cref="IncludeCore"/> inside its own transaction (design lesson 2).
/// </summary>
/// <remarks>
/// Takes the DI <see cref="DatabaseConnectionString"/> and opens a short-lived tracking context
/// per call, as <see cref="CustomColumnRepository"/> does; each write runs in one explicit
/// transaction, so a refusal part way through writes nothing. Synchronous, as every repository in
/// this namespace is. Refusals throw a <see cref="MosaicWriteException"/> carrying the spec's
/// sentence; an unknown id throws <see cref="KeyNotFoundException"/>, a stale page rather than a
/// user path.
/// </remarks>
public sealed class MosaicRepository(DatabaseConnectionString connectionString)
{
    private static readonly int[] Rotations = [0, 90, 180, 270];

    // ---- mosaics -------------------------------------------------------------------------

    /// <summary>Creates an empty mosaic with the trimmed name.</summary>
    public Guid Create(string name)
        => Write(context => CreateCore(context, name).Id);

    /// <summary>Renames a mosaic; refuses a blank name or one another mosaic carries.</summary>
    public void Rename(Guid id, string name)
        => Write(context =>
        {
            var mosaic = FindMosaic(context, id);
            var trimmed = CheckName(context, name, except: id);
            mosaic.Name = trimmed;
            Touch(mosaic);
            return 0;
        });

    /// <summary>The detail page's notes autosave; a blank box stores null (spec 5.22).</summary>
    public void SetNotes(Guid id, string? notes)
        => Write(context =>
        {
            var mosaic = FindMosaic(context, id);
            mosaic.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes;
            Touch(mosaic);
            return 0;
        });

    /// <summary>Deletes a mosaic; the cascades take its panels, their nights and its mosaic-scope
    /// custom values. No suggestion and no frame is touched (spec 5.22).</summary>
    public void Delete(Guid id) => DeleteMany([id]);

    /// <summary>Deletes every listed mosaic that exists and returns how many that was.</summary>
    public int DeleteMany(IReadOnlyList<Guid> ids)
        => Write(context =>
        {
            var list = ids.ToList();
            var rows = context.Mosaics.Where(mosaic => list.Contains(mosaic.Id)).ToList();
            context.Mosaics.RemoveRange(rows);
            return rows.Count;
        });

    /// <summary>Every mosaic name, compared case insensitively, for detection's unique naming
    /// (spec 7.7).</summary>
    public IReadOnlySet<string> ExistingNames()
    {
        using var context = Open();
        return context.Mosaics.Select(mosaic => mosaic.Name).AsEnumerable().ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // ---- panels --------------------------------------------------------------------------

    /// <summary>Adds an empty panel at the end of <c>sort_order</c>.</summary>
    public Guid AddPanel(Guid mosaicId, string label)
        => Write(context => AddPanelCore(context, FindMosaic(context, mosaicId), label).Id);

    /// <summary>Renames a panel; its rows keep their frame labels (spec 5.24).</summary>
    public void RenamePanel(Guid panelId, string label)
        => Write(context =>
        {
            var panel = FindPanel(context, panelId);
            var trimmed = CheckLabel(context, panel.MosaicId, label, except: panelId);
            panel.PanelLabel = trimmed;
            Touch(FindMosaic(context, panel.MosaicId));
            return 0;
        });

    /// <summary>Deletes a panel and its available rows; refused while it has an included night.</summary>
    public void DeletePanel(Guid panelId)
        => Write(context =>
        {
            var panel = FindPanel(context, panelId);
            if (context.MosaicPanelSessions.Any(row => row.PanelId == panelId && row.Status == MosaicPanelSession.Included))
            {
                throw new PanelNotEmptyException();
            }

            context.MosaicPanels.Remove(panel);
            Touch(FindMosaic(context, panel.MosaicId));
            return 0;
        });

    /// <summary>Spec 12.17's add panel form: the panel of that label, created at the end when the
    /// mosaic has none, gains the target's triples as <c>included</c> rows. When any LIGHT frame
    /// of the target carries the label (compared case insensitively) only those pairs are taken;
    /// otherwise every (night, frame label) pair of the target, the null label included. A triple
    /// another panel of the mosaic includes is skipped and counted.</summary>
    public PanelAddResult AddPanelWithTarget(Guid mosaicId, Guid targetId, string label)
        => Write(context =>
        {
            var mosaic = FindMosaic(context, mosaicId);
            var trimmed = RequireLabel(label);
            var panel = context.MosaicPanels.Where(row => row.MosaicId == mosaicId).AsEnumerable()
                .FirstOrDefault(row => SameLabel(row.PanelLabel, trimmed))
                ?? AddPanelCore(context, mosaic, trimmed);

            var triples = MosaicFrames.Triples(MosaicFrames.Buckets(context, [targetId]));
            var labelled = triples.Where(triple => SameLabel(triple.Label, trimmed)).ToList();
            var taken = labelled.Count > 0 ? labelled : triples;

            var included = MosaicFrames.IncludedTriples(context, mosaicId);
            var added = 0;
            var skipped = 0;
            foreach (var (target, date, frameLabel) in taken)
            {
                if (included.TryGetValue(TripleKey.Of(target, date, frameLabel), out var holder) && holder.Id != panel.Id)
                {
                    skipped++;
                    continue;
                }

                IncludeCore(context, panel, target, date, frameLabel);
                added++;
            }

            return new PanelAddResult(panel.Id, added, skipped);
        });

    // ---- nights --------------------------------------------------------------------------

    /// <summary>Inserts the triple as <c>included</c>, or flips its <c>available</c> row; refused
    /// when another panel of the mosaic includes the triple.</summary>
    public void IncludeNight(Guid panelId, Guid targetId, DateOnly date, string? frameLabel)
        => Write(context =>
        {
            IncludeCore(context, FindPanel(context, panelId), targetId, date, frameLabel);
            return 0;
        });

    /// <summary>Turns the triple's row <c>available</c>. It deletes no row, so Include undoes it
    /// (spec 12.17, the web's shape).</summary>
    public void RemoveNight(Guid panelId, Guid targetId, DateOnly date, string? frameLabel)
        => Write(context =>
        {
            var panel = FindPanel(context, panelId);
            var row = FindRow(context, panelId, targetId, date, frameLabel);
            if (row is null)
            {
                context.MosaicPanelSessions.Add(NewRow(panelId, targetId, date, frameLabel, MosaicPanelSession.Available));
            }
            else
            {
                row.Status = MosaicPanelSession.Available;
            }

            Touch(FindMosaic(context, panel.MosaicId));
            return 0;
        });

    /// <summary>Includes every Available triple of the panel (spec 12.17) and returns the count.</summary>
    public int IncludeAll(Guid panelId)
        => Write(context => IncludeAllCore(context, FindPanel(context, panelId)));

    /// <summary>Includes every Available triple of every panel, panels in <c>sort_order</c>, so a
    /// triple available in two panels goes to the first.</summary>
    public int IncludeAllAvailable(Guid mosaicId)
        => Write(context =>
        {
            FindMosaic(context, mosaicId);
            var total = 0;
            foreach (var panel in context.MosaicPanels.Where(row => row.MosaicId == mosaicId).OrderBy(row => row.SortOrder).ToList())
            {
                total += IncludeAllCore(context, panel);
            }

            return total;
        });

    /// <summary>Spec 12.17's As new panel: a panel of <paramref name="label"/> at the end of
    /// <c>sort_order</c> that includes the triple, its frame label unchanged.</summary>
    public Guid IncludeAsNewPanel(Guid mosaicId, Guid fromPanelId, Guid targetId, DateOnly date, string? frameLabel, string label)
        => Write(context =>
        {
            var mosaic = FindMosaic(context, mosaicId);
            if (FindPanel(context, fromPanelId).MosaicId != mosaicId)
            {
                throw new KeyNotFoundException($"Panel {fromPanelId} is not in mosaic {mosaicId}.");
            }

            var panel = AddPanelCore(context, mosaic, label);
            IncludeCore(context, panel, targetId, date, frameLabel);
            return panel.Id;
        });

    /// <summary>Spec 12.17's Add nights from any target: an <c>available</c> row on the panel for
    /// each (night, frame label) pair of the target's LIGHT frames whose triple no panel of the
    /// mosaic includes and the panel does not already hold. Returns the rows added.</summary>
    public int AddTargetNights(Guid panelId, Guid targetId)
        => Write(context =>
        {
            var panel = FindPanel(context, panelId);
            var included = MosaicFrames.IncludedTriples(context, panel.MosaicId);
            var held = context.MosaicPanelSessions.Where(row => row.PanelId == panelId && row.TargetId == targetId)
                .AsEnumerable()
                .Select(row => TripleKey.Of(row.TargetId, row.SessionDate, row.FrameLabel))
                .ToHashSet();

            var added = 0;
            foreach (var (target, date, label) in MosaicFrames.Triples(MosaicFrames.Buckets(context, [targetId])))
            {
                var key = TripleKey.Of(target, date, label);
                if (included.ContainsKey(key) || !held.Add(key))
                {
                    continue;
                }

                context.MosaicPanelSessions.Add(NewRow(panelId, target, date, label, MosaicPanelSession.Available));
                added++;
            }

            Touch(FindMosaic(context, panel.MosaicId));
            return added;
        });

    /// <summary>The Create mosaic dialog's write (spec 12.17), in one transaction: a new mosaic
    /// named <paramref name="newName"/>, or the existing one. Rows are grouped by trimmed panel
    /// label, case insensitively; each group goes into the mosaic's panel of that label or a new
    /// one at the end, and each row is an <c>included</c> row of the target, its night and its
    /// own frame label. Any refusal writes nothing.</summary>
    public Guid CreateFromNights(
        string? newName, Guid? existingMosaicId, Guid targetId,
        IReadOnlyList<(DateOnly Date, string? FrameLabel, string Label)> nights)
        => Write(context =>
        {
            foreach (var night in nights)
            {
                RequireLabel(night.Label);
            }

            var mosaic = existingMosaicId is { } existing
                ? FindMosaic(context, existing)
                : CreateCore(context, newName ?? "");

            foreach (var group in nights.GroupBy(night => night.Label.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                var panel = context.MosaicPanels.Where(row => row.MosaicId == mosaic.Id).AsEnumerable()
                    .FirstOrDefault(row => SameLabel(row.PanelLabel, group.Key))
                    ?? AddPanelCore(context, mosaic, group.Key);
                foreach (var night in group)
                {
                    IncludeCore(context, panel, targetId, night.Date, night.FrameLabel);
                }
            }

            return mosaic.Id;
        });

    // ---- suggestions ---------------------------------------------------------------------

    /// <summary>Spec 12.17's suggestion list: every <c>pending</c> row whose name no mosaic
    /// carries, compared case insensitively, ordered by suggested name, ordinal and case
    /// insensitive.</summary>
    public IReadOnlyList<MosaicSuggestionRow> ListPending()
    {
        using var context = Open();
        var names = context.Mosaics.Select(mosaic => mosaic.Name).AsEnumerable().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. context.MosaicSuggestions
            .Where(row => row.Status == MosaicSuggestion.Pending)
            .AsEnumerable()
            .Where(row => !names.Contains(row.SuggestedName))
            .OrderBy(row => row.SuggestedName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.SuggestedName, StringComparer.Ordinal)
            .Select(Describe)];
    }

    /// <summary>Spec 7.7's accept: in one transaction, the mosaic with the suggested name, one
    /// panel per distinct checked label in entry order, an <c>included</c> row for each date of
    /// each checked entry and an <c>available</c> row for each other night of that target whose
    /// LIGHT frames carry the label, then the suggestion set <c>accepted</c>. Where two entries
    /// would include the same triple, the first takes it and the later one is offered.</summary>
    public Guid Accept(Guid suggestionId, IReadOnlyList<string> checkedLabels)
        => Write(context =>
        {
            // Only a pending row can be accepted: an accepted or dismissed one is a stale page, and
            // answers as an unknown id does.
            var suggestion = context.MosaicSuggestions
                .SingleOrDefault(row => row.Id == suggestionId && row.Status == MosaicSuggestion.Pending)
                ?? throw new KeyNotFoundException($"Suggestion {suggestionId} does not exist or is not pending.");
            var entries = ParseEntries(suggestion.SessionDates)
                .Where(entry => checkedLabels.Any(label => SameLabel(label.Trim(), entry.Label)))
                .ToList();
            if (entries.Count == 0)
            {
                throw new MosaicWriteException(MosaicMessages.NoPanelChecked);
            }

            // A target merged away (or deleted) since detection has no frames of its own any more:
            // its entries are skipped rather than written as rows that join nothing.
            var entryTargets = entries.Select(entry => entry.TargetId).Distinct().ToList();
            var live = context.Targets
                .Where(target => entryTargets.Contains(target.Id) && target.MergedIntoId == null)
                .Select(target => target.Id)
                .ToHashSet();
            entries = [.. entries.Where(entry => live.Contains(entry.TargetId))];
            if (entries.Count == 0)
            {
                throw new KeyNotFoundException($"Suggestion {suggestionId} names no active target.");
            }

            var mosaic = CreateCore(context, suggestion.SuggestedName);
            var panels = new List<MosaicPanel>();
            foreach (var entry in entries)
            {
                if (!panels.Any(panel => SameLabel(panel.PanelLabel, entry.Label)))
                {
                    panels.Add(AddPanelCore(context, mosaic, entry.Label));
                }
            }

            var buckets = MosaicFrames.Buckets(context, entries.Select(entry => entry.TargetId));
            foreach (var entry in entries)
            {
                var panel = panels.First(row => SameLabel(row.PanelLabel, entry.Label));
                foreach (var date in entry.Dates)
                {
                    var included = MosaicFrames.IncludedTriples(context, mosaic.Id);
                    if (included.TryGetValue(TripleKey.Of(entry.TargetId, date, panel.PanelLabel), out var holder) && holder.Id != panel.Id)
                    {
                        Offer(context, panel, entry.TargetId, date, panel.PanelLabel);
                    }
                    else
                    {
                        IncludeCore(context, panel, entry.TargetId, date, panel.PanelLabel);
                    }
                }

                var otherNights = buckets
                    .Where(bucket => bucket.TargetId == entry.TargetId && SameLabel(bucket.Label, entry.Label))
                    .Select(bucket => bucket.Date)
                    .Distinct()
                    .Where(date => !entry.Dates.Contains(date))
                    .Order();
                foreach (var date in otherNights)
                {
                    Offer(context, panel, entry.TargetId, date, panel.PanelLabel);
                }
            }

            suggestion.Status = MosaicSuggestion.Accepted;
            return mosaic.Id;
        });

    /// <summary>Spec 7.7's dismiss: the row turns <c>rejected</c> and keeps its signature and
    /// nights, which the dismissed-subset rule reads.</summary>
    public void Dismiss(Guid suggestionId)
        => Write(context =>
        {
            var suggestion = context.MosaicSuggestions.SingleOrDefault(row => row.Id == suggestionId)
                ?? throw new KeyNotFoundException($"Suggestion {suggestionId} does not exist.");
            suggestion.Status = MosaicSuggestion.Rejected;
            return 0;
        });

    /// <summary>Spec 7.7's write, in one transaction: every <c>pending</c> row deleted and the new
    /// set inserted, in the order given. <c>accepted</c> and <c>rejected</c> rows are untouched.</summary>
    public void ReplacePending(IReadOnlyList<SuggestionCandidate> candidates)
        => Write(context =>
        {
            context.MosaicSuggestions.Where(row => row.Status == MosaicSuggestion.Pending).ExecuteDelete();
            var now = DateTime.UtcNow;
            foreach (var candidate in candidates)
            {
                context.MosaicSuggestions.Add(new MosaicSuggestion
                {
                    Id = Guid.NewGuid(),
                    SuggestedName = candidate.SuggestedName,
                    BaseName = candidate.BaseName,
                    TargetIds = JsonSerializer.Serialize(candidate.TargetIds),
                    PanelLabels = JsonSerializer.Serialize(candidate.PanelLabels),
                    PanelPatterns = JsonSerializer.Serialize(candidate.Panels.Select(panel => panel.Pattern)),
                    SessionDates = JsonSerializer.Serialize(candidate.Panels.Select(panel => new EntryDocument(
                        panel.TargetId, panel.Label, panel.Pattern,
                        [.. panel.Dates.Select(date => date.ToString(SqlReaders.DateFormat, CultureInfo.InvariantCulture))]))),
                    Status = MosaicSuggestion.Pending,
                    CreatedAt = now,
                    Confidence = candidate.Confidence,
                    DiscoverySource = candidate.DiscoverySource,
                    Geometry = JsonSerializer.Serialize(candidate.Geometry),
                    Flags = JsonSerializer.Serialize(candidate.Flags),
                    DedupSignature = candidate.DedupSignature,
                });
            }

            return 0;
        });

    /// <summary>Every <c>rejected</c> signature with the union of its nights; several dismissed
    /// rows sharing a signature pool their nights (spec 7.7 step 1).</summary>
    public IReadOnlyList<DismissedSignature> DismissedSignatures()
    {
        using var context = Open();
        return [.. context.MosaicSuggestions
            .Where(row => row.Status == MosaicSuggestion.Rejected)
            .Select(row => new { row.DedupSignature, row.SessionDates })
            .AsEnumerable()
            .GroupBy(row => row.DedupSignature, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new DismissedSignature(
                group.Key,
                group.SelectMany(row => ParseEntries(row.SessionDates)).SelectMany(entry => entry.Dates).ToHashSet()))];
    }

    /// <summary>Spec 7.7 step 3's covered triples: every (target, night, frame label) an
    /// <c>included</c> row holds in any mosaic, the label compared case insensitively. A null-label
    /// row is left out, because no suggestion entry carries a null label.</summary>
    public IReadOnlySet<(Guid TargetId, DateOnly Night, string Label)> CoveredTriples()
    {
        using var context = Open();
        var set = new HashSet<(Guid TargetId, DateOnly Night, string Label)>(CoveredComparer.Instance);
        foreach (var row in context.MosaicPanelSessions
            .Where(row => row.Status == MosaicPanelSession.Included && row.FrameLabel != null)
            .Select(row => new { row.TargetId, row.SessionDate, row.FrameLabel }))
        {
            set.Add((row.TargetId, row.SessionDate, row.FrameLabel!));
        }

        return set;
    }

    // ---- layout --------------------------------------------------------------------------

    /// <summary>The arranger's save (Phase 19A): the global rotation and every listed panel's
    /// position, rotation and flip, in one transaction. A rotation outside 0, 90, 180 and 270, or
    /// a panel of another mosaic, refuses the whole call.</summary>
    public void UpdateLayout(
        Guid mosaicId, double rotationAngle,
        IReadOnlyList<(Guid PanelId, double? X, double? Y, int Rotation, bool FlipH)> panels)
        => Write(context =>
        {
            var mosaic = FindMosaic(context, mosaicId);
            mosaic.RotationAngle = rotationAngle;
            foreach (var layout in panels)
            {
                if (Array.IndexOf(Rotations, layout.Rotation) < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(panels), layout.Rotation, "A panel rotation is 0, 90, 180 or 270.");
                }

                var panel = FindPanel(context, layout.PanelId);
                if (panel.MosaicId != mosaicId)
                {
                    throw new KeyNotFoundException($"Panel {layout.PanelId} is not in mosaic {mosaicId}.");
                }

                (panel.CanvasX, panel.CanvasY, panel.Rotation, panel.FlipH) = (layout.X, layout.Y, layout.Rotation, layout.FlipH);
            }

            Touch(mosaic);
            return 0;
        });

    // ---- merge -----------------------------------------------------------------------------

    /// <summary>Spec 5.24's merge rule: every <c>mosaic_panel_sessions</c> row of the loser is
    /// rewritten to the winner, inside the merge's own transaction, because the winner is where
    /// the frames now resolve. Collisions are resolved per (mosaic, night, frame label) against
    /// the winner's rows: of two <c>included</c> rows the panel earlier in <c>sort_order</c> keeps
    /// its row and the other is dropped (in one panel the winner's stays); a moving
    /// <c>available</c> row that meets any winner row of the triple in the mosaic is dropped; a
    /// moving <c>included</c> row that meets the winner's <c>available</c> row in the same panel
    /// replaces it. Returns the rows moved. Nothing is recorded for unmerge, which does not move
    /// rows back (a stated limit).</summary>
    /// <remarks>Takes the caller's open context, as <c>CustomColumnRepository.MoveValuesOnMerge</c>
    /// does: a second connection inside that transaction would deadlock on the same file.</remarks>
    public static int MoveSessionsOnMerge(GalactiLogContext context, Guid winnerId, Guid loserId)
    {
        var rows = (from session in context.MosaicPanelSessions
                    join panel in context.MosaicPanels on session.PanelId equals panel.Id
                    where session.TargetId == winnerId || session.TargetId == loserId
                    select new { Row = session, panel.MosaicId, panel.SortOrder })
                   .ToList();

        var winners = rows.Where(row => row.Row.TargetId == winnerId).ToList();
        var dropped = new HashSet<MosaicPanelSession>();
        var moving = new List<MosaicPanelSession>();
        foreach (var loser in rows.Where(row => row.Row.TargetId == loserId)
            .OrderBy(row => row.Row.Status == MosaicPanelSession.Included ? 0 : 1)
            .ThenBy(row => row.SortOrder))
        {
            var key = Fold(loser.Row);
            var sameTriple = winners
                .Where(row => row.MosaicId == loser.MosaicId && !dropped.Contains(row.Row) && Fold(row.Row) == key)
                .ToList();

            if (loser.Row.Status == MosaicPanelSession.Available)
            {
                if (sameTriple.Count > 0)
                {
                    dropped.Add(loser.Row);
                    continue;
                }
            }
            else
            {
                var included = sameTriple.FirstOrDefault(row => row.Row.Status == MosaicPanelSession.Included);
                if (included is not null)
                {
                    if (included.SortOrder <= loser.SortOrder)
                    {
                        dropped.Add(loser.Row);
                        continue;
                    }

                    dropped.Add(included.Row);
                }

                foreach (var offered in sameTriple.Where(row =>
                    row.Row.Status == MosaicPanelSession.Available && row.Row.PanelId == loser.Row.PanelId))
                {
                    dropped.Add(offered.Row);
                }
            }

            moving.Add(loser.Row);
        }

        // The deletes are flushed first, so no moved row lands on a slot the row it replaces still
        // holds: EF does not promise to order a delete before an update of the same table.
        if (dropped.Count > 0)
        {
            context.MosaicPanelSessions.RemoveRange(dropped);
            context.SaveChanges();
        }

        foreach (var row in moving)
        {
            row.TargetId = winnerId;
        }

        // Spec 5.22: updated_at moves on every write to a mosaic's nights.
        var touched = rows.Where(row => moving.Contains(row.Row) || dropped.Contains(row.Row))
            .Select(row => row.MosaicId)
            .Distinct()
            .ToList();
        foreach (var mosaic in context.Mosaics.Where(mosaic => touched.Contains(mosaic.Id)).ToList())
        {
            Touch(mosaic);
        }

        return moving.Count;

        static TripleKey Fold(MosaicPanelSession row) => TripleKey.Of(Guid.Empty, row.SessionDate, row.FrameLabel);
    }

    // ---- the pieces the writes share -----------------------------------------------------

    /// <summary>The one-triple rule and the upsert, the single path by which a row becomes
    /// <c>included</c>. Saves at once, so the next check in the same transaction sees it.</summary>
    private static void IncludeCore(GalactiLogContext context, MosaicPanel panel, Guid targetId, DateOnly date, string? frameLabel)
    {
        var label = MosaicFrames.NormalizeLabel(frameLabel);
        var clash = (from session in context.MosaicPanelSessions
                     join other in context.MosaicPanels on session.PanelId equals other.Id
                     where other.MosaicId == panel.MosaicId
                         && other.Id != panel.Id
                         && session.TargetId == targetId
                         && session.SessionDate == date
                         && session.Status == MosaicPanelSession.Included
                     select new { session.FrameLabel, other.PanelLabel })
                    .AsEnumerable()
                    .FirstOrDefault(row => SameLabel(row.FrameLabel, label));
        if (clash is not null)
        {
            var targetName = context.Targets.Where(target => target.Id == targetId).Select(target => target.PrimaryName).SingleOrDefault()
                ?? targetId.ToString();
            throw new NightAlreadyInMosaicException(date, targetName, label, clash.PanelLabel);
        }

        var row = FindRow(context, panel.Id, targetId, date, label);
        if (row is null)
        {
            context.MosaicPanelSessions.Add(NewRow(panel.Id, targetId, date, label, MosaicPanelSession.Included));
        }
        else
        {
            row.Status = MosaicPanelSession.Included;
        }

        Touch(FindMosaic(context, panel.MosaicId));
        context.SaveChanges();
    }

    /// <summary>An <c>available</c> row for the triple unless the panel already holds one.</summary>
    private static void Offer(GalactiLogContext context, MosaicPanel panel, Guid targetId, DateOnly date, string? frameLabel)
    {
        if (FindRow(context, panel.Id, targetId, date, frameLabel) is null)
        {
            context.MosaicPanelSessions.Add(NewRow(panel.Id, targetId, date, frameLabel, MosaicPanelSession.Available));
            context.SaveChanges();
        }
    }

    private static int IncludeAllCore(GalactiLogContext context, MosaicPanel panel)
    {
        var contributors = context.MosaicPanelSessions.Where(row => row.PanelId == panel.Id)
            .Select(row => row.TargetId).Distinct().ToHashSet();
        var triples = MosaicFrames.AvailableTriples(
            MosaicFrames.Buckets(context, contributors), contributors, MosaicFrames.IncludedTriples(context, panel.MosaicId));
        foreach (var (target, date, label) in triples)
        {
            IncludeCore(context, panel, target, date, label);
        }

        return triples.Count;
    }

    private static Mosaic CreateCore(GalactiLogContext context, string name)
    {
        var now = DateTime.UtcNow;
        var mosaic = new Mosaic { Id = Guid.NewGuid(), Name = CheckName(context, name, except: null), CreatedAt = now, UpdatedAt = now };
        context.Mosaics.Add(mosaic);
        context.SaveChanges();
        return mosaic;
    }

    private static MosaicPanel AddPanelCore(GalactiLogContext context, Mosaic mosaic, string label)
    {
        var trimmed = CheckLabel(context, mosaic.Id, label, except: null);
        var last = context.MosaicPanels.Where(row => row.MosaicId == mosaic.Id).Max(row => (int?)row.SortOrder);
        var panel = new MosaicPanel { Id = Guid.NewGuid(), MosaicId = mosaic.Id, PanelLabel = trimmed, SortOrder = (last ?? -1) + 1 };
        context.MosaicPanels.Add(panel);
        Touch(mosaic);
        context.SaveChanges();
        return panel;
    }

    private static string CheckName(GalactiLogContext context, string name, Guid? except)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            throw new MosaicWriteException(MosaicMessages.EmptyName);
        }

        // NOCASE on the column makes this comparison case insensitive in SQL, which is the same
        // comparison the unique index makes.
        if (context.Mosaics.Any(mosaic => mosaic.Name == trimmed && mosaic.Id != except))
        {
            throw new DuplicateMosaicNameException(trimmed);
        }

        return trimmed;
    }

    private static string CheckLabel(GalactiLogContext context, Guid mosaicId, string label, Guid? except)
    {
        var trimmed = RequireLabel(label);
        if (context.MosaicPanels.Any(panel => panel.MosaicId == mosaicId && panel.PanelLabel == trimmed && panel.Id != except))
        {
            throw new DuplicatePanelLabelException(trimmed);
        }

        return trimmed;
    }

    private static string RequireLabel(string label)
        => label.Trim() is { Length: > 0 } trimmed ? trimmed : throw new MosaicWriteException(MosaicMessages.EmptyLabel);

    private static MosaicPanelSession? FindRow(GalactiLogContext context, Guid panelId, Guid targetId, DateOnly date, string? frameLabel)
        => context.MosaicPanelSessions
            .Where(row => row.PanelId == panelId && row.TargetId == targetId && row.SessionDate == date)
            .AsEnumerable()
            .FirstOrDefault(row => SameLabel(row.FrameLabel, frameLabel));

    private static MosaicPanelSession NewRow(Guid panelId, Guid targetId, DateOnly date, string? frameLabel, string status)
        => new()
        {
            Id = Guid.NewGuid(),
            PanelId = panelId,
            TargetId = targetId,
            SessionDate = date,
            FrameLabel = MosaicFrames.NormalizeLabel(frameLabel),
            Status = status,
        };

    private static Mosaic FindMosaic(GalactiLogContext context, Guid id)
        => context.Mosaics.SingleOrDefault(mosaic => mosaic.Id == id)
            ?? throw new KeyNotFoundException($"Mosaic {id} does not exist.");

    private static MosaicPanel FindPanel(GalactiLogContext context, Guid id)
        => context.MosaicPanels.SingleOrDefault(panel => panel.Id == id)
            ?? throw new KeyNotFoundException($"Panel {id} does not exist.");

    /// <summary>Spec 5.22: <c>updated_at</c> is rewritten on every write to the mosaic, its panels
    /// or its nights.</summary>
    private static void Touch(Mosaic mosaic) => mosaic.UpdatedAt = DateTime.UtcNow;

    private static bool SameLabel(string? a, string? b)
        => TripleKey.Fold(MosaicFrames.NormalizeLabel(a)) == TripleKey.Fold(MosaicFrames.NormalizeLabel(b));

    private static MosaicSuggestionRow Describe(MosaicSuggestion row)
        => new(
            row.Id,
            row.SuggestedName,
            row.BaseName,
            [.. ParseEntries(row.SessionDates).Select(entry => new SuggestionPanel(entry.TargetId, entry.Label, entry.Pattern, entry.Dates))],
            row.Confidence,
            row.DiscoverySource,
            ParseOrDefault<SuggestionGeometry>(row.Geometry),
            ParseOrDefault<string[]>(row.Flags) ?? [],
            row.DedupSignature,
            row.CreatedAt);

    private sealed record Entry(Guid TargetId, string Label, string Pattern, IReadOnlyList<DateOnly> Dates);

    // Spec 5.25's session_dates entry, {target_id, label, pattern, dates}.
    private sealed record EntryDocument(
        [property: JsonPropertyName("target_id")] Guid TargetId,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("pattern")] string Pattern,
        [property: JsonPropertyName("dates")] string[] Dates);

    /// <summary>A malformed or hand-edited document reads as no entries, and a date that does not
    /// parse is dropped, rather than failing the whole list.</summary>
    private static List<Entry> ParseEntries(string document)
        => [.. (ParseOrDefault<EntryDocument[]>(document) ?? [])
            .Select(entry => new Entry(
                entry.TargetId,
                entry.Label ?? "",
                entry.Pattern ?? "",
                [.. (entry.Dates ?? [])
                    .Select(text => DateOnly.TryParseExact(text, SqlReaders.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? (DateOnly?)date : null)
                    .OfType<DateOnly>()]))];

    private static T? ParseOrDefault<T>(string? document) where T : class
    {
        if (string.IsNullOrWhiteSpace(document))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(document);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The covered set's comparer: target and night exactly, the label case insensitively.</summary>
    private sealed class CoveredComparer : IEqualityComparer<(Guid TargetId, DateOnly Night, string Label)>
    {
        public static readonly CoveredComparer Instance = new();

        public bool Equals((Guid TargetId, DateOnly Night, string Label) x, (Guid TargetId, DateOnly Night, string Label) y)
            => x.TargetId == y.TargetId && x.Night == y.Night && string.Equals(x.Label, y.Label, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((Guid TargetId, DateOnly Night, string Label) obj)
            => HashCode.Combine(obj.TargetId, obj.Night, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Label));
    }

    private T Write<T>(Func<GalactiLogContext, T> body)
    {
        using var context = Open();
        using var transaction = context.Database.BeginTransaction();
        var result = body(context);
        context.SaveChanges();
        transaction.Commit();
        return result;
    }

    private GalactiLogContext Open()
        => new(GalactiLogContextOptions.Create(connectionString.Value, tracking: true));
}
