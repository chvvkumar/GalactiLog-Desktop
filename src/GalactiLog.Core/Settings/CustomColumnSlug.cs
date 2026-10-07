using System.Globalization;
using System.Text;

namespace GalactiLog.Core.Settings;

/// <summary>Spec 12.15's three column types. The stored value is the lower-case member name.</summary>
public enum CustomColumnType { Boolean, Text, Dropdown }

/// <summary>Spec 12.15's four scopes. `Mosaic` arrived with Phase 18 (ruling R2): its values are
/// keyed by the mosaic alone.</summary>
public enum CustomColumnScope { Target, Session, Rig, Mosaic }

/// <summary>
/// Spec 12.15's slug rule: derives, and on request disambiguates, the stored <c>custom_*</c> key
/// for a user-typed column name. The one place that rule is applied; no second deriving of a slug
/// exists anywhere in this phase.
/// </summary>
public static class CustomColumnSlug
{
    /// <summary>The mandatory prefix. Spec 12.15's slug rule step 5, and the whole reason a custom
    /// column named "Name" cannot shadow the dashboard's built-in `name` key.</summary>
    public const string Prefix = "custom_";

    /// <summary>Spec 12.15: 1 to 60 characters after trimming.</summary>
    public const int MaxNameLength = 60;

    /// <summary>Spec 12.15: 1 to 500 characters after trimming.</summary>
    public const int MaxValueLength = 500;

    /// <summary>The literal a boolean column stores for a checked box.</summary>
    public const string True = "true";

    /// <summary>The literal a boolean column stores for a cleared box.</summary>
    public const string False = "false";

    /// <summary>Steps 1 to 5 of spec 12.15's slug rule, with no uniqueness pass. Never null, never
    /// empty, always begins with <see cref="Prefix"/>.</summary>
    public static string Base(string name)
    {
        var lowered = name.Trim().ToLower(CultureInfo.InvariantCulture);
        var builder = new StringBuilder(lowered.Length);
        var lastWasUnderscore = false;

        foreach (var ch in lowered)
        {
            var isAllowed = (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9');
            if (isAllowed)
            {
                builder.Append(ch);
                lastWasUnderscore = false;
            }
            else if (!lastWasUnderscore)
            {
                builder.Append('_');
                lastWasUnderscore = true;
            }
        }

        var slug = builder.ToString().Trim('_');
        return Prefix + (slug.Length == 0 ? "column" : slug);
    }

    /// <summary>Step 6: <see cref="Base"/>, then `_2`, `_3` and so on until
    /// <paramref name="taken"/> answers false. Ordinal comparison.</summary>
    public static string Unique(string name, Func<string, bool> taken)
    {
        var slug = Base(name);
        if (!taken(slug))
        {
            return slug;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{slug}_{suffix}";
            if (!taken(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>True when the key carries <see cref="Prefix"/>. What a `display.columns` reader
    /// uses to tell a custom key from a built-in one without a lookup.</summary>
    public static bool IsCustom(string? key)
        => key is not null && key.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The stored word for a type: `boolean`, `text`, `dropdown`.</summary>
    public static string Word(CustomColumnType type) => type switch
    {
        CustomColumnType.Boolean => "boolean",
        CustomColumnType.Text => "text",
        CustomColumnType.Dropdown => "dropdown",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, message: null),
    };

    /// <summary>The stored word for a scope: `target`, `session`, `rig`, `mosaic`.</summary>
    public static string Word(CustomColumnScope scope) => scope switch
    {
        CustomColumnScope.Target => "target",
        CustomColumnScope.Session => "session",
        CustomColumnScope.Rig => "rig",
        CustomColumnScope.Mosaic => "mosaic",
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, message: null),
    };

    /// <summary>Null for anything outside the set, which is how a hand-edited row reads as unknown
    /// rather than throwing.</summary>
    public static CustomColumnType? ParseType(string? word) => word switch
    {
        "boolean" => CustomColumnType.Boolean,
        "text" => CustomColumnType.Text,
        "dropdown" => CustomColumnType.Dropdown,
        _ => null,
    };

    /// <summary>Null for anything outside the set, which is how a hand-edited row reads as unknown
    /// rather than throwing.</summary>
    public static CustomColumnScope? ParseScope(string? word) => word switch
    {
        "target" => CustomColumnScope.Target,
        "session" => CustomColumnScope.Session,
        "rig" => CustomColumnScope.Rig,
        "mosaic" => CustomColumnScope.Mosaic,
        _ => null,
    };
}
