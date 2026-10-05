using System.Globalization;
using GalactiLog.Core.Wbpp;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.Data.Queries;

/// <summary>
/// One page open's worth of WBPP export paths (core-shapes.md section 5): the selected nights'
/// frame paths, and the contamination index built over the whole catalogue.
/// </summary>
/// <param name="Nights">One entry per requested night, in the requested order, an empty list and
/// all, so a caller never has to tell an absent key from a night with no frames. Every path is
/// already <see cref="Path.GetFullPath"/> normalised.</param>
/// <param name="Catalogue">The built index, never a row list: the catalogue statement's rows are
/// streamed into <c>ContaminationIndex.Build</c> as the reader walks them, so a 200,000 frame
/// library never materialises 200,000 records for a caller to hold (seam review finding 14).
/// </param>
public sealed record WbppPaths(
    IReadOnlyDictionary<DateOnly, IReadOnlyList<WbppFramePath>> Nights,
    ContaminationIndex Catalogue);

/// <summary>
/// The one read the WBPP export page issues (spec 12.13, W3), port of <c>_fetch_session_paths</c>
/// and <c>_fetch_all_paths_for_contamination</c> in <c>backend/app/api/wbpp.py</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two statements in one round trip over a short-lived <see cref="GalactiLogContext"/> so
/// <see cref="PragmaConnectionInterceptor"/> stays in the path, projected straight into the
/// records with nothing tracked and no entity materialised, which is the shape
/// <see cref="SessionDetailQuery"/> and <c>DiscoveredNamesQuery</c> already use. The group is
/// named by <see cref="SqlFragments.GroupScope"/>, so the export page can never describe a group
/// the dashboard does not produce.
/// </para>
/// <para>
/// Spec 5.1, single writer: this is a read. No mutation verb appears in this file, and the only
/// <c>System.IO</c> member it names is <see cref="Path.GetFullPath"/>, which is pure string work
/// and touches no disk. Nothing in this phase opens a user file.
/// </para>
/// <para>
/// W3: issued once per page open, never per night. The Python rebuilds its occupant map inside
/// every session's call; the port builds one index here and passes it to every
/// <c>FolderLevels.ForSession</c>. Same answer, one pass.
/// </para>
/// </remarks>
/// <param name="connectionString">The library database.</param>
public sealed class WbppPathsQuery(DatabaseConnectionString connectionString)
{
    /// <summary>
    /// Reads one target's selected nights and the whole catalogue in one round trip.
    /// </summary>
    /// <param name="groupKey">A <c>TargetRow.GroupKey</c>, the same value
    /// <see cref="SessionDetailQuery.Get"/> takes.</param>
    /// <param name="nights">The nights the page has selected. The returned
    /// <see cref="WbppPaths.Nights"/> carries one entry per night in this order; a repeated night
    /// yields one entry. An empty list yields an empty <see cref="WbppPaths.Nights"/> and a
    /// catalogue index that is still built in full.</param>
    /// <param name="scanRoots">The page's <c>GeneralSettings.ScanRoots</c>, passed through
    /// unchanged, in any spelling, because the index needs them. This query reads no settings store
    /// of its own. Normalising, trimming and dropping a blank root is
    /// <c>ContaminationIndex.Build</c>'s, which is the one home for it (review P2-3); this file
    /// normalises stored file paths only.</param>
    /// <param name="cancellationToken">Observed between the two statements and every few thousand
    /// streamed catalogue rows, so a page closed mid-load stops reading. A cancelled token throws
    /// <see cref="OperationCanceledException"/> and the connection is disposed on the way out.</param>
    /// <remarks>
    /// <para>
    /// Synchronous, and on a large library it takes on the order of seconds: statement B is a full
    /// scan of <c>images</c> that ruling R4 makes unavoidable, and the index is built as it streams.
    /// It must never be called on the UI thread.
    /// </para>
    /// </remarks>
    public WbppPaths Read(
        string groupKey,
        IReadOnlyList<DateOnly> nights,
        IReadOnlyList<string> scanRoots,
        CancellationToken cancellationToken = default)
    {
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString.Value));
        context.Database.OpenConnection();
        var connection = (SqliteConnection)context.Database.GetDbConnection();

        var parameters = new SqlParameters();
        var scope = SqlFragments.GroupScope(groupKey, parameters.Add);

        // Spec 5.1 stores session_date as an invariant yyyy-MM-dd string, and every other query
        // binds it the same way. Binding a DateOnly would let the provider choose a form. The night
        // list is bound, never interpolated. An empty selection writes "IN ()", which SQLite
        // documents as an always-false empty list, so the page can open with nothing selected and
        // still get its catalogue index; a case pins that.
        var nightList = string.Join(
            ", ",
            nights.Select(night => parameters.Add(night.ToString(SqlReaders.DateFormat, CultureInfo.InvariantCulture))));

        using var command = connection.CreateCommand();

        // Statement B has no WHERE clause and that is ruling R4, not an omission:
        // SqlFragments.LightFrameOnly is deliberately NOT composed into it. The Python filters to
        // LIGHT rows with a session date because contamination is its only consumer; the port's
        // size figure is the whole folder's, so every images row is returned and the two consumers
        // filter in C#. The occupant map takes IsLight and a non-null Night; the size index takes
        // everything, and needs only file_path and file_size from a row that can never be an
        // occupant.
        //
        // ponytail: statement B is a full table scan of images and no index can serve it, because it
        // has no WHERE and selects six columns no index covers. Ceiling, measured at the Phase 16
        // gate on a 200,000 row library whose rows carry a realistic 3,126 byte raw_headers, 851 MiB
        // on disk: 3,640 ms warm for this read plus three ForSession calls, against 4,416 ms with
        // the two CASE guards below removed. Accepted for this phase: the read is issued once per
        // page open, off the UI thread, behind a loading line, and the Statistics aggregate already
        // reads every LIGHT row by an accepted full scan. Upgrade path, if a real library ever makes
        // it felt: a covering index on
        // images(file_path, session_date, image_type, file_size, resolved_target_id), which would
        // make this an index-only scan for every row except an unresolved dated LIGHT one, whose
        // group key needs raw_headers and which therefore still visits the table.
        //
        // The two CASE guards are load-bearing (seam review finding 14).
        // SqlFragments.GroupKeyExpression carries json_extract(i.raw_headers, '$.OBJECT'), and
        // raw_headers is the largest column in the table. SQLite's coalesce short-circuits, so the
        // extract already runs only for a row with a null resolved_target_id, but on a mostly
        // unresolved library that is still every calibration and undated row. The guard makes the
        // expensive column unreachable for exactly the rows whose key nothing reads.
        command.CommandText =
            $"""
            SELECT i.id, i.file_path, i.file_size, i.session_date
            FROM images i
            WHERE i.{SqlFragments.LightFrameOnly}
              AND {scope}
              AND i.session_date IN ({nightList});

            SELECT i.file_path,
                   CASE WHEN i.{SqlFragments.LightFrameOnly} AND i.session_date IS NOT NULL
                        THEN {SqlFragments.GroupKeyExpression} END AS k,
                   CASE WHEN i.{SqlFragments.LightFrameOnly} AND i.session_date IS NOT NULL
                        THEN coalesce(t.primary_name, '') END,
                   i.session_date, i.image_type, i.file_size
            FROM images i LEFT JOIN targets t ON t.id = i.resolved_target_id;
            """;
        parameters.ApplyTo(command);

        var lists = new Dictionary<DateOnly, List<WbppFramePath>>();
        foreach (var night in nights)
        {
            lists.TryAdd(night, []);
        }

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            // A row reaches here only through the IN predicate, so its session_date is one of the
            // requested nights and the lookup cannot miss.
            var night = SqlReaders.ReadDate(reader, 3)!.Value;
            lists[night].Add(new WbppFramePath(
                reader.GetGuid(0),
                Path.GetFullPath(reader.GetString(1)),
                ReadNullableLong(reader, 2)));
        }

        // Insertion ordered, because the contract is "in the requested order" and a plain
        // Dictionary promises no order at all.
        var byNight = new OrderedDictionary<DateOnly, IReadOnlyList<WbppFramePath>>(nights.Count);
        foreach (var night in nights)
        {
            byNight.TryAdd(night, lists[night]);
        }

        // Between the statements: the nights are answered and the full scan has not started, which
        // is the cheapest point at which a closed page can stop.
        cancellationToken.ThrowIfCancellationRequested();

        // The catalogue rows are handed to Build as an iterator over the live reader, so the list
        // of every catalogue row never exists. The roots go through untouched: normalising them is
        // Build's (review P2-3).
        reader.NextResult();
        var catalogue = ContaminationIndex.Build(scanRoots, CatalogueRows(reader, cancellationToken));

        return new WbppPaths(byNight, catalogue);
    }

    /// <summary>One <c>WbppCataloguePath</c> per catalogue row, yielded as the reader walks it.
    /// Nothing outside this method and <c>ContaminationIndex.Build</c> ever holds one. The token is
    /// observed every <see cref="CancellationInterval"/> rows, which is the only point inside the
    /// full scan where a closed page can stop.</summary>
    private static IEnumerable<WbppCataloguePath> CatalogueRows(
        SqliteDataReader reader,
        CancellationToken cancellationToken)
    {
        var row = 0;
        while (reader.Read())
        {
            if (++row % CancellationInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            // Both guarded columns are NULL for a row that can never be an occupant, so they are
            // read through SqlReaders.ReadText, which answers null instead of throwing the way
            // GetString would. Neither can come back as a number: column 1 is the whole coalesce
            // over a concatenation and column 2 the whole coalesce over primary_name, and SQLite's
            // || and coalesce over TEXT arguments yield TEXT (review P3).
            var key = SqlReaders.ReadText(reader, 1) ?? "";
            var name = SqlReaders.ReadText(reader, 2) ?? "";

            yield return new WbppCataloguePath(
                Path.GetFullPath(reader.GetString(0)),
                key,
                // The display name is targets.primary_name when there is one, else the inverse of
                // the group key expression's own 'obj:' concatenation.
                name.Length == 0 ? SqlFragments.DisplayNameOf(key) : name,
                SqlReaders.ReadDate(reader, 3),
                // Spec 7.5: image_type is stored trimmed and upper cased, so an exact match is the
                // whole rule, exactly as SqlFragments.LightFrameOnly compares it in SQL.
                string.Equals(SqlReaders.ReadText(reader, 4), LightFrameType, StringComparison.Ordinal),
                ReadNullableLong(reader, 5));
        }
    }

    private const string LightFrameType = "LIGHT";

    /// <summary>How often the streamed scan observes the cancellation token. Every few thousand
    /// rows: often enough that a closed page stops within a few milliseconds of a 200,000 row
    /// scan, rare enough to cost nothing.</summary>
    private const int CancellationInterval = 4096;

    /// <summary>The one 64 bit column either statement selects. <see cref="SqlReaders"/> carries
    /// no long read and this phase edits no shared file, so the conversion lives here; a second
    /// caller is what would move it.</summary>
    private static long? ReadNullableLong(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
}
