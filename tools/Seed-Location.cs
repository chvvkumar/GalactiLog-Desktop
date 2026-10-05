#!/usr/bin/env dotnet
#:package Microsoft.Data.Sqlite@10.*
#:package GeoTimeZone@5.*
#:property ManagePackageVersionsCentrally=false

// Seeds observer_latitude, observer_longitude and observer_timezone in user_settings.general
// from a GPS receiver's status endpoint. Run by tools/Build-GalactiLog.ps1 -SeedLocation.
//
//   dotnet run tools/Seed-Location.cs -- <galactilog.db> <status url>
//
// The endpoint returns JSON with "lat", "lon" and "fixStatus" (ESP GPS /api/status). The time
// zone is looked up offline from the coordinates (GeoTimeZone, IANA id) and converted to the
// Windows id the Location tab lists. The database must already carry the schema: the script
// runs `GalactiLog.exe scan` first. Every other general setting is left as stored.

using System.Text.Json;
using GeoTimeZone;
using Microsoft.Data.Sqlite;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: Seed-Location.cs <galactilog.db> <status url>");
    return 2;
}

var (dbPath, url) = (args[0], args[1]);
if (!File.Exists(dbPath))
{
    Console.Error.WriteLine($"Database not found: {dbPath}");
    return 3;
}

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
using var status = JsonDocument.Parse(await http.GetStringAsync(url));
var root = status.RootElement;
var fix = root.TryGetProperty("fixStatus", out var f) ? f.GetString() ?? "" : "";
var lat = root.GetProperty("lat").GetDouble();
var lon = root.GetProperty("lon").GetDouble();
if (!fix.Contains("Fix", StringComparison.OrdinalIgnoreCase) || (lat == 0 && lon == 0))
{
    Console.Error.WriteLine($"No GPS fix (fixStatus='{fix}', lat={lat}, lon={lon}).");
    return 4;
}

var iana = TimeZoneLookup.GetTimeZone(lat, lon).Result;
var timezone = TimeZoneInfo.TryConvertIanaIdToWindowsId(iana, out var windowsId) ? windowsId : iana;

using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
connection.Open();
using var command = connection.CreateCommand();
command.CommandText =
    """
    INSERT OR IGNORE INTO user_settings (id, general, filters, equipment, dismissed_suggestions, display, graph, updated_at)
        VALUES (1, '{}', '{}', '{}', '[]', '{}', '{}', $now);
    UPDATE user_settings SET
        general = json_set(general, '$.observer_latitude', $lat, '$.observer_longitude', $lon, '$.observer_timezone', $tz),
        updated_at = $now;
    """;
command.Parameters.AddWithValue("$lat", lat);
command.Parameters.AddWithValue("$lon", lon);
command.Parameters.AddWithValue("$tz", timezone);
command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fffffff"));
command.ExecuteNonQuery();

Console.WriteLine($"Seeded lat={lat} lon={lon} timezone={timezone} ({iana}) into {dbPath}");
return 0;
