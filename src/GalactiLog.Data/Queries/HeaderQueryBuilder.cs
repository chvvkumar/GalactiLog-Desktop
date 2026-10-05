using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GalactiLog.Data.Queries;

/// <summary>
/// Builds the raw FITS header clauses of spec 12.3. Every operand is a bound parameter; a key
/// that fails the gate or a numeric comparison whose value does not parse is dropped, not
/// escaped and not partially applied.
/// </summary>
/// <remarks>
/// Pure and static: no database access. <see cref="Append"/> takes a caller-supplied
/// <c>bindParameter</c> delegate rather than a parameter list of its own, because Task 2's landed
/// <c>TargetListingQuery.SqlParameters</c> is a private, positionally-named bag
/// (<c>@p0</c>, <c>@p1</c>, ...) with no way to hand a foreign parameter collection into the same
/// command. Calling back into the caller's own naming factory means every name this builder
/// returns is already unique against the caller's other parameters, with no prefix scheme needed.
/// </remarks>
public static partial class HeaderQueryBuilder
{
    /// <summary>The seven operators of spec 12.2. Task 6 binds the operator combo box directly to
    /// this rather than repeating the list in XAML.</summary>
    public static readonly string[] SupportedOperators = ["=", "!=", ">", "<", ">=", "<=", "contains"];

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,20}$")]
    private static partial Regex KeyPattern();

    /// <summary>Spec 12.3's key gate, verbatim. A key that does not match is dropped, never
    /// escaped. Exposed so <see cref="DistinctHeaderKeysQuery"/> never offers a key the builder
    /// would drop.</summary>
    public static bool IsValidKey(string? key) => key is not null && KeyPattern().IsMatch(key);

    /// <summary>Appends one clause per accepted condition to <paramref name="clauses"/> (the same
    /// list the caller joins with "AND" for the rest of its base filter) and binds every operand
    /// through <paramref name="bindParameter"/>, which must return the SQL parameter name it
    /// assigned the given value. Returns the number of conditions accepted. A dropped condition
    /// contributes nothing at all: no clause, no parameter, and no change to the meaning of the
    /// remaining ones.</summary>
    public static int Append(
        IReadOnlyList<HeaderCondition> conditions,
        IList<string> clauses,
        Func<object?, string> bindParameter)
    {
        var accepted = 0;
        foreach (var raw in conditions)
        {
            // A null Value (HeaderCondition's declared type is non-nullable, but nothing stops a
            // caller from constructing one with null) is treated exactly like an empty string:
            // every operator's existing empty-value handling already applies (dropped by the
            // numeric parse, bound as "" for "=", matches everything for "contains"). Without this,
            // EscapeLike's foreach over a null value throws.
            var condition = raw.Value is null ? raw with { Value = "" } : raw;
            var clause = BuildClause(condition, bindParameter);
            if (clause is null)
            {
                continue;
            }

            clauses.Add(clause);
            accepted++;
        }

        return accepted;
    }

    private static string? BuildClause(HeaderCondition condition, Func<object?, string> bindParameter)
    {
        if (!IsValidKey(condition.Key) || Array.IndexOf(SupportedOperators, condition.Operator) < 0)
        {
            return null;
        }

        switch (condition.Operator)
        {
            case ">" or "<" or ">=" or "<=":
                // Invariant culture: a header value is machine-written data, and a decimal comma
                // must not become a valid number on a German desktop and an invalid one on an
                // English one.
                if (!double.TryParse(condition.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var bound))
                {
                    return null;
                }

                var rangeKey = bindParameter(condition.Key);
                var rangeValue = bindParameter(bound);
                return $"CAST({Extract(rangeKey)} AS REAL) {condition.Operator} {rangeValue}";

            case "=" or "!=":
                var equalityKey = bindParameter(condition.Key);

                // Q6: json_extract returns a SQLite number for a numeric JSON value, and SQLite
                // compares a number to a text parameter as unequal, so a user typing "1.5" against
                // a numeric header would match nothing with "=". Binding as a double when the
                // value parses makes the operator behave the way its label reads; this is a
                // binding-type choice on an already-bound parameter, not a security one.
                object equalityOperand = double.TryParse(
                    condition.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var asDouble)
                    ? asDouble
                    : condition.Value;
                var equalityValue = bindParameter(equalityOperand);

                // "!=" uses IS NOT rather than <>: a frame whose header lacks the key extracts to
                // SQL NULL, and NULL <> 'x' is NULL, which excludes the frame. IS NOT includes it,
                // which is what "the header is not equal to x" means to a user reading the label.
                return condition.Operator == "="
                    ? $"{Extract(equalityKey)} = {equalityValue}"
                    : $"{Extract(equalityKey)} IS NOT {equalityValue}";

            case "contains":
                var containsKey = bindParameter(condition.Key);
                var containsValue = bindParameter("%" + EscapeLike(condition.Value) + "%");
                return $"{Extract(containsKey)} LIKE {containsValue} ESCAPE '\\'";

            default:
                return null;
        }
    }

    private static string Extract(string keyParameterName) => $"json_extract(i.raw_headers, '$.' || {keyParameterName})";

    // The only string manipulation applied to a value; its result is still a bound parameter.
    // Prefixing backslash first (rather than in a second pass over already-escaped output) is
    // what a single left-to-right scan does naturally: a backslash introduced to escape a % or _
    // is never re-escaped, because it was never one of the original characters being scanned.
    //
    // Internal rather than private since Phase 20: spec 12.15's Contains filter builds the same
    // "%" + escaped + "%" parameter against the same ESCAPE '\' clause, and a second copy of this
    // five line scan is exactly the duplication design lesson 1 refuses at the second occurrence.
    internal static string EscapeLike(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '\\' or '%' or '_')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
