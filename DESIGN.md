# GalactiLog design

A target page is an observing log, not a dashboard. One aligned ledger of nights with the target's
own averages as its first row, one night open beside it. Colour is spent only on data, the filter
inks, the metric inks and the one "worse" ink; light is spent only on state, so a selected row is
lit rather than coloured. This file records the world as it is built, in the values the running
application reads. Every figure here is grep-checkable against the file it names.

## 1. Where it came from

The greys are a stretched FITS frame: neutral luminance with no blue cast, which is what a linear
frame looks like once it is stretched, and what the page has to sit beside without arguing with it.
The type and the alignment are a printed star atlas: one family, figures in columns that line up,
no rules between rows, hierarchy carried by weight rather than by boxes. The filter inks are the
Hubble palette, so a narrowband reader already knows which tick is which, and the metric inks are
the object symbols of a printed atlas, separated by hue rather than by saturation. The night strip
is the sequencer's own timeline: one tick per exposure along the night, which is the graphic an
imager already reads at the telescope.

The approved comp is `docs/superpowers/work/phase12/comp-observing-ledger.html`; its direction
contract is the HTML comment at the top of that file. The structural findings it keeps come from
three panel reports: `docs/superpowers/work/phase12/panel-ia.md`,
`docs/superpowers/work/phase12/panel-density.md` and
`docs/superpowers/work/phase12/panel-workflow.md`.

## 2. Tokens

Forty-one keys, every one a `SolidColorBrush`, in `src/GalactiLog.App/Theme/Themes/`. The group
counts are 5 surface, 2 border, 3 text, 3 accent, 4 semantic, 10 metric, 2 indicator, 2 badge,
7 filter and 3 scrollbar (Phase 14C adds the scrollbar group). Thirty-seven of them mirror the web
`ThemeTokens` interface one for one; `ColorAccentPressed` is Avalonia-only because the web has no
pressed state to paint, and the three scrollbar tokens are Avalonia-only because the web reaches
the same colours through a raw CSS variable rather than through its typed token interface.
`glass-void` carries the same keys and is documented in `docs/design-spec.md` section 14.1.

| Key | `observing-ledger` | `red-light` |
| --- | --- | --- |
| `ColorBgBase` | `#00000000` | `#00000000` |
| `ColorBgSurface` | `#FF171717` | `#FF170404` |
| `ColorBgElevated` | `#FF262626` | `#FF260808` |
| `ColorBgHover` | `#FF2F2F2F` | `#FF300A0A` |
| `ColorBgInput` | `#FF0C0C0C` | `#FF0E0202` |
| `ColorBorderDefault` | `#14FFFFFF` | `#1FFF5A3C` |
| `ColorBorderEmphasis` | `#26FFFFFF` | `#38FF5A3C` |
| `ColorTextPrimary` | `#FFE8E5DE` | `#FFE0442E` |
| `ColorTextSecondary` | `#FFA8A49C` | `#FFB73C2E` |
| `ColorTextTertiary` | `#FF8A867F` | `#FF903126` |
| `ColorAccent` | `#FFEFE9DC` | `#FFFF5A3C` |
| `ColorAccentHover` | `#FFFFFFFF` | `#FFFF7A60` |
| `ColorAccentPressed` | `#FFD8D2C6` | `#FFD8442A` |
| `ColorSuccess` | `#FF8FB58A` | `#FFB9422C` |
| `ColorWarning` | `#FFD8A657` | `#FFFF6A4A` |
| `ColorError` | `#FFD07A68` | `#FFFF8A70` |
| `ColorInfo` | `#FF8FB0C0` | `#FFC24A36` |
| `ColorMetricIntegration` | `#FFA8A49C` | `#FFB63A28` |
| `ColorMetricFrames` | `#FF8FB58A` | `#FFB9422C` |
| `ColorMetricHfr` | `#FFD9B34A` | `#FFE9573B` |
| `ColorMetricEccentricity` | `#FFC8584A` | `#FFC7402A` |
| `ColorMetricFwhm` | `#FF5F9E6E` | `#FF9E3322` |
| `ColorMetricStars` | `#FF4F9FA5` | `#FFD24C33` |
| `ColorMetricGuiding` | `#FF5B83B8` | `#FFB63A28` |
| `ColorMetricTemp` | `#FF5F9E6E` | `#FF9E3322` |
| `ColorMetricGain` | `#FF8FB58A` | `#FFB9422C` |
| `ColorMetricTime` | `#FFD07A68` | `#FFFF8A70` |
| `ColorMetricBest` | `#FF8FB58A` | `#FFB9422C` |
| `ColorMetricWorst` | `#FFD07A68` | `#FFFF8A70` |
| `ColorBadgeBg` | `#00000000` | `#00000000` |
| `ColorBadgeText` | `#FF8A867F` | `#FF903126` |
| `ColorFilterHa` | `#FF6BAB6B` | `#FFA83A28` |
| `ColorFilterOiii` | `#FF5B8FC4` | `#FFB63A28` |
| `ColorFilterSii` | `#FFC25A3A` | `#FFC7402A` |
| `ColorFilterL` | `#FFBDBAB3` | `#FFD8503A` |
| `ColorFilterR` | `#FFC8584A` | `#FFC7402A` |
| `ColorFilterG` | `#FF5F9E6E` | `#FF9E3322` |
| `ColorFilterB` | `#FF5B83B8` | `#FFB63A28` |
| `ColorScrollbarThumb` | `#26FFFFFF` | `#38FF5A3C` |
| `ColorScrollbarThumbHover` | `#FF8A867F` | `#FF903126` |
| `ColorScrollbarTrack` | `#00000000` | `#00000000` |

`ColorBadgeBg` is transparent in both: a badge is outlined text, not a filled pill. `ColorAccent`
is a near-white ink in `observing-ledger` rather than a hue, because it marks state and nothing
else. The accent is a three-rung ramp: `ColorAccentHover` above the rest value and
`ColorAccentPressed` below it, so a press is visible without a pointer hover, which is what a
keyboard activation does.

### Additional resources, not among the 41

Each dictionary also declares the eight callout composites, the two gradient stops and the six
platform scrollbar aliases section 6 names. None of the three sets is counted as a token; all
three are part of what a fourth dictionary has to reproduce.

| Key | Composition |
| --- | --- |
| `ColorSuccessCalloutFill` | `ColorSuccessValue` at `Opacity` 0.2 |
| `ColorSuccessCalloutBorder` | `ColorSuccessValue` at `Opacity` 0.5 |
| `ColorWarningCalloutFill` | `ColorWarningValue` at `Opacity` 0.2 |
| `ColorWarningCalloutBorder` | `ColorWarningValue` at `Opacity` 0.5 |
| `ColorErrorCalloutFill` | `ColorErrorValue` at `Opacity` 0.2 |
| `ColorErrorCalloutBorder` | `ColorErrorValue` at `Opacity` 0.5 |
| `ColorInfoCalloutFill` | `ColorInfoValue` at `Opacity` 0.2 |
| `ColorInfoCalloutBorder` | `ColorInfoValue` at `Opacity` 0.5 |

| Key | `observing-ledger` | `red-light` |
| --- | --- | --- |
| `ColorGradientFromValue` | `#FF0F0F0F` | `#FF120303` |
| `ColorGradientToValue` | `#FF171717` | `#FF170404` |

`BrushPageBackground` is a `LinearGradientBrush` from `0%,0%` to `100%,100%` between those two
stops. Every `Window` root paints it. The two stops are one step apart, so the page reads as one
flat field; the gradient mechanism is kept because a later theme may want one.

The four semantic colours are declared as `Color` values first (`ColorSuccessValue`,
`ColorWarningValue`, `ColorErrorValue`, `ColorInfoValue`) so the composites derive from them
instead of repeating a literal.

## 3. Type

Two families, both SIL Open Font License 1.1, embedded as Avalonia resources under
`src/GalactiLog.App/Assets/Fonts` with `OFL.txt` beside them. The resource keys are declared in
`App.axaml`, before `Application.Styles`, because `Theme/Controls.axaml` resolves them with
`StaticResource` at style-load time.

| Resource key | Family | Files |
| --- | --- | --- |
| `FontSans` | Atkinson Hyperlegible Next, fallbacks Segoe UI, sans-serif | `AtkinsonHyperlegibleNext-Regular.ttf` (400), `-Medium.ttf` (500), `-SemiBold.ttf` (600), `-Bold.ttf` (700) |
| `FontMono` | Atkinson Hyperlegible Mono, fallbacks Cascadia Code, Consolas, monospace | `AtkinsonHyperlegibleMono-Regular.ttf` (400), `-Medium.ttf` (500) |

Static weight instances, not the variable files: Avalonia does not support variable fonts. The
family was chosen for the subject, because catalogue designations and figures are read at low
brightness at night and it is drawn to keep 0 and O, and 1, l and I, apart.

The weight to instance mapping above is asserted by file census only. `FontResourceTest` proves the
six files are present, are TrueType, carry no `fvar` axis table and resolve as Avalonia resources;
no headless test can prove that `FontWeight` Medium, SemiBold and Bold select the three Next
instances rather than one synthetic bold, because the headless platform does not shape text. The
verification agent settled it on the launched application on 2026-09-17: the interface renders in
Atkinson Hyperlegible Next and not Segoe UI, identified at 3x by the open-tailed y, the flat-topped
3 and the dotted 0, with the page title, the other names, the tag and the log line's figures each
at a visibly different weight and no smeared synthetic bold. The frame table's file names, the
drawer's RA and Dec and the raw header panel render in Atkinson Hyperlegible Mono rather than
Cascadia Code. The scale-bar label was the one mono site not observed, because the fixture's 64 by
64 frames give a 1.03 arcminute field and no bar is drawn under 3 arcminutes.

Six tiers. Weight and ink carry the hierarchy; the family never changes for emphasis.

| Class | Ratio to root | Weight | Ink | Role |
| --- | --- | --- | --- | --- |
| `t-hero` | 1.5 | 700 | primary | The page title |
| `t-stat` | 1.2 | 700 | primary | A night header |
| `figure` | 1.0 | 600 | primary | A figure in prose, tabular |
| (none) | 1.0 | 400 | primary, secondary for text columns | Body, and a summary table's cells |
| `t-label` | 0.786 | 500 | secondary | Table headers, facts lines, buttons, frame cells |
| `t-caption` | 0.714 | 400 | tertiary | Stat labels, tags, disclosures, deltas |

The ratios live on the class, through `MultiplyConverter` over `$parent[Window].FontSize`, so no
call site on the Target detail page repeats a `FontSize` attribute. One site elsewhere still
carries the converter form inline, `Views/Settings/DisplayTabView.axaml`'s text-size preview, which
is a size the user is choosing rather than a tier. Root size is the user's setting: Small 14, Medium 16,
Large 18 (default), Extra large 20.

Letter-spacing is not used on this page. A tracked uppercase label beside a figure competes with
the figure, so the page's section labels (Nights, Frames) are sentence case at weight 600 in the
primary ink.

## 4. Control vocabulary

One file, `src/GalactiLog.App/Theme/Controls.axaml`. A view that declares its own `Button` or badge
style is what `ControlStyleScanTest` fails on, with no exemption list, so a new view is styled by
default rather than by remembering a convention.

**One declaration in that file is the application's default text ink**, and it is the reason a
bare `TextBlock` is not white. Until Phase 17 nothing set one: a block with neither an inked class
nor its own `Foreground` inherited Fluent's Dark default, pure white, a colour no theme dictionary
here declares, and that reached 99 blocks in 27 files including every `num` figure. The `Window`
style now carries `Foreground` as a `DynamicResource` of `ColorTextPrimary`. It is a `Window`
style and not a `TextBlock` selector on purpose: inheritance is the lowest priority there is, so
every control that inks its own content keeps its ink, while a type selector on `TextBlock` would
outrank them and paint the one filled button's label in the page ink over its accent fill. A type
selector matches the control's style key, which `Window` fixes for its subclasses, so the one
style reaches all eight shipped windows and no popup top level. The platform's own combo box and
date picker inks do not inherit and are aliased per theme instead (section 9).

| Selector | What it is |
| --- | --- |
| `Window` | `FontFamily` set to `FontSans`, and `Foreground` to `ColorTextPrimary`, the application's one inherited default text ink |
| `Button` | Transparent fill, 1 px `ColorBorderEmphasis` outline, radius 3, primary ink, padding 12,0, min height 30, weight Medium; `ColorBgHover` on hover, `ColorBgElevated` while pressed, tertiary ink and no fill when disabled |
| `Button.primary` | `ColorAccent` fill with `ColorBgSurface` ink, accent border, weight SemiBold; `ColorAccentHover` on hover, `ColorAccentPressed` while pressed, flat outlined tertiary form when disabled |
| `Button.quiet` | No border, secondary ink, padding 8,0 |
| `Button.sm` | Min height 26, padding 9,0 |
| `Button.chevron` | Padding 6,0, no border, tertiary ink, primary ink on hover |
| `Button.pill` | Min height 26, padding 9,2, right margin 4, default border, secondary ink, weight Normal; `.selected` takes the emphasis border, primary ink and an elevated presenter |
| `Button.header-cell` | Left content alignment; `.numeric` right |
| `ToggleButton` | Transparent fill, 1 px default border, radius 3, secondary ink, padding 8,3, min height 26; checked takes the elevated presenter, primary ink and the emphasis border |
| `Border.tag` | `ColorBadgeBg` over a 1 px `ColorBorderDefault` outline, radius 3, padding 6,1, tertiary ink |
| `Border.rule` | One bottom edge in `ColorBorderDefault` |
| `TextBlock.t-hero` | Ratio 1.5, weight Bold, primary ink |
| `TextBlock.t-stat` | Ratio 1.2, weight Bold, primary ink |
| `TextBlock.t-label` | Ratio `FontSizeLabel`, weight Medium, secondary ink |
| `TextBlock.t-caption` | Ratio `FontSizeCaption`, tertiary ink |
| `TextBlock.figure` | Weight SemiBold, `tnum=1`, primary ink |
| `TextBlock.num` | Right aligned, `tnum=1` |
| `TextBlock.mono` | `FontMono` |
| `TextBlock.worse` | `ColorMetricWorst` |
| `TextBlock.section` | Weight SemiBold, primary ink, combined with `t-label` for size |
| `Border.callout` and `.warn`, `.error` | 1 px border, radius 3, padding 8,4, the composite fill and border |
| `Button.help` | The chevron shape with four setters of its own over it: min height 0, a fixed 28 by 20, centred vertically, and a content template of two drawn `Path` elements in a 16 square panel, a ring and an `i`, at stroke 1.4 in the tertiary ink and the primary ink on hover. The control sets both classes, `chevron` and `help`, and overrides its style key to `Button` so it inherits the built-in theme. 83 placements across 29 markup files (section 10's census, widened by two in Phase 15B for `target.guiding` and `stats.guiding`, by five in Phase 16 for `page.export-stacking` and the four `export.*` topics, all five in the one new file `WbppExportWindow.axaml`, by seven in Phase 17 for `page.analysis`, `analysis.filters` and the five `analysis.<tab>` ids, all seven in the one new file `AnalysisView.axaml`, and by four in Phase 20 for `dashboard.custom`, `settings.display.ledger-columns` and the two `settings.custom-columns.*` ids, two of them in the one new file `CustomColumnsTabView.axaml`). |
| `IconGear` | One `StreamGeometry` appended to `Theme/Controls.axaml`'s resources this phase (Phase 14B ruling on `questions.md` Q10): a ring with six detached radial strokes, drawn as a `Path` at 16 px carrying `ToolTip.Tip="Columns"`. Task 6's review found the gear repeating `Path.help-glyph`'s four stroke setters inline instead of reaching a shared class, so it drew with no pointer-over ink; the phase fixer closed it (fixer-list section 1 item 41, DONE): the four setters are now declared once for `Path.help-glyph, Path.icon-glyph`, `TargetListView.axaml`'s gear takes `Classes="icon-glyph"`, and a `Button:pointerover Path.icon-glyph` rule gives it the hover ink every other drawn icon has. The ring-and-six-strokes shape itself, which at 16 px reads as brightness rather than as a gear without the tooltip to disambiguate it, was **recorded as accepted rather than redrawn** (fixer-list section 1 item 42, DONE by its own second option): redrawing an icon is a design decision with no test that can judge it, and carried item 26 puts the whole icon set, this shape included, in 14C's hands. The sixth drawn or Unicode icon in the application, and now the second (after `Path.help-glyph`) to reach a shared stroke class rather than a private one; there is still no application-wide icon dictionary (fixer-list section 2 item 26). |
| Filter swatch | `Button Classes="quiet"` whose content is a `Border` filled with the resolved filter colour, opening a `ColorView` in a `Button.Flyout` |
| `StripItemTemplate` | Phase 14C. A keyed `DataTemplate` over `GalactiLog.App.ViewModels.IStripItem`, which declares three members and nothing else: `ShortLabel`, `FullLabel` and `IsMonospace`. It is the one template for every 48 pixel collapsed strip in the application, and section 6 says why the third use did not become a third copy. |
| `Button.strip-item` and `Button.strip-item.active TextBlock` | Phase 14C. Geometry only over `Button.quiet`, which already carries the borderless transparent form: padding 0,5, no minimum height, centred content, because the strip is 48 pixels wide and the padding is vertical alone. The active item's label takes `ColorAccent`, which is the strip's one use of the accent and is what section 8's accent refusal already scopes. |
| `ListBox.rail ListBoxItem` and `.compact` | Phase 14C, carried fixer-list item 32. Six selectors replacing two near-identical five-rule blocks that `MainWindow.axaml` and `SettingsView.axaml` each declared: the shared rules carry the navigation rail's 16,10 padding, and `ListBox.rail.compact ListBoxItem`, declared after them, overrides to the Settings tab strip's 14,8. Nothing renders differently, which was the requirement: a fold that also restyles a page is two changes reported as one. |
| `Button.page.current` | Phase 14C. Every page button is a plain `Button.sm`; this is the one class the current page adds over it, in the shape `Button.pill.selected` already uses: the emphasis border, primary ink and an elevated presenter. Declared after `Button.sm`, which is load bearing in this file. |
| `ItemsControl.refetching` | Phase 14C. `Opacity` 0.5 on the target list's rows container while a query is in flight. It is the whole of the refetch dim's appearance, and it sits on an `ItemsControl` selector rather than inside a `Button` or `ToggleButton` block, which `ControlStyleScanTest` forbids an `Opacity` setter in. The rows are made inert by `IsHitTestVisible` on the same container and by a guard in each keyboard-reachable command's own body, neither of which is a style. |
| `ScrollBar` | Phase 14C. **No selector and no control theme.** The platform Fluent `ScrollBar` theme is kept whole and restyled through its own resource keys: `ScrollBarSize` at 8 here, and six thumb and track aliases in each theme dictionary. Section 6 says why the aliases cannot live in this file and why the hand-written theme that first shipped was withdrawn. |
| `Controls/CustomCellEditor.axaml(.cs)` | Phase 20. The one editor for every custom column value (spec 12.15), on all four surfaces. It picks its kind from the column's type and nothing else: a `CheckBox` with no content, a `TextBox` watermarked with the column's name, or a `ComboBox` whose first entry is "Not set". It declares no style of its own, and it is the reason the three `.refused` selectors below sit in this file. |
| `TextBox.refused, CheckBox.refused, ComboBox.refused` | Phase 20. The error ink on a refused cell: `ColorError` as both `Foreground` and `BorderBrush`, carried by the editor itself rather than by a wrapper class, because both properties belong on the control and `CustomCellEditor.axaml` declares no style. A refused cell reverts to the stored value, takes this ink and carries the refusal sentence on its tooltip and in its `AutomationProperties.HelpText` until the next successful write. Nothing opens a dialog and nothing pushes a toast, so this ink is the whole of the refusal on a table row. |
| `RigLabelRowTemplate` | Phase 20, widened. The keyed `DataTemplate` both session pane tables draw a rig label row with. It now carries the **custom cell strip** as its trailing content: one horizontal run of a caption in `t-label` and a `CustomCellEditor` per rig-scope column, 4 apart inside a cell and 8 apart between cells. The caption is there because a rig label row has no header row, so a bare editor on it has no visible name (spec 12.15). The strip's visibility is bound rather than left to an empty `ItemsControl`: a visible zero-width strip still takes the row's own 8 pixel spacing, which is enough to move the pane's layout pins, so a night with no rig-scope column draws exactly what it drew before. |
| `Controls/DisclosureBand.axaml(.cs)` | Phase 15B. The chevron band's fourth-occurrence extraction (design lesson 1, overdue by the argument section 6 already made): one control over the `Auto,Auto,*` grid every disclosure band hand-assembled, with `Title`, `IsExpanded`, `ToggleCommand`, a `HelpSlot` content property for the section's help glyph and a `TrailingContent` content property for the band's own trailing chrome (a session count, a baseline segment). The session pane's four bands (Metrics over the night, Night detail, Guiding, Frames) all bind to it now, added with the fourth, "Guiding"; the trend band above the workbench and the Nights ledger's own collapse are a different shape (no help glyph, no trailing content slot the same way) and are not converted. A fifth band of the session pane's own shape is a control instance, not a fifth hand copy of the grid. |
| `IntegrationSendItemTheme` | Phase 21, spec 12.16. A `ControlTheme` over `MenuItem`, `BasedOn` the built-in `MenuItem` theme, binding `Header` to the item's name, `Command` to its send command, `IsEnabled` and `ToolTip.Tip` to its offered state and reason. It serves both of the Target detail page's overflow submenus, "Send to NINA" and "Slew Stellarium". It was first declared inline inside `TargetDetailView.axaml`, which `ControlStyleScanTest` cannot see; ruling B30 moved it here, the shared vocabulary rule every other `ControlTheme` follows, and widened the scan's needle to the whole `<ControlTheme` element so a future one declared inside a view fails the same way a local `Style` already does. |

This table dropped its third column, "Where it is used", in Phase 14B. Three phases running (Task
2's review, Task 3's review and Task 9's review this phase alone) found it stale the same phase it
was last hand-updated, and the coordinator's ruling on it is generate or drop: hand-maintaining
eleven classes' use sites across thirty-odd views has now failed three times, so this table stops
claiming to be the census. Find every current use of a class with
`grep -rn 'Classes="[^"]*<name>[^"]*"' src/GalactiLog.App/Views src/GalactiLog.App/Controls`, or
for a bare element selector such as `Window` or `Button` (no class), the same grep without the
class filter, or `ControlStyleScanTest`'s own walk (`Theme/Controls.axaml` is its one exemption).

The filter swatch keeps spec 14.5's "no tinted fill" rule for the button itself: the chrome stays
the shared quiet outline, and the colour lives inside the content, a `Border` the view fills
directly rather than a new button style. The same shape serves both the grouped row's swatch and
the smaller ungrouped one, at 14 px.

**New in the vocabulary this phase, drawn from the table above rather than a style of their own.**
The status bar's job monitor button and its flyout (`Views/StatusBarView.axaml`): `Button` for the
monitor, `Border.rule` between the running and recent sections, `TextBlock.t-label section` for
the two headings, `TextBlock.t-caption` for each job's progress message, and `Border.tag` for
nothing, because an outcome is a word in `t-label`, not a badge. The Create target form
(`Views/Settings/TargetsTabView.axaml`'s `CreateTargetSection`, reached from a "New target" header
button and from an unresolved row's "Create target" button): plain `TextBox`, `ComboBox` and
`CheckBox` controls with no local style, laid out under `Border.rule` the way the rest of the
Targets tab is, closed by `Button.quiet` and submitted by the plain `Button`. The scan filter
notice (`Views/DashboardView.axaml` and `Views/Settings/LibraryTabView.axaml`): `Border.callout`
with its one `Button.quiet` Review action, the same composite the session pane's status lines
already use.

**The two-worlds rule's state at the Phase 14B close.** `SetupWizardWindow`, `LibraryTabView`,
`TargetsTabView` (with `UnresolvedNamesView`), `LogViewerView`, `ActivityView` and
`DiagnosticsView` are now in the Ledger vocabulary: zero non-zero `CornerRadius` and no local
button or badge style, each verified by `ControlStyleScanTest`. `MaintenanceTabView` and
`SettingsView` were already clean and are unchanged.

**Amended at the Phase 14C close, and the amendment is smaller than the phase's own plan.**
`Views/Dashboard/TargetListView.axaml` **moved**: the rows are flat and rule-separated on a
shared-size spine, zero non-zero `CornerRadius` remains in the file, it declares no `<Style` of any
kind, its badges are `Border.tag` and its buttons are the shared vocabulary, all of which
`ControlStyleScanTest` and a `CornerRadius` grep both confirm. `Views/DashboardView.axaml` **did
not**: the phase rebuilt the page into three columns and replaced its two Unicode guillemets with
drawn `Path` geometries, but the six `RadiusLg`, `RadiusMd` and `RadiusSm` region containers it
carried before the phase were byte-identical to `HEAD`, so the page shell around the moved rows
was still card chrome. The rule fires on a structural edit, and the phase made one, so this was a
gap rather than a scoping decision. **Owner: the next phase that opens `DashboardView.axaml` for
layout**, which was the roadmap's Phase 20, custom columns.

**Discharged at the Phase 20 close.** Phase 20 opened that file for the filter panel's eighth
section and closed the gap in full: the six region containers lost their card fill and their
radius, no non-zero `CornerRadius` remains in the file, and nothing on the page shell draws a card
any more. A case in that unit's own view test holds it, counting the `Classes="callout"` sites the
page declares and failing a returning radius.
`FilterPanelCustomSectionTests.DashboardView_CarriesNoNonZeroCornerRadius` holds it, asserting
`Regex.Matches(source, "Classes=\"callout\"").Count` equals 3 over `Views/DashboardView.axaml`'s own
source text. The phase review (`task5b-review.md` P3-7) asked for two of those three sites,
`UnreachableRootBanner` and `QueryFailureBanner`, to take `Classes="callout warn"` and
`Classes="callout error"` instead of the plain `callout` class with two locally repeated brushes,
which would change this count in the same edit; that edit had not landed in the source read for
this report, so the count stays 3 and the class stays the plain, undifferentiated `callout` on all
three sites.
`work/phase14c/fixer-list.md` section 2 item 27 is closed for the Dashboard half; `StatisticsView`
and the two Settings shell views named under the same item keep their own owners and stay open.

**One carry is accepted and is not a gap.** `TargetListRegion` keeps its 16 pixel padding and its
1 pixel edge, because `DashboardView.axaml.cs`'s `ListRegionChrome` is the literal
`(2 * 16) + (2 * 1)` and is the figure the filter panel's layout bound is computed from: the
markup in this file is what that constant reads. Dropping two of the four edges frees two pixels,
which at the window minimum crosses the target row's own column trimming breakpoint and pushes the
Expand button past the list's trailing edge, which
`AtTheWindowMinimumAllotment_TheExpandButtonAndThePageSizeSelect_AreInsideTheList` fails on. The
edge and the constant therefore change together in one edit or not at all.
`TargetListRegion` keeps its four-sided 1 pixel hairline and `DashboardView.axaml.cs`'s
`ListRegionChrome` keeps its 34 pixel figure. A top-only rule was tried and reverted: dropping the
left and right edge freed two pixels, which at the window minimum crossed the target row's own
column-trimming breakpoint and moved three layout pins by those two pixels, including pushing the
Expand button past the list's trailing edge. This section's rule concerns the radius and the card
fill, both of which are gone from this file, not a 1 pixel outline, so the carry above is discharged
in full for `DashboardView.axaml` as written.

Also still in the old vocabulary: `Views/Merge/MergeHistoryView.axaml` and
`Views/Settings/RenameHistoryView.axaml` (owner the Targets tab, whichever phase next opens
`TargetsTabView.axaml` for layout), and `StatisticsView` (owner Phase 15B, which roadmap section 0
names as the phase that closes Statistics; Phase 14C's own edit to it was an input-handler insert,
not a structural edit, so the rule correctly did not fire).

**The chevron band** is the one disclosure idiom in the application. Phase 13 grew it from one use
to five: the trend band above the workbench, the session pane's three collapsible sections
("Metrics over the night", "Night detail", "Frames"), and the Nights ledger's own collapse. Phase
14A gave four of the five a help glyph, which turned each band into an `Auto,Auto,*` grid with the
chevron button in column 0, the glyph in column 1 and the band's own trailing content in column 2,
the same hand edit made four times. **Phase 15B extracted that grid at its fourth occurrence**,
the session pane's own four bands (its fourth, "Guiding", was the one that would have made it a
fifth hand copy): `Controls/DisclosureBand.axaml(.cs)` above is the control, and the trend band and
the Nights ledger's collapse, which never carried the help-glyph grid, are unchanged and stay their
own shape.

**The help flyout.** One flyout shape in the application, declared once as the keyed
`HelpTopicFlyoutTemplate` in `Theme/Controls.axaml` over `GalactiLog.Core.Help.HelpTopic`:
the topic's title in `t-label section` over its paragraph in `TextBlock.help-paragraph`, both
wrapping, on a column bounded at 420 with a 6 pixel gap. `HelpButton` looks the template up and builds its own `Flyout`
from it, because a `Setter` cannot yield a `FlyoutBase` and a `<Flyout>` written in a setter would
be one instance shared by every glyph. The template's root is `Focusable` with `IsTabStop` off and
the control focuses it when the flyout opens, which is what makes the paragraph reachable from
the keyboard; it is a mechanism, not decoration.

**The band inks.** Three semantic brushes carry the frame table's quality bands and nothing else:
`ColorSuccess` for better, `ColorWarning` for watch, `ColorError` for reject, as
`TextBlock.band-better`, `TextBlock.band-watch` and `TextBlock.band-reject` in
`Theme/Controls.axaml`. There is no neutral class, because neutral is the primary ink, which is
to say no mark. The row score takes the same three on `Border.score-rule`, a 2 pixel rule down the
row's leading edge with a transparent default, never a fill: the band is carried by ink, which is
why the refusal list's "tinted button fills" entry and spec 14.5's no-tinted-fill rule both still
hold on a page that now colours six cells per row.

## 5. Spacing and geometry

There is no single modular grid. The steps actually in use are 2, 3, 4, 6, 8, 10, 12, 16, 18, 20,
28 and 32; 6 and 12 carry most of the page. 18 is not a step chosen from the list: it is the
measured width of an empty `CheckBox`, and the ledger's selection gutter takes it because the box
has to fit, so it is a measurement that happens to be a width rather than a spacing decision.

| Figure | Value | Where it is declared |
| --- | --- | --- |
| Page gutter | 20 left and right, 18 top, 12 bottom | `TargetDetailView.axaml`, the page root `Grid.Margin` |
| Workbench gutter | 28 | `TargetDetailView.axaml`, the workbench column set `Auto,28,*` |
| Session pane row gap | 12 | `SessionPane.axaml`, `RowSpacing` |
| Ledger width | 640 wide, 520 narrow | `TargetDetailViewModel.WideLedgerWidth` and `NarrowLedgerWidth` |
| Ledger breakpoint | 1600, measured on the page's own width | `TargetDetailViewModel.WideBreakpoint` |
| Details drawer width | 380 | `TargetDetailView.axaml`, `SplitView.OpenPaneLength` |
| Reference thumbnail | 128 square | `TargetDetailView.axaml`, `DetailThumbnailSize` |
| Summary table row height | Night row 34, target row 36, header row 28 | `TargetDetailView.axaml`, the ledger `ListBoxItem`, the target row `Grid` and `LedgerHeaderRow` |
| Frame table row height | 28 | `FrameTableView.axaml`, `Border.frame-row` |
| Frame table header cell padding | 4,2 | `FrameTableView.axaml`, the `FrameHeaderCell` template |
| Frame column widths | Time 78, file name 240, text 90, number 84, RMS value 70 | `FrameTableView.axaml` resources |
| Night strip height | 72 | `NightStrip.StripHeight` |
| Frame table floor | 36 toolbar plus 30 header plus eight rows of 28, clamped to half the pane | `SessionPane.axaml.cs`, `FrameTableMinHeight` |
| Pane row gap subtracted from the scroller cap | 12 | `SessionPane.axaml.cs`, `PaneRowSpacing` |
| Narrow ledger numeric minimums | 48, 52, 60, 52, 60, 56, 52, totalling 380, each with a maximum, and the Night column starred with a 104 floor. The seven numeric figures are unchanged by Phase 14A; only the Night floor moved, from 108, and it moved because the cell gutter under it did | `TargetDetailView.axaml`, the three ledger column sets |
| Ledger cell gutter | 2 horizontal, narrow and wide alike (4 before Phase 14A, 8 before Phase 12) | `TargetDetailView.axaml`, `Grid.ledger-row > TextBlock` |
| Ledger selection gutter | 18, the measured width of an empty `CheckBox`, holding the night selection box; it replaced the 2 pixel lit-edge gutter, and its leading 2 pixels are still that edge | `TargetDetailView.axaml`, the first column of the three ledger column sets |
| Per-night thumbnail box | Bounded at 120 high by 240 wide, `Uniform`, never cropped, with an 8 pixel gap between boxes carried as a trailing margin cancelled by the strip's own negative margin | `SessionPane.axaml`, `ThumbnailStrip` and its item `Image`; the strip is the first row of the "Night detail" section, not a fixed row of the pane |
| Frames chrome reservation | Measured off the loaded control rather than declared, and zero while the section is closed | `SessionPane.axaml.cs`, `ApplyHeights` reading `FramesChrome.Bounds.Height` |
| Control height | 30, and 26 for `sm`, `pill` and `ToggleButton` | `Theme/Controls.axaml` |
| Control radius | 3 | `Theme/Controls.axaml`, a literal, not `RadiusSm` |
| Filter table minimum width | 682 | `SessionPane.axaml.cs`, `FilterTableMinWidth` |
| Table gutter | 32 | `SessionPane.axaml.cs`, `TableGutter` |
| Ranges table width | 360 | `SessionPane.axaml.cs`, `RangesTableWidth` |
| Pane side-by-side breakpoint | 1074, the sum of the three above | `SessionPane.axaml.cs`, `WideBreakpoint` |
| Ledger guiding RMS cell (Phase 15A) | A 44 pixel value box plus a 9 pixel mark box inside the unmoved 56 to 58 `LedgerRms` shared-size group; the mark box always reserves its width and is empty on an unmarked night, so every row (night, totals) shares one right digit edge whether or not it carries the guiding-source dagger | `TargetDetailView.axaml`, `LedgerGuidingRmsText` and `LedgerGuidingRmsMark`, all three consumers of the `LedgerRms` group (header, night row, totals row) |

The two breakpoints are structural, not chosen: the pane's is asserted to equal the sum of its
parts, so a column added to the filter table moves it.

## 6. Patterns

**The aligned table.** Three grids under one `Grid.IsSharedSizeScope` with one `SharedSizeGroup`
per numeric column, so the header, the target row and every night row cannot drift apart and the
widest figure in a column sets that column's width. The nights ledger, the session pane's filter
table and its ranges table all use it. A shared-width case that compares columns sitting at their
declared `MinWidth` proves nothing, so each case seeds a figure wide enough to exceed the minimum
(123456.789) and asserts the measured column exceeds it before comparing the grids. The pane's two
cases were run red with the `SharedSizeGroup`s removed; the ledger's carries the same seed and the
same guard assertion.

**The lit row.** One selection idiom in the whole application: `ColorBgElevated` on the selected
presenter plus a 2 pixel `ColorAccent` edge, with the ledger adding the accent ink and the frame
table not. The edge is drawn in the row's own first column or as an overlay rather than as a
`BorderThickness`, so turning it on shifts no cell. Since Phase 14A the ledger's own first column
is 18 pixels rather than 2, because it holds the night selection check box; the edge is still its
leading 2 and nothing else about the idiom changed. The frame table's first column carries the
row-score rule the same way, a 2 pixel `Border.score-rule` in the band's ink, so a row can carry a
selection edge and a score rule without either shifting a cell.

**The rig label row.** The shape a shared table takes when a night was imaged by more than one
rig: a full-width row in the table's own grid carrying the rig's label in the `section` type and
its frame count, with that rig's rows following under it. The columns and their shared size groups
are unchanged, so two rigs' blocks stay aligned with each other and a single-rig night renders
exactly as it did before. It is written once, as `RigLabelRowTemplate` in `Theme/Controls.axaml`
over one `RigLabelRowViewModel`, and placed by a `ContentControl` that carries the column span;
the session pane's filter table and its ranges table both place it. A later table that splits per
rig places the same template rather than writing a third copy.

**The thumbnail strip.** A wrapping row of picture boxes at the head of the section that
describes them, one box per rig, each bounded rather than sized: a maximum height and a maximum
width with `Uniform` fit, so the frame's own aspect survives and nothing is cropped or scaled down
to force a fit. The wrap is the `WrapPanel`'s own behaviour and not a measured branch, so a
narrower pane or a third rig puts a box on the next line. It sits inside a collapsible section,
which is what makes it free at rest: a collapsed subtree is not laid out, and the decode that
fills it is started by the section opening rather than by the night loading.

**The finding as a sentence.** A session insight is a line of prose with a "show" action that
filters the frame table, not a callout box and not a badge. The action is a toggle: show, then
shown, and clearing the filter anywhere clears the state.

**The collapsed vertical strip.** A pattern rather than a one-off: the navigation rail collapses
to it, and Phase 13 gives the Nights ledger the same shape when it collapses. 48 pixels wide, a
chevron at the top that reopens it, one short label per item with the item's full label on its
tooltip, and the open item drawn in the accent ink. On the ledger the label is the night's date in
`MM-dd` form ("09-08"), in `ColorTextSecondary`, the navigation rail's own ink, rather than the
expanded ledger's primary ink; the open night's label is the exception, in the accent ink; the
target row is an "All" button rather than a list item, because it is not a selectable night, and
it reopens the ledger on click, the same action the chevron performs. Both chevrons are drawn
`Path` elements, matching this pattern's rule below. The rail's own toggle is still a glyph rather
than a drawn path, a pre-existing exception this pattern does not extend.

**The session pane's three bands.** Since Phase 13, the pane's fixed content is three collapsible
sections in a fixed order, each behind its own chevron band: "Metrics over the night" closed by
default, "Night detail" closed by default holding the thumbnail strip as its first row, "Frames"
open by default. Each closed section lays out
nothing: a closed "Night detail" collapses to its band alone, and a closed "Frames" section
collapses the same way, which is what lets the height contract's fixed-rows scroller claim the
whole pane rather than a share of it (section 5's height contract figures). The "Frames" section
hides the frame table by `IsVisible` alone, never by removing it from the tree: the table's own
starred row keeps its parentage and its height contract whether the section is open or closed, so
a hidden table degrades to zero space rather than to an unbounded one.

**The collapsed vertical strip, a third use.** The Dashboard filter panel's collapsed state
(spec 12.2) takes the same 48 pixel shape this pattern already names for the navigation rail and
the Nights ledger: a chevron at the top that reopens, one short label per item on the item's own
tooltip, and the open or active item in the accent ink. Its items are the seven filter sections'
short labels rather than dates, and clicking an item expands the panel with that section already
open rather than switching a selection, which is what the ledger strip's own click already does
for a night; the difference is what the click does, not the strip's shape, so this is the third
use of the pattern rather than a fourth hand copy.

What was actually lifted is **the item template alone**, as the keyed `StripItemTemplate` over a
new three-member `IStripItem`. The two containers are not the same control and were never going to
be: the Nights ledger's strip is a `ListBox` with a two-way selection, and the filter strip is an
`ItemsControl` of `Button.strip-item` buttons with no selection at all, because clicking a filter
section opens the panel rather than choosing one of seven. One template over one interface is what
made that difference cost nothing; a shared container would have had to carry a selection the
filter strip does not have. The ledger's five `ListBox#LedgerStripNights` styles are untouched and
still select on the container's name, descending into whatever the shared template produced.

**The draggable splitter.** A `GridSplitter` between the filter panel and the target list, width
clamped 220 to 480 pixels, dragging a proxy rather than the live column so the target list's
shared-size spine is not re-measured on every tick, and committing the width to
`display.dashboard.filter_panel_width` on release only (ruling E1). No other split view in the
application drags today; if a second one is ever wanted, this is its spine.

Two mechanisms it needs that a reader would not guess. There is no view-model event for a finished
drag, so the commit is read from the panel `ColumnDefinition`'s own `WidthProperty` change in
`Views/DashboardView.axaml.cs`; the clamp and the persistence stay in the view-model and the
handler holds no policy. And the same handler re-asserts the column width whenever the column and
the view-model disagree, because an Avalonia binding caches the last value it published and skips
republishing an equal one, so a second drag that clamps to the same 480 would otherwise leave the
column at whatever the splitter wrote locally, widening with every further drag.

**The thin scrollbar.** Every `ScrollViewer` and list in the application draws the platform Fluent
`ScrollBar`, restyled rather than replaced: 8 pixels wide in either orientation, a hairline thumb
in `ColorScrollbarThumb` over a transparent `ColorScrollbarTrack` at rest, expanding under the
pointer to the full 8 pixels with the platform's own line buttons and the thumb in
`ColorScrollbarThumbHover` under the pointer or while dragging. There is no third colour: the
platform's pressed fill takes the hover token. The bar overlays its scroller's trailing edge and
takes no layout space of its own, which is the platform `ScrollViewer` theme's own `AllowAutoHide`
behaviour and not something this application declares, so no scroller's content is measured
narrower for a bar that may or may not be drawn (spec 12 shell paragraph, spec 14; Q11's reserved
gutter was ruled, tried and reversed in the same phase, spec 12.5's fixer report has the
mechanism). A control placed at a scroller's trailing edge keeps its own 8 pixels clear of the
overlaid bar where that matters.

The restyle is seven resource overrides and no template: `ScrollBarSize` at 8 in
`Theme/Controls.axaml`, and six aliases from the Fluent theme's own thumb and track keys to the
three tokens, declared in each theme dictionary beside the tokens themselves because a
`StaticResource` in the control file would freeze on whichever theme was merged when it was
parsed. Ruling E3's hand-written control theme is withdrawn (phase review P1 and P2): written from
a blank page it shipped every vertical scrollbar upside down, its `Track` keeping
`IsDirectionReversed` false where the platform's vertical template sets it true, and it dropped
the platform's collapsed and expanded states, leaving a full strength thumb over the trailing
content of every scroller. Both were invisible to the suite, the theme census and three task
reviews. **A platform control template is restyled through its resource keys before it is ever
replaced, and a control theme that is written carries a geometry case for each state and
orientation it draws.**

**The modal page with a docked footer** (Phase 16, `Views/TargetDetail/Wbpp/WbppExportWindow.axaml`,
spec 12.13). The third `ModalPageWindow` page after the merge preview and Copy frame list, and the
first whose body is long enough to scroll under something fixed. The footer is declared **before**
the body inside the `DockPanel`, so the dock gives it the bottom edge and the scroller takes what
is left; tab order follows declaration order, so both footer buttons carry an explicit `TabIndex`
that puts them after the fields they act on. The window declares no style of its own at all: it
reaches `Border.callout warn`, `Button.primary`, `Button.quiet sm`, `TextBlock.t-label section`,
`TextBlock.mono`, `controls:DisclosureBand` and `controls:HelpButton` from the shared vocabulary,
and it names no colour, brush or font size. Inside the footer, order is the long-path warning, then
the generate failure, then the totals, the staging line and the footer note, with Close and the
Generate split control on the trailing side: a warning about the copy sits **above** the figures it
qualifies rather than beside them.

**Escape closes, and no page declares a default button.** The window carries one
`Window.KeyBindings` entry binding Escape to its close command, which is the shape
`Views/Preview/PreviewModalWindow.axaml` already used; three such bindings ship. Generate is a menu
rather than a single action, so there is no action Enter could mean, and Enter inside a path field
or a threshold box must never write a file. A modal page therefore gets a real Close button in the
footer instead of relying on the window chrome.

**A cost badge is ink, not a tag.** The export page's contamination badges ("+2 other nights",
"+1 other target") are plain `TextBlock` elements in `ColorWarning` with the names themselves on
the tooltip, not `Border.tag`. `Border.tag` is an outlined neutral label for a fact; these are a
cost the reader is being warned about, and the one semantic ink carries that with no box around it.
They appear twice per night, once on the chosen row and once on each contaminated level in the
tree, from one view-model pair.

**A flyout is declared on its button, never in a `Setter`.** The target page's Export button
(spec 12.4) carries its `MenuFlyout` inline, because a `Setter`'s value is one instance shared by
every control the style reaches, which is the same reason the help flyout is a keyed
`DataTemplate` the control builds from. Its two `MenuItem` headers are bound rather than literal,
because each appends the checked-night count to itself. One binding mode is load bearing here and
is not obvious: a `Mode=OneTime` binding on a menu item evaluates **before** the popup inherits its
`DataContext` and never again, so the default-flavour radio mark on the Generate menu silently
never appeared until it was `OneWay`. No other `OneTime` binding on a menu item ships.

**The quality panel's metric cell** (`Views/TargetDetail/Wbpp/QualityPanelView.axaml`) is the second
host to re-declare the three band inks locally, after `FrameTableView.axaml`: nine `TextBlock`
styles that re-state `band-better`, `band-watch` and `band-reject` inside this host so they win
over the layout-role style, binding the same `ColorSuccess`, `ColorWarning` and `ColorError` tokens
the shared file binds. It declares no `Button`, `Border.callout` or `Border.tag` style. Each metric
column is a 56 pixel value box plus a 12 pixel mark slot, and the mark slot is present on every
cell whether the row failed a gate or not, so the digits keep one right edge down the column and a
failing row does not shift its neighbours.

**A chart control is painted by the control vocabulary, not by the view that hosts it.**
`Controls.axaml` carries three style blocks, one per chart type the package ships
(`CartesianChart`, `PieChart`, `PolarChart`), each setting the four tooltip and legend paints from
four `DynamicResource` keys that `ChartTheme.Apply()` writes into the application resources on
every call. That is the whole mechanism: a setter beats the styled-property default the control
resolves once per process, and a dynamic resource re-evaluates when the key is written. A view
that sets one of the four locally beats the block and is what `ChartPaintCensusTests` fails on.
Three blocks rather than one comma-separated selector is a compiler rule: a multi-type selector
resolves its setter target to the common base, `UserControl`, which declares none of the four, and
the one-block form fails to build.

**An axis follows a theme only by being replaced.** Measured at rc5.4: an axis instance that has
already been measured keeps the ink it was built with, and a later `ChartTheme.Apply()` does not
revisit it. The theme's axis rule is a closure that runs **when an axis is built** and at no other
time. Every chart view-model therefore publishes new axis objects from its theme handler; one that
answers a swap by rebuilding only its series leaves its axis furniture in the previous theme.
Under the Avalonia headless platform only the **first** chart control in the process ever measures
its axes, so a test that reads ink off a hosted control is a false green or a false red by test
order, and the case that pins this reads the ink off an axis the builder list was run over.

**An in-place list refresh under a bound picker must carry the selection by identity.** When the
row at a bound `ComboBox`'s selected index is replaced in its `ObservableCollection`, Avalonia
writes `null` back through the two-way `SelectedItem`, synchronously, inside the publish: it does
not write the new item and does not leave the selection alone. An insert ahead of the selection is
safe, but an apply that compares by index and writes never inserts, so an inserted row arrives as
a chain of replaces over the reader's own index. A refresh that applies rows in place therefore
captures each selection's identity before the apply and reassigns it after, with the selection
setters silenced for the duration. The Analysis filter bar and Compare's two group pickers are
built this way.

**A chart axis with no limits fits itself to the data it holds.** A category axis given ten labels
but points in only nine of the ten drops the tenth category entirely, labels and all, which is how
the Matrix shipped a ten by ten grid that drew nine columns. A grid whose cells are optional pins
both axes to the full category span and lets the empty ones draw blank.

**`FocusAdorner="{x:Null}"` is a no-op, and a case asserting a null `FocusAdorner` is vacuous.**
Null is exactly the state that makes the platform reach for its own default adorner, a plain white
two pixel rectangle, white on `red-light` too. Suppressing it needs an adorner template that draws
nothing, an empty `Panel`. No view under `src/` uses the null idiom today, so nothing shipped
relies on it.

**A chart's draw margin is known at `CartesianChartEngine.DrawMarginDefined` and at no earlier
signal.** An overlay that has to sit square with a chart's painted plot area reads the margin
there and follows a resize from the same event. `UpdateFinished` is never raised without a render
loop, so a view wired to it is wired to nothing under the headless harness and depends on the
animation loop in the application; `Measuring` fires before the measure and offers a margin one
pass stale. The measure is also throttled, so the layout pass that follows a resize carries
nothing and the new margin lands a few hundred milliseconds later.

## 7. The drawn controls

Four now, in landing order: `NightStrip`, `CalendarHeatmap`, `GuideGraph` (Phase 15B) and
`AltitudeArc` (Phase 15B). Each is a `Control` subclass in `Controls/` with no `.axaml` beside it,
drawn in `Render`, one `StyledProperty` per ink handed in by the host through `DynamicResource`
with no colour literal in the file, a computed `MeasureOverride`, and a headless render tick in
its own tests. `GuideGraph` adds ruling G1 on top of that shared shape: every pointer handler
turns a position into a data coordinate and calls one pure member of `GuideGraphViewModel`, so
no clamp, window arithmetic or ported figure lives in the control itself
(`GuideGraphTests.GuideGraphSource_HandlersAreThin` is the enforcement, alongside
`GuideGraphSource_ContainsNoColourLiteral`).

**`Controls/DrawnTip.cs`**, the shared tooltip retarget, extracted at its second occurrence
(Phase 15B Task 4a). A drawn control has no per-mark visual to attach a tooltip to, so `NightStrip`
and `GuideGraph` both retarget one attached `ToolTip.Tip` as the pointer crosses what they drew,
through `DrawnTip.SetTip(control, ref last, text)`, which writes the property only when the text
actually changes, because pointer moves arrive by the hundred across one render pass and most of
them land on the same mark as the one before. `AltitudeArc` takes a different route (below) rather
than this one, for two reasons that both bind: its tooltip is a table of seven figures a plain
string cannot carry, and spec 12.5 requires it to answer keyboard focus as well as hover, which an
attached string tip cannot do either. `CalendarHeatmap` predates the extraction and already used
the same bound-panel route `AltitudeArc` now shares.

**`Controls/GuideGraph.cs`** (spec 12.4, section 13), the third drawn control: one guiding
session's RA and Dec error against the time of night, with settle bands, star-lost points and
dither lines, drawn for the same reason `NightStrip` is, a session is thousands of frames and a
composed chart would be thousands of visuals. Not LiveCharts. Its gestures (wheel, shift-wheel,
drag, double-click, the corner height grip) are described in full in spec 12.4; here it is only
the vocabulary entry: no colour literal, ruling G1's thin handlers, and the `DrawnTip` retarget for
its hover readout in `NightStrip`'s own shape.

**`Controls/AltitudeArc.cs`** (spec 12.5, section 13), the fourth drawn control: one rig's quarter
dome, the observer at the lower left, the horizon to the right and the zenith at the top, carrying
three wedges in horizon-first order. Two departures from the shared shape, both deliberate: it is
**focusable at rest**, the opposite of `NightStrip`'s press-to-focus rule, because it sits in a
page body with nothing competing for focus and spec 12.5 requires its figures to be reachable
without a pointer; and its tooltip is a bound `HoveredWedge` `DirectProperty` behind a panel the
page binds, `CalendarHeatmap`'s route, not `DrawnTip`'s, because a seven-row table and a
focus-answering tooltip are both past what an attached string tip can carry.

### The night strip

`src/GalactiLog.App/Controls/NightStrip.cs`, with
`src/GalactiLog.App/ViewModels/TargetDetail/NightStripViewModel.cs`. Drawn in `Render` rather than
composed, for the same reason `CalendarHeatmap` is: a four hundred frame night would otherwise be
four hundred visuals and four hundred bindings for a graphic that is a few hundred one-pixel lines.
There is no `.axaml` beside it, because there are no templated children to hold.

What it draws, at a fixed height of 72 with an 8 pixel inset at each end:

- The astronomical-night band, a fill with no border, from dusk to dawn, present only when the
  observer location is configured (`AstroNight.NightBounds`, spec 8.4).
- One tick per exposure at its capture time, 1.4 pixels wide, in that frame's filter's configured
  colour.
- An outlier tick, 2 pixels wide and taller, in `ColorMetricWorst`, for a frame either outlier rule
  flagged.
- An axis line with hour marks and hour labels in local wall clock, and the first and last frame
  times a tier above them.

Every colour arrives from outside: the tick inks from the view-model, which built them from the
application's one filter-colour resolution, and the five brush properties from the host through
`DynamicResource`, so a theme swap repaints the strip. The control paints a transparent fill over
its own bounds before anything else, so the pointer answers to the whole 72 pixel band rather than
only to the drawn ink (Phase 13, the hover link below needed it; `work/phase13/task6-report.md`
section 13). Focusability follows a press rather than sitting on at rest: a press on a tick turns
it on and focuses the control, a press that misses every tick leaves it off and leaves focus on
the frame table (a Phase 12 rule the Phase 13 fix kept true), and losing focus turns it off again.
It is not a tab stop, because it handles no key.

**The hover link.** A tick's hover tints the matching frame row and scrolls it into view; a tick's
click selects it and opens the preview.

**Amended by user ruling U1 (Phase 14C).** The strip now draws **one** mark for the hover, and the
refusal that produced the old sentence holds for everything else. The user's own report was that on
a night with many close-set ticks the tick under the pointer cannot be told from its neighbours, so
the mark that "belongs on the row" was unreachable in the one direction the reader was looking.
What the strip draws is a single **active** tick: taller than its neighbours, past the band at both
ends, 2 pixels wide in `ColorTextPrimary`, with a small drawn caret on the axis beneath it. Every
other tick keeps its filter colour untouched: no fill, no glow, no dimming of the rest and no
second highlight layer, so section 8's refusals of an accent hue carrying state and of a second
highlight layer both still hold, and the row tint stays the mark for everything but the one active
frame. "Active" also covers the resting case, the frame the table has selected or the preview has
open, which is what makes the strip and the table point at the same frame in both directions rather
than only from the strip to the table.

## 8. Refused

Each of these was available and was not taken. The clause after each says what carries the job
instead.

- Card chrome and nested bordered containers. Alignment and a 1 pixel rule carry containment.
- Tinted button fills and gradients. A 1 pixel outline over a transparent fill, with light for
  hover and press.
- A coloured left edge above 1 pixel. A callout's whole border is its 1 pixel.
- Pill badges. `Border.tag`: outlined text in the tertiary ink.
- Uppercase tracked labels. Sentence case at weight 600 in the primary ink.
- A Unicode glyph standing in for an icon. Drawn paths in one stroke weight; the sort direction
  marker in a table header is the one text run that survives.
- An eyebrow label above a heading. The heading says what it is.
- A sparkline standing in for content. The night strip is the graphic, and it carries one mark per
  real exposure.
- A hero-metric tile. The log line carries the totals as one sentence. This refusal is scoped to
  Target detail, the one page built on this vocabulary; it is not a ruling against a hero tile
  anywhere else in the application (UI layout ruling, 2026-09-17).
- An accent hue carrying state. Light carries state; the accent is a near-white ink.
- A page-level scroller. The workbench is the page's starred row; the pane's fixed rows sit in
  `PaneScroller`, a vertical scroller whose `MaxHeight` is set from the pane's own height on every
  size change; and the frame table is the pane's starred row outside that scroller, floored at its
  own chrome plus eight rows and clamped to half the pane. The table therefore always has height,
  the scrollbar sits on the block that grows with the data, and a pane too short for the floor
  degrades to two scrolling blocks rather than one hidden one.

## 9. Themes

A fourth theme is one dictionary under `src/GalactiLog.App/Theme/Themes/` carrying the 41 brush
tokens, the eight callout composites, the four semantic `*Value` colours the composites derive
from, the two gradient stops with their `BrushPageBackground`, the six platform scrollbar
aliases section 6 names and the ten combo box and date picker ink aliases below: 72 `x:Key`
entries, which
`ThemeResourceTest.TheThreeShippedThemes_DeclareOneIdenticalKeySet` asserts are the same set in
every dictionary.

**The ten ink aliases, added by Phase 17 on the same mechanism as the scrollbar's.** A combo box
and a date picker take their ink from Fluent resource keys rather than by inheritance, so each
dictionary maps five of them to its own `ColorTextPrimary` (`ComboBoxForeground`,
`ComboBoxForegroundFocused`, `ComboBoxForegroundFocusedPressed`, `DatePickerButtonForeground`,
`DatePickerButtonForegroundPressed`) and five to its `ColorTextTertiary`
(`ComboBoxForegroundDisabled`, `ComboBoxPlaceHolderForeground`,
`ComboBoxPlaceHolderForegroundFocusedPressed`, `DatePickerButtonForegroundDisabled`,
`TextControlPlaceholderForeground`). The last is the platform's single placeholder ink and is
shared with every text box watermark, which therefore also takes the tertiary token. Three
families of platform ink are deliberately left alone because each pairs with a background this
does not touch: the open drop-down's rows, tooltips, and the `Expander` chevron. One entry goes in `ThemeManager`'s private table; `Available` is a derived
projection of it and is not edited. Nothing else changes: the picker binds `ThemeManager.Available`,
which derives from that table, and `ThemeManager.Apply` re-runs `ChartTheme.Apply()` after the swap
so live charts repaint.

The table's first entry is the default for a profile with nothing stored. A stored
`general.theme` wins, and it wins at startup: `ThemeManager.ApplyStored` is called once as the
application starts, with a `StartupTheme` value the host registers from the settings document it
has already read, so a theme survives a restart rather than lasting until the next launch. An id
this build does not ship falls back to the first entry. A `SourceScan` census pins the startup call
site, because the headless harness cannot reach it.

`ThemeResourceTest`'s census case is what fails when a key is missed: it loads each dictionary and
asserts the full key set is present, and a second case asserts the three sets are equal, so a key
added to one dictionary and forgotten in another is a build failure rather than a missing brush at
runtime.

## 10. What the tests enforce

| Test | What has to be true for it to fail |
| --- | --- |
| `ThemeResourceTest` | A theme dictionary is missing one of the 41 brush tokens or one of the callout composites, the three key sets are not identical, or a dictionary named in `ThemeManager`'s table does not load |
| `ScrollBarThemeTests` | The scrollbar is not 8 pixels wide, a vertical thumb does not sit at the top of its track at offset zero or at the bottom at maximum, a forward drag or a page down does not increase the offset, the thumb does not collapse at rest and reach full width while the bar is expanded, a thumb or track ink does not resolve to its theme's own token in all three themes, a scroller's content width depends on whether a bar is drawn, or a file under `src/` declares a `ScrollBar` style or control theme |
| `ControlStyleScanTest` | A `Button` style carries a gradient or a semi-transparent tinted fill, or a view under `src/` declares a style of its own for any of the four needled selectors, `Button`, `ToggleButton`, `Border.tag` or `Border.callout`, instead of using the shared one |
| `FontSizeTokenTest` | A `FontSize` binds a ratio key directly instead of going through `MultiplyConverter` over `$parent[Window].FontSize` |
| `FontResourceTest` | One of the six font files is missing from `Assets/Fonts`, is not a static TrueType instance, or is not reachable as an Avalonia resource |
| `NightStripTests.NightStripSource_ContainsNoColourLiteral` | `NightStrip.cs` names a colour of its own instead of taking every brush from its host |
| `ButtonStateStyleTest` | The disabled or pressed presenter of `Button.primary` renders the wrong brush |
| `WindowBackgroundsTest` | A window root paints anything other than `BrushPageBackground` |
| `HelpPlacementCensusTest` | A `HelpButton` under `src/GalactiLog.App` names a topic id `HelpTopics` does not hold, a topic id is placed twice or not at all, or the placed set and `HelpTopics.Ids` differ. A bound `Topic` counts as the set of ids its view-model's total switch produces, resolved by markup file, so a bound site the map does not know fails rather than borrowing another site's ids. Its own offender case feeds it an unknown id, a missing `Topic`, a doubled id, a glyph inside an XML comment and an unmapped bound site, so the rule is falsifiable and not merely green |
| `HelpTopicsTests` | `HelpTopics` does not hold 93 topics with unique ids, a paragraph is empty, carries a double space, does not end in a full stop, carries an em or en dash, or says sigma outside `target.frames`' two correcting sentences; or one of the five longest paragraphs differs by a byte from spec 12.12's row for it. The case **computes** its five from the table itself, longest first with ties broken by id, and fails by name when a paragraph grows into the five with no transcription beside it. It named five ids by hand until Phase 16, by which point three of them had fallen out of the five and nothing was red |
| `ChartPaintCensusTests` | A chart element in the markup sets one of the four tooltip or legend paints locally, beating the shared style block; `Controls.axaml` does not declare all three chart types with all four paints; or a titled axis's `NamePaint` is not the theme's axis ink across a live theme swap |
| `ChartEmptyAxisCensusTest` | A type whose name ends `ChartViewModel` publishes an empty axis array in its empty state, which throws "XAxes and YAxes must contain at least one element" at rc5.4 and takes the whole page down with it |
| `AnalysisResultRegionCensusTest` | A file matching `Views/Analysis/*TabView.axaml` declares no element named `ResultRegion`, or more than one, so a sixth tab could hide its own pickers or draw its chart in every state |
| `InheritedInkTests` | A bare `TextBlock` or a `num` figure does not take its theme's primary ink, the inherited ink does not follow a theme swap, the one `Window` style does not reach a shipped `Window` subclass, a filled button's label or an inked class loses its own ink to the inherited one, a combo box or date picker does not take its theme's inks, or a theme dictionary is missing one of the ten Fluent ink aliases |
