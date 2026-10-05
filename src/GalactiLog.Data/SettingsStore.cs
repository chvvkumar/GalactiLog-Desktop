using System.Text.Json;
using System.Text.Json.Nodes;
using GalactiLog.Core.Io;
using GalactiLog.Core.Phd2;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.Data;

// The one write path Settings screens (Phase 9) and AppHost (Task 5) go through: validates
// before persisting and raises the two hooks later phases subscribe to.
//
// No concurrency token: each Get*/Save* pair does a full load-modify-save of the single row.
// Per design-spec 5.1, UI-originated writes "are rare, small, and human-paced," and WAL plus
// the 5 second busy_timeout (Task 3) is what the spec already relies on for safety against a
// scan running concurrently. An optimistic-concurrency check is not asked for here; in-process
// concurrency is handled by the single gate below, which is what stops two background write
// chains from losing one another's document (Phase 6 review finding 3).
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    /// <summary>The options <see cref="ReadDisplay"/> reads with, and nothing writes with. The one
    /// difference from <see cref="JsonOptions"/> is that a stored <c>null</c> against a member this
    /// build declares non-nullable raises <see cref="JsonException"/> instead of being assigned, so
    /// the repair below can drop that member and answer its default. Without it a hand-edited
    /// <c>"target_page": null</c> throws nothing at all and hands every caller a null member, which
    /// is the shape <c>AnalysisDisplaySettingsConverter.HandleNull</c> had to answer by hand for the
    /// one member it covers. Writing still goes through <see cref="JsonOptions"/>, so no document
    /// this store writes can be refused by an annotation.</summary>
    private static readonly JsonSerializerOptions DisplayReadOptions = new() { RespectNullableAnnotations = true };
    private static readonly string[] ValidLogLevels = ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];
    private static readonly string[] ValidTextSizes = ["small", "medium", "large", "x-large"];
    private static readonly string[] ValidContentWidths = ["normal", "wide", "extra-wide"];

    // The most members ReadDisplay will drop from one stored document before it gives up and
    // answers the defaults whole. The display document has five known members today; a document
    // needing more removals than this is not a hand edit of one key, it is unreadable.
    private const int MaxUnreadableMembers = 32;

    private readonly SettingsRepository _repository;
    private readonly ILogger _logger;

    // user_settings is a single row holding six documents, and every Save* below is a whole-row
    // load-modify-save. Two independent background write chains (GraphSettingsWriter's and Task 5's
    // DisplayColumnWriter's) therefore race: each reads the row, replaces its own column, and
    // writes the whole row back, so the later save silently reverts the earlier one's document.
    // Phase 6 review finding 3: the serialisation point is here, the one place every access passes
    // through, rather than a convention each App-layer writer has to remember (design-lessons
    // rule 2). Change events are raised outside the lock, because a subscriber does file I/O.
    //
    // Every Get* takes it too, not only every Save*. SettingsRepository.Load() creates the single
    // user_settings row on first use, which is a check-then-insert: two concurrent reads on a
    // fresh database each miss the row and the second insert fails with "UNIQUE constraint failed:
    // user_settings.id". Verified by removing this lock and running
    // SaveGraph_And_SaveDisplay_Interleaved_NeitherDocumentIsLost, which fails with exactly that.
    //
    // ponytail: one process-wide lock on a path spec 5.1 calls "rare, small, and human-paced", so
    // a UI-thread read can block behind a background write for the length of one small SQLite
    // transaction. If settings access ever becomes hot, the upgrade is a per-document column or an
    // optimistic version check on the row, not a finer-grained lock.
    private readonly Lock _writeGate = new();

    // Raised after every successful SaveGeneral. Task 5's AppHost subscribes to update the
    // Serilog LoggingLevelSwitch from general.log_level with no restart (design-spec 16.1).
    public event EventHandler<GeneralSettings>? GeneralChanged;

    // Raised after every successful SaveFilters/SaveEquipment. Phase 5's AliasMap subscribes
    // to invalidate its cache (design-spec 12.7: "invalidates the alias map cache on any
    // change to filters or equipment"). The 30 second TTL backstop belongs to AliasMap itself
    // (Phase 5); this store provides only the hook.
    public event EventHandler? AliasSourcesChanged;

    /// <summary>
    /// Raised after every successful <see cref="SaveDisplay"/>, outside the write gate, exactly as
    /// <see cref="GeneralChanged"/> is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Phase 9 FIXER item 7. Phase 6 reads <c>display.groups</c> once, by value, and passes it into
    /// each frame table (ruling Q13), which was correct while nothing in the process could write
    /// it. Phase 9 Task 6's Display tab is the first writer, so without this a metric group turned
    /// off would not hide its columns until the next start.
    /// </para>
    /// <para>
    /// <c>DisplayColumnWriter</c> also calls <see cref="SaveDisplay"/>, so one column click now
    /// raises two notifications: that writer's own <c>Changed</c>, which carries one table's column
    /// list, and this one, which carries the whole document. That is deliberate and it is
    /// design-spec 5.8.2's own split. A subscriber to both must take only the <c>groups</c> half
    /// from this event and only the <c>columns</c> half from <c>DisplayColumnWriter.Changed</c>, or
    /// it will apply one click twice.
    /// </para>
    /// </remarks>
    public event EventHandler<DisplaySettings>? DisplayChanged;

    /// <summary>
    /// Raised, at most once per save and outside the write gate, when a general save moved any
    /// input of spec 7.6's zone and site resolution order: the canonical form of
    /// <c>general.phd2_profile_map</c>, <c>observer_timezone</c>, <c>observer_latitude</c> or
    /// <c>observer_longitude</c>. Phase 15A Task 6 subscribes it in <c>AppHost</c> to queue the
    /// correlation re-run of spec 7.6 and 12.7 as a registered job.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is <b>not</b> a profile map event. Spec 7.6's resolution order falls through from the
    /// profile's own value to the observer value, so a user who sets the observer timezone on the
    /// Location tab and maps no profile has changed exactly the same thing as a user who set a
    /// zone on one profile row, and must get the same re-run.
    /// </para>
    /// <para>
    /// It carries nothing: the subscriber re-reads the settings anyway, and the store must not
    /// learn about the job registry (spec 12.7). It is raised after <see cref="GeneralChanged"/>,
    /// from both writers of the general document, because this is the choke point every writer
    /// already passes through and a hook in the Settings panel would be one a future writer could
    /// forget (design-lessons rule 2).
    /// </para>
    /// <para>
    /// The map half of the test is <see cref="Phd2Profiles.SameMap"/> over
    /// <see cref="Phd2Profiles.Normalize"/> of both sides, never <c>!=</c> or <c>Equals</c>:
    /// <c>Normalize</c> returns a freshly allocated dictionary on every call, so a reference
    /// comparison reports a change on every general save whatever the contents, and spec 12.7's
    /// own sentence is that "a save that changes the map's canonical form in no way, which is what
    /// re-saving an unchanged row does, queues nothing". The cost is one normalisation of each
    /// side per general save, on a path spec 5.1 calls rare, small and human-paced.
    /// </para>
    /// </remarks>
    public event EventHandler? Phd2GuidingInputsChanged;

    /// <param name="repository">The single <c>user_settings</c> row.</param>
    /// <param name="logger">Optional, and the only thing this store logs is a stored document it
    /// could not read in full (<see cref="GetDisplay"/>). Non-generic <see cref="ILogger"/> and
    /// trailing, so no call site has to change, which is the shape <c>DuplicateDetector</c> and
    /// <c>FrameHeadersQuery</c> already have in this project.</param>
    public SettingsStore(SettingsRepository repository, ILogger? logger = null)
    {
        _repository = repository;
        _logger = logger ?? NullLogger.Instance;
    }

    public GeneralSettings GetGeneral()
    {
        lock (_writeGate) { return ClampGeneral(Deserialize<GeneralSettings>(_repository.Load().General)); }
    }

    public void SaveGeneral(GeneralSettings value)
    {
        ValidateGeneral(value);
        bool guidingInputsMoved;
        lock (_writeGate)
        {
            var row = _repository.Load();
            // Clamped, like MutateGeneral's side (review P3-3). The clamp provably reaches none of
            // the four guiding inputs today, so the two writers already answer the same question;
            // reading the stored side the same way in both is what keeps that true the day a clamp
            // is added to a coordinate.
            var stored = ClampGeneral(Deserialize<GeneralSettings>(row.General));
            guidingInputsMoved = GuidingInputsMoved(stored, value);
            value = WithCorrelationPending(value, stored, guidingInputsMoved);
            row.General = Serialize(value);
            _repository.Save(row);
        }
        GeneralChanged?.Invoke(this, value);
        if (guidingInputsMoved)
        {
            Phd2GuidingInputsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Reads, mutates, validates and writes the <c>general</c> document in one critical section,
    /// and returns what was written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Phase 9 Task 5 review escalation. <see cref="GetGeneral"/> and <see cref="SaveGeneral"/>
    /// each take the gate separately, so a caller that reads, modifies and writes holds nothing
    /// across the window between them: two such callers can lose one another's update even though
    /// every individual access is serialised. The gate above serialises writers of <em>different</em>
    /// documents; it does not make a caller's load-modify-save atomic. This method does, and it is
    /// one method on the store rather than a convention every Settings tab has to remember
    /// (design-lessons rule 2).
    /// </para>
    /// <para>
    /// Every App-layer writer of the general document uses this: the Settings Library tab (Task 5),
    /// the Location, Display and Storage tabs (Task 6), and the setup wizard (Task 9).
    /// <see cref="SaveGeneral"/> stays for a caller that genuinely has a whole document to write
    /// and nothing to merge it with.
    /// </para>
    /// <para>
    /// <paramref name="mutate"/> runs inside the gate, so it must be pure and fast: no I/O, no
    /// dispatcher post, and no call back into this store. Validation runs inside the gate too, so a
    /// refused document writes nothing at all. <see cref="GeneralChanged"/> is raised after the gate
    /// is released, like <see cref="SaveGeneral"/>'s, because a subscriber does file I/O.
    /// </para>
    /// </remarks>
    /// <param name="mutate">Receives the stored document and returns the one to write.</param>
    /// <returns>The document that was written, which is also what <see cref="GeneralChanged"/>
    /// carried.</returns>
    /// <exception cref="SettingsValidationException">The mutated document is invalid. Nothing was
    /// written and no event was raised.</exception>
    public GeneralSettings MutateGeneral(Func<GeneralSettings, GeneralSettings> mutate)
    {
        GeneralSettings next;
        bool guidingInputsMoved;
        lock (_writeGate)
        {
            var row = _repository.Load();
            var stored = ClampGeneral(Deserialize<GeneralSettings>(row.General));
            next = mutate(stored);
            ValidateGeneral(next);
            guidingInputsMoved = GuidingInputsMoved(stored, next);
            next = WithCorrelationPending(next, stored, guidingInputsMoved);
            row.General = Serialize(next);
            _repository.Save(row);
        }

        GeneralChanged?.Invoke(this, next);
        if (guidingInputsMoved)
        {
            Phd2GuidingInputsChanged?.Invoke(this, EventArgs.Empty);
        }

        return next;
    }

    /// <summary>Whether any input of spec 7.6's zone and site resolution order differs between the
    /// document that was stored and the one just written. See
    /// <see cref="Phd2GuidingInputsChanged"/> for why the map half is a structural comparison and
    /// not <c>!=</c>.</summary>
    private static bool GuidingInputsMoved(GeneralSettings before, GeneralSettings after)
        => !Phd2Profiles.SameMap(
               Phd2Profiles.Normalize(before.Phd2ProfileMap),
               Phd2Profiles.Normalize(after.Phd2ProfileMap))
            // The two site fields are compared "is not null" style by value: 0 is a legal
            // longitude (Greenwich) and a legal latitude (the equator), so nothing here may use a
            // truth test on either.
            || !string.Equals(before.ObserverTimezone, after.ObserverTimezone, StringComparison.Ordinal)
            || before.ObserverLatitude != after.ObserverLatitude
            || before.ObserverLongitude != after.ObserverLongitude;

    /// <summary>
    /// Spec 7.6's "The obligation survives a crash": the save that queues a correlation re-run
    /// records the obligation in <c>general.phd2_correlation_pending</c>, and no save of any kind
    /// discharges one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>In the same document, inside the same gate</b>, so the flag and the change it records are
    /// one atomic write: a process that dies between them cannot leave a moved guiding input with
    /// no record that a re-run is owed. The flag is <b>never</b> an input of
    /// <see cref="GuidingInputsMoved"/>, so the clearing write, which changes nothing else, raises
    /// no second re-run.
    /// </para>
    /// <para>
    /// <b>It is monotonic: every write ORs the stored value in</b> (fix-wave review P2-3). The
    /// helper used to set only, which is harmless through <see cref="MutateGeneral"/>, whose mutate
    /// runs on a freshly read document, and is not harmless through <see cref="SaveGeneral"/>,
    /// which serializes the caller's document wholesale: a caller holding a document it read before
    /// the flag was set wrote the flag back to false and discharged an owed re-run. Neither writer
    /// can now clear it by accident or on purpose. The <b>one</b> door that clears it is
    /// <see cref="ClearCorrelationPendingIfUnchanged"/>, which is the only place in the solution
    /// allowed to decide the obligation is discharged.
    /// </para>
    /// </remarks>
    private static GeneralSettings WithCorrelationPending(
        GeneralSettings value, GeneralSettings stored, bool guidingInputsMoved)
    {
        // Monotonic: anyone may SET it, nobody may clear it. The stored term is what closes P2-3
        // (a wholesale save of a document read before the flag was set no longer discharges the
        // obligation); the value term keeps a caller that deliberately sets it working, which is
        // what a test fixture arming the flag does.
        var pending = value.Phd2CorrelationPending
            || stored.Phd2CorrelationPending
            || guidingInputsMoved;
        return value.Phd2CorrelationPending == pending
            ? value
            : value with { Phd2CorrelationPending = pending };
    }

    /// <summary>
    /// The one door that clears <c>general.phd2_correlation_pending</c> (spec 7.6): a
    /// compare-and-clear against the stored document, taken under the write gate, on behalf of a
    /// correlation pass that has just completed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Fix-wave review P1-1.</b> The clear used to be unconditional, so a save that landed while
    /// a pass was running was discharged by that pass even though the pass never saw it. The newer
    /// change's obligation then lived only in the runner's in-process coalescing flag and was lost
    /// for good if the application exited before the coalesced second pass finished; for a
    /// telescope re-map nothing else ever recovers it, because the re-derive moves no row, no later
    /// scan widens to <c>InvalidatedNights</c>, and the nights hold filled frames so the unfilled
    /// query skips them. That is the phase review's loss sequence 3 re-opened inside the mechanism
    /// built to close it.
    /// </para>
    /// <para>
    /// <b>Why this is structural rather than a gate or a second in-process flag.</b> The comparison
    /// runs <b>inside the write gate, against the document as stored at that instant</b>, and it is
    /// <see cref="GuidingInputsMoved"/>, the same single comparison this store uses to decide
    /// whether a save owes a re-run at all. There is no second definition of "a guiding input
    /// moved" to drift. A save therefore either landed before the pass took its snapshot, in which
    /// case the pass observed it and the clear is correct, or it landed after, in which case the
    /// stored inputs no longer equal the snapshot and the flag stays true. There is no window: a
    /// save that lands while this method holds the gate is serialised behind it and finds the flag
    /// set again by <see cref="WithCorrelationPending"/>.
    /// </para>
    /// <para>
    /// Both passes call it, the scan directly and the out-of-scan re-run through the delegate
    /// <c>AppHost</c> binds, so the two cannot drift. It is deliberately NOT routed through
    /// <see cref="MutateGeneral"/>: that writer now preserves the flag on every mutation, which is
    /// what closes P2-3, and this is the one place the preservation is meant not to apply.
    /// </para>
    /// </remarks>
    /// <param name="observed">The general document the completed pass read at its start. Its four
    /// guiding inputs are the snapshot the stored document is compared against; nothing else on it
    /// is read.</param>
    /// <returns>True when the flag was cleared, false when nothing was owed or a newer save has
    /// moved a guiding input since the pass began.</returns>
    public bool ClearCorrelationPendingIfUnchanged(GeneralSettings observed)
    {
        ArgumentNullException.ThrowIfNull(observed);

        GeneralSettings next;
        lock (_writeGate)
        {
            var row = _repository.Load();
            var stored = ClampGeneral(Deserialize<GeneralSettings>(row.General));
            if (!stored.Phd2CorrelationPending || GuidingInputsMoved(observed, stored))
            {
                return false;
            }

            next = stored with { Phd2CorrelationPending = false };
            row.General = Serialize(next);
            _repository.Save(row);
        }

        // Outside the gate, exactly as the two writers raise it, and for the same reason: a
        // subscriber does work of its own. Phd2GuidingInputsChanged is deliberately NOT raised,
        // because this write moves no guiding input; raising it would make the pump feed itself.
        GeneralChanged?.Invoke(this, next);
        return true;
    }

    public DisplaySettings GetDisplay()
    {
        lock (_writeGate) { return ReadDisplay(_repository.Load().Display); }
    }

    /// <summary>
    /// Reads <c>user_settings.display</c> so that a member this build cannot read costs that member
    /// alone: it answers its documented default and every other member of the document survives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Spec 5.8.2's closing rule is that a hand-edited document still opens. A bare
    /// <c>Deserialize&lt;DisplaySettings&gt;</c> does not give that: <c>System.Text.Json</c> throws
    /// on a token of the wrong kind, and one such token anywhere in the document used to cost the
    /// profile its columns, its dashboard panel, its <c>target_page</c> disclosures and its Analysis
    /// keys at once, and the application would not start, because the startup read of this document
    /// is what raised the exception. A stored literal <c>null</c> was worse still, because it threw
    /// nothing and handed every caller a null document.
    /// </para>
    /// <para>
    /// The guard is here, at the one read every caller passes through, rather than a converter per
    /// member. A converter per member covers the members that exist on the day it is written and
    /// has to be remembered for every member a later phase adds, which is the convention rather
    /// than structure that design lesson 2 warns about. This covers every present and future member
    /// with no list of members anywhere in it.
    /// </para>
    /// <para>
    /// The happy path is one <c>Deserialize</c> and nothing else. The repair below runs only after a
    /// read has already failed, so it costs a document that opens nothing at all.
    /// </para>
    /// </remarks>
    private DisplaySettings ReadDisplay(string json)
    {
        try
        {
            if (JsonSerializer.Deserialize<DisplaySettings>(json, DisplayReadOptions) is { } document)
            {
                return document;
            }
        }
        catch (JsonException)
        {
            // Repaired below. A stored literal null throws nothing and falls through to the same
            // place, where it is answered with the defaults.
        }

        return RepairDisplay(json);
    }

    /// <summary>
    /// Drops the members of a stored display document that this build cannot read and returns what
    /// is left, or the defaults when nothing at all can be read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="JsonException.Path"/> carries the JSON path of the token the serializer refused,
    /// so the member to drop never has to be guessed: the stored text is parsed into a
    /// <see cref="JsonNode"/> tree once, the member that path names is removed, and the read is
    /// tried again. A removed member is then simply absent, which is the case spec 5.8's own
    /// opening already answers with that member's documented default.
    /// </para>
    /// <para>
    /// The path is matched against <see cref="JsonNode.GetPath"/> rather than parsed, so there is no
    /// second JSON path syntax in this file to drift from the serializer's. A path that matches no
    /// node, a path of <c>$</c>, a document that is not a JSON object and a document needing more
    /// than <see cref="MaxUnreadableMembers"/> removals all answer the defaults whole.
    /// </para>
    /// <para>
    /// What is written on the next save is what this returns, so the members that were readable are
    /// preserved and an unknown member still round-trips through
    /// <c>DisplaySettings.ExtensionData</c>. The raw text of a member that could not be read is
    /// NOT preserved: it has no home on the document, and inventing a quarantine key for it would
    /// be a change to spec 5.8.2's key list. The log line below is what stops that being silent.
    /// </para>
    /// </remarks>
    private DisplaySettings RepairDisplay(string json)
    {
        JsonObject? document;
        try
        {
            document = JsonNode.Parse(json) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            // Not JSON at all, or a duplicate key, which JsonNode.Parse refuses.
            document = null;
        }

        var dropped = new List<string>();
        while (document is not null && dropped.Count <= MaxUnreadableMembers)
        {
            string? refused;
            try
            {
                if (JsonSerializer.Deserialize<DisplaySettings>(document.ToJsonString(), DisplayReadOptions) is not { } repaired)
                {
                    break;
                }

                _logger.LogWarning(
                    "The stored display document held {Count} member(s) this build cannot read ({Paths}). "
                    + "Each answers its documented default and the rest of the document was kept.",
                    dropped.Count,
                    string.Join(", ", dropped));
                return repaired;
            }
            catch (JsonException ex)
            {
                refused = ex.Path;
            }

            if (Drop(document, refused) is not { } path)
            {
                break;
            }

            dropped.Add(path);
        }

        _logger.LogWarning(
            "The stored display document could not be read. Every key answers its documented "
            + "default until the next write replaces the document.");
        return new DisplaySettings();
    }

    /// <summary>Removes the member of the stored document that the refused path names, and
    /// returns that member's path, or null when there is nothing to remove, which is the whole
    /// document being unreadable.</summary>
    /// <remarks>
    /// <para>
    /// The document is walked from the root, following at each step the child whose own path is the
    /// refused path or leads to it, and what is removed is the DEEPEST object member on that way
    /// down. A refusal inside an array therefore costs the member that holds the array rather than
    /// one element: an element has no documented default of its own while the member does.
    /// </para>
    /// <para>
    /// A child's path comes from <see cref="JsonNode.GetPath"/>, so there is no second JSON path
    /// syntax here to drift from the serializer's. The one exception is a stored <c>null</c>, which
    /// <see cref="JsonNode"/> represents as no node at all: its path is built from its holder's,
    /// which is the plain <c>$.holder.name</c> form, so a null under a key that needs quoting is not
    /// matched and the document answers the defaults whole.
    /// </para>
    /// </remarks>
    private static string? Drop(JsonObject document, string? refused)
    {
        if (refused is null || refused.Length < 2)
        {
            return null;
        }

        JsonObject? owner = null;
        string? name = null;
        string? path = null;

        for (JsonNode? node = document; node is not null;)
        {
            var next = Step(node, refused, out var childName, out var childPath);
            if (childPath is null)
            {
                break;
            }

            if (node is JsonObject holder && childName is not null)
            {
                owner = holder;
                name = childName;
                path = childPath;
            }

            if (string.Equals(childPath, refused, StringComparison.Ordinal))
            {
                break;
            }

            node = next;
        }

        if (owner is null || name is null)
        {
            return null;
        }

        owner.Remove(name);
        return path;
    }

    /// <summary>The child of <paramref name="node"/> that is at the refused path or holds it, with
    /// that child's own path and, when the child is a member of an object, its name.</summary>
    private static JsonNode? Step(JsonNode node, string refused, out string? name, out string? path)
    {
        name = null;
        path = null;

        switch (node)
        {
            case JsonObject entries:
                foreach (var entry in entries)
                {
                    var candidate = entry.Value?.GetPath() ?? $"{entries.GetPath()}.{entry.Key}";
                    if (LeadsTo(candidate, refused))
                    {
                        name = entry.Key;
                        path = candidate;
                        return entry.Value;
                    }
                }

                break;

            case JsonArray elements:
                for (var index = 0; index < elements.Count; index++)
                {
                    var candidate = elements[index]?.GetPath() ?? $"{elements.GetPath()}[{index}]";
                    if (LeadsTo(candidate, refused))
                    {
                        path = candidate;
                        return elements[index];
                    }
                }

                break;
        }

        return null;
    }

    /// <summary>Whether a path is the refused path or an ancestor of it. The character after the
    /// shorter path has to open a member or an element, so <c>$.tab</c> is not read as an ancestor
    /// of <c>$.table</c>.</summary>
    private static bool LeadsTo(string path, string refused)
        => string.Equals(path, refused, StringComparison.Ordinal)
            || (refused.Length > path.Length
                && refused.StartsWith(path, StringComparison.Ordinal)
                && (refused[path.Length] == '.' || refused[path.Length] == '['));

    public void SaveDisplay(DisplaySettings value)
    {
        lock (_writeGate)
        {
            var row = _repository.Load();
            row.Display = Serialize(value);
            _repository.Save(row);
        }

        // Outside the gate, like GeneralChanged's, because a subscriber does work of its own: the
        // open Target detail page re-gates every frame table's columns from here.
        DisplayChanged?.Invoke(this, value);
    }

    public GraphSettings GetGraph()
    {
        lock (_writeGate) { return Deserialize<GraphSettings>(_repository.Load().Graph); }
    }

    public void SaveGraph(GraphSettings value)
    {
        lock (_writeGate)
        {
            var row = _repository.Load();
            row.Graph = Serialize(value);
            _repository.Save(row);
        }
    }

    public Dictionary<string, FilterSetting> GetFilters()
    {
        lock (_writeGate) { return Deserialize<Dictionary<string, FilterSetting>>(_repository.Load().Filters); }
    }

    public void SaveFilters(Dictionary<string, FilterSetting> value)
    {
        lock (_writeGate)
        {
            var row = _repository.Load();
            row.Filters = Serialize(value);
            _repository.Save(row);
        }
        AliasSourcesChanged?.Invoke(this, EventArgs.Empty);
    }

    public EquipmentSettings GetEquipment()
    {
        lock (_writeGate) { return Deserialize<EquipmentSettings>(_repository.Load().Equipment); }
    }

    public void SaveEquipment(EquipmentSettings value)
    {
        lock (_writeGate)
        {
            var row = _repository.Load();
            row.Equipment = Serialize(value);
            _repository.Save(row);
        }
        AliasSourcesChanged?.Invoke(this, EventArgs.Empty);
    }

    public List<List<string>> GetDismissedSuggestions()
    {
        lock (_writeGate) { return Deserialize<List<List<string>>>(_repository.Load().DismissedSuggestions); }
    }

    /// <summary>
    /// Writes <c>user_settings.dismissed_suggestions</c>, each inner group sorted.
    /// </summary>
    /// <remarks>
    /// The sort is the web's own normalization,
    /// <c>api/settings.py::update_dismissed_suggestions</c>'s
    /// <c>[sorted(group) for group in payload]</c>, and its comment says why: a dismissed grouping
    /// is identified by its members, so two writers that list the same names in different orders
    /// must produce the same stored group or the same dismissal is remembered twice and matched
    /// never. Python's <c>sorted</c> on strings is code-point order, which is
    /// <c>StringComparer.Ordinal</c> here. Applied at the store rather than in the callers
    /// (Phase 9 Task 7 review): it happened to be correct only because <c>SuggestionGrouper</c>
    /// hands over sorted lists, which puts the rule at every call site instead of at the one
    /// choke point every write passes through (design-lessons rule 2).
    /// </remarks>
    public void SaveDismissedSuggestions(List<List<string>> value)
    {
        var normalized = value.Select(group => group.Order(StringComparer.Ordinal).ToList()).ToList();
        lock (_writeGate)
        {
            var row = _repository.Load();
            row.DismissedSuggestions = Serialize(normalized);
            _repository.Save(row);
        }
    }

    /// <summary>Spec 5.8.1's clamp-on-read rule, task 7's answer to <c>questions.md</c> Q12: one
    /// clamp here, beside the existing save-time validation, rather than in
    /// <see cref="GeneralSettings"/> itself or at each consumer, so there is one place and the
    /// consumers stay simple. A document hand-edited outside a key's range still opens; it is only
    /// ever refused on write, by <see cref="ValidateGeneral"/>.</summary>
    private static GeneralSettings ClampGeneral(GeneralSettings g) => g with
    {
        ActivityRetentionDays = Math.Clamp(g.ActivityRetentionDays, 1, 3650),
        AppLogRetentionDays = Math.Clamp(g.AppLogRetentionDays, 1, 3650),
        AppLogMaxRows = Math.Clamp(g.AppLogMaxRows, 1000, 500_000),
    };

    private static void ValidateGeneral(GeneralSettings g)
    {
        if (g.ActivityRetentionDays is < 1 or > 3650)
            throw new SettingsValidationException($"general.activity_retention_days must be 1-3650, got {g.ActivityRetentionDays}.");
        if (g.AppLogRetentionDays is < 1 or > 3650)
            throw new SettingsValidationException($"general.app_log_retention_days must be 1-3650, got {g.AppLogRetentionDays}.");
        if (g.AppLogMaxRows is < 1000 or > 500_000)
            throw new SettingsValidationException($"general.app_log_max_rows must be 1000-500000, got {g.AppLogMaxRows}.");
        if (g.ObserverLatitude is < -90 or > 90)
            throw new SettingsValidationException($"general.observer_latitude must be -90 to 90, got {g.ObserverLatitude}.");
        if (g.ObserverLongitude is < -180 or > 180)
            throw new SettingsValidationException($"general.observer_longitude must be -180 to 180, got {g.ObserverLongitude}.");
        if (g.AutoScanIntervalMinutes <= 0)
            throw new SettingsValidationException("general.auto_scan_interval_minutes must be positive.");
        if (g.DefaultPageSize <= 0)
            throw new SettingsValidationException("general.default_page_size must be positive.");
        if (g.ThumbnailWidth <= 0)
            throw new SettingsValidationException("general.thumbnail_width must be positive.");
        if (g.PreviewResolution < 0)
            throw new SettingsValidationException("general.preview_resolution must be 0 or positive.");
        if (g.PreviewCacheMb <= 0)
            throw new SettingsValidationException("general.preview_cache_mb must be positive.");
        if (g.ThumbnailCacheDir.Length > 0)
        {
            if (!Path.IsPathRooted(g.ThumbnailCacheDir))
                throw new SettingsValidationException("general.thumbnail_cache_dir must be empty or an absolute path.");
            if (AppWriter.IsDriveOrShareRoot(Path.GetFullPath(g.ThumbnailCacheDir)))
                throw new SettingsValidationException("general.thumbnail_cache_dir must not be a bare drive or UNC share root.");
        }
        if (Array.IndexOf(ValidLogLevels, g.LogLevel) < 0)
            throw new SettingsValidationException($"general.log_level '{g.LogLevel}' is not recognized.");
        if (Array.IndexOf(ValidTextSizes, g.TextSize) < 0)
            throw new SettingsValidationException($"general.text_size '{g.TextSize}' is not recognized.");
        if (Array.IndexOf(ValidContentWidths, g.ContentWidth) < 0)
            throw new SettingsValidationException($"general.content_width '{g.ContentWidth}' is not recognized.");

        // Coordinator ruling Q1 (Phase 4 Task 1): wire ScanFilterConfig.Validate into the
        // settings save path now rather than deferring it to the Phase 9 Settings screen.
        // ScanFilterValidationException is translated to SettingsValidationException so
        // callers only ever see one exception type from SaveGeneral.
        try
        {
            g.ScanFilters.Validate(g.ScanRoots);
        }
        catch (ScanFilterValidationException ex)
        {
            throw new SettingsValidationException(ex.Message);
        }
    }

    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions)!;
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
}
