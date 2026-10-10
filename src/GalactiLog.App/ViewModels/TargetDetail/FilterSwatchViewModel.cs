using Avalonia.Media.Immutable;

namespace GalactiLog.App.ViewModels.TargetDetail;

/// <summary>
/// A canonical filter name and its configured colour, for the 7 px dot the log line, the ledger's
/// filters column, the session pane's filter table and the chart pills all draw.
/// </summary>
/// <remarks>
/// The brush is an <see cref="ImmutableSolidColorBrush"/> (spec 14.5), never a
/// <c>SolidColorBrush</c>: these are built wherever the query result arrives, which is not always
/// the UI thread, and <c>SolidColorBrush</c>'s constructor calls <c>VerifyAccess</c> once a
/// dispatcher exists. A filter colour is user data rather than a theme token, which is why this is
/// the one brush on this page that a view model is allowed to hold at all.
/// <para>
/// Declared as its own small file because more than one surface renders it: the log line's filter
/// run (Task 4) and the session pane's filter table (Task 5).
/// </para>
/// </remarks>
/// <param name="FilterName">The canonical filter name, as the queries already resolved it.</param>
/// <param name="Brush">The filter's configured colour, or the shared fallback grey.</param>
public sealed record FilterSwatchViewModel(string FilterName, ImmutableSolidColorBrush Brush);
