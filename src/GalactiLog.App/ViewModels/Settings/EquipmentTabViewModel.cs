using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.Settings;

/// <summary>
/// Design-spec 12.7's Equipment tab: "the same shape for cameras and telescopes: canonical name,
/// aliases, discovered names with counts, suggested groupings, dismiss." Two
/// <see cref="GroupingEditorViewModel"/>s, both with <c>ShowColorPicker</c> false, sharing one
/// suggestions banner and one Save (design-lessons rule 1 at the third
/// <see cref="GroupingEditorViewModel"/> occurrence).
/// </summary>
public sealed partial class EquipmentTabViewModel : ObservableObject, IDisposable, IPendingEdits
{
    private const string TelescopesSection = "telescopes";
    private const string CamerasSection = "cameras";

    private readonly Func<EquipmentSettings> _loadEquipment;
    private readonly Action<EquipmentSettings> _saveEquipment;
    private readonly Func<List<List<string>>> _loadDismissed;
    private readonly Action<List<List<string>>> _saveDismissed;
    private readonly Func<IReadOnlyList<(string Name, int Count)>> _discoveredCameras;
    private readonly Func<IReadOnlyList<(string Name, int Count)>> _discoveredTelescopes;
    private readonly Func<IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>, int>? _rewriteRigLabels;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;

    private readonly CancellationTokenSource _lifetime = new();
    private int _generation;
    private bool _disposed;

    // Bumped by every edit. A save records the value it snapshotted at and clears IsDirty only if
    // nothing was edited while it ran: an edit made during an in-flight save is not in that save's
    // document, so it has to stay pending.
    private int _editVersion;

    private List<List<string>> _dismissed = [];

    // The canonical telescope name each group was loaded with, keyed by the group instance. A
    // plain rename assigns AliasGroupViewModel.Canonical and touches no alias, so the saved
    // document holds no trace of the old name and the alias table alone cannot recover it; this is
    // the only record of it, and without it a rename strands every profile mapped to the old name
    // (review P1-1). Rebuilt on every Publish, so it can never accumulate.
    private readonly Dictionary<AliasGroupViewModel, string> _loadedTelescopeNames = [];

    // The camera counterpart, kept the same way and for the same reason (spec 12.15). A rig label
    // carries both halves, "{telescope} / {camera}", so a camera rename moves it exactly as a
    // telescope rename does and the profile map's telescope-only record is not enough.
    private readonly Dictionary<AliasGroupViewModel, string> _loadedCameraNames = [];

    // The alias table each section was loaded with: a raw spelling and the canonical name it
    // resolved to then. A stored rig label holds the canonical name of the moment it was written,
    // so the only honest way to tell that a label has moved is to compare what a raw spelling
    // resolved to before this save with what it resolves to after. The saved document alone cannot
    // say it: a group that was deleted leaves no entry behind, and a canonical name that stops
    // folding a spelling looks exactly like a name nothing ever folded. Rebuilt beside the loaded
    // names, on a load and on a save that reached the end, so neither can accumulate.
    private readonly Dictionary<string, string> _loadedTelescopeAliases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _loadedCameraAliases = new(StringComparer.Ordinal);
    private IReadOnlyList<(string Name, int Count)> _lastDiscoveredCameras = [];
    private IReadOnlyList<(string Name, int Count)> _lastDiscoveredTelescopes = [];

    /// <param name="loadEquipment">Normally <c>SettingsStore.GetEquipment</c>. Called off the UI
    /// thread.</param>
    /// <param name="saveEquipment">Normally <c>SettingsStore.SaveEquipment</c>, which raises
    /// <c>AliasSourcesChanged</c>. Called off the UI thread, inside <see cref="SaveAsync"/>.
    /// </param>
    /// <param name="loadDismissed">Normally <c>SettingsStore.GetDismissedSuggestions</c>. Shared
    /// with the Filters tab's own dismissed list, per spec 5.8.</param>
    /// <param name="saveDismissed">Normally <c>SettingsStore.SaveDismissedSuggestions</c>, which
    /// raises nothing.</param>
    /// <param name="discoveredCameras">Normally <c>DiscoveredNamesQuery.Read(Cameras)</c>.
    /// </param>
    /// <param name="discoveredTelescopes">Normally <c>DiscoveredNamesQuery.Read(Telescopes)</c>.
    /// </param>
    /// <param name="post">How to reach the UI thread. Defaults to <c>UiPost.Default</c>.</param>
    /// <param name="logger">Optional. A failed load or save is logged, never rethrown on the UI
    /// thread.</param>
    /// <param name="phd2Profiles">Spec 12.7's PHD2 profiles panel (Phase 15A Task 6), held below
    /// the two grouping editors. A trailing optional parameter (collision map section 5) so no
    /// existing call site of this constructor moves. Null hides the panel, which is this task's
    /// own state until Task 6b's <c>AppHost</c> registration supplies it: the panel needs the
    /// registered <c>Phd2ProfilesQuery</c> and <c>SettingsStore.MutateGeneral</c>, both reached
    /// only from <c>AppHost.cs</c>, which this task does not open. It also carries this tab's
    /// rename-rewrite hook (<see cref="SaveAsync"/>): with no panel there is nothing to rewrite.
    /// </param>
    /// <param name="rewriteRigLabels">Spec 12.15's third write, normally
    /// <c>CustomColumnRepository.RewriteRigLabels</c>, bound by <c>AppHost</c>. Takes the telescope
    /// moves and the camera moves, each an old canonical spelling and the one it resolves to after
    /// this save (<see cref="BuildLabelMoves"/>), and answers the number of <c>custom_column_values</c>
    /// rows whose <c>rig_label</c> moved; it reads the stored labels and splits them itself, so
    /// nothing here composes a label (ruling C9). Called off the UI thread inside
    /// <see cref="SaveAsync"/>. A trailing optional parameter, so every existing construction site
    /// compiles unchanged; null means there is nothing to rewrite, which is what a unit test with
    /// no database gets.</param>
    public EquipmentTabViewModel(
        Func<EquipmentSettings> loadEquipment,
        Action<EquipmentSettings> saveEquipment,
        Func<List<List<string>>> loadDismissed,
        Action<List<List<string>>> saveDismissed,
        Func<IReadOnlyList<(string Name, int Count)>> discoveredCameras,
        Func<IReadOnlyList<(string Name, int Count)>> discoveredTelescopes,
        Action<Action>? post = null,
        ILogger? logger = null,
        Phd2ProfilesViewModel? phd2Profiles = null,
        Func<IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>, int>? rewriteRigLabels = null)
    {
        _rewriteRigLabels = rewriteRigLabels;
        _loadEquipment = loadEquipment;
        _saveEquipment = saveEquipment;
        _loadDismissed = loadDismissed;
        _saveDismissed = saveDismissed;
        _discoveredCameras = discoveredCameras;
        _discoveredTelescopes = discoveredTelescopes;
        _post = post ?? UiPost.Default;
        _logger = logger ?? NullLogger.Instance;
        Phd2Profiles = phd2Profiles;

        CamerasEditor = new GroupingEditorViewModel(showColorPicker: false);
        TelescopesEditor = new GroupingEditorViewModel(showColorPicker: false);
        CamerasEditor.RenameRefused += (_, name) => ErrorMessage = $"'{name}' is already a canonical camera name.";
        TelescopesEditor.RenameRefused += (_, name) => ErrorMessage = $"'{name}' is already a canonical telescope name.";
        // Each editor's one change signal covers every group, alias and rename edit in its
        // section, including the ones AcceptSuggestion makes through it. A dismissal touches only
        // _dismissed and marks itself. The PHD2 profiles panel is not an edit here: it commits each
        // row through MutateGeneral as it changes, so it never has anything staged.
        CamerasEditor.Edited += (_, _) => MarkEdited();
        TelescopesEditor.Edited += (_, _) => MarkEdited();

        Load();
    }

    /// <summary>Spec 12.7's Cameras section.</summary>
    public GroupingEditorViewModel CamerasEditor { get; }

    /// <summary>Spec 12.7's Telescopes section.</summary>
    public GroupingEditorViewModel TelescopesEditor { get; }

    /// <summary>Spec 12.7's PHD2 profiles panel, below the two grouping editors above. Null until
    /// Task 6b's <c>AppHost</c> registration supplies one.</summary>
    public Phd2ProfilesViewModel? Phd2Profiles { get; }

    /// <summary>
    /// Every telescope name spec 12.7's PHD2 profile picker offers: the canonical names of
    /// <see cref="TelescopesEditor"/>'s groups, and the discovered telescope names of the library
    /// that belong to no group. The rule has one home, here, and both the picker's source and the
    /// rename rewrite's "is this still a telescope name" test read it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The canonical names alone were the picker's source until Phase 15B's real-data pass found
    /// that a library whose reader has never created an alias group has none, so every row's
    /// picker offered "Not mapped" and no profile could be mapped at all. The web reads the
    /// DISTINCT telescope values of the images table for the same picker
    /// (<c>Phd2ProfilePanel.tsx</c> over <c>GET /api/targets/equipment</c>), which is what
    /// <see cref="GroupingEditorViewModel.Ungrouped"/> already holds on this same page.
    /// </para>
    /// <para>
    /// One scope appears once: <see cref="GroupingEditorViewModel.Ungrouped"/> is exactly the
    /// discovered names no group's canonical name or alias list covers, so a grouped telescope
    /// contributes its canonical name only and its member spellings never appear. No second query
    /// is issued: this reads what the tab's own load already put on the editor. The picker sorts
    /// and de-duplicates this list itself, so it is returned in the two collections' own order.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> KnownTelescopes() =>
    [
        .. TelescopesEditor.Groups.Select(group => group.Canonical),
        .. TelescopesEditor.Ungrouped.Select(row => row.Name),
    ];

    /// <summary>Spec 12.7's suggested groupings, cameras and telescopes in one banner (the web's
    /// combined <c>suggestions/equipment</c> response), each tagged by
    /// <see cref="SuggestionViewModel.Section"/>.</summary>
    public ObservableCollection<SuggestionViewModel> Suggestions { get; } = [];

    public bool HasSuggestions => Suggestions.Count > 0;

    public string SuggestionsHeading
        => Suggestions.Count == 1 ? "Found 1 possible duplicate" : $"Found {Suggestions.Count} possible duplicates";

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial bool LoadFailed { get; private set; }

    /// <summary>Spec 12.10's failure line for this tab's discovered read (the equipment
    /// equivalent of the Filters tab's message; both sections load in one batch, so one failure
    /// covers both).</summary>
    public string LoadFailedMessage => "The discovered equipment names could not be loaded.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(SaveRefusal))]
    public partial bool IsSaving { get; private set; }

    /// <summary>An edit exists that the stored equipment document and dismissed list do not hold
    /// yet. Set by every user edit in either section and by a dismissal, cleared by a load and by
    /// a save that reached the end with no edit made while it ran.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(HasPendingEdits))]
    [NotifyPropertyChangedFor(nameof(SaveRefusal))]
    public partial bool IsDirty { get; private set; }

    /// <inheritdoc />
    public bool HasPendingEdits => IsDirty;

    /// <inheritdoc />
    public string Label => "Equipment names";

    /// <inheritdoc />
    public string NavigationKey => "equipment";

    /// <inheritdoc />
    public string? SaveRefusal => IsDirty && IsSaving ? "Equipment is still saving." : null;

    Task IPendingEdits.SaveAsync() => SaveCommand.ExecuteAsync(null);

    /// <summary>Re-reads the stored documents through the same load the constructor runs, which
    /// replaces both sections' groups, the dismissed list and the loaded names, and clears
    /// <see cref="IsDirty"/> when it lands.</summary>
    /// <remarks>
    /// <see cref="IsDirty"/> is deliberately not cleared here. Publish clears it only on a read
    /// that succeeded; a failed read leaves the edits on screen, and they must keep the save bar
    /// up rather than sit there unmarked. While a save is in flight the reload waits for it: a
    /// pool-thread read started now can reach the store before the save writes, which would leave
    /// the editors on the pre-save document and let the next save silently revert this one.
    /// </remarks>
    void IPendingEdits.Discard()
    {
        if (IsSaving)
        {
            _discardAfterSave = true;
            return;
        }

        Load();
    }

    // Set by a Discard that arrived while a save was in flight, consumed by that save's own UI
    // thread callback, which runs the reload once the save has written (or failed).
    private bool _discardAfterSave;

    private void RunDeferredDiscard()
    {
        if (_discardAfterSave)
        {
            _discardAfterSave = false;
            Load();
        }
    }

    private void MarkEdited()
    {
        _editVersion++;
        IsDirty = true;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorMessage))]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    public partial string? StatusMessage { get; private set; }

    public bool HasErrorMessage => ErrorMessage is not null;

    public bool HasStatusMessage => StatusMessage is not null;

    internal Task? PendingLoad { get; private set; }

    internal Task? PendingSave { get; private set; }

    /// <summary>Whether <see cref="Dispose"/> has run.</summary>
    internal bool IsDisposed => _disposed;

    public void Reload() => RefreshSuggestions();

    private bool _phd2ProfilesInViewPending;

    /// <summary>
    /// Asks for spec 12.7's PHD2 profiles panel to be brought into view the next time this tab is
    /// shown. Spec 12.5's Guiding empty notice reads "n guiding sessions found but no PHD2 profile
    /// is mapped to a telescope" and offers "Map profiles", which lands here through
    /// <c>MainWindowViewModel</c>'s one Settings route.
    /// </summary>
    /// <remarks>
    /// Phase 15B Task 5c. A flag rather than an event, mirroring
    /// <c>LibraryTabViewModel.RequestNameRulesInView</c>: the shell selects the tab before its
    /// view is attached and often before the tab itself is constructed, so there is no view to
    /// receive an event when the route runs. <c>EquipmentTabView.OnAttachedToVisualTree</c>
    /// consumes it.
    /// </remarks>
    public void RequestPhd2ProfilesInView() => _phd2ProfilesInViewPending = true;

    /// <summary>Reads the pending request and clears it, so one route scrolls once.</summary>
    internal bool ConsumePhd2ProfilesInViewRequest()
    {
        var pending = _phd2ProfilesInViewPending;
        _phd2ProfilesInViewPending = false;
        return pending;
    }

    private Action? _unfollowGeneralChanged;

    /// <summary>
    /// Follows <c>SettingsStore.GeneralChanged</c> so <see cref="Phd2Profiles"/> brings a general
    /// document written elsewhere onto itself when a settings-triggered correlation re-run
    /// completes. The re-run clears <c>general.phd2_correlation_pending</c> through
    /// <c>MutateGeneral</c>, which raises that event; without this the panel's "a re-run is still
    /// owed" sentence stayed up until the tab was rebuilt, because the flag is read only at load
    /// (Phase 15A carried item 64).
    /// </summary>
    /// <param name="subscribe">Normally <c>handler =&gt; settingsStore.GeneralChanged += handler</c>.
    /// The tab takes the two sides as delegates rather than the store itself, like every other
    /// view-model in this application (design-spec 18.3).</param>
    /// <param name="unsubscribe">Its pair, called from <see cref="Dispose"/>, so a disposed tab
    /// leaves no handler on a store that outlives it.</param>
    /// <remarks>
    /// A method called after construction rather than two more constructor parameters, because
    /// carried item 56 owns that constructor: it calls <see cref="Load"/> on a pool thread and the
    /// structural fix is moving that call out, which is a different item with a different owner.
    /// The panel's <c>Reload</c> is posted because <c>GeneralChanged</c> is raised on whichever
    /// thread saved the document, and the reload writes observable properties.
    /// </remarks>
    public void FollowGeneralChanges(
        Action<EventHandler<GeneralSettings>> subscribe,
        Action<EventHandler<GeneralSettings>> unsubscribe)
    {
        // Review P3-2: a second call would subscribe a second handler and overwrite the unfollow
        // that drops the first, leaving one handler on a process-singleton store holding this tab
        // alive. One follower per tab; the later call is the one refused, because the earlier one
        // is the one Dispose can still drop.
        if (_disposed || _unfollowGeneralChanged is not null)
        {
            return;
        }

        subscribe(OnGeneralChanged);
        _unfollowGeneralChanged = () => unsubscribe(OnGeneralChanged);
    }

    // Carried item 64. What a completed correlation re-run moves is the stored document, not the
    // panel's rows, so this hands the panel the document the event already carries and the panel
    // applies it in place. It deliberately does NOT reload: the panel commits every row edit
    // through the same MutateGeneral that raises this event, so a reload here would rebuild the
    // editor the user is typing into on the first keystroke that parses (review P2-1).
    private void OnGeneralChanged(object? sender, GeneralSettings general)
    {
        // This panel's own commit coming back through the store's event, the check
        // GeneralSettingsTabViewModel.OnGeneralChanged makes on the raising thread for the same
        // reason: CommitRow has already patched and published the edited row, and answering the
        // event as well would recompute it a second time on every keystroke.
        if (Phd2Profiles is not { } panel || panel.IsOwnWriteThread)
        {
            return;
        }

        _post(() =>
        {
            if (!_disposed)
            {
                panel.ApplyGeneral(general);
            }
        });
    }

    [RelayCommand]
    private void AcceptSuggestion(SuggestionViewModel suggestion)
    {
        if (!Suggestions.Contains(suggestion))
        {
            return;
        }

        var editor = string.Equals(suggestion.Section, TelescopesSection, StringComparison.Ordinal)
            ? TelescopesEditor
            : CamerasEditor;

        var canonical = suggestion.SelectedName;
        var aliases = suggestion.Names.Where(name => !string.Equals(name, canonical, StringComparison.Ordinal)).ToList();

        // The web's cleanup (EquipmentTab.tsx:77, review I2), within the section being merged
        // into: a raw name about to become an alias may already be an existing canonical name in
        // this same section.
        foreach (var absorbed in editor.Groups.Where(group => aliases.Contains(group.Canonical, StringComparer.Ordinal)).ToList())
        {
            editor.RemoveGroup(absorbed);
        }

        var existing = editor.Groups.FirstOrDefault(group => string.Equals(group.Canonical, canonical, StringComparison.Ordinal));
        if (existing is not null)
        {
            foreach (var alias in aliases)
            {
                if (!existing.Aliases.Contains(alias))
                {
                    existing.Aliases.Add(alias);
                }
            }
        }
        else
        {
            editor.AddGroup(new AliasGroupViewModel(canonical, color: null, aliases));
        }

        Suggestions.Remove(suggestion);
        OnPropertyChanged(nameof(HasSuggestions));
        OnPropertyChanged(nameof(SuggestionsHeading));
    }

    [RelayCommand]
    private void DismissSuggestion(SuggestionViewModel suggestion)
    {
        if (!Suggestions.Contains(suggestion))
        {
            return;
        }

        _dismissed.Add([.. suggestion.Names]);
        MarkEdited();
        Suggestions.Remove(suggestion);
        OnPropertyChanged(nameof(HasSuggestions));
        OnPropertyChanged(nameof(SuggestionsHeading));
    }

    /// <summary>Writes one equipment document (cameras and telescopes together), then the
    /// dismissed list, matching the web's <c>{cameras: ..., telescopes: ...}</c> payload shape.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!CanSave())
        {
            return;
        }

        IsSaving = true;
        var document = new EquipmentSettings
        {
            Cameras = CamerasEditor.Groups.ToDictionary(
                group => group.Canonical,
                group => new EquipmentItemSettings { Aliases = [.. group.Aliases] },
                StringComparer.Ordinal),
            Telescopes = TelescopesEditor.Groups.ToDictionary(
                group => group.Canonical,
                group => new EquipmentItemSettings { Aliases = [.. group.Aliases] },
                StringComparer.Ordinal),
        };
        var dismissed = _dismissed.Select(group => new List<string>(group)).ToList();
        var savedVersion = _editVersion;

        // What this save writes, captured with the document: the success callback records these as
        // the loaded names, never what the editors hold by then. A rename made while the save is in
        // flight is not on disk yet, and recording it as loaded would leave the next save with no
        // rename pair and no label move for it.
        var savedTelescopes = Snapshot(TelescopesEditor);
        var savedCameras = Snapshot(CamerasEditor);

        // Built here, on the UI thread, with the rest of the document: the groups are a bound
        // collection and are read where every other read of them happens (TRACKING section 6
        // item 24).
        var renamed = BuildRenames(TelescopesEditor, _loadedTelescopeNames);
        var camerasRenamed = BuildRenames(CamerasEditor, _loadedCameraNames);

        // Both alias tables of the document about to be saved, built here with the rest of it and
        // for the same reason, and read by both rewrites below.
        var (cameraAliases, telescopeAliases) = BuildEquipmentAliasMaps(document);

        // What the rig-label rewrite is handed: the spellings a stored label has to move BETWEEN,
        // not the names the user renamed. A rename is only one of the ways a canonical name moves,
        // and the loaded alias tables are read here on the UI thread for the same reason the groups
        // are.
        var telescopeMoves = BuildLabelMoves(renamed, _loadedTelescopeAliases, telescopeAliases);
        var cameraMoves = BuildLabelMoves(camerasRenamed, _loadedCameraAliases, cameraAliases);

        // The same list the picker offers, captured on the UI thread for the same reason, because
        // the rewrite below unmaps every telescope name it does not recognise. A profile mapped to
        // an ungrouped discovered name is a name the canonical dictionary alone does not carry, and
        // testing against that dictionary would wipe such a mapping on the next equipment save.
        var knownTelescopes = new HashSet<string>(KnownTelescopes(), StringComparer.Ordinal);

        var task = Task.Run(() =>
        {
            try
            {
                _saveEquipment(document);

                // The dismissed suggestions, ahead of both rewrites (review P3-3). It is a settings
                // write with nothing to do with either of them, and a catalogue or general-document
                // failure below must not discard it as well.
                _saveDismissed(dismissed);

                // Spec 5.8.1, 12.7: a telescope rename or regrouping moves the map's telescope
                // fields onto the new canonical name in the same write as the equipment save that
                // caused it, so a mapping never strands on an alias. Immediately after
                // SaveEquipment and the dismissed list, ahead of the catalogue write (Task 6 brief
                // section 5).
                //
                // They are two writes and they cannot be made one here: the aliases live in the
                // equipment document behind SettingsStore.SaveEquipment and the map lives in the
                // general document behind SettingsStore.MutateGeneral, and no store method writes
                // both. If this one throws after the equipment save has landed, the map keeps the
                // old telescope name until the next save of the same rename repairs it, because
                // the rename pairs are rebuilt from the loaded names each time. Nothing to rewrite
                // with no panel constructed.
                if (Phd2Profiles is not null)
                {
                    var telescopeCanonicals = document.Telescopes;

                    // Three sources, composed in this order, because a rename can land on a name
                    // that the alias table then folds again (renaming "Askar 120" to a name that
                    // is already an alias of "Askar FMA180" must reach "Askar FMA180", not stop on
                    // the alias):
                    //   1. the old-to-new pairs of groups whose Canonical moved, which is the only
                    //      record of a plain rename (review P1-1),
                    //   2. the saved document's alias table, which is what a regroup or a merge
                    //      leaves behind: the two together are Fold,
                    //   3. the name itself when it is already a current canonical.
                    // A name none of the three recognises is left alone by the rewrite, and
                    // isCurrentName below decides its fate: an ungrouped discovered name is a name
                    // the picker offers and it stays, a telescope whose group was deleted and whose
                    // name the library no longer carries is unmapped rather than left dangling.
                    // Folding onto an alias always lands on a key of the saved document, because
                    // that is what the alias table is built from, so the membership test below is
                    // the one answer for both paths.
                    string? Rename(string name)
                    {
                        var folded = Fold(name, renamed, telescopeAliases);
                        return telescopeCanonicals.ContainsKey(folded) ? folded : null;
                    }

                    Phd2Profiles.RewriteTelescopes(Rename, knownTelescopes.Contains);
                }

                // Spec 12.15: a rig label is "{telescope} / {camera}" over the canonical names
                // (SessionDetailQuery line 418), so anything that moves either half moves every
                // stored rig-scope custom value keyed to it. The third write, immediately after the
                // profile-map rewrite and for the same reason: a label never strands on a canonical
                // name the catalogue no longer knows.
                //
                // Three documents, three writes, and no call writes all three: the aliases live in
                // the equipment document, the map in the general document and the labels in the
                // catalogue. If this one throws after the equipment save has landed, the labels keep
                // the old canonical name until the next save of the same change repairs it, because
                // the moves are rebuilt from the loaded names and the loaded alias tables each time
                // and both are only refreshed on a save that reached the end. Until then those
                // values render under a rig row the pane no longer draws, rather than under the
                // wrong rig.
                if (_rewriteRigLabels is not null && (telescopeMoves.Count > 0 || cameraMoves.Count > 0))
                {
                    _rewriteRigLabels(telescopeMoves, cameraMoves);
                }

                _post(() =>
                {
                    if (_disposed)
                    {
                        return;
                    }

                    // Only here, after the profile-map and rig-label rewrites: a save that throws
                    // in either of them has landed the equipment document but not the loaded
                    // names, so the next save repeats the rewrite, and the edit has to stay
                    // pending for that save to be offered.
                    if (_editVersion == savedVersion)
                    {
                        IsDirty = false;
                    }

                    IsSaving = false;
                    ErrorMessage = null;
                    StatusMessage = "Equipment settings saved";

                    // The saved names and the saved alias tables are the loaded ones now, so the
                    // next save carries only the changes made after this one.
                    Remember(savedTelescopes, _loadedTelescopeNames, _loadedTelescopeAliases);
                    Remember(savedCameras, _loadedCameraNames, _loadedCameraAliases);

                    RefreshSuggestions();
                    RunDeferredDiscard();
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Saving the equipment settings failed");
                _post(() =>
                {
                    if (_disposed)
                    {
                        return;
                    }

                    IsSaving = false;
                    ErrorMessage = "The equipment settings could not be saved. See the log for details.";
                    RunDeferredDiscard();
                });
            }
        });
        PendingSave = task;

        // No ConfigureAwait(false): the generated AsyncRelayCommand raises its own property and
        // CanExecute notifications when this task completes, and off the dispatcher that is the
        // shape of the Phase 14B Clear log crash (TRACKING section 5).
        await task;
    }

    // Old canonical name to new, for every group of one editor whose name the user changed since
    // the document was loaded. GroupingEditorViewModel.OnRenameRequested renames by assigning
    // group.Canonical and adds no alias, so nothing in the saved document remembers the old name
    // (review P1-1). A group added since the load carries no loaded name and is not a rename; a
    // group removed since the load is absent from Groups and is handled as a deletion by the
    // isCurrentName half of the rewrite.
    //
    // One method over both editors, not one per editor (design lesson 1, review P3-1): a third
    // editor is a call site here rather than a third copy.
    private static Dictionary<string, string> BuildRenames(
        GroupingEditorViewModel editor, Dictionary<AliasGroupViewModel, string> loadedNames)
    {
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in editor.Groups)
        {
            if (loadedNames.TryGetValue(group, out var loaded)
                && !string.Equals(loaded, group.Canonical, StringComparison.Ordinal))
            {
                renames[loaded] = group.Canonical;
            }
        }

        return renames;
    }

    // Where a stored rig-label half has to move: the canonical spelling a raw equipment name
    // resolved to before this save, and the one it resolves to after. A rename is only one of the
    // ways a canonical name moves. Adding a discovered spelling to a group, taking one out of a
    // group and deleting a group outright all move it with no name renamed, and BuildRenames sees
    // none of them (spec 12.15's own promise, review P2-1). One method over both sections, like
    // BuildRenames beside it.
    //
    // Only the spellings the two alias tables and the rename pairs name are considered: every other
    // spelling resolves to itself on both sides and cannot have moved.
    //
    // A canonical name that two spellings leave for two different destinations is left exactly
    // where it is. This tab holds no frame and cannot tell which of them a stored label was written
    // for, so moving every value to one of the two would be a guess; a group of three folded names
    // taken apart again is the shape that reaches it, and those values stay where a later regroup
    // can still reach them.
    private static Dictionary<string, string> BuildLabelMoves(
        IReadOnlyDictionary<string, string> renames,
        IReadOnlyDictionary<string, string> loadedAliases,
        IReadOnlyDictionary<string, string> savedAliases)
    {
        var destinations = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var name in renames.Keys.Concat(loadedAliases.Keys).Concat(savedAliases.Keys))
        {
            var before = loadedAliases.GetValueOrDefault(name, name);
            if (!destinations.TryGetValue(before, out var reached))
            {
                reached = new HashSet<string>(StringComparer.Ordinal);
                destinations[before] = reached;
            }

            reached.Add(Fold(name, renames, savedAliases));
        }

        var moves = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (before, reached) in destinations)
        {
            if (reached.Count == 1 && !reached.Contains(before))
            {
                moves[before] = reached.First();
            }
        }

        return moves;
    }

    // One equipment name through a save's rename pairs and then the saved document's alias table,
    // in that order, which is what the profile map rewrite's own comment explains: a rename can
    // land on a name the alias table folds again. The one place that composition lives, read by the
    // profile map rewrite and by BuildLabelMoves above.
    private static string Fold(
        string name,
        IReadOnlyDictionary<string, string> renames,
        IReadOnlyDictionary<string, string> aliases)
    {
        var target = renames.GetValueOrDefault(name, name);
        return aliases.GetValueOrDefault(target, target);
    }

    // The other half of the same pattern: what one editor's groups are called NOW becomes what
    // they were loaded as, and what they fold now becomes what they folded then, so the next
    // BuildRenames and the next BuildLabelMoves carry only the changes made after this point.
    // Called on a load and on a save that reached the end, never on one that failed.
    private static void Remember(
        GroupingEditorViewModel editor,
        Dictionary<AliasGroupViewModel, string> loadedNames,
        Dictionary<string, string> loadedAliases)
        => Remember(Snapshot(editor), loadedNames, loadedAliases);

    private static void Remember(
        IReadOnlyList<(AliasGroupViewModel Group, string Canonical, string[] Aliases)> snapshot,
        Dictionary<AliasGroupViewModel, string> loadedNames,
        Dictionary<string, string> loadedAliases)
    {
        loadedNames.Clear();
        loadedAliases.Clear();
        foreach (var (group, canonical, aliases) in snapshot)
        {
            loadedNames[group] = canonical;
            foreach (var alias in aliases)
            {
                loadedAliases[alias] = canonical;
            }
        }
    }

    // One editor's groups as they are now, copied, so a save can record what it wrote after the
    // editors have moved on.
    private static List<(AliasGroupViewModel Group, string Canonical, string[] Aliases)> Snapshot(
        GroupingEditorViewModel editor)
        => [.. editor.Groups.Select(group => (group, group.Canonical, group.Aliases.ToArray()))];

    private bool CanSave() => IsDirty && !IsSaving;

    private void Load()
    {
        if (_disposed)
        {
            return;
        }

        IsLoading = true;
        var generation = ++_generation;
        var token = _lifetime.Token;
        PendingLoad = Task.Run(
            () =>
            {
                try
                {
                    var equipment = _loadEquipment();
                    var dismissed = _loadDismissed();
                    var cameras = _discoveredCameras();
                    var telescopes = _discoveredTelescopes();
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    _post(() => Publish(generation, equipment, dismissed, cameras, telescopes));
                }
                catch (OperationCanceledException)
                {
                    // The tab went away while the read was in flight.
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Loading the equipment settings failed");
                    _post(() => Publish(generation, null, null, null, null));
                }
            },
            token);
    }

    private void Publish(
        int generation,
        EquipmentSettings? equipment,
        List<List<string>>? dismissed,
        IReadOnlyList<(string Name, int Count)>? cameras,
        IReadOnlyList<(string Name, int Count)>? telescopes)
    {
        if (generation != _generation || _disposed)
        {
            return;
        }

        if (equipment is not null && dismissed is not null && cameras is not null && telescopes is not null)
        {
            _dismissed = dismissed;
            // Suggestions run over the raw, unfolded counts (settings.py's suggest_equipment
            // reads the same ungrouped queries the discovered endpoints do, with no fold).
            _lastDiscoveredCameras = cameras;
            _lastDiscoveredTelescopes = telescopes;

            var (cameraMap, telescopeMap) = BuildEquipmentAliasMaps(equipment);
            CamerasEditor.SetDiscovered(FoldDiscovered(cameras, cameraMap));
            CamerasEditor.SetGroups(equipment.Cameras.Select(entry => new AliasGroupViewModel(entry.Key, color: null, entry.Value.Aliases)));

            TelescopesEditor.SetDiscovered(FoldDiscovered(telescopes, telescopeMap));
            TelescopesEditor.SetGroups(equipment.Telescopes.Select(entry => new AliasGroupViewModel(entry.Key, color: null, entry.Value.Aliases)));

            Remember(TelescopesEditor, _loadedTelescopeNames, _loadedTelescopeAliases);
            Remember(CamerasEditor, _loadedCameraNames, _loadedCameraAliases);

            RefreshSuggestions();
            // Both editors now hold exactly the stored document. Only on a successful read: a
            // failed one left the editors as they were, edits included, so they stay pending.
            IsDirty = false;
        }

        LoadFailed = equipment is null;
        IsLoading = false;
    }

    // Port of settings.py:572-578's discovered-endpoint fold, the equipment-section counterpart
    // of FiltersTabViewModel.FoldDiscovered (review minor 3, ruled "implement"). aliasMap is the
    // plain alias-to-canonical dictionary AliasMap itself builds internally; equipment has no
    // FilterConfig-shaped canonical/colour document to hand a fresh AliasMap the way filters does,
    // so the fold works directly off that dictionary instead.
    private static IReadOnlyList<(string Name, int Count)> FoldDiscovered(
        IReadOnlyList<(string Name, int Count)> discovered,
        IReadOnlyDictionary<string, string> aliasMap)
    {
        var order = new List<string>();
        var merged = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (name, count) in discovered)
        {
            var canonical = aliasMap.GetValueOrDefault(name, name);
            if (merged.TryGetValue(canonical, out var existing))
            {
                merged[canonical] = existing + count;
            }
            else
            {
                merged[canonical] = count;
                order.Add(canonical);
            }
        }

        return [.. order
            .Select(name => (Name: name, Count: merged[name]))
            .OrderByDescending(item => item.Count)];
    }

    // Port of normalization.py::build_equipment_alias_maps: alias -> canonical, one dictionary
    // per section, built straight from the just-loaded document rather than through AliasMap
    // (which folds filters and equipment together and this tab only ever holds equipment).
    private static (Dictionary<string, string> Cameras, Dictionary<string, string> Telescopes) BuildEquipmentAliasMaps(EquipmentSettings equipment)
    {
        var cameras = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (canonical, item) in equipment.Cameras)
        {
            foreach (var alias in item.Aliases)
            {
                cameras[alias] = canonical;
            }
        }

        var telescopes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (canonical, item) in equipment.Telescopes)
        {
            foreach (var alias in item.Aliases)
            {
                telescopes[alias] = canonical;
            }
        }

        return (cameras, telescopes);
    }

    private void RefreshSuggestions()
    {
        // The web unions the camera and telescope known-name sets before filtering either
        // section's suggestions (settings.py:644-646, review minor 1): a name already known as a
        // telescope alias must not be re-suggested as a camera grouping candidate either, even
        // though the two sections save to different dictionaries.
        var known = KnownNames(CamerasEditor);
        known.UnionWith(KnownNames(TelescopesEditor));

        var cameraCounts = _lastDiscoveredCameras.ToDictionary(item => item.Name, item => item.Count, StringComparer.Ordinal);
        var telescopeCounts = _lastDiscoveredTelescopes.ToDictionary(item => item.Name, item => item.Count, StringComparer.Ordinal);

        // The web's own order: cameras first, telescopes second.
        var cameraGroups = SuggestionGrouper.Group(cameraCounts)
            .Where(group => !SuggestionGrouper.AlreadyMerged(group, known) && !SuggestionGrouper.IsDismissed(group, _dismissed))
            .Select(group => new SuggestionViewModel(group, CamerasSection));
        var telescopeGroups = SuggestionGrouper.Group(telescopeCounts)
            .Where(group => !SuggestionGrouper.AlreadyMerged(group, known) && !SuggestionGrouper.IsDismissed(group, _dismissed))
            .Select(group => new SuggestionViewModel(group, TelescopesSection));

        Suggestions.Clear();
        foreach (var suggestion in cameraGroups.Concat(telescopeGroups))
        {
            Suggestions.Add(suggestion);
        }

        OnPropertyChanged(nameof(HasSuggestions));
        OnPropertyChanged(nameof(SuggestionsHeading));
    }

    private static HashSet<string> KnownNames(GroupingEditorViewModel editor)
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in editor.Groups)
        {
            known.Add(group.Canonical);
            foreach (var alias in group.Aliases)
            {
                known.Add(alias);
            }
        }

        return known;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Carried item 64's handler, dropped before the tab goes away: the store is a process
        // singleton and outlives every tab built over it.
        _unfollowGeneralChanged?.Invoke();
        _unfollowGeneralChanged = null;

        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
