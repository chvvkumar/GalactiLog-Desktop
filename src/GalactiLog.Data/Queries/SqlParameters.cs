using System.Globalization;
using Microsoft.Data.Sqlite;

namespace GalactiLog.Data.Queries;

/// <summary>
/// Bound parameter bag. Names are positional (@p0, @p1, ...) and every value the caller can
/// influence goes through here: nothing user-supplied reaches a statement's text. All of a
/// call's parameters are applied to each of its commands; Microsoft.Data.Sqlite ignores a
/// parameter a statement does not reference.
/// </summary>
/// <remarks>
/// Extracted from <c>TargetListingQuery</c> when <c>TargetDetailQuery</c> became the second
/// query needing it (design-lessons rule 1). <c>SessionDetailQuery</c> is the third. FIXER LIST
/// F14: <c>FrameHeadersQuery</c> is not a fourth, and never was. It binds one image id and
/// composes nothing, so it uses a single <c>SqliteParameter</c> directly, the same as
/// <c>DistinctHeaderKeysQuery</c>; a bound single parameter needs no bag.
/// </remarks>
internal sealed class SqlParameters
{
    private readonly List<KeyValuePair<string, object>> _values = [];

    public string Add(object? value)
    {
        var name = "@p" + _values.Count.ToString(CultureInfo.InvariantCulture);
        _values.Add(new KeyValuePair<string, object>(name, value ?? DBNull.Value));
        return name;
    }

    public void ApplyTo(SqliteCommand command)
    {
        foreach (var (name, value) in _values)
        {
            command.Parameters.Add(new SqliteParameter(name, value));
        }
    }
}
