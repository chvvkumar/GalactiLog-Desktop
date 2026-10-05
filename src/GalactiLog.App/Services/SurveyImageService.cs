using GalactiLog.Core.Io;
using GalactiLog.Core.Settings;
using GalactiLog.Core.Survey;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.Services;

/// <summary>Spec 11.3 and 12.4: what the Sky view window draws for one request.</summary>
public enum SurveyImageOutcome
{
    /// <summary>A cached or fetched JPEG.</summary>
    Image,

    /// <summary><c>general.survey_downloads_enabled</c> is off; nothing was read or requested.</summary>
    SwitchOff,

    /// <summary>The fetch failed; no file was written or removed.</summary>
    Failed,

    /// <summary>The fetch was cancelled; no file was written or removed.</summary>
    Cancelled,
}

/// <summary>Spec 11.3: an outcome, and the bytes when it is <see cref="SurveyImageOutcome.Image"/>.</summary>
public sealed record SurveyImageResult(SurveyImageOutcome Outcome, byte[]? Jpeg);

/// <summary>Spec 11.3's survey image cache in front of <see cref="Hips2FitsClient"/>, the one gate
/// every Sky view request passes.</summary>
public sealed class SurveyImageService(
    Hips2FitsClient client,
    AppWriter writer,
    JobRegistry jobs,
    Func<GeneralSettings> getGeneral,
    ILogger? logger = null)
{
    /// <summary>Spec 11.3: the registered job's kind.</summary>
    public const string FetchJobKind = "survey_image_fetch";

    /// <summary>Spec 11.3: the registered job's title.</summary>
    public const string FetchJobTitle = "Sky view image";

    /// <summary>Spec 11.3: the folder under the thumbnail cache root.</summary>
    public const string Directory = "survey";

    /// <summary>Spec 11.3: the most files one target's folder keeps.</summary>
    public const int PerTargetCap = 20;

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>Spec 11.3 and 12.4: the switch, then a hit unless refreshing, then one registered
    /// fetch that overwrites and sweeps on success and leaves any cached file otherwise.</summary>
    public async Task<SurveyImageResult> GetAsync(Guid targetId, SurveyView view, bool refresh, CancellationToken token)
    {
        var folder = Path.Combine(Directory, targetId.ToString("D"));

        // The settings load and the reparse walk stay off the caller's thread, which is the UI's.
        var (enabled, path, hit) = await Task.Run(() =>
        {
            if (!getGeneral().SurveyDownloadsEnabled)
            {
                return (false, "", (byte[]?)null);
            }

            var resolved = writer.ResolveThumbnailPath(Path.Combine(folder, view.CacheKey() + ".jpg"));
            return (true, resolved, refresh ? null : TryRead(resolved));
        }, CancellationToken.None);

        if (!enabled)
        {
            return new SurveyImageResult(SurveyImageOutcome.SwitchOff, null);
        }

        if (hit is not null)
        {
            return new SurveyImageResult(SurveyImageOutcome.Image, hit);
        }

        var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var job = jobs.Begin(FetchJobKind, FetchJobTitle, () =>
        {
            // The monitor's cancel may arrive after the fetch ended and the source was disposed.
            try { cancel.Cancel(); } catch (ObjectDisposedException) { }
        });

        Hips2FitsResult result;
        using (cancel)
        {
            result = await client.FetchAsync(view, cancel.Token);
        }

        switch (result.Status)
        {
            case Hips2FitsStatus.Ok:
                var jpeg = result.Jpeg!;
                try
                {
                    await Task.Run(() => WriteAndSweep(folder, path, jpeg), CancellationToken.None);
                }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or UnauthorizedPathException)
                {
                    // The fetched bytes are still drawn; only the cache copy is lost.
                    _logger.LogWarning(failure, "Could not cache the {SurveyId} survey image", view.SurveyId);
                }
                job.Finish(JobResult.Succeeded, "");
                return new SurveyImageResult(SurveyImageOutcome.Image, jpeg);

            case Hips2FitsStatus.Cancelled:
                job.Finish(JobResult.Cancelled, "");
                return new SurveyImageResult(SurveyImageOutcome.Cancelled, null);

            default:
                _logger.LogWarning(
                    result.Failure, "The {SurveyId} survey image fetch ended {Status}", view.SurveyId, result.Status);
                job.Finish(JobResult.Failed, SurveyMessages.LoadFailed);
                return new SurveyImageResult(SurveyImageOutcome.Failed, null);
        }
    }

    // An unreadable hit is served as a miss, so a damaged file is refetched rather than drawn.
    private static byte[]? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void WriteAndSweep(string folder, string written, byte[] jpeg)
    {
        writer.WriteAllBytes(written, jpeg);
        var files = writer.EnumerateThumbnailFiles(folder, "*.jpg");
        var excess = files.Count - PerTargetCap;
        foreach (var file in files
                     .Where(f => !string.Equals(f.Path, written, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(f => f.LastWriteUtc)
                     .ThenBy(f => f.Path, StringComparer.Ordinal)
                     .Take(Math.Max(excess, 0)))
        {
            writer.Delete(file.Path);
        }
    }
}
