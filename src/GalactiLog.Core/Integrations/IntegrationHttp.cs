namespace GalactiLog.Core.Integrations;

/// <summary>The handler both integration clients send through, built here rather than at the
/// registration site so the rule below is one line of code and not a convention a later caller
/// has to remember.</summary>
public static class IntegrationHttp
{
    /// <summary>A handler that does not follow redirects: a 302 from a configured instance would
    /// re-send the target's coordinates to whatever host the response names, so a redirect reaches
    /// the client as the non-success status it is and ends the action with the fixed sentence
    /// (design-spec 12.16).</summary>
    public static HttpClientHandler NewHandler() => new() { AllowAutoRedirect = false };
}
