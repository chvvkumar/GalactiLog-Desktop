using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using GalactiLog.App.Services;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.TargetDetail;

/// <summary>
/// Spec 12.4's raw header panel: every <c>raw_headers</c> key sorted with a filter box,
/// <c>COMMENT</c>/<c>HISTORY</c> rendered as the multi-line blocks their stored arrays carry, a
/// Derived metrics section pairing each stored metric with its provenance string (spec 7.3), and
/// the two FWHM rows spec 7.1.1 requires stay visually distinct.
/// </summary>
/// <remarks>
/// <para>
/// Built by <c>FrameRowViewModel</c> the first time a row's panel is expanded, keyed on the
/// frame's <c>images.id</c> alone (out-of-scope note: Phase 8's preview modal reuses this
/// unchanged, keyed on the previewed frame's id). <see cref="Load"/> issues one read through
/// <c>FrameHeadersQuery.Get</c> off the calling thread and publishes back through
/// <paramref name="post"/> below, the same shape <c>SessionCardViewModel</c> uses for its own
/// expansion query.
/// </para>
/// <para>
/// Every metric other than the two FWHM columns is not in <c>FrameHeadersQuery</c>'s result: the
/// query deliberately selects only <c>raw_headers</c>, <c>provenance</c>, <c>median_fwhm</c> and
/// <c>fwhm</c> (design lessons rule 1, <c>SessionDetailQuery</c> already carries the rest). The
/// Derived metrics section instead reads the <see cref="FrameRow"/> the frame table already
/// holds, passed in here as <paramref name="row"/>, so this panel never issues a second query for
/// values <c>SessionDetailQuery</c> returned (Task 6 handoff). <paramref name="row"/> is optional:
/// a caller with only an image id still gets the header list and the two FWHM rows, just no
/// Derived metrics section, which keeps "takes an image id and nothing else" true for a future
/// caller that has no row at hand.
/// </para>
/// </remarks>
public sealed partial class RawHeaderPanelViewModel : ObservableObject
{
    private readonly Guid _imageId;
    private readonly Func<Guid, FrameHeaders?> _get;
    private readonly FrameRow? _row;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;
    private readonly CancellationToken _lifetime;

    private bool _started;

    /// <param name="imageId">The frame whose headers this panel shows.</param>
    /// <param name="get">Normally <c>FrameHeadersQuery.Get</c>.</param>
    /// <param name="row">The frame table's own read model for this frame, supplying every value
    /// the Derived metrics section renders (Task 6 handoff). Null renders that section empty
    /// rather than issuing a second query.</param>
    /// <param name="post">How to reach the UI thread. Defaults to <c>UiPost.Default</c>.</param>
    /// <param name="logger">Optional. A failed read is logged and surfaced on the panel, never
    /// rethrown on the UI thread.</param>
    /// <param name="lifetime">The owning frame table's lifetime (phase review item 4). The read
    /// itself is one indexed row of <c>images</c> and is not cancellable mid-flight, so this is
    /// checked immediately before the publish: a panel whose table has been disposed, because its
    /// session card collapsed or the detail page closed, drops the result instead of posting to a
    /// dispatcher that may no longer be running. Default is a token that is never cancelled, which
    /// is what a caller with no owning table wants.</param>
    public RawHeaderPanelViewModel(
        Guid imageId,
        Func<Guid, FrameHeaders?> get,
        FrameRow? row = null,
        Action<Action>? post = null,
        ILogger? logger = null,
        CancellationToken lifetime = default)
    {
        _imageId = imageId;
        _get = get;
        _row = row;
        _post = post ?? UiPost.Default;
        _logger = logger ?? NullLogger.Instance;
        _lifetime = lifetime;
    }

    /// <summary>
    /// Starts the one read. Called by the row the first time the panel is expanded. Idempotent: a
    /// second call while a read is in flight or after it completed does nothing.
    /// </summary>
    public void Load()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        IsLoading = true;

        PendingLoad = Task.Run(() =>
        {
            try
            {
                var result = _get(_imageId);
                Post(() => Publish(result, null));
            }
            catch (Exception ex)
            {
                Post(() => Publish(null, ex));
            }
        });
    }

    /// <summary>The in-flight (or completed) read, so a test can join it instead of sleeping.
    /// Mirrors <c>SessionCardViewModel.PendingLoad</c>.</summary>
    internal Task? PendingLoad { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEmptyState))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEmptyState))]
    public partial Exception? LastFailure { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEmptyState))]
    public partial bool IsMissing { get; private set; }

    /// <summary>Spec 12.4's filter box. Filters on the key and on every line, case-insensitive,
    /// substring. No debounce: the list is at most a few hundred entries already in memory and a
    /// debounce would make the box feel laggy for no gain.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(FilteredHeaders), nameof(HasVisibleHeaders), nameof(EmptyStateText), nameof(HasEmptyState))]
    public partial string Filter { get; set; } = "";

    /// <summary>Every header entry, sorted, in the query's order.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(FilteredHeaders), nameof(HasHeaders), nameof(HasVisibleHeaders),
        nameof(EmptyStateText), nameof(HasEmptyState))]
    public partial IReadOnlyList<HeaderEntryViewModel> Headers { get; private set; } = [];

    public IEnumerable<HeaderEntryViewModel> FilteredHeaders => string.IsNullOrEmpty(Filter)
        ? Headers
        : Headers.Where(entry => entry.Matches(Filter));

    public bool HasHeaders => Headers.Count > 0;

    public bool HasVisibleHeaders => FilteredHeaders.Any();

    /// <summary>The empty-state wording: a non-empty document that the filter excludes everything
    /// from reads differently than a frame with no headers at all.</summary>
    public string EmptyStateText => HasHeaders
        ? "No headers match this filter."
        : "No raw headers recorded for this frame.";

    /// <summary>Gates <see cref="EmptyStateText"/>: a failed read or a pruned frame render their
    /// own line instead (review finding 2), and a read still in flight renders the loading
    /// indicator instead, so the generic empty-state sentence must not show through any of those.
    /// </summary>
    public bool HasEmptyState => !HasVisibleHeaders && LastFailure is null && !IsMissing && !IsLoading;

    /// <summary>Spec 12.4's Derived metrics section: one row per stored metric with its value and
    /// its provenance string.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDerivedMetrics))]
    public partial IReadOnlyList<DerivedMetricViewModel> DerivedMetrics { get; private set; } = [];

    /// <summary>Gates the "Derived metrics" section heading: a frame with no stored metric at all
    /// (no <see cref="FrameRow"/> handed in, or one whose fields are all null) renders no heading
    /// over an empty list.</summary>
    public bool HasDerivedMetrics => DerivedMetrics.Count > 0;

    /// <summary>Spec 7.1.1 and 12.4. The two rows are separate properties with separate labels so
    /// no template can accidentally render them as one figure.</summary>
    public string HeaderFwhmLabel => "Header FWHM";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFwhmSection))]
    public partial string HeaderFwhmText { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFwhmSection))]
    public partial bool HasHeaderFwhm { get; private set; }

    public string FwhmArcsecLabel => "FWHM (arcsec)";

    [ObservableProperty]
    public partial string FwhmArcsecText { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFwhmSection))]
    public partial bool HasFwhmArcsec { get; private set; }

    /// <summary>Gates the separator border the two FWHM rows sit behind: absent when neither row
    /// has anything to show, so an unmeasured frame does not render a bare rule with nothing under
    /// it.</summary>
    public bool HasFwhmSection => HasHeaderFwhm || HasFwhmArcsec;

    // The one exit from the background read. Nothing is published once the owning table is gone.
    private void Post(Action publish)
    {
        if (!_lifetime.IsCancellationRequested)
        {
            _post(publish);
        }
    }

    // Runs on the UI thread through the post seam.
    private void Publish(FrameHeaders? result, Exception? failure)
    {
        IsLoading = false;

        if (failure is not null)
        {
            LastFailure = failure;
            _logger.LogWarning(failure, "Loading raw headers for frame {ImageId} failed", _imageId);
            return;
        }

        if (result is null)
        {
            IsMissing = true;
            return;
        }

        Headers = [.. result.RawHeaders.Select(entry => new HeaderEntryViewModel(entry.Key, entry.Lines))];
        DerivedMetrics = BuildDerivedMetrics(_row, result.Provenance);

        // Spec 7.1.1: no unit is appended to the header value, and the parenthetical source
        // keyword is omitted rather than guessed when the provenance entry is missing.
        // MetricText.Format is what every other metric in this application renders a nullable
        // double through (review finding 5): it also excludes a non-finite value, which a raw
        // ToString call would have rendered as "NaN" or "Infinity".
        var headerFwhm = MetricText.Format(result.MedianFwhm, "0.00");
        HasHeaderFwhm = headerFwhm.Length > 0;
        var sourceKeyword = result.Provenance.TryGetValue("median_fwhm", out var keyword) ? keyword : null;
        HeaderFwhmText = HasHeaderFwhm
            ? headerFwhm + (sourceKeyword is null ? "" : $" ({sourceKeyword})")
            : "";

        HasFwhmArcsec = result.Fwhm is not null;
        FwhmArcsecText = MetricText.Format(result.Fwhm, "0.00");
    }

    // ---- the Derived metrics table --------------------------------------------------------
    //
    // The join between spec 7.1's field table and this frame's values, in spec 7.1's row order.
    // The only place in the application that maps a stored field name to a human label for this
    // purpose (Task 6 brief); FrameColumns' labels are a separate table on purpose (collision-map
    // designated owners), so a header-panel wording change never touches the frame table's column
    // titles and vice versa. hfr_stdev and altitude_deg are not on FrameRow yet and are left for
    // whichever task adds them a column (Task 6 handoff).
    //
    // fwhm and median_fwhm are excluded here: they render as the two dedicated rows above, never
    // through this generic list, so nothing can fold them back into one row by accident.
    private sealed record MetricField(string Key, string Label, Func<FrameRow, (bool HasValue, string Text)> Resolve);

    private static (bool, string) Num(double? value, string format, string suffix = "")
        => (value is not null, MetricText.Format(value, format, suffix));

    private static (bool, string) Int(int? value)
        => (value is not null, value is { } present ? present.ToString("N0", CultureInfo.InvariantCulture) : "");

    private static (bool, string) TextOf(string? value)
        => (!string.IsNullOrWhiteSpace(value), value ?? "");

    private static readonly MetricField[] MetricFields =
    [
        new("exposure_time", "Exposure", row => Num(row.ExposureTime, "0.##", " s")),
        new("filter_used", "Filter", row => TextOf(row.FilterUsed)),
        new("sensor_temp", "Sensor temp", row => Num(row.SensorTemp, "0.0")),
        new("camera_gain", "Camera gain", row => Int(row.CameraGain)),
        new("rotator_position", "Rotator position", row => Num(row.RotatorPosition, "0.0")),
        new("median_hfr", "HFR", row => Num(row.MedianHfr, "0.00")),
        new("eccentricity", "Eccentricity", row => Num(row.Eccentricity, "0.00")),
        new("detected_stars", "Detected stars", row => Int(row.DetectedStars)),
        new("guiding_rms_arcsec", "Guiding RMS", row => Num(row.GuidingRmsArcsec, "0.00")),
        new("guiding_rms_ra_arcsec", "Guiding RMS RA", row => Num(row.GuidingRmsRaArcsec, "0.00")),
        new("guiding_rms_dec_arcsec", "Guiding RMS Dec", row => Num(row.GuidingRmsDecArcsec, "0.00")),
        new("adu_stdev", "ADU sigma", row => Num(row.AduStdev, "N0")),
        new("adu_mean", "ADU mean", row => Num(row.AduMean, "N0")),
        new("adu_median", "ADU median", row => Num(row.AduMedian, "N0")),
        new("adu_min", "ADU min", row => Int(row.AduMin)),
        new("adu_max", "ADU max", row => Int(row.AduMax)),
        new("focuser_position", "Focuser position", row => Int(row.FocuserPosition)),
        new("focuser_temp", "Focuser temp", row => Num(row.FocuserTemp, "0.0")),
        new("pier_side", "Pier side", row => TextOf(row.PierSide)),
        new("airmass", "Airmass", row => Num(row.Airmass, "0.00")),
        new("ambient_temp", "Ambient temp", row => Num(row.AmbientTemp, "0.0")),
        new("dew_point", "Dew point", row => Num(row.DewPoint, "0.0")),
        new("humidity", "Humidity", row => Num(row.Humidity, "0")),
        new("pressure", "Pressure", row => Num(row.Pressure, "0")),
        new("wind_speed", "Wind speed", row => Num(row.WindSpeed, "0.0")),
        new("wind_direction", "Wind direction", row => Num(row.WindDirection, "0")),
        new("wind_gust", "Wind gust", row => Num(row.WindGust, "0.0")),
        new("cloud_cover", "Cloud cover", row => Num(row.CloudCover, "0")),
        new("sky_quality", "Sky quality", row => Num(row.SkyQuality, "0.00")),
    ];

    private static IReadOnlyList<DerivedMetricViewModel> BuildDerivedMetrics(
        FrameRow? row, IReadOnlyDictionary<string, string> provenance)
    {
        if (row is null)
        {
            return [];
        }

        var metrics = new List<DerivedMetricViewModel>();
        foreach (var field in MetricFields)
        {
            var (hasValue, text) = field.Resolve(row);
            if (!hasValue)
            {
                // A field with a provenance entry but no value is a stale document (spec 7.3: only
                // fields that received a value are present), so it renders no row at all rather
                // than a provenance for a value that is not there.
                continue;
            }

            metrics.Add(new DerivedMetricViewModel(
                field.Label,
                text,
                provenance.TryGetValue(field.Key, out var source) ? source : ""));
        }

        return metrics;
    }
}

/// <param name="Key">The header key, rendered monospace.</param>
/// <param name="Lines">One or many. A multi-line entry renders as a block.</param>
public sealed record HeaderEntryViewModel(string Key, IReadOnlyList<string> Lines)
{
    public bool IsMultiLine => Lines.Count > 1;

    public string SingleLine => Lines.Count > 0 ? Lines[0] : "";

    public bool Matches(string filter)
        => Key.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || Lines.Any(line => line.Contains(filter, StringComparison.OrdinalIgnoreCase));
}

/// <param name="Label">The human label for the stored field.</param>
/// <param name="ValueText">The formatted value.</param>
/// <param name="Provenance">The provenance string from <c>images.provenance</c>, rendered as
/// secondary text (spec 7.3: "The raw header panel renders each stored metric with its provenance
/// string as secondary text"). Empty when the field has no provenance entry.</param>
public sealed record DerivedMetricViewModel(string Label, string ValueText, string Provenance)
{
    public bool HasProvenance => Provenance.Length > 0;
}
