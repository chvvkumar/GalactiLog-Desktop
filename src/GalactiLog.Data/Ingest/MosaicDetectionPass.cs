using System.Globalization;
using GalactiLog.Core.Mosaics;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.Data.Ingest;

/// <summary>What one detection run did (spec 7.7), for the job summary and the
/// <c>mosaic_detection_complete</c> event. <see cref="SkippedExisting"/> counts the suggestions
/// skipped because their name is a mosaic's or every triple is already covered;
/// <see cref="SkippedDismissed"/> those skipped by the dismissed-subset rule.</summary>
public sealed record MosaicDetectionResult(
    int Relabelled,
    int Backfilled,
    int SuggestionsWritten,
    int SkippedDismissed,
    int SkippedExisting);

/// <summary>
/// Spec 7.7's detection pass over the catalogue, in the <see cref="DuplicateDetector"/> shape:
/// step 0 relabels frames and backfills positions, then the candidates are gathered, grouped by
/// <see cref="MosaicDetection.Detect"/> and written through
/// <see cref="MosaicRepository.ReplacePending"/>. Database rows only: nothing here touches a file.
/// </summary>
/// <remarks>
/// Two passes never overlap (spec 7.7): a process-wide lock makes a second run wait for the one in
/// flight. Cancellation is checked between steps; step 0's writes are committed per step, and the
/// suggestion write is one transaction, so a cancelled or failed run leaves the previous list in
/// place.
/// </remarks>
public sealed class MosaicDetectionPass(string connectionString, DetectionSettings settings)
{
    /// <summary>The steps of spec 7.7's job, in order.</summary>
    public const int TotalSteps = 4;

    /// <summary>The <c>TotalSteps</c> of the terminal envelope a scan raises when the pass threw,
    /// the signal the App's job reads to end <c>failed</c> (the convention spec 10.4 states for
    /// <c>phd2_correlate</c>).</summary>
    public const int FailedEnvelopeTotalSteps = -1;

    private static readonly object Gate = new();

    /// <summary>The detection settings stored under <c>general</c> (spec 5.8.1).</summary>
    public static DetectionSettings SettingsFrom(GeneralSettings general)
        => new(general.MosaicKeywords, general.MosaicCampaignGapDays, general.MosaicPositionToleranceArcmin);

    /// <summary>The <c>panel_label</c> the token rule gives an <c>OBJECT</c>: <c>Panel n</c>, or
    /// null when it carries no token (spec 7.7). The one rule <c>ScanWriter</c> and step 0 share.</summary>
    public static string? LabelFor(string? objectName, IReadOnlyList<string> keywords)
        => PanelTokens.Match(objectName, keywords) is { } match ? PanelTokens.Label(match.Number) : null;

    /// <param name="report">(step, totalSteps, message), once per step; the coordinator forwards it
    /// into its <c>mosaic_detection</c> envelope or the on-demand caller's job.</param>
    public MosaicDetectionResult Run(Action<int, int, string> report, CancellationToken ct)
    {
        lock (Gate)
        {
            using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString, tracking: true));
            context.Database.OpenConnection();
            var connection = (SqliteConnection)context.Database.GetDbConnection();

            ct.ThrowIfCancellationRequested();
            report(1, TotalSteps, "Relabelling frames");
            var relabelled = Relabel(connection);

            ct.ThrowIfCancellationRequested();
            report(2, TotalSteps, "Filling positions");
            var backfilled = Backfill(connection);

            ct.ThrowIfCancellationRequested();
            report(3, TotalSteps, "Grouping candidates");
            var targets = Gather(connection);
            var repository = new MosaicRepository(new DatabaseConnectionString(connectionString));
            var existing = repository.ExistingNames();
            var dismissed = repository.DismissedSignatures();
            var covered = repository.CoveredTriples();
            var written = MosaicDetection.Detect(targets, settings, existing, dismissed, covered);

            // The skip counts, by difference: each skip of spec 7.7 step 4 is decided per
            // suggestion and independently of the others, and only the unique-name step (which
            // drops nothing) depends on order. ponytail: two extra Detect calls over the same
            // in-memory records; fold the counts into Core's Detect if a library ever makes this slow.
            var unfiltered = MosaicDetection.Detect(targets, settings, new HashSet<string>(), []).Count;
            var withoutDismissed = MosaicDetection.Detect(targets, settings, existing, [], covered).Count;

            ct.ThrowIfCancellationRequested();
            report(4, TotalSteps, "Writing suggestions");
            repository.ReplacePending(written);

            return new MosaicDetectionResult(
                relabelled, backfilled, written.Count,
                SkippedDismissed: withoutDismissed - written.Count,
                SkippedExisting: unfiltered - withoutDismissed);
        }
    }

    // OBJECT as text: json_extract yields a SQLite number for an unquoted numeric card.
    private const string ObjectExpression = "CAST(json_extract(raw_headers, '$.OBJECT') AS TEXT)";

    // Step 0 item 1 (ruling R6): one label per distinct (OBJECT, stored label) among LIGHT frames,
    // and one UPDATE per pair whose label moved, so an unchanged row is never written.
    private int Relabel(SqliteConnection connection)
    {
        var pairs = new List<(string? Object, string? Stored)>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText = $"SELECT {ObjectExpression}, panel_label FROM images WHERE {SqlFragments.LightFrameOnly} GROUP BY 1, 2";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                pairs.Add((SqlReaders.ReadText(reader, 0), SqlReaders.ReadText(reader, 1)));
            }
        }

        using var transaction = connection.BeginTransaction();
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
            $"""
            UPDATE images SET panel_label = $new
            WHERE {SqlFragments.LightFrameOnly} AND {ObjectExpression} IS $object AND panel_label IS $old
            """;
        var newLabel = update.Parameters.Add("$new", SqliteType.Text);
        var objectName = update.Parameters.Add("$object", SqliteType.Text);
        var oldLabel = update.Parameters.Add("$old", SqliteType.Text);

        var changed = 0;
        foreach (var (name, stored) in pairs)
        {
            var label = LabelFor(name, settings.Keywords);
            if (string.Equals(label, stored, StringComparison.Ordinal))
            {
                continue;
            }

            newLabel.Value = (object?)label ?? DBNull.Value;
            objectName.Value = (object?)name ?? DBNull.Value;
            oldLabel.Value = (object?)stored ?? DBNull.Value;
            changed += update.ExecuteNonQuery();
        }

        transaction.Commit();
        return changed;
    }

    // Step 0 item 2 (ruling R10): LIGHT rows with all three geometry columns null read their
    // headers through the spec 7.1 rules; a row that yields nothing stays null and is read again
    // next run (spec 7.7's stated ceiling).
    private static int Backfill(SqliteConnection connection)
    {
        var found = new List<(string Id, double? Ra, double? Dec, int? Width)>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText =
                $"""
                SELECT id, json_extract(raw_headers, '$.RA'), json_extract(raw_headers, '$.DEC'),
                       json_extract(raw_headers, '$.OBJCTRA'), json_extract(raw_headers, '$.OBJCTDEC'),
                       json_extract(raw_headers, '$.NAXIS1')
                FROM images
                WHERE {SqlFragments.LightFrameOnly} AND ra_deg IS NULL AND dec_deg IS NULL AND width_px IS NULL
                """;
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                var position = SkyCoordinates.FramePosition(Card(reader, 1), Card(reader, 2), Card(reader, 3), Card(reader, 4));
                var width = Width(reader.IsDBNull(5) ? null : reader.GetValue(5));
                if (position is not null || width is not null)
                {
                    found.Add((reader.GetString(0), position?.RaDeg, position?.DecDeg, width));
                }
            }
        }

        if (found.Count == 0)
        {
            return 0;
        }

        using var transaction = connection.BeginTransaction();
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE images SET ra_deg = $ra, dec_deg = $dec, width_px = $width WHERE id = $id";
        var ra = update.Parameters.Add("$ra", SqliteType.Real);
        var dec = update.Parameters.Add("$dec", SqliteType.Real);
        var widthPx = update.Parameters.Add("$width", SqliteType.Integer);
        var id = update.Parameters.Add("$id", SqliteType.Text);
        foreach (var row in found)
        {
            ra.Value = (object?)row.Ra ?? DBNull.Value;
            dec.Value = (object?)row.Dec ?? DBNull.Value;
            widthPx.Value = (object?)row.Width ?? DBNull.Value;
            id.Value = row.Id;
            update.ExecuteNonQuery();
        }

        transaction.Commit();
        return found.Count;
    }

    // A header value as the text MetadataExtractor's CardText gives the same card: json_extract
    // yields a SQLite number for an unquoted card.
    private static string? Card(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal) switch
        {
            long l => l.ToString(CultureInfo.InvariantCulture),
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            var other => other.ToString(),
        };

    // NAXIS1 only, a base-10 integer greater than zero, the extractor's rule (ruling R10).
    private static int? Width(object? value) => value switch
    {
        long l when l is > 0 and <= int.MaxValue => (int)l,
        string s when int.TryParse(s.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var w) && w > 0 => w,
        _ => null,
    };

    // Step 1: every target with no merged_into_id and its LIGHT frames that carry a label, in
    // capture_date then id order, which fixes the field-of-view frame (spec 7.7). A frame with no
    // label carries no token and can be no candidate's frame.
    private static List<DetectionTarget> Gather(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT t.id, t.primary_name, i.session_date, i.ra_deg, i.dec_deg, i.arcsec_per_pixel, i.width_px,
                   i.exposure_time, i.filter_used, CAST(json_extract(i.raw_headers, '$.OBJECT') AS TEXT)
            FROM targets t
            JOIN images i ON i.resolved_target_id = t.id
            WHERE t.merged_into_id IS NULL AND i.{SqlFragments.LightFrameOnly} AND i.panel_label IS NOT NULL
            ORDER BY t.id, i.capture_date, i.id
            """;

        var targets = new List<DetectionTarget>();
        var frames = new List<DetectionFrame>();
        Guid? current = null;
        var name = "";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetGuid(0);
            if (current != id)
            {
                if (current is { } previous) targets.Add(new DetectionTarget(previous, name, frames));
                (current, name, frames) = (id, reader.GetString(1), []);
            }

            frames.Add(new DetectionFrame(
                SqlReaders.ReadDate(reader, 2),
                SqlReaders.ReadNullableDouble(reader, 3),
                SqlReaders.ReadNullableDouble(reader, 4),
                SqlReaders.ReadNullableDouble(reader, 5),
                SqlReaders.ReadNullableInt(reader, 6),
                SqlReaders.ReadDouble(reader, 7),
                SqlReaders.ReadText(reader, 8),
                SqlReaders.ReadText(reader, 9)));
        }

        if (current is { } last) targets.Add(new DetectionTarget(last, name, frames));
        return targets;
    }
}
