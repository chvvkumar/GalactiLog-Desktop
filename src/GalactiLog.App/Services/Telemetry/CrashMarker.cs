using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using GalactiLog.Core.Io;

namespace GalactiLog.App.Services.Telemetry;

/// <summary>
/// The crash object of telemetry payload schema v1, kept in <c>&lt;data root&gt;\crash-report.json</c>
/// until a report carrying it is sent. Written synchronously from the crash handlers, so it uses
/// nothing but the exception, System.Text.Json and one write. The write and the delete go through
/// a throwaway <see cref="AppWriter"/> on the data root, the one write choke point (spec 2.1.3,
/// FileSafetyTest); constructing one does no I/O.
/// </summary>
public static class CrashMarker
{
    public const string FileName = "crash-report.json";
    public const int MaxInner = 5;
    public const int MaxFrames = 12;

    public static string PathFor(string dataRoot) => Path.Combine(dataRoot, FileName);

    /// <summary>Builds the crash object and overwrites the marker. May throw; the caller
    /// swallows.</summary>
    public static void Write(
        Exception ex, string source, string dataRoot, string version, string sha, string? page, long uptimeSeconds)
    {
        if (ex is AggregateException aggregate && aggregate.Flatten().InnerExceptions is [var first, ..])
        {
            ex = first;
        }

        var inner = new List<string>();
        for (var e = ex.InnerException; e is not null && inner.Count < MaxInner; e = e.InnerException)
        {
            inner.Add(e.GetType().FullName ?? e.GetType().Name);
        }

        var crash = new Dictionary<string, object>
        {
            ["ts"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["version"] = version,
            ["sha"] = sha,
            ["type"] = ex.GetType().FullName ?? ex.GetType().Name,
            ["inner"] = inner,
            ["hresult"] = ex.HResult,
            ["message"] = TelemetryScrubber.Scrub(ex.Message),
            ["frames"] = Frames(ex),
            ["page"] = page ?? "",
            ["uptime_s"] = uptimeSeconds,
            ["source"] = source,
        };

        new AppWriter(dataRoot).WriteAllBytes(PathFor(dataRoot), JsonSerializer.SerializeToUtf8Bytes(crash));
    }

    /// <summary>The marker's crash object, or null when it is missing or unreadable.</summary>
    public static JsonElement? TryRead(string dataRoot)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(PathFor(dataRoot)));
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Removes the marker. A missing marker is not an error.</summary>
    public static void Delete(string dataRoot) => new AppWriter(dataRoot).Delete(PathFor(dataRoot));

    // Type and method names only: never a file name or a line number. GalactiLog frames first,
    // each group in stack order.
    private static List<string> Frames(Exception ex)
    {
        var names = new StackTrace(ex, fNeedFileInfo: false).GetFrames()
            .Select(frame => frame.GetMethod())
            .Where(method => method?.DeclaringType?.FullName is not null)
            .Select(method => method!.DeclaringType!.FullName + "." + method.Name)
            .ToList();

        return names.Where(IsOurs).Concat(names.Where(name => !IsOurs(name))).Take(MaxFrames).ToList();

        static bool IsOurs(string name) => name.StartsWith("GalactiLog.", StringComparison.Ordinal);
    }
}
