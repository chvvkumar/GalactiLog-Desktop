namespace GalactiLog.Core.Survey;

/// <summary>Spec 12.4: the three fixed sentences of the Sky view.</summary>
public static class SurveyMessages
{
    /// <summary>Spec 12.4: the window's text while <c>general.survey_downloads_enabled</c> is off.</summary>
    public const string SwitchOff =
        "Sky view is off. Turn on Survey downloads on the General tab of Settings. Images are fetched from alasky.cds.unistra.fr.";

    /// <summary>Spec 11.3 and 12.4: the window's text after a failed, refused or timed-out fetch.</summary>
    public const string LoadFailed =
        "The survey image did not load. Check the internet connection and press Refresh.";

    /// <summary>Spec 12.4: the disabled Sky view button's tooltip.</summary>
    public const string DisabledTooltip =
        "Turn on Survey downloads on the General tab of Settings to use Sky view.";
}
