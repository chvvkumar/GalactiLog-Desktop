namespace GalactiLog.Core.Survey;

/// <summary>Spec 11.3: one survey the Sky view offers, by its HiPS id and its visible label.</summary>
public sealed record SurveyOption(string Id, string Label);

/// <summary>Spec 11.3 and 5.8.1: the five surveys, in the order the pickers list them.</summary>
public static class Surveys
{
    /// <summary>Spec 5.8.1: the survey a missing or unknown stored id resolves to.</summary>
    public const string DefaultId = "P/DSS2/color";

    /// <summary>Spec 11.3: the five surveys in the table's order.</summary>
    public static IReadOnlyList<SurveyOption> All { get; } =
    [
        new(DefaultId, "DSS2 Color"),
        new("P/DSS2/red", "DSS2 Red"),
        new("P/2MASS/color", "2MASS Color"),
        new("P/PanSTARRS/DR1/color-z-zg-g", "PanSTARRS DR1"),
        new("P/allWISE/color", "AllWISE Color"),
    ];

    /// <summary>Spec 5.8.1: an ordinal match on the stored id, else the default survey.</summary>
    public static SurveyOption Resolve(string? storedId) =>
        All.FirstOrDefault(s => string.Equals(s.Id, storedId, StringComparison.Ordinal)) ?? All[0];
}
