using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GalactiLog.Core.Fits;
using GalactiLog.Core.Io;
using GalactiLog.Core.Metadata;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Xisf;
using GalactiLog.Data;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GalactiLog.Cli;

// design-spec 4.4 / 15. TryRun returns false only when args is empty, meaning "not a CLI
// invocation; start the GUI." For any non-empty args, it returns true and sets exitCode per
// spec 15's exit-code table, whether or not args[0] is a recognized verb (see
// scratchpad/phase1/questions.md Q1 for why the unknown-verb case is handled here rather than
// by Program.Main falling through to Avalonia).
//
// services is a Func, not an already-built IServiceProvider: building the host opens the
// database and runs migrations, which must not happen on the usage/unknown-verb/--help path.
public static class CliDispatcher
{
    private static readonly string[] KnownVerbs = { "scan", "resolve", "inspect", "dump-headers" };

    private const string UsageText =
        "Usage: galactilog <command> [options]\n\n" +
        "Commands:\n" +
        "  scan [<path>...]        Scan configured or given roots for new or changed files.\n" +
        "  resolve <name>          Resolve a target name against catalogs and online services.\n" +
        "  inspect <file>          Print extracted metadata for one FITS or XISF file.\n" +
        "  dump-headers <file>     Print every raw header card for one FITS or XISF file.\n\n" +
        "Global options:\n" +
        "  --json                  Emit machine-readable JSON instead of text.\n" +
        "  --quiet                 Suppress progress output.\n";

    // services is a factory, not a built provider: it must not be invoked until a known verb
    // has matched, so the usage/unknown-verb/--help path never builds the host (no database
    // open, no migration) just to print two lines and exit.
    public static bool TryRun(string[] args, Func<IServiceProvider> services, out int exitCode)
    {
        if (args.Length == 0)
        {
            exitCode = 0;
            return false;
        }

        var verb = args[0];
        if (Array.IndexOf(KnownVerbs, verb.ToLowerInvariant()) < 0)
        {
            Console.Error.Write(UsageText);
            exitCode = 2;
            return true;
        }

        var rest = args[1..];
        var json = Array.IndexOf(rest, "--json") >= 0;
        var quiet = Array.IndexOf(rest, "--quiet") >= 0;
        var positional = Array.FindAll(rest, a => a != "--json" && a != "--quiet");
        var verbLower = verb.ToLowerInvariant();

        // Only scan/resolve touch the database, so only they build the host (spec 15: "only
        // verbs that need the DB build the host"). inspect/dump-headers never call `services`
        // at all -- not just "don't use the result" -- so opening a file with either verb
        // never opens or migrates the database.
        IServiceProvider? resolvedServices = null;
        if (verbLower is "scan" or "resolve")
        {
            try
            {
                resolvedServices = services();
            }
            catch (AppDataRootUnavailableException ex)
            {
                // Phase 10 Task 9, spec 15's new exit code 6. Not a fallback and not exit 4: the
                // database was never reached, and falling back to the default location would
                // create a second, empty catalogue beside the user's own (spec 17.2). The same
                // message the GUI's message box shows, composed in one place.
                Console.Error.WriteLine(ex.UserMessage);
                exitCode = 6;
                return true;
            }
            catch (Exception ex)
            {
                // Host construction failed (e.g. the database could not be opened or
                // migrations failed): spec 15 exit code 4, not 70, which is reserved for a
                // verb body failure.
                Console.Error.WriteLine(ex.Message);
                exitCode = 4;
                return true;
            }
        }

        try
        {
            exitCode = verbLower switch
            {
                "scan" => RunScan(positional, json, quiet, resolvedServices!),
                "resolve" => RunResolve(positional, json, quiet, resolvedServices!),
                "inspect" => RunInspect(positional, json, quiet, null!),
                "dump-headers" => RunDumpHeaders(positional, json, quiet, null!),
                _ => throw new InvalidOperationException("Unreachable: verb already validated."),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            exitCode = 70;
        }

        return true;
    }

    // design-spec 15. Positional arguments are scan roots: given, they replace the
    // configured general.scan_roots for this run (ScanCoordinator's rootsOverride); absent,
    // every configured root is scanned. args here is already stripped of --json/--quiet by
    // TryRun.
    //
    // Exit codes (spec 15): 0 for a completed run INCLUDING one that found nothing new (scan
    // never reports "nothing to do" as an error); 2 for a usage error, which includes a scan
    // filter configuration that fails validation; 3 for a root that is not there; 4 for a
    // database error raised during the run; 5 for a cancelled run. Anything else falls
    // through to TryRun's own catch, which reports 70.
    private static int RunScan(string[] args, bool json, bool quiet, IServiceProvider services)
    {
        // Before anything resolves a service or opens the database: a bad root is a rejected
        // request, not a failed scan, so it must not produce a scan_runs row.
        if (!TryValidateScanRoots(args, out var rootError))
        {
            return rootError;
        }
        var rootsOverride = args.Length > 0 ? args : null;

        var coordinator = services.GetRequiredService<ScanCoordinator>();

        var interrupted = false;
        void OnCancelKey(object? sender, ConsoleCancelEventArgs e)
        {
            // Keep the process alive so the scan drains cooperatively (spec 10.5): the
            // default Ctrl+C behaviour would kill it mid-write.
            e.Cancel = true;
            interrupted = true;
            coordinator.Cancel();
        }

        // Progress lines are progress, so --quiet suppresses them; --json suppresses them
        // too, because stdout under --json is exactly one JSON object (spec 15). The run's
        // RESULT is never suppressed: --quiet means "no progress output", not "no output".
        var showProgress = !json && !quiet;
        void OnProgress(object? sender, ScanProgress p)
            => Console.WriteLine($"{p.Task} {p.Step}/{p.TotalSteps} {p.Message}");

        ScanRunOutcome outcome;
        Console.CancelKeyPress += OnCancelKey;
        if (showProgress)
        {
            coordinator.ProgressChanged += OnProgress;
        }
        try
        {
            // The CLI process has nothing else to do while the scan runs, so it blocks here.
            // Cancellation arrives through Cancel(), not through a token this frame owns.
            //
            // No ScanRunOptions (spec 10.3): the CLI run takes the stored scope and always leaves
            // the orphan cleanup override off, because an unattended run must not be the thing
            // that decides a storage volume is gone.
            outcome = coordinator.RunAsync(ScanTrigger.Cli, rootsOverride, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (ScanFilterValidationException ex)
        {
            // Configuration drift (an include/exclude path that no longer sits under any
            // scan root, a malformed name rule). Thrown before the scan_runs row exists, so
            // this is a rejected request like a bad argument, not a failed run: spec 15's
            // exit code 2, with the offending entry named.
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        catch (Exception ex) when (ex is SqliteException or DbUpdateException)
        {
            // A database write failed mid-run. The coordinator has already recorded the run
            // as failed (with error_text) and rethrown, so there is nothing left to do but
            // report it: spec 15's exit code 4, the same code a database that would not open
            // gets, rather than 70's "unhandled".
            Console.Error.WriteLine(ex.Message);
            return 4;
        }
        catch (OperationCanceledException)
        {
            // The coordinator records cancellation rather than rethrowing it, so this is a
            // belt-and-braces path (a token firing outside RunFullPipelineAsync's own catch);
            // it must still exit 5, not 70.
            coordinator.WaitForIdleAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            return 5;
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKey;
            if (showProgress)
            {
                coordinator.ProgressChanged -= OnProgress;
            }
        }

        // Unconditional: spec 15's --quiet suppresses progress, never the result.
        Console.WriteLine(json
            ? ScanRunJson(services, outcome)
            : $"Scan {outcome.State}: {outcome.Discovered} discovered, {outcome.NewFiles} new, " +
              $"{outcome.ChangedFiles} changed, {outcome.Completed} completed, {outcome.Failed} failed, " +
              $"{outcome.SkippedCalibration} calibration skipped, {outcome.Removed} removed, " +
              // "stored" rather than "ingested" deliberately: ScanVerbTests asserts that --quiet
              // prints no line containing ScanTaskNames.Ingest, which is the bare token "ingest",
              // and a summary clause reading "guide logs ingested" contains it. The JSON payload
              // carries the column's real name, phd2_ingested; this line is prose.
              $"{outcome.Phd2Found} guide logs found, {outcome.Phd2Ingested} guide logs stored, " +
              // The other three figures of the pass, so the line adds up on a pass that ran to
              // completion: found equals stored plus unchanged plus empty plus failed (spec 15).
              // A cancelled pass is the one state where it does not: found is the full candidate
              // count and the other four cover only the candidates the pass reached, which is why
              // the line is read after its own "Scan cancelled:" prefix. Without these three a
              // corpus with ten empty logs reads ten short with no visible reason. "unchanged"
              // rather than "skipped" because this same line already says "calibration skipped"
              // for frames, so a second "skipped" would read as the same axis.
              $"{outcome.Phd2SkippedUnchanged} guide logs unchanged, " +
              $"{outcome.Phd2Empty} guide logs empty, " +
              $"{outcome.Phd2Failed} guide logs failed, {outcome.Phd2Removed} guide logs removed.");

        if (outcome.State == "cancelled" || interrupted)
        {
            // Spec 10.5's drain budget: the exit code is not reported until the coordinator
            // is actually idle, so the process never leaves a scan mid-write. Already idle in
            // the common case, since RunAsync has returned.
            coordinator.WaitForIdleAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            return 5;
        }

        // Includes state "pending" (another scan was already running process-wide). Not
        // reachable from a fresh CLI process in normal use; reported, not treated as an error.
        return 0;
    }

    // Spec 15's exit codes 2 and 3 for the `scan` verb's positional roots. A relative path is
    // a usage error rather than something to resolve against the process's current directory:
    // the scan roots the settings store holds are absolute, and a root the caller did not
    // actually name is the wrong thing to walk.
    private static bool TryValidateScanRoots(string[] roots, out int exitCode)
    {
        foreach (var root in roots)
        {
            if (root.Length == 0 || !Path.IsPathFullyQualified(root))
            {
                Console.Error.WriteLine($"scan root must be an absolute directory path: {root}");
                Console.Error.Write(UsageText);
                exitCode = 2;
                return false;
            }

            if (!UserFiles.DirectoryExists(root))
            {
                Console.Error.WriteLine($"scan root not found: {root}");
                exitCode = 3;
                return false;
            }
        }

        exitCode = 0;
        return true;
    }

    // --json emits the run's `scan_runs` row itself (spec 15), read back after the run rather
    // than reassembled from the outcome record, so what the CLI prints is what was persisted.
    // The read goes through ScanRunRepository.Get, which opens the database read-only, rather
    // than a second copy of AppHost's connection-string expression (FIXER LIST 5).
    private static string ScanRunJson(IServiceProvider services, ScanRunOutcome outcome)
    {
        if (outcome.RunId is not { } runId)
        {
            // ScanRunOutcome.AlreadyRunning: no row was created for this invocation.
            return JsonSerializer.Serialize(new { id = (int?)null, state = outcome.State }, IndentedJsonOptions);
        }

        var run = services.GetRequiredService<ScanRunRepository>().Get(runId)!;

        return JsonSerializer.Serialize(new
        {
            id = run.Id,
            started_at = run.StartedAt,
            finished_at = run.FinishedAt,
            trigger = run.Trigger,
            state = run.State,
            discovered = run.Discovered,
            new_files = run.NewFiles,
            changed_files = run.ChangedFiles,
            completed = run.Completed,
            failed = run.Failed,
            skipped_calibration = run.SkippedCalibration,
            removed = run.Removed,
            // Spec 5.13's three guide-log counters, after `removed` and before `error_text`, in
            // the row's own column order. Zero when general.phd2_scan_enabled is off.
            phd2_found = run.Phd2Found,
            phd2_ingested = run.Phd2Ingested,
            phd2_failed = run.Phd2Failed,
            // THE THREE FIELDS OF THIS OBJECT THAT ARE NOT SCAN_RUNS COLUMNS. They come from the
            // in-memory pass result of THIS invocation, because spec 5.13 stores three of the
            // pass's six figures and not these (the stored run reconciles through its
            // phd2_pass_complete activity event instead). Without them phd2_found does not add up:
            // a log recorded `empty` and a log the delta skip passed over are neither ingested nor
            // failed. A reader fetching the same run through any other path sees no such fields,
            // which is the deliberate cost of adding no column (spec 15).
            phd2_skipped_unchanged = outcome.Phd2SkippedUnchanged,
            phd2_empty = outcome.Phd2Empty,
            phd2_removed = outcome.Phd2Removed,
            error_text = run.ErrorText,
        }, IndentedJsonOptions);
    }

    // design-spec 15: read-only with respect to targets. createIfMissing: false skips the
    // insert; dryRun: true additionally blocks the alias-append + SaveChanges on an existing
    // target that a plain createIfMissing: false linking step would otherwise still perform
    // (TargetResolver review fix, item 4) -- the CLI must write nothing at all to `targets`.
    // Resolver/simbad/sesame catalog_cache rows are still written either way; that is app
    // data, not target data. args here is already stripped of --json/--quiet by TryRun.
    private static int RunResolve(string[] args, bool json, bool quiet, IServiceProvider services)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: galactilog resolve <name>");
            return 2;
        }
        var name = args[0];

        var resolver = services.GetRequiredService<TargetResolver>();
        // CancellationToken.None: the verb resolves exactly one name and the process has no
        // other work to unwind, so there is nothing for a cancellation token to shorten here
        // (review ruling, item 14).
        var result = resolver.Resolve(name, createIfMissing: false, dryRun: true, ct: CancellationToken.None);

        // One spelling for every stage, shared with ScanWriter's target_created details, so a
        // stage cannot be recorded two ways in the same database (Task 7 review ruling). This
        // switch used to spell the six values out and would have printed "unresolved" for a
        // name the solar-system stage did resolve.
        var stageText = TargetResolver.StageName(result.Stage);

        if (result.Identity is null || result.Stage == TargetResolver.ResolutionStage.Unresolved)
        {
            // A miss is a result, not progress: unconditional and on stdout, matching the
            // --json branch below (review fix). --quiet suppresses progress output only
            // (spec 15); it never suppresses a verb's actual result.
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { source = stageText }, IndentedJsonOptions));
            }
            else
            {
                Console.WriteLine($"unresolved: {name}");
            }
            return 1;
        }

        var identity = result.Identity;
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                primary_name = identity.PrimaryName,
                catalog_id = identity.CatalogId,
                common_name = identity.CommonName,
                ra = identity.Ra,
                dec = identity.Dec,
                object_type = identity.ObjectType,
                aliases = identity.Aliases,
                source = stageText,
            }, IndentedJsonOptions));
        }
        else
        {
            Console.WriteLine($"primary_name = {identity.PrimaryName}");
            Console.WriteLine($"catalog_id = {identity.CatalogId}");
            Console.WriteLine($"common_name = {identity.CommonName}");
            Console.WriteLine($"ra = {Convert.ToString(identity.Ra, CultureInfo.InvariantCulture)}");
            Console.WriteLine($"dec = {Convert.ToString(identity.Dec, CultureInfo.InvariantCulture)}");
            Console.WriteLine($"object_type = {identity.ObjectType}");
            Console.WriteLine($"aliases = {string.Join(", ", identity.Aliases)}");
            Console.WriteLine($"source = {stageText}");
        }

        return 0;
    }

    // File-shape check for dump-headers (spec 15). The extension table itself lives in
    // FrameReader (spec 10.2) so the CLI and Phase 4's scan share one list. inspect does not
    // call this at all: FrameReader.TryRead performs the same two checks internally.
    // Neither verb needs the host, so `services` is accepted but never used below.
    private static bool TryResolveFormat(string path, out FrameReader.FrameFormat format, out string? error)
    {
        format = FrameReader.FrameFormat.Unsupported;
        if (!UserFiles.Exists(path))
        {
            error = $"file not found: {path}";
            return false;
        }

        format = FrameReader.FormatOf(path);
        if (format == FrameReader.FrameFormat.Unsupported)
        {
            error = $"unsupported file extension: {Path.GetExtension(path)}";
            return false;
        }

        error = null;
        return true;
    }

    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    private static int RunInspect(string[] args, bool json, bool quiet, IServiceProvider services)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: galactilog inspect <file>");
            return 2;
        }
        var path = args[0];

        // One call into the shared spine: existence, extension, header read, extraction and
        // best-effort CSV backfill all happen there, and every failure comes back as a
        // reason string rather than an exception (spec 15 exit code 3 for all of them). A
        // throwaway NinaCsvReader is fine for a single file; Phase 4's scan shares one
        // instance across a run.
        if (!FrameReader.TryRead(path, new NinaCsvReader(), out var extraction, out var rejectionReason))
        {
            Console.Error.WriteLine(rejectionReason);
            return 3;
        }

        if (!quiet)
        {
            foreach (var warning in extraction.Warnings)
            {
                Console.Error.WriteLine(warning);
            }
        }

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(extraction.Metadata, IndentedJsonOptions));
        }
        else
        {
            foreach (var property in typeof(ExtractedMetadata).GetProperties())
            {
                if (property.Name is nameof(ExtractedMetadata.RawHeaders) or nameof(ExtractedMetadata.Provenance))
                {
                    continue;
                }
                var jsonName = property.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name;
                var value = property.GetValue(extraction.Metadata);
                // Derived fields (eccentricity_source, guiding_rms_source) carry a value but
                // never a provenance entry of their own. Skipping on a provenance miss hid
                // them entirely; only a null value is skipped now, and a field with no
                // provenance prints with no suffix.
                if (!extraction.Metadata.Provenance.TryGetValue(jsonName, out var source))
                {
                    if (value is null)
                    {
                        continue;
                    }
                    Console.WriteLine($"{jsonName} = {Convert.ToString(value, CultureInfo.InvariantCulture)}");
                    continue;
                }
                Console.WriteLine($"{jsonName} = {Convert.ToString(value, CultureInfo.InvariantCulture)}  ({source})");
            }
        }

        return 0;
    }

    private static int RunDumpHeaders(string[] args, bool json, bool quiet, IServiceProvider services)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: galactilog dump-headers <file>");
            return 2;
        }
        var path = args[0];
        if (!TryResolveFormat(path, out var format, out var formatError))
        {
            Console.Error.WriteLine(formatError);
            return 3;
        }

        try
        {
            using var stream = UserFiles.OpenRead(path);

            if (format == FrameReader.FrameFormat.Fits)
            {
                var header = FitsHeaderReader.Read(stream);
                if (!header.Accepted)
                {
                    Console.Error.WriteLine(header.RejectionReason);
                    return 3;
                }
                if (json)
                {
                    var rawHeaders = FitsHeaderReader.BuildRawHeaders(header.Cards);
                    Console.WriteLine(rawHeaders.ToJsonString(IndentedJsonOptions));
                }
                else
                {
                    // One line per CARD, in file order -- deliberately not the deduplicated
                    // raw_headers object, so a repeated keyword (COMMENT/HISTORY, or a
                    // duplicate scalar key) shows every occurrence, matching spec 15's "one
                    // KEY = value line per card, in file order".
                    foreach (var card in header.Cards)
                    {
                        Console.WriteLine($"{card.Keyword} = {Convert.ToString(card.Value, CultureInfo.InvariantCulture)}");
                    }
                }
            }
            else
            {
                var header = XisfHeaderReader.Read(stream);
                if (!header.Accepted)
                {
                    Console.Error.WriteLine(header.RejectionReason);
                    return 3;
                }
                if (json)
                {
                    // Same builder MetadataExtractor.FromXisf uses, so the two can never
                    // disagree about what raw_headers contains.
                    Console.WriteLine(XisfHeaderReader.BuildRawHeaders(header).ToJsonString(IndentedJsonOptions));
                }
                else
                {
                    foreach (var (key, value) in header.FitsKeywords) Console.WriteLine($"{key} = {value}");
                    foreach (var (key, value) in header.Properties) Console.WriteLine($"{key} = {value}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // File exists but couldn't actually be read (locked, permission denied, etc.):
            // spec 15 exit code 3 ("not readable"), not the unhandled-exception 70.
            Console.Error.WriteLine(ex.Message);
            return 3;
        }

        return 0;
    }
}
