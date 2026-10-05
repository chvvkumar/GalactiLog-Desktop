using System.Net;

namespace GalactiLog.Core.Survey;

/// <summary>Spec 11.3: how one hips2fits request ended.</summary>
public enum Hips2FitsStatus
{
    /// <summary>A 200 with an <c>image/jpeg</c> body.</summary>
    Ok,

    /// <summary>Any other status or content type, a transport failure, or the 15 second bound.</summary>
    Failed,

    /// <summary>The caller's token, and nothing else.</summary>
    Cancelled,
}

/// <summary>Spec 11.3: a request's outcome; <see cref="Jpeg"/> is non-null exactly when
/// <see cref="Status"/> is <see cref="Hips2FitsStatus.Ok"/>.</summary>
public sealed record Hips2FitsResult(Hips2FitsStatus Status, byte[]? Jpeg, Exception? Failure);

/// <summary>Spec 11.3: the one hips2fits endpoint, over the host's integration
/// <see cref="HttpClient"/>, whose <see cref="HttpClient.Timeout"/> this client never touches.</summary>
public sealed class Hips2FitsClient(HttpClient http, TimeSpan? requestTimeout = null)
{
    /// <summary>Spec 11.3: the one host and path.</summary>
    public const string Endpoint = "https://alasky.cds.unistra.fr/hips-image-services/hips2fits";

    /// <summary>Spec 11.3: the bound on one request when the caller passes none.</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly TimeSpan _requestTimeout = requestTimeout ?? DefaultRequestTimeout;

    /// <summary>Spec 11.3: one GET for the view, the body buffered whole.</summary>
    public async Task<Hips2FitsResult> FetchAsync(SurveyView view, CancellationToken token)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(token);
        bound.CancelAfter(_requestTimeout);
        try
        {
            using var response = await _http
                .GetAsync($"{Endpoint}?{view.QueryString()}", HttpCompletionOption.ResponseHeadersRead, bound.Token)
                .ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return Failed(new HttpRequestException(
                    $"hips2fits answered {(int)response.StatusCode}", null, response.StatusCode));
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.Equals(mediaType, "image/jpeg", StringComparison.OrdinalIgnoreCase))
            {
                return Failed(new HttpRequestException($"hips2fits answered content type {mediaType}"));
            }

            var jpeg = await response.Content.ReadAsByteArrayAsync(bound.Token).ConfigureAwait(false);
            return new Hips2FitsResult(Hips2FitsStatus.Ok, jpeg, null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new Hips2FitsResult(Hips2FitsStatus.Cancelled, null, null);
        }
        catch (Exception failure)
        {
            return Failed(failure);
        }
    }

    private static Hips2FitsResult Failed(Exception failure) => new(Hips2FitsStatus.Failed, null, failure);
}
