using GalactiLog.Data.Repositories;

namespace GalactiLog.Data.Ingest;

/// <summary>
/// The two values spec 10.9 allows in <c>phd2_correlation_complete.trigger</c> and
/// <c>phd2_correlation_failed.trigger</c>, declared once so the scan path and the out-of-scan
/// re-run cannot spell the same vocabulary two ways (spec 12's "no second vocabulary" sentence).
/// </summary>
public static class Phd2CorrelationTriggers
{
    /// <summary>The correlation that runs inside a scan, spec 10.3 step 5 item 5.</summary>
    public const string Scan = "scan";

    /// <summary>The out-of-scan re-run a settings save dispatches, spec 12.7. It names a settings
    /// change and not a profile map change because the trigger covers the observer zone and site
    /// too, which are inputs of spec 7.6's resolution order exactly as the profile map is
    /// (<see cref="SettingsStore.Phd2GuidingInputsChanged"/> is what fires it).</summary>
    public const string SettingsChange = "settings_change";
}

/// <summary>
/// Spec 10.9's correlation-time activity rows, written once for both triggers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this type exists.</b> The scan path and the out-of-scan re-run each had their own copy
/// of the same four event types, the same four severities, the same four details shapes and the
/// same complete-message sentence, differing only in the trigger literal and in whether a
/// <c>parent_id</c> was carried, and the message existed a third time as a job summary. That is
/// the second occurrence of one pattern, so the spine goes in here (design-lessons rule 1) and
/// neither caller holds event code of its own. Spec 10.9 declares one vocabulary; there is now one
/// implementation of it.
/// </para>
/// <para>
/// <b>Nothing here can fail a scan.</b> Every write is wrapped: by the time these rows are written
/// the guiding values are already committed, and spec 10.9's own sentence is that a failure to
/// write one of them is logged and never fails the scan. <c>Phd2Ingest.EmitOrphanEvents</c> is the
/// deliberate opposite and stays so: that one guards a delete.
/// </para>
/// <para>
/// THIS TYPE PERFORMS NO FILESYSTEM ACCESS. It writes <c>activity_events</c> rows and nothing else.
/// </para>
/// </remarks>
public static class Phd2CorrelationEvents
{
    /// <summary>Spec 10.9: a guiding section carrying no equipment profile is named this in every
    /// profile list. Declared here because both the guide-log pass and the correlation's own
    /// warnings print it, and two spellings would put two different names in one feed. The test
    /// against it is <c>string.IsNullOrEmpty</c> and not <c>is null</c>: an ASIAIR header never
    /// produces the empty string, but a stored column filled with <c>?? ""</c> can.</summary>
    public const string NoProfileLabel = "(no equipment profile)";

    /// <summary>Spec 10.9's <c>phd2_timezone_unset</c> names the setting the reader has to go and
    /// fill in.</summary>
    public const string ProfileMapSetting = "phd2_profile_map";

    /// <summary>
    /// The <c>TotalSteps</c> a <c>phd2_correlate</c> envelope carries when the phase it describes
    /// FAILED, which is how the Data layer tells the App layer's job seam that the registered job
    /// must end failed rather than succeeded (spec 10.9's "the registered <c>phd2_correlate</c> job
    /// ends with outcome failed, carrying the same reason as its one-line summary").
    /// </summary>
    /// <remarks>
    /// The progress envelope is the only channel between the two: <c>ScanCoordinator</c> lives in
    /// this assembly and <c>ScanStatusService</c>, which owns the job, is in <c>GalactiLog.App</c>,
    /// which this assembly must not reference (spec 4.2). A negative total is inert everywhere else
    /// that reads one: <c>ScanProgress.Percent</c> already returns 0 for any total that is not
    /// positive, and every determinate-percent test in the App layer is <c>TotalSteps &gt; 0</c>.
    /// </remarks>
    public const int FailedEnvelopeTotalSteps = -1;

    private const string Category = "scan";

    /// <summary>
    /// Writes spec 10.9's <c>phd2_correlation_complete</c> and whichever of its three warnings the
    /// result earned, on one short-lived tracking context with one <c>SaveChanges</c>.
    /// </summary>
    /// <param name="connectionString">The library database.</param>
    /// <param name="result">The pass's own record. Every figure is read off it and nothing is
    /// recomputed: a figure derived twice is a figure that can disagree with itself.</param>
    /// <param name="trigger">One of <see cref="Phd2CorrelationTriggers"/>.</param>
    /// <param name="parentId">The run's <c>scan_started</c> event id inside a scan, null outside
    /// one.</param>
    /// <param name="ingest">The guide-log pass of the same scan, or null out of a scan. Spec 10.9
    /// says <c>phd2_timezone_unset</c> and <c>phd2_pixel_scale_missing</c> are raised once per
    /// pass, and a scan runs two passes over one corpus, so the correlation names only the profiles
    /// the ingest did not already name and says nothing when none is left. Ordinal, to match the
    /// sorted ordinal lists both sides build.</param>
    /// <param name="warn">Where a failure to write the rows goes.</param>
    public static void Emit(
        string connectionString,
        Phd2CorrelationResult result,
        string trigger,
        int? parentId,
        Phd2PassResult? ingest = null,
        Action<string>? warn = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        Write(connectionString, warn, context =>
        {
            ActivityRepository.Emit(
                context, Category, "info", "phd2_correlation_complete", Describe(result),
                new
                {
                    nights = result.Nights,
                    frames_considered = result.FramesConsidered,
                    filled = result.Filled,
                    cleared = result.Cleared,
                    below_gate = result.BelowGate,
                    trigger,
                },
                parentId: parentId);

            if (result.UnattributedProfiles.Count > 0)
            {
                ActivityRepository.Emit(
                    context, Category, "warning", "phd2_correlation_unattributed",
                    $"Guiding on {result.UnattributedNights} night" +
                    $"{(result.UnattributedNights == 1 ? " was" : "s was")} attributed to no " +
                    "telescope: " + string.Join(", ", result.UnattributedProfiles),
                    new { profiles = result.UnattributedProfiles, nights = result.UnattributedNights },
                    parentId: parentId);
            }

            var timezoneUnset = NotAlreadyNamed(result.TimezoneUnsetProfiles, ingest?.TimezoneUnsetProfiles);
            if (timezoneUnset.Count > 0)
            {
                ActivityRepository.Emit(
                    context, Category, "warning", "phd2_timezone_unset",
                    "Guiding sessions resolved to no timezone and were not correlated: " +
                    string.Join(", ", timezoneUnset),
                    new { profiles = timezoneUnset, setting = ProfileMapSetting },
                    parentId: parentId);
            }

            var noPixelScale =
                NotAlreadyNamed(result.PixelScaleMissingProfiles, ingest?.PixelScaleMissingProfiles);
            if (noPixelScale.Count > 0)
            {
                ActivityRepository.Emit(
                    context, Category, "warning", "phd2_pixel_scale_missing",
                    "Guiding sessions carried no pixel scale and cannot be converted to " +
                    "arcseconds: " + string.Join(", ", noPixelScale),
                    // Spec 10.9's row also names `session_count`, which is the guide-log pass's
                    // member alone: Phd2CorrelationResult does not carry it and this path must not
                    // recompute it.
                    new { profiles = noPixelScale },
                    parentId: parentId);
            }
        });
    }

    /// <summary>
    /// Spec 10.9's <c>phd2_correlation_failed</c>: the correlation, or the session time re-derive
    /// that begins it, threw. Exactly one of this row and <c>phd2_correlation_complete</c> is
    /// written per pass that ran to an end, and a cancelled pass writes neither.
    /// </summary>
    /// <param name="reason">The exception's message.</param>
    public static void EmitFailed(
        string connectionString, string trigger, string reason, int? parentId,
        Action<string>? warn = null)
        => Write(connectionString, warn, context => ActivityRepository.Emit(
            context, Category, "error", "phd2_correlation_failed", FailedMessage(reason),
            new { trigger, reason },
            parentId: parentId));

    /// <summary>One sentence for the <c>phd2_correlation_complete</c> message, the job's
    /// recent-list summary and the pass's closing progress line, so the flyout and the activity
    /// feed cannot disagree.</summary>
    public static string Describe(Phd2CorrelationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return $"PHD2 correlation filled {result.Filled} frame guiding value" +
            $"{(result.Filled == 1 ? "" : "s")} over {result.Nights} night" +
            $"{(result.Nights == 1 ? "" : "s")}";
    }

    /// <summary>The one sentence a failed pass reports, in the <c>phd2_correlation_failed</c> row,
    /// in the terminal envelope carrying <see cref="FailedEnvelopeTotalSteps"/> and in the job's
    /// summary, so the feed, the status bar and the flyout cannot disagree.</summary>
    public static string FailedMessage(string reason)
        => "The PHD2 guiding correlation failed: " + reason;

    private static IReadOnlyList<string> NotAlreadyNamed(
        IReadOnlyList<string> profiles, IReadOnlyList<string>? alreadyNamed)
        => alreadyNamed is null or { Count: 0 }
            ? profiles
            : [.. profiles.Where(profile => !alreadyNamed.Contains(profile, StringComparer.Ordinal))];

    private static void Write(string connectionString, Action<string>? warn, Action<GalactiLogContext> emit)
    {
        try
        {
            using var context = new GalactiLogContext(
                GalactiLogContextOptions.Create(connectionString, tracking: true));
            emit(context);
            context.SaveChanges();
        }
        catch (Exception ex)
        {
            // The guiding values this row describes are already committed, and spec 10.9 says
            // plainly that losing the note about them is not worth losing the work. The scan's own
            // terminal row does the same for the same reason.
            warn?.Invoke($"Could not write the PHD2 correlation activity events: {ex.Message}");
        }
    }
}
