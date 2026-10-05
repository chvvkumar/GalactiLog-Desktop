using GalactiLog.Core.Aliases;

namespace GalactiLog.Data;

/// <summary>
/// The process-wide alias map, rebuilt on demand (spec 12.7). Explicit invalidation via
/// SettingsStore.AliasSourcesChanged is the primary mechanism; the TTL is the backstop for a
/// write that arrived through some path that did not raise the event.
/// </summary>
public sealed class AliasMapCache : IDisposable
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private readonly SettingsStore _settings;
    private readonly Func<DateTime> _utcNow;
    private readonly EventHandler _onAliasSourcesChanged;
    private readonly Lock _lock = new();

    private AliasMap? _cached;
    private DateTime _builtAtUtc;

    public AliasMapCache(SettingsStore settings, Func<DateTime>? utcNow = null)
    {
        _settings = settings;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _onAliasSourcesChanged = (_, _) => Invalidate();
        _settings.AliasSourcesChanged += _onAliasSourcesChanged;
    }

    /// <summary>Returns the current map, rebuilding it if it was invalidated or the TTL
    /// elapsed. Safe to call from any thread.</summary>
    public AliasMap Current
    {
        get
        {
            lock (_lock)
            {
                if (_cached is null || _utcNow() - _builtAtUtc >= Ttl)
                {
                    _cached = new AliasMap(_settings.GetFilters(), _settings.GetEquipment());
                    _builtAtUtc = _utcNow();
                }

                return _cached;
            }
        }
    }

    /// <summary>Drops the cached map so the next read rebuilds. Called by the
    /// AliasSourcesChanged handler; public so a maintenance action can force it.</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _cached = null;
        }
    }

    /// <summary>Phase review item 4: observable so a test can assert that the DI container
    /// actually disposed this, rather than trusting an unsubscribe nothing can see.</summary>
    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        IsDisposed = true;
        _settings.AliasSourcesChanged -= _onAliasSourcesChanged;
    }
}
