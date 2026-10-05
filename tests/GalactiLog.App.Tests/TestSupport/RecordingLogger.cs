using Microsoft.Extensions.Logging;

namespace GalactiLog.App.Tests.TestSupport;

// FIXER LIST F7: the App-layer guard code (a throwing ScanFinished subscriber, a failed option-list
// reload, a failed column write) logs through an injected ILogger instead of the static Serilog
// one, so a test can assert that the guard actually ran rather than trusting a swallowed exception.
internal sealed class RecordingLogger : ILogger
{
    private readonly Lock _gate = new();
    private readonly List<(LogLevel Level, string Message, Exception? Exception)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries
    {
        get { lock (_gate) { return [.. _entries]; } }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    /// <summary>
    /// The same recorder seen as an <see cref="ILogger{TCategoryName}"/>, for a service whose
    /// constructor takes the generic form (<c>ScanScheduler</c>, <c>WatcherService</c>). One
    /// recorder either way: <see cref="Entries"/> carries what was written through both views.
    /// </summary>
    public ILogger<TCategory> For<TCategory>() => new Typed<TCategory>(this);

    private sealed class Typed<TCategory>(RecordingLogger inner) : ILogger<TCategory>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => inner.Log(logLevel, eventId, state, exception, formatter);
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
        {
            _entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
