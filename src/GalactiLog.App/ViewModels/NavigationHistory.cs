namespace GalactiLog.App.ViewModels;

/// <summary>What the shell's detail overlay holds in a history entry.</summary>
public enum DetailKind { None, Target, Mosaic }

/// <summary>
/// One place the user has been (.planning/mouse-navigation.md, decision 2): the rail destination,
/// the detail page over it if any, and the tab or mode inside the page that is showing.
/// </summary>
/// <param name="Destination">The <see cref="NavigationItem.Key"/> of the rail destination.</param>
/// <param name="Detail">Which kind of detail page is open over the destination.</param>
/// <param name="DetailKey">The target's group key or the mosaic id as text; null for
/// <see cref="DetailKind.None"/>.</param>
/// <param name="TabKey">The Analysis tab key, the Settings tab key, or the target page's
/// <c>TargetPageMode</c> name; null when the page showing has no strip.</param>
public sealed record NavigationEntry(string Destination, DetailKind Detail, string? DetailKey, string? TabKey);

/// <summary>
/// A browser-style history: a list and a cursor. Pushing after a Back discards the forward side;
/// pushing the entry already current changes nothing; the oldest entry drops at
/// <see cref="HistoryCap"/>.
/// </summary>
public sealed class NavigationHistory
{
    internal const int HistoryCap = 100;

    private readonly List<NavigationEntry> _entries = [];
    private int _cursor = -1;

    public NavigationEntry? Current => _cursor >= 0 ? _entries[_cursor] : null;

    public bool CanGoBack => _cursor > 0;

    public bool CanGoForward => _cursor < _entries.Count - 1;

    public int Count => _entries.Count;

    /// <summary>Records an arrival. Returns false when <paramref name="entry"/> is already current.</summary>
    public bool Push(NavigationEntry entry)
    {
        if (entry == Current)
        {
            return false;
        }

        _entries.RemoveRange(_cursor + 1, _entries.Count - _cursor - 1);
        _entries.Add(entry);
        if (_entries.Count > HistoryCap)
        {
            _entries.RemoveAt(0);
        }

        _cursor = _entries.Count - 1;
        return true;
    }

    public NavigationEntry? Back() => CanGoBack ? _entries[--_cursor] : null;

    public NavigationEntry? Forward() => CanGoForward ? _entries[++_cursor] : null;
}
