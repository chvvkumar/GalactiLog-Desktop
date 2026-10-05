namespace GalactiLog.App.ViewModels;

/// <summary>
/// One destination in the left navigation rail (design-spec 12), and one tab on the Settings
/// page (design-spec 12.7). <see cref="Page"/> is the destination's view-model; the
/// <c>Application.DataTemplates</c> block in App.axaml picks the view for it, so adding a
/// destination is one entry here plus one template line there.
/// </summary>
/// <remarks>
/// Two ways to supply the page, and the difference is when it is built:
/// <list type="bullet">
/// <item>The <see cref="NavigationItem(string, string, object)"/> constructor takes a page that
/// already exists. The rail's Dashboard and Settings entries use it: the dashboard is a singleton
/// that must exist before the window is shown.</item>
/// <item>The <see cref="NavigationItem(string, string, Func{object})"/> constructor takes a
/// factory, invoked once on the first read of <see cref="Page"/> and memoized after that. The
/// Settings tab strip uses it (TRACKING.md section 6 item 16): ten eagerly built tabs cost
/// several SQLite reads at window resolution for tabs the user may never open. The rail's
/// Statistics and Activity entries use it too (FIXER LIST F21), for the same reason: each page
/// runs a query in its own constructor.</item>
/// </list>
/// <para>
/// The memo needs no lock. <see cref="Page"/> is read by the <c>ContentControl</c> binding on
/// the Settings page, which runs on the UI thread, and by <see cref="IsConstructed"/>-guarded
/// disposal, which also runs there. A second reader on another thread would be a design change,
/// not a reason to add a lock here.
/// </para>
/// </remarks>
/// <remarks>
/// A sealed class, not a record (Task 5 review finding M3). A record's synthesized
/// <c>Equals</c> and <c>GetHashCode</c> cover every instance field, the memo included, so a
/// factory item's hash code changed the first time <see cref="Page"/> was read and an item put
/// in a hash-based collection before its first visit became unfindable after it. Reference
/// identity is what both the rail and the tab strip actually want, and it is what every call
/// site already relies on.
/// </remarks>
public sealed class NavigationItem
{
    private readonly Func<object>? _factory;
    private object? _page;

    /// <param name="key">Stable identifier, used by tests and by later phases to find a
    /// destination.</param>
    /// <param name="title">The rail or tab strip label.</param>
    /// <param name="page">The page view-model shown in the content region while this item is
    /// selected, already constructed.</param>
    /// <param name="iconKey">The StreamGeometry resource the collapsed rail draws for this
    /// destination; null for a Settings tab, which never collapses.</param>
    public NavigationItem(string key, string title, object page, string? iconKey = null)
    {
        Key = key;
        Title = title;
        _page = page;
        IconKey = iconKey;
    }

    /// <param name="key">Stable identifier, used by tests and by later phases to find a
    /// destination.</param>
    /// <param name="title">The rail or tab strip label.</param>
    /// <param name="factory">Builds the page view-model on the first read of
    /// <see cref="Page"/>, and never again.</param>
    /// <param name="iconKey">The StreamGeometry resource the collapsed rail draws for this
    /// destination; null for a Settings tab, which never collapses.</param>
    public NavigationItem(string key, string title, Func<object> factory, string? iconKey = null)
    {
        Key = key;
        Title = title;
        _factory = factory;
        IconKey = iconKey;
    }

    public string Key { get; }

    public string Title { get; }

    /// <summary>The resource key of the icon a collapsed rail draws, or null.</summary>
    public string? IconKey { get; }

    /// <summary>
    /// The page view-model. For a factory item the first read builds it; every later read
    /// returns the same instance.
    /// </summary>
    public object Page => _page ??= _factory!();

    /// <summary>
    /// Whether <see cref="Page"/> exists yet. False for a factory item nobody has visited.
    /// <c>SettingsViewModel.Dispose</c> checks it so disposing the Settings page does not build
    /// every tab in order to dispose it, which is the exact defect the factory constructor
    /// exists to avoid.
    /// </summary>
    public bool IsConstructed => _page is not null;
}
