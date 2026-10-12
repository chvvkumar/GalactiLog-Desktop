using Serilog.Core;
using Serilog.Events;

namespace GalactiLog.App.Services.Telemetry;

public sealed record ErrorTemplateCount(string Ctx, string Level, string Template, int N);

/// <summary>
/// Serilog sink counting Warning and above per (SourceContext, level, message template) for the
/// telemetry report's <c>errors</c> array. Keeps only the template text, never the rendered
/// message, the property values or the exception, so nothing a log call interpolates reaches the
/// report. The template is scrubbed as well, in case a call site ever passes an interpolated
/// string as its template.
/// </summary>
public sealed class ErrorTemplateCounter : ILogEventSink
{
    public const int MaxTemplateLength = 120;
    public const int MaxReported = 20;

    // ponytail: hard cap on distinct keys so a pathological dynamic template cannot grow the map
    // without bound; templates are code constants today, so the cap is never reached.
    private const int MaxKeys = 1000;

    private readonly Lock _gate = new();
    private readonly Dictionary<(string Ctx, LogEventLevel Level, string Template), int> _counts = new();

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Warning)
        {
            return;
        }

        var ctx = logEvent.Properties.TryGetValue("SourceContext", out var value)
            && value is ScalarValue { Value: string s } ? s : "";
        var template = TelemetryScrubber.Scrub(logEvent.MessageTemplate.Text);
        if (template.Length > MaxTemplateLength)
        {
            template = template[..MaxTemplateLength];
        }

        var key = (ctx, logEvent.Level, template);
        lock (_gate)
        {
            if (_counts.TryGetValue(key, out var n))
            {
                _counts[key] = n + 1;
            }
            else if (_counts.Count < MaxKeys)
            {
                _counts[key] = 1;
            }
        }
    }

    /// <summary>The top <see cref="MaxReported"/> entries by count, highest first.</summary>
    public IReadOnlyList<ErrorTemplateCount> Snapshot()
    {
        lock (_gate)
        {
            return _counts
                .OrderByDescending(entry => entry.Value)
                .Take(MaxReported)
                .Select(entry => new ErrorTemplateCount(
                    entry.Key.Ctx, entry.Key.Level.ToString(), entry.Key.Template, entry.Value))
                .ToList();
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _counts.Clear();
        }
    }
}
