using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace GalactiLog.Core.Integrations;

/// <summary>The Stellarium slew protocol of design-spec 12.16 ("The Stellarium protocol"). Port of
/// <c>backend/app/api/integrations.py</c>'s <c>send_to_stellarium</c>.
/// <para>
/// No <see cref="Microsoft.Extensions.Logging.ILogger"/>, the standing <c>GalactiLog.Core</c>
/// convention: the <c>Debug</c> line spec 12.16 step 2 asks for per failed focus candidate is the
/// caller's, and this client returns the facts instead.
/// </para></summary>
public sealed partial class StellariumClient
{
    /// <summary>Spec 12.16, "The timeout": 5 seconds per request through a linked
    /// <see cref="CancellationTokenSource"/>, never by setting <see cref="HttpClient.Timeout"/>. A
    /// slew that tries two names, a coordinate fallback and a field of view call can run longer
    /// than 5 seconds in total, which is the right bound: each leg can hang on its own.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private const string FormContentType = "application/x-www-form-urlencoded";

    /// <summary>The field of view spec 12.16 step 4 sets on both routes.</summary>
    private const string FieldOfView = "20";

    private readonly HttpClient _http;
    private readonly TimeSpan _requestTimeout;

    /// <param name="http">The one integration <see cref="HttpClient"/> singleton <c>AppHost</c>
    /// registers for both clients (ruling B1). This client never touches its
    /// <see cref="HttpClient.Timeout"/>.</param>
    /// <param name="requestTimeout">Overrides the 5 second bound; null (every production caller)
    /// keeps it.</param>
    public StellariumClient(HttpClient http, TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _requestTimeout = requestTimeout ?? RequestTimeout;
    }

    // Verbatim from the web, under a one second match timeout whose expiry reads as "no prefix"
    // rather than as a failure.
    [GeneratedRegex(
        @"^(NGC|IC|M|Sh2|LDN|LBN|Abell|Ced|vdB|Cr|Mel|Barnard|PGC|UGC|Arp)\s*(?:-\s*)?\d+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex CatalogPrefixRegex();

    /// <summary>Slews one Stellarium instance to the target: focus by name where a name is known,
    /// coordinates otherwise, then the field of view.</summary>
    /// <param name="url">The stored instance URL. One that fails
    /// <see cref="IntegrationUrl.TryParse"/> ends the action here with
    /// <see cref="IntegrationMessages.BadUrl"/> and no call at all.</param>
    /// <param name="ra">Right ascension in degrees.</param>
    /// <param name="dec">Declination in degrees.</param>
    /// <param name="targetName">The target name, or null or blank when none is known, in which
    /// case focus by name does not run and the coordinate route is used.</param>
    /// <param name="token">The caller's token. A cancellation through it ends the action as a
    /// failure rather than throwing at the caller.</param>
    public async Task<StellariumSlewResult> SlewAsync(
        string? url,
        double ra,
        double dec,
        string? targetName,
        CancellationToken token = default)
    {
        if (!IntegrationUrl.TryParse(url, out var baseAddress))
        {
            return new StellariumSlewResult(
                false, StellariumFocus.Coordinates, IntegrationMessages.BadUrl, null);
        }

        var focus = StellariumFocus.Coordinates;
        var focused = false;
        foreach (var (candidate, route) in FocusCandidates(targetName))
        {
            // An attempt that threw, timed out or answered anything but a success status carrying
            // "true" is not a failure of the action: the next candidate is tried and an exhausted
            // list falls through to the coordinates.
            try
            {
                if (!await FocusAsync(baseAddress, candidate, token).ConfigureAwait(false))
                {
                    continue;
                }
            }
            catch (Exception)
            {
                continue;
            }

            focus = route;
            focused = true;
            break;
        }

        try
        {
            if (!focused)
            {
                var script = string.Create(
                    CultureInfo.InvariantCulture,
                    $"core.moveToRaDecJ2000(\"{ra:F6}d\", \"{dec:F6}d\", 3);");
                await PostAsync(baseAddress, "/api/scripts/direct", "code", script, token)
                    .ConfigureAwait(false);
            }

            // Always, whichever route pointed Stellarium at the target: a slew that left the view
            // at the wrong scale did not do what the menu item says (spec 12.16 step 4).
            await PostAsync(baseAddress, "/api/main/fov", "fov", FieldOfView, token)
                .ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            return new StellariumSlewResult(
                false, focus, IntegrationMessages.StellariumFailed, failure);
        }

        return new StellariumSlewResult(true, focus, null, null);
    }

    /// <summary>Up to two focus candidates in the order spec 12.16 step 2 gives: the catalogue
    /// prefix of the trimmed name, then the trimmed name itself. One candidate when the prefix
    /// match is the whole trimmed name and one when there is no prefix at all, two otherwise
    /// (ruling B3).</summary>
    private static List<(string Candidate, StellariumFocus Route)> FocusCandidates(string? targetName)
    {
        var candidates = new List<(string, StellariumFocus)>();
        if (string.IsNullOrWhiteSpace(targetName))
        {
            return candidates;
        }

        var trimmed = targetName.Trim();
        string? prefix = null;
        try
        {
            var match = CatalogPrefixRegex().Match(trimmed);
            prefix = match.Success ? match.Value : null;
        }
        catch (RegexMatchTimeoutException)
        {
            prefix = null;
        }

        if (prefix is not null)
        {
            candidates.Add((prefix, StellariumFocus.CatalogName));
        }

        if (prefix is null || !string.Equals(prefix, trimmed, StringComparison.Ordinal))
        {
            candidates.Add((trimmed, StellariumFocus.FullName));
        }

        return candidates;
    }

    // Succeeded means a success status whose body, trimmed and compared ordinally without case,
    // is exactly "true".
    private async Task<bool> FocusAsync(string baseAddress, string candidate, CancellationToken token)
    {
        using var bound = Bound(token);
        using var request = FormRequest(baseAddress, "/api/main/focus", "target", candidate);
        using var response = await _http.SendAsync(request, bound.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(bound.Token).ConfigureAwait(false);
        return string.Equals(body.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task PostAsync(
        string baseAddress, string path, string field, string value, CancellationToken token)
    {
        using var bound = Bound(token);
        using var request = FormRequest(baseAddress, path, field, value);
        using var response = await _http.SendAsync(request, bound.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    // Percent-encoded with Uri.EscapeDataString, which the web does not do: it interpolates the
    // raw name, so a target named "Sh2-101 & friends" sends a body Stellarium reads as two fields.
    private static HttpRequestMessage FormRequest(
        string baseAddress, string path, string field, string value)
    {
        var content = new StringContent(field + "=" + Uri.EscapeDataString(value), Encoding.UTF8);

        // The bare media type the web sends: the StringContent overload that takes one appends a
        // charset parameter the web's header does not carry.
        content.Headers.ContentType = new MediaTypeHeaderValue(FormContentType);
        return new HttpRequestMessage(HttpMethod.Post, baseAddress + path) { Content = content };
    }

    private CancellationTokenSource Bound(CancellationToken token)
    {
        var bound = CancellationTokenSource.CreateLinkedTokenSource(token);
        bound.CancelAfter(_requestTimeout);
        return bound;
    }
}
