using Serilog.Core;
using Serilog.Events;

namespace GalactiLog.App.Services;

// The 500-entry in-memory warning ring from design-spec 16.1's sink table, feeding the
// future Diagnostics "last errors" panel (Phase 10). Stores simplified entries, not raw
// Serilog.Events.LogEvent, so Phase 10's Diagnostics page stays decoupled from Serilog.
public sealed record LogRingEntry(DateTimeOffset Timestamp, string Level, string Message, string? Exception);

public sealed class LogRingBuffer : ILogEventSink
{
    private const int Capacity = 500;
    private readonly object _gate = new();
    private readonly Queue<LogRingEntry> _entries = new();

    public void Emit(LogEvent logEvent)
    {
        var entry = new LogRingEntry(
            logEvent.Timestamp,
            logEvent.Level.ToString(),
            logEvent.RenderMessage(),
            logEvent.Exception?.ToString());

        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > Capacity)
            {
                _entries.Dequeue();
            }
        }
    }

    public IReadOnlyList<LogRingEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }
}
