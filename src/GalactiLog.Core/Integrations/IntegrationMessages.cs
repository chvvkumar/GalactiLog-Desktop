namespace GalactiLog.Core.Integrations;

/// <summary>The three fixed sentences of design-spec 12.16 ("The fixed sentences"), and no fourth.
/// A caller never builds a message out of an exception, because an exception's own text carries
/// internal addresses and library detail; the exception goes to the log and one of these goes to
/// the reader.
/// <para><see cref="BadUrl"/> has two callers by design, the client's fail-closed refusal and the
/// External Tools tab's refusal (spec 12.7), so the reader meets one wording wherever the URL rule
/// bites.</para></summary>
public static class IntegrationMessages
{
    /// <summary>A NINA call failed, for any reason including the timeout.</summary>
    public const string NinaFailed = "NINA request failed. The reason is in the log.";

    /// <summary>A Stellarium call failed, for any reason including the timeout.</summary>
    public const string StellariumFailed = "Stellarium request failed. The reason is in the log.";

    /// <summary>The stored URL did not pass <see cref="IntegrationUrl.TryParse"/>.</summary>
    public const string BadUrl = "The instance URL must start with http:// or https:// and name a host.";
}
