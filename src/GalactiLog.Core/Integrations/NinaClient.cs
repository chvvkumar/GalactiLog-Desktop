using System.Globalization;

namespace GalactiLog.Core.Integrations;

/// <summary>The NINA framing protocol of design-spec 12.16 ("The NINA protocol"). Port of
/// <c>backend/app/api/integrations.py</c>'s <c>send_to_nina</c>.
/// <para>
/// No <see cref="Microsoft.Extensions.Logging.ILogger"/>, which is the standing
/// <c>GalactiLog.Core</c> convention: a failure comes back as data in
/// <see cref="NinaSendResult"/> and the caller writes the log line, including the <c>Warning</c>
/// spec 12.16 step 6 asks for on a failed rotation.
/// </para></summary>
public sealed class NinaClient
{
    /// <summary>Spec 12.16, "The timeout": every request is bounded at 5 seconds through a linked
    /// <see cref="CancellationTokenSource"/> and never by setting <see cref="HttpClient.Timeout"/>,
    /// which belongs to the catalogue resolver and would be rewritten for the process's life.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Spec 12.16 step 5, web parity: <c>set-coordinates</c> triggers a sky survey image
    /// reload in NINA that resets the rotation, so a rotation sent before that reload finishes is
    /// discarded. The wait is outside any request's timeout.</summary>
    private static readonly TimeSpan RotationWait = TimeSpan.FromSeconds(2);

    private readonly HttpClient _http;
    private readonly TimeSpan _requestTimeout;

    /// <param name="http">The one integration <see cref="HttpClient"/> singleton
    /// <c>AppHost</c> registers for both clients (ruling B1). This client never touches its
    /// <see cref="HttpClient.Timeout"/>.</param>
    /// <param name="requestTimeout">Overrides the 5 second bound; null (every production caller)
    /// keeps it.</param>
    public NinaClient(HttpClient http, TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _requestTimeout = requestTimeout ?? RequestTimeout;
    }

    /// <summary>Sends the target's coordinates to one NINA instance, then, when the target carries
    /// a position angle, its rotation.</summary>
    /// <param name="url">The stored instance URL. One that fails
    /// <see cref="IntegrationUrl.TryParse"/> ends the action here with
    /// <see cref="IntegrationMessages.BadUrl"/> and <b>no call at all</b>, which is spec 12.16's
    /// fail-closed second enforcement point.</param>
    /// <param name="ra">Right ascension in degrees, the header block's own value.</param>
    /// <param name="dec">Declination in degrees.</param>
    /// <param name="positionAngle">Position angle in degrees, or null when the target carries
    /// none, in which case the rotation call is not made and its absence is not reported.</param>
    /// <param name="token">The caller's token. A cancellation through it ends the action as a
    /// failure like any other, rather than throwing at the caller.</param>
    public async Task<NinaSendResult> SendCoordinatesAsync(
        string? url,
        double ra,
        double dec,
        double? positionAngle,
        CancellationToken token = default)
    {
        if (!IntegrationUrl.TryParse(url, out var baseAddress))
        {
            return new NinaSendResult(false, NinaRotation.None, IntegrationMessages.BadUrl, null);
        }

        // Invariant throughout: a culture that writes a decimal comma would put a second argument
        // separator inside a query value (spec 12.16 step 2).
        var coordinates = string.Create(
            CultureInfo.InvariantCulture,
            $"{baseAddress}/v2/api/framing/set-coordinates?RAangle={ra}&DecAngle={dec}");

        try
        {
            await GetAsync(coordinates, token).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            return new NinaSendResult(
                false, NinaRotation.None, IntegrationMessages.NinaFailed, failure);
        }

        if (positionAngle is not { } angle)
        {
            return new NinaSendResult(true, NinaRotation.None, null, null);
        }

        var rotation = string.Create(
            CultureInfo.InvariantCulture,
            $"{baseAddress}/v2/api/framing/set-rotation?rotation={angle}");

        try
        {
            await Task.Delay(RotationWait, token).ConfigureAwait(false);
            await GetAsync(rotation, token).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            // Spec 12.16 step 6: the coordinates did land, so the action succeeded and the
            // FailureMessage stays null; Rotation carries the fact the reader needs.
            return new NinaSendResult(true, NinaRotation.Failed, null, failure);
        }

        return new NinaSendResult(true, NinaRotation.Sent, null, null);
    }

    // No response body is parsed: NINA's framing endpoints answer with a status, and the status is
    // the whole of what is read.
    private async Task GetAsync(string requestUri, CancellationToken token)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(token);
        bound.CancelAfter(_requestTimeout);
        using var response = await _http
            .GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, bound.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}
