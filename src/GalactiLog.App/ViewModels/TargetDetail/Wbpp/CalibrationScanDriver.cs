using System.Diagnostics;
using GalactiLog.Core.Io;
using GalactiLog.Core.Wbpp;
using Microsoft.Extensions.Logging;

namespace GalactiLog.App.ViewModels.TargetDetail.Wbpp;

/// <summary>
/// Runs each night's calibration scan for <see cref="WbppExportViewModel"/>: walks the start level,
/// climbs while the parent holds only this target and a light set still lacks flats, and hands the
/// result to the night. All disk access is the injected walker's.
/// </summary>
/// <remarks>
/// The walk runs on <c>Task.Run</c>; every write to the night goes through the page's post and is
/// dropped when the page has closed, the night's scan was restarted (generation), or its token was
/// cancelled. One cancellation source per night, linked to the page's lifetime token; a Rescan
/// cancels the running one first. Members other than the worker body are called on the UI thread.
/// </remarks>
internal sealed class CalibrationScanDriver(
    CalibrationWalk.Walker walk,
    Func<ContaminationIndex?> catalogue,
    Func<NightRule> nightRule,
    Func<SessionLevelViewModel, IReadOnlyList<LightSet>> lightSets,
    Action<SessionLevelViewModel> published,
    Action<Action> post,
    Func<bool> disposed,
    CancellationToken closing,
    ILogger logger)
{
    private readonly Dictionary<DateOnly, CancellationTokenSource> _running = [];
    private readonly Dictionary<DateOnly, int> _generation = [];

    /// <summary>Scans a night afresh from its start level.</summary>
    public void Start(SessionLevelViewModel session)
    {
        if (session.HasLevels)
        {
            Run(session, CalibrationClimb.StartLevel(session.Levels, session.ChosenIndex), manual: false);
        }
    }

    /// <summary>Climbs one level from the last scan, past the other-targets guard.</summary>
    public void LookHigher(SessionLevelViewModel session)
    {
        if (session.ScanState == CalibrationScanState.Done
            && session.ScannedLevelIndex >= 0
            && CalibrationClimb.ManualNext(session.Levels, session.ScannedLevelIndex) is int parent)
        {
            Run(session, parent, manual: true);
        }
    }

    /// <summary>Cancels every night's scan. The page's own token cancels them too; this is the
    /// explicit half for <c>Dispose</c>.</summary>
    public void CancelAll()
    {
        foreach (var source in _running.Values)
        {
            source.Cancel();
        }
    }

    private void Run(SessionLevelViewModel session, int startIndex, bool manual)
    {
        var night = session.Night;
        if (_running.Remove(night, out var previous))
        {
            previous.Cancel();
        }

        var source = CancellationTokenSource.CreateLinkedTokenSource(closing);
        _running[night] = source;
        var generation = _generation[night] = _generation.GetValueOrDefault(night) + 1;
        var token = source.Token;

        var levels = session.Levels;
        var sets = lightSets(session);
        var rule = nightRule();
        var index = catalogue();
        IReadOnlyList<ScannedFrame> kept = manual ? session.ScannedFrames : [];
        IReadOnlyList<string> keptRoots = manual ? session.ScannedRoots : [];
        IReadOnlyList<ScanIssue> keptIssues = manual ? session.Issues : [];
        var keptHeaders = manual ? session.HeadersRead : 0;
        session.BeginScan(levels.Levels[startIndex].Path, fresh: !manual);

        // Read on the UI thread, inside a post.
        bool Current() => !disposed() && !token.IsCancellationRequested && _generation[night] == generation;

        void Post(Action action)
        {
            if (!token.IsCancellationRequested)
            {
                post(() =>
                {
                    if (Current())
                    {
                        action();
                    }
                });
            }
        }

        _ = Task.Run(
            () =>
            {
                var stopwatch = Stopwatch.StartNew();
                var frames = new List<ScannedFrame>(kept);
                var seen = new HashSet<string>(frames.Select(frame => frame.FilePath), StringComparer.OrdinalIgnoreCase);
                var roots = new List<string>(keptRoots);
                var issues = new List<ScanIssue>(keptIssues);
                var headers = keptHeaders;
                var level = startIndex;
                try
                {
                    while (true)
                    {
                        var root = levels.Levels[level].Path;
                        var earlier = roots.ToArray();
                        var before = headers;
                        var progress = new Throttled(count => Post(() => session.ReportProgress(before + count, root)));

                        // A climb walks only what it newly exposes: folders an earlier step read are
                        // sized again but not header-read, and the first record of a file is kept.
                        var result = walk(
                            root,
                            dir => (index?.HoldsLights(dir) ?? false) || earlier.Any(done => PathConfinement.IsUnderOrEqual(done, dir)),
                            rule,
                            progress,
                            token);
                        token.ThrowIfCancellationRequested();

                        frames.AddRange(result.Frames.Where(frame => seen.Add(frame.FilePath)));
                        issues.AddRange(result.Issues);
                        headers += result.HeadersRead;
                        roots.Add(root);

                        // No light sets: nothing to match, so the start level alone is scanned.
                        if (manual || sets.Count == 0 || CalibrationMatcher.Match(sets, night, frames).EveryFilterHasAFlat
                            || CalibrationClimb.AutomaticNext(levels, level) is not int next)
                        {
                            break;
                        }

                        level = next;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Never blocks the export: the night copies its lights alone.
                    var failed = levels.Levels[level].Path;
                    logger.LogWarning(ex, "Scanning {Folder} for calibration frames failed", failed);
                    Post(() =>
                    {
                        session.FailScan(failed);
                        published(session);
                        session.MarkDone();
                    });
                    return;
                }

                var elapsed = stopwatch.Elapsed;
                Post(() =>
                {
                    session.CompleteScan(frames, roots, level, headers, issues, elapsed);
                    published(session);
                    session.MarkDone();
                });

                // The Look in offer's file count: the parent listed, sizes only, no header read.
                if (sets.Count == 0
                    || CalibrationMatcher.Match(sets, night, frames).EveryFilterHasAFlat
                    || CalibrationClimb.ManualNext(levels, level) is not int parent)
                {
                    return;
                }

                try
                {
                    var listed = walk(levels.Levels[parent].Path, _ => true, rule, null, token);
                    var count = listed.Frames.Count(frame => !roots.Any(done => PathConfinement.IsUnderOrEqual(done, frame.FilePath)));
                    Post(() => session.SetLookHigherCount(parent, count));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex, "Counting the files under {Folder} failed", levels.Levels[parent].Path);
                }
                catch (OperationCanceledException)
                {
                    // The page closed or the night was rescanned.
                }
            },
            CancellationToken.None);
    }

    // At most one progress post per 100 ms, so a 36,000 header walk does not flood the UI thread.
    private sealed class Throttled(Action<int> report) : IProgress<int>
    {
        private long _next;

        public void Report(int value)
        {
            var now = Environment.TickCount64;
            if (now < _next)
            {
                return;
            }

            _next = now + 100;
            report(value);
        }
    }
}
