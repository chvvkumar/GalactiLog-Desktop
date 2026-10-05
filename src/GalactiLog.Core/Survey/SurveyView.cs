using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GalactiLog.Core.Survey;

/// <summary>Spec 12.4: the header values the Sky view opens on, RA and Dec in degrees and
/// <see cref="SizeMajor"/> in arcminutes.</summary>
public sealed record SurveyTarget(Guid TargetId, string PrimaryName, double Ra, double Dec, double? SizeMajor);

/// <summary>Spec 11.3: one survey, centre and field, which is one request and one cached file.</summary>
public sealed record SurveyView(string SurveyId, double Ra, double Dec, double Fov)
{
    /// <summary>Spec 11.3: the lower field clamp, in degrees.</summary>
    public const double MinField = 0.1;

    /// <summary>Spec 11.3: the upper field clamp, in degrees.</summary>
    public const double MaxField = 5.0;

    /// <summary>Spec 11.3: the field when the target carries no usable size.</summary>
    public const double UnknownSizeField = 0.5;

    /// <summary>Spec 11.3: the field ratio of one zoom step.</summary>
    public const double ZoomFactor = 1.5;

    /// <summary>Spec 11.3: the requested image side, in pixels, both sides.</summary>
    public const int ImageSize = 1024;

    /// <summary>Spec 11.3's field rule: <c>size_major * 1.5 / 60</c> clamped to 0.1 and 5.0, and
    /// 0.5 for a null, non-finite or non-positive size.</summary>
    public static double DefaultField(double? sizeMajor) =>
        sizeMajor is { } size && double.IsFinite(size) && size > 0
            ? Math.Clamp(size * 1.5 / 60, MinField, MaxField)
            : UnknownSizeField;

    /// <summary>Spec 12.4: the target's centre at <see cref="DefaultField"/>.</summary>
    public static SurveyView Initial(SurveyTarget target, string surveyId) =>
        new(surveyId, WrapRa(target.Ra), ClampDec(target.Dec), DefaultField(target.SizeMajor));

    /// <summary>Spec 11.3: positive steps divide the field by 1.5 each, negative multiply, clamped
    /// to 0.1 and 5.0.</summary>
    public SurveyView Zoomed(int steps) =>
        this with { Fov = Math.Clamp(Fov / Math.Pow(ZoomFactor, steps), MinField, MaxField) };

    /// <summary>Spec 12.4: a drag by a fraction of the drawn side, positive right and down, moves
    /// the centre east and north.</summary>
    public SurveyView Panned(double dxFraction, double dyFraction)
    {
        // The cosine floor keeps a pan beside a pole bounded.
        var cosDec = Math.Max(Math.Cos(Dec * Math.PI / 180), 0.01);
        return this with
        {
            Ra = WrapRa(Ra + dxFraction * Fov / cosDec),
            Dec = ClampDec(Dec + dyFraction * Fov),
        };
    }

    /// <summary>Spec 11.3: the eight parameters in the table's order, invariant numbers.</summary>
    public string QueryString() =>
        $"hips={Uri.EscapeDataString(SurveyId)}&ra={RaText()}&dec={Number(ClampDec(Dec))}&fov={Number(Fov)}"
        + $"&width={ImageSize}&height={ImageSize}&projection=TAN&format=jpg";

    /// <summary>Spec 11.3: lowercase hex of the first 16 bytes of SHA-256 over
    /// <c>hips|ra|dec|fov|width</c>, the numbers as <see cref="QueryString"/> formats them.</summary>
    public string CacheKey()
    {
        var text = $"{SurveyId}|{RaText()}|{Number(ClampDec(Dec))}|{Number(Fov)}|{ImageSize}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 16));
    }

    // Wrapped again after rounding, so 359.99996 is sent as 0.0000 and never as 360.0000.
    private string RaText()
    {
        var rounded = Math.Round(WrapRa(Ra), 4);
        return Number(rounded >= 360 ? rounded - 360 : rounded);
    }

    // Adding 0.0 turns a negative zero into zero, which "F4" would otherwise print as -0.0000.
    private static string Number(double value) =>
        (Math.Round(value, 4) + 0.0).ToString("F4", CultureInfo.InvariantCulture);

    private static double WrapRa(double ra)
    {
        var wrapped = ra % 360;
        return wrapped < 0 ? wrapped + 360 : wrapped + 0.0;
    }

    private static double ClampDec(double dec) => Math.Clamp(dec, -90, 90);
}
