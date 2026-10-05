using System.Text.Json;
using GalactiLog.App.ViewModels.TargetDetail.Wbpp;
using GalactiLog.Core.Settings;
using GalactiLog.Core.Wbpp;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.Tests.ViewModels.Wbpp;

/// <summary>
/// The frames, gradings and settings documents spec 12.13's quality panel cases are built from.
/// Shared by the view-model cases and the view cases so a layout case and a behaviour case judge
/// the same rows.
/// </summary>
/// <remarks>No real user path and no site coordinate anywhere: every path below is under the
/// fixture root.</remarks>
internal static class QualityPanelTestFactory
{
    public const string Rig = "Esprit 100 / ASI2600MM";

    public static readonly DateOnly Night = new(2025, 3, 20);

    /// <summary>One frame. Every metric is nullable, so a case names only what it is about.
    /// <paramref name="withoutCaptureDate"/> is how a case asks for a frame with no capture time,
    /// because a null <paramref name="captureDate"/> means "take the fixture's own".</summary>
    public static WbppFrame Frame(
        string fileName,
        double? hfr = null,
        double? eccentricity = null,
        double? fwhm = null,
        int? stars = null,
        double? rms = null,
        DateTime? captureDate = null,
        bool withoutCaptureDate = false,
        string? filterUsed = "Ha",
        DateOnly? night = null,
        string rig = Rig,
        Guid? imageId = null)
        => new(
            imageId ?? Guid.NewGuid(),
            night ?? Night,
            @"C:\tmp\p16-fixtures\M31\" + fileName,
            fileName,
            withoutCaptureDate ? null : captureDate ?? new DateTime(2025, 3, 20, 21, 0, 0, DateTimeKind.Utc),
            filterUsed,
            rig,
            "Esprit 100",
            "ASI2600MM",
            hfr,
            eccentricity,
            fwhm,
            stars,
            rms);

    /// <summary>A grading whose nine grades are all the neutral band, so a case that is not about
    /// colour is not silently also about colour.</summary>
    public static FrameGrading NeutralGrading()
    {
        var neutral = new MetricGrade(0d, 2d);
        return new FrameGrading(neutral, neutral, neutral, neutral, neutral, neutral, neutral, neutral, neutral);
    }

    /// <summary>A grading carrying a different deviation for the session and the rig halves of each
    /// pair, so the baseline toggle has something to select between.</summary>
    public static FrameGrading SplitGrading(double sessionZ, double rigZ, double starsZ = 0d)
    {
        var session = new MetricGrade(sessionZ, 2d);
        var rig = new MetricGrade(rigZ, 2d);
        var starGrade = new MetricGrade(starsZ, 900d);
        var rms = new MetricGrade(4d, 0.5d);
        return new FrameGrading(
            session, rig,
            session, rig,
            session, rig,
            starGrade,
            new MetricGrade(0d, 1000d),
            rms);
    }

    public static QualityPanelFrame Entry(WbppFrame frame, FrameGrading? grading = null)
        => new(frame, grading);

    /// <summary>A <c>general</c> document holding the given rigs' quality entries and nothing
    /// else, built through the one writer the panel itself uses.</summary>
    public static GeneralSettings General(params (string RigKey, WbppQualityState State)[] entries)
    {
        JsonElement? document = null;
        foreach (var (rigKey, state) in entries)
        {
            document = WbppSettingsRead.WriteQualityForRig(document, rigKey, state);
        }

        return new GeneralSettings { WbppQualityByRigDocument = document };
    }

    /// <summary>A <c>general</c> document whose <c>wbpp_quality_by_rig</c> holds a malformed entry
    /// for one rig: a value that is not an object at all.</summary>
    public static GeneralSettings GeneralWithMalformedEntry(string rigKey)
    {
        var document = JsonSerializer.SerializeToElement(
            new Dictionary<string, object> { [rigKey] = "not an object" });

        return new GeneralSettings { WbppQualityByRigDocument = document };
    }

    /// <summary>The debounce seam every view-model takes, driven by the case rather than by a
    /// clock, so a coalescing case asserts a call count instead of sleeping.</summary>
    internal sealed class DelaySeam
    {
        private TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Func<TimeSpan, CancellationToken, Task> Seam => (_, token) => _gate.Task.WaitAsync(token);

        /// <summary>Ends the open window. A window opened afterwards waits on a fresh gate, which
        /// is what makes "two separate windows" expressible.</summary>
        public void Elapse()
        {
            var open = _gate;
            _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            open.TrySetResult();
        }
    }

    /// <summary>A <c>MutateGeneral</c> delegate that records every call and applies it to an
    /// in-memory document, which is what a per-rig case reads back.</summary>
    internal sealed class SettingsSpy(GeneralSettings? seed = null)
    {
        private readonly Lock _gate = new();

        public GeneralSettings Current { get; private set; } = seed ?? new GeneralSettings();

        public int Calls { get; private set; }

        /// <summary>Set to make every write throw, which is the failed-save case.</summary>
        public bool Throws { get; set; }

        public GeneralSettings Mutate(Func<GeneralSettings, GeneralSettings> mutate)
        {
            lock (_gate)
            {
                Calls++;
                if (Throws)
                {
                    throw new InvalidOperationException("the settings database is unavailable");
                }

                Current = mutate(Current);
                return Current;
            }
        }

        public WbppQualityState StateFor(string rigKey)
            => WbppSettingsRead.ReadQualityByRig(Current.WbppQualityByRigDocument).For(rigKey);
    }
}
