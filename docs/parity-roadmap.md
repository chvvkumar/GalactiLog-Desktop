# GalactiLog-Windows parity roadmap: Phase 14 onward

Written 2026-09-17 during Phase 13 from the user's directions in
`docs/superpowers/work/phase13/parity-backlog.md` section 3, the audit in
`docs/superpowers/work/phase13/parity-audit.md` and the web inventory in
`docs/superpowers/work/phase13/audit-inventory-web-target.md`. The user approves this document
once as a whole; each phase then runs under the standing process with no further plan review.
Phase 13 is running when this is written and is not planned here. `docs/roadmap.md` stays the
record of Phases 0 to 13; this document is the plan for Phases 14 to 22 and the status record for
each as it runs (the coordinator edits the Status line of the phase in flight).

Web paths are relative to the sibling clone `C:\Users\Challa\git\GalactiLog`. Port paths are
relative to this repository. Every figure marked "estimate" is the planner's estimate from the
sizes of the web sources and the port's nearest code, not a measurement.

## 0. How this document is used

Approved by the user on 2026-09-17 as a whole, with the eight rulings in
`docs/superpowers/work/phase13/parity-roadmap-report.md` section 4 taken as proposed, including the
explicit reversal of spec 2.2 and 19.2 for Phase 22 behind an off-by-default switch. Each phase
runs under the standing process without a further plan review; the UI layout ruling that added
Phase 14C is in `docs/superpowers/work/phase13/parity-backlog.md` section 4.

One phase per session. The session reads `docs/superpowers/HANDOFF.md`, then
`docs/superpowers/TRACKING.md`, then the previous phase's lines at the tail of
`docs/superpowers/progress.md`, then its own phase section here, and opens the phase from its task
table. Nothing else is read before the brief-writer is dispatched.

The standing process, in order: spec-writer (the phase's first task; the user approves the spec
section before anything else runs), brief-writer, implementers one task at a time on disjoint
files, a per-task reviewer after each implementer, the phase reviewer over the whole diff, one
fixer, the verification agent on a clean copy running `dotnet build -c Release` with 0 warnings,
`dotnet test -c Release` green and the phase's launched-app bar, then one commit on `snd` and a
push. Reports go under `docs/superpowers/work/phaseN/`; every transition goes into `progress.md`
at once.

Agent count is unlimited. The coordinator chooses the model per task: haiku for mechanical edits
(markers, hash lines, renames), sonnet for bounded edits and documents, opus for layout,
view-model, query and review work, fable for the hardest design or cross-cutting task in a phase.
The Model column in every task table is the planner's proposal; the coordinator may change it and
records the change in `progress.md`.

Every dispatch follows the Subagent Dispatch and Return Discipline
(`C:\Users\Challa\.claude\rules\subagent-returns.md`): the dispatch names the report file, states
the role's return template (implementer: files changed one per line, suite verdict and count,
blockers; reviewer: verdict, severity tally, one line per blocker, rebuttal rulings; docs writer:
per-blocker disposition, escalations; verification agent: per-item verdict, deltas observed
against declared, deviations, blockers), includes the escape valve ("Anything surprising that fits
no template slot: one line prefixed ESCALATION. When in doubt, escalate.") and ends with "Nothing
else". Returns carry no diffs, code blocks, logs or narration. Every dispatch also carries the
prose rules of `documentation-style.md`: no em or en dashes, no emojis, manual tone.

The build lock in `docs/superpowers/work/phase13/phase13-inputs.md` section 4 applies to every
agent that runs `dotnet build`, `dotnet test` or `dotnet run`, filtered runs included. The lock
directory name changes per phase (`C:/tmp/p14-build.lock` and so on); a lock older than 20
minutes belongs to a dead agent and is reported as an ESCALATION and taken.

Every session ends by writing the next session's prompt into HANDOFF section 2 and handing it to
the user verbatim. The prompt is the fenced block at the end of the next phase's section here,
amended only if the session changed something the next session must know.

Nothing is deleted, moved or modified on disk outside app data or a path a save dialog returned
(HANDOFF section 4 rule 1, spec 2.1). Every write goes through `AppWriter`; every read of a user
path goes through `UserFiles`. A phase that seems to need more stops and asks the user, then
confirms again. `FileSafetyTest` stays green at every commit.

The repomix packs under `docs/reference/repomix/` are the citable copies of both repositories and
are refreshed at each phase close with the commands in `docs/reference/README.md`.

The user watches a progress tracker artifact at
https://claude.ai/artifact/YS7CCdEgU729Jufxn72J1a (title "GalactiLog Parity Tracker"; source
committed at `docs/superpowers/parity-tracker.html`). Every session updates the source file and
republishes the artifact with that url at each transition (task done, review in, phase committed,
blocker found, session close), copying dates and figures from `docs/superpowers/progress.md`. It
never publishes without the url, because a publish without it creates a separate page.

A phase moves every page it touches into the Observing Ledger vocabulary (`DESIGN.md`: flat
outlined controls, `Border.tag` badges, the type tiers, no card chrome), so the two-worlds seam
closes as the phases pass rather than as its own item: 14B closes Settings Library, Targets and
Maintenance, the log viewer, Activity, Diagnostics and the setup wizard; 15B closes Statistics;
14C closes the Dashboard's rows. A page a phase edits and leaves in the old vocabulary is a
phase-review finding (ui-layout-assessment ruling 7).

For the six pages the port has not built (Analysis, the Mosaics list and detail, the arranger,
the composite, the WBPP page, the Custom Columns tab) the web's region map is the starting
composition and the port's vocabulary is the finish. A departure from the web's region map needs
a recorded reason in the phase's spec section, as Phase 12's redesign recorded three
(ui-layout-assessment ruling 14).

Phase order and why: the audit gaps first (Phases 14A and 14B) because they touch pages every
later phase builds on and because Phase 14 lays two spines the later phases consume (14A the
night selection column on the ledger, 14B the background job registry); then 14C, the seven web
shell and dashboard idioms the layout assessment kept (Option A), which may run in parallel with
14A because it edits no file 14A or 14B rewrites; then the reopened areas in
imaging-workflow order: PHD2 guiding (15A, 15B), WBPP export (16), the analysis page (17), mosaics
(18, 19A, 19B), custom columns (20), integrations with the AstroBin CSV (21), the sky viewer (22).
A phase whose section here needed more than 260 lines is split into A and B phases, each its own
session, and the split is stated in the section.

Re-ordered by user ruling 2026-09-21: Phases 18, 19A and 19B (mosaics) are on hold until the user
says, and Phase 20 (custom columns) runs next after the scan abort fix, ahead of them, with 21 and
22 following in their stated order.

---

## Phase 14A: Audit gaps on the Target detail page

Goal: the Target detail page closes its eight audit gaps: contextual help on every heading
through one help spine (PAR-002), per-frame quality grading in robust MAD units against a chosen
baseline with a tally and cell deviation tooltips (PAR-003), per-rig presentation on a multi-rig
night (PAR-004), a Copy Frame List dialog with good and bad modes and three formats (PAR-006),
per-night and per-rig reference thumbnails (PAR-008), object type edit (PAR-009), the preview's
metadata strip and render-on-navigation option (PAR-011) and a session deep link (PAR-018). It
also lays the night selection column that Phases 16, 18 and 21 consume. Phase 14 is split: 14A is
this page, 14B is the dashboard, settings and shell gaps.

Status: DONE, `2ab40c8`, `5118` tests. Opened 2026-09-17, closed 2026-09-18; every
agent on opus by user directive. All eight tasks closed with their reviews and fix passes, the
phase review and the fixer pass closed, the fix-wave re-review's one P2 closed in a fix pass. Eight
PAR ids closed: PAR-002, 003, 004, 006, 008, 009, 011 and 018.

### What the web does

- `frontend/src/components/HelpPopover.tsx`: a help glyph beside a heading opens on hover after
  150 ms or on click, shows a titled paragraph, closes on outside click or Escape; about 83
  opening tags in 28 files (the audit's 222 counts imports and closing tags), on every page
  heading and most section headings. The port's glyph opens on click only (ui-layout-assessment
  ruling 10).
- `frontend/src/components/SessionAccordionCard.tsx` with `frontend/src/utils/frameQuality.ts`
  and `backend/app/services/frame_quality.py`: each metric cell is graded as a signed MAD z
  against the baseline of its `telescope|camera|filter` group (minimum group 8, uniform group
  ungraded); bands better (z at or below -1), neutral, watch (1.5 to 3), reject (3 and above); a
  "Compare to" control switches the baseline between this session and this rig overall; a tally
  states good, watch and reject counts and the mean per-frame score (signal, sharpness, roundness
  weighted 0.5, 0.25, 0.25, renormalised when an axis is null); each row is tinted by that score;
  hovering a cell reports the value, the z, the baseline median and the baseline mode; a legend
  states that no frame is deleted or hidden.
- The same card on a night imaged by more than one (telescope, camera): an "N rigs" pill, one
  colour-coded row per rig with its frame count, integration, HFR, eccentricity, FWHM and RMS,
  a frame table per enabled rig, rig pills on both charts (`RigTogglePills.tsx`) with each rig in
  its own dash pattern.
- `frontend/src/components/ReferenceThumbnail.tsx`: each night and each rig on a multi-rig night
  shows its own reference frame thumbnail beside its summary, "No thumbnail" when none, opening
  full screen on click.
- `frontend/src/components/FrameListModal.tsx`: for the checked sessions, a good or bad mode
  toggle, an include-unmeasured checkbox in good mode, a running tally (in list out of total,
  graded split, override count), a partial-load warning, and an output format of Windows
  Explorer search string or plain one-per-line file names. The move scripts and the browser move
  stay excluded (audit section 4).
- `frontend/src/pages/TargetDetailPage.tsx`: a pencil beside the object type swaps in a dropdown
  of object types, saved through `PUT /{target_id}/identity` (`backend/app/api/merges.py`).
- `frontend/src/components/FilePreviewModal.tsx`: pills under the path show verdict, filter and
  metric values with failing values marked and explained on hover; a "render full preview on
  navigation" checkbox makes every step render a fresh full-resolution preview.
- `TargetDetailPage.tsx` and `SessionAccordionCard.tsx`: a `session` query parameter expands that
  night's card and scrolls it into view.
- The Export menu and the session checkboxes select nights for the exports; the port's ledger
  has no selection column today.

### What the port has

- `src/GalactiLog.App/Views/TargetDetail/TargetDetailView.axaml` and
  `ViewModels/TargetDetail/TargetDetailViewModel.cs`: the ledger `ListBox` with `SelectedSession`,
  the identity line, the overflow flyout (Rename, Merge, Re-resolve), `CopyFrameListCommand`
  (absolute paths, no dialog), `RevealFolderCommand`.
- `src/GalactiLog.App/Controls/SessionPane.axaml` with `SessionCardViewModel.cs`: the three
  Phase 13 sections, the night strip, the filter table, the findings, session notes.
- `ViewModels/TargetDetail/FrameTableViewModel.cs` and `FrameRowViewModel.cs`: multiple
  selection, `Copy paths (n)`, Reveal, the outlier filter, `IsHfrOutlier`, `IsEccentricityOutlier`,
  `IsOutlier` only.
- `src/GalactiLog.Core/Metrics/FrameQuality.cs`: `MetricBaseline`, `GradedFrame`, `MinGroup` 8,
  `ZReject` 3.0, `GroupBaselines`, `MadZ`, `CountOutliers`: the grading math already exists in
  Core, used by the insight rules only. `src/GalactiLog.Data/Queries/RigBaselinesCache.cs`
  (5 minute TTL) holds the rig-wide baselines.
- `ViewModels/TargetDetail/TargetHeaderViewModel.cs` `ReferenceThumbnail`, one per target, and
  `Services/ThumbnailCache.cs` with `reference/`, `frames/` and `previews/`.
- `ViewModels/Preview/PreviewModalViewModel.cs` and `PreviewFrameViewModel.cs`: path, thumbnail
  and preview slots, zoom transform, header panel, Phase 13's focus-on-open.
- `ViewModels/TargetDetail/ChartSelectionViewModel.cs`: metric and filter toggles, `FilterTint`.
- 38 `ToolTip.Tip` attributes across all views, no help surface.

### Spec work

The spec-writer task amends `docs/design-spec.md`: 12.4 (grading bands, baseline toggle, tally,
tooltips, per-rig split, per-night thumbnails, the Copy Frame List dialog replacing the "no
dialog, no grading, no format picker" sentence, object type edit under Actions, the `Open(target,
night)` deep link replacing "selects the newest night on open", the night selection column); 11.5
(metadata strip, render-on-navigation checkbox); 13 (rig dash patterns on both charts); 5.8.1 and
5.8.2 (the keys below); 12.7 (the object type list the edit offers is section 9.8's category
list). A new section 12.12 "Contextual help" is the PAR-002 contract: the help spine, the glyph,
the topic table, the census. The user approves 12.4 and 12.12 before the brief-writer runs.

### Data model

No table and no migration. New settings keys, all in existing documents: `display.target_page`
gains `grading_baseline` (`session` or `rig`, default `session`), `frame_list_format`
(`explorer`, `names` or `paths`, default `paths`), `frame_list_mode` (`good` or `bad`, default
`good`) and `frame_list_include_unmeasured` (bool, default `true`); `general` gains
`preview_render_on_navigate` (bool, default `false`). Object type edit writes
`targets.object_type` and `targets.category` through `TargetWriteRepository` and emits a
`user_action` / `target_object_type_changed` activity event. Per-night thumbnails are generated
by `ThumbnailCache` under `frames/` for the night's reference frame (the night's best LIGHT by
the 11.4 rule) and written through `AppWriter`; nothing is written elsewhere. The Copy Frame List
dialog writes the clipboard only. Every write stays inside app data (rule 1).

### Task table

| Task | Model | Files | Verify |
| --- | --- | --- | --- |
| 1. Spec-writer: 12.4, 11.5, 13, 5.8.1, 5.8.2, 12.7 amended and 12.12 written from this section and the web files above; user approves 12.4 and 12.12. | opus | `docs/design-spec.md` | Zero em or en dashes; every PAR id of this phase named in its section; the 12.12 topic table lists every page and section heading of the port. |
| 2. Help spine (PAR-002): `Core/Help/HelpTopics.cs` (one table of topic id, title, paragraph, seeded from the web's popover texts), a `HelpButton` control opening a `Flyout` with the topic, its style in `Theme/Controls.axaml`, placed beside every page and section heading of every view; a census test that every `HelpButton` names a topic that exists and every 12.12 topic is placed once. | fable | `src/GalactiLog.Core/Help/*`, `src/GalactiLog.App/Controls/HelpButton.axaml(.cs)`, `Theme/Controls.axaml`, every `Views/**/*.axaml`, tests | Census test (expect on the order of the web's 83 placements, mapped to the port's headings); flyout opens on click, never on hover, and closes on Escape in a headless case; no view declares its own glyph style (`ControlStyleScanTest`). |
| 3. Quality grading (PAR-003): `FrameQuality` gains `BandForZ` and `CombinedScore`; `SessionDetailQuery` supplies per-frame z per metric for both baselines (session from the night's own frames, rig from `RigBaselinesCache`); `FrameRowViewModel` exposes band and tooltip per cell and the row score; the pane's Frames section gains the baseline toggle, the tally line and the legend sentence; cell ink by band from the theme's four semantic `*Value` colours. | opus | `Core/Metrics/FrameQuality.cs`, `Data/Queries/SessionDetailQuery.cs`, `SessionDetailModels.cs`, `App/ViewModels/TargetDetail/{FrameRowViewModel,FrameTableViewModel,SessionCardViewModel}.cs`, `Controls/SessionPane.axaml`, `Views/TargetDetail/FrameTableView.axaml`, `Core/Settings/DisplaySettings.cs` | Band table against the web's thresholds; the Phase 12 fixture night's two figures, 2 HFR and 1 eccentricity, are OUTLIER FLAG counts and not band counts, and the flag case pins them; the band ladder, the sign flip and the sparse and uniform cases are proved by synthetic groups, because a band needs a (telescope, camera, filter) group of at least 8 and the launched-app proof of a reject band is the Phase 14A fixture's worse night; toggle persists; tooltip text table; the existing outlier filter still equals the flags. |
| 4. Night selection and Copy Frame List (PAR-006, the selection spine): a check column on the ledger with select all and none in the header, `SelectedNights` on `TargetDetailViewModel` consumed by the dialog now and by Phases 16, 18 and 21 later; `FrameListDialog` as a `ModalPageWindow` page with mode, include unmeasured, tally, format; `Core/Text/FrameListFormats.cs` for the three formats. | opus | `Views/TargetDetail/TargetDetailView.axaml`, `ViewModels/TargetDetail/{TargetDetailViewModel,NightSelectionViewModel,FrameListDialogViewModel}.cs`, `Views/TargetDetail/FrameListDialogWindow.axaml(.cs)`, `Core/Text/FrameListFormats.cs` | Format table (Explorer string, names, paths) byte for byte; mode and unmeasured truth table; tally equals the grading counts; the dialog writes nothing (`FileSafetyTest`). |
| 5. Multi-rig and per-night thumbnails (PAR-004, PAR-008): `SessionDetail` carries rig groups; the facts line lists rigs; the filter table and ranges split per rig under a rig label row; the frame table gains a rig column and a rig pill filter; `ChartSelectionViewModel` gains rig toggles with dash patterns on both charts; the night header shows the night's reference thumbnail (per rig on a multi-rig night), a "No thumbnail" placeholder, click opens the preview. | opus | `Data/Queries/{SessionDetailQuery,SessionDetailModels}.cs`, `App/ViewModels/TargetDetail/{SessionCardViewModel,ChartSelectionViewModel,MetricChartViewModel,FilterTableRowViewModel}.cs`, `Controls/SessionPane.axaml`, `Services/ThumbnailCache.cs`, `ThumbnailWorker.cs` | Rig grouping on a two-rig fixture night; dash pattern per rig index; thumbnail key per night; a single-rig night renders exactly as before (existing tests green). |
| 6. Object type edit and deep link (PAR-009, PAR-018): an object type picker in the Details drawer beside the read-only value, writing through `TargetWriteRepository.SetObjectType`; `TargetDetailViewModel.Open(targetId, sessionDate?)` selecting that night when present, used by 14B's dashboard Deep dive and by later phases. | sonnet | `ViewModels/TargetDetail/{TargetHeaderViewModel,TargetDetailViewModel}.cs`, `Views/TargetDetail/TargetDetailView.axaml`, `Data/Repositories/TargetWriteRepository.cs`, `MainWindowViewModel.cs` | Category list equals 9.8; the write emits the activity event; open with a date selects it, without a date selects the newest. |
| 7. Preview strip and render option (PAR-011): metadata pills (filter, exposure, the graded metrics with band ink) on `PreviewFrameViewModel`, fed from the frame rows; a "Render full preview on navigation" checkbox bound to `general.preview_render_on_navigate`. | sonnet | `ViewModels/Preview/{PreviewModalViewModel,PreviewFrameViewModel}.cs`, `Views/Preview/PreviewModalWindow.axaml`, `Core/Settings/GeneralSettings.cs` | Pill text table; navigation requests a preview render when the flag is on and the thumbnail when off. |
| 8. Docs and handoff: spec deltas reconciled with what shipped, `DESIGN.md` gains the help glyph, the band inks and the rig row shape, HANDOFF sections 1, 2 and 8, TRACKING, the Status line here, the next-session prompt for 14B. | sonnet | docs only | Zero em or en dashes; the 14B prompt matches this document's block. |

Order: Task 1 first and approved; Tasks 2, 3, 4 and 7 start together on disjoint files; Task 5
after Task 3 (shares `SessionCardViewModel` and the pane); Task 6 after Task 4 (shares
`TargetDetailViewModel`); Task 8 last.

### Fixtures

As shipped. `docs/superpowers/work/phase14a/fixtures/` holds the recipe in force:
`GeneratePhase14AFixtures.cs` (the Phase 12 recipe with the Phase 13 night folded in and two
nights added, not part of the build), `README.md` with the regeneration command and every
assertable figure, and `manifest.sha256`. It produces `C:\tmp\p14a-fixtures`, **84 FITS files over
six nights**, one target: 2025-01-10 Ha 10; 2025-02-14 OIII 8; 2025-03-01 five filters over 10, the
night that grades nothing; 2025-03-05 SII 20 on two rigs, 10 frames each, the second rig's frames
at 96 by 64 so the aspect rule is falsifiable; 2025-03-12 Ha 14, the worse night, Good 8 Watch 2
Reject 4 at mean score 42 against its own baseline; 2025-03-20 Ha 22, still the newest, Good 13
Watch 4 Reject 5. The 50 files inherited from `C:\tmp\p13-fixtures` are byte-identical and the
Phase 12 and Phase 13 fixture folders were not edited. The recipe writes FITS headers and pixel
blocks only. Every fixture figure a verification step asserts comes from that README, not from a
phase review.

### Verification bar

Launched app against the extended fixture under a fresh `GALACTILOG_APPDATA`:

1. Every page and section heading carries a help glyph; clicking one opens the paragraph;
   Escape closes it; the paragraph names the baseline the section grades against.
2. The Frames section shows the tally; switching the baseline to "This rig" changes at least
   one cell's band on the fixture; hovering a rejected cell reports value, z, median, mode.
3. The two-rig night lists both rigs, splits the filter table, shows two thumbnails and draws
   two dash patterns on the night chart; the rig pill hides one rig's frames.
4. Check two nights, open Copy Frame List, switch to bad mode: the tally equals the reject
   count; each format pastes as specified.
5. Change the object type; the dashboard's Object Type filter groups the target under it.
6. Preview: pills present and updated on navigation; the render option re-renders per step.
7. Fixture hash identical before and after; profile folders untouched per HANDOFF 5.2 item 1.

The deep link has no launched-app route until 14B wires the dashboard; the headless case is its
proof in this phase.

The bar as run is `docs/superpowers/work/phase14a/phase-review.md`, "Notes for the verification
agent", which expands the seven items above into 32 steps with their preconditions, and whose
thumbnail steps open "Night detail" first because the user's B1 ruling put the strip there.

Verification status: `PASSED 2026-09-18 on two clean copies: 32 of 32 launched-app steps, 5118 tests green, 84-file fixture and both profile folders byte-identical; one blocker (the per-night rig series drew no line) fixed and re-verified`

### Size

Estimate: 9 to 11 agent-days, one session. PAR-002 and PAR-003 are the large items; the spine
work in Task 2 touches every view and is the reason for fable.

### Risks and rulings needed

1. Help surface (PAR-002): proposed answer is a `HelpButton` glyph beside each heading opening
   a `Flyout` on click from one `HelpTopics` table, with a census test; not a docked help pane,
   not F1, not hover-open (ruling 10 given). The table lives in Core so the CLI and tests can
   read it without Avalonia.
2. Grading units: the web grades in raw MAD units and calls them sigma in its help text.
   Proposed answer: the port grades in raw MAD units and its help text says "MAD units",
   correcting the web's wording; the thresholds stay the web's numbers.
3. Row tint: `DESIGN.md` refuses tinted fills. Proposed answer: the row score is shown as a
   narrow left rule in the band ink, not a fill; the cell ink carries the band.
4. Per-rig split doubles the filter table's rows on multi-rig nights. Proposed answer: a rig
   label row per rig; a single-rig night is unchanged.
5. The night selection column widens the 520 px ledger. Proposed answer: the checkbox replaces
   the row's leading gutter and the narrow column set drops nothing.
6. Copy Frame List formats against the port's existing absolute paths. Proposed answer: three
   formats with absolute paths as the default, so the existing behaviour is the dialog's default
   and `Copy paths (n)` on the frame table stays as it is.

### Next session prompt

```
Read docs/superpowers/HANDOFF.md, then docs/superpowers/TRACKING.md, then the P13 lines at the tail of docs/superpowers/progress.md, then docs/parity-roadmap.md section Phase 14A, and open Phase 14A from its task table.
```

---

## Phase 14B: Audit gaps on the dashboard, settings and shell

Goal: the remaining ten audit gaps close: manual target creation from scratch and from an
unresolved name (PAR-001), smart rebuild and catalog-identity backfill (PAR-007), the dashboard
session expander with filters and a Deep dive link plus a column gear on the table (PAR-010),
retention editors for activity and application logs (PAR-012), per-run scan scope and a guarded
orphan cleanup that touches rows only (PAR-013), the scan filter notice (PAR-014), a background
job monitor built as the job registry every later phase's long action registers with (PAR-015),
log save-as and clear (PAR-016) and the activity seen marker (PAR-017). PAR-005 waits for
Phase 21, which supplies the filter id map it needs.

Status: DONE, `a57943c`, `5634` tests (closed 2026-09-18)

### What the web does

- `frontend/src/components/settings/CreateTargetModal.tsx`, `TargetCreateForm.tsx`,
  `CreateTargetFromOrphanModal.tsx`, `backend/app/api/merges.py` (`POST /custom`,
  `POST /orphan-create`): a form with primary name, object type (deep sky and solar system
  groups plus a free text), RA and Dec in degrees (optional), catalog id, comma-separated aliases
  and a "user defined" checkbox; the backend refuses a name or catalog id already in use, creates
  the target name-locked, and retro-links pending orphan candidates whose source name matches,
  resolving their images.
- `backend/app/api/scan.py` (`POST /smart-rebuild-targets`, `POST /backfill-catalog-identity`)
  through `frontend/src/components/settings/MaintenanceSection.tsx`: smart rebuild repairs
  orphaned images, missing aliases, inconsistent names and stale merge candidates from the local
  database and the resolver cache with no network call; backfill re-runs the identity matcher
  over unlinked LIGHT frames and attaches those that now resolve to an existing target.
- `frontend/src/components/SessionTable.tsx` and `ColumnPicker.tsx`: the dashboard row's
  sessions table shows Date, Frames, Integration, Filters and custom columns, a gear button opens
  the column picker on the table, and each row has a Deep Dive link opening the target on that
  night.
- `frontend/src/components/settings/ActivityLogTab.tsx`, `backend/app/api/settings.py`
  (`GET`, `PUT /activity`): editors for `activity_retention_days`, `app_log_capture_level`,
  `app_log_retention_days` (default 14) and `app_log_max_rows` (default 50000).
- `frontend/src/components/ScanControls.tsx`: "All frames" against "Light frames only" per scan
  run, and a "Force orphan cleanup" checkbox explained with the 50 percent safety limit.
- `frontend/src/components/ScanFiltersOnboarding.tsx`: a notice on the dashboard and the Library
  tab until scan filters are configured, with a Review button that opens the filters panel.
- `frontend/src/components/JobMonitor.tsx` in `NavBar.tsx`, `backend/app/api/jobs.py`: every
  running and recently finished background job with its own progress and outcome.
- `backend/app/api/logs.py` (`GET /download`, `DELETE`) through `ActivityLogTab.tsx`: save the
  current log as a file, empty the log store.
- `backend/app/api/activity.py` (`POST /seen`), `frontend/src/components/ActivityFeed.tsx`:
  rows not yet looked at are marked; opening the feed clears the mark.

### What the port has

- `src/GalactiLog.App/ViewModels/Settings/TargetsTabViewModel.cs`, `UnresolvedNamesViewModel.cs`,
  `Data/Repositories/TargetWriteRepository.cs`, `TargetResolver.cs`: merge candidates, merge
  history, unresolved names with retry and assign-to-existing, rename history; no create.
- `ViewModels/Settings/MaintenanceTabViewModel.cs` with `Data/Ingest/TargetRebuild.cs`,
  `UnresolvedRetry.cs`, `Data/Maintenance/DatabaseReset.cs`,
  `Data/Repositories/CatalogMembershipMatcher.cs`: six actions, each reporting inline.
- `ViewModels/Dashboard/TargetRowViewModel.cs` (`SessionRowViewModel` with `DateText`,
  `FrameCount`, `IntegrationText`), `ViewModels/Settings/DisplayTabViewModel.cs` and
  `ColumnPickerViewModel.cs` (the picker on the Display tab), `Services/DisplayColumnWriter.cs`.
- `ViewModels/Settings/LibraryTabViewModel.cs` (`IncludeCalibration`, `RunScanCommand`),
  `Data/Ingest/ScanCoordinator.cs`, `OrphanPruner.cs` (this line was wrong: the port had only the
  zero-discovery guard here, `OrphanPruner.cs:75`'s own comment said "Spec 10.3's one guard", and
  the 50 percent safety limit was new code this phase wrote, which is why Task 5 moved to opus),
  `ViewModels/Setup/SetupWizardViewModel.cs` seeding the five `setup-exclude-*` rules.
- `ViewModels/StatusBarViewModel.cs` and `Services/ScanStatusService.cs`: the scan only.
- `ViewModels/Diagnostics/LogViewerViewModel.cs`: copy selection, copy all, open log folder;
  `Core/Io/AppWriter.cs` `BeginExport` and `Delete` (app data only); `Services/LogRingBuffer.cs`.
- `ViewModels/Activity/ActivityViewModel.cs`: filters, keyset paging, prune; no seen state.
- `SettingsStore.MutateGeneral` is the one write path for `general` keys.

### Spec work

Amend 12.7 (Targets tab gains Create target and Create from unresolved; Maintenance gains smart
rebuild and identity backfill with their exact row-level effects; Library gains the per-run scope
radio pair, the orphan cleanup action with the 50 percent limit and the "rows only" sentence, the
scan filter notice; Diagnostics gains the log retention editors beside the level), 12.2 (the
expander's Filters column and Deep dive link, the gear on the table), 12.6 (seen marker), 12.8
(save log as, clear log), the section 12 shell paragraph (the job monitor and the job registry),
5.8.1 (keys below), 9.7 (user-defined creation and the name-locked rule), 10.3 or 10.9 (orphan
cleanup as a scan option and its activity event). The user approves 12.7 and the shell paragraph.

### Data model

No table. One migration is not needed: the seen marker is a settings key, not a column. New
`general` keys: `app_log_retention_days` (int, default 14, the Serilog `retainedFileCountLimit`
in days, section 16.1), `app_log_max_rows` (int, default 50000, the ring buffer and viewer cap),
`activity_seen_at` (ISO datetime or null). The scan scope and orphan cleanup are per-run
arguments to `ScanCoordinator`, not stored keys; `include_calibration` stays the stored default
the radio pair starts from. Orphan cleanup deletes `images` rows for files absent from disk past
the limit and never touches disk (spec 2.1: "delete DB rows, never touch disk"). Log clear
deletes the rolled Serilog files under the app data log directory through `AppWriter.Delete`
and truncates nothing the sink holds open; save-as writes a new file at a save-dialog path
through `AppWriter.BeginExport`, the same class of write as the diagnostics bundle (2.1.1).
Target creation writes `targets` and, for the orphan case, `images.resolved_target_id` and the
`merge_candidates` row status. Every write stays inside app data or the dialog path (rule 1).

### Task table

| Task | Model | Files | Verify |
| --- | --- | --- | --- |
| 1. Spec-writer: 12.7, 12.2, 12.6, 12.8, the 12 shell paragraph, 5.8.1, 9.7 and 10.9 amended; user approves 12.7 and the shell paragraph. | opus | `docs/design-spec.md` | Zero em or en dashes; every PAR id of this phase named; every new key in 5.8.1. |
| 2. Job registry and monitor (PAR-015, the job spine): `Services/JobRegistry.cs` (register, progress, outcome, recent list with a 10 entry cap) that `ScanStatusService`, every Maintenance action, `ThumbnailWorker`'s regenerate passes and later phases' passes register with; the status bar shows the running count and a flyout listing each job with progress and outcome. | opus | `App/Services/JobRegistry.cs`, `ScanStatusService.cs`, `ViewModels/StatusBarViewModel.cs`, `Views/StatusBarView.axaml`, `ViewModels/Settings/MaintenanceActionViewModel.cs` | Two concurrent jobs listed; outcome kept after completion; the scan's existing cancel still works; a census that every long action registers. |
| 3. Target creation (PAR-001): `TargetWriteRepository.CreateUserDefined` with the name and catalog id conflict checks and orphan retro-link; a Create target form on the Targets tab (name, object type from 9.8's list plus free text, RA, Dec, catalog id, aliases); "Create target" on each unresolved name row pre-filling the name. | opus | `Data/Repositories/TargetWriteRepository.cs`, `Data/Queries/UnresolvedNamesQuery.cs`, `App/ViewModels/Settings/{TargetsTabViewModel,UnresolvedNamesViewModel,CreateTargetViewModel}.cs`, `Views/Settings/{TargetsTabView,UnresolvedNamesView,CreateTargetView}.axaml` | Conflict table (name, alias, catalog id); created row is `name_locked` and `user_defined`; the orphan case links its frames and closes the candidate; coordinate parse bounds. |
| 4. Smart rebuild and identity backfill (PAR-007): `Data/Ingest/SmartRebuild.cs` (orphaned images, missing aliases, inconsistent names, stale candidates, cache-only) and `CatalogIdentityBackfill.cs` over `CatalogMembershipMatcher` and `TargetResolver`'s identity match; two Maintenance actions registered with the job registry. | opus | `Data/Ingest/{SmartRebuild,CatalogIdentityBackfill}.cs`, `App/ViewModels/Settings/MaintenanceTabViewModel.cs`, `Views/Settings/MaintenanceTabView.axaml` | Each repair class on a seeded database; no network call (a resolver stub asserts none); counts reported; `targets` rows never deleted. |
| 5. Scan scope, orphan cleanup, filter notice (PAR-013, PAR-014): the scope radio pair and the cleanup checkbox with its explanation on the Library tab's scan control, passed to `ScanCoordinator` per run; `OrphanPruner` takes a force flag past the limit; a notice on the dashboard and the Library tab while the stored rules are only the five seeded ones, with a Review button opening the rule editor. | sonnet | `Data/Ingest/{ScanCoordinator,OrphanPruner}.cs`, `App/ViewModels/Settings/LibraryTabViewModel.cs`, `Views/Settings/LibraryTabView.axaml`, `ViewModels/DashboardViewModel.cs`, `Views/DashboardView.axaml` | Scope reaches the header pass; force flag deletes rows past the limit and touches no file (`FileSafetyTest`); notice visibility truth table. |
| 6. Dashboard expander and column gear (PAR-010): `SessionRowViewModel` gains `Filters` and a Deep dive command calling `MainWindowViewModel.OpenDetail(groupKey, sessionDate)`, 14A's already-published deep link, reached from the application for the first time; a gear button on the target list header opening the existing `ColumnPickerViewModel` in a flyout, the Display tab picker kept. | sonnet | `App/ViewModels/Dashboard/{TargetRowViewModel,TargetListViewModel}.cs`, `Views/Dashboard/TargetListView.axaml`, `Data/Queries/TargetListingQuery.cs` | Filters text per night; Deep dive opens the named night; the flyout writes `display.columns.dashboard` through `DisplayColumnWriter`. |
| 7. Logs and activity (PAR-012, PAR-016, PAR-017): retention editors on the Diagnostics tab writing the two new keys and `activity_retention_days`, applied to the Serilog sink the next time GalactiLog starts (this line's "next roll" was corrected: Serilog bakes `retainedFileCountLimit` into the sink at construction) and to the viewer cap at once; Save log as (dialog, `BeginExport`) and Clear log (rolled files only) on the log viewer; `activity_seen_at` with a marker on newer rows and a write on feed open. | sonnet | `ViewModels/Diagnostics/{LogViewerViewModel,DiagnosticsViewModel}.cs`, `Views/LogViewerView.axaml`, `ViewModels/Activity/{ActivityViewModel,ActivityRowViewModel}.cs`, `Views/ActivityView.axaml`, `Core/Settings/GeneralSettings.cs`, `AppHost.cs` (sink configuration) | Editors round trip and validate ranges; save-as writes one new file at the dialog path; clear removes rolled files only; marker truth table across two opens. |
| 8. Docs and handoff: spec reconciled, HANDOFF, TRACKING, the Status line, the 14C prompt (or the 15A prompt if 14C already ran in parallel with 14A). | sonnet | docs only | Zero em or en dashes; the next prompt matches. |
| 9. Setup wizard and Settings shell vocabulary (added by the coordinator's ruling on `questions.md` Q4, option 1; not in the original eight-row table, because roadmap section 0's two-worlds sentence names the setup wizard as one of the seven surfaces 14B closes and the eight-row table gave it to nobody): a vocabulary-only, no-behaviour-change move of `SetupWizardWindow.axaml` into the Ledger vocabulary; `SettingsView.axaml` read and confirmed already clean rather than edited, which corrected carried item 27's listing of it. | sonnet | `Views/Setup/SetupWizardWindow.axaml`, `Views/Settings/SettingsView.axaml` | `grep -c CornerRadius` 0 on both; `ControlStyleScanTest` and `FontSizeTokenTest` green; every `x:Name` kept; every help glyph kept; the existing view tests green. |

Order: Task 1 first; Task 2 next alone (the registry is a seam for Tasks 4 and 5); then Tasks 3,
5, 6, 7 and 9 together on disjoint files (Task 9 added mid-phase, collision-free with every other
task, so it joined the same wave as soon as its brief was written); Task 4 after Task 3 (shares the
repositories); Task 8 last, after every other task's review, fix pass, the phase review, the fixer
pass and the verification agent.

### Fixtures

The Phase 12 fixture plus the 14A additions cover the dashboard expander and the deep link. Two
additions to the recipe under `docs/superpowers/work/phase14b/fixtures/`: a folder of frames
whose `OBJECT` is a name the offline catalogue cannot resolve ("Comet C/2026 X1") for PAR-001's
orphan case, and a second copy of a night under a folder the verification agent removes from a
mirror of the fixture (never from the fixture itself) to exercise the orphan cleanup on missing
files. The recipe can produce both; the removal is the agent's on its own mirror.

### Verification bar

1. Create a target "Comet C/2026 X1" from the unresolved name; its frames move to it; the
   dashboard lists it under the chosen object type; a second create with the same name is
   refused with the conflict sentence.
2. Run smart rebuild and identity backfill; each reports its counts inline and in the job
   monitor; the Activity feed records both; the network stays silent (no resolver events).
3. **Amended after verification (D5): no two user-startable jobs can be made to overlap on this
   phase's build.** Every Maintenance action is gated behind the scan, and every job other than a
   scan finishes in under a second on both the fixture and a 570-file real library, so "start a
   scan and a thumbnail regeneration together" is not producible as written. What is producible,
   and is the bar from here: two jobs finish in sequence, each keeping its own outcome word and
   summary in the flyout's recent list, the flyout button stays reachable while the recent list is
   non-empty even with nothing running, and the registry's two-simultaneous-entry contract is
   proven headless by `JobRegistryTests` rather than by a launched overlap.
4. **Amended after verification (B3): the whole-folder-removed shape reaches the zero-discovery
   guard, not the 50 percent limit.** On the mirror, removing 5 of a night's 8 files (not the
   whole folder) with the cleanup box off keeps the rows past the limit and reports
   `orphan_prune_limited`; with the box on, the 5 rows go and `orphan_prune_forced` is reported;
   the fixture itself is unchanged by hash. Removing the whole folder instead exercises the
   zero-discovery guard: `orphan_prune_skipped` fires and nothing is deleted, box on or off,
   because the cleanup override reaches the 50 percent limit only.
5. The filter notice shows on a fresh profile and goes after a rule is edited; Review lands on
   the rule editor.
6. Expand a dashboard row: Filters per night; Deep dive opens the target on that night.
7. Set application log retention to 2 days and the row cap to 5000; Save log as writes one
   file; Clear log leaves the live file; the Diagnostics field reflects each.
8. Open Activity after a scan: the new rows carry the marker; reopen: they do not.
9. Fixture hash identical before and after; profile folders untouched per HANDOFF 5.2 item 1.

### Size

Estimate: 8 to 10 agent-days, one session. PAR-001 and PAR-007 are the medium-to-large items;
the job registry is small in code and wide in touch points.

### Risks and rulings needed

1. The job registry (PAR-015) is a spine built on the second occurrence of the pattern (scan
   status plus the maintenance actions) because Phases 15, 18, 19 and 22 add passes. Proposed
   answer: build it here as `JobRegistry` in App with a census test, before the third occurrence.
2. Orphan cleanup wording: the web calls it cleanup and moves nothing on disk either. Proposed
   answer: the port's checkbox reads "Remove catalogue rows for missing files past the safety
   limit" and its help paragraph states that no file is touched.
3. `app_log_max_rows` against the viewer's 50,000 copy cap (12.8). Proposed answer: the key
   bounds the ring buffer and the viewer; the copy cap becomes `min(50000, app_log_max_rows)`.
4. The dashboard column gear duplicates the Display tab picker. Proposed answer: one
   `ColumnPickerViewModel` instance shown in two places; the Display tab keeps its copy.
5. User-defined targets and the resolver: a later scan must not re-resolve a name-locked target.
   Proposed answer: 9.7's name-locked rule is stated for user-defined rows and pinned by a test.

### Next session prompt

```
Read docs/superpowers/HANDOFF.md, then docs/superpowers/TRACKING.md, then the P14A lines at the tail of docs/superpowers/progress.md, then docs/parity-roadmap.md section Phase 14B, and open Phase 14B from its task table.
```

The prompt this phase hands on points at Phase 14C (its own block below), unless 14C already ran
in parallel with 14A, in which case it points at 15A.

---

## Phase 14C: Shell and dashboard idioms from the web

Goal: the seven web idioms the layout assessment kept under Option A
(`docs/superpowers/work/phase13/ui-layout-assessment.md` section 5, rulings 3 to 8 and 13 of
section 7) land in the shell and the dashboard: the filter panel collapses to a 48 px initials
strip and is otherwise draggable between 220 and 480 px with the width persisted; dashboard rows
become flat rule-separated rows on the shared-size spine, retiring the per-row cards and the
seven fixed widths; a numbered pager with a page-size select sits at the top of the list and
writes `general.default_page_size`; the list dims and disables during a filtered refetch instead
of being replaced; the Imaging timeline gains wheel zoom and drag pan; `MainWindow` gains a
minimum size of 1024 by 700; scrollbars become thin, themed and keep a stable gutter. One docs
task records the hero-tile refusal's Target-detail-only scope in `DESIGN.md`. This phase edits
no file 14A or 14B rewrites and may run in parallel with 14A.

Status: DONE, commit `ed85af0` on `snd`, `5873` tests at the close. Run
2026-09-18 to 2026-09-19 as the third roadmap session, not in parallel with 14A. Eight tasks rather
than six: user rulings U1 and U4 added Tasks 7 and 8 mid-phase, and user ruling U3 handed Task 3
whole to a separate team. Two of the section's three proposed rulings were reversed during the
phase (see Risks and rulings needed below).

### What the web does

- `frontend/src/components/Sidebar.tsx`, `SidebarRail.tsx`, `SidebarResizeHandle.tsx`,
  `sidebarLayout.ts`: the dashboard's filter sidebar collapses to a narrow rail of section
  initials and, when open, is dragged to a width inside a clamped range that is remembered;
  clicking an initial on the rail opens the sidebar on that section.
- `frontend/src/components/TargetTable.tsx` and `TargetRow.tsx`: the target list is a table of
  flat rows separated by rules, columns sized by the table, no card around a row; the expanded
  sessions sit under the row inside the same rule pair.
- `frontend/src/pages/DashboardPage.tsx` and `TargetFeed.tsx`: a pager with numbered page
  buttons, previous and next, and a page-size select above the rows, writing
  `default_page_size`; while a filter change refetches, the current rows stay on screen dimmed
  and inert, and the new rows replace them when the response lands.
- `frontend/src/components/ImagingTimeline.tsx`: scroll or pinch zooms between monthly, weekly
  and daily bars and drag pans the axis (spec 12.5 already records the granularity and presets).
- The web's window is a browser; the assessment's section 2 item 8 sets the port's minimum from
  the measured breakpoints instead.
- `frontend/src/index.css`: thin scrollbars in theme colours with the gutter reserved so
  content does not shift when a scrollbar appears.

### What the port has

- `src/GalactiLog.App/Views/Dashboard/FilterPanelView.axaml` and `ViewModels/Dashboard/FilterPanelViewModel.cs`:
  the panel collapsed behind a Filters button (Phase 11), hidden when collapsed, fixed width.
- `Views/Dashboard/TargetListView.axaml` and `ViewModels/Dashboard/{TargetListViewModel,TargetRowViewModel}.cs`:
  per-row card borders, seven fixed column widths, the bottom pager (previous and next), the
  list replaced on refetch, `general.default_page_size` read at 50.
- `Views/TargetDetail/TargetDetailView.axaml`: the Phase 13 R7 ledger strip (48 px, initials,
  accent on the open item) whose item template the filter strip shares.
- `Views/MainWindow.axaml`: no `MinWidth` or `MinHeight`; the shipped 1280x800 window.
- `Views/StatisticsView.axaml` and `ViewModels/Stats/ImagingTimelineViewModel.cs`: the timeline
  with granularity, presets and the gap toggle, no wheel or drag.
- `Theme/Controls.axaml` and the three dictionaries: Fluent's scrollbar thumbs, unthemed in Red
  Light (roadmap Phase 12 candidate 7c).
- `Core/Settings/DisplaySettings.cs` `display.target_page` for the persisted booleans.

### Spec work

Amend 12 shell (the window minimum, the scrollbar rule), 12.2 (the panel's two states and the
width clamp, the flat rows on the shared-size spine, the pager at the top with its page-size
select, the refetch dim), 12.5 (wheel zoom and drag pan on the timeline), 14 (the thin scrollbar
tokens), 5.8.1 (`default_page_size` now written by the select), 5.8.2 (keys below). `DESIGN.md`
section 8 gains one sentence scoping the hero-tile refusal to Target detail (ruling 8) and
section 3 or 6 gains the scrollbar and splitter shapes. The user approves 12.2 before the
brief-writer runs; the rulings are already given, so approval is a read.

### Data model

No migration. `display.dashboard` gains `filter_panel_expanded` (bool, default true) and
`filter_panel_width` (int pixels, default 300, clamped 220 to 480 on read and write);
`general.default_page_size` is written by the select from the choice list 25, 50, 100, 250.
Every write is `user_settings` (rule 1).

### Task table

| Task | Model | Files | Verify |
| --- | --- | --- | --- |
| 1. Spec-writer: 12 shell, 12.2, 12.5, 14, 5.8.1 and 5.8.2 amended; `DESIGN.md` section 8 sentence drafted; user reads and approves 12.2. | sonnet | `docs/design-spec.md`, `DESIGN.md` | Zero em or en dashes; the clamp, the two states and the pager position stated. |
| 2. Filter panel: the 48 px initials strip as the collapsed state sharing the R7 ledger strip's item template (click on an initial expands on that section), a `GridSplitter` between panel and list with the width clamped 220 to 480 and persisted, the expanded state persisted. | opus | `Views/Dashboard/{FilterPanelView,DashboardView}.axaml(.cs)`, `ViewModels/Dashboard/FilterPanelViewModel.cs`, `ViewModels/DashboardViewModel.cs`, `Core/Settings/DisplaySettings.cs`, `Theme/Controls.axaml` (the shared strip template lifted from `TargetDetailView.axaml`) | Strip items equal the filter sections; click expands on the section; clamp at both ends; width and state round trip; the ledger strip still renders from the shared template. |
| 3. Flat rows: the target list as rule-separated rows on a `SharedSizeGroup` spine, the per-row `Border` cards and the seven fixed widths retired, the expander inside the same rule pair, the row's badges on `Border.tag` and its buttons on the shared vocabulary. | opus | `Views/Dashboard/TargetListView.axaml`, `ViewModels/Dashboard/TargetRowViewModel.cs` | No fixed column width remains (a source scan); columns align across rows after layout; `ControlStyleScanTest`; the row's existing tests green. |
| 4. Pager and refetch: numbered page buttons with previous and next and a page-size select at the top of the list, the select writing `general.default_page_size` through `MutateGeneral`; a filtered refetch keeps the rows on screen dimmed and disabled until the new page lands. | sonnet | `Views/Dashboard/TargetListView.axaml`, `ViewModels/Dashboard/TargetListViewModel.cs`, `ViewModels/DashboardViewModel.cs` | Page button set for 1, 5 and 40 pages; the select writes the key; `IsRefetching` truth table; keyboard focus survives the refetch. |
| 5. Timeline, window minimum, scrollbars: wheel zoom stepping the granularity about the cursor and drag panning the axis on the timeline; `MinWidth` 1024 and `MinHeight` 700 on `MainWindow`; a thin scrollbar style in `Theme/Controls.axaml` with themed thumb and track tokens in all three dictionaries and a reserved gutter on every page scroller. | sonnet | `Views/StatisticsView.axaml(.cs)`, `ViewModels/Stats/ImagingTimelineViewModel.cs`, `Views/MainWindow.axaml`, `Theme/Controls.axaml`, `Theme/Themes/*.axaml` | Wheel steps monthly to weekly to daily and back; drag moves the visible range; the window refuses a smaller size in a headless case; the theme census counts the new keys in all three dictionaries. |
| 6. Docs and handoff: `DESIGN.md` section 8 sentence and the strip, splitter and scrollbar shapes; spec reconciled; HANDOFF, TRACKING, the Status line, the 15A prompt. | sonnet | `DESIGN.md`, docs only | Zero em or en dashes; the 15A prompt matches. |
| 7. **Added mid-phase by user ruling U1.** The night strip's active tick: the tick under the pointer, and at rest the tick of the frame the table has selected or the preview has open, draws taller than its neighbours past the band at both ends, at 2 px in the primary ink, with a drawn caret on the axis under it; every other tick untouched. | sonnet | `Controls/NightStrip.cs`, `ViewModels/TargetDetail/NightStripViewModel.cs`, `ViewModels/TargetDetail/{FrameTableViewModel,SessionCardViewModel}.cs`, one brush attribute in `Controls/SessionPane.axaml`, their tests | With 200 ticks at the shipped pane width the active tick's drawn rectangle differs from an inactive tick's in both height and ink, asserted on the render geometry through an internal accessor `Render` itself consumes; a hover repaints; an unbound `ActiveBrush` degrades rather than blanking the tick. |
| 8. **Added mid-phase by user ruling U4.** The three timezone selectors read their GMT offset first and a display name second, ordered negative offsets, GMT, positive offsets, from one shared option builder replacing three hand-built lists. | sonnet | `ViewModels/Settings/LocationTabViewModel.cs`, `ViewModels/Setup/ObserverLocationStepViewModel.cs`, their tests | The offset is the zone's standard offset, so no calendar date changes the order; the order is offset then displayed name, ordinal ignoring case; the special entries keep their places and wording; the stored value is still the zone id; a source scan census over every `TimezoneOption` construction site. |

Order as run: Task 1; Tasks 2, 3, 5 and 7 together, with Task 8 joining that wave when U4 arrived;
Task 4 after Task 3; Task 6 last, beside the four phase fixers. Tasks 2 and 3 do **not** share
`DashboardView.axaml`: every row-level concern lives in `Views/Dashboard/TargetListView.axaml`, a
different file. The real shared file is `Theme/Controls.axaml`, appended by Tasks 2, 4 and 5 in that
order with Task 2's ruling E2 lift as its first edit.

**Task 3 ran as a separate team under user ruling U3** ("do these with a different team"): all four
of the user's row directives landed in the two files that were this row's entire file set, so a
second team beside Task 3 would have collided in one file and built the rows twice. Task 3 was
handed whole to the team, with its own lead (who amended spec 12.2's row paragraphs and wrote
`work/phase14c/task3.md` and `task3-questions.md`), its own implementer and its own reviewer. The
main team kept Tasks 2, 4, 5, 6, 7 and 8.

**Eleven Files entries above are wrong against the tree.** They are left as the plan recorded them;
`work/phase14c/collision-map.md` section 7 has the file-by-file correction, and four of the eleven
shaped a brief: neither dashboard code-behind carried any logic to edit (Task 2 needed
`FilterSectionViewModel.cs`, `TargetDetailView.axaml` and `SessionCardViewModel.cs`, none of them
listed); the Imaging timeline is a LiveCharts `CartesianChart` with no drawn control and no
code-behind handler, and its view-model had no visible-range member at all (Task 5);
`TargetListViewModel` had no general-document seam, no `MutateGeneral` and no refetch flag, and the
listing record's member is `TotalGroups`, not `TotalCount` (Task 4); and the night strip had no
inbound channel of any kind, every wire running strip to host, so two more files were unavoidable
(Task 7). Also corrected there: only six of the "seven fixed widths" carry the `TargetColumn*Width`
prefix, the theme census pins the tokens and not a control theme, and Avalonia has no
`scrollbar-gutter` equivalent to reserve with.

### Fixtures

The Phase 12 fixture has one target, which exercises no pager. One addition under
`docs/superpowers/work/phase14c/fixtures/`: 120 single-frame targets with distinct catalogue
names resolving offline, so the pager shows several pages at every page size. The Phase 12
recipe produces it with a name list.

### Verification bar

**NOT RUN. User ruling U5 of 2026-09-19** ("skip this in the future", quoting the tracker's
sub-step "The running app checked screen by screen, in all three themes") removed the
screen-by-screen walk of the running application and the per-theme repetition, in this phase and
every later one. What ran in its place on the clean copy: `dotnet build -c Release` with 0
warnings, the full suite, the headless CLI scan, and the byte-identity proof over
`C:\tmp\p14c-fixtures`, both profile folders and the user's own files. Of the seven items below,
five were covered instead by an implementer's one targeted look at the screen it changed, and item
1's own second clause is superseded by a ruling taken after it was written. Two things were seen by
nobody on a launched application and are recorded as such in HANDOFF section 7: the 48 px filter
strip beside the new dashboard rows, and the application at the 1024 by 700 minimum.

1. Dashboard on a fresh profile: the panel open at 300 px; drag it to 220 and to 480 and no
   further; collapse it: a 48 px strip of initials remains; click an initial: the panel opens on
   that section; relaunch keeps the width and the state. **The drag-to-480 clause is superseded**:
   the panel's rendered width is now bounded by what the target list needs, so 480 is first
   reachable at about 1456 px of window and the shipped 1280 window drags between 220 and about
   304. The stored width is unchanged by the bound.
2. The rows are flat with a rule between them and no card; the columns align down the list;
   expanding a row keeps the alignment; Red Light and Glass Void render the rows.
3. The pager at the top shows numbered pages for 120 targets at 50 per page; choosing 25
   writes the key (visible on the Display tab) and repaginates; changing a filter dims the rows
   until the new page lands, with nothing replaced in between.
4. Statistics: wheel over the timeline steps the granularity; drag pans; the presets still work.
5. Resize the window below 1024 by 700: it stops at the minimum.
6. Every page's scrollbar is thin and themed in all three themes and the content does not shift
   when one appears.
7. Profile folders untouched per HANDOFF 5.2 item 1; the fixture hash identical.

### Size

Estimate: 4 to 5 agent-days, one session. The assessment's own costing (1.5 to 2 for the panel,
1 to 1.5 for the rows, 0.5 for the pager, four quarter-day items) is kept.

### Risks and rulings needed

The fourteen rulings of ui-layout-assessment section 7 are given as proposed; none is open.
Residual risks:

1. `GridSplitter` inside the dashboard's grid with a `ListBox` on the shared-size spine can
   re-measure every row on each drag tick. Proposed answer: the splitter drags a proxy and
   commits the width on release, which is the pattern the brief names. **Ruled E1 as proposed and
   it stood**: `ShowsPreview="True"` plus a commit read from the panel column's own
   `WidthProperty` change, clamped 220 to 480 on write and again on read.
2. The shared strip template lifted from `TargetDetailView.axaml` into `Theme/Controls.axaml`
   changes a Phase 13 file that 14A also edits. Proposed answer: Task 2 lifts the template in
   one small commit-sized edit first and 14A rebases on it; the coordinator orders the two
   sessions accordingly if they run in parallel. **Ruled E2 as proposed and it stood**; the rebase
   half was moot, because 14A was closed and no parallel session existed. The lift as the first
   edit in the first build window also served as the serialization device for the two later tasks
   that appended to the same file.
3. Fluent's scrollbar template exposes limited parts. Proposed answer: a full `ScrollBar`
   control theme in `Controls.axaml` rather than setter overrides, pinned by the theme census.
   **Ruled E3 as proposed and REVERSED at the phase review.** The hand-written template hardcoded
   `IsDirectionReversed="False"` for both orientations, inverting every vertical scrollbar in the
   application, and dropped the platform's auto-hide states; both were green past the suite, the
   census and three task reviews, and both were found on a screenshot. What ships keeps the platform
   Fluent theme and restyles it through its own resource keys, declared as `StaticResource` aliases
   in each of the three dictionaries beside the three new tokens, with `ScrollBarSize` 8.
4. Added during the phase and also reversed: **the reserved scrollbar gutter** the Spec work
   paragraph above and the approved 12.2 text both promised. Avalonia has no `scrollbar-gutter`
   equivalent, and the one global mechanism that approximated it moved five pinned layouts by 8
   pixels. The scrollbar is an 8 px themed overlay that takes no layout space, so content never
   shifts when a bar appears, which is what verification item 6 below actually asks for.

### Next session prompt

This section's own opening prompt, kept as the record of how Phase 14C was opened. Under section
0's rule the prompt that opens the **next** session is the fenced block at the end of the next
phase's section, so the one HANDOFF section 2 now carries is the Phase 15A section's, below.

```
Read docs/superpowers/HANDOFF.md, then docs/superpowers/TRACKING.md, then the P14B lines at the tail of docs/superpowers/progress.md, then docs/parity-roadmap.md section Phase 14C, and open Phase 14C from its task table.
```

---

## Phase 15A: PHD2 guiding, discovery, parsing, storage and correlation

Goal: the library scan discovers PHD2 guide logs beside the frames, parses them in Core into
sessions, frames and calibrations, stores them in four new tables, maps PHD2 equipment profiles
to the port's telescopes with an optional per-profile timezone and site, fills each LIGHT frame's
guiding RMS from the guide log when no NINA CSV sidecar supplied it, and reports the pass in the
job monitor and the Activity feed. Phase 15 is split: 15A is the data path, 15B is the guiding
section on the night pane, the guide graph and the Statistics guiding section.

Status: DONE, commit `2dee235` on `snd`, `6452` tests at the close.
Run 2026-09-19 as the fourth roadmap session. Eight
roadmap tasks ran as thirteen task units: Tasks 4, 5 and 6 each split into a core half and a
wiring half at the user's mid-phase directive to use more agents, so a contended file (the
migrated schema, the `ScanCoordinator` hook, `AppHost`) did not serialise the whole phase behind
one implementer. The phase review was FIX-FIRST, P1 0, P2 7, P3 12, over 127 changed paths,
dispatched to four file-disjoint fixers (A the pass and its two triggers, B the write predicate
and the repository, C the parser, D the profiles panel and the ledger mark), all four closed. The
session's own residual risks grew from four to eighteen coordinator rulings as the data path's
edges (the session-time re-derive, the out-of-scan trigger, the failed-correlation event, the
durable re-run flag, the key-off behaviour) turned out to need more than the roadmap anticipated;
HANDOFF 5.1 item 10 lists all eighteen for the user.

### What the web does

- `backend/app/services/scanner.py`: files named `PHD2_GuideLog_*.txt` (never the debug logs)
  are reported to a callback during the walk; `backend/app/worker/tasks_phd2.py`
  `scan_phd2_logs` ingests each log after the frame pass, skipping an unchanged file by size and
  mtime, recording an unusable file as `failed` with its error so it is not re-read, dropping
  rows for logs gone from disk, and writing `phd2_found`, `phd2_ingested`, `phd2_failed`
  counters on the scan run.
- `backend/app/services/phd2_parser.py` (811 lines, pure text): segments stacked runs on the
  `PHD2 version` banner (ASIAIR builds omit the version and platform), reads the equipment
  header lines (profile, pixel scale, focal length, camera, exposure, mount, algorithms, minimum
  move, aggression, backlash, dec guide mode, guide speeds, last calibration issue, pointing
  with pier side, altitude, azimuth, hour angle, lock position), the guiding CSV rows (frame,
  time, dx, dy, RA and Dec raw and guide distances, pulse durations and directions, star mass,
  SNR, error code, DROP rows), the events (settle state changes, dithers, lock position, star
  lost, parameter changes) and calibration blocks with their per-step series.
- `backend/app/services/phd2_metrics.py`: converts naive wall-clock timestamps to UTC through
  the profile's timezone, excludes dither and settle windows from RMS, drops excursions past
  5 sigma once for the filtered RMS, computes RMS RA, Dec and total in arcseconds from pixels
  and the pixel scale, peak errors, drop count, longest drop run, unguided seconds, dither and
  settle counts, failed settles, median settle time, star-lost reasons; `MIN_FRAMES` 100 gates a
  session out of every average; night rollups are frame-count weighted.
- `backend/app/services/phd2_profiles.py`: the profile map's one canonical shape: profile name
  to telescope (or unmapped), timezone (empty inherits the observer zone), latitude and
  longitude (null inherits the site); rewritten when equipment is renamed.
- `backend/app/services/phd2_correlation.py`: for every night with an unfilled image, picks the
  guiding sessions belonging to that image's rig (mapped telescope, or the night's sole unmapped
  profile), pools the frames overlapping each exposure's window and writes
  `guiding_rms_ra_arcsec`, `guiding_rms_dec_arcsec`, `guiding_rms_arcsec` with
  `guiding_rms_source` `phd2`; never overwrites a CSV value; warns once per pass naming profiles
  with no configured timezone.
- `frontend/src/components/settings/Phd2ProfilePanel.tsx`: a table of discovered profiles
  (name, guide camera, focal length, pixel scale, session count, first and last seen) with a
  telescope picker, a timezone picker and latitude and longitude fields per row; a guide-log
  scan toggle (`phd2_scan_enabled`).
- `SessionAccordionCard.tsx`: a dagger after a guiding RMS that came from a guide log, with the
  source in the tooltip.

### What the port has

- `src/GalactiLog.Core/Scanning/FileWalker.cs` and `Data/Ingest/ScanCoordinator.cs`,
  `ScanPipeline.cs`, `ScanWriter.cs`: the walk, delta classification by size and mtime, the
  header pass, the scan run counters.
- `Core/Metadata/NinaCsvReader.cs` and `Provenance.cs`: the CSV sidecar path that writes the
  three guiding columns with `guiding_rms_source` `csv`; `Data/Entities/Image.cs` already
  carries `GuidingRmsSource`.
- `Core/Sessions/SessionDate.cs` and `AstroNight.cs`: the imaging night rule and the observer
  timezone; `Core/Settings/GeneralSettings.cs` `ObserverTimezone`, `ObserverLatitude`,
  `ObserverLongitude`.
- `Core/Aliases/AliasMap.cs` and `Data/AliasMapCache.cs`: telescope canonical names.
- `ViewModels/Settings/EquipmentTabViewModel.cs` and `Views/Settings/EquipmentTabView.axaml`:
  the telescope grouping editor the profile map sits under.
- `Data/Migrations/` with three migrations; `Data/GalactiLogContext.cs`.
- 14B's `JobRegistry` for the pass; 14B's Activity events.

### Spec work

New spec section 7.6 "PHD2 guide logs" (discovery rule, the parser contract with the banner and
header regexes named, the metrics with `MIN_FRAMES`, the window exclusion and the filtered RMS,
the timezone resolution order, the correlation rule and its never-overwrite guarantee); new 5.15
to 5.18 for the four tables; 5.8.1 keys; 10.3 (the guide-log pass after the header pass), 10.4
(the envelope's PHD2 counters), 10.9 (events); 12.7 (the PHD2 profiles panel on the Equipment
tab, the scan toggle on the Library tab); 19.1 (PHD2 removed from the deferred list). The user
approves 7.6 and the table sections before the brief-writer runs.

### Data model

One migration, the fourth: `phd2_logs` (id, file_path unique, file_size, file_mtime,
parse_status `ok`, `empty` or `failed`, parse_error, phd2_version, log_version, run_count,
session_count, calibration_count, parsed_at), `phd2_sessions` (the web's columns verbatim from
`backend/app/models/phd2.py`, with `started_at_local` kept beside `started_at_utc`, `events`
and `star_lost_reasons` as JSON text, indexes on session_date, telescope plus session_date,
started_at_utc, log_id), `phd2_frames` (pixels only, narrow, indexed on session plus frame index
and session plus time offset), `phd2_calibrations` (header fields plus `steps` JSON). Foreign
keys cascade from logs. New `general` keys: `phd2_scan_enabled` (bool, default true),
`phd2_profile_map` (object keyed by profile name with `telescope`, `timezone`, `latitude`,
`longitude`, every field optional). No user file is written, moved or modified: guide logs are
read through `UserFiles` in shared-read mode and only the catalogue changes (rule 1).

### Task table

| Task | Model | Files | Verify |
| --- | --- | --- | --- |
| 1. Spec-writer: 7.6, 5.15 to 5.18, 5.8.1, 10.3, 10.4, 10.9, 12.7, 19.1; user approves 7.6 and the tables. | opus | `docs/design-spec.md` | Zero em or en dashes; every stored column named; the correlation never-overwrite sentence present. |
| 2. Parser: `Core/Phd2/Phd2LogParser.cs` (text in, records out, no IO) with the banner, header, CSV, event and calibration grammars ported regex by regex from `phd2_parser.py`, including the ASIAIR shapes. | opus | `Core/Phd2/{Phd2LogParser,Phd2Run,Phd2Section,Phd2Calibration}.cs`, `tests/GalactiLog.Core.Tests/Phd2/*` | Fixture strings for a desktop log, an ASIAIR log, a stacked two-run file, a truncated tail, an empty file; every regex has a case. |
| 3. Metrics and profiles: `Core/Phd2/Phd2Metrics.cs` (UTC conversion, windows, filtered RMS, drop and settle metrics, session and night aggregates, `MinFrames` 100) and `Phd2Profiles.cs` (the canonical map, timezone and site resolution order, rename rewrite). | opus | `Core/Phd2/{Phd2Metrics,Phd2Profiles}.cs`, `Core/Settings/GeneralSettings.cs`, tests | Metric values against numbers computed by hand on a 200-row fixture; window exclusion; gating; profile map normalisation table. |
| 4a. Schema and Diagnostics half, split from Task 4 at the coordinator's ruling: the migration and entities, `Phd2Repository`, three `scan_runs` counters, the Diagnostics row counts and size figure, reset database extended to clear the four tables. Names no `GalactiLog.Core.Phd2` type. | opus | `Data/Entities/Phd2*.cs`, `Data/Migrations/<stamp>_Phd2.cs`, `GalactiLogContext.cs`, `Data/Repositories/Phd2Repository.cs`, `Data/Queries/DiagnosticsQuery.cs`, `Data/Maintenance/DatabaseReset.cs`, `App/ViewModels/Settings/ResetConfirmViewModel.cs`, `App/ViewModels/Diagnostics/DiagnosticsViewModel.cs`, `App/Services/DiagnosticsBundle.cs` | Migration applies and rolls back on a fresh database; every column and index by name against 5.15 to 5.18; `dbstat` branch checked on the shipped native library; `FileSafetyTest`. |
| 4b. Discovery and pass half, split from Task 4, wiring: `Data/Ingest/Phd2Ingest.cs` (discovery from `FileWalker` by the filename rule, delta skip, failed record, per-walked-root orphan row drop sharing `OrphanPruner`'s guard arithmetic, counters on `scan_runs`, job registry, Activity events) running after the header pass when `phd2_scan_enabled`. Opens `Phd2Repository` from 4a. | opus | `Data/Ingest/{Phd2Ingest,ScanCoordinator}.cs`, `Core/Scanning/FileWalker.cs`, `Data/Repositories/OrphanPruner.cs` | Unchanged log skipped; failed log not re-read; removed log's rows dropped per walked root; counters on the run row; `FileSafetyTest`. |
| 5a. Correlation core half, split from Task 5, naming no contended file: `Data/Ingest/Phd2Correlation.cs` filling the three RMS columns with source `phd2` for unfilled LIGHT frames whose rig has sessions that night, never overwriting `csv` (the predicate inside the one update statement, ruling F6), one warning event per pass for profiles without a zone. | opus | `Data/Ingest/Phd2Correlation.cs`, `Data/Queries/Phd2Queries.cs`, tests | Overlap pooling on a constructed night; CSV values untouched; source written; the unmapped sole profile rule; the warning emitted once; the never-overwrite predicate proven red by removing it. |
| 5b. Correlation wiring half, split from Task 5, after 4b and 5a: the `ScanCoordinator` call and the `SettingsStore` map-change hook that re-runs correlation on save. | opus | `Data/Ingest/ScanCoordinator.cs`, `Data/SettingsStore.cs` | Re-run after a profile map change case. |
| 6a. Profiles panel core half, split from Task 6, naming no contended file: the discovered profiles table with telescope, timezone, latitude and longitude editors on the Equipment tab, the toggle on the Library tab, both through `MutateGeneral`; equipment rename rewrites the map through `Phd2Profiles.SameMap`. | sonnet | `ViewModels/Settings/{Phd2ProfilesViewModel,EquipmentTabViewModel,LibraryTabViewModel}.cs`, `Views/Settings/{EquipmentTabView,LibraryTabView}.axaml`, `Data/Queries/Phd2ProfilesQuery.cs` | Row fields from a seeded database; each edit writes the key shape of 5.8.1; rename rewrite case only writes when `SameMap` reads false. |
| 6b. Profiles panel wiring half, split from Task 6, after 5a, 5b and 6a: `AppHost` registration and the save-triggered correlation re-run. | sonnet | `App/AppHost.cs` | Save re-runs correlation, an Activity event says so. |
| 7. Provenance on the page: the ledger's guide RMS cell and the pane's facts line carry the dagger and the source sentence when any value came from a guide log, from `guiding_rms_source`. The roadmap's file list omitted `FrameRowViewModel.cs` and `FrameTableView.axaml` (ruling Q6), which this task also owns. | sonnet | `ViewModels/TargetDetail/{SessionCardViewModel,TargetDetailViewModel}.cs`, `Data/Queries/TargetDetailQuery.cs`, `ViewModels/TargetDetail/FrameRowViewModel.cs`, `Views/TargetDetail/FrameTableView.axaml` | Source summary truth table (csv, phd2, mixed, none). |
| 8. Docs and handoff: spec reconciled, HANDOFF, TRACKING, the Status line, the 15B prompt. | sonnet | docs only | Zero em or en dashes. |

Order as planned: Task 1; Tasks 2 and 3 together (Task 3 codes against Task 2's record shapes
agreed in the brief); Task 4 after both; Task 5 after Task 4; Tasks 6 and 7 after Task 4 together;
Task 8 last.

Order as it actually ran, after the coordinator's ruling to write brief-writer A's briefs for
Tasks 2 and 3 beside the spec (their contract is the web source, not the approved text, so no
implementer started before the user's approval) and the user's mid-phase directive to split every
waiting task into a core half and a wiring half: Task 1 and the fixture step together at the open,
beside brief-writer A; Tasks 2 and 3 together once the user approved; brief-writer B and Task 4a
after the approval; Tasks 4a's fix pass, 5a, 6a and 7 together, each naming no file another live
task opens; Task 8 beside all of them, from the coordinator's directive to use more agents as
needed. Tasks 4b, 5b and 6b wait on their own core half or halves only, not on the whole of Task 4.

Every task unit closed: Task 2 (APPROVE, one fix pass), Task 3 (FIX-FIRST, one fix pass), Task 4a
(FIX-FIRST, one fix pass), Task 4b (APPROVE, one fix pass), Task 5a (APPROVE, one fix pass plus a
same-day follow-up), Task 5b (FIX-FIRST, one follow-up that reversed a coordinator ruling), Task 6a
(FIX-FIRST with two P1s, its fix pass dispatched to a fresh opus agent rather than back to the
sonnet implementer), Task 6b (FIX-FIRST, one fix pass), Task 7 (FIX-FIRST, one fix pass plus a
second coordinator-caught correction). The phase review found no P1, seven P2s and twelve P3s
across the 127 changed paths and dispatched four file-disjoint fixers, all closed: fixer A (the
pass, the two triggers, the failed-correlation event, the durable re-run flag, the 64 MiB
ceiling), fixer B (the write predicate, `Phd2Repository` batching, the unreachable-warning fix),
fixer C (the parser's null guards and lazy line enumeration), fixer D (the profiles panel's
resolution line, first and last seen, the ledger mark's tooltip, the Library help sentence).

Three more rounds followed the phase review, all closed before the figure of record. The
coordinator's first closing run found four exact-count pins outside every task's own filtered
figure and a recurring test-harness failure shape; **fixer E** re-aimed three of the four pins and
left the fourth as a deliberate guard, and **the flake investigation** (three passes) established
a mechanism for one hypothesis without confirming it, refuted a second, and left harness
diagnostics (`GALACTILOG_TEST_DIAG_DIR`) in the tree for the next recurrence. **The fix-wave
review** then read the four phase-review fixers' own new behaviour and found one P1 (the
`phd2_correlation_pending` flag could be cleared out from under a save that landed mid-pass);
**fixer A's and fixer D's fix-wave passes** closed it with a compare-and-clear door and a
per-row seen basis, and fixer A's pass also implemented ruling B, the silence rule for a library
with no guide logs. Finally, **the real-log pass**, the port's first encounter with a real PHD2
file (the user's own 60 guide logs), found a genuine rounding-parity defect that **fixer F**
closed with `Phd2Metrics.RoundLikePython`. The coordinator's closing figure took four full runs to
reach honestly: 61 failed under load (4 real, since fixed; 57 of the investigated shape), 1 failed
on a quiet machine (one case of that shape, now a fourth known flake), a clean run, then the
rounding fix's own clean run as the figure of record.

### Fixtures

The Phase 12 recipe writes no guide logs. A new generator under
`docs/superpowers/work/phase15a/fixtures/` writes, beside the Phase 12 nights, one desktop-shaped
`PHD2_GuideLog_2025-03-20_213000.txt`[^15a-1] with two guiding sections (one of 400 frames at 0.5 s
covering the newest night's exposures, one of 60 frames that the gate excludes), one dither with
a settle window, one failed settle, one calibration block, and one ASIAIR-shaped log for the
OIII night with no pixel scale line. The Phase 12 recipe cannot produce these; the generator is
a sibling test class in the same style. The fixture frames carry no CSV sidecar, so every
guiding value on the page is correlated.[^15a-2]

[^15a-1]: **Accepted deviation.** The generated file is
`PHD2_GuideLog_2025-03-19_213000.txt`, one calendar day earlier than this paragraph names, per the
paragraph's own permission ("If the Phase 12 newest night is not 2025-03-20, keep the roadmap's
filename shape but use the date and start time that actually cover that night's exposures, and say
so in the README"). PHD2 stamps its filename with the LOCAL wall-clock session start, with no zone
recorded in the file; night 3's `DATE-OBS` runs 2025-03-20T01:00 to 02:08 UTC, and a local evening
session (21:30 local) under any real-world fixed offset that later produces UTC timestamps in the
small hours of a given UTC date must have started the evening before that UTC date, because no real
offset (the largest is +14:00) reaches 19 to 20 hours ahead of UTC. `work/phase15a/fixtures/README.md`
states the one-day shift and the assumed fixed UTC-04:00 zone in full.

[^15a-2]: **Accepted deviation.** The correlation verification steps below need a zone that reads
as a fixed UTC-04:00 on both guide log dates configured on the mapped profile or on
`general.observer_timezone` (for example IANA `America/La_Paz` or Windows `SA Western Standard
Time`), because a headless profile carries no observer zone by default and, under ruling F1, a bare
CLI scan of this fixture stores both logs and correlates neither. `America/New_York` does not work
for this purpose: it is UTC-05:00 on the February log's date and only UTC-04:00 after its own March
daylight saving change, which lands after this fixture's dates.

### Verification bar

**NOT RUN. User ruling U5 of 2026-09-19**, which applies to every phase from 14C on: the
verification agent does not walk the running application screen by screen and repeats nothing per
theme, so the launched-app items below are not run as written. What ran in its place on the clean
copy: `dotnet build -c Release` with 0 warnings, the full suite, the headless CLI scan, and the
byte-identity proof over `C:\tmp\p15a-fixtures`, both profile folders and the user's own files. An
implementer's one targeted look at the screen it changed covers what it can of the five items
below; whatever it cannot is recorded in HANDOFF section 7 as unverified on a launched application.
Item 1's counters and job visibility were covered twice, once by Task 4b's launched-app fixture
scan and once by the coordinator's own CLI reproduction (section 3.5): `phd2_found` 2,
`phd2_ingested` 2, `phd2_failed` 0, the pass exiting cleanly. Item 2's panel listing and re-run
were covered by Task 6b's launched-app look, both real profiles shown, the re-run-queued sentence
present, first and last seen moving after a save. Item 3's rendering (the dagger, the sentence) was
covered by Task 7's two launched-app looks against seeded data; its **fill**, a real correlation
producing a non-zero guiding value on the running application, was not seen by anyone, because the
one zone an agent could select in the timezone picker's automation tree was `GMT-07:00`, not the
fixture's own `GMT-04:00`, and the CLI's own FILLED 0 figure is the correct result for that
mismatch, not a failure. Items 4 and 5 (the orphan drop, the before-and-after hash) were not run
on a launched application; the suite and the coordinator's own hash bookkeeping cover them
instead. The bar below is left as written.

1. A scan of the fixture reports `phd2_found` 2, ingested 2, failed 0 on the run row and in the
   Activity feed, with the pass visible in the job monitor.
2. The Equipment tab lists both profiles with their camera, scale and session counts; mapping
   one to the fixture telescope and saving re-runs correlation (an Activity event says so).
3. The newest night's frames carry guiding RMS with the dagger and the "from a PHD2 guide log"
   sentence; the OIII night's frames carry none and the feed holds the missing-scale warning.
4. Removing the desktop log from a mirror and scanning the mirror drops its rows; the fixture
   itself is unchanged by hash.
5. Fixture hash identical before and after; profile folders untouched per HANDOFF 5.2 item 1.

### Size

Estimate: 10 to 12 agent-days, one session. The parser and metrics are 1,900 lines of Python
ported with their regexes; the correlation is 800. This is the largest Core task since the
readers of Phase 2.

### Risks and rulings needed

1. Timezone: PHD2 writes local wall clock with no zone. Proposed answer: the resolution order is
   the profile's own zone, then `observer_timezone`, then nothing (the session is stored, the
   correlation skips it and warns), exactly the web's rule; the port never assumes the machine
   zone silently.
2. Table volume: the web's corpus reached 390k frame rows. Proposed answer: `phd2_frames` stays
   pixels only with the two indexes; a size figure goes into Diagnostics' Database group.
3. Discovery scope: guide logs may live outside the scan roots. Proposed answer: the walk covers
   the scan roots only in 15A; a "guide log folders" list is a candidate if the user asks.
4. The dagger glyph in Atkinson Hyperlegible Mono. Proposed answer: the spec-writer checks the
   glyph exists in the embedded face; if not, a superscript "PHD2" tag replaces it.

### Next session prompt

```
Read docs/superpowers/HANDOFF.md, then docs/superpowers/TRACKING.md, then the P14C lines at the tail of docs/superpowers/progress.md, then docs/parity-roadmap.md section Phase 15A, and open Phase 15A from its task table.
```

---

## Phase 15B: PHD2 guiding on the night pane and the Statistics page

Goal: the night pane gains a fourth collapsible section, "Guiding", with the night's guiding
summary, calibration issue chips and a guide graph drawn as a control in the night strip's
manner (RA and Dec traces, star-lost points, dither lines, settle bands, legend toggles, zoom
and pan, a session selector and a rig filter); the Statistics page gains a Guiding section with
the per-rig scorecard and RMS by altitude band; the cross-session and night charts accept the
PHD2-derived RMS like any other value. Everything reads the tables Phase 15A filled.

Status: DONE, commit `8bf0e14` on `snd`, `6805` tests at the close (Core
1660, Cli 58, Data 1382, App 3705, with the App assembly's first pass at that build reading 3704
of 3705 against the known flake `PreviewModalViewModelTests.HeaderPanel_StaysOpenAcrossNavigation`
and a fresh rebuild reading the whole assembly 3705 of 3705; the earlier clean single pass was
`6772`, Core 1635, Cli 58, Data 1380, App 3699, at 23:24 on 2026-09-19).
Run 2026-09-19 to 2026-09-20 as the fifth roadmap session. Task order as it actually ran: 1
(spec), 2 (night queries), 2c and 2d (carried items 65, 54 and 55, the latter with the fifth
migration, taken this phase on the user's standing fresh-database directive rather than deferred
again), 5a (Statistics baselines), 3 (the Guiding section), 4a and 4b (the guide graph, split core
and wiring), 5b and 5c (Statistics guiding and the Settings destinations spine, split), 6 (help
topics), 7 (these documents); then a rig fixer ahead of the phase review for a stale-stored-column
defect a task review found across two queries. The phase review found
five P2 and eight P3 new findings over the session's own new files, plus ten items ruled from
earlier task reviews; four file-disjoint fixers took them (A the Data and Core read paths, B the
prose and help topics and the Settings scroll spine, C the Guiding band and the guide graph, D
the Statistics guiding cards, the drawn controls and the shell's staleness spine). Clean-copy
verification: PASSED WITH FINDINGS (6772 of 6772 on a copy of the tree as it stood before the two
blocker fixes below; the CLI scan reconciled on both passes; fixture 66 of 66 and both profile
folders untouched; its one blocker, the PHD2 telescope picker offering no ungrouped telescope, was
found independently of the real-data pass and fixed by `fix-p15b-e`).
Real-data pass over the user's own three 2026 nights and sixty guide logs, `realdata-report.md`:
the first pass FAILED on two blockers and the recheck PASSED with neither. D drive 506 of 506
files identical before and after on both passes, the private copy identical, no user file
written; 416 real LIGHT frames and 60 guide logs (50 ingested, 10 empty) reconciling on both scans;
937 sessions, 1,229,517 frames, 52 calibrations stored, matching Phase 15A's own count over the
same logs. Blocker 1 (the telescope picker offering only "Not mapped" on a library with no alias
group) was fixed by `fix-p15b-e`; the recheck mapped all four of the user's PHD2 profile names to
their two telescopes through the user interface on a library with no alias group, the mappings
surviving two unrelated equipment saves, and an unmap and a re-map both taking effect at once with
no rescan and no restart (per rig: `Askar_140APO` 834 sessions, 178.9 guided hours;
`Askar_SQA55` 103 sessions, 160.3 guided hours; unmapped tally zero). Blocker 2 (one stored
decimal of 937 real guiding sessions differing from the web's Python parser, `snr_mean`) was
fixed by `fix-p15b-f`, which ported two of CPython's own summation rules; the recheck compared
all 24 stored fields of all 937 sessions against the web exactly, with 0 differing. The two-rig
night of 2026-07-13 showed each card its own rig throughout (Crescent Nebula on `Askar_140APO`:
RMS 0.65, RA 0.53, Dec 0.38 arcsec over 3 sessions; Sh2 129 on `Askar_SQA55`: RMS 0.76, RA 0.52,
Dec 0.55 arcsec over 4 sessions), and every one of the 416 real LIGHT frames kept its NINA sidecar
guiding value through every correlation pass the session ran. The user's own library is what
exposed both blockers: the fixture's own seeded settings could show that the read side worked but
could not show that the write side (the picker) did not.

### What the web does

- `frontend/src/components/Phd2GuidingPanel.tsx`: a collapsible "Guiding (PHD2)" panel headed
  with the number of guiding sessions covered; total, RA and Dec RMS in arcseconds plus how many
  sessions were too short to grade; star-lost count with unguided time and longest run; dither
  count, median settle time, failed settles; warning chips for calibration issues.
- `frontend/src/components/Phd2GuideGraph.tsx` (959 lines): loads only when opened; a rig
  dropdown for a night mixing rigs; a session dropdown labelled with start time, duration, rig
  and a "short" marker; RA and Dec guide error in arcseconds against clock time with star-lost
  points on the zero line; dashed dither lines and shaded settle bands, red when the settle
  failed; a legend that hides or restores RA, Dec, Star lost, Dither and Settling; scroll zooms
  time, Shift and scroll zooms arcseconds, drag pans, double-click resets; a resize grip whose
  height survives collapse; a caption naming the visible slice of the session; a notice instead
  of the plot when the log carries no pixel scale.
- `backend/app/api/phd2.py` `GET /sessions` (night and optional telescope, using the
  mapped-or-sole-unmapped rule) and `GET /sessions/{id}/frames` (`Phd2FramePoint` and
  `Phd2EventPoint` series).
- `frontend/src/components/GuidingScorecard.tsx`, `GuidingAltitude.tsx`, `FwhmValue.tsx`,
  `backend/app/api/stats_guiding.py` and `schemas/stats_guiding.py`: per rig, sessions, gated
  sessions, guided hours, RMS total, RA, Dec, filtered, Dec-to-RA ratio, median settle seconds
  and the guide exposures used; RMS cells graded against the cross-rig median; a notice when no
  logs or no mapped profiles exist; a sky-arc card per rig with three altitude wedges (below 30,
  30 to 60, above 60) shaded by that rig's own RMS range, each printing RMS, the ratio to the
  above-60 band and the session count, with a table view.

### What the port has

- `src/GalactiLog.App/Controls/NightStrip.cs` and `ViewModels/TargetDetail/NightStripViewModel.cs`:
  the drawn-control pattern (render in `Render`, immutable brushes from the host, hover and
  click, no colour literal in source, a headless render tick in tests).
- `Controls/SessionPane.axaml` and `SessionCardViewModel.cs`: three collapsible sections with
  persisted state in `display.target_page`; the section band shape.
- `ViewModels/Stats/StatisticsViewModel.cs`, `Views/StatisticsView.axaml`,
  `Data/Queries/StatsQuery.cs` and `StatsCache.cs`: the sectioned Statistics page and its one
  cached query; `Controls/CalendarHeatmap.cs` as the second drawn control.
- `Theme/ChartTheme.cs` and the `metric-*` tokens; `metric-guiding` exists.
- 15A's tables, `Phd2Repository`, `Phd2Queries` and the profile map.

### Spec work

Amend 12.4 (the fourth section "Guiding": summary figures, chips, the graph's gestures, the
selector and rig filter, the no-scale notice, closed by default), 5.8.2
(`display.target_page.guiding_expanded` default false, `guide_graph_height`), 12.5 (a Guiding
section row: scorecard columns, grading rule, the altitude card and its table view, the empty
notice), 13 (two chart rows: the guide graph as a drawn control, the altitude arc as a drawn
control), 14.1 if a token is added for the settle band. The user approves 12.4's section and the
12.5 row.

### Data model

**One migration, the fifth this port has, not none.** Carried items 54 and 55 (the session-time
re-derive's DST-crossing residual and the clear-side hold-out for an unzoned session, both
originally deferred at Phase 15A) were taken this phase on the user's standing directive that the
application has not shipped and every install is treated as a fresh database until `main`
releases run through CI/CD, so a schema change is never deferred for want of a migration. The
fifth migration adds one nullable `phd2_sessions.ended_at_local` column and nothing else, with no
backfill. `display.target_page` gains `guiding_expanded` (bool, default false) and
`guide_graph_height` (int pixels, default 240, bounded 160 to 800). Otherwise reads only, through
two new queries: `Phd2NightQuery` (sessions for a night and rig with the summary rollup) and
`Phd2FramesQuery` (one session's samples and events, arcseconds derived at read time from the
session's pixel scale), plus `GuidingStatsQuery` cached with the Statistics response. Nothing is
written outside `user_settings` and the one new column (rule 1).

### Task table

| Task | Model | Files | Verify |
| --- | --- | --- | --- |
| 1. Spec-writer: 12.4, 5.8.2, 12.5, 13 and 14.1 amended; user approves the 12.4 section and the 12.5 row. | opus | `docs/design-spec.md` | Zero em or en dashes; every gesture and figure of the web list named or explicitly dropped. |
| 2. Night queries: `Phd2NightQuery` (the mapped-or-sole-unmapped rule, frame-count weighted rollup, gated count, calibration issues, profiles) and `Phd2FramesQuery` (samples in arcseconds, events typed, null scale reported). | opus | `Data/Queries/{Phd2NightQuery,Phd2FramesQuery,Phd2Models}.cs`, tests | Rollup numbers on the 15A fixture; rig rule truth table; null scale case. |
| 3. Guiding section: the fourth band on the pane with the summary line, the counts, the chips and the persisted state; loads its night query when opened. | opus | `Controls/SessionPane.axaml(.cs)`, `ViewModels/TargetDetail/{SessionCardViewModel,GuidingSectionViewModel}.cs`, `Core/Settings/DisplaySettings.cs` | Section closed on a fresh profile; figures from the query; chips per issue; nothing loads until opened. |
| 4. Guide graph control: `Controls/GuideGraph.cs` drawn like `NightStrip` (traces, zero line, star-lost points, dither lines, settle bands, labels), `GuideGraphViewModel` with the session selector, the rig filter, legend toggles, the time and arcsecond zoom, pan, reset, the range caption, the height grip persisted, the no-scale notice. | fable | `Controls/GuideGraph.cs`, `ViewModels/TargetDetail/GuideGraphViewModel.cs`, `Controls/SessionPane.axaml`, tests | Trace point counts; event overlays per event; legend toggle hides a trace; zoom and pan transform table; height persists; no colour literal in source; headless render tick. |
| 5. Guiding statistics: `GuidingStatsQuery` (rigs, altitude bands, unmapped count) joined into `StatsQuery`; the scorecard table with graded RMS cells and the exposures line; the altitude arc as `Controls/AltitudeArc.cs` with wedge tooltips and a table view; the empty notice linking to the Equipment tab. | opus | `Data/Queries/{GuidingStatsQuery,StatsQuery,StatsModels}.cs`, `ViewModels/Stats/{GuidingViewModel,StatisticsViewModel}.cs`, `Controls/AltitudeArc.cs`, `Views/StatisticsView.axaml` | Scorecard rows on the fixture; grading against the cross-rig median; wedge figures; table view equals the wedges; empty notice on a bare database. |
| 6. Help topics and column help: 14A's `HelpTopics` gains the guiding section, the graph and every scorecard column, from the web's popover texts. | sonnet | `Core/Help/HelpTopics.cs`, the two views | Census test extended; every new heading has a glyph. |
| 7. Docs and handoff: spec reconciled, `DESIGN.md` gains the two drawn controls, HANDOFF, TRACKING, the Status line, the Phase 16 prompt. | sonnet | docs only | Zero em or en dashes. |

Order: Task 1; Task 2 alone; Tasks 3 and 5 together; Task 4 after Task 3; Task 6 after Tasks 4
and 5; Task 7 last.

### Fixtures

The 15A fixture (two guide logs beside the Phase 12 nights) covers the section, the graph and
the scorecard for one rig. One addition under `docs/superpowers/work/phase15b/fixtures/`: a
third log for the two-rig night of 14A with a second profile at a different pixel scale and
sessions at three altitudes, so the rig filter, the session selector and the three altitude
wedges have data. The 15A generator produces it with a second profile block.

### Verification bar

Amended by user ruling U5 of 2026-09-19, which applies to every phase from 14C on: the
verification agent does not walk the running application screen by screen and repeats nothing per
theme, so the launched-app items below are not run as written. The clean-copy check keeps the
build with 0 warnings, the full suite, the headless CLI scan and the byte-identity proof over the
fixture library, both profile folders and the user's own files; an implementer's one targeted look
at the screen it changed covers what it can, and whatever it cannot is recorded in HANDOFF section
7 as unverified on a launched application. The bar below is left as written.

1. On the newest night, the Guiding band is closed on a fresh profile; opening it shows the RMS
   figures, one gated session, one dither, one failed settle and the calibration chip.
2. The graph draws RA and Dec, the dither line and the red settle band; hiding RA through the
   legend removes it; scroll zooms time, Shift and scroll zooms arcseconds, drag pans,
   double-click resets; the caption names the visible slice; the grip changes the height and it
   survives a relaunch.
3. The session selector lists both sessions with the "short" marker; on the two-rig night the
   rig filter narrows the list.
4. The OIII night shows the no-scale notice in place of the plot.
5. Statistics shows the scorecard with graded cells and the altitude arcs; the table view lists
   the same figures. Neither Guiding notice form is reachable on a library with no LIGHT frames
   at all, because `StatisticsViewModel.ShowSections` hides every section on `IsEmpty`; the two
   empty-notice forms (no guide logs catalogued, logs catalogued but no profile mapped) are shown
   on the fixture library `C:\tmp\p15b-fixtures` and the real-data library
   `C:\tmp\p15b-real\library` instead, both catalogued and non-empty.
6. Red Light and Glass Void render both controls without a colour literal.
7. Fixture hash identical before and after; profile folders untouched per HANDOFF 5.2 item 1.

### Size

Estimate: 8 to 10 agent-days, one session. The guide graph is the large item (the web's is 959
lines with its own gesture model) and is the fable task.

### Risks and rulings needed

1. The graph's zoom and pan on a drawn control: the night strip has none. Proposed answer: a
   transform on the view-model (time window, arcsecond window) applied in `Render`; wheel and
   drag handled in the control; no LiveCharts.
2. Section count on the pane: four collapsible bands. Proposed answer: Guiding sits after Night
   detail and before Frames, closed by default, so the Frames section keeps its height contract.
3. Altitude arc without observer coordinates: the web reads altitude from the log's pointing
   line, not from the site. Proposed answer: the same; the arc needs no coordinates and says so
   in its help topic.
4. Grading scorecard cells against the cross-rig median with one rig. Proposed answer: with one
   rig every cell is neutral; the help topic states it.

### Next session prompt

```
Read docs/superpowers/HANDOFF.md, then docs/superpowers/TRACKING.md, then the P15A lines at the tail of docs/superpowers/progress.md, then docs/parity-roadmap.md section Phase 15B, and open Phase 15B from its task table.
```

---

## Phase 16: WBPP export

Goal: from the Target detail page's selected nights, an "Export for stacking" modal page
computes the folder level to copy per night with its contamination cost, applies a quality
filter over the selected LIGHT frames (raw metric constraints against the outlier flags and the
Phase 14A grades, either baseline), states the frame, folder and byte totals, and writes a
PowerShell or Bash copy script as a new file to a folder the save dialog returned. The
application copies nothing itself; the script the user runs does. The web's in-browser copy and
its file moves stay excluded.

Status: DONE, commit `ccca30a` on `snd`, `7436` tests at the
close (Core 2045, Cli 58, Data 1396, App 3937, 631 over the `6805` baseline, one pass, no repeat,
no flake, `--no-incremental`, reproduced exactly with the same split by the clean-copy verification
agent on an independent copy). The pre-fixer figure, taken once every task unit had closed, was
`7424` (Core 2047, Cli 58, Data 1393, App 3926): the difference is Data +3 and App +11 but Core
minus 2, because the fifth fixer replaced a five-row theory with one computed fact and the fixers
added two Core cases beside it.
Run 2026-09-20 as the sixth roadmap session, across two sessions after the first was killed by a
quota limit at about 05:14 and the second resumed from `work/phase16/STATE.md` and the `P16` ledger
lines alone. Seven tasks ran as ten task units: 1 (the spec, three passes, at the user's gate and
after the seam and security reviews), 2a (`FolderLevels`), 2b (`WbppPathsQuery`), 3a split into the
`QualityFilter` half and the four tolerant `wbpp_` settings keys, 4 (the two script generators),
3b (the quality panel), 5a (the export page core with the one `BeginExport` allowlist entry), 5b
(the Export flyout and the wiring), 6 (help topics), 7 (these documents). Two steps this section's
task table does not name were added and paid for themselves: a records-first agent landed every
shared record and enum before any implementer started, which removed the Task 2 to Task 4 ordering
constraint; and a seam review read `core-shapes.md` and the three Core briefs against the Python,
the approved spec and the rulings before a line of code existed, returning P1 6, P2 11 and P3 9,
every one of them a defect an implementer would otherwise have built. Task 4's review was run as
the phase's security review and told to execute its attacks rather than read for them, which found
the phase's one P1, a code-execution path through a folder name carrying a typographic apostrophe,
present in the web application's own quoter too. Two fixture libraries were built, the tidy
`C:\tmp\p16-fixtures` and `C:\tmp\p16-fixtures-nina` in the user's own real NINA shape, because
every rule taken from the real tree had no generated library it could be seen on. The phase review
was FIX-FIRST, P1 0, P2 4, P3 10, dispatched to four file-disjoint fixers. A launched-app look then
ran the page end to end on both libraries and ran the generated `.ps1` for real under Windows
PowerShell 5.1: it refuted an earlier task's report of a dead Export button, identifying that
report's cause as two harness facts now recorded in HANDOFF 3.5, and it found one blocker no
headless case could reach, a staging Browse button permanently disabled because the window assigns
its picker seam after the command has already read it. A fifth fixer took that blocker at the root
and two of the look's four deviations. All five fixers closed.
Clean-copy verification: PASSED, eight bars, eight passes, no blocker and no deviation from a
declared figure; it reproduced 7436 of 7436 with the coordinator's own per-assembly split on an
independent copy, re-established file safety by its own grep of that copy's `src/` rather than by
reading the claim, and left both fixture manifests 47 of 47 identical before and after.
Real-data pass over the user's own three 2026 nights, `realdata-report.md`: PASSED with no
blocker, every setting set through the running application's own UI on an unseeded profile.
Parity against the web's own folder logic is exact on levels, order, frame counts, contamination,
the default pick on all five sessions and every staging entry name including the date-prefixed
collision; the only numeric difference anywhere is the eight byte figures at the shared date and
LIGHT levels, each equal to ruling R4's arithmetic to the byte, which is the correction R4 exists
to make. Per-rig quality limits isolated in both directions, the kept and left-out counts derived
independently from the catalogue and exact on all three targets, the Browse button enabled on the
launched application, and the generated `.ps1` run for real under Windows PowerShell 5.1: byte
order mark present, no destructive verb, exit 0, 56 files and 796,891,595 bytes, being the
footer's own 44 frames plus 12 sidecars copied and uncounted exactly as the footer says. The
library was 507 of 507 identical before and after, taken twice, and the D drive was never touched.

The four risks below were answered as proposed but for the third, which was ruled in full at the
Task 2b review: the contamination read stays one unfiltered query per page open, no covering index
is added this phase, and the accepted limit with its upgrade path is recorded in the code and in
HANDOFF 5.1 item 12a as a choice for the user.

### What the web does

- `frontend/src/components/WbppExportModal.tsx` (1,241 lines): a modal titled Export For
  Stacking naming the target, session count and frame count; an empty-export warning when the
  filter excludes every frame; "Copy to" and "Copy from" pickers (Chromium only) and a "Script
  to" readout; a library settings disclosure (library root, staging path, excluded folder
  patterns, defaulted from settings, applying to this export) with Save as defaults; "Preview
  folder levels" listing one expandable row per session with date, frame count and chosen
  folder, a rescan icon, and a rename note when two sessions' folder names collide.
- `frontend/src/components/wbpp/WbppLevelEditor.tsx`: an indented radio list of ancestor folders
  from the frame's directory up to the library root, each annotated with how many other sessions
  or other targets the folder drags along, the assembled path underneath.
- `backend/app/services/wbpp_export.py` (591 lines) and `backend/app/api/wbpp.py`: per-session
  ancestor chains, the longest common ancestor, contamination from every other path in the
  catalogue, the default level pick (the deepest level that drags nothing along), staging name
  disambiguation, subtree byte totals (null when any size is unknown), and the two script
  generators (quoted literals, an exclusions list defaulting to WBPP, PixInsight, finals,
  WORK_AREA, masters, CALIBRATED variants, per-file excludes for filtered LIGHT frames, an
  unblock step on Windows).
- `frontend/src/components/wbpp/WbppQualityPanel.tsx` and `frontend/src/lib/wbppQualityFilter.ts`:
  an Enable filters checkbox; ghost chips for HFR, Ecc, FWHM, Stars and RMS that expand into a
  comparator and a threshold; eccentricity presets with tooltips; a kept and skipped tally; a
  "This session" or "Overall" baseline for the cell colours; a sortable verdict table (copy,
  unmeasured, exclude glyphs with the failure sentence on hover, the failing value in red, a
  per-row copy override, the full path under duplicate names); a footnote that filters apply to
  LIGHT frames only.
- `frontend/src/components/wbpp/WbppFooter.tsx` and `WbppScriptMenu.tsx`: frame count, folder
  count and total size; separate Copy and Script destination lines; Generate script offering
  PowerShell or Shell with the default marked; after generating, Download, Copy script, the exact
  run command and a Show or Hide toggle.
- Settings: `wbpp_library_root`, `wbpp_default_os`, `wbpp_staging_path`, `wbpp_exclusions`,
  `wbpp_quality_enabled`, `wbpp_quality_mode`, `wbpp_quality_score_threshold`,
  `wbpp_quality_baseline`, `wbpp_quality_raw_constraints` (`backend/app/schemas/settings.py`).

### What the port has

- 14A's night selection column and `SelectedNights`; 14A's grades and bands on
  `FrameRowViewModel`; `Core/Metrics/FrameQuality.cs`; the outlier flags from Phase 12.
- `Views/ModalPageWindow.cs` and `ViewModels/IModalPageViewModel.cs`: the modal page host used
  by the merge dialog and 14A's frame list dialog.
- `Core/Io/AppWriter.cs` `BeginExport(path)` returning an `ExportWriter` with `WriteAllText`:
  the one sanctioned write to a save-dialog path (spec 2.1.1), used by the diagnostics bundle
  and 14B's log save-as.
- `Data/Queries/SessionDetailQuery.cs` (frame paths per night) and `images.file_path`,
  `file_size` for every catalogued file; `Core/Io/PathConfinement.cs`.
- `Core/Text/` for pure text generators (14A's `FrameListFormats`).
- The port runs on the machine that holds the files, so the web's container-to-library path
  translation is unnecessary: paths are already native.

### Spec work

New section 12.13 "Export for stacking": the page's parts, the level rule, the contamination
rule, the quality filter's chips and verdicts, the totals, the script contract (what it copies,
what it never does, the unblock line), and the sentence that the application never copies a file
itself. Amend 12.4 (the Export flyout on the target page listing Copy frame list, Export for
stacking and, from Phase 21, AstroBin CSV), 5.8.1 (keys), 2.1.1 (the export writer's second
caller class named: "generated scripts"), 19.1 (WBPP removed). The user approves 12.13 before the
brief-writer runs.

### Data model

No migration. New `general` keys: `wbpp_default_os` (`powershell` or `bash`, default
`powershell`), `wbpp_staging_path` (string or null), `wbpp_exclusions` (string list, the web's
default list), `wbpp_quality_enabled` (bool, false), `wbpp_quality_baseline` (`session` or
`rig`, `session`), `wbpp_quality_raw_constraints` (list of `{metric, op, value, enabled}`).
`wbpp_library_root` is not ported: the scan root containing the frame is the root. The script is
written through `AppWriter.BeginExport` as a new file at the path the save dialog returned; the
dialog's own overwrite prompt is the only way an existing file is replaced. No other write
(rule 1).

### Task table

| Task | Model | Files | Verify |
| --- | --- | --- | --- |
| 1. Spec-writer: 12.13 written, 12.4, 5.8.1, 2.1.1 and 19.1 amended; user approves 12.13. | opus | `docs/design-spec.md` | Zero em or en dashes; the "never copies a file itself" sentence present; every key in 5.8.1. |
| 2. Folder levels: `Core/Wbpp/FolderLevels.cs` (ancestor chain to the scan root, longest common ancestor, contamination from a supplied path set, default pick, staging name disambiguation, subtree bytes with null on unknown) and `Data/Queries/WbppPathsQuery.cs` (paths and sizes for the selected nights and for the whole catalogue). | opus | `Core/Wbpp/FolderLevels.cs`, `Data/Queries/WbppPathsQuery.cs`, tests | Level table on a constructed tree; contamination counts; collision rename; null bytes propagate. |
| 3. Quality filter: `Core/Wbpp/QualityFilter.cs` (constraints, verdicts copy, unmeasured, exclude with failure sentences, overrides, totals) reusing `FrameQuality` and the 14A grades; the chips, presets, tally, baseline toggle and verdict table on the page. | opus | `Core/Wbpp/QualityFilter.cs`, `ViewModels/TargetDetail/Wbpp/{QualityPanelViewModel,VerdictRowViewModel,ConstraintChipViewModel}.cs`, `Views/TargetDetail/Wbpp/QualityPanelView.axaml` | Verdict truth table per metric and comparator; unmeasured rule; override; totals; constraints persist. |
| 4. Script generators: `Core/Wbpp/ScriptGenerator.cs` for PowerShell and Bash, ported line by line from `wbpp_export.py` (quoting, exclusions, per-file excludes, unblock step, the header comment naming target and nights). | sonnet | `Core/Wbpp/ScriptGenerator.cs`, tests | Golden files for both scripts on a fixed input; quoting of a path with an apostrophe and a dollar sign; the script contains no delete, move or rename verb (a string scan). |
| 5. Export page: `WbppExportViewModel` as a modal page from the target page's Export flyout with the selected nights, the level rows with their radio trees and cost badges, the settings disclosure with Save as defaults, the empty-export warning, the footer totals, the script type menu, Generate writing through a save dialog and `BeginExport`, then Copy script and Show script. | opus | `ViewModels/TargetDetail/Wbpp/{WbppExportViewModel,SessionLevelViewModel,LevelRowViewModel}.cs`, `Views/TargetDetail/Wbpp/WbppExportWindow.axaml(.cs)`, `ViewModels/TargetDetail/TargetDetailViewModel.cs`, `Views/TargetDetail/TargetDetailView.axaml` | Page opens with the selected nights; level change updates the totals; Generate with a cancelled dialog writes nothing; with a path writes one new file (`FileSafetyTest`, `BeginExport` allowlist gains the one file by full path). |
| 6. Help topics: the four-step page topic and the chip and column topics. | sonnet | `Core/Help/HelpTopics.cs`, the page views | Census extended. |
| 7. Docs and handoff: spec reconciled, HANDOFF, TRACKING, the Status line, the Phase 17 prompt. | sonnet | docs only | Zero em or en dashes. |

Order: Task 1; Tasks 2, 3 and 4 together (disjoint, all pure); Task 5 after all three; Task 6
after Task 5; Task 7 last.

### Fixtures

The Phase 12 fixture's nights sit in flat folders, so every level is the same. One addition
under `docs/superpowers/work/phase16/fixtures/`: the three nights regenerated under a nested
tree (`<root>/M 31/2025-03-20/LIGHT/Ha/`), with a second target's frames sharing the
`2025-03-20` folder so contamination has a value, and file sizes recorded by the scan. The
Phase 12 recipe produces it with a path template change.

### Verification bar

Amended by user ruling U5 of 2026-09-19, which applies to every phase from 14C on: the
verification agent does not walk the running application screen by screen and repeats nothing per
theme, so the launched-app items below are not run as written. The clean-copy check keeps the
build with 0 warnings, the full suite, the headless CLI scan and the byte-identity proof over the
fixture library, both profile folders and the user's own files; an implementer's one targeted look
at the screen it changed covers what it can, and whatever it cannot is recorded in HANDOFF section
7 as unverified on a launched application. The bar below is left as written.

1. Check two nights, open Export for stacking: the page names the target, 2 nights and the
   frame count; each night's level tree shows the assembled path and a cost badge on the shared
   folder; the default level drags nothing along.
2. Enable the filter, set HFR at most 5: the tally drops the two outliers; the verdict table
   marks them with the failure sentence; the override checkbox restores one.
3. Generate PowerShell: the save dialog opens; cancel writes nothing; choose a folder: one `.ps1`
   file appears, its text equals Show script, its run command includes the unblock step; the
   Bash variant likewise. Both scripts contain no delete, move or rename.
4. Save as defaults; reopen: the staging path and exclusions are prefilled.
5. Fixture hash identical before and after; the only new file is the script at the dialog path;
   profile folders untouched per HANDOFF 5.2 item 1.

### Size

Estimate: 7 to 9 agent-days, one session. The page is large (the web's modal is 1,241 lines
plus 1,050 in its four components) but the pure parts are well specified by their Python.

### Risks and rulings needed

1. In-application copy (the web's Chromium copy). The backlog's ruling is script only. Proposed
   answer: script only in Phase 16; an in-application copy of new files into a dialog-chosen
   folder is recorded as a candidate that needs its own spec 2.1.1 amendment and a second
   confirmation from the user, since it is a new class of write.
2. Script default: PowerShell on Windows. Proposed answer: `wbpp_default_os` defaults to
   `powershell`; Bash stays for WSL and network-share users.
3. Contamination over the whole catalogue costs one query per open. Proposed answer: one query
   of `file_path` for every image, computed once per page open, with the 5,000 frame case timed
   in the verification bar.
4. The `BeginExport` allowlist in `FileSafetyTest` names callers by full path. Proposed answer:
   `WbppExportViewModel.cs` joins the list with no existing entry changed (HANDOFF rule 1).

### Next session prompt

```
Read docs/superpowers/HANDOFF.md, then docs/superpowers/TRACKING.md, then the P15B lines at the tail of docs/superpowers/progress.md, then docs/parity-roadmap.md section Phase 16, and open Phase 16 from its task table.
```

---

## Phase 17: Analysis page

Goal: a new navigation page, Analysis, after Statistics: a shared filter bar (equipment
combination, filter, frame or session granularity, date range) and five tabs, Correlation
(scatter with trend line, confidence band and IQR outlier hiding), Distributions (histogram with
skewness and a box plot grouped by filter, equipment, month or target), Time Series (points with
a moving average and a band), Matrix (Pearson r for every environmental metric against every
quality metric) and Compare (two equipment or filter groups side by side), each with summary
stats cards. Queries live in Data; charts go through `ChartTheme`; the PHD2 night metrics of
Phase 15A join the Correlation X list.

Status: DONE, commit `988e678` on `snd`, `8196` tests at the close. Run as
nine task units plus a records-first step, a pre-code seam review, a chart spike, a fixture agent,
a real-data preparation agent with a Python oracle, a per-unit review of every unit, two spine
agents, the phase review with eight file-disjoint fixers, a fix-wave re-review with seven more, a
launched-app look, and the clean-copy and real-data verifications.

**Three statements in the sections below were wrong when this plan was written and are corrected
here rather than left standing**, because the plan is read as input by the session that opens the
phase:

- **Session granularity takes the median, and that is exact parity rather than a departure.** The
  "Risks and rulings needed" item below says the web groups with means and proposes the median as
  the port's improvement. `analysis.py` already takes `statistics.median` over each group, at line
  430 for correlation, 524 for the distribution and 716 for the time series. The port takes the
  median because the web does.
- **The session grouping key is the night and the resolved target, with no rig.** The Task 3 row
  below says "by `session_date` and rig". `analysis.py` builds the key as
  `(str(r.night), r.resolved_target_id)` at lines 421 and 523. A night on which two rigs imaged
  one target yields one point, not two.
- **HFR converts to arcseconds on the Compare tab only.** The Task 3 row below puts the conversion
  on every query. `_PIXEL_METRICS` is read at `analysis.py` line 843, inside `/compare`, and
  nowhere else; the other four tabs plot pixels with a visible warning when the selection spans
  more than one plate scale.

All three are settled in spec 12.14, which the user approved, and it is the spec and not this
section that binds a later phase.

### What the web does

- `frontend/src/pages/AnalysisPage.tsx`: a heading with a help popover, a Shared Filters card
  (equipment combination listing every telescope plus camera pair with a grouped marker, a filter
  select, a Per frame or Per session toggle, date from and to with an inline error when reversed),
  five tabs that keep their state once visited, and a navigation hook that opens Correlation on a
  named pair.
- `frontend/src/components/analysis/CorrelationTab.tsx` and `CorrelationChart.tsx`: X from ten
  environmental and equipment metrics (humidity, wind speed, ambient temperature, dew point,
  pressure, cloud cover, sky quality, focuser temperature, airmass, sensor temperature) plus the
  five PHD2 night metrics (RMS total, RA, Dec, star-lost percent, mean SNR); Y from ten quality
  metrics (HFR, FWHM, eccentricity, guiding RMS total, RA, Dec, detected stars, ADU mean, median,
  stdev); a hide-outliers checkbox; two stats cards.
- `backend/app/api/analysis.py` (933 lines): correlation returns points capped at 5,000 by
  downsampling while trend and stats use the full set, Pearson r, Spearman rho, a least-squares
  trend line with a confidence band, IQR outlier flags, HFR converted to arcseconds through each
  frame's plate scale; distribution returns bins and skewness; boxplot returns quartiles,
  whiskers and outliers per group; timeseries returns daily points with a moving average and
  band; matrix returns Pearson r and n per X and Y pair with a 10 point minimum; compare returns
  two groups' summary stats and box plots.
- `DistributionsTab.tsx`, `HistogramChart.tsx`, `BoxPlotChart.tsx`, `TimeSeriesTab.tsx`,
  `TimeSeriesChart.tsx`, `MatrixTab.tsx`, `CompareTab.tsx`, `StatsCard.tsx`: the tab controls
  named above; the matrix cells coloured by r with n on hover and a click that opens Correlation
  on that pair; the stats card with count, mean, median, stdev, min, max, p25, p75.

### What the port has

- `ViewModels/MainWindowViewModel.cs`: the navigation table (dashboard, statistics, activity,
  diagnostics, settings) with lazy page builders; `Views/MainWindow.axaml` rail of initials.
- `ViewModels/Stats/*`, `Views/StatisticsView.axaml`, `Data/Queries/{StatsQuery,StatsCache,StatsModels}.cs`:
  the sectioned page shape, the cached aggregate, `StatsBarChartViewModel`.
- `Theme/ChartTheme.cs`; LiveCharts 2.0.0-rc5.4 with scatter, line, column, box and heat series
  available; `Controls/MetricChartView.axaml` and `ViewModels/TargetDetail/MetricChartViewModel.cs`
  for the pill row shape.
- `ViewModels/Dashboard/FilterPanelViewModel.cs`: the equipment combo boxes and date pickers to
  reuse by shape; `Core/Metrics/Statistics.cs` for median and MAD.
- `images` carries every X and Y column (spec 7.1); 15A's `phd2_sessions` for the night metrics.

### Spec work

New section 12.14 "Analysis": the filter bar, the five tabs with their controls, the metric
lists, the statistics definitions (Pearson, Spearman, trend and band, IQR rule, skewness, the
moving average window, the 10 point matrix minimum, the 5,000 point cap), the empty states. Amend
12 (the navigation table gains Analysis after Statistics), 13 (five chart rows: scatter, column
histogram, box, line with band, heat matrix), 5.8.2 if the tab state persists
(`display.analysis` with the last tab and the last metric pair), 19.1 (analysis page removed).
The user approves 12.14 before the brief-writer runs.

### Data model

No migration. `display.analysis` gains `tab`, `x_metric`, `y_metric`, `granularity` (defaults
`correlation`, `humidity`, `hfr`, `frame`). Reads only, through `Data/Queries/AnalysisQuery.cs`
with one method per tab; the matrix runs as one pass over the filtered rows in C# because SQLite
has no `corr()`. Results are cached per filter key for five minutes in `AnalysisCache`, cleared
when a scan completes or settings change, the `StatsCache` pattern. Nothing is written outside
`user_settings` (rule 1).

### Task table

| Task | Model | Files | Verify |
| --- | --- | --- | --- |
| 1. Spec-writer: 12.14 written; 12, 13, 5.8.2 and 19.1 amended; user approves 12.14. | opus | `docs/design-spec.md` | Zero em or en dashes; every metric key listed; every statistic defined with its formula source. |
| 2. Statistics core: `Core/Metrics/Analysis.cs` (Pearson, Spearman, least-squares trend with the band, IQR outliers, histogram bins, skewness, quartiles and whiskers, moving average, downsampling) as pure functions. | opus | `Core/Metrics/Analysis.cs`, tests | Each function against values computed by hand and against the web's Python on the same inputs recorded in the test. |
| 3. Queries: `AnalysisQuery` with correlation, distribution, boxplot, timeseries, matrix and compare over the shared filter, the equipment combination list with the grouped marker, HFR to arcseconds per frame, session granularity by `session_date` and rig, the PHD2 night join; `AnalysisCache`. | opus | `Data/Queries/{AnalysisQuery,AnalysisModels,AnalysisCache}.cs`, tests | Each method on a seeded database; filter application; the cap; the 10 point minimum; cache invalidation on scan completion. |
| 4. Page shell and filter bar: the navigation item, `AnalysisViewModel` with the shared filter, the five tab view-models created lazily and kept, the date range error, persisted tab and pair. | opus | `ViewModels/Analysis/{AnalysisViewModel,SharedFilterViewModel}.cs`, `Views/Analysis/AnalysisView.axaml`, `MainWindowViewModel.cs`, `Core/Settings/DisplaySettings.cs` | Rail shows the initial; filter change refreshes the visible tab only; reversed dates block the query and show the sentence; persistence round trip. |
| 5. Correlation and Matrix tabs: scatter with trend, band and outlier hiding, the X and Y pickers with the PHD2 group, two stats cards; the matrix as a heat series with r ink, n on hover and click-to-correlation. | opus | `ViewModels/Analysis/{CorrelationTabViewModel,MatrixTabViewModel,StatsCardViewModel}.cs`, `Views/Analysis/{CorrelationTabView,MatrixTabView}.axaml` | Series point counts; trend endpoints; outlier toggle; matrix cell count 100; click sets the pair. |
| 6. Distributions, Time Series and Compare tabs: histogram with skewness, box plot by group, the time series with its average and band, the two-group compare with cards and box plots. | opus | `ViewModels/Analysis/{DistributionsTabViewModel,TimeSeriesTabViewModel,CompareTabViewModel}.cs`, `Views/Analysis/*.axaml` | Bin counts sum to n; box figures equal the query; the average window; compare refuses the same group twice. |
| 7. Help topics for the page, the filter bar and each tab, from the web's popover texts. | sonnet | `Core/Help/HelpTopics.cs`, the views | Census extended. |
| 8. Docs and handoff: spec reconciled, HANDOFF, TRACKING, the Status line, the Phase 18 prompt. | sonnet | docs only | Zero em or en dashes. |

Order: Task 1; Tasks 2 and 3 together (Task 3 codes against Task 2's signatures agreed in the
brief); Task 4 after Task 3; Tasks 5 and 6 together after Task 4; Task 7; Task 8 last.

### Fixtures

The Phase 12 fixture carries HFR and eccentricity only. One addition under
`docs/superpowers/work/phase17/fixtures/`: the three nights regenerated with weather, focuser,
mount and ADU header keywords varying frame by frame with a known linear relation between
humidity and HFR (so r is predictable), a second telescope on one night, and 15A's guide log so
the PHD2 X metrics have a value. The Phase 12 recipe produces it with additional header
keywords; the guide log comes from the 15A generator.

### Verification bar

Amended by user ruling U5 of 2026-09-19, which applies to every phase from 14C on: the
verification agent does not walk the running application screen by screen and repeats nothing per
theme, so the launched-app items below are not run as written. The clean-copy check keeps the
build with 0 warnings, the full suite, the headless CLI scan and the byte-identity proof over the
fixture library, both profile folders and the user's own files; an implementer's one targeted look
at the screen it changed covers what it can, and whatever it cannot is recorded in HANDOFF section
7 as unverified on a launched application. The bar below is left as written.

1. The rail shows Analysis after Statistics; the page opens on Correlation with humidity
   against HFR and draws the constructed relation with r near the constructed value; hiding
   outliers removes the planted point.
2. Switching to the second rig in the filter bar changes every tab's data; Per session reduces
   the point count to the night count; a reversed date range shows the sentence and runs
   nothing.
3. Distributions: the histogram bins sum to the frame count; the box plot by filter shows Ha and
   OIII; by month shows three groups.
4. Time Series: three daily points with the average line; Matrix: 100 cells, most empty on the
   small fixture, the humidity row coloured; clicking it lands on Correlation with that pair.
5. Compare: Ha against OIII shows two cards and two boxes.
6. All five tabs in Red Light and Glass Void; every heading has a help glyph.
7. Profile folders untouched per HANDOFF 5.2 item 1.

### Size

Estimate: 8 to 10 agent-days, one session. Six charts and six queries; each is small, the count
is the size.

### Risks and rulings needed

1. LiveCharts box and heat series at rc5.4: both exist; their axis and tooltip theming has not
   been exercised in the port. Proposed answer: Task 5 and Task 6 each carry a headless render
   case; if a series fails to theme, it is drawn as a control like the night strip.
   **Answered by a chart spike run before the tab briefs were written: all five chart kinds theme
   and none is drawn as a control.** The spike found three shapes rc5.4 does not have, each
   settled in spec 13, and two defects outside the phase: no chart control's tooltip or legend
   paints followed a theme change, and no axis title was painted from a token at all.
2. Session granularity: the web groups by `session_date` and rig with means. Proposed answer: the
   same, with the night's median rather than mean for the quality metrics, stated in 12.14.
   **Answered, and the premise was wrong twice over: see the three corrections at the head of this
   section.** The web already takes the median, and the key carries no rig.
3. Navigation count: the backlog calls Analysis the fifth page; the port already has five items
   with Diagnostics. Proposed answer: Analysis is the sixth item, placed after Statistics.
   **Answered as proposed.**

### Next session prompt

```
Read docs/superpowers/HANDOFF.md, then docs/superpowers/TRACKING.md, then the P16 lines at the tail of docs/superpowers/progress.md, then docs/parity-roadmap.md section Phase 17, and open Phase 17 from its task table.
```

---

## Phase 18: Mosaics A, detection, suggestions, list and detail

Goal: the port gains mosaics as data and as pages: panel detection over target names and sky
positions with configurable keywords, a campaign gap and a position tolerance; suggestions with
confidence and source badges, review notes, a session table and accept or dismiss; a Mosaics
page listing mosaics with sort, column picker, create, rename, expand, add panel, delete and the
Needs Review flag; a mosaic detail page with the summary line, notes, available panel labels,
per-panel included and available sessions with include, remove, include all and include as new
panel, delete panel, and per-panel integration figures with the deficit stated as a number;
create-mosaic from the target page's selected nights; the mosaic link on a dashboard target row.
The arranger and the composite are Phase 19.

Status: ON HOLD (user ruling 2026-09-21: mosaics skipped for now)

### What the web does

- `backend/app/services/panel_tokens.py`, `mosaic_detection.py` (974 lines),
  `mosaic_suggestions.py`: a name carrying a keyword and a number ("M 31 Panel 2", "NGC 7000 P3")
  yields a base name and a panel number; targets are grouped by base name and, when frames carry
  RA and Dec, by single-link clustering of their robust median centres within a tolerance derived
  from the field of view (focal length, pixel size, `NAXIS`) or the configured arcminutes; each
  group is scored high or low confidence with flags naming the reasons (positions not distinct,
  one panel, mixed keywords), tagged by discovery source name, position or both; sessions split
  into campaigns by the gap setting with a date range suffix; a dedup signature keeps dismissed
  suggestions from resurfacing.
- `frontend/src/components/settings/MosaicsTab.tsx` (1,572 lines) under `pages/MosaicsPage.tsx`:
  the keywords panel (add, remove, collapsed state remembered), Run Detection with progress
  toasts, the suggestions panel with count, text filter past four, campaign gap select, badges,
  totals line reflecting checked panels, the sortable session table with panel checkboxes, review
  notes, a read-only tile preview, accept and dismiss with confirmation, bulk accept and dismiss
  with a progress bar; the mosaics table (name, panel count, integration, frames, date range,
  custom columns, sort persisted), Needs Review pill and Clear All Reviews, the column picker,
  Create Mosaic (name on Enter), row expand with rename, panel list with Remove and an Add Panel
  search form, Delete with confirm and bulk delete.
- `frontend/src/pages/MosaicDetailPage.tsx` (867 lines): name with inline rename, help, panel
  count, total integration and frames; actions menu with Export panels (CSV) and Delete mosaic;
  a Composite button; custom column values; notes with autosave; a review banner with Include
  All on legacy mosaics; an available panel labels banner with Add panel; the arranger; per-panel
  Included and Available session tables (date, per-filter frame counts, frames, integration) with
  include, remove, include all, include as new panel prompting for a label; the available count
  badge; Delete panel when empty.
- `frontend/src/components/CreateMosaicDialog.tsx`: from the target page's checked sessions, new
  or existing mosaic, a prefilled name from the target and month range, a panel label per session
  with rows sharing a label combined, a duplicate name error inline.
- `frontend/src/components/TargetRow.tsx`: a mosaic link on a dashboard target row whose target
  is a panel.
- `backend/app/api/mosaics.py` (1,055 lines), `services/mosaic_stats.py`,
  `panel_membership.py`: the routes above; `images.panel_label` and `images.panel_id` resolved at
  ingest from the `OBJECT` token and re-linked when a panel is created.

### What the port has

- `Data/Repositories/TargetRepository.cs`, `TargetWriteRepository.cs`, `Data/Queries/TargetSearchQuery.cs`
  (the debounced search the add-panel form reuses), `TargetListingQuery.cs` (the dashboard row
  gains the link).
- `Data/Ingest/ScanWriter.cs` (where `panel_label` is set at ingest), `ScanCoordinator.cs`,
  14B's `JobRegistry` (the detection pass), `Data/Repositories/TargetEnrichmentRepository.cs`
  (RA, Dec, size, the frame `RA` and `DEC` headers already extracted per spec 7.1).
- `ViewModels/Settings/AliasGroupViewModel.cs` and `SuggestionViewModel.cs`: the accept-or-dismiss
  suggestion shape with remembered dismissals.
- 14A's night selection and `Open(target, night)`; `Views/ModalPageWindow.cs`; the page pattern
  of `TargetDetailView` for the detail page; `ViewModels/Settings/ColumnPickerViewModel.cs`.
- `Core/Sessions/AstroNight.cs` (great-circle separation helpers do not exist yet; `Core/Catalogs`
  has coordinate parsing).

### Spec work

New sections 5.19 to 5.22 for the four tables and the two `images` columns; 7.7 "Panel tokens
and mosaic detection" (the token grammar, the grouping and scoring rules, campaign splitting,
the signature); 12.15 "Mosaics" (the page, the suggestions, the list, the detail page, the
create dialog, the dashboard link); 5.8.1 keys; 5.8.2 `display.columns.mosaics`; 10.3 (panel
label at ingest); 12.2 (the link); 12.4 (Create mosaic in the Export flyout's sibling, Actions);
19.1 (mosaics removed). The user approves 7.7 and 12.15 before the brief-writer runs.

### Data model

One migration, taking the next free ordinal when it opens (Phase 15B took the fifth for its
own `ended_at_local` column and Phase 20 took the sixth for `custom_column_values` under the
user's 2026-09-21 ruling to build custom columns first): `mosaics` (id, name unique,
notes, created_at, updated_at,
rotation_angle, pixel_coords, needs_review), `mosaic_panels` (id, mosaic_id cascade, target_id,
panel_label, sort_order, object_pattern, grid_row, grid_col, rotation 0 90 180 270, flip_h,
unique on mosaic, target and label), `mosaic_panel_sessions` (id, panel_id cascade,
session_date, status `included` or `available`, unique on panel and date), `mosaic_suggestions`
(id, suggested_name, base_name, target_ids JSON, panel_labels JSON, panel_patterns JSON,
session_dates JSON, status, created_at, confidence, discovery_source, geometry JSON, flags JSON,
dedup_signature), and `images.panel_label`, `images.panel_id` with their indexes. The arranger
columns ship now, unused until Phase 19, so Phase 19 adds no migration. New `general` keys:
`mosaic_keywords` (string list, default `["Panel", "P"]`), `mosaic_campaign_gap_days` (int, 0),
`mosaic_position_tolerance_arcmin` (double, 0 meaning derived). `display.columns.mosaics`
default `["name", "panels", "integration", "frames", "date_range"]`. Export panels (CSV) writes
a new file at a save-dialog path through `BeginExport`. Every other write is catalogue only
(rule 1).

### Task table

| Task | Model | Files | Verify |
| --- | --- | --- | --- |
| 1. Spec-writer: 5.19 to 5.22, 7.7, 12.15 written; 5.8.1, 5.8.2, 10.3, 12.2, 12.4, 19.1 amended; user approves 7.7 and 12.15. | opus | `docs/design-spec.md` | Zero em or en dashes; every column named; the token grammar stated with examples. |
| 2. Tokens and detection core: `Core/Mosaics/PanelTokens.cs` (match, strip, pattern, exact regex, label) and `MosaicDetection.cs` (robust centre, field of view, separation, candidates, clustering, scoring, campaign split, suffix, unique name, signature) as pure functions over records. | opus | `Core/Mosaics/{PanelTokens,MosaicDetection}.cs`, tests | Token table from the web's tests; grouping on constructed targets by name only, by position only, by both; scoring flags; campaign split; signature stability. |
| 3. Storage and passes: the migration and entities; `MosaicRepository` (create, rename, delete, panels, sessions, suggestions, needs review, clear reviews); `ScanWriter` sets `panel_label` and links `panel_id`; `Data/Ingest/MosaicDetectionPass.cs` gathering per-target records and writing suggestions under the job registry; retro-link on panel creation; stale panel session pruning. | opus | `Data/Entities/Mosaic*.cs`, `Data/Migrations/<stamp>_Mosaics.cs`, `GalactiLogContext.cs`, `Data/Repositories/MosaicRepository.cs`, `Data/Ingest/{MosaicDetectionPass,ScanWriter}.cs`, `Data/Queries/MosaicQueries.cs` | Migration applies and rolls back; ingest labels; the pass writes suggestions and skips dismissed signatures; accept creates the mosaic and links frames; stats per panel (integration, frames, per-filter, last session, available count). |
| 4. Mosaics page: navigation item; keywords panel; Run Detection; the suggestions list with badges, filter, campaign gap, totals, session table, review notes, accept, dismiss, bulk actions with progress; the mosaics table with sort, column picker, create, expand with rename, panels and add panel search, delete with confirm, bulk delete, Needs Review and Clear All Reviews. | fable | `ViewModels/Mosaics/{MosaicsPageViewModel,SuggestionRowViewModel,MosaicRowViewModel,AddPanelViewModel}.cs`, `Views/Mosaics/MosaicsView.axaml`, `MainWindowViewModel.cs`, `Core/Settings/{GeneralSettings,DisplaySettings}.cs` | Each action against the repository stub; sort persists; totals follow the checked panels; dismiss asks; bulk progress counts. |
| 5. Mosaic detail page: header with rename, summary line, notes autosave, actions (Export panels CSV through a save dialog, Delete with confirm), the review banner with Include All, the available labels banner with Add panel, the per-panel session tables with the five actions and the count badge, Delete panel when empty; the deficit per panel as a number against the leading panel; a placeholder band where Phase 19's arranger goes. | opus | `ViewModels/Mosaics/{MosaicDetailViewModel,PanelViewModel,PanelSessionViewModel}.cs`, `Views/Mosaics/MosaicDetailView.axaml(.cs)`, `Data/Queries/MosaicQueries.cs` | Include and remove refresh the totals; include as new panel prompts with the next suffix; delete panel guarded; CSV golden file; `FileSafetyTest`. |
| 6. Create mosaic and the dashboard link: the dialog from the target page's selected nights (new or existing, prefilled name, label per session, combined rows, duplicate error inline); the link on a dashboard row whose target is a panel, opening the mosaic detail page. | sonnet | `ViewModels/TargetDetail/{CreateMosaicViewModel,TargetDetailViewModel}.cs`, `Views/TargetDetail/CreateMosaicWindow.axaml`, `ViewModels/Dashboard/TargetRowViewModel.cs`, `Views/Dashboard/TargetListView.axaml`, `Data/Queries/TargetListingQuery.cs` | Prefill name; label combining; duplicate refused inline; the link present only for panel targets. |
| 7. Help topics for the page, the suggestions, the table, the detail page and the dialog. | sonnet | `Core/Help/HelpTopics.cs`, the views | Census extended. |
| 8. Docs and handoff: spec reconciled, HANDOFF, TRACKING, the Status line, the Phase 19A prompt. | sonnet | docs only | Zero em or en dashes. |

Order: Task 1; Task 2 alone; Task 3 after Task 2; Tasks 4, 5 and 6 together after Task 3 on
disjoint files; Task 7; Task 8 last.

### Fixtures

A new generator under `docs/superpowers/work/phase18/fixtures/`: four targets "NGC 7000 Panel 1"
to "Panel 4" with RA and Dec headers on a 2 by 2 grid one field apart, a focal length and pixel
size giving a known field of view, two nights each, one panel with half the integration of the
others; a fifth target "IC 1396 P1" alone (one panel, low confidence); a sixth pair of "Sh2-155
Panel 1" and "Panel 2" with identical positions (positions not distinct). The Phase 12 recipe
produces it with per-target header sets. The offline catalogue resolves NGC 7000, IC 1396 and
Sh2-155 to their base names.

### Verification bar

Amended by user ruling U5 of 2026-09-19, which applies to every phase from 14C on: the
verification agent does not walk the running application screen by screen and repeats nothing per
theme, so the launched-app items below are not run as written. The clean-copy check keeps the
build with 0 warnings, the full suite, the headless CLI scan and the byte-identity proof over the
fixture library, both profile folders and the user's own files; an implementer's one targeted look
at the screen it changed covers what it can, and whatever it cannot is recorded in HANDOFF section
7 as unverified on a launched application. The bar below is left as written.

1. Run Detection reports through the job monitor and the feed; the suggestions list shows
   NGC 7000 high confidence by both, IC 1396 low with its flag, Sh2-155 low with "positions not
   distinct"; the totals line follows the checked panels.
2. Accept NGC 7000: the mosaics table lists it with 4 panels and the summed figures; the
   dashboard rows of the four panels carry the mosaic link.
3. Dismiss IC 1396 with confirmation; Run Detection again: it does not resurface.
4. Open NGC 7000: the summary line, the per-panel tables, the deficit number on the short panel;
   remove a session and include it again with the totals following; include as new panel
   creates "Panel 5" with the prefilled label; Export panels writes one CSV at the dialog path.
5. Create a mosaic from two selected nights of the target page: new with the prefilled name;
   existing when one includes the target; a duplicate name is refused inline.
6. Fixture hash identical before and after; profile folders untouched per HANDOFF 5.2 item 1.

### Size

Estimate: 11 to 13 agent-days, one session. The page is the largest single view in the web
(1,572 lines) and the detail page is 867 more; detection is 1,400 lines of pure Python.

### Risks and rulings needed

1. Page placement: the web has a Mosaics page and a Mosaics settings tab that are the same
   component. Proposed answer: one navigation page, Mosaics, after Analysis; no settings tab; the
   keywords panel lives on the page as the web places it.
2. Field of view needs focal length and pixel size from headers. Proposed answer: the
   `mosaic_position_tolerance_arcmin` key overrides when headers lack them, exactly the web's
   rule, and the help topic says so.
3. `pixel_coords` and `rotation_angle` are arranger state stored now. Proposed answer: stored
   and unused in Phase 18 so Phase 19 adds no migration.
4. Export panels CSV is a second `BeginExport` caller in one phase after Phase 16. Proposed
   answer: the allowlist gains `MosaicDetailViewModel.cs` by full path.

### Next session prompt

```
Read docs/superpowers/HANDOFF.md, then docs/superpowers/TRACKING.md, then the P17 lines at the tail of docs/superpowers/progress.md, then docs/parity-roadmap.md section Phase 18, and open Phase 18 from its task table. Before anything else, check that a scan-fix commit follows the Phase 17 commit on snd: git log --oneline snd -5 should show a commit naming the scan abort fix above 988e678. If it is not there, that fix is owed before Phase 18 opens: HANDOFF section 1 names the file, the line and the defect.
```

The trailing sentence is appended by the Phase 17 close and is not part of section 0's standing
form: the user ruled the scan abort fix belongs to the session that closed Phase 17 and not to
this one, so the session that opens Phase 18 checks for it rather than assuming it. HANDOFF
section 2 carries this block verbatim, which is the rule that the two are the same text.

---

## Phase 19A: Mosaics B, the arranger

Goal: the mosaic detail page's placeholder band becomes the panel arranger, a Skia-drawn
control: one tile per panel showing that panel's best frame thumbnail for the chosen filter,
dragged freely and saved after a debounce, selected by click, rotated in 90 degree steps and
flipped horizontally with a state badge, faded by an opacity slider while aligning, labelled or
not by a Labels toggle, carrying its integration and a colour-coded deficit overlay, under a
global rotation slider, zoom with fit, minus, plus, a percentage readout and wheel zoom at the
cursor, a filter selector, a canvas height grip, a Saving indicator and Reset All. Phase 19 is
split: 19A is the arranger, 19B is the composite lightbox and the panel thumbnail service both
share. The web's drop-to-swap is documented and not wired, so it is not a target.

Status: ON HOLD (user ruling 2026-09-21: mosaics skipped for now)

### What the web does

- `frontend/src/components/mosaics/KonvaMosaicArranger.tsx` (1,340 lines): tiles at
  `grid_col`, `grid_row` (stored as free canvas coordinates rounded to integers, null meaning
  auto layout from `sort_order`), rotation and flip per panel, a mosaic-level rotation angle;
  drag moves a tile and saves the layout through `PUT /{mosaic_id}/panels/batch` after a short
  debounce with a Saving note; click selects a tile and enables Rotate CW and Flip H, right-click
  rotates directly, clicking empty canvas or the tile again deselects; Fit, minus, plus and a
  percentage readout control zoom, the wheel zooms at the cursor, dragging empty canvas pans; a
  slider rotates the whole mosaic between -180 and 180 with a readout and a reset; Reset All
  clears every tile rotation and flip and the global rotation; the opacity slider fades the
  selected tile from 100 to 20 percent and is not saved; Labels toggles every text overlay; each
  tile carries its label, a rotation and flip badge, its total integration at bottom right and a
  deficit at top right, green within 20 percent of the leading panel, amber within 60 percent,
  red below; a filter dropdown swaps every tile's thumbnail with a Loading state; a corner grip
  drags the canvas taller or shorter.
- `backend/app/api/mosaics.py` `GET /{mosaic_id}/panels/thumbnails` and
  `GET /{mosaic_id}/panels/{panel_id}/thumbnail`, `services/mosaic_composite.py`
  `score_frames` (detected stars 0.35 higher better, HFR 0.30 lower better, eccentricity 0.15
  lower better, the rest from the file), `select_best_frame_for_filter`, `find_default_filter`,
  `generate_panel_thumbnail` (MTF stretch), a per-panel per-filter thumbnail cache.
- The suggestions panel's read-only tile preview (`MosaicsTab.tsx`) uses the same arranger in a
  read-only mode with the suggestion's geometry.

### What the port has

- `Controls/NightStrip.cs`, 15B's `GuideGraph.cs` and `AltitudeArc.cs`: drawn controls with
  hover, click, wheel and drag handled in the control and the state on a view-model; the
  no-colour-literal rule; the headless render tick.
- `Core/Imaging/{ThumbnailRenderer,MtfStretch,Debayer,Resampler}.cs`, `Services/ThumbnailCache.cs`
  and `ThumbnailWorker.cs`: frame thumbnails under `frames/` keyed by image id, generated off
  the UI thread, written through `AppWriter`.
- 18's `MosaicRepository` (panels with `grid_row`, `grid_col`, `rotation`, `flip_h`, the mosaic
  `rotation_angle`), `MosaicDetailViewModel` with its placeholder band, the per-panel stats.
- `ViewModels/Preview/PreviewModalViewModel.cs`: the zoom transform pattern (scale about a point,
  pan, reset).
- `Services/Debouncer.cs` for the layout save.

### Spec work

Amend 12.15 (the arranger's parts, gestures, badges, the deficit thresholds and inks, the save
rule and what is not saved), 13 (a chart row for the arranger as a drawn control), 11.3 (the
panel thumbnail cache under `mosaics/<panel id>/<filter>.jpg` with its key and eviction), 11.4
(the best-frame score), 14.1 if a token is added for the tile badge. The user approves the 12.15
amendment before the brief-writer runs.

### Data model

No migration (Phase 18 shipped the columns). Writes: `mosaic_panels.grid_row`, `grid_col`,
`rotation`, `flip_h` and `mosaics.rotation_angle` through `MosaicRepository.UpdateLayout` in one
transaction; panel thumbnails under the thumbnail cache root through `ThumbnailCache` and
`AppWriter`. Opacity, zoom and the Labels state are view state and are not stored. Nothing else
is written (rule 1).

### Task table

| Task | Model | Files | Verify |
| --- | --- | --- | --- |
| 1. Spec-writer: 12.15, 13, 11.3, 11.4 and 14.1 amended; user approves. | opus | `docs/design-spec.md` | Zero em or en dashes; every gesture and overlay of the web list named or explicitly dropped; the not-saved list stated. |
| 2. Panel thumbnails: `Core/Mosaics/FrameScore.cs` (the weighted score), `Data/Queries/PanelFrameQuery.cs` (best frame per panel per filter, the default filter across panels, the available filters), `Services/PanelThumbnailService.cs` rendering the best frame through `ThumbnailRenderer` into the `mosaics/` cache with the job registry for a batch. | opus | `Core/Mosaics/FrameScore.cs`, `Data/Queries/PanelFrameQuery.cs`, `App/Services/{PanelThumbnailService,ThumbnailCache}.cs` | Score ordering on constructed frames; default filter rule; cache key; render off the UI thread awaited; `FileSafetyTest`. |
| 3. Arranger layout model: `ArrangerViewModel` with tiles (position, rotation, flip, label, integration, deficit band), auto layout from `sort_order` when unplaced, selection, rotate, flip, reset all, global rotation, zoom state (fit, step, wheel at a point, pan), the Labels flag, opacity on the selected tile, the debounced save through the repository with the Saving flag, the filter selector reloading thumbnails. | fable | `ViewModels/Mosaics/{ArrangerViewModel,TileViewModel}.cs`, `Data/Repositories/MosaicRepository.cs` (`UpdateLayout`) | Deficit band table at the thresholds; rotate cycles 0 to 270; reset all; save called once after a burst of moves; zoom transform table; the not-saved list does not reach the repository. |
| 4. Arranger control: `Controls/MosaicArranger.cs` drawing tiles from bitmaps with rotation, flip and opacity, the label, badge, integration and deficit overlays, selection outline, drag with hit testing, right-click rotate, wheel zoom at the cursor, empty-canvas pan, the height grip; a read-only mode for the suggestion preview. | fable | `Controls/MosaicArranger.cs`, `Views/Mosaics/MosaicDetailView.axaml`, `Views/Mosaics/MosaicsView.axaml` (preview) | Hit test table; drag updates the tile; right-click rotates; read-only mode ignores input; no colour literal; headless render tick with four tiles. |
| 5. Toolbar: Rotate CW, Flip H, Reset All, the global rotation slider with readout and reset, Fit, minus, plus, the percentage, Labels, the opacity slider, the filter selector with Loading, the Saving note, wired to the view-model in the shared control vocabulary. | sonnet | `Views/Mosaics/MosaicDetailView.axaml`, `ViewModels/Mosaics/MosaicDetailViewModel.cs` | Buttons enable on selection; slider bounds; the Loading state while thumbnails load; `ControlStyleScanTest`. |
| 6. Help topics for the arranger (the web's long popover) and the toolbar. | sonnet | `Core/Help/HelpTopics.cs`, the view | Census extended. |
| 7. Docs and handoff: spec reconciled, `DESIGN.md` gains the arranger, HANDOFF, TRACKING, the Status line, the 19B prompt. | sonnet | docs only | Zero em or en dashes. |

Order: Task 1; Tasks 2 and 3 together (Task 3 codes against Task 2's thumbnail interface agreed
in the brief); Task 4 after Task 3; Task 5 after Task 4; Task 6; Task 7 last.

### Fixtures

The Phase 18 fixture (NGC 7000, four panels on a 2 by 2 grid, one short panel, two filters)
covers every arranger item. The frames are 64 by 64 synthetic images, so the thumbnails are
flat; one addition to the 18 generator under `docs/superpowers/work/phase19a/fixtures/`: a
gradient across each panel's pixels so rotation and flip are visible. The Phase 12 recipe writes
pixel data and can produce it.

### Verification bar

Amended by user ruling U5 of 2026-09-19, which applies to every phase from 14C on: the
verification agent does not walk the running application screen by screen and repeats nothing per
theme, so the launched-app items below are not run as written. The clean-copy check keeps the
build with 0 warnings, the full suite, the headless CLI scan and the byte-identity proof over the
fixture library, both profile folders and the user's own files; an implementer's one targeted look
at the screen it changed covers what it can, and whatever it cannot is recorded in HANDOFF section
7 as unverified on a launched application. The bar below is left as written.

1. Open NGC 7000: four tiles laid out from `sort_order`, each with its label, integration and
   the deficit overlay in the right ink (the short panel red or amber by its ratio).
2. Drag a tile; Saving appears and clears; relaunch: the position is kept. Select a tile, Rotate
   CW twice and Flip H: the badge reads the state, the gradient shows it; relaunch keeps it;
   Reset All clears every tile and the global slider.
3. The opacity slider fades the selected tile and is not kept across a relaunch; Labels hides
   every overlay; Fit, minus, plus, the wheel at the cursor and empty-canvas pan behave; the grip
   changes the height.
4. The filter selector swaps every tile to OIII with Loading shown while they render.
5. The suggestions panel's preview draws the pending suggestion's tiles read-only.
6. Red Light and Glass Void render the control without a colour literal.
7. Fixture hash identical before and after; the only new files are under the thumbnail cache;
   profile folders untouched per HANDOFF 5.2 item 1.

### Size

Estimate: 9 to 11 agent-days, one session. The web's arranger is 1,340 lines over Konva; the
port draws it by hand, which is why two tasks are fable.

### Risks and rulings needed

1. Free placement against a grid: the web stores rounded canvas coordinates in `grid_row` and
   `grid_col` and does no snapping. Proposed answer: the same, no snapping, so a layout saved by
   either application reads the same.
2. Thumbnail memory: four to sixteen bitmaps at the frame thumbnail width. Proposed answer: the
   arranger decodes at 400 px wide from the cache file and releases on page close.
3. The deficit overlay's three inks against `DESIGN.md`'s colour-only-on-data rule. Proposed
   answer: the overlay is data (a ratio) and uses the four semantic `*Value` colours; no new
   token unless the badge needs one, ruled by the spec-writer.
4. Right-click rotate against the flyout convention. Proposed answer: kept, because the tile
   has no flyout; stated in the help topic.

### Next session prompt

```
Read docs/superpowers/HANDOFF.md, then docs/superpowers/TRACKING.md, then the P18 lines at the tail of docs/superpowers/progress.md, then docs/parity-roadmap.md section Phase 19A, and open Phase 19A from its task table.
```

---

## Phase 19B: Mosaics B, the composite lightbox

Goal: the mosaic detail page's Composite button builds the combined mosaic image for the
selected filter (each panel's best frame stretched, placed by a gnomonic projection of the
panels' sky centres onto a common tangent plane, or by the saved arranger layout when the mosaic
is in pixel coordinates, rotated and flipped as arranged), shows it in a lightbox with a spinner
while it builds and an error with Retry on failure, caches it under app data, and offers a
Download that writes the JPEG as a new file at a save-dialog path.

Status: ON HOLD (user ruling 2026-09-21: mosaics skipped for now)

### What the web does

- `frontend/src/components/mosaics/MosaicCompositeModal.tsx`: opened from the Composite
  button for the current filter; a spinner while `GET /{mosaic_id}/composite` builds; the JPEG
  shown; an error message with Retry; a Download JPEG link named from the mosaic and filter.
- `backend/app/services/mosaic_composite.py` (797 lines): `select_best_frame` per panel by the
  weighted score, `compute_panel_layout` projecting each panel's RA and Dec onto a tangent plane
  (TAN) at the mosaic centre with the plate scale from the frames, `composite_panels` placing
  each stretched tile at its layout position with rotation and flip, the mosaic rotation angle
  applied, `pixel_coords` mode using the arranger's positions instead, a cache keyed by mosaic,
  frame ids and filter, and an activity event on build.
- `backend/app/api/mosaics.py` `GET /{mosaic_id}/composite` (filter query, cache hit or build).
- `frontend/src/pages/MosaicDetailPage.tsx`: the Composite button sits in the header beside the
  actions menu and is disabled while the mosaic has no panel with a frame in the selected
  filter; the arranger's filter selector and the composite share the selected filter, so the
  lightbox shows what the tiles show.
- `backend/app/services/mosaic_composite.py` `generate_panel_thumbnail`: each tile is read
  binned from the FITS or XISF file, stretched with the same MTF as the thumbnails, and the
  composite is a JPEG at quality 85 (the web's constant), which is the format the download
  carries.

### What the port has

- 19A's `PanelThumbnailService`, `FrameScore`, `PanelFrameQuery` and the arranger layout;
  18's `MosaicRepository`.
- `Core/Imaging/{ThumbnailRenderer,MtfStretch,Resampler}.cs` (full-frame decode and stretch,
  already used for previews at `preview_resolution`), `Core/Metadata/Units.cs` (arcseconds per
  pixel), `Core/Catalogs` coordinate parsing.
- `Services/ThumbnailCache.cs` `previews/` with its size-bounded eviction, `AppWriter`
  `BeginExport` for the download.
- `Views/Preview/PreviewModalWindow.axaml` and `PreviewModalViewModel.cs`: the lightbox shape
  (zoom, pan, reset, error and retry).
- 14B's `JobRegistry` for the build.

### Spec work

Amend 12.15 (the lightbox, its states, the download), 11.3 (the composite cache under
`mosaics/composites/<key>.jpg` with its key and eviction inside the preview cache bound), a new
11.6 "Mosaic composite" (the projection, the layout rule for both modes, the stretch, the tile
size cap, the output size cap), 2.1.1 (the download as a `BeginExport` caller). The user approves
11.6 before the brief-writer runs.

### Data model

No migration and no new key. Writes: the composite JPEG under the thumbnail cache root through
`AppWriter`; the download as a new file at a save-dialog path through `BeginExport`; an
activity event per build. Nothing else (rule 1).

### Task table

| Task | Model | Files | Verify |
| --- | --- | --- | --- |
| 1. Spec-writer: 12.15, 11.3, 2.1.1 amended and 11.6 written; user approves 11.6. | opus | `docs/design-spec.md` | Zero em or en dashes; both layout modes stated; the size caps stated. |
| 2. Projection and layout: `Core/Mosaics/TangentProjection.cs` (RA, Dec to tangent-plane x, y at a centre), `CompositeLayout.cs` (positions and tile sizes from the plate scales in sky mode, from the arranger in pixel mode, the mosaic rotation, the canvas bounds, the output cap). | opus | `Core/Mosaics/{TangentProjection,CompositeLayout}.cs`, tests | Projection against values computed by hand at three declinations; pixel mode equals the arranger positions; canvas bounds; the cap scales every tile equally. |
| 3. Compositor: `Core/Mosaics/Compositor.cs` decoding each best frame through `ThumbnailRenderer`, stretching, rotating and flipping, drawing onto one Skia surface at the layout, encoding JPEG; `Services/CompositeService.cs` with the cache key (mosaic, frame ids, filter, layout hash), the job registry and the activity event. | opus | `Core/Mosaics/Compositor.cs`, `App/Services/{CompositeService,ThumbnailCache}.cs` | A four-tile composite from the fixture has the expected size and tile placement (pixel probes); cache hit on a second build; invalidated by a layout change; off the UI thread awaited; `FileSafetyTest`. |
| 4. Lightbox: `CompositeLightboxViewModel` and window with the spinner, the image with zoom and pan reused from the preview modal, the error with Retry, Download through a save dialog and `BeginExport` with the derived file name. | opus | `ViewModels/Mosaics/CompositeLightboxViewModel.cs`, `Views/Mosaics/CompositeLightboxWindow.axaml(.cs)`, `ViewModels/Mosaics/MosaicDetailViewModel.cs` | States in order; retry rebuilds; a cancelled dialog writes nothing; a chosen path writes one new file; allowlist by full path. |
| 5. Help topics for the composite and its download. | sonnet | `Core/Help/HelpTopics.cs`, the view | Census extended. |
| 6. Docs and handoff: spec reconciled, HANDOFF, TRACKING, the Status line, the Phase 20 prompt. | sonnet | docs only | Zero em or en dashes. |

Order: Task 1; Task 2 alone; Task 3 after Task 2; Task 4 after Task 3; Task 5; Task 6 last.

### Fixtures

The 19A fixture (four gradient panels on a 2 by 2 grid with RA and Dec headers and a known plate
scale) covers both layout modes. No addition.

### Verification bar

Amended by user ruling U5 of 2026-09-19, which applies to every phase from 14C on: the
verification agent does not walk the running application screen by screen and repeats nothing per
theme, so the launched-app items below are not run as written. The clean-copy check keeps the
build with 0 warnings, the full suite, the headless CLI scan and the byte-identity proof over the
fixture library, both profile folders and the user's own files; an implementer's one targeted look
at the screen it changed covers what it can, and whatever it cannot is recorded in HANDOFF section
7 as unverified on a launched application. The bar below is left as written.

1. Composite on NGC 7000 for Ha: the spinner, then a 2 by 2 image whose tiles sit where the sky
   positions place them; the job monitor and the feed record the build; a second open is
   instant (cache hit).
2. Rotate one tile in the arranger, then Composite: the tile is rotated in the image (the
   gradient shows it); the cache key changed.
3. Switch to OIII: a different composite; a filter with no frames on one panel leaves that tile
   empty and the image builds.
4. Download: cancel writes nothing; choose a folder: one JPEG named from the mosaic and filter
   appears and matches the lightbox image byte for byte.
5. Fixture hash identical before and after; new files only under the cache and at the dialog
   path; profile folders untouched per HANDOFF 5.2 item 1.

### Size

Estimate: 5 to 7 agent-days, one session. The projection and compositor are 800 lines of Python
over numpy and astropy; the port has the stretch and decode already.

### Risks and rulings needed

1. Output size: full-resolution panels composited would exceed memory on a 16 panel mosaic.
   Proposed answer: each tile is decoded at most 1,600 px on its long side and the canvas is
   capped at 6,000 px, scaling every tile equally; stated in 11.6.
2. Plate scale absent on a panel: sky mode cannot size the tile. Proposed answer: fall back to
   the arranger layout for that mosaic with a sentence in the lightbox, the web's `pixel_coords`
   behaviour.
3. The cache lives inside the preview cache bound (`preview_cache_mb`). Proposed answer: yes,
   so a large composite evicts old previews rather than growing without bound.
4. A build over sixteen full frames can take tens of seconds on a network share. Proposed
   answer: the build runs under the job registry with cancel, the lightbox's spinner names the
   panel being decoded, and closing the lightbox cancels the build; the partial result is not
   cached.
5. The JPEG quality constant and the file name pattern `<mosaic>-<filter>.jpg` are the web's.
   Proposed answer: kept verbatim so a composite from either application reads the same;
   stated in 11.6 with `sanitize_script_name`'s character rule reused from Phase 16 for the
   name.

### Next session prompt

```
Read docs/superpowers/HANDOFF.md, then docs/superpowers/TRACKING.md, then the P19A lines at the tail of docs/superpowers/progress.md, then docs/parity-roadmap.md section Phase 19B, and open Phase 19B from its task table.
```

---

## Phase 20: Custom columns

Goal: the user defines custom columns (boolean, text or dropdown) that apply to targets,
sessions or rigs, orders them, and edits their values in place on dashboard target rows, ledger
night rows and the rig rows of a multi-rig night, with the dashboard filter panel gaining a
section that filters targets by custom values. One generic cell editor serves every surface; the
column picker lists custom columns beside the built-in ones.

Amended 2026-09-21 by user ruling: mosaics are skipped for now and Phase 20 runs next instead,
ahead of Phases 18, 19A and 19B, which are on hold. This phase prepares storage only for mosaics:
`applies_to` accepts only target, session and rig in the UI and in validation, there are no
mosaic-scope cells, screens, fixtures or verification steps, and `custom_column_values` keeps a
nullable `mosaic_id` column with no foreign key, since no mosaics table exists yet. The deferred
mosaic parts (the mosaic scope in the UI and validation, the mosaic-scope cells on the mosaics
table and detail header, and the foreign key with its cascade) are picked up by the phase that
builds mosaics.

Status: DONE, commit `e515164` on `snd`, `8557` passed, 0 failed, 0 skipped, 0 warnings
(Core 2277, Cli 60, Data 1581, App 4639; 352 over the `8205` the phase opened on) tests at the
close. Run as
ten task units in four waves, widest wave six: a records-first seam step alone in the tree, then
storage, the cell editor and the help topics together, then the six surfaces together (the
Settings tab, the dashboard cells, the dashboard filter with the card chrome, the ledger cells,
the rig rows and the pickers), then the review, fix and documents wave. Plus a per-unit review of
every unit, the phase review with its file-disjoint fixers, the documents beside the fixers, and
the clean-copy verification.
Twelve fixer dispatches ran across eleven fixer units: fix-p20-a through f and the fresh
fix-p20-t5a, fix-p20-g (its agent died before reporting; finished by fix-p20-g2), then fix-p20-h, i
and j after the fix-wave re-review. The fix-wave re-review (`work/phase20/fixwave-review.md`) was
FIX-FIRST, P1 0, P2 4, P3 5; every P2 was fixed and every P3 swept or ruled, verified by the
coordinator in source rather than by a second re-review. No real-data pass ran this phase; every
look ran against a private fixture copy, never the user's own library.

**Three statements in the sections below were wrong when this plan was written and are corrected
here rather than left standing**, because the plan is read as input by the session that opens the
phase:

- **The spec sections are 5.19, 5.20 and 12.15, not 5.23, 5.24 and 12.16.** The plan numbered them
  around the mosaic sections that Phases 18, 19A and 19B would have written first. Mosaics are on
  hold, section 5 of the spec ended at 5.18 and section 12 at 12.14 when this phase folded, so the
  free numbers were taken (coordinator ruling C1). Whichever phase builds mosaics picks its own
  numbers then.
- **Task 6's "rig rows of a multi-rig night" is too narrow.** A rig label row is drawn on a
  single-rig night too while any rig-scope column exists, which is user choice 7: otherwise a rig
  column is unreachable on a one-telescope library, which is most of them.
- **Task 5's "the ledger's narrow column set drops custom columns first" is only half the rule.**
  The wide ledger has three pixels of slack, so it widens for a switched-on custom column and the
  session pane gives up the space, to a ceiling of 208 pixels; a column that does not fit under
  the ceiling is dropped the same way. The dashboard row drops custom columns first for the same
  reason, before the Equipment column steps aside (rulings C21 and C24).

### What the web does

- `frontend/src/components/CustomColumnsTab.tsx`: a create form (name, type boolean, text or
  dropdown, applies to target, session, rig or mosaic, dropdown options as chips) and a table of
  columns with order arrows, inline rename, type and scope, options editing for dropdowns, and
  delete; `backend/app/api/custom_columns.py`: list, create with a unique slug, patch, delete
  (cascading values), and `PUT /values` which validates a boolean as `true` or `false` and a
  dropdown value against its options and upserts on the column plus the target, mosaic, session
  date and rig label key.
- `frontend/src/components/InlineEditCell.tsx`: a checkbox, a text box saving on blur or Enter,
  or a select, rendered inside a table cell; used by `SessionAccordionCard.tsx` (session and
  rig scopes), `TargetRow.tsx` and `SessionTable.tsx` (target and session scopes),
  `MosaicsTab.tsx` and `MosaicDetailPage.tsx` (mosaic scope).
- `frontend/src/components/CustomColumnFilters.tsx` and `backend/app/services/target_listing.py`:
  a dashboard filter section with one control per column (a tri-state for booleans, a text
  contains for text, a select for dropdowns), applied as an EXISTS over the values table with
  target-level values, and the listing returning target values when asked.
- `ColumnPicker.tsx`: built-in and custom columns in one list, persisted per table.

### What the port has

- `ViewModels/Settings/SettingsViewModel.cs` with the eleven tabs; the tab view-model shape.
- `ViewModels/Dashboard/{FilterPanelViewModel,FilterSectionViewModel,TargetRowViewModel}.cs`,
  `Data/Queries/{TargetListingQuery,TargetListingCriteria}.cs`: the filter sections and the
  listing criteria; `Views/Dashboard/TargetListView.axaml`.
- `ViewModels/Settings/ColumnPickerViewModel.cs`, `Services/DisplayColumnWriter.cs`,
  `Core/Settings/FrameColumns.cs`: column visibility per table.
- `Views/TargetDetail/TargetDetailView.axaml`: the ledger rows (session scope) and 14A's rig
  rows; `ViewModels/AutosaveField.cs` for the debounced write shape.
- `Data/SettingsStore.cs` events for invalidation.

### Spec work

New 5.19 and 5.20 for the two tables; new 12.15 "Custom columns" (the tab, the editor, each
surface, the filter section, validation, and a sentence that the mosaic scope is reserved and
not offered until a later phase builds mosaics); amend 12.2 (the filter section and the row
cells), 12.4 (the ledger and rig cells), 12.7 (the Custom Columns tab restored from the dropped
list, the tab table and the reset lists), 12.12 (four help topics, 93 topics and 83 placements),
5.8.2 (custom slugs allowed in every `display.columns` list, and the third `ledger` id), 19.1
(custom columns removed). The user approved the drafted section before the brief-writer ran, as
22 plain-language choices rather than as spec text ("All recommended", 2026-09-21).

### Data model

One migration, the sixth (Phase 15B took the fifth; by the user's 2026-09-21 ruling this phase
runs ahead of mosaics, so it takes the ordinal mosaics would otherwise have used):
`custom_columns` (id, name, slug unique, column_type, applies_to, dropdown_options JSON,
display_order, created_at) and `custom_column_values` (id, column_id cascade, target_id nullable
cascade, mosaic_id nullable with no foreign key (no mosaics table exists yet; whichever later
phase creates mosaics adds the foreign key and its cascade), session_date nullable, rig_label
nullable, value, updated_at, a unique index over column plus the four coalesced key parts as the
web defines it, indexes on target, column and mosaic). No `created_by` or `updated_by`: there are
no users (spec 2.3). No new settings key; `display.columns` lists may carry custom slugs. Every
write is catalogue only (rule 1).

### Task table

Reconciled with what ran. The plan's eight tasks became ten units plus this documents pass: Task 1
ran before the phase opened and wrote a draft rather than the file itself, because Phase 17 was
still uncommitted and `docs/design-spec.md` belonged to its commit; a records-first seam step was
added ahead of Wave 1; Task 5 split into 5a and 5b and Task 6 into 6a, 6b and 6c, so the widest
wave could run six file-disjoint builders at once.

| Task | Model | Files | Verify |
| --- | --- | --- | --- |
| 1. Spec-writer, run before the phase opened: `work/phase20/spec-draft.md` (part 1 the new sections, part 2 the amendments) and `questions.md`, the 22 plain-language choices the user approved. Folded into `docs/design-spec.md` by Task 8 under ruling C1's numbers. | opus | `work/phase20/spec-draft.md`, `questions.md`, `spec-report.md` | Zero em or en dashes; the value key rule stated; validation rules stated; the mosaic scope stated as reserved and not offered. |
| 1a. Records-first, alone in the tree: every shared record, enum and entity before any implementer started, including `GalactiLogContext` and `MergeManifestPayload`, which four later units build against. | sonnet | `Core/Settings/CustomColumnSlug.cs`, `Data/Entities/CustomColumn*.cs`, `Data/Queries/CustomColumnModels.cs`, `GalactiLogContext.cs`, `MergeManifestPayload.cs` and seven more | Build with 0 warnings; the slug and settings filters green. It left every database-backed case red by design until Task 2 landed the migration. |
| 2. Storage: the migration and entities; `CustomColumnRepository` (list, create with slug, update, reorder, delete, set value with validation and upsert, values for a target set, a session set and a rig set, with no mosaic set in the repository's reads beyond the stored `mosaic_id` column); `Core/Settings/CustomColumnSlug.cs`. | opus | `Data/Entities/CustomColumn*.cs`, `Data/Migrations/<stamp>_CustomColumns.cs`, `GalactiLogContext.cs`, `Data/Repositories/CustomColumnRepository.cs`, `Core/Settings/CustomColumnSlug.cs` | Migration applies and rolls back; slug uniqueness; boolean and dropdown validation; upsert on each key shape; delete cascades. |
| 3. Cell editor: `Controls/CustomCellEditor.axaml` choosing checkbox, text box or combo by type, saving on change, blur or Enter through a `CustomValueViewModel` with the debounced write and an error state on refusal. | opus | `Controls/CustomCellEditor.axaml(.cs)`, `ViewModels/CustomColumns/CustomValueViewModel.cs`, `Theme/Controls.axaml` if a style is needed | Editor kind per type; write on each trigger; refusal shown; `ControlStyleScanTest`. |
| 4. Settings tab: the Custom Columns tab with the create form (the create form offers three scopes: target, session and rig), the table with order arrows, inline rename, options chips and delete with confirmation. | sonnet | `ViewModels/Settings/{CustomColumnsTabViewModel,CustomColumnRowViewModel,SettingsViewModel}.cs`, `Views/Settings/CustomColumnsTabView.axaml` | Create each type; reorder; rename; options edit; delete asks and cascades. |
| 5a. Dashboard cells: target-scope cells on the target row and session-scope cells in the night expander, with the drop rule of ruling C24. | opus | `ViewModels/Dashboard/{TargetRowViewModel,TargetListViewModel,DashboardViewModel}.cs`, `Views/Dashboard/TargetListView.axaml(.cs)` | Cells per visible custom column; no built-in column and never Expand pushed out; the header strip and the rows driven by one figure. |
| 5b. Dashboard filter and card chrome: the eighth filter section with one control per column, applied through `TargetListingCriteria` as an EXISTS, plus the `DashboardView.axaml` card chrome carried from Phase 14C. | opus | `ViewModels/Dashboard/{FilterPanelViewModel,CustomColumnFilterViewModel}.cs`, `Data/Queries/{TargetListingQuery,TargetListingCriteria}.cs`, `Views/Dashboard/{FilterPanelView,DashboardView}.axaml` | Filter truth table per type; every value a bound parameter with an explicit `ESCAPE`; the section absent with no column; the six radiused containers gone. |
| 6a. Ledger cells: session-scope cells on the Nights ledger's night rows, with the widening of ruling C21. | opus | `ViewModels/TargetDetail/TargetDetailViewModel.cs`, `Views/TargetDetail/TargetDetailView.axaml` | Each night's cell holds that night's value; no existing layout pin moves; the ledger is 640 with nothing switched on. |
| 6b. Rig rows: rig-scope cells on the session pane's rig label row, on the first table only and with a caption each (ruling C23), drawn on a single-rig night too. | sonnet | `ViewModels/TargetDetail/{SessionCardViewModel,FilterTableRowViewModel,RigLabelRowViewModel}.cs`, `Theme/Controls.axaml` | Each surface shows and writes its scope's values; one cell group per rig; the ranges table's label row carries no cell. |
| 6c. The pickers: the dashboard picker's "Custom" group and the Display tab's "Nights ledger columns" picker. | sonnet | `ViewModels/Settings/{ColumnPickerViewModel,DisplayTabViewModel}.cs`, `Views/Settings/DisplayTabView.axaml` | A custom slug persists through the picker; the empty sentence while no column exists. |
| 7. Help topics for the tab, the ledger picker and the filter section: four topics, four placements. | sonnet | `Core/Help/HelpTopics.cs`, the views | Census extended to 93 topics and 83 placements. |
| 8. Docs and handoff: spec folded and reconciled, `DESIGN.md`, HANDOFF, TRACKING, the Status line, the Phase 21 prompt. | opus | docs only | Zero em or en dashes; every behaviour verified against source rather than against a report. |

Order as run: Task 1 before the phase opened; Wave 0 records-first alone; Wave 1 Tasks 2, 3 and 7
together; Wave 2 Tasks 4, 5a, 5b, 6a, 6b and 6c together on disjoint files; Wave 3 the phase
review, the fixer partition, Task 8 beside the fixers, and verification.

### Fixtures

No new frames. The Phase 12 and 14A fixtures give targets, nights and a two-rig night to attach
values to. A seeded set of three columns (a boolean "Done", a text "Notes tag", a dropdown
"Priority" with three options) is created through the tab during verification.

### Verification bar

Amended by user ruling U5 of 2026-09-19, which applies to every phase from 14C on: the
verification agent does not walk the running application screen by screen and repeats nothing per
theme, so the launched-app items below are not run as written. The clean-copy check keeps the
build with 0 warnings, the full suite, the headless CLI scan and the byte-identity proof over the
fixture library, both profile folders and the user's own files; an implementer's one targeted look
at the screen it changed covers what it can, and whatever it cannot is recorded in HANDOFF section
7 as unverified on a launched application. The bar below is left as written.

1. Create the three columns on the tab, one per scope of target and session, plus a rig
   boolean; reorder them; rename one.
2. Dashboard: the target column appears after enabling it in the picker; set Priority on two
   targets; the filter section narrows the list by that value and clears with Reset Filters.
3. Target page: the session column on each ledger row; a value set there shows in the
   dashboard expander for the same night; the rig boolean on the two-rig night's rig rows.
4. A dropdown value typed outside its options is refused with the sentence; delete the dropdown
   column: its values are gone everywhere.
5. Profile folders untouched per HANDOFF 5.2 item 1.

### Size

Estimate: 6 to 8 agent-days, one session. Small pieces on many surfaces; the migration and the
listing filter are the load-bearing parts.

### Risks and rulings needed

1. The ledger's width: custom columns compete with the seven numeric columns. Proposed answer:
   custom columns are off by default on the ledger and the narrow column set drops them first,
   stated in 12.4.
2. Rig label identity: the web keys rig values by a label string. Proposed answer: the label is
   the canonical `telescope + camera` string 14A's rig rows already carry; a rename through the
   equipment editor rewrites stored labels in the same transaction.
3. Filter semantics for session-scope columns on the dashboard: the web filters target-level
   values only. Proposed answer: the same, stated in 12.15 (the plan wrote 12.16 here; see the
   correction above the task table).
4. Slug collisions with built-in column keys (a custom column named "Name" would slug to
   `name`). Proposed answer: custom slugs carry a `custom_` prefix in every `display.columns`
   list and in `FrameColumns`, so no custom column can shadow a built-in key; the web has no
   such rule and the port's spec states the difference.

### Next session prompt

```
Read docs/superpowers/HANDOFF.md, then docs/superpowers/TRACKING.md, then the P17 lines at the tail of docs/superpowers/progress.md, then docs/parity-roadmap.md section Phase 20, and open Phase 20 from its task table.
```

---

## Phase 21: Integrations, External Tools, NINA, Stellarium and the AstroBin CSV

Goal: Settings gains an External Tools tab holding the AstroBin filter id map and Bortle class,
a list of NINA instances and a list of Stellarium instances, each with a name, a URL and an
enabled flag; the Target detail page's overflow flyout gains "Send to NINA <name>" and "Slew
Stellarium <name>" items per enabled instance, sending the target's RA, Dec and position angle
to NINA's framing assistant and focusing Stellarium by catalogue name with a coordinate
fallback; and the Export flyout gains the AstroBin acquisition CSV for the selected nights, one
row per filter with the mapped filter id and the Bortle class (PAR-005), copied to the
clipboard.

Status: DONE, commit `5fccc76` on `snd`, `8693` passed, 0 failed, 0
skipped, 0 warnings tests at the close.

### What the web does

- `frontend/src/components/settings/AstroBinTab.tsx`: a filter id mapping (one numeric box per
  canonical or ungrouped filter name, written to `astrobin_filter_ids`), a Bortle class box
  (1 to 9, `astrobin_bortle`), and two instance lists (name, URL, enabled) for
  `nina_instances` and `stellarium_instances`, each with a help paragraph naming the URL forms
  `http://host:1888` and `http://host:8090`; the tab also carries the WBPP library fields, which
  Phase 16 placed on the export page instead.
- `backend/app/api/integrations.py`: NINA `GET {base}/v2/api/framing/set-coordinates?RAangle=
  &DecAngle=` then, after a two second wait, `set-rotation?rotation=` when a position angle is
  known, with a 5 second timeout; Stellarium tries `focus` by the catalogue prefix of the name
  (NGC, IC, M, Sh2, LDN, LBN, Abell, Ced, vdB, Cr, Mel, Barnard, PGC, UGC, Arp) then the full
  name, falls back to `core.moveToRaDecJ2000` through `POST {base}/api/scripts/direct`, then sets
  the field of view to 20 through `POST {base}/api/main/fov`; each returns ok or a fixed error
  sentence with the detail in the log; URLs are validated to http or https and, on the server,
  loopback is refused for SSRF reasons.
- `frontend/src/components/SessionAccordionCard.tsx`: one button per configured instance on the
  card; a "Astrobin CSV" button per session and per rig copying rows under the header
  `date,filter,number,duration,binning,gain,sensorCooling,fNumber,bortle,meanSqm,meanFwhm,temperature`
  with the filter's AstroBin id in place of its name, the frame count, the exposure, the modal
  gain, the mean sensor temperature, the Bortle class, mean SQM and mean FWHM; blank cells for
  unknowns; `frontend/src/pages/TargetDetailPage.tsx` copies the same rows for every checked
  session in one action; `backend/app/services/target_detail.py` supplies the per-filter rows
  (mode of gain, the id lookup by canonical then raw name).

### What the port has

- `ViewModels/Settings/SettingsViewModel.cs` tabs and `LocationTabViewModel.cs` (the small
  numeric-field tab shape); `ViewModels/Settings/ScanRootRowViewModel.cs` (the editable row list
  shape).
- `ViewModels/TargetDetail/TargetDetailViewModel.cs`: the overflow flyout and, from 14A, the
  Export flyout and `SelectedNights`; `TargetHeaderViewModel.cs` with RA, Dec and position angle
  (spec 12.4 header block).
- `Data/Queries/SessionDetailQuery.cs` per-filter rows (`FilterDetailRow`: frames, integration,
  exposure) and the frame columns `camera_gain`, `sensor_temp`, `sky_quality`, `fwhm`.
- `HttpClient` in `AppHost.cs` for the resolver with the 15 second timeout (spec 3);
  `Core/Aliases/AliasMap.cs` for canonical filter names; `Services/ShellIntegration.cs` for the
  clipboard.
- 14A's `HelpTopics`.

### Spec work

Amend 12.7 (the External Tools tab restored from the dropped list with its four groups), 12.4
(the flyout items per instance, the CSV export under the Export flyout with the header line
verbatim and the per-cell rules), 5.8.1 (the four keys), new 12.16 "Integrations" (the two
protocols, the timeout, the URL rule, the error sentences, the activity events), 19.1 (NINA and
Stellarium removed), 19.2 (the "no server" line stays: the port is a client only). The user
approves 12.16 before the brief-writer runs.

### Data model

No migration. New `general` keys: `astrobin_filter_ids` (object, filter name to integer),
`astrobin_bortle` (int 1 to 9 or null), `nina_instances` and `stellarium_instances` (lists of
`{name, url, enabled}`). Outbound HTTP to the configured hosts only; no listening socket. The
CSV goes to the clipboard; nothing is written to disk (rule 1).

### Task table

| Task | Model | Files | Verify |
| --- | --- | --- | --- |
| 1. Spec-writer: 12.7, 12.4, 5.8.1, 19.1, 19.2 amended and 12.16 written; user approves 12.16. | opus | `docs/design-spec.md` | Zero em or en dashes; the CSV header line verbatim; the URL rule stated. |
| 2. Clients: `Core/Integrations/{NinaClient,StellariumClient}.cs` over an injected `HttpClient` with the 5 second timeout, the catalogue prefix regex, the focus-then-coordinates-then-fov sequence, the fixed error sentences, the URL validation (http or https, a host present; loopback allowed on the desktop). | opus | `Core/Integrations/*.cs`, tests with a local `HttpListener` stub in the test process | Request sequence and query strings per client; timeout; error mapping; the URL table. |
| 3. CSV: `Core/Text/AstroBinCsv.cs` building rows from per-filter figures (id lookup canonical then raw, modal gain, means, blanks) for one night, one rig or several nights; `SessionDetailQuery` gains the per-filter gain, sensor temperature, SQM and FWHM aggregates. | opus | `Core/Text/AstroBinCsv.cs`, `Data/Queries/{SessionDetailQuery,SessionDetailModels}.cs`, tests | Golden CSV for the fixture nights; blank rules; multi-night concatenation with one header. |
| 4. External Tools tab: the filter id map over the canonical and ungrouped names, the Bortle box, the two instance lists with add, remove, enable, all through `MutateGeneral`. | sonnet | `ViewModels/Settings/{ExternalToolsTabViewModel,InstanceRowViewModel,SettingsViewModel}.cs`, `Views/Settings/ExternalToolsTabView.axaml` | Key shapes per edit; Bortle bounds; a blank id removes the entry. |
| 5. Target page: flyout items per enabled instance calling the clients with the header's coordinates and position angle, a result line in the status bar and an activity event; "AstroBin CSV (n nights)" under the Export flyout copying the rows for `SelectedNights`, disabled at zero. | sonnet | `ViewModels/TargetDetail/TargetDetailViewModel.cs`, `Views/TargetDetail/TargetDetailView.axaml`, `ViewModels/StatusBarViewModel.cs` | Items equal enabled instances; the call carries the figures; failure shows the sentence; the CSV equals the golden file. |
| 6. Help topics for the tab's four groups and the two flyout entries. | sonnet | `Core/Help/HelpTopics.cs`, the views | Census extended. |
| 7. Docs and handoff: spec reconciled, HANDOFF, TRACKING, the Status line, the Phase 22 prompt. | sonnet | docs only | Zero em or en dashes. |

Order: Task 1; Tasks 2 and 3 together; Task 4 after Task 1; Task 5 after Tasks 2, 3 and 4;
Task 6; Task 7 last.

### Fixtures

The Phase 12 fixture plus 14A's two-rig night cover the CSV; the frames gain `GAIN`, `CCD-TEMP`
and `SQM` header keywords in a regenerated copy under `docs/superpowers/work/phase21/fixtures/`,
which the Phase 12 recipe produces. For the clients, the test project hosts an `HttpListener`
stub; the launched-app bar uses a stub process the verification agent starts on a free port and
stops afterwards (the application itself listens on nothing).

### Verification bar

Amended by user ruling U5 of 2026-09-19, which applies to every phase from 14C on: the
verification agent does not walk the running application screen by screen and repeats nothing per
theme, so the launched-app items below are not run as written. The clean-copy check keeps the
build with 0 warnings, the full suite, the headless CLI scan and the byte-identity proof over the
fixture library, both profile folders and the user's own files; an implementer's one targeted look
at the screen it changed covers what it can, and whatever it cannot is recorded in HANDOFF section
7 as unverified on a launched application. The bar below is left as written.

1. External Tools: map Ha to 4657 and OIII to 4663, set Bortle 4, add a NINA instance at the
   stub's URL enabled and a Stellarium instance disabled.
2. Target page flyout: one NINA item, no Stellarium item; choosing it sends the two requests the
   stub logs (coordinates, then rotation after the wait); the status bar reports ok; disabling
   the instance removes the item.
3. Point the NINA URL at a closed port: the item reports the fixed sentence within the timeout
   and the log carries the detail.
4. Check two nights and copy the AstroBin CSV: the clipboard holds one header and one row per
   filter per night with the ids and Bortle 4; an unmapped filter leaves its cell blank.
5. Profile folders untouched per HANDOFF 5.2 item 1; the fixture hash identical.

### Size

Estimate: 4 to 5 agent-days, one session. The smallest phase; two clients, one CSV, one tab.

### Risks and rulings needed

1. Loopback: the web refuses loopback for SSRF reasons; on the desktop NINA usually runs on the
   same machine. Proposed answer: the port allows loopback and private ranges and refuses only
   non-http schemes and an empty host; 12.16 states why the rule differs from the web.
2. The web's per-rig CSV button on multi-rig nights. Proposed answer: the port's CSV takes a rig
   filter from 14A's rig pill state: with a rig selected, rows for that rig only.
3. Position angle source: the header block's position angle comes from the catalogue, not the
   frames. Proposed answer: send it only when the reference frame carries a rotator or `POSANGLE`
   value; otherwise omit rotation, as the web does when it is null.
4. The collision map must name every exact-count and exact-set census a phase's new surface can
   reach, not only the views it edits: this phase's own map missed `JobRegistryCensusTest`,
   `ExportFlyoutViewTests.cs` and `SettingsViewTests.cs`'s tab id list, found only at the phase
   review and the coordinator's own full run rather than by a task filter.

### Next session prompt

```
Read docs/superpowers/HANDOFF.md, then docs/superpowers/TRACKING.md, then the P20 lines at the tail of docs/superpowers/progress.md, then docs/parity-roadmap.md section Phase 21, and open Phase 21 from its task table. Mosaics (Phases 18, 19A and 19B) stay on hold by the user's ruling of 2026-09-21 and are not opened by this session.
```

---

## Phase 22: Sky viewer and the DSS reference thumbnail

Goal: the Target detail page's Details drawer gains a Sky view section: an Aladin Lite panel
centred on the target inside a WebView with a survey selector (DSS2 Color, DSS2 Red, 2MASS
Color, PanSTARRS DR1, AllWISE Color), reticle, zoom and fullscreen, and a loading overlay; and
a DSS2 Red survey cutout fetched from SkyView, cached under app data and shown as the reference
image beside it, with the target's own-frame thumbnail kept as the default and the cutout
offered as a labelled alternative. Both need the network and sit behind one Settings switch,
off by default. This phase reverses two written rules (spec 2.2 "No webview" and 19.2 "no
survey downloads"), so its first ruling is the package choice and its spec task rewrites those
rules before anything else.

Status: DONE, commit `c0aa036` on `snd`, `8904` passed, 0 failed, 0 skipped,
0 warnings tests at the close. The user ruled at the gate (U3) for the roadmap's option (d) in the
user's own shape: a Sky view button beside Reveal folder on the Target detail header, hidden
without a target id, RA and Dec and disabled with a tooltip while the switch is off, opens a
preview-modal window that draws Skia over still JPEGs from the CDS hips2fits service, with a
five-survey combo, drag to pan, wheel or plus and minus to zoom, Reset and Refresh, images cached
on disk under `survey/<target id>/`. There is no WebView and no Aladin; one host, hips2fits at
`P/DSS2/red` and the target's default field, serves the DSS cutout too, so no NASA SkyView client
or second cache was built, and the target header carries no reference thumbnail or Use as
reference toggle since polish wave 9 removed the header thumbnail before this phase opened.

### What the web does

- `frontend/src/components/AladinViewer.tsx`: loads `aladin.min.css` and `aladin.js` (v3) from
  `aladin.cds.unistra.fr` at runtime, creates the panel with `survey`, `fov` (default 0.5
  degrees), `target` as RA and Dec, `showReticle`, `showZoomControl`, `showFullscreenControl`;
  a survey dropdown calling `setImageSurvey`; a "Loading sky viewer..." overlay until the
  library initialises; a 15 second load timeout with an error logged.
- `frontend/src/pages/TargetDetailPage.tsx`: a collapsible Sky View section with a help popover
  shown when the target has coordinates; a labelled DSS reference image beside the panel when
  one has been cached for the target.
- `backend/app/services/skyview.py`: `GET https://skyview.gsfc.nasa.gov/current/cgi/runquery.pl`
  with `Survey=DSS2 Red`, `Position=<ra>,<dec>`, `Size=<fov degrees>` where the field is
  `size_major * 1.5 / 60` clamped to 0.1 to 5.0 and 0.5 when unknown, `Pixels=512`,
  `Scaling=Log`, `Return=JPEG`; saved as `<target id>.jpg`; fetched by a background task.

### What the port has

- `Views/TargetDetail/TargetDetailView.axaml`: the Details `SplitView` drawer with the header
  block, notes and merge history; `TargetHeaderViewModel.cs` with RA, Dec, angular size and
  `ReferenceThumbnail` from the target's own frames (spec 11.4).
- `Services/ThumbnailCache.cs` `reference/`, `ThumbnailWorker.cs`, `AppWriter`; `HttpClient`
  in `AppHost.cs`; 14B's `JobRegistry`.
- `Core/Settings/GeneralSettings.cs`; `Views/Settings/GeneralTabView.axaml` for the switch.
- No WebView package; `docs/design-spec.md` 2.2, 3 ("XAML UI with no webview") and 19.2 forbid
  one; `docs/roadmap.md` Global Constraints repeat it; Velopack packaging in
  `docs/packaging.md` and `release.yml`.

### Spec work

Rewrite 2.2 from "No webview" to the bounded rule: one WebView, on one drawer section, showing
one external page, behind a switch that ships off, with no other HTML surface; amend 3 (the
package row and the "no webview" reason), 19.2 (both lines rewritten to the bounded rule),
19.1 (sky viewer removed), 11.4 (the survey cutout as an alternative reference image, never the
default), 11.3 (`survey/<target id>.jpg` in the cache), 12.4 (the Sky view section of the
drawer), 5.8.1 (keys), 17.1 (the runtime the installer must ensure, if the chosen package needs
one), 12.7 (the switch on the General tab). The user approves 2.2, 19.2 and 12.4's section
before the brief-writer runs.

### Data model

No migration. New `general` keys: `survey_downloads_enabled` (bool, default false, gating both
the panel and the cutout), `sky_view_survey` (string, default `P/DSS2/color`). The cutout is
written under the thumbnail cache root as `survey/<target id>.jpg` through `AppWriter`; the
WebView's own profile data (cookies, cache) is directed into a folder under app data by the
package's user data folder option. Nothing else is written (rule 1).

### Task table

| Task | Model | Files | Verify |
| --- | --- | --- | --- |
| 1. **SUPERSEDED (U3).** Ruling and spec-writer: the package choice recorded (the options below), then 2.2, 3, 19.1, 19.2, 11.3, 11.4, 12.4, 5.8.1, 12.7 and 17.1 amended; user approves 2.2, 19.2 and the 12.4 section. | opus | `docs/design-spec.md`, `docs/roadmap.md` Global Constraints | The gate ruled option (d) instead of a package; spec-p22 amended 2.1.1, 5.8.1, 11.3, 11.4, 12.4, 12.7, 18.1, 19.1, 19.2, 20 to the shipped shape and 2.2 gained one sentence; 3 and 17.1 are untouched, since no package and no runtime exist. |
| 2. **SUPERSEDED (U3), the switch survives.** Survey cutout: `Core/Imaging/SurveyCutoutClient.cs` (the SkyView query with the field rule, JPEG bytes, a 15 second timeout), `Services/SurveyCutoutService.cs` fetching once per target on demand under the job registry, cached, refreshable; the switch on the General tab. | opus | `Core/Imaging/SurveyCutoutClient.cs`, `App/Services/{SurveyCutoutService,ThumbnailCache}.cs`, `ViewModels/Settings/GeneralTabViewModel.cs`, `Views/Settings/GeneralTabView.axaml`, `Core/Settings/GeneralSettings.cs` | Built as unit A's `Core/Survey/Hips2FitsClient.cs` and `SurveyImageService` against hips2fits, not SkyView; the switch itself shipped as unit B's own General tab section, its topic `settings.general.survey-downloads`. |
| 3. **SUPERSEDED (U3).** Package and host page: the chosen package pinned in `Directory.Packages.props`; `Assets/aladin.html` shipped as an Avalonia resource that loads Aladin Lite v3 from the CDS URL and exposes `setSurvey` and `goTo` through the package's message bridge; the user data folder under app data. | opus | `Directory.Packages.props`, `GalactiLog.App.csproj`, `Assets/aladin.html`, `App/Services/SkyViewHost.cs`, `AppHost.cs` | No package, no host page and no bridge exist; `Directory.Packages.props` is untouched by this phase. |
| 4. **SUPERSEDED (U3).** Sky view section: `SkyViewViewModel` (coordinates, field from the angular size, the survey selector, loading and error states, the runtime-absent sentence, the switch-off sentence) and the drawer section with the WebView, the selector, the overlay and the cutout beside it with its "DSS2 Red" label and a Use as reference toggle that swaps the header thumbnail for this session only. | opus | `ViewModels/TargetDetail/{SkyViewViewModel,TargetHeaderViewModel}.cs`, `Views/TargetDetail/TargetDetailView.axaml` | Built as unit C's `SurveyViewViewModel` and `SurveyViewWindow` on the preview modal's shell, and unit D's header button, in place of the drawer section; there is no Use as reference toggle, since the header carries no reference thumbnail after polish wave 9. |
| 5. **SUPERSEDED (U3).** Packaging: the installer step or bootstrap the chosen runtime needs, `docs/packaging.md` and `docs/release-checklist.md` amended, the Diagnostics Versions group gains the WebView runtime version or "absent". | sonnet | `docs/packaging.md`, `docs/release-checklist.md`, `.github/workflows/release.yml` if the package needs a step, `ViewModels/Diagnostics/DiagnosticsViewModel.cs` | No runtime to bootstrap or report; none of these files changed for this phase. |
| 6. **DONE.** Help topics for the section, the selector, the cutout and the switch. | sonnet | `Core/Help/HelpTopics.cs`, the view | Folded into units B and D: `page.sky-view` and `settings.general.survey-downloads` land the census at 99 topics, 89 `HelpButton` elements, 32 markup files. |
| 7. **DONE.** Docs and handoff: spec reconciled, HANDOFF sections 1, 2, 4 (rule 5 gains the WebView sentence), TRACKING, the Status line, the "Sequence and totals" table closed. | sonnet | docs only | This documents pass; HANDOFF's rule 5 gains no WebView sentence, since none was built. |

Order: Task 1 with the ruling first; Tasks 2 and 3 together; Task 4 after Task 3; Task 5 after
Task 3; Task 6; Task 7 last.

### Fixtures

The Phase 12 fixture's "M 31" resolves offline with coordinates and an angular size, which is
all the section needs. No new frames. The network is a fixture here: the verification agent
records whether the machine reaches `aladin.cds.unistra.fr` and `skyview.gsfc.nasa.gov` before
the bar and reports each item as NOT OBSERVABLE if not, never as a failure.

### Verification bar

Amended by user ruling U5 of 2026-09-19, which applies to every phase from 14C on: the
verification agent does not walk the running application screen by screen and repeats nothing per
theme, so the launched-app items below are not run as written. The clean-copy check keeps the
build with 0 warnings, the full suite, the headless CLI scan and the byte-identity proof over the
fixture library, both profile folders and the user's own files; an implementer's one targeted look
at the screen it changed covers what it can, and whatever it cannot is recorded in HANDOFF section
7 as unverified on a launched application. The bar below is left as written.

1. With the switch off on a fresh profile: the Sky view section shows the switch-off sentence
   and no request leaves the machine (the log shows none).
2. Switch on: the panel loads Aladin centred on M 31 at the computed field, the reticle and
   zoom controls present; the selector switches to DSS2 Red and the panel repaints; fullscreen
   opens and Escape returns.
3. The cutout appears beside the panel labelled DSS2 Red after its fetch; the job monitor and
   the feed record it; a relaunch serves it from the cache with no request.
4. Use as reference swaps the header thumbnail; a relaunch shows the own-frame thumbnail again.
5. On a machine or profile without the runtime (simulated by the probe seam), the section shows
   the runtime-absent sentence and Diagnostics reads "absent".
6. Every other page unchanged; `FileSafetyTest` green; profile folders untouched per HANDOFF
   5.2 item 1; the WebView's data folder is under app data and nowhere else.

### Size

Estimate: 5 to 7 agent-days, one session, plus the packaging test on an installed build the
user runs (HANDOFF 5.3 style).

### Risks and rulings needed

1. The package (the phase's first ruling). Options for Avalonia 11.3.21 on Windows x64:
   (a) `Avalonia.Controls.WebView` from Avalonia UI (`NativeWebView`), a commercial Avalonia
   Accelerate component with a licence key, over the Microsoft Edge WebView2 runtime
   (preinstalled on Windows 11, an installable evergreen runtime on Windows 10), AOT and trimming
   compatible, with a user data folder option; (b) `WebView.Avalonia` plus
   `WebView.Avalonia.Desktop` (MicroSugar, open source), also over the WebView2 runtime, wired
   with `UseDesktopWebView()` in `Program.Main` and `AvaloniaWebViewBuilder.Initialize`, whose
   readme states it tracks Avalonia preview releases, so the 11.3.21 pin must be checked at
   Task 3; (c) `WebViewControl-Avalonia` (OutSystems, over CefGlue), which bundles Chromium in
   the package, needs no runtime on the machine and adds on the order of 150 MB to the Velopack
   package (estimate) with a Chromium update cadence the release checklist would inherit;
   (d) no WebView: a Skia-drawn survey viewer fetching cutouts from the CDS hips2fits service per
   survey, centre and field, with pan and zoom refetching, which keeps spec 2.2 as written and
   gives survey selection, reticle and zoom without fullscreen or Aladin's own controls.
   Proposed answer: (b), because it is open source, keeps the package small and uses the runtime
   Windows 11 already has; (d) is the fallback if (b) fails to pin at 11.3.21 or the installed
   build's runtime probe fails on the user's machine; (a) if the user prefers a supported vendor
   component and accepts the licence; (c) is not proposed.

   **Ruled: option (d)**, at the user's gate (U3, 2026-09-29). The coordinator had ruled (a),
   `Avalonia.Controls.WebView` 11.4.1, and a spike proved it end to end (pin, page load, bridge
   round trip, user data folder) on every step but the headless one. The user reversed it anyway:
   the concern was bloat and a second runtime inside the application, not a failed spike, so the
   fallback shape was taken and built in the user's own words, a Sky view button opening a
   preview-style window over hips2fits.
2. Network policy: the port has made no request except catalogue resolution. Proposed answer:
   one switch, off by default, gating both the panel and the cutout, with the help topic naming
   both hosts; the resolver's existing behaviour is unchanged.
3. The runtime on Windows 10: WebView2 may be absent. Proposed answer: a probe at section open;
   the sentence names the runtime and links to nothing (no download inside the application);
   `docs/packaging.md` records whether the installer bootstraps it, decided at Task 5.
4. Spec 19.2 "downloading survey images for reference thumbnails". Proposed answer: the cutout is
   an alternative image the user chooses per session, never the stored reference; 11.4 keeps the
   own-frame rule as the default so the existing behaviour and tests stand.

### Next session prompt

```
Read docs/superpowers/HANDOFF.md, then docs/superpowers/TRACKING.md, then the P21 lines at the tail of docs/superpowers/progress.md, then docs/parity-roadmap.md section Phase 22, give the package ruling from its first risk, and open Phase 22 from its task table.
```

## Phase 23: Target page layouts

Six stages, 0 to 5, one local commit per stage; spec, order and seam are in `docs/superpowers/work/target-layouts`, resume at its HANDOFF.md.
Stage 0 is done (1fe554f, 8921): the target page is fourteen parts, a page shell and a layout registry, no visible change.
Stage 1 is done (da9f936, 8828): window minimum 1280 x 720, layout C with Night review, Compare nights and Integration, the current page retired.
Stage 2 is done (b219bbf, 8969): the per-night per-filter query, the Compare lanes and table, Integration segments, goals and tables, and the shared night axis; layout C is complete with sixteen parts.
Stage 3 is done (d29a21f, 9077): layout A (Night Bench), the layout selector in the header and in Settings, Display, the stored keys `layout` and `layouts.<key>.lanes_height`, the lanes handle in both layouts and the Night detail drawer.
The stage 3 follow-up is done (c7ccd49, 9098): the launched look ran twice, six findings were fixed, and the real-font frame rows are 2 to 3 at 1280 x 720 and 9 to 10 at 1600 x 900 in Night Bench.
Stage 4 is done (b753b74, 9159): layout D (Cascading Rows), the third registry row, its rows scrolling as one column under the fixed header on a short window with six whole frame rows kept, no coordinates block, one scroller per wheel notch, and the trend and night chart fixes; pushed with b28354c on 2026-10-04.
Stage 5 was built as layout B (Aligned Tracks) with the brush (work in progress 835eaa8); its close is superseded by stage 6.
Stage 6 is done (b721299, 9083): one layout by the user's ruling U37, Question Modes; the other three layouts, the selector, the Night detail drawer, the brush and the session chart's lanes are removed; local until the user says to push.
Refining Question Modes is next; it stops at its approval step.

---

## Sequence and totals

| Phase | Sessions | Agent-days (estimate) | Spec sections touched | Migrations added |
| --- | --- | --- | --- | --- |
| 14A Target detail audit gaps | 1 | 9 to 11 | 5.8.1, 5.8.2, 11.5, 12.4, 12.7, 13, new 12.12 | none |
| 14B Dashboard, settings, shell audit gaps | 1 | 8 to 10 | 5.8.1, 9.7, 10.9, 12 shell, 12.2, 12.6, 12.7, 12.8 | none |
| 14C Shell and dashboard idioms from the web | 1 | 4 to 5 | 5.8.1, 5.8.2, 12 shell, 12.2, 12.5, 14 | none |
| 15A PHD2 data path | 1 | 10 to 12 | 5.8.1, new 5.15 to 5.18, new 7.6, 10.3, 10.4, 10.9, 12.7, 19.1 | 4th: phd2_logs, phd2_sessions, phd2_frames, phd2_calibrations |
| 15B PHD2 on the pane and Statistics | 1 | 8 to 10 | 5.8.2, 12.4, 12.5, 13, 14.1 | 5th: phd2_sessions.ended_at_local (carried items 54 and 55, taken this phase rather than deferred, per the standing fresh-database directive) |
| 16 WBPP export | 1 (run as 2 after a quota kill) | 7 to 9 | 2.1.1, 5.8.1, 12.4, 12.7, 12.12, new 12.13, 13, 19.1 | none (confirmed by reading `src/GalactiLog.Data/Migrations/`: still five, unmodified) |
| 17 Analysis page | 1 | 8 to 10 | 5.8.2, 12, 13, new 12.14, 19.1 | none |
| 18 Mosaics A (ON HOLD) | 1 | 11 to 13 | 5.8.1, 5.8.2, four new 5.x sections, new 7.7, 10.3, 12.2, 12.4, one new 12.x section, 19.1 | the next free ordinal at the time: mosaics, mosaic_panels, mosaic_panel_sessions, mosaic_suggestions, images.panel_label, images.panel_id |
| 19A Mosaics B, arranger (ON HOLD) | 1 | 9 to 11 | 11.3, 11.4, Phase 18's mosaics page section, 13, 14.1 | none |
| 19B Mosaics B, composite (ON HOLD) | 1 | 5 to 7 | 2.1.1, 11.3, new 11.6, Phase 18's mosaics page section | none |
| 20 Custom columns | 1 | 6 to 8 | 5.8.2, new 5.19, 5.20, 12.2, 12.4, 12.7, 12.12, new 12.15, 19.1 | 6th: custom_columns, custom_column_values |
| 21 Integrations and AstroBin CSV | 1 | 4 to 5 | 5.8.1, 12.4, 12.7, one new 12.x section, 19.1, 19.2 | none |
| 22 Sky viewer and DSS cutout, DONE, shipped as still images over hips2fits rather than the WebView first proposed (U3) | 1 | 5 to 7 | 2.1.1, 2.2, 5.8.1, 11.3, 11.4, 12.4, 12.7, 12.12, 18.1, 19.1, 19.2, 20 | none |
| Total | 13 sessions | 94 to 118 | | 4 migrations (4th, 5th, 6th, and one more when mosaics open) |

**The three mosaic rows no longer name spec section numbers, and that is deliberate.** They named
5.19 to 5.22 and 12.15, which Phase 20 took while mosaics were on hold, because a section number
is only free until something claims it. Whichever session opens mosaics picks the free numbers at
that moment and writes them into its own plan then. Phases 21 and 22 lose their numbers for the
same reason.

Rulings the user gives before Phase 14A starts are listed in
`docs/superpowers/work/phase13/parity-roadmap-report.md` section 4; every other ruling is given
at its phase's spec-writer task.
