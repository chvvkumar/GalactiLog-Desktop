using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.Data.Repositories;

/// <summary>
/// The one place an <c>activity_events</c> row is built (spec 5.12, 10.9). Every caller in
/// the solution goes through <see cref="Emit"/>: ScanWriter's per-record events, the
/// coordinator's scan lifecycle events, and any future out-of-scan event.
/// </summary>
/// <remarks>
/// Two shapes, because there are two kinds of caller (spec 10.9's last paragraph):
/// <list type="bullet">
/// <item><see cref="Emit"/> takes an already-open context, so a caller mid-scan writes the
/// event on the SAME SaveChanges batch as the record that produced it -- the event cannot
/// survive a rolled-back record, and cannot cost an extra round trip per frame.</item>
/// <item><see cref="EmitStandalone"/> opens and saves its own short-lived context, for a
/// caller with no scan in flight.</item>
/// </list>
/// No logger: this type returns counts and lets its caller (which has one) do the logging.
/// </remarks>
public sealed class ActivityRepository(string connectionString)
{
    /// <summary>
    /// Adds the event to <paramref name="context"/>'s change tracker and returns it. The
    /// caller saves on its own schedule; <c>Id</c> is only populated after that SaveChanges.
    /// </summary>
    public static Entities.ActivityEvent Emit(
        GalactiLogContext context, string category, string severity, string eventType,
        string message, object? details = null, Guid? targetId = null, int? parentId = null,
        int? durationMs = null)
    {
        var evt = new Entities.ActivityEvent
        {
            Timestamp = DateTime.UtcNow,
            Severity = severity,
            Category = category,
            EventType = eventType,
            Message = message,
            Details = details is null ? null : JsonSerializer.Serialize(details),
            TargetId = targetId,
            ParentId = parentId,
            DurationMs = durationMs,
        };
        context.ActivityEvents.Add(evt);
        return evt;
    }

    /// <summary>Emits and saves on a context of its own; returns the new row's id.</summary>
    public int EmitStandalone(
        string category, string severity, string eventType, string message,
        object? details = null, Guid? targetId = null, int? parentId = null, int? durationMs = null)
    {
        using var context = Open();
        var evt = Emit(context, category, severity, eventType, message, details, targetId, parentId, durationMs);
        context.SaveChanges();
        return evt.Id;
    }

    /// <summary>
    /// Spec 5.12: the activity log is pruned to <c>general.activity_retention_days</c> on
    /// application start and after every scan. Returns the number of rows deleted; when that
    /// is above zero it also records the <c>activity_pruned</c> housekeeping event itself, so
    /// its two callers (startup and post-scan) share one implementation rather than each
    /// repeating the same prune-then-emit pair.
    /// </summary>
    /// <remarks>
    /// A bulk <c>ExecuteDelete</c>, not a load-then-Remove: the whole point is to not
    /// materialize a retention window's worth of rows. Sub-events whose parent ages out in
    /// the same pass go with it through the <c>parent_id</c> FK's ON DELETE CASCADE, which
    /// SQLite applies itself (PRAGMA foreign_keys is ON, see PragmaConnectionInterceptor) --
    /// a scan sub-event is meaningless once its scan_started parent is gone.
    /// </remarks>
    public int PruneRetention(int retentionDays)
    {
        // A floor, not a validation error: SettingsStore already rejects anything outside
        // 1-3650, so this only catches a caller that computed the window itself. Zero or
        // negative would put the cutoff at or after "now" and wipe the entire log, including
        // the in-flight scan_started row every sub-event of the running scan hangs off.
        if (retentionDays < 1) return 0;

        using var context = Open();
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        var deleted = context.ActivityEvents.Where(e => e.Timestamp < cutoff).ExecuteDelete();
        if (deleted > 0)
        {
            Emit(context, "system", "info", "activity_pruned",
                $"Activity log pruned: {deleted} entr{(deleted == 1 ? "y" : "ies")} older than " +
                $"{retentionDays} days removed",
                new { deleted_count = deleted, retention_days = retentionDays });
            context.SaveChanges();
        }
        return deleted;
    }

    private GalactiLogContext Open()
        => new(GalactiLogContextOptions.Create(connectionString, tracking: true));
}
