# GalactiLog-Windows Implementation Roadmap

Greenfield build of the native Windows port. Source of truth for product behavior is
`docs/design-spec.md`; this document is the source of truth for execution state. Update the
Status fields as work proceeds so an interrupted run resumes from this doc alone.

Every task cites the spec sections it implements and the verification that proves it. Code-level
detail per task is not written here: a brief-writer produces it at phase start from the cited spec
sections.

## Resume State (2026-09-16)

Execution state lives in `docs/superpowers/HANDOFF.md` section 1 and `docs/superpowers/TRACKING.md`
section 6, which carry the per-phase commit hashes and test counts; the per-phase Status lines
below are the same record in place. Read HANDOFF first.

## Execution Protocol

- Coordinator reads this doc, picks the first phase not marked DONE, runs the phase process below,
  updates statuses, commits once, moves on.
- Status values: TODO, IN PROGRESS, BLOCKED (with reason), DONE (with commit hash).
- Per-phase process, in order:
  1. **Brief-writer** expands the phase's task table into per-task implementation briefs with
     code-level detail, reading the cited spec sections.
  2. **Implementers**, one per task, dispatched sequentially. Never in parallel: tasks within a
     phase share types and a single working tree.
  3. **Per-task reviewer** after each implementer, scoped to that task's diff.
  4. **Phase reviewer** over the whole phase diff once every task is DONE.
  5. **Fixer** resolves phase review findings.
  6. **Verification agent** runs the full `dotnet build` and `dotnet test` plus the phase's own
     Verification bar and records the result in the phase's Verification status line.
- One git commit per phase, not per task, made after verification passes. Commit message names the
  phase. Never add AI attribution to commits.
- The coordinator never pushes. Merge direction when the work is released is `snd` to `dev` to
  `main` (spec 17.3), by the user.
- Docs changes named in the phase's task table are part of the phase commit; nothing else in
  `docs/` is, except this file's status updates. Phases 10, 11 and 12 all shipped this way, and
  Phase 12's task 7 writes `DESIGN.md` at the repository root, which is outside `docs/` in any
  case.

## Global Constraints

- **Branch:** all work on `snd`.
- **.NET SDK location.** .NET 10 SDK 10.0.401 is installed under the user profile at
  `%LOCALAPPDATA%\Microsoft\dotnet` (the original machine; any 10.0.4xx install location works, see `docs/superpowers/HANDOFF.md` section 3), not in `Program Files`. `Program Files` has no
  SDK at all. Every agent shell must prefix PATH before invoking `dotnet`:
  - bash: `export PATH="/c/Users/<user>/AppData/Local/Microsoft/dotnet:$PATH"`
  - PowerShell: `$env:PATH="$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"`
  A bare `dotnet` call without this prefix fails or resolves to a runtime-only install.
- **File safety, spec 2.1, verbatim and not subject to interpretation:** the app NEVER deletes,
  moves, renames, or modifies any user file. Read-only on disk except the app's own data dir. No
  Recycle Bin feature. If a feature seems to need file mutation, stop and ask the user, then
  confirm again. Missing files on rescan delete DB rows and never touch disk.
- Every filesystem write in the solution goes through `GalactiLog.Core.Io.AppWriter`; every read of
  a user path goes through `GalactiLog.Core.Io.UserFiles`, which has no write members
  (spec 2.1.1). `FileSafetyTest` (spec 2.1.3) is the enforcement and must stay green from Phase 1
  onward.
- No WebView2, no CefGlue, no embedded browser, no HTML surface (spec 2.2).
- No authentication, no users, no sessions, no tokens (spec 2.3). Trust-boundary validation still
  applies: scan filter paths are confined (spec 10.2) and the header query builder binds every
  operand (spec 12.3).
- Target platform is Windows x64 only. No macOS or Linux CI target (spec 19.2).
- Stack versions are fixed by spec 3 where stated; see the Package versions table below.
- Deferred features (spec 19.1) are not to be implemented: PHD2, mosaics, analysis page, WBPP
  export, custom columns, NINA/Stellarium integrations, API keys, Prometheus, backup/restore,
  filename target inference, fpack FITS, light theme, any file deletion (red-light, added
  in Phase 12, is a dark variant and is not the deferred light theme). The sky viewer left this
  list in Phase 22, gated by `general.survey_downloads_enabled`, off by default (spec 5.8.1).
- Design-lessons rules apply: build the shared spine on the second occurrence of a pattern, not the
  sixth; enforce security structurally at the choke point, never by a per-call-site convention.
- Documentation and code comments follow the house style: no em dashes, no emojis, no fluff.
- A view-model that holds a brush holds an `ImmutableSolidColorBrush`, never a `SolidColorBrush`
  (spec 14.5). `SolidColorBrush`'s constructor calls `VerifyAccess` once a dispatcher exists, so
  one constructed on a background thread throws, and a view-model builds its brushes wherever its
  data arrives.
- A test that asserts something ran off the UI thread awaits the work; it never blocks on it with
  `Wait()` or `GetAwaiter().GetResult()`. Avalonia's headless UI thread is itself a thread-pool
  thread, so a blocking wait from it on a `Task.Run` queued from it lets the pool inline that work
  onto the very thread the test claims it is not on. The failure needs a saturated pool to appear,
  which means it reproduces in full runs and passes filtered. `xUnit1031` catches the blocking call
  in a test method itself; a blocking wait inside a test harness helper is not caught and must be
  avoided by hand in anything that carries an off-thread assertion.

## Package versions

Pinned centrally in `Directory.Packages.props` with `ManagePackageVersionsCentrally`. Phase 1
Task 1 resolves each "latest stable" entry with `dotnet package search <id> --exact-match` (or the
NuGet API) and records the pinned version in this table's Notes column at that time.

| Package | Version | Notes |
| --- | --- | --- |
| `Avalonia` | 11.3.x | Fixed by spec 3. |
| `Avalonia.Desktop` | 11.3.x | Matches `Avalonia`. |
| `Avalonia.Themes.Fluent` | 11.3.x | Base control theme. The three theme dictionaries (spec 14) are token dictionaries layered over it, not a replacement, with `Theme/Controls.axaml` carrying the shared control vocabulary. |
| `Avalonia.Headless.XUnit` | 11.3.x | Fixed by spec 3. Test projects only. |
| `CommunityToolkit.Mvvm` | 8.4.x | Fixed by spec 3. |
| `Microsoft.EntityFrameworkCore.Sqlite` | 10.x | Fixed by spec 3. |
| `Microsoft.EntityFrameworkCore.Design` | 10.x | Data project only, for migrations. |
| `Microsoft.Data.Sqlite` | 10.x | Transitive via EF Core; declared explicitly for the raw `SqliteConnection` header query builder (spec 12.3). |
| `LiveChartsCore.SkiaSharpView.Avalonia` | 2.0.0-rc5.x | Fixed by spec 3. Prerelease; `Directory.Packages.props` must allow it. |
| `SkiaSharp` | 3.x | Transitive via Avalonia; declared explicitly because Core uses it directly (spec 4.1). |
| `K4os.Compression.LZ4` | 1.3.x | Fixed by spec 3. XISF lz4/lz4hc codecs. |
| `Serilog` | 4.x | Fixed by spec 3. |
| `Serilog.Extensions.Hosting` | latest stable at scaffold time, pinned in Directory.Packages.props | Generic host integration. Pinned to 10.0.0. |
| `Serilog.Sinks.File` | 6.x | Fixed by spec 3. |
| `Serilog.Sinks.Console` | latest stable at scaffold time, pinned in Directory.Packages.props | CLI mode only (spec 16.1). Pinned to 6.1.1. |
| `Velopack` | latest stable at scaffold time, pinned in Directory.Packages.props | Spec 3 says "latest stable". Pinned to 1.2.0. |
| `Microsoft.Extensions.Hosting` | latest stable at scaffold time, pinned in Directory.Packages.props | Generic host, spec 3. Pinned to 10.0.12. |
| `Microsoft.Extensions.DependencyInjection` | latest stable at scaffold time, pinned in Directory.Packages.props | Transitive via Hosting; declared for clarity. Pinned to 10.0.12. |
| `Microsoft.Extensions.Logging.Abstractions` | latest stable at scaffold time, pinned in Directory.Packages.props | Core's only logging reference (spec 4.1). Pinned to 10.0.12. |
| `xunit` | 2.9.x | Fixed by spec 3. |
| `xunit.runner.visualstudio` | latest stable at scaffold time, pinned in Directory.Packages.props | Test discovery. Pinned to 2.8.2 (the 2.x line matching xunit 2.9.x; the 3.x/4.x runner line targets xunit.v3). |
| `Microsoft.NET.Test.Sdk` | latest stable at scaffold time, pinned in Directory.Packages.props | Test host. Pinned to 18.10.0. |
| `System.CommandLine` | **not taken** | Spec 3 rejects it explicitly: four commands with positional arguments do not justify a dependency; a switch on `args[0]` covers it (spec 15). Do not add it or any equivalent. |
| `Avalonia.Controls.ColorPicker` | 11.3.21 | Added Phase 13 (P13 R2b). Must match the `Avalonia` version exactly; ships its own Fluent control themes as assembly resources, so `App.axaml` carries one `StyleInclude` for them after `Theme/Controls.axaml`. Confirmed against `Directory.Packages.props`. |

Atkinson Hyperlegible Next and Atkinson Hyperlegible Mono are embedded font files under
`src/GalactiLog.App/Assets/Fonts`, not packages, so they take no row in this table. Phase 12
removed the `Avalonia.Fonts.Inter` row with the package pin and the `WithInterFont()` call.

Deliberately not taken, per spec 3: any FITS or XISF library, any HTTP wrapper beyond
`HttpClient`, any DI container beyond `Microsoft.Extensions.DependencyInjection`, any astronomy
library.

---

## Phase 1: Scaffold

Goal: A buildable, testable solution with the file-safety choke point, the database schema, the
host, the theme, the CLI dispatch, and CI files in place. Nothing user-visible works yet; the build
and the test suite are green and every later phase has somewhere to put its code.

Status: DONE (one commit on snd; see progress.md)

| Task | Status | Notes |
|------|--------|-------|
| Solution and projects per spec 4: `GalactiLog.sln`, `src/GalactiLog.Core`, `src/GalactiLog.Data`, `src/GalactiLog.App`, `src/GalactiLog.Cli`, `tests/GalactiLog.Core.Tests`, `tests/GalactiLog.Data.Tests`, `tests/GalactiLog.App.Tests`, `tests/GalactiLog.Cli.Tests`, with the exact reference directions in 4.1 to 4.5 (Core references no Avalonia and no EF Core). `Directory.Build.props` (net10.0, C# 14, nullable enable, warnings as errors, deterministic) and `Directory.Packages.props` with every row of the Package versions table resolved and pinned. Verify: `dotnet build` succeeds; `dotnet package search` output recorded in the Package versions Notes column; a Core.Tests assertion that `GalactiLog.Core` has no Avalonia or EF Core assembly reference. | DONE | |
| `GalactiLog.Core.Io.UserFiles` and `GalactiLog.Core.Io.AppWriter` per spec 2.1.1: three allowed roots, `UnauthorizedPathException`, `ResolveAppDataPath`, `BeginExport` scoped writer, delete confined to app data and thumbnail cache, `UserFiles` opening `FileAccess.Read` with `FileShare.ReadWrite` and carrying no write member. Plus `GalactiLog.Core.Tests.FileSafetyTest` per spec 2.1.3 with the full forbidden-call list and the single allowlisted file. Verify: `GalactiLog.Core.Tests` AppWriter cases from spec 18.1 (app data accepted, thumbnail cache accepted, scan root refused, outside-all refused, `..` traversal refused, scoped export writer refuses other paths and refuses everything after disposal) plus `FileSafetyTest` green. | DONE | |
| EF Core layer: `GalactiLogContext`, `GalactiLogContextFactory`, `DatabasePaths`, one entity class per table in spec 5.2 to 5.13 with every column, type, nullability, index, unique constraint, and foreign-key delete behavior as specified; the connection interceptor applying the five pragmas in spec 5.1; `NoTracking` default for read contexts; the initial EF Core migration. Verify: `GalactiLog.Data.Tests` applies the migration on an empty file, asserts the resulting schema table and index set, asserts re-run is idempotent, and asserts WAL, `busy_timeout`, and `foreign_keys` are set on a fresh connection (spec 18.1 Migrations and Pragmas rows). | DONE | |
| Settings layer: `GalactiLog.Core.Settings` records for `general`, `display`, `graph`, `filters`, `equipment`, `dismissed_suggestions` covering every key in spec 5.8.1 to 5.8.4 with its default; unknown-key preservation on write; `SettingsRepository` and `SettingsStore` with validate-before-persist, the single row id 1, and the 30 second alias cache TTL backstop with explicit invalidation hooks (spec 12.7). Verify: `GalactiLog.Data.Tests` round-trips every settings section, asserts a missing key reads its documented default, asserts an unrecognized key survives a write cycle (spec 18.1 Settings store row). | DONE | |
| Host: `GalactiLog.App.AppHost` wiring `Microsoft.Extensions.Hosting`, DI registrations for the repositories and services named in spec 4.2 and 4.3, and Serilog per spec 16.1 (rolling file sink with the exact path template from `AppWriter.ResolveAppDataPath("logs/galactilog-.log")`, format string, daily roll, 14 retained files, 32 MB size limit, 1 second flush, `LoggingLevelSwitch` bound to `general.log_level`, 500-entry in-memory warning ring). Verify: `GalactiLog.App.Tests` asserts the host builds, resolves every registered service, that the Serilog file path resolves under app data, and that changing `general.log_level` moves the level switch without a restart. | DONE | |
| Avalonia shell: `Program`, `App`, an empty `MainWindow` with the navigation rail regions and status bar placeholder, `Theme/Themes/GlassVoid.axaml` carrying all 37 tokens from spec 14.1 as `SolidColorBrush` plus parallel `Color` resources, `Theme/Scales.axaml` with the radius, shadow, typography ratio, and motion values from spec 14.4, `BrushPageBackground` per 14.2, and `ThemeManager` with `Available`. Verify: `GalactiLog.App.Tests` headless test constructs `MainWindow` and produces a non-zero layout; a resource test asserts all 37 keys plus every scale key resolve from the merged dictionary (spec 18.3 Theme resolution row). | DONE | |
| CLI dispatch and CI files: `GalactiLog.Cli` entry point invoked from `Program.Main` when `args.Length > 0` and `args[0]` is a known verb, with `--json` and `--quiet` global options, usage on stderr, and every exit code in spec 15 wired (verbs themselves stubbed to a not-implemented path returning 70 until their own phases). `VelopackApp.Build().Run()` as the first statement in `Program.Main`, before verb dispatch and before Serilog (spec 17.1). The three workflow files from spec 17.5 (`build-test.yml`, `release.yml`, `branch-merge-policy.yml`) with their exact triggers, permissions, concurrency groups, and step order; files only, not run. Verify: `GalactiLog.Cli` unit tests assert verb routing and exit codes 0, 2, and 70; a shell check parses each workflow YAML; `dotnet build` green. | DONE | TryRun takes Func<IServiceProvider>; host built lazily; AttachConsole guarded by GetFileType; tests/GalactiLog.Cli.Tests added. |

Verification: from a clean clone on `snd`, with PATH prefixed per Global Constraints,
`dotnet restore`, `dotnet build -c Release` and `dotnet test -c Release` all exit 0 with zero
failures. `FileSafetyTest` is among the passing tests. `galactilog.exe --help` prints usage and
exits 2; `galactilog.exe` with no arguments opens an empty themed window.

Verification status: PASSED 2026-09-09. Clean-copy restore, build (0 warnings), test 56/56 incl FileSafetyTest; GalactiLog.exe --help exit 2 with DB untouched; stub verb exit 70; piped stdout and redirected stderr both work; three workflow YAMLs parse; hygiene grep clean. Known accepted limits: hard links undetectable by AppWriter reparse walk; junction test passes silently if mklink /J fails; nested settings records lack JsonExtensionData.

Resume notes: check whether `GalactiLog.sln` exists; whether `src/GalactiLog.Core/Io/AppWriter.cs`
exists; whether `src/GalactiLog.Data/Migrations` holds a migration; whether
`src/GalactiLog.App/Theme/Themes/GlassVoid.axaml` exists; whether `.github/workflows` holds three
files. Per task, the named test class existing and passing is the completion signal.

---

## Phase 2: File readers and metadata extraction

Goal: Any FITS or XISF file on disk can be turned into a metadata record with provenance and a raw
header document, and two CLI verbs prove it against real files.

Status: DONE (one commit on snd; see progress.md)

| Task | Status | Notes |
|------|--------|-------|
| Fixture builders per spec 18.2: `GalactiLog.Core.Tests.Fixtures.FitsBuilder` (card padding, `END`, block padding, endianness, `BZERO`/`BSCALE`, deliberate malformations), `XisfBuilder` (monolithic layout, optional zlib or lz4, optional byte shuffling), `CsvBuilder` (`ImageMetaData.csv`, `WeatherData.csv`, gaps, `NaN` cells, zero HFR). No user file is ever committed. Verify: self-tests asserting a `FitsBuilder` output round-trips through the reader once Task 2 lands, and that every builder writes into a `MemoryStream` with no filesystem write. | DONE | |
| FITS header reader: `FitsHeaderReader`, `FitsCard` per spec 6.1.1 to 6.1.3 and the rejection rules in 6.1.5. Covers blank-name exclusion, `COMMENT` and `HISTORY` as JSON arrays, `HIERARCH` including the no-`=` tolerance, quoted strings with doubled quotes, `CONTINUE` concatenation, `D`-exponent floats, duplicate keys last-wins, non-finite doubles as strings, and the compressed-FITS skip. Verify: `GalactiLog.Core.Tests` FITS header parsing row of spec 18.1, one assertion per card type and one per rejection rule. | DONE | |
| FITS pixel reader: `FitsImageReader` per spec 6.1.4. Each supported `BITPIX`, big-endian conversion, `BZERO`/`BSCALE` including the unsigned-16 case, `BLANK` as 0, `[height, width]` row-major buffers, the `NAXIS3 = 3` planar-colour branch, and the header-only degradation cases in 6.1.5. Verify: `GalactiLog.Core.Tests` FITS pixel reading row of spec 18.1. | DONE | |
| XISF header reader: `XisfHeaderReader` per spec 6.2.1 to 6.2.3 and the header-level rejection rules in 6.2.6. Signature, header length, namespace-qualified `Image` lookup, every read attribute, `FITSKeyword` unquoting, `Property`, `ColorFilterArray`, and the four `location` forms. Verify: `GalactiLog.Core.Tests` XISF row of spec 18.1, signature and location cases. | DONE | |
| XISF data block and pixel reader: `XisfDataBlock`, `XisfImageReader` per spec 6.2.4, 6.2.5, 6.2.7, 6.2.8. Every sample format including the three skipped ones, zlib, lz4, lz4hc, all three `+sh` variants, the unshuffle transform, the declared-size mismatch rejection, planar channel layout, and the top-down orientation that must not be flipped. Verify: `GalactiLog.Core.Tests` XISF row of spec 18.1, including the unshuffle transform against a known vector. | DONE | |
| `MetadataExtractor`, `ExtractedMetadata`, `Provenance`, `Units`: the full field table in spec 7.1 with left-to-right keyword priority, the HFR zero fall-through to the filename regex, `camera_gain` strict integer parse, the plate scale formula and its null cases, eccentricity and ellipticity conversion per 7.2, `DATE-OBS` parsing per 7.1.2 including the offsetless-is-UTC rule, the XISF property fill-the-gaps map, and the `provenance` JSON written by the same code path that resolves each value per 7.3. `median_fwhm` and `fwhm` stay strictly separate per 7.1.1. Verify: `GalactiLog.Core.Tests` Metadata extraction row of spec 18.1, plus one assertion that no code path assigns `median_fwhm` to a display or aggregate field. | DONE | |
| `NinaCsvReader` and CSV backfill per spec 7.4: both files looked up in the frame's parent directory, BOM tolerance, the `FilePath` last-segment key split on both separators, the `ExposureStartUTC` weather join, blank/whitespace/`NaN` to null, `HFR == 0` to null, the merge rule that a null CSV value never erases a header value, the eccentricity and guiding provenance stamps, and the `ConcurrentDictionary` mtime-keyed cache. Plus calibration classification per spec 7.5. Verify: `GalactiLog.Core.Tests` CSV merge row of spec 18.1, specifically the "blank CSV cell does not erase a header value" case. | DONE | Read/extract/backfill pipeline lives in Core as FrameReader.TryRead (shared with Phase 4); factory not invoked for inspect or dump-headers. |
| CLI verbs `inspect` and `dump-headers` per spec 15: text and `--json` output shapes, exit codes 0 and 3, results on stdout and errors on stderr. Verify: `galactilog inspect <generated fixture>.fits` prints one `field = value  (provenance)` line per stored field and exits 0; `galactilog dump-headers <fixture>.fits --json` emits the `raw_headers` object parseable by `jq`; a nonexistent path exits 3. | DONE | |

Verification: `dotnet build -c Release` and `dotnet test -c Release` green. Additionally the
verification agent generates one FITS and one XISF fixture to a temp directory and runs
`galactilog inspect` and `galactilog dump-headers` on each, confirming the four documented output
shapes and the exit codes.

Verification status: PASSED 2026-09-09. Clean-copy restore, build (0 warnings), test 299/299 incl FileSafetyTest; inspect and dump-headers on generated FITS and XISF fixtures print the documented shapes, --json parses, provenance present, nonexistent path exit 3, --help exit 2, app DB untouched by CLI reads; hygiene grep clean. Known accepted limits: non-seekable streams rejected as data; MaxAxisLength duplicated between FITS and XISF readers with cross-comment; pixel readers exercised by unit tests only until Phase 8.

Resume notes: check for `src/GalactiLog.Core/Fits/FitsHeaderReader.cs`,
`src/GalactiLog.Core/Xisf/XisfHeaderReader.cs`, `src/GalactiLog.Core/Metadata/MetadataExtractor.cs`,
`tests/GalactiLog.Core.Tests/Fixtures/FitsBuilder.cs`. Per task, the named type existing with its
test class passing is the completion signal.

---

## Phase 3: Catalogs and target resolution

Goal: An `OBJECT` string resolves to a target identity offline first, then online, with caching,
and `galactilog resolve <name>` proves the whole pipeline without any UI.

Status: DONE (one commit on snd; see progress.md)

| Task | Status | Notes |
|------|--------|-------|
| Bundle the seven catalog data files verbatim from `<web reference clone>\backend\data\catalogs\` (`openngc.csv`, `names.dat`, `caldwell.csv`, `abell.csv`, `arp.csv`, `sac.csv`, `herschel400.csv`) into a `Catalogs` content directory shipped alongside the binaries, read through `UserFiles`. `StaticCatalogLoader` loads them into `openngc_catalog` and `static_catalog_entries` on first run, guarded by `general.catalogs_loaded_version` per spec 9.3 and 17.2, behind a progress dialog. `OpenNgcCatalog` parses RA and Dec per 9.3. Verify: `GalactiLog.Data.Tests` asserts the loader is idempotent, that `openngc_catalog` holds the expected row count for the shipped file, that `static_catalog_entries` holds five distinct `catalog_name` values, and that a second run with the version already set performs no writes. | DONE | |
| `NameNormalizer` (spec 9.1: `Normalize`, `NormalizeDisplay`, `NormalizeCatalogId`, `StripPanel`, `Compact`, `NormalizeNgcName` from 9.3), `CatalogPriority` (spec 9.4.1, all 25 ordered patterns and `ExtractCatalogId`), and `Trigram` (spec 9.7 `similarity()` only, with the documented padding and 3-gram set rule). Verify: `GalactiLog.Core.Tests` Name normalization row of spec 18.1 across all 25 patterns, and the Trigram row asserting against captured PostgreSQL `pg_trgm` values hardcoded in the test. | DONE | |
| `StellariumNames` parsing `names.dat` per spec 9.3.2 (fixed-width columns, the `_("...")` regex, skip rules, lowercased key, first occurrence wins) with the full `PREFIX_MAP`, and `CommonNameOverrides` carrying spec 9.4.2's roughly 130 entries copied verbatim including the full Caldwell 1 to 109 mapping. Verify: `GalactiLog.Core.Tests` asserts a sample of parsed records, that a short or commentless line is skipped, that a duplicate name does not overwrite, and that the override map entry count matches the web source's dictionary length. | DONE | |
| Offline catalog lookup per spec 9.3: the four-step lookup order (direct designation, common name via overrides then Stellarium, descriptive-suffix strip, Sharpless and LBN rewrites), and `enrich_target_from_openngc` behavior (fill only where null, `common_name` first semicolon entry rebuilding `primary_name`, skip when `user_defined`). Verify: `GalactiLog.Data.Tests` asserts `M 31`, `NGC0031`, `Caldwell 14`, and a descriptive-suffix name each resolve to the expected OpenNGC row without any network access; that `Sh2 174` and `Horsehead Nebula` each return null from the offline lookup and RewriteForOnlineQuery yields 'SH 2-174' and 'Barnard 33' respectively; and that enrichment leaves a non-null field untouched. | DONE | |
| `SimbadClient` (spec 9.4.3: both calls, the script format, sanitization, `::error::` and `::data::` parsing, the doubled-quote ADQL escape which is mandatory), `SesameClient` (spec 9.4.4: the `NV` resolver URL, XML iteration, first element carrying both coordinates), and `AliasCurator` (spec 9.4.5: `CurateAliases`, `ExtractCommonName`, `BuildPrimaryName` for all four input combinations). Both clients take an `HttpMessageHandler` and use a 15 second timeout. Verify: `GalactiLog.Core.Tests` supply a stub handler returning captured response bodies stored as plain text resources; assert the ADQL escape against a `main_id` containing a single quote; no test touches the network. | DONE | |
| `CatalogCacheRepository` and cache semantics per spec 9.6: the three `source` values kept distinct, positive rows cached forever, negative rows with a 7 day TTL treated as a miss when expired, the 3-attempt retry with 1 then 2 second backoff, `NonTransientException` on any 4xx other than 429 with no retry and no cache write, the SIMBAD dual-key check-then-write, and `skipSimbad` cache-only mode. Verify: `GalactiLog.Data.Tests` Catalog cache row of spec 18.1: positive hit, negative hit, negative expiry, backoff not triggered on a 4xx, the dual-key write. | DONE | |
| `TargetResolver` per spec 9.2 (the eight-step pipeline with step 1 reading only `resolver` rows), `MatchTargetByIdentity` and `FindTargetByName` per 9.5 including the alias append on match and the merged-away exclusion, target creation per 9.7 steps 1 to 5 with the unique-violation retry deliberately absent, and catalog membership plus `ObjectTypeCategories.Categorize` per 9.8. Verify: `GalactiLog.Data.Tests` asserts an offline-resolvable name never calls the stub HTTP handler; that a second resolution of the same name creates no second target; that a name resolving to an identity an existing target carries links rather than inserts; that a unique violation propagates rather than being swallowed; that `Categorize` maps every SIMBAD code in the 9.8 table, every OpenNGC code, and `Other`; `Unresolved` is a frame state, never a Categorize output. | DONE | |
| CLI verb `resolve` per spec 15: prints the resolved identity and the resolving stage (`cache`, `offline`, `simbad`, `sesame`, `unresolved`), never creates a target, does write resolver cache rows, exit code 0 on a hit and 1 on a miss. Verify: `galactilog resolve "M 31"` exits 0 and names stage `offline` with no network; `galactilog resolve "zzzz not a real object" --json` emits an object with `source` and exits 1. | DONE | EnrichFromSac included per ruling Q8. |

Verification: `dotnet build -c Release` and `dotnet test -c Release` green. Additionally the
verification agent runs `galactilog resolve "M 31"` which resolves offline with exit 0; runs
`galactilog resolve "Horsehead Nebula"` and `galactilog resolve "zzzz"` with the network
unreachable, both exiting 1 within 30 seconds with connections refused (a black-holed network can cost up to 96 seconds per name; Phase 4 adds a per-scan online circuit breaker) and writing
no negative cache row for the network failure.

Verification status: PASSED 2026-09-09. Clean-copy restore, build (0 warnings), test 579/579 incl FileSafetyTest and both migrations; catalog files hash-identical to the web source; resolve M 31 and M 57 offline exit 0 (M 57 carries NGC 6720 alias); Horsehead and zzzz with connections refused exit 1 in 19 s and 1 s, no resolver negative written for the network failure; targets untouched by the dry-run verb; 13969 OpenNGC and 4822 static rows, 5 catalogs; hygiene grep clean. Known accepted limits: black-holed network up to 96 s per name until Phase 4's circuit breaker; Abell has no NGC crosswalk; no app-data root override for the exe yet (Phase 4 Task 1 adds GALACTILOG_APPDATA).

Resume notes: check for `src/GalactiLog.Core/Catalogs/StaticCatalogLoader.cs`,
`src/GalactiLog.Core/Targets/TargetResolver.cs`, `src/GalactiLog.Core/Targets/CommonNameOverrides.cs`,
and a `Catalogs` content directory holding seven files. Check whether `general.catalogs_loaded_version`
is written by any code path.

---

## Phase 4: Scan pipeline

Goal: A configured scan root walks end to end, producing image rows, targets, sessions, activity
events, and a `scan_runs` record, driven from the CLI and from an in-process coordinator, with
progress and cancellation.

Status: DONE (uncommitted on snd; commit hash recorded in docs/superpowers/progress.md at close)

| Task | Status | Notes |
|------|--------|-------|
| `ScanFilterConfig`, `NameRule`, and roots per spec 10.1 and 10.2: independent roots each with its own confinement boundary, symlinks followed but out-of-root results skipped, the four supported extensions, include-path nesting deduplication, the six-step file evaluation order, directory pruning per `should_walk_dir`, the three match types with a 250 ms regex timeout, and the deliberate divergence that `enabled` is tested on include rules too. Plus the test-a-path tool with its four verdicts and matched rule ids. Verify: `GalactiLog.Core.Tests` Scan filters row of spec 18.1: every verdict, glob against substring against regex, folder rules matching ancestor segments, include narrowing per target type, nested include deduplication, path confinement rejection. | DONE | Add an app-data root override for the exe: environment variable GALACTILOG_APPDATA honored by AppHost.Build before the default %LOCALAPPDATA%\GalactiLog, so CLI verification and CI can use a fresh root. Spec 17.2 to be amended.; Validate wired into SettingsStore.SaveGeneral; GALACTILOG_APPDATA added; regex timeout logged via NameRuleMatcher.OnTimeout installed by ScanCoordinator |
| Session derivation: `SessionDate.Compute` per spec 8.2, `ResolveLongitude` per 8.3 with the four-step order including the timezone-offset approximation, and the once-per-scan-run fallback warning per 8.3 step 4 and 16.1. Verify: `GalactiLog.Core.Tests` Session date row of spec 18.1, including that the fallback warning is emitted once for a run ingesting many frames, not once per frame. | DONE | SessionDate + OnceGate in Core |
| `FileWalker` and classification per spec 10.3 steps 1 and 2: depth-first enumeration with `MatchCasing.CaseInsensitive`, directory pruning before descent, inaccessible entries logged at warning and skipped without failing the scan, progress every 50 discovered files, and the new/changed/unchanged decision against the known set with the 1.0 second mtime tolerance. Verify: `GalactiLog.Core.Tests` (`FileWalkerTests`) seeds a known set and asserts each classification outcome, and asserts a permission-denied directory is skipped rather than throwing. | DONE | Junction cycle guard, depth cap 64; classification tests live in Core.Tests |
| `GalactiLog.Data.Ingest.ScanWriter` and the channel pipeline per spec 10.6 and 5.1: walk task feeding a `Channel<FileInfo>`, N reader tasks (`ProcessorCount` clamped to 2 to 8) performing zero database access and producing `ParsedRecord` into a capacity-256 channel, one writer task performing image upsert, target resolution, target create, membership rows, and activity events sequentially, `SaveChanges` per 200 records and once at the end, and the shared CSV cache. Verify: `GalactiLog.Data.Tests` asserts that ingesting 500 synthetic records through the pipeline produces exactly 500 rows, that no two targets are created for the same identity, and that a deliberately slow resolver stub causes backpressure rather than unbounded memory growth. | DONE | Add the per-scan online circuit breaker: after the first transient network failure in a scan, skip SIMBAD and Sesame for the rest of that scan and log once; pass the scan's CancellationToken into Resolve.; ParsedRecord in Core.Scanning; ScanPipeline owns channels; per-scan online circuit breaker; resolution_failed gated on NegativeCached |
| `ScanCoordinator` per spec 10, 10.4, 10.5 and 5.13: one scan at a time process-wide with the pending flag, the immutable `ScanProgress` record and the closed seven-value task vocabulary with `Percent` derived at read time, the counter set, the `ProgressChanged` event on the Avalonia dispatcher throttled to 10 per second, one `CancellationTokenSource` per run checked at the three documented points, cooperative cancellation leaving the database consistent, the 5 second shutdown drain, and `scan_runs` rows written for every trigger and terminal state. Verify: `GalactiLog.Data.Tests` (`ScanCoordinatorTests`; ruling Q4 puts `ScanCoordinator` in `GalactiLog.Data.Ingest`) asserts the task vocabulary is exactly the seven names, that `prune_orphans` and `prune_activity` are distinct, that a cancelled run records `state = cancelled` and skips orphan pruning, and that a trigger during a run sets the pending flag and starts exactly one more scan. | DONE | Lives in GalactiLog.Data.Ingest, no interface (ruling Q4); WaitForIdleAsync(TimeSpan); Cancel clears the pending flag; MarkInterrupted at startup |
| Orphan pruning and activity per spec 10.3 steps 4 and 5, 10.9, and 5.12: rows deleted only for paths under a walked root and absent from the discovered set, rows under a removed root left alone, the zero-discovery guard skipping pruning with a warning, `removed` recorded, `ActivityRepository`, every event row in the 10.9 table with its category, severity, and details payload, `parent_id` set to the `scan_started` event for every scan sub-event, and retention pruning to `general.activity_retention_days` on start and after each scan. Deleting a row never touches the file. Verify: `GalactiLog.Data.Tests` Orphan pruning row of spec 18.1 plus an assertion that no test in the suite causes a file delete under a scan root (`FileSafetyTest` covers the static side). | DONE | Prunes over walked (effective) roots only; exclude_paths subtrees are pruned by design (spec 10.3); ActivityRepository.Emit spine; retention floor 1 day |
| `WatcherService` per spec 10.7 (one `FileSystemWatcher` per root, 64 KB buffer, the three event kinds collected into a path set, the debounce timer, targeted ingest rather than a full walk, the same filter config, the two-check size stability gate, `Error` events triggering a full scan) and `ScanScheduler` per spec 10.8 (`PeriodicTimer`, interval reset after any scan from any trigger, skipped while a scan runs). Verify: `GalactiLog.App.Tests` drives a fake clock and a fake watcher source asserting debounce coalescing, that a file still growing is not ingested until stable, that a buffer-overflow error escalates to a full scan, and that a manual scan pushes the next scheduled run out by a full interval. | DONE | Delegate-bound to ScanCoordinator in AppHost; stability gate opens via UserFiles.OpenRead; shutdown drain in App.axaml.cs DrainForShutdown |
| CLI verb `scan` per spec 15 plus the end-to-end test: optional path arguments defaulting to every configured root, progress lines `<task> <step>/<total> <message>`, `--json` emitting the `scan_runs` record, Ctrl+C cancelling cooperatively with exit code 5, and exit code 0 when nothing new was found. Verify: an end-to-end `GalactiLog.Data.Tests` case generates a synthetic folder of FITS and XISF fixtures plus `ImageMetaData.csv`, runs a scan, and asserts the exact expected `images`, `targets`, `target_catalog_memberships`, `activity_events`, and `scan_runs` row counts and key field values. | DONE | Exit codes 2 (filter validation) / 4 (database) / 5 (cancel) / 70; real Ctrl+C run recorded; images.file_path COLLATE NOCASE via migration 0003 |

Verification: `dotnet build -c Release` and `dotnet test -c Release` green. Additionally the
verification agent creates a temp folder of generated fixtures, sets `GALACTILOG_APPDATA` to a
fresh absolute root, and runs `galactilog scan <fixture folder> --json`, passing the fixture
folder as the positional root (no settings writer exists yet, so `general.scan_roots` cannot be
set from outside the application). The fixtures must contain no malformed file: a rejected file
writes no `images` row either, so it would inflate the second run's `new_files` above
`skipped_calibration`. Confirm the emitted `scan_runs` object reports the expected `discovered`,
`new_files`, `completed`, and `skipped_calibration` counts with `state = complete`; then re-run it
and confirm the second run writes no new `images` rows, reports `new_files` equal to
`skipped_calibration` (calibration files write no row, so the path-keyed delta re-classifies them
as new every scan), and exits 0.

Verification status: PASSED 2026-09-09. Clean-copy build (0 warnings), test 880/880 twice (no flake), FileSafetyTest green, three migrations applied; exe smoke on 350 generated frames: first run complete/350/350/50, second run new_files 50 == skipped_calibration with images unchanged at 300, exit 0 both; fixture folder byte-identical before and after (351 files, matching md5 listings); real Ctrl+C over 4000 frames exit 5, state cancelled, 3021 rows kept, files intact; hygiene grep clean. Report: docs/superpowers/work/phase4/verification-report.md.

Resume notes: check for `src/GalactiLog.Data/Ingest/ScanWriter.cs`,
`src/GalactiLog.Data/Ingest/ScanCoordinator.cs`, `src/GalactiLog.App/Services/WatcherService.cs`,
`src/GalactiLog.Core/Scanning/ScanFilterConfig.cs`. Check whether `galactilog scan` exists as a
routed verb rather than the Phase 1 stub.

---

## Phase 5: Dashboard

Goal: The main window shows the real library: filter panel, target list, search, paging, and
persisted column choices, backed by the listing query.

Status: DONE (uncommitted on snd; commit hash recorded in docs/superpowers/progress.md at close)

| Task | Status | Notes |
|------|--------|-------|
| `GalactiLog.Core.Aliases.AliasMap` per spec 5.8.4 and 12.2: canonical name to raw alias expansion for filters, cameras, and telescopes, the forward expansion used for query criteria (`expand_canonical`), a raw name absent from every alias list being its own canonical name, and cache invalidation on any `filters` or `equipment` settings write. Verify: `GalactiLog.Core.Tests` asserts selecting canonical `OIII` expands to match stored `Oiii` and `O3`, and `GalactiLog.Data.Tests` asserts the cache is invalidated on a settings write. | DONE | Core.Aliases.AliasMap + Data.AliasMapCache; case-insensitive dedup, blank aliases skipped |
| `TargetListingQuery` per spec 12.2: the six-step query shape, the `LIGHT` and non-merged filter, the group key with the empty-and-missing `OBJECT` collapse, the per-group aggregates, the page slice and overall aggregates in one round trip, the second pass over the page's groups only, and every sort key. Metric range semantics per 12.2.1 using the documented `HAVING min/max` form, with only the bounds the user set contributing clauses. Verify: `GalactiLog.Data.Tests` Listing query row of spec 18.1, specifically that a group holding one in-range and one out-of-range frame is excluded, that an all-null group is included, and that the aggregates describe the same set as the page. | DONE | Data.Queries.TargetListingQuery + DashboardFacetsQuery; LibrarySeeder (spec 18.2) in Data.Tests; page 3.3 ms on the seeded library |
| FITS header query builder per spec 12.3: the `^[A-Za-z0-9_-]{1,20}$` key gate dropping rather than escaping, `json_extract(raw_headers, '$.' || ?)` with the key bound, the value always bound, numeric operators requiring a parseable double or dropping the clause, and `contains` escaping `\`, `%`, `_` with `ESCAPE '\'`. Verify: `GalactiLog.Data.Tests` asserts an injection-shaped key is dropped, that a non-numeric value on `>` drops the clause, that a `%` in a `contains` value matches literally, and that the generated SQL text contains no user-supplied substring. | DONE | HeaderQueryBuilder + DistinctHeaderKeysQuery; keys validated once at the gate, every value parameterised; json_valid guard |
| `MainWindow` shell per spec 12: left navigation rail (Dashboard, Statistics, Activity, Diagnostics, Settings), content region honoring `general.content_width`, and the persistent status bar bound to `ScanCoordinator.ProgressChanged` showing state, message, percent, a cancel button while a scan runs, and the update indicator slot. Verify: `GalactiLog.App.Tests` headless test asserts each rail item navigates, that the status bar renders a progress envelope pushed from a fake coordinator, and that the cancel button's enablement follows scan state. | DONE | Split into Task 4 (shell, nav rail, RequestedThemeVariant=Dark, content widths 1200/1600/unbounded) and Task 5 (ScanStatusService marshals ScanCoordinator events to the dispatcher; StatusBarViewModel; ScanCoordinatorTestFactory) |
| Dashboard filter panel per spec 12.2: the summary strip with its distinct Groups label, and the seven collapsible sections (Search, Object Type with the sixteen toggle pills from 9.8, Date Range with presets, Filters tinted per configured colour, Equipment combos, Metrics Quality with the exact groups, keys, and steps from 12.2, FITS Header Query rows), each with an active marker, debounced inputs, a Reset Filters button, and session-scoped persistence of panel and filter state. Verify: `GalactiLog.App.Tests` asserts filter state maps to the correct `TargetListingQuery` criteria for each section, that debounce coalesces rapid input, and that Reset clears every section. | DONE | FilterPanelViewModel with background Reload and PendingReload; 250 ms shared debounce with injected delay; per-control clear; refreshes after ScanFinished |
| Target list per spec 12.2: the seven columns from `TargetRow`, the Sessions expander, row click navigation, six sort keys ascending and descending, paging at `general.default_page_size`, and column visibility persisting to `display.columns.dashboard` as an ordered key list with the documented default. Verify: `GalactiLog.App.Tests` asserts sort and page state produce the expected criteria, that hiding a column writes the ordered list, and that a table id absent from the map falls back to the default list. | DONE | TargetListViewModel; ItemsControl plus header row (no DataGrid); columns persisted via DisplaySettings.ColumnsFor with background serialised PendingPersist; page clamped after shrink |
| Dashboard search and empty states: fuzzy alias-aware search per spec 9.7 using `similarity()` at the 0.4 threshold against `primary_name`, `catalog_id`, `common_name`, and each alias separately taking the maximum, never against a concatenated blob; unresolved `OBJECT` strings appearing with frame counts and selecting as `obj:<name>`; and every dashboard empty and error state from 12.10 including the unreachable-root warning banner. Verify: `GalactiLog.Data.Tests` asserts search ranking over the seeded library; `GalactiLog.App.Tests` asserts surfacing/pinning in the dashboard, that selecting a result pins `target_id`, that the two distinct dashboard empty states render for the no-frames and no-match cases, and that an unreachable root renders a banner naming the path rather than an empty list. | DONE | TargetSearchQuery (Trigram.Similarity per spec 9.7, ranking asserted in Data.Tests per ruling Q8); two empty states plus query-failure and unreachable-root banners; probe on load and ScanFinished off the UI thread |

Verification: `dotnet build -c Release` and `dotnet test -c Release` green. Additionally the
verification agent scans a generated fixture library over a fresh `GALACTILOG_APPDATA` root with
`general.default_page_size` set to 5. The library holds six `OBJECT` groups across two rigs, two
filters, two session dates, and one name that resolves offline plus one that does not. The agent
then launches the application and confirms: the dashboard lists the six groups; each filter
section narrows the list, using a SIMBAD-derived category or `Unresolved` for the Object Type
section and never a solar-system pill; changing the sort key and the page changes the visible
rows; a hidden column is still hidden after a restart; and `logs\galactilog-<date>.log` exists
under the resolved app data root and is not empty.

Verification status: PASSED 2026-09-10. Clean-copy build (0 warnings), test 1257/1257 twice (no flake), FileSafetyTest, FontSizeTokenTest and ThemeResourceTest green, three migrations; 44-file fixture library (6 OBJECT groups, 2 rigs, 2 filters, 2 session dates) scanned complete; GUI driven through UI Automation at page size 5: totals 6 groups / 38 frames / 3.2 h match the database, search pin, Object Type (Galaxy, Unresolved), Date Range, Filters, Equipment, Metrics (HFR), FITS Header Query (GAIN = 100), sort and paging each changed the rows as expected, a hidden column survived a restart, logs\galactilog-20260910.log written with the startup line; fixture folder byte-identical before and after; hygiene grep clean (3 #AARRGGBB literals are shadow tokens in Theme/Scales.axaml). Report: docs/superpowers/work/phase5/verification-report.md.

Resume notes: check for `src/GalactiLog.Data/Queries/TargetListingQuery.cs`,
`src/GalactiLog.Core/Aliases/AliasMap.cs`, and a populated `src/GalactiLog.App/Views/MainWindow.axaml`
with a navigation rail. Check whether `display.columns.dashboard` is written by any code path.

---

## Phase 6: Target detail

Goal: A target opens to its header block, totals, session accordion, frame table, raw header panel
with provenance, and both metric charts.

Status: DONE

| Task | Status | Notes |
|------|--------|-------|
| `TargetDetailQuery` per spec 12.4: the header block fields, the totals row including average HFR in pixels and arcseconds with the excluded-frame count for frames lacking a plate scale, the eccentricity average pooling only the modal `eccentricity_source` with its excluded count, and the session overview list. `fwhm` is the only FWHM read (spec 7.1.1). Verify: `GalactiLog.Data.Tests` Detail queries row of spec 18.1, asserting the arcsecond conversion applies only to plate-scaled frames and that both exclusion counts are reported. | DONE | |
| `SessionDetailQuery` per spec 12.4: one session's ranges (min and max HFR, eccentricity, FWHM, guiding RMS, sensor temperature, gain, exposure times present, first and last frame time), per-filter medians and detail rows, median airmass, ambient temperature and humidity, session insights (HFR and eccentricity outliers against the session and rig baselines), and frame rows. Issued on card expansion, not up front. Verify: `GalactiLog.Data.Tests` asserts each aggregate against a seeded session with known values; `GalactiLog.App.Tests` asserts that the card issues the query exactly once and only on expansion (Phase 6 ruling Q8). | DONE | |
| Target detail view: header block with inline rename setting `name_locked`, aliases, object type and category, constellation, coordinates, size, position angle, magnitudes, SAC text, membership badges, the reference thumbnail slot, the totals row, actions (rename, re-resolve, copy frame list to clipboard, reveal target folder), and the collapsible target notes box with 1 second idle autosave and a saving indicator. Verify: `GalactiLog.App.Tests` asserts autosave fires once after the debounce window rather than per keystroke, that rename sets `name_locked`, and that the header block binds every listed field. | DONE | Re-resolve action hidden until Phase 7 (ruling: dead action while `TargetResolver` returns Cache for existing targets). |
| `SessionAccordionCard` control per spec 12.4: the collapsed field set including the rig count when more than one rig imaged that night, the expanded field set, per-filter medians and detail rows, session insights, and the session notes box with autosave. Verify: `GalactiLog.App.Tests` headless test asserts a collapsed card renders every collapsed field, that expansion issues `SessionDetailQuery` exactly once, and that the rig count appears only for a multi-rig night. | DONE | |
| Frame table per spec 12.4: every column in the eight-group table, sortable ascending and descending, visibility persisting in `display.columns.frames` and additionally governed by the `display.groups` metric-group toggles so a disabled group hides its columns everywhere, the guiding RMS source glyph, and the per-frame actions (open preview, reveal, open with, copy path, show raw headers). Verify: `GalactiLog.App.Tests` asserts that disabling a metric group hides its columns even when they are present in the persisted column list, and that every column sorts both directions. | DONE | |
| `RawHeaderPanel` control per spec 12.4 and 7.3: every `raw_headers` key sorted with a filter box, `COMMENT` and `HISTORY` rendered as multi-line blocks from their stored arrays, and the Derived metrics section listing each stored metric with its value and provenance string. `median_fwhm` appears here only, labelled "Header FWHM" with its source keyword and visually separated from the "FWHM (arcsec)" row. Verify: `GalactiLog.App.Tests` asserts the two FWHM rows carry distinct labels, that a stored metric renders its provenance string, and that a repeated `COMMENT` renders every line. | DONE | |
| LiveCharts2 global configuration (`ChartTheme.Apply()` invoked at application startup from `App.OnFrameworkInitializationCompleted`, since `AppHost.Build` runs before Avalonia exists and is also the CLI entry point; transparent background, axis and legend colours bound to theme tokens and re-read on theme change, tooltip colours, 200 ms animations), the fixed `metric-*` series colour mapping, and the `user_settings.graph` writer per 5.8.3. Verify: `GalactiLog.App.Tests` asserts the series colour for each metric key matches its `metric-*` token and that toggling a metric writes `graph.enabled_metrics`. | DONE | Row split from the original charts row (Phase 6 ruling Q1). |
| The cross-session metric trend chart with gaps preserved for missing sessions and optional per-filter splits, and the per-session frame chart with the median reference line; selections persist in `user_settings.graph` per 5.8.3. Verify: `GalactiLog.App.Tests` asserts that a missing session produces a gap rather than a joined line. | DONE | Row split from the original charts row (Phase 6 ruling Q1). |

Verification: `dotnet build -c Release` and `dotnet test -c Release` green. Additionally the
verification agent opens a seeded target in the running application and confirms the header block,
totals, at least two session cards, an expanded card's frame table, the raw header panel showing
both FWHM rows distinctly, and both charts rendering.

Verification status: PASSED 2026-09-10 (clean-copy build 0 warnings, test 1811/1811 twice with
identical per-project totals and no flake, FileSafetyTest green; 234-file fixture library with a
three-session multi-rig target, a 200-frame session and a N.I.N.A. CSV sidecar pair scanned
complete 232/232; GUI driven through UI Automation: row click opens the target, header block,
totals row 20 frames / 1.7 h / 2.61 px / 5.17 arcsec with "4 frames without a plate scale" /
0.49 with "source: header, 4 frames excluded", three collapsed cards including the 2-rigs pill,
one card expanded with five ranges, two per-filter median rows, two insights and a twelve-row
frame table, the column picker hid Stars and it survived a restart, the raw header panel shows
"Header FWHM 3.10 (FWHM)" and "FWHM (arcsec) 2.60" as separate rows plus a three-line COMMENT
block, both charts render with a legend and the median reference line, two metric pill toggles
rewrote user_settings.graph.enabled_metrics, Back returned to the dashboard, and the notes box
wrote targets.notes only after the 1 s idle window; library byte-identical before and after;
hygiene grep clean. Finding: the F12 virtualisation condition is met, the largest session
(200 frames) stalls the UI thread 605 ms median, 688 ms worst. Report:
docs/superpowers/work/phase6/verification-report.md.)

Resume notes: check for `src/GalactiLog.Data/Queries/TargetDetailQuery.cs`,
`SessionDetailQuery.cs`, `src/GalactiLog.App/Controls/SessionAccordionCard.axaml`, and
`RawHeaderPanel.axaml`. Check whether `LiveChartsCore` is configured in `AppHost`.

---

## Phase 7: Merge, deduplication, and unresolved names

Goal: Duplicate targets are detected, proposed, merged with a reversible manifest, and unmerged
exactly; unresolved `OBJECT` names are listed and retryable.

Status: DONE (commit 9507ebd on snd)

| Task | Status | Notes |
|------|--------|-------|
| Duplicate detection pass per spec 9.7: run at the end of every scan that ingested at least one new file, over distinct unresolved `OBJECT` strings with no existing `merge_candidates` row in any status. Pass 1's four outcomes with their exact `method`, `similarity_score`, and reason text; the 0.4 trigram threshold; Pass 2 proposing merges between active targets sharing a normalized name or overlapping aliases. Wired into `ScanCoordinator` as the `dedup` progress task left as a hook in Phase 4. Verify: `GalactiLog.Data.Tests` asserts each of the four Pass 1 outcomes against a seeded library, that outcome 2 assigns frames directly and writes no candidate, and that the reason text matches the specified strings verbatim. | DONE | Pass 2 writes method duplicate (ruling Q4); runs only when the scan classified at least one new file (Q6); a transient network failure stops Pass 1 and leaves Pass 2 to run (Q7). |
| Merge and unmerge service per spec 9.7 and 5.11: the manifest written before anything moves, recording moved image ids, re-keyed notes, and appended notes with the winner's prior text; the note collision rule producing the exact `--- merged from <loser name> ---` form with the loser's row left in place; `merged_into_id` and `merged_at` set with the loser row never deleted; unmerge consuming and deleting the manifest with no fallback path. Verify: `GalactiLog.Data.Tests` Merge row of spec 18.1: a merge writes a manifest, moves frames, re-keys and appends notes correctly; unmerge restores the exact pre-merge state; a second unmerge is a no-op. | DONE | Manifest payload carries five keys: moved_image_ids, notes_rekeyed, notes_appended, aliases_added, source_name (rulings Q9 and the Task 2 review); every merge shape writes a manifest (Q8). |
| `MergeCandidateQuery` and the candidate list in Settings, Targets tab per spec 12.9: one row per pending candidate showing source name, frame count, suggested target, score as a percentage, method, and reason text, with accept, dismiss, and edit-target actions, and the "No duplicate suggestions" empty state. Verify: `GalactiLog.App.Tests` asserts dismiss sets status `dismissed` and removes the row, and that edit-target opens target search. | DONE | Also built the Settings page shell over ten NavigationItem tabs, Targets real, nine placeholders (ruling Q2); edit target writes suggested_target_id via Retarget (Q17). |
| Merge preview dialog per spec 12.9: the fuzzy alias-aware target search box, the side-by-side winner and loser comparison with every listed field, the colliding session date list with the note-merge behavior stated, and confirm and cancel with the confirm reporting counts. Verify: `GalactiLog.App.Tests` asserts the dialog lists exactly the colliding dates for a seeded pair and that cancel writes nothing. | DONE | |
| Merge history and undo per spec 12.9, on Target detail and in Settings: one row per manifest showing loser name, merge time, moved frame count, and an undo button that consumes the manifest. Verify: `GalactiLog.App.Tests` asserts undo removes the history row and restores the loser as an active target. | DONE | Merged-away target shows This target was merged into "<winner>". with an Open button; no silent redirect (ruling Q12). |
| Unresolved names per spec 9.7 and 12.7: the distinct unresolved `OBJECT` list with frame counts, the retry action clearing every negative cache row and re-running resolution for each distinct name assigning frames where it now succeeds, the rename history list, and the "Every OBJECT name resolved." empty state. Verify: `GalactiLog.Data.Tests` asserts the retry clears negative rows and assigns frames for a name made resolvable by a newly loaded catalog, and that a still-unresolvable name is re-negative-cached. | DONE | Rename history reads target_renamed activity events, bounded by activity retention (ruling Q11); the retry refuses while a scan runs (Task 6 review). |
| Solar-system name-pattern classification at target creation (FIXER LIST item 4): `SolarSystemNames.Classify` runs after the offline catalogs, SIMBAD and SESAME have all produced nothing and before the negative-cache row is written, creating a `user_defined` target with the category literal in `object_type`; the `Categorize` category-name pass-through restored. The catalog re-enrichment writer `TargetEnrichmentRepository.ReEnrich` (FIXER LIST item 13) and `TargetDetailViewModel.ReResolveAvailable` set to true. Verify: `GalactiLog.Core.Tests` asserts the classifier's matches and non-matches; `GalactiLog.Data.Tests` asserts a re-enriched target takes the identity's fields and that a `user_defined` target is suppressed. | DONE | Added at the Phase 7 brief (ruling Q1); not in the original plan. |

Verification: `dotnet build -c Release` and `dotnet test -c Release` green. Additionally the
verification agent seeds two targets that should merge, runs the dedup pass, accepts the candidate
through the dialog, confirms the frames moved and the notes merged per the specified text, then
undoes it and confirms the pre-merge state is restored exactly.

Verification status: PASSED 2026-09-11 on a clean copy (see docs/superpowers/work/phase7/verification-report.md).

Resume notes: check for `src/GalactiLog.Data/Repositories/MergeRepository.cs`,
`src/GalactiLog.Data/Queries/MergeCandidateQuery.cs`, and a merge preview dialog view. Check
whether `ScanCoordinator` emits the `dedup` progress task with a real implementation behind it.

---

## Phase 8: Thumbnails and preview

Goal: Frames render. Debayer, stretch, resample, cache, on-demand frame thumbnails, the reference
thumbnail pass, and the preview modal with shell integration.

Status: DONE

| Task | Status | Notes |
|------|--------|-------|
| `Debayer` per spec 11.1: superpixel 2x2 binning with the four pattern offsets, blue at the diagonal opposite and the two greens at the off-diagonal corners, even-truncated dimensions, pattern from `BAYERPAT` or the XISF `ColorFilterArray`, an unknown pattern treated as mono, 512-row even strips, and `XBAYROFF`/`YBAYROFF` deliberately not applied. Verify: `GalactiLog.Core.Tests` Debayer row of spec 18.1: each of the four patterns against a hand-built 4x4 array, and strip-wise output byte-identical to whole-frame output. | DONE | Debayer with four pattern resolvers, FitsImageReader.ReadMonoStrip with Describe and PixelGeometry extracted |
| `MtfStretch` and `Resampler` per spec 11.2: the MTF formula, per-channel unit normalization, `stretchChannel` with the `shadows >= 1.0` reset to 0 and the `mad == 0` flat-128 early return placed after the shadows and midtone computation, unlinked per-channel colour stretching, the `resize_array` four-step resample with the integer prefilter and Mitchell cubic on `SKColorType.RgbaF32`, and the `_read_binned` pre-binning step. Verify: `GalactiLog.Core.Tests` Stretch row of spec 18.1 with arithmetic exactness on the MTF math, and the Resampling row asserting shape only with no exactness assertion against any reference resampler. | DONE | MtfStretch with median and MAD computation, Resampler with Mitchell cubic on RgbaF32, row offsets widened to nint |
| `ThumbnailRenderer` per spec 11.2 pipeline order (read, block-bin, normalize, flip for FITS only, resize on linear data, stretch, encode) and 6.2.8 orientation, encoding into a `MemoryStream` and writing through `AppWriter` so SkiaSharp is never handed a path. Verify: `GalactiLog.Core.Tests` asserts a FITS frame is vertically flipped and an XISF frame is not, and `FileSafetyTest` stays green with no SkiaSharp path-based encode call anywhere. | DONE | ThumbnailRenderer pipeline with header-aware debayer probes and in-place FlipVertical, OutOfMemoryException reported as skip |
| `ThumbnailCache` per spec 11.3: the cache root from `general.thumbnail_cache_dir` with relocation not moving existing files, the three kinds with their widths, formats, qualities, and path shapes, the SHA-256 cache key over path, size, mtime, and width, and LRU eviction bounded by `general.preview_cache_mb` applied to `previews` only, on start and after each preview generation. Verify: `GalactiLog.Core.Tests` asserts the key changes when any of the four inputs changes; `GalactiLog.App.Tests` asserts eviction removes the least recently used previews down to the bound and never touches `frames` or `reference`. | DONE | ThumbnailKey and ThumbnailCache with per-key gate, in-process LRU on monotonic counter, 100 MB floor; phase fixer added spec 10.6's budget of 2 as a semaphore outside the key gate (the one choke point every render passes through) and a forced render that deletes the existing reference file before rendering |
| On-demand frame thumbnails per spec 11.4: never generated during the header pass, requested by a frame grid or a preview opening, queued to a bounded worker at concurrency 2 serving the most recently requested item first, with a placeholder rendered until generation completes. Verify: `GalactiLog.App.Tests` asserts the queue serves LIFO, that concurrency never exceeds 2, and that a placeholder is replaced on completion. | DONE | ThumbnailWorker with LIFO queue and dedup, ThumbnailSlotViewModel with off-thread decode, 5 s shutdown budget |
| Reference thumbnail pass per spec 11.4: one per target, generated in the background after the header pass from the target's most recent LIGHT frame with a capture date that is not rejected for pixel reading, processing targets with no `reference_thumbnail_path` or all targets when forced, cancellable, committing every 10 targets, and emitting the `thumbnail`/`reference_thumbnails` activity event. No survey image is ever fetched (spec 11.4, 19.2). Verify: `GalactiLog.Data.Tests` asserts an interrupted pass keeps what it produced; a code search asserts no SkyView or DSS URL exists anywhere in the solution. | DONE | ReferenceThumbnailSourcesQuery with up to 3 frames per target, ReferenceThumbnailPass with per-target isolation, NoSurveyImageFetchTest; phase fixer forwards force into the render delegate, so a forced run replaces the pixels instead of taking the cache hit |
| Preview modal per spec 11.5 and `ShellIntegration`: render at `general.preview_resolution` or native when 0, the zoom range and pointer-centred wheel zoom, drag pan, fit on double-click and `0`, next and previous within the originating frame list, the header panel toggle on `H`, reveal via `explorer.exe /select,"<path>"` on `Ctrl+E`, open with default application via `Process.Start` with `UseShellExecute` on `Ctrl+O`, copy path on `Ctrl+Shift+C`, and close on `Escape`. Always autostretched, no manual controls. Verify: `GalactiLog.App.Tests` asserts every keyboard shortcut at the ViewModel level (spec 18.3), that navigation is bounded by the originating list, and that the reveal and open commands are Windows-guarded. | DONE | ModalHost extracted and reusable, PreviewModalViewModel with zoom bounds, ShellIntegration Windows guard on reveal, FrameTableViewModel seam |

Verification: `dotnet build -c Release` and `dotnet test -c Release` green. Additionally the
verification agent scans generated fixtures, confirms reference thumbnails appear on target detail,
opens the preview modal on a frame, and exercises zoom, pan, next, previous, header toggle, and
close, confirming no file outside the thumbnail cache is written.

Verification status: PASSED 2026-09-11. Clean-copy restore, build (0 warnings), test 2592/2592 twice (2593 after the logo and icon addition, coordinator run); fixture scan of 8 files (7 ingested, 1 rejected, 3 targets, 2 sessions); reference thumbnails on disk and in the Target detail header (Bayer in colour); preview modal on mono FITS, Bayer FITS and XISF; pointer-centred wheel zoom, pan only above fit, fit on 0 and double-click, next/previous clamped at both ends, H panel with six derived metrics and provenance, Ctrl+Shift+C, Ctrl+E, Ctrl+O, Escape; fixture folder byte-identical after the run (including access times), 13 files created under app data only; hygiene grep clean (three comment-only SDSS/prohibition hits). Known accepted limits: in-process LRU; a preview larger than the whole bound can be evicted by a concurrent start-up sweep; OutOfMemoryException reported as a skip; three-frame walk; CLI scans render on the scan thread; zoom below fit is centred and non-pannable; merge confirm counts shown on the Targets tab only.

Resume notes: Core/Imaging/MtfStretch.cs, Debayer.cs, Resampler.cs, ThumbnailRenderer.cs, ThumbnailKey.cs exist; App/Services/ThumbnailCache.cs, ThumbnailWorker.cs, ModalHost.cs, PreviewModalService.cs exist; App/Views/Preview/PreviewModalWindow.axaml exists; Data/Ingest/ReferenceThumbnailPass.cs exists. Check whether %LOCALAPPDATA%\GalactiLog\thumbnails (or GALACTILOG_APPDATA\thumbnails) holds reference/, frames/ and previews/ after a scan.

---

## Phase 9: Statistics, activity, settings, and setup wizard

Goal: Every remaining screen exists. The application is usable from first launch through daily use.

Status: DONE

| Task | Status | Notes |
|------|--------|-------|
| `AstroNight` per spec 8.4: the -18 degree definition, `darkHoursForNight` sampling every 10 minutes from 12:00 UTC, `sunAltitudeDegrees` transcribed with every degree-to-radian conversion explicit, `mod360` and `mod24` correct for negative operands, `julianDate`, memoized monthly and weekly totals, and ISO week boundaries. Unavailable when observer coordinates are unset. Verify: `GalactiLog.Core.Tests` Astro night row of spec 18.1 against the three hardcoded USNO rows at 10 minute tolerance on the threshold crossings and 20 minutes on `darkHoursForNight`, plus the negative-`n` cases. | DONE | AstroNight in GalactiLog.Core, Sessions folder; verified against the three USNO rows of spec 18.1 |
| `StatsQuery.Get()` and `StatsQuery.Calendar(from, to)` per spec 12.5: the full single-call response with overview tiles, equipment performance including MADs and the per-filter breakdown, equipment inventory, filter usage, top targets, data quality including both HFR histograms and the two exclusion counts, storage, and ingest history from `scan_runs`; cached in memory until the next scan completes or settings change. Verify: `GalactiLog.Data.Tests` Stats query row of spec 18.1: every field against a seeded fixture with known totals, plus an assertion that a completed scan invalidates the cache. | DONE | StatsQuery, StatsCache and the calendar query in GalactiLog.Data; the cache invalidates on scan completion, settings saves and maintenance actions |
| Statistics page per spec 12.5 and 13: every section, the timeline and calendar toggle with granularity and range presets and the imaged-only toggle, efficiency labels shown only when observer coordinates are configured, and the eight LiveCharts charts plus the custom `CalendarHeatmap` control with its five-step ramp and tooltip. Verify: `GalactiLog.App.Tests` asserts each chart binds its specified series and colour token, that the efficiency series is hidden when coordinates are unset, and that the calendar tooltip carries date, integration, frame count, target count, and dark hours. | DONE | |
| Activity page per spec 12.6: `ActivityQuery.Page(filters, before, limit)` with keyset pagination on `(timestamp, id)`, the reverse-chronological list with severity, category, message, and duration, scan events expanding to their `parent_id` children, details rendered as a key-value table falling back to formatted JSON, severity, category, and free-text filters, refresh, prune now, and the empty state. Verify: `GalactiLog.Data.Tests` asserts keyset paging returns no duplicates and no gaps across pages; `GalactiLog.App.Tests` asserts a scan event expands to its children. | DONE | ActivityQuery with the keyset cursor and the Activity page; a failed or cancelled scan surfaces on the collapsed scan_started row |
| Settings tabs Library, Location, Display, Storage per spec 12.7: the scan roots, include and exclude path lists, the name rule editor with the test-a-path box showing verdict and deciding rule ids, calibration, auto-scan, and watcher controls, a manual scan button with progress and cancel; observer fields with range validation; the metric group and field checkboxes writing `display.groups`, the per-table column picker writing `display.columns`, text size, content width, and the theme picker; and the thumbnail path picker with free-space readout, preview resolution, preview cache size, and thumbnail width. Verify: `GalactiLog.App.Tests` asserts each control round-trips through `SettingsStore`, that an include path outside every root surfaces as a configuration error rather than being silently dropped, and that the test-a-path box returns each of the four verdicts. | DONE | Phase 7 Task 3 built the Settings page shell (SettingsViewModel over ten NavigationItem tabs, Targets real, nine PlaceholderPageViewModel entries); this phase replaces one placeholder per tab and should construct tabs lazily on first visit (Phase 7 fixer list F2). This row is delivered by two tasks: Task 5 builds the Library tab and the lazy-tab mechanism, Task 6 builds the Location, Display and Storage tabs. |
| Settings tabs Filters and Equipment per spec 12.7: the canonical filter table with colour swatch and alias list editor, the discovered-name list with frame counts, suggested groupings with accept and dismiss, and `dismissed_suggestions` remembered; the same shape for cameras and telescopes. Saving invalidates the alias map cache. Verify: `GalactiLog.App.Tests` asserts a dismissed suggestion does not reappear, that accepting a grouping writes the alias list and invalidates the cache, and that a filter colour defaults to `#808080`. | DONE | Filters and Equipment tabs on the shared GroupingEditorViewModel |
| Settings tab Maintenance per spec 12.7: rebuild targets (running resolution in `skipSimbad` cache-only mode per 9.6), retry unresolved, regenerate reference thumbnails missing or all, regenerate frame thumbnails missing or purge-and-regenerate, prune activity events, and reset database with a typed confirmation. Each emits the `rebuild` activity events from 10.9. Purge deletes only inside the thumbnail cache (spec 2.1, 11.3). Verify: `GalactiLog.Data.Tests` asserts rebuild targets performs no network call, that reset database recreates an empty schema, and that thumbnail purge deletes nothing outside the cache root. | DONE | Two recorded deviations from this Verify wording, both accepted: the thumbnail purge assertions (`ThumbnailPurgeTests`) live in `GalactiLog.App.Tests`, because `ThumbnailCache` is an App-layer type, and Task 7's `SuggestionGrouper` tests live in `GalactiLog.Core.Tests` rather than where row 6 implies. |
| Setup wizard per spec 12.1: the five steps with their listed elements and validations, each step persisting its own settings before advancing, the shallow-probe file count, the free-space readout, the interval presets, the first-scan progress display with counters and cancel, and finishing writing `setup_complete = true` and navigating to the Dashboard. Reachable again from Settings as "Run setup again". Verify: `GalactiLog.App.Tests` asserts Next is disabled until a folder is chosen, that a failed save keeps the user on the step, that each step's settings are persisted before advancing, and that finishing sets `setup_complete`. | DONE | Five-step wizard on ModalHost, shown when general.setup_complete is false and from the Library tab's Run setup again |

Verification: `dotnet build -c Release` and `dotnet test -c Release` green. Additionally the
verification agent deletes the app data directory, launches the application, completes the setup
wizard against a generated fixture folder, and confirms the Dashboard, Statistics, and Activity
pages populate and every Settings tab loads and saves.

Verification status: PASSED 2026-09-15. Clean-copy restore, build (0 warnings), test 3608/3608 twice; fresh app data, fixture library of 8 files (3 targets, 2 sessions, 1 rejected) scanned through the setup wizard, which appeared unprompted, refused Escape and the title-bar close, persisted every step and seeded the five setup-exclude rules; Dashboard, Statistics (6 tiles, 8 charts, calendar tooltips) and Activity (scan_started with 7 children) populated; all seven Settings tabs loaded and saved; Run setup again then Skip left the filter document byte-identical; clean exit; fixture folder byte-identical before and after; 0 dashes across 39 documents. Report: `docs/superpowers/work/phase9/verification-report.md`.

Resume notes: Phase 9 landed src/GalactiLog.Core/Sessions/AstroNight.cs, src/GalactiLog.Data/Queries/StatsQuery.cs and ActivityQuery.cs, src/GalactiLog.App/Controls/CalendarHeatmap.cs (a custom-drawn Control, no axaml), the ViewModels/Stats, ViewModels/Activity, ViewModels/Settings and ViewModels/Setup folders, Views/Setup/SetupWizardWindow.axaml, Services/Debouncer.cs, Services/SetupWizardService.cs and Views/ModalPageWindow.cs. general.setup_complete is written by SetupWizardViewModel only.

---

## Phase 10: Diagnostics, packaging, and release

Goal: The application can be diagnosed, updated, packaged, and released. This is the phase that
makes it shippable.

Status: DONE

| Task | Status | Notes |
|------|--------|-------|
| `DiagnosticsQuery` and the Diagnostics page per spec 12.8: all seven field groups (Database, Scan, Resolver, Unresolved, Errors, Versions, Paths) with every listed field, including watcher state per root with reachability, next scheduled scan time, resolver hit and miss counters for the current process, and expired negative row count. Verify: `GalactiLog.Data.Tests` asserts every row count and size field against a seeded database; `GalactiLog.App.Tests` asserts the page renders all seven groups with no null field where the spec names a value. | DONE | DiagnosticsQuery, ResolverCounters, DiagnosticsService, DiagnosticsViewModel, and DiagnosticsView implement the Diagnostics page with database, scan, resolver, unresolved, errors, versions, and paths groups. |
| Log viewer per spec 12.8 and 16.1: a virtualized list over the current Serilog file plus retained rolled files, all read through `UserFiles` in shared-read mode so the live sink is undisturbed; minimum level filter, free-text search, follow-tail toggle, copy selection, copy all, open log folder, the `general.log_level` selector taking effect with no restart, and the empty state. Verify: `GalactiLog.App.Tests` asserts the viewer reads a file the sink currently holds open, that the level filter narrows the list, and that changing `general.log_level` changes what is subsequently captured. | DONE | LogLineParser, LogFileSet, LogReader, and LogViewerViewModel build the virtualized log viewer with filtering, search, follow-tail, and copy actions. |
| JSON diagnostics bundle per spec 12.8 and 16.3: `DiagnosticsService.ExportBundle(destination)` writing the exact eleven-key document, with `destination` coming from a platform save dialog and nowhere else, handed to `AppWriter.BeginExport(path)`, written as a new file with the dialog's own overwrite prompt the only replacement path. This is the sole sanctioned write outside app data. Verify: `GalactiLog.App.Tests` asserts the bundle contains all eleven top-level keys, that the scoped writer refuses any other path and refuses everything after disposal, and that no code path calls `ExportBundle` with a path not returned by a dialog. | DONE | DiagnosticsBundle exports JSON containing diagnostics, settings, activity events, and log entries through the platform save dialog. |
| `UpdateService` per spec 17.1: `CheckForUpdatesAsync` on start and every 6 hours, `DownloadUpdatesAsync` with progress into the status bar, a user prompt before applying, `ApplyUpdatesAndRestart` on confirmation, nothing applied silently mid-scan, channel-scoped so a stable install never offers itself a prerelease, and the `update_available` and `update_applied` activity events. Plus the Settings About tab per spec 12.7: version, git SHA, release channel, update check button, release notes, and log folder link. Verify: `GalactiLog.App.Tests` asserts the update prompt is suppressed while `ScanCoordinator` reports a running scan, that the configured channel is passed through, that a failed check logs at warning without disrupting startup, and that the About tab renders every listed field. | DONE | BuildInfo, UpdateService, and AboutTabViewModel provide update checking, download progress, and application with a status bar indicator and About tab. |
| Velopack packaging end to end per spec 17.1 and 17.5 step 4 and 5: `dotnet publish -r win-x64 --self-contained` of `src/GalactiLog.App`, then `vpk pack` with the specified pack id, version, dir, main exe, and channel, producing an installer and delta-capable package locally. Verify: the verification agent runs the publish and pack locally with a synthetic version, installs the result into a throwaway location, launches it, confirms the window opens and `%LOCALAPPDATA%\GalactiLog` is created, then uninstalls and confirms the Velopack hooks ran. | DONE | Release workflow pinned vpk version and publish directory; install and uninstall checks performed by user on 2026-09-15; data root relocated in Task 9. |
| Version derivation and workflow verification per spec 17.4 and 17.5: the tag-based derivation script for all three branches with the anchored stable-tag pattern, patch-only auto-increment, prerelease counter reset on base change; and a dry-run validation of each workflow file's trigger, permissions, concurrency, and step order, confirming `vpk upload github` is the only release-creating command and no `gh release create` exists. Verify: a script test drives the derivation against a fixture tag list asserting the expected version for each branch, including the no-tags case yielding `1.0.0`. | DONE | Derive-version.sh script with per-branch prerelease counters and VersionDerivationTests and WorkflowFileTests providing fixture-driven validation. |
| Release checklist in `docs/`: the ordered steps to cut a release from `snd`, the branch merge direction, what the prerelease retention numbers keep, how to verify an installed build's channel, and the rollback procedure. Verify: a reviewer follows the checklist against the local pack output and confirms every step is executable as written. | DONE | Release-checklist.md documents branch merge direction, alpha/rc/stable promotion, retention rules, and rollback procedure. |
| Host lifetime ownership (`TRACKING.md` section 6 item 6) and the CLI reference thumbnail pass test (`TRACKING.md` section 6 item 23): convert the convertible `AppHost.Build` instance registrations to factory registrations so the container disposes what it owns, give the three resolution-stack HTTP clients `IDisposable` with an `ownsHandler` flag set true only where `AppHost` creates the handler, and cover the reference thumbnail pass that `galactilog scan` runs with a `GalactiLog.Cli.Tests` case over a generated renderable fixture. Verify: `GalactiLog.App.Tests` asserts the host disposes the clients it owns, does not dispose a handler a caller supplied, and registers no instance that implements `IDisposable`; `GalactiLog.Cli.Tests` asserts the CLI pass writes one reference thumbnail per target under the cache root, records the path on the target row, and leaves every frame byte-identical. | DONE | AppHost converts six registrations to factories; CatalogHttpClient, SimbadClient, and SesameClient become IDisposable with ownsHandler flag; ScanReferenceThumbnailTests covers the CLI pass. |
| Relocate the application data root out of the Velopack install root and let the user choose it (spec 17.2, 2.1.1, 5.8.1, 12.1, 12.7): the default becomes `%LOCALAPPDATA%\GalactiLogData`, a pointer document at `%APPDATA%\GalactiLog\datapath.json` records the chosen root and survives an uninstall and a move, `GALACTILOG_APPDATA` still overrides both, a first start after the change copies an existing `%LOCALAPPDATA%\GalactiLog` catalogue into the new root and leaves the old one in place, and the setup wizard's storage step and the Settings Storage tab both show the current root with a folder picker. Verify: `GalactiLog.Core.Tests` asserts the resolution order, the copy-and-verify procedure, and that a destination already holding a database is refused; `GalactiLog.App.Tests` asserts the host honours the pointer, performs a pending move on start, and throws rather than creating a second empty catalogue when the pointer names a missing directory; `FileSafetyTest` stays green with the copy and the pointer write allowlisted to `AppWriter.cs` and its two callers. | DONE | New default root is %LOCALAPPDATA%\GalactiLogData with pointer at %APPDATA%\GalactiLog\datapath.json; AppDataRelocation copies existing catalogue and nothing is deleted on a move. |

Verification: `dotnet build -c Release` and `dotnet test -c Release` green. Additionally a local
`dotnet publish` plus `vpk pack` produces an installer that installs, launches, creates its app
data directory, and uninstalls cleanly; the version derivation script produces the expected version
for each of the three branches against a fixture tag list.

Verification status: PASSED 2026-09-15 on a robocopy clean copy (work/phase10/verification-report.md): build 0 warnings, 4024/4024 twice, derivation fixtures, publish and pack, CLI scan with the reference pass and a byte-identical fixture, GUI bars for the wizard Storage locations step, Diagnostics, log viewer, bundle and About, opaque dialogs; the hygiene bar was fixed by the coordinator (seven em dashes in two review files) and the pointer bar was skipped to keep the real profile untouched. The WAL escalation it raised was fixed (unpooled read-only connection) and re-observed by the coordinator; the suite is 4025 after that fix. Install and uninstall checks were performed by the user.

Resume notes: check for `src/GalactiLog.Data/Queries/DiagnosticsQuery.cs`,
`src/GalactiLog.App/Services/DiagnosticsService.cs`,
`src/GalactiLog.Core/Diagnostics/LogReader.cs`,
`src/GalactiLog.App/Services/DiagnosticsBundle.cs`,
`src/GalactiLog.App/Services/UpdateService.cs`,
`src/GalactiLog.App/Services/BuildInfo.cs`,
`tools/derive-version.sh`,
`docs/packaging.md`,
`docs/release-checklist.md`,
`src/GalactiLog.Core/Io/AppDataRootResolver.cs`,
`src/GalactiLog.Core/Io/AppDataRelocation.cs`,
`tests/GalactiLog.Cli.Tests/ScanReferenceThumbnailTests.cs`.

---

## Phase 11: Tray residency, startup, and tie-up

Goal: The application can live in the Windows notification area, start with Windows, and keep
scanning while no window is open; the remaining Phase 10 tie-up items are closed. Added by user
directive on 2026-09-15 after the Phase 10 close. `design-spec.md` has no section for this
feature yet: the brief-writer proposes one (numbered 12.11, "Tray residency and startup") in
`questions.md`, and the coordinator's rulings become the spec text before any implementer starts.

Status: DONE

| Task | Status | Notes |
|------|--------|-------|
| Single-instance GUI with activation: a second GUI launch hands its arguments to the running instance and exits, and the running instance shows and activates its window; CLI verbs are unaffected (the CLI beside the GUI stays a supported shape, spec 15). Verify: `GalactiLog.App.Tests` asserts the second launch returns without building a host and the first receives the activation; a manual bar starts the exe twice. | DONE | SingleInstanceGate with a named mutex plus auto-reset event, no pipe or socket. Second launch exits before AppHost.Build; one listener thread, fail-open on kernel-object access denial. |
| Tray icon with a native menu (Open, Scan now, Check for updates, Exit) through Avalonia `TrayIcon` in `App.axaml`, the program icon as the tray icon, a tooltip carrying the scan state; close-to-tray and minimize-to-tray as `general` settings with a General tab control each; `ShutdownMode` explicit so hiding the last window does not exit; Exit from the tray runs the same shutdown drain as the title-bar close. Verify: `GalactiLog.App.Tests` asserts the window hides rather than closes when close-to-tray is on, shows again on Open, and that Exit drains; the manual bar checks the icon, menu and tooltip. | DONE | WindowResidencyService and TrayIconViewModel implement residency, tooltips, and menu commands. General tab at index 1 carries all five controls. ShutdownMode.OnExplicitShutdown set; one exit path through TryShutdown; close_to_tray defaults on. |
| Start with Windows: a `general.start_with_windows` setting that creates or removes a Startup shortcut through Velopack's `Shortcuts` API pointing at the install-root stub `%LOCALAPPDATA%\GalactiLog\GalactiLog.exe` with `--minimized`, offered only on an installed build (disabled with the reason otherwise), never touched by the CLI, and reported in the Diagnostics Versions or Paths group. Verify: `GalactiLog.App.Tests` asserts the toggle calls the shortcut seam once per change and never when not installed; the manual bar confirms the `.lnk` under the Startup folder and its removal. | DONE | VelopackStartupShortcut behind IStartupShortcut seam, using Velopack Shortcuts API. FileSafetyTest includes StartupShortcut group with two Paths diagnostics fields. |
| Start minimized: a `--minimized` argument and a `general.start_minimized` setting that start the process into the tray with no window, the watcher and scheduler running as they do today; the setup wizard gains no step (the General tab carries all five controls). Verify: `GalactiLog.App.Tests` asserts the argument and the setting both start with the window hidden and the services started; the manual bar launches with the argument. | DONE | StartupArguments parses and consumes the switch as whole list only. MainWindow built on first Open, wizard shown on first Open after minimized start. Argument wins over setting. |
| Scan completion notice: a tray balloon or Windows toast when a scan finishes while no window is shown, with counts and the outcome, off by default (`general.notify_on_scan_complete`). The mechanism is a question for the coordinator (Avalonia `TrayIcon` has no balloon API; a toast needs a package or a P/Invoke). Verify: `GalactiLog.App.Tests` asserts the notice is requested once per finished scan only while hidden and only when enabled. | DONE | ScanCompletionWatcher and TrayTooltipScanNotifier behind IScanCompletionNotifier seam. Tooltip mechanism with notice composer, toast as upgrade path. |
| Tie-up: commit the four documents carrying the Phase 10 hash; close or carry each HANDOFF section 5 backlog item with a ruling; `TRACKING.md` watch items 1, 3, 26, 27 re-examined against one more starved run; the release checklist gains the tray and startup behaviour where an operator verifies an installed build. Verify: the docs fixer's report lists every backlog item with its disposition. | DONE | Spec 12.11 written. Backlog dispositions in tie-up-docs-report.md Part B. Starved run clean. HANDOFF rewrite and commit pending. |

Verification: `dotnet build -c Release` and `dotnet test -c Release` green. Additionally the
verification agent installs a local pack into a fresh profile (or the user does, as in Phase 10),
confirms the tray icon, the close-to-tray behaviour, the Startup shortcut's creation and removal,
a `--minimized` launch with a scheduled scan completing while hidden, and that a second launch
activates the first; `%LOCALAPPDATA%\GalactiLogData` and `%APPDATA%\GalactiLog\datapath.json`
survive the uninstall.

Verification status: PASSED 2026-09-16 on a robocopy clean copy (work/phase11/verification-report.md): build 0 warnings, 4256 tests green twice, CLI unaffected, second launch activates the first instance with no data-root change, General tab and Diagnostics fields as specified, close-to-tray on hides and off exits, real data folders and the real Startup folder untouched, zero em or en dashes in 42 documents. The tray menu order, tooltip text and Win32 minimize-to-tray were not driven (the icon sat in the hidden-icons overflow) and the install half (Startup shortcut creation, removal, uninstall survival, wizard on first Open after a minimized first run, update apply then restart) is the user's per release-checklist section 6a.

Resume notes: Phase 11 Tasks 1-5 landed the following verified paths: src/GalactiLog.App/Services/SingleInstanceGate.cs, WindowResidencyService.cs, VelopackStartupShortcut.cs, IStartupShortcut.cs, StartupArguments.cs, ScanCompletionWatcher.cs, TrayTooltipScanNotifier.cs, src/GalactiLog.App/ViewModels/Tray/TrayIconViewModel.cs, src/GalactiLog.App/ViewModels/Settings/GeneralTabViewModel.cs, src/GalactiLog.App/Views/Settings/GeneralTabView.axaml, the TrayIcon.Icons block in src/GalactiLog.App/App.axaml, and the five keys (close_to_tray, minimize_to_tray, start_with_windows, start_minimized, notify_on_scan_complete) in src/GalactiLog.Core/Settings/GeneralSettings.cs.

---

## Phase 12: Observing Ledger, the Target detail page redesign

Goal: The Target detail page becomes an observing ledger (one aligned table of nights under the
target's own averages, one night open beside it, a night strip as the session's first graphic),
the application's visual world moves to the Observing Ledger theme (neutral luminance greys,
colour only on data, flat controls, Atkinson Hyperlegible type) with a red-light variant, and
the frame table gains selection, multi-path copy and an outlier filter. Added by user directive
on 2026-09-16 after a three-lens UX panel and an approved comp
(`docs/superpowers/work/phase12/comp-observing-ledger.html`). Coordinator rulings R1 to R10 and
the task boundaries are in `docs/superpowers/work/phase12/phase12-inputs.md`; the rulings made
after the briefs were written are consolidated in
`docs/superpowers/work/phase12/task7-addenda.md`.

Status: DONE (8a12b55)

| Task | Status | Notes |
|------|--------|-------|
| Theme, fonts, control styles: `observing-ledger` (default) and `red-light` dictionaries with the 37 keys and the callout composites, `glass-void` kept; Atkinson Hyperlegible Next and Mono embedded, `Avalonia.Fonts.Inter` removed; flat Button default, `Button.primary`, `Border.tag`, text tier classes if the style-setter probe passes. Verify: theme census test for all three, default theme id test, a string-scan test that no Button style carries a gradient or tinted fill. | DONE | Three dictionaries at 53 keys each: 38 brush tokens, the 8 callout composites, the 4 semantic `*Value` colours they derive from, the gradient pair and `BrushPageBackground`; `ThemeManager` table of three with `observing-ledger` first (R7, Q13). The 38th token, `ColorAccentPressed`, is the fixer's third accent rung. The blocker fix added `ThemeManager.ApplyStored`, which applies the stored theme id at startup from a `StartupTheme` value `AppHost` registers, so Q13 holds at boot and not only after a visit to Settings; a `SourceScan` census pins the call site. Six static Atkinson TTFs plus `OFL.txt`, the Inter package and `WithInterFont()` gone (R8, Q1 to Q3). R5's setter probe passed, so the four type tiers ship as classes. `red-light` ships at 4.77 / 3.50 / 2.51 contrast by decision. The fix passes added `Button.primary:disabled` and `:pressed` and made the tinted-fill scan able to match a property-element brush. |
| Query flags and night bounds: `SessionDetailQuery` frame rows carry `IsHfrOutlier` and `IsEccentricityOutlier` by the insight rules; `AstroNight.NightBounds` gives astronomical dusk and dawn for a session date and site, null without coordinates. Verify: flagged-row counts equal the insight counts on the fixture sessions; bounds for a known date and site within one minute of spec 8.4. | DONE | `FrameRow` gained the two flags as its last two positional parameters and the session-wide rule is the one rule (R3, Q8, Q9). The shipped signature is `NightBounds(sessionDate, latitude?, longitude?, TimeZoneInfo)`. The review's P1 moved the sampling window from 12:00 UTC to 12:00 local in the display zone with the longest dark run selected inside it; spec 8.4 records both windows. `TargetDetailQuery` also gained `ReferenceArcsecPerPixel` and `ReferenceFrameWidthPixels` from one ordered subquery (Q6). |
| NightStrip control: drawn control plus view-model (ticks by capture time in the filter's ink, outliers taller in the worse ink, dusk to dawn band, first and last labels, click selects the frame). Verify: tick and outlier counts, band absent without bounds, pointer press raises `FrameSelected`, immutable brushes. | DONE | `Controls/NightStrip.cs` drawn in `Render` with no `.axaml`; every brush arrives from the host or the view-model and `NightStripSource_ContainsNoColourLiteral` is the enforcement. The fix pass added a headless render tick and real dusk-to-dawn bounds to the cases, and made the control non-tab-stop while focusable. The DST-safe axis is a Phase 13 candidate. |
| Page shell, ledger, drawer: identity and log lines, thumbnail arcminute bar, "Trend across nights" band, 640 px ledger `ListBox` with the target row fixed above the nights and worse-ink cells against the target means, Details `SplitView` drawer (header block, target notes, merge history), overflow flyout for Rename, Merge, Re-resolve, keyboard bindings, `SelectedSession` driving the existing load path. Verify: newest night selected and loaded on open, selection switch semantics, `IsWorse*` truth table, drawer and flyout tests, existing action tests green. | DONE | `FilterSwatchViewModel` and `ChartSelectionViewModel.FilterTint` are the one filter-colour spine the ledger, the log line and the pane's filter table all read. The Q6 scale bar measures the reference frame's own field. Ruling (1) of the task review puts the target's arcsecond HFR mean in the Details drawer beside its plate scale, ruling (3) makes the ledger's selected row read in the accent ink, ruling (6) makes the page's Escape and its other bindings yield to a focused text box. The blocker fix gave the narrow ledger's Night column a 108 px floor, dropped the cell gutter to 4 px, capped the seven numeric columns and made the Filters column leave its shared size group below the breakpoint rather than be clamped; the identity line became a `DockPanel` that trims the other names before the actions. |
| SessionPane: night header and facts line, NightStrip host, comparison sentence labelled per R4, findings lines with show actions, merged per-filter table with `SharedSizeGroup`, ranges with median bars, "Metrics over the night" band, session notes inline, frame table host filling the height; `MetricChartView` to one pill row with dot pills and no legend; `SessionAccordionCard` retired. Verify: named elements, merged-row shapes, show action sets the filter, shared column widths equal after layout. | DONE | `Controls/SessionPane.axaml` with `FindingViewModel` and `FilterTableRowViewModel`; `SessionAccordionCard` and its tests deleted (R10). `Button.pill` and `Button.header-cell` were lifted into `Theme/Controls.axaml` from four views and the `ControlStyleScanTest` skip is gone. Ruling (8) makes a multi-exposure filter's medians row show the totals of its exposure rows; ruling (9), re-ruled, sets the pane's side-by-side breakpoint structurally at 1074 and withdraws Q12's "1400 rule". Ruling (1) of the task review retires the per-night rig count and notes indicator. |
| Frame table: multiple selection, `Copy paths (n)`, Reveal at one, outlier filter with shown count, outlier rows in the worse ink, mono file names, numeric headers right-aligned with units, no row hairlines, no fixed viewport height. Verify: copy format and order, reveal enablement, filter membership equals the flags, header alignment, `FileSafetyTest`. | DONE | The 420 px scroller and its viewport resource are gone; the table takes the pane's starred row and stays virtualised. `Copy paths (n)` reuses `ShellIntegration.CopyFrameListAsync` rather than a second format (ruling (4)). Ruling (a) of the task review lifts a flagged row's whole line to the primary ink with only the offending cell in the worse ink; ruling (b) gives Escape a `CanExecute` so an empty Escape reaches the page (Q14). |
| Docs and DESIGN.md: spec 12.4, 13 and 14 amended per R2, R6, R7, R8, R9, R10; `DESIGN.md` at the repository root from the built world; HANDOFF, TRACKING, this file. Verify: zero em or en dashes in every touched document; every spec 12.4 item still named; DESIGN.md values match the dictionary by grep. | DONE | Spec 4.3, 5.8, 8.4, 12.4, 13 and 14 amended, including the three-dictionary opening, the three-column token table, the Atkinson typefaces, the six type tiers, the flat button rule with its one exception, and the page's height and responsive contracts. `DESIGN.md` written at the repository root and checked key by key against `ObservingLedger.axaml` and `RedLight.axaml`. HANDOFF sections 1, 2, 5 and 8 and TRACKING sections 3, 6 and 8 rewritten for the close. |

Verification: `dotnet build -c Release` with 0 warnings and `dotnet test -c Release` green on a
clean copy. No real library exists on this machine, so the verification agent launches the
application against the generated library at `C:\tmp\p12-fixtures`, scanned into a fresh
`GALACTILOG_APPDATA`, with `%LOCALAPPDATA%\GalactiLogData` and `%APPDATA%\GalactiLog` asserted
absent before and after. The fixture is 40 renderable 64 by 64 FITS LIGHT frames for one
target, "M 31", which resolves offline to "M 31 - Andromeda Galaxy", across three nights:
2025-01-10 Ha, five frames at 120 s and five at 300 s, which is the multi-exposure filter the
medians row is checked on; 2025-02-14 OIII, eight at 300 s; and 2025-03-20 Ha, twenty-two at 300 s,
the newest, carrying the two constructed HFR outliers (8 and 9 against a 3.63 threshold) and the
one eccentricity outlier (0.92). A headless scan of it discovers 40, completes 40 and fails none.
It is regenerated from `docs/superpowers/work/phase12/fixtures/` by the recipe in its README. The agent opens the target
and confirms: the newest night is selected on open; the
ledger's first row carries the target means and worse cells read in the worse ink; the night
strip's ticks match the frame count and its outlier ticks match the findings; "show" filters the
frame table to the flagged rows; three selected rows copy three paths; the Details drawer opens
and closes; the red-light theme applies from Settings and every page reads in red only; the
glass-void theme still applies; and no user file changed, by a before and after hash of
`C:\tmp\p12-fixtures`.

The phase review adds eleven launched-app steps that the suite cannot observe, listed in full
under "Notes for the verification agent" in `docs/superpowers/work/phase12/phase-review.md`. In
short: at the shipped 1280x800 window with the rail expanded, report whether the frame table has
height and whether the ledger's last columns fit, with the trend band collapsed and expanded;
maximise and report the widths at which the ledger widens to 640, the drawer becomes inline, and
the ranges table moves beside the filter table (expect about 1800, 1800 and 1982 with the rail
expanded, 1650 and 1830 collapsed); confirm the page title, the night header and the log-line
figures render in genuinely different weights and in Atkinson Hyperlegible rather than Segoe UI;
confirm the mono face in the frame table's file names, the drawer's RA and Dec, the scale-bar
label and the raw header panel; walk every page in Red Light, charts included, then return to
Glass Void; confirm a fresh profile opens in Observing Ledger and a stored `glass-void` is kept;
with and without coordinates, confirm the night strip's band, its local wall-clock hour labels,
its tooltip retargeting and its click-to-select; walk the Escape order in the frame table and
inside each text box; tab to Run scan, press space, and record whether the fill changes; copy
three selected paths and confirm Reveal is enabled at exactly one; and hash the library folder
before and after the whole session.

Verification status: PASSED 2026-09-17 on a clean copy against `C:\tmp\p12-fixtures`, after a
blocker fix (`work/phase12/verification-report.md`, both runs, and `work/phase12/blocker-fix-report.md`).
Restore, build at 0 warnings and 4476 passed, 0 skipped, 0 failed in one pass; `FileSafetyTest`
green; a headless scan discovering, completing and failing 40, 40 and 0 with the fixture hashes
identical before and after; 16 bar items PASS with one NOT OBSERVABLE, the ledger's worse ink,
because every one of the fixture's outliers lifts the target mean above every night's median. The
observed widths match the declared figures: the ledger widens at a 1800 pixel window with the rail
expanded and 1648 collapsed, and the ranges table moves beside the filter table at a pane width of
1074, a 1830 pixel window collapsed.

The first run found four defects at the shipped default window that the suite could not see, and
all four were fixed with cases at the page's real allotment and re-verified: the ledger's Night
column rendered nothing at 1280x800, a stored theme was not applied at startup so Red Light and
Glass Void survived only until the next launch, the eccentricity finding's show action was drawn
outside the window, and Copy frame list overprinted the other-names text. The re-run confirms each:
whole ISO dates in the Night column at 126 pixels inside the 520 pixel ledger with the target row's
"All 3 nights" label, with the trend band collapsed and expanded; a fresh profile opening in
Observing Ledger and a stored `glass-void` or `red-light` applied at the next launch; the show
action inside the pane and filtering to the one flagged frame; and Copy frame list clear of the
trimmed other names.

Deviations the runs recorded and that no document declared are carried as Phase 13 candidates
rather than fixed at the commit gate: clearing an outlier filter keeps the finding's sort order
instead of returning to capture time; scrollbar thumbs stay Fluent neutral grey in Red Light;
observer coordinates, the display timezone and a theme change reach an open Target detail page only
after a restart; the night strip's axis spans about 30 hours when no frame falls inside the sampled
band, compressing the ticks and overprinting the edge labels, and what the strip should do in that
case is undefined; the strip's hit tolerance is about 4 pixels in a 25 pixel tick pitch; Escape with
focus on a column header skips a live selection; the findings block's last action can sit 12 pixels
below the pane scroller's viewport at the default window until the top block is scrolled; and a
fixture night genuinely worse than the target means is needed before the ledger's worse ink can be
observed at all.

Phase 13 candidates recorded by this phase, each with what raised it. They are candidates, not a
plan; the user decides whether a Phase 13 exists and what is in it.

1. A second, narrower ledger column set for a 1280 pixel page, so the eight columns that fit 520
   pixels are chosen rather than trimmed (Q12).
2. The dashboard's and Settings' badges and buttons re-skinned to `Border.tag` and the shared
   button vocabulary; Phase 12 scoped the new vocabulary to Target detail (Q5).
3. A DST-safe night axis: the night strip's axis is local wall clock of kind `Unspecified`, so a
   transition night distorts it (`task3-review.md` P3-4).
4. The headless render tick as a suite-wide test convention, rather than the one Task 3 case that
   needed it.
5. A test harness that renders a page inside the shell's real geometry, the navigation rail and
   the status bar. Page cases hosted as the whole window measure about 200 pixels wider and 64
   taller than the application gives the page, which is why a green suite hid both phase-review
   P1s.
6. A target-level per-filter integration aggregate, which R1 refused for this phase.
7. Restoring keyboard focus to the first surviving row after a frame-table re-projection. The
   fixer left it: it needs a change in `FrameTableViewModel.Project` and in the view together,
   which is behaviour work with its own review surface. The verification run confirmed the
   consequence: after Escape clears the filter, a second Escape reaches nothing until focus is
   re-established.
7a. The findings block's last action at the shipped 1280x800 window with the trend band collapsed
    sits 12 pixels below the pane scroller's viewport and is clipped until the top block is
    scrolled. It should be fully inside the viewport at the default window.
7b. Verify the extra-large text size against the narrow ledger. It was not exercised by either
    verification run, so it is an unverified size rather than a known defect.
7c. The deviations the verification runs recorded and no document declared: clearing an outlier
    filter keeps the finding's sort order; scrollbar thumbs stay Fluent neutral grey in Red Light;
    observer coordinates, the display timezone and a theme change reach an open Target detail page
    only after a restart; the night strip's axis spans about 30 hours and overprints its edge
    labels when no frame falls inside the sampled band, and what it should do then is undefined;
    the strip's hit tolerance is about 4 pixels in a 25 pixel tick pitch; and Escape with focus on
    a column header skips a live selection.
7d. A fixture night genuinely worse than the target means, so the ledger's worse ink can be
    observed at all. Every outlier in the current fixture lifts the target mean above every
    night's median, which is why that bar item was NOT OBSERVABLE.
8. A text-editor guard for Ctrl+D, F2 and Alt+Left on the Target detail page. F2 with the caret in
   a notes box begins a rename today. Avalonia evaluates a `KeyBinding` before the focused
   control's key handler and without consulting `Handled`, and moving the three gestures into a
   guarded code-behind dispatch loses Alt+Left to the access-key path, so the fix needs Avalonia's
   Alt routing understood first.
9. An About-tab attribution line naming the SIL Open Font License and the two Atkinson families.
   `OFL.txt` ships as an Avalonia resource and is reachable by no user-facing path.
10. `RawHeaderPanel`'s `TextBlock.mono` re-declaring the spine's `FontFamily`, and the remaining
    comment and test-naming residue the fixer left with reasons in its report.
11. A true field-of-view scale bar, if the Q6 frame-field bar proves to be the wrong measure.
12. The carried HANDOFF 5.4 backlog, which Phase 12 did not touch.

`ColorAccentPressed` and the `Border.callout` lift were on this list in the first pass and are not
candidates: the phase fixer shipped both.

---

## Phase 13: Parity with the web application, first slice

Goal: the port gains the web application's filter colour behaviour (seeded palette with category
folding, a real picker, colours on ungrouped filters), the Target detail page's session pane
becomes three persisted collapsible sections with "Metrics over the night" first, the Nights
ledger collapses to a date strip, the frame table stops trimming file names and opens the
preview on a row click, the night strip's hover and click drive the frame list, the preview's
arrow keys work on open, both repositories are packed with repomix for reference, and a parity
audit of the web application against the port is written as the backlog for the phases that
follow. Added by user directive on 2026-09-17 ("essentially I want feature parity with the
docker galactilog"). Rulings R1 to R14, the task table, the acceptance bar and the build lock are
in `docs/superpowers/work/phase13/phase13-inputs.md`.

Status: DONE (f90a3ba)

| Task | Status | Notes |
|------|--------|-------|
| Repomix packs of the web clone and this port under `docs/reference/repomix/` (gitignored), `docs/reference/README.md` with the refresh commands, refresh step on the release checklist and HANDOFF 3.4. | DONE | Two packs: `galactilog-web.xml` (10.1 MB, 638 files, 3.88M tokens) and `galactilog-windows.xml` (18.1 MB, 1,020 files, 5.93M tokens), both gitignored. `docs/reference/README.md` carries the two refresh commands and a network note: this machine's IPv6 route is dead, so every `npx repomix` call needs `NODE_OPTIONS="--no-network-family-autoselection --dns-result-order=ipv4first"` or every registry fetch times out. Refresh step added to `docs/release-checklist.md` section 2 and to HANDOFF 3.4. No review needed (a generated pack and a docs-only change). |
| Parity audit: the web application against the port, page by page, as `work/phase13/parity-audit.md`. Report only; nothing from it is built this phase. | DONE | 418 lines, 0 dashes. 305 web items considered (213 from a prior web-Target-detail inventory, 92 enumerated directly): PRESENT 102, EXCLUDED 166, MISSING 10, PARTIAL 8, IN PROGRESS 9, Uncertain 10. Eighteen ranked gaps PAR-001 to PAR-018, topped by manual target creation, in-app contextual help, per-frame quality grading, multi-rig presentation and an AstroBin acquisition CSV export. Nothing built this phase (R11); the list and the reopened spec 19.1 deferral areas became the Phase 14 onward plan in `docs/parity-roadmap.md`. |
| Filter colour spine and picker: seeded palette and category folding in `AliasMap.FilterColor`, `Avalonia.Controls.ColorPicker` at 11.3.21, swatch-opens-picker for grouped and ungrouped filters, ungrouped colour stored as a one-name group. Verify: resolution order and folding tables, picker round trip, ungrouped write shape. | DONE | `GalactiLog.Core.Aliases.FilterCategory` (new): the web's category fold and the seven-entry seeded palette. `FilterColor.Resolve`: the one four-step resolution (stored intent, category default, alias category default, `#808080`), called by both `AliasMap.FilterColor` and `AliasGroupViewModel.ResolvedColor`. `FilterSetting.Color` becomes `string?` with no initializer; the pre-Phase-13 creation sites store no colour. `Avalonia.Controls.ColorPicker` 11.3.21 pinned, with one `StyleInclude` in `App.axaml`; both the grouped and ungrouped swatches are `Button Classes="quiet"` with a `ColorView` flyout. Review FIX-FIRST (P2 2, P3 5); the fix pass lifted the Settings swatch onto the one shared resolution (it had run a shorter, three-step copy) and fixed the ungrouped picker committing on every drag step instead of once on close. Re-ruled mid-phase (Task 3 review P3-5, reverses Q2's tail): a stored `#808080` is now treated as no colour stored, because every pre-Phase-13 build wrote it unasked, so a user who wants grey picks any grey but that exact value; `SaveAsync` never writes it back. 21 files, filtered 160/160 after the fix pass. |
| Session pane sections: "Metrics over the night" first and open, "Night detail" closed with a findings summary on its band, "Frames" open; `display.target_page` keys and the flipped `graph.session_chart_expanded` default; frame table height contract kept. Verify: fresh-profile defaults, persistence round trip, summary text, bounded table. | DONE | `TargetPageSettings` on `DisplaySettings.TargetPage` (`night_detail_expanded` false, `frames_expanded` true, `ledger_expanded` true); `GraphSettings.SessionChartExpanded` default flipped to true for a fresh profile only, no migration, then the user's B1 ruling of 2026-09-17 reversed the flip back to false; one new `DisplayColumnWriter.Write(Func<DisplaySettings, DisplaySettings>)` overload on the existing queued chain. `SessionPane.axaml` now five fixed rows with three chevron bands; the closed "Night detail" band's summary counts outlier frames, not findings (ruling Q19), so it cannot disagree with the row ink or the outlier filter. Review FIX-FIRST (P2 2, P3 3); the fix pass opened the Night detail section from the Notes toggle when it was closed (the box was otherwise unreachable) and added cases over the real writer overload. 15 files, filtered 386/386 after the fix pass. |
| Ledger collapse: 48 px date strip, persisted `ledger_expanded`, pane takes the width, the 1600 px breakpoint case moved with it. Verify: strip items, click switches the night, persistence, breakpoint case. | DONE | `TargetDetailViewModel.IsLedgerExpanded`, `CollapsedLedgerWidth` 48, `ToggleLedgerCommand`; `LedgerWidth` collapses to 48 at any page width, outranking the 1600 px breakpoint. `TargetDetailView.axaml` splits the ledger into `LedgerExpandedBody` and `LedgerStrip`; the strip's "All" row is a quiet button on `ToggleLedgerCommand` that reopens the ledger (ruling Q15) rather than a no-op label; both chevrons are drawn `Path` elements, not glyphs (ruling Q16). Review CLEAN (P3 4, carried to the fixer list); no fix pass needed. 7 files, filtered 204/204. One deviation the review accepted: the strip's date ink is `ColorTextSecondary`, the navigation rail's ink, rather than the expanded ledger's primary ink the brief prescribed; the verification agent reports what it sees. |
| Frame table and strip link: file name column fits the longest name, plain click selects and opens the preview, Ctrl and Shift extend, strip hover highlights and scrolls the row, strip click opens. Verify: width measure, click matrix, highlight set and cleared, hidden-row hover no-op. | DONE | `FrameTableViewModel.FileNameColumnWidth`, measured in the view from the whole loaded night (`FileNamesToMeasure`, projecting `_captureOrder`), floored at 240 with no ceiling, never resized by a filter or a sort. `PointerReleased` on the row `Border` with four guards (left button, no modifiers, no `Button` ancestor, resolved from the row's own `DataContext`) drives the click matrix; keyboard navigation and Ctrl+A open nothing. `HighlightedRow`, `HighlightFrameAt` and `NightStripViewModel.FrameHovered` drive the strip's hover link, scrolling the row into view. Review FIX-FIRST (P2 1, P3 7); the fix pass painted a transparent fill over the whole 72 px strip band, because Avalonia hit-tests a drawn visual against its own painted ink and the strip previously answered the pointer only within about a pixel of a tick line, and changed the strip's focus contract to a dynamic one (focusable only while a press lands on a tick) so a Phase 12 rule (a miss leaves focus on the frame table) still held. 48 new cases across five test files, filtered 282/282 after the fix pass. |
| Preview focus: focus on open so Left and Right fire at once, Up and Down aliases. Verify: headless case with no prior button click. | DONE | `Viewport` gains `Focusable="True"` and `IsTabStop="False"`, focused in `OnOpened` with `NavigationMethod.Unspecified`. Up and Down are handled in `OnKeyDown` beside the existing `H` and `0` cases, behind the same text-box-focused guard; Left and Right stay window `KeyBinding`s and still fire inside the header filter box, which this task did not fix (Phase 14 candidate, ruling Q22). Review FIX-FIRST (P2 2, P3 2), both P2s a comment and a report correction rather than a code change: Avalonia 11.3.21's headless harness routes an unfocused key press to the window regardless, so it cannot reproduce R10's own defect, and the fix's proof is the launched-app verification bar's item 6, not the suite. 3 files, filtered 76/76. |
| Docs: spec 5.8.2, 5.8.3, 12.4 and 12.7 amended; `DESIGN.md` gains the ledger strip and the section bands; HANDOFF, TRACKING, this file. Verify: zero em or en dashes. | DONE | Spec 5.8.2 (`target_page`), 5.8.3 (`session_chart_expanded` default and provenance), 5.8.4 (the resolution order, the seeded palette table, the `#808080`-is-unstored rule), 11.5 (focus on open, Up and Down), 12.4 (the three sections, the height contract's closed-Frames case, the collapsed ledger strip, the responsive rule's third state, the file name column, the click matrix, the night strip's pointer surface and focus contract), 12.7 (the Filters tab's swatch and picker) and 14.5 (the filter-colour resolution and the second colour-literal exception) amended. `DESIGN.md` sections 4 (the chevron band's five uses), 6 (the collapsed vertical strip as a pattern), 7 (the hover link, the transparent pointer surface, the dynamic focus contract) and 8 (the hero-tile refusal scoped to Target detail) amended. `HANDOFF.md`, `TRACKING.md` and this file recorded the phase, including the exact Phase 14A prompt in HANDOFF section 2 and the process findings from the shared-tree compile break, the build lock owner-name gap and the collision map's missed test file. Zero em or en dashes across every document touched (verified command in `work/phase13/task8-report.md`). |

Verification: `dotnet build -c Release` with 0 warnings and `dotnet test -c Release` green on a
clean copy, then the eight launched-app items in `phase13-inputs.md` section 3 against the
generated library, with the fixture and profile-folder hashes identical before and after.

Verification status: PASSED 2026-09-17 on a clean copy against `C:\tmp\p13-fixtures` (50 files, the Phase 12 recipe plus a 2025-03-01 night of L, R, G, B, Duoband and one 93-character name). Restore, build at 0 warnings and 4697 passed in one pass; `FileSafetyTest` green; a headless scan discovering, completing and failing 50, 50 and 0; all 8 acceptance items and all 22 launched-app steps PASS; fixture and profile-folder hashes identical before and after. One judgement-call blocker, B1: with Metrics open by default its 260 px chart pushed the Night detail band and the Frames band below the pane viewport at 1280x800 and consumed the wheel. The user ruled Metrics first but closed by default; the fix reverted the `session_chart_expanded` default, added a viewport case, and the re-run on a second clean copy passed at 4698 with the outlier summary on screen at rest and every section toggle surviving a night switch, a Back and reopen, and a relaunch. Residue and the carried findings are in `work/phase13/fixer-list.md` section 2 (items 18 to 23) and `work/phase13/verification-report.md`.

Phase 14 onward is planned in full, not as a candidate list here: `docs/parity-roadmap.md`,
approved by the user as a whole on 2026-09-17, lays out thirteen sessions (14A, 14B, 14C, 15A,
15B, 16 through 22) covering the parity audit's eighteen ranked gaps and the seven spec 19.1
deferral areas the user reopened (PHD2 guiding, mosaics, WBPP export, the analysis page, custom
columns, NINA and Stellarium with AstroBin, the sky viewer), at an estimated 94 to 118 agent-days
across three migrations. Each phase section there carries its own task table, spec work, data
model and verification bar, in the shape this document's own phase sections use, and each session
ends by handing the user the next session's prompt. The Phase 14 candidates list below is this
document's own record of what Phase 13 itself left open, in the same shape Phase 12's list used;
it is not the roadmap, which is the plan of record for everything past it.

This document ends at Phase 13 and is closed history. Phase 14A ran on 2026-09-18 and its record
is `docs/parity-roadmap.md` section "Phase 14A" and `docs/superpowers/work/phase14a/`; the carried
list below is superseded there by `work/phase14a/fixer-list.md` section 2, which restates each of
these entries with Phase 14A's effect on it.

1. `DisplayColumnWriter`'s name is now narrower than what it writes, since it carries the whole
   display document through its general overload, not only `display.columns`; renaming it to
   `DisplaySettingsWriter` touches `AppHost`, three view-models and their tests (ruling Q5).
2. The navigation rail's own collapse toggle is still a glyph `TextBlock`; the Nights ledger's
   collapse chevron is a drawn `Path`, and the rail was left alone (ruling Q16).
3. Left and Right on the preview modal still fire while the header filter `TextBox` has focus; R10
   asked only for the focus-on-open defect and the Up and Down aliases (ruling Q22).
4. Phase 12 candidate 7, keyboard focus after a frame-table re-projection, is still a candidate:
   Task 6's click handler and highlight seam do not touch where focus lands after `Project()`
   rebuilds `Rows` (ruling Q26).
5. The build lock protocol needs the holding agent's name written into the lock folder, so a
   release that finds the directory already gone can tell a stolen lock from the 20-minute stale
   rule firing against one that was still live.
6. `HitFraction` 0.004 on the night strip, about 4.7 px against a roughly 3 px tick pitch on a
   dense night; the verification agent watches for tint cycling now that the whole band is a
   pointer surface (Task 6 review coordinator item (b), carried from the Phase 12 list).
7. `CalendarHeatmap.cs` has the same ink-only pointer surface the night strip had before this
   phase's fix; the same transparent bounds fill applies (Task 6 review P2-1's sibling finding).
8. `SessionCardViewModel` mixes the field form and the partial-property form of
   `[ObservableProperty]` in one 1,100 line file; converging them waits for a phase that owns the
   file alone (Task 4 review P3-3).
9. Every carried item from Phase 12's own candidate list that Phase 13 did not touch (this
   document's Phase 12 section, "Phase 13 candidates recorded by this phase").
10. The full list, with the finding that raised each item, is `work/phase13/fixer-list.md`
    section 2.

---

## Deferred and out of scope

Per spec 19, and not to be added without a new decision:

- Deferred (spec 19.1): PHD2 guiding, mosaics, analysis page, WBPP export, custom columns,
  NINA and Stellarium integrations, API keys, Prometheus, backup and restore endpoint, sky viewer,
  filename-based target inference, fpack compressed FITS, light theme, any file deletion.
- Non-goals (spec 19.2): embedded browsers, authentication, any server or listening socket, Docker
  or PostgreSQL or Redis, importing from the web application's database, writing or moving or
  deleting any user file, downloading survey images, macOS and Linux builds, cloud sync and
  telemetry.

Known ceilings the spec records and this roadmap does not attempt to remove:

- No SQLite index can serve the `json_extract` group key or the header query builder. Measure
  before building a generated column (spec 5.2, 12.2).
- `word_similarity()` is not implemented; only `similarity()` is (spec 9.7).
- Resampling is Mitchell cubic, not Lanczos, and carries no exactness assertion (spec 11.2).
