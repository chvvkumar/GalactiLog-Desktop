namespace GalactiLog.App.Tests.TestSupport;

/// <summary>
/// The GMT offset prefix <c>TimezoneOptions.Build</c> renders (design-spec 12.1, 12.7, user
/// ruling U4), recomputed here from the production zone database. A case that runs a view
/// model's Location or wizard timezone list against the real resolver, rather than a pinned
/// dictionary, has no way to predict the machine's display names (or the relative order of two
/// zones that share an offset, which the display name then decides); it can still assert the GMT
/// offset half of the label, which <c>TimeZoneInfo.BaseUtcOffset</c> makes stable on any machine
/// that resolves the id at all. The second occurrence of this exact computation is the point at
/// which it is extracted rather than copied a third time (design-lessons rule 1).
/// </summary>
internal static class TimezoneLabels
{
    public static string GmtPrefix(string id)
    {
        var offset = TimeZoneInfo.FindSystemTimeZoneById(id).BaseUtcOffset;
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        var magnitude = offset.Duration();
        return $"GMT{sign}{magnitude.Hours:D2}:{magnitude.Minutes:D2} ";
    }
}
