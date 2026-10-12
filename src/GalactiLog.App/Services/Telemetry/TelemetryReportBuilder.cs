using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using GalactiLog.App.Theme;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.Data;
using GalactiLog.Data.Repositories;

namespace GalactiLog.App.Services.Telemetry;

/// <summary>
/// The one producer of the bytes that leave the machine (telemetry payload schema v1). The
/// preview UI and the sender both call <see cref="Build"/>. Every value written here is either a
/// build or runtime fact, a counter, a code-defined key or a log template; nothing the user typed.
/// </summary>
public sealed class TelemetryReportBuilder
{
    public const int SchemaVersion = 1;
    public const int MaxBytes = 8192;

    /// <summary>The crash object's keys in schema v1. A marker key outside this list is not
    /// copied, so the payload stays schema-shaped whatever the file holds.</summary>
    private static readonly string[] CrashKeys =
        ["ts", "version", "sha", "type", "inner", "hresult", "message", "frames", "page", "uptime_s", "source"];

    private readonly BuildInfo _buildInfo;
    private readonly SettingsStore _settings;
    private readonly ResolverCounters _counters;
    private readonly ErrorTemplateCounter _errors;
    private readonly string _dataRoot;

    public TelemetryReportBuilder(
        BuildInfo buildInfo, SettingsStore settings, ResolverCounters counters, ErrorTemplateCounter errors, string dataRoot)
    {
        _buildInfo = buildInfo;
        _settings = settings;
        _counters = counters;
        _errors = errors;
        _dataRoot = dataRoot;
    }

    /// <summary>Produces the report. Reads the general settings document, so it may throw on an
    /// unreadable document; the sender catches.</summary>
    /// <param name="includeCrash">Embed the crash marker under <c>crash</c> when one exists.</param>
    /// <param name="indented">Indented for the preview, compact for the wire. The size cap is
    /// enforced on the compact form either way.</param>
    public string Build(bool includeCrash, bool indented)
    {
        var general = _settings.GetGeneral();
        var crash = includeCrash ? CrashMarker.TryRead(_dataRoot) : null;
        var errors = _errors.Snapshot().ToList();
        var scale = PrimaryScreenScaling();

        while (errors.Count > 0 && Render(general, crash, errors, scale, indented: false).Length > MaxBytes)
        {
            errors.RemoveAt(errors.Count - 1);
        }

        return System.Text.Encoding.UTF8.GetString(Render(general, crash, errors, scale, indented));
    }

    private byte[] Render(
        Core.Settings.GeneralSettings general, JsonElement? crash, IReadOnlyList<ErrorTemplateCount> errors, double scale, bool indented)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = indented,
            // Relaxed so "<path>" and apostrophes read literally in the preview; the output is
            // posted as JSON, never embedded in HTML.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            w.WriteStartObject();
            // Only a well-formed GUID is sent; anything else in a hand-edited document becomes "".
            w.WriteString("id", Guid.TryParse(general.TelemetryId, out var id) ? id.ToString("D") : "");
            w.WriteNumber("schema", SchemaVersion);

            w.WriteStartObject("app");
            w.WriteString("version", _buildInfo.Version);
            w.WriteString("sha", _buildInfo.GitSha);
            w.WriteString("channel", _buildInfo.Channel);
            w.WriteEndObject();

            w.WriteStartObject("env");
            w.WriteString("os", Environment.OSVersion.VersionString);
            w.WriteString("arch", RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant());
            w.WriteString("dotnet", Environment.Version.ToString());
            w.WriteString("avalonia", typeof(Application).Assembly.GetName().Version?.ToString() ?? "");
            // Stored ids are only trusted when this build ships them: the theme and font keys are
            // not validated on save, so a hand-edited document could hold free text.
            w.WriteString("theme", Known(general.Theme, ThemeManager.Available));
            w.WriteString("text_size", Known(general.TextSize, DisplayTabViewModel.TextSizes.Select(option => option.Id)));
            w.WriteString("font", Known(general.Font, FontManager.Options.Select(option => option.Id)));
            w.WriteNumber("scale", scale);
            w.WriteEndObject();

            w.WriteStartObject("session");
            w.WriteNumber("uptime_s", (long)TelemetryContext.Uptime.Elapsed.TotalSeconds);
            w.WriteEndObject();

            w.WriteStartObject("counters");
            w.WriteNumber("resolver_hits", _counters.Hits);
            w.WriteNumber("resolver_misses", _counters.Misses);
            w.WriteNumber("resolver_failures", _counters.Failures);
            w.WriteEndObject();

            w.WriteStartArray("errors");
            foreach (var error in errors)
            {
                w.WriteStartObject();
                w.WriteString("ctx", error.Ctx);
                w.WriteString("level", error.Level);
                w.WriteString("template", error.Template);
                w.WriteNumber("n", error.N);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            if (crash is { ValueKind: JsonValueKind.Object } c)
            {
                w.WriteStartObject("crash");
                foreach (var key in CrashKeys)
                {
                    if (c.TryGetProperty(key, out var value))
                    {
                        w.WritePropertyName(key);
                        value.WriteTo(w);
                    }
                }
                w.WriteEndObject();
            }

            w.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static string Known(string? value, IEnumerable<string> allowed)
        => value is not null && allowed.Contains(value, StringComparer.Ordinal) ? value : "other";

    /// <summary>Primary screen scaling, or 0 when there is no main window (headless, tray only) or
    /// the read fails. Screens are read on the UI thread; a caller on another thread waits at most
    /// two seconds for it.</summary>
    private static double PrimaryScreenScaling()
    {
        try
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
            {
                return 0;
            }

            double Read() => window.Screens.Primary?.Scaling ?? 0;
            if (Dispatcher.UIThread.CheckAccess())
            {
                return Read();
            }

            var task = Dispatcher.UIThread.InvokeAsync(Read).GetTask();
            return task.Wait(TimeSpan.FromSeconds(2)) ? task.Result : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
