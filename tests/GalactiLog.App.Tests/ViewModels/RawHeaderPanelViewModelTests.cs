using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Design-spec 18.3's view-model tests for spec 12.4's raw header panel, and the roadmap Verify
// line: the two FWHM rows carry distinct labels, a stored metric renders its provenance string,
// and a repeated COMMENT renders every line.
public class RawHeaderPanelViewModelTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);
    private static readonly Guid ImageId = Guid.NewGuid();

    private static FrameHeaders Sample() => new(
        RawHeaders:
        [
            new HeaderEntry("GAIN", ["100"]),
            new HeaderEntry("OBJECT", ["M 51"]),
            new HeaderEntry("COMMENT", ["line one", "line two", "line three"]),
        ],
        Provenance: new Dictionary<string, string>
        {
            ["median_hfr"] = "csv:HFR",
            ["median_fwhm"] = "MEANFWHM",
        },
        MedianFwhm: 3.10,
        Fwhm: 2.85);

    private static FrameRow SampleRow(double? medianHfr = 2.30) => new(
        ImageId: ImageId,
        FilePath: @"C:\lib\m51.fits",
        FileName: "m51.fits",
        CaptureDate: null,
        FilterUsed: null,
        ExposureTime: null,
        MedianHfr: medianHfr,
        Eccentricity: null,
        Fwhm: 2.85,
        DetectedStars: null,
        GuidingRmsArcsec: null,
        GuidingRmsRaArcsec: null,
        GuidingRmsDecArcsec: null,
        GuidingRmsSource: null,
        AduMean: null,
        AduMedian: null,
        AduStdev: null,
        AduMin: null,
        AduMax: null,
        FocuserPosition: null,
        FocuserTemp: null,
        AmbientTemp: null,
        DewPoint: null,
        Humidity: null,
        Pressure: null,
        WindSpeed: null,
        WindDirection: null,
        WindGust: null,
        CloudCover: null,
        SkyQuality: null,
        Airmass: null,
        PierSide: null,
        RotatorPosition: null,
        SensorTemp: null,
        CameraGain: null,
        Rig: "Unknown",
        IsHfrOutlier: false,
        IsEccentricityOutlier: false);

    private sealed class Harness
    {
        public int Calls;
        public Guid RequestedId;
        public int CallThread;
        public FrameHeaders? Result;
        public Exception? Throws;

        /// <summary>Parks the read inside the query delegate until a test releases it (F18
        /// follow-up). Null leaves it unparked, which is what every other test wants.</summary>
        public ManualResetEventSlim? Release;

        public RecordingLogger Logger { get; } = new();

        public RawHeaderPanelViewModel Panel { get; }

        public Harness(
            FrameRow? row = null,
            Action<Action>? post = null,
            CancellationToken lifetime = default)
        {
            Result = Sample();
            Panel = new RawHeaderPanelViewModel(
                ImageId,
                id =>
                {
                    Interlocked.Increment(ref Calls);
                    RequestedId = id;
                    CallThread = Environment.CurrentManagedThreadId;
                    Release?.Wait(Budget);
                    if (Throws is not null)
                    {
                        throw Throws;
                    }

                    return Result;
                },
                row,
                post: post ?? (action => action()),
                logger: Logger,
                lifetime: lifetime);
        }

        public Harness Settle()
        {
            Panel.PendingLoad?.Wait(Budget);
            return this;
        }
    }

    [Fact]
    public void Load_ReadsOnceAndIsIdempotent()
    {
        var harness = new Harness();

        harness.Panel.Load();
        harness.Settle();
        harness.Panel.Load();
        harness.Panel.Load();

        Assert.Equal(1, harness.Calls);
        Assert.Equal(ImageId, harness.RequestedId);
    }

    // F18 follow-up, and the clearest case of the mechanism in this suite: this test used to
    // compare thread ids after Settle() blocked on the read's own Task, which is exactly what lets
    // the thread pool inline that read onto the waiting thread under saturation. It now parks the
    // read and observes that Load returned anyway (TRACKING section 2 item 8).
    [Fact]
    public async Task Load_RunsOffTheCallingThread()
    {
        using var release = new ManualResetEventSlim(false);
        var harness = new Harness { Release = release };

        harness.Panel.Load();

        // Load returned while the read is parked. A synchronous read would have parked Load.
        Assert.False(harness.Panel.PendingLoad!.IsCompleted);
        Assert.Empty(harness.Panel.Headers);

        release.Set();
        await harness.Panel.PendingLoad!;

        Assert.Equal(1, harness.Calls);
        Assert.NotEmpty(harness.Panel.Headers);
    }

    [Fact]
    public void Load_WithACancelledOwnerLifetime_PublishesNothing()
    {
        // Phase review item 4. The read is one indexed row and is not cancellable mid-flight, so
        // the token is checked at the publish: a panel whose frame table has been disposed, because
        // the card collapsed or the detail page closed, must not post to the dispatcher.
        using var lifetime = new CancellationTokenSource();
        var harness = new Harness(SampleRow(), lifetime: lifetime.Token);
        lifetime.Cancel();

        harness.Panel.Load();
        harness.Settle();

        Assert.Equal(1, harness.Calls);
        Assert.Empty(harness.Panel.Headers);
        Assert.Empty(harness.Panel.DerivedMetrics);
        Assert.True(harness.Panel.IsLoading);
    }

    [Fact]
    public void Load_NullResult_SetsIsMissing()
    {
        var harness = new Harness { Result = null };

        harness.Panel.Load();
        harness.Settle();

        Assert.True(harness.Panel.IsMissing);
        Assert.False(harness.Panel.IsLoading);
    }

    [Fact]
    public void Load_Throwing_SetsLastFailure()
    {
        var harness = new Harness { Throws = new InvalidOperationException("the database is locked") };

        harness.Panel.Load();
        harness.Settle();

        Assert.NotNull(harness.Panel.LastFailure);
        Assert.False(harness.Panel.IsLoading);
    }

    [Fact]
    public void Filter_MatchesOnKey()
    {
        var harness = new Harness();
        harness.Panel.Load();
        harness.Settle();

        harness.Panel.Filter = "gain";

        Assert.Equal(["GAIN"], harness.Panel.FilteredHeaders.Select(entry => entry.Key));
    }

    [Fact]
    public void Filter_MatchesOnValue()
    {
        var harness = new Harness();
        harness.Panel.Load();
        harness.Settle();

        harness.Panel.Filter = "line two";

        Assert.Equal(["COMMENT"], harness.Panel.FilteredHeaders.Select(entry => entry.Key));
    }

    [Fact]
    public void Filter_IsCaseInsensitive()
    {
        var harness = new Harness();
        harness.Panel.Load();
        harness.Settle();

        harness.Panel.Filter = "OBJ";

        Assert.Equal(["OBJECT"], harness.Panel.FilteredHeaders.Select(entry => entry.Key));
    }

    [Fact]
    public void Filter_NoMatch_SetsHasVisibleHeadersFalse()
    {
        var harness = new Harness();
        harness.Panel.Load();
        harness.Settle();

        harness.Panel.Filter = "no such key or value";

        Assert.False(harness.Panel.HasVisibleHeaders);
        Assert.Equal("No headers match this filter.", harness.Panel.EmptyStateText);
    }

    [Fact]
    public void EmptyStateText_NotifiesWhenHeadersLoadAndWhenFilterChanges()
    {
        // Review finding 1: in production order the row builds the panel, the view binds
        // EmptyStateText, and only then does Load's async Publish set Headers. Without
        // EmptyStateText in the Headers and Filter NotifyPropertyChangedFor lists, a bound
        // TextBlock would cache whatever it read at construction forever.
        var harness = new Harness();
        var notifications = new List<string?>();
        harness.Panel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RawHeaderPanelViewModel.EmptyStateText))
            {
                notifications.Add(harness.Panel.EmptyStateText);
            }
        };

        harness.Panel.Load();
        harness.Settle();

        Assert.NotEmpty(notifications);

        notifications.Clear();
        harness.Panel.Filter = "no such key or value";

        Assert.Equal(["No headers match this filter."], notifications);
    }

    [Fact]
    public void Filter_Cleared_RestoresEveryEntry()
    {
        var harness = new Harness();
        harness.Panel.Load();
        harness.Settle();

        harness.Panel.Filter = "gain";
        harness.Panel.Filter = "";

        Assert.Equal(3, harness.Panel.FilteredHeaders.Count());
    }

    [Fact]
    public void MultiLineEntry_ExposesEveryLine()
    {
        // The roadmap Verify line: a repeated COMMENT renders every line.
        var harness = new Harness();
        harness.Panel.Load();
        harness.Settle();

        var comment = harness.Panel.Headers.Single(entry => entry.Key == "COMMENT");

        Assert.True(comment.IsMultiLine);
        Assert.Equal(["line one", "line two", "line three"], comment.Lines);
    }

    [Fact]
    public void DerivedMetrics_RenderTheProvenanceString()
    {
        // The roadmap Verify line: a stored metric renders its provenance string.
        var harness = new Harness(row: SampleRow(medianHfr: 2.30));
        harness.Panel.Load();
        harness.Settle();

        var hfr = harness.Panel.DerivedMetrics.Single(metric => metric.Label == "HFR");

        Assert.Equal("2.30", hfr.ValueText);
        Assert.Equal("csv:HFR", hfr.Provenance);
        Assert.True(hfr.HasProvenance);
    }

    [Fact]
    public void DerivedMetrics_FieldWithNoProvenanceEntry_RendersAnEmptyProvenance()
    {
        // AmbientTemp carries a value but the sample provenance document names no "ambient_temp"
        // entry, unlike median_hfr's "csv:HFR": the gate is per field, not all-or-nothing.
        var harness = new Harness(row: SampleRow() with { AmbientTemp = 4.5 });
        harness.Panel.Load();
        harness.Settle();

        var ambient = harness.Panel.DerivedMetrics.Single(metric => metric.Label == "Ambient temp");

        Assert.Equal("", ambient.Provenance);
        Assert.False(ambient.HasProvenance);
    }

    [Fact]
    public void DerivedMetrics_ProvenanceWithoutAValue_RendersNoRow()
    {
        // The sample provenance names "median_hfr", but a row with no MedianHfr must not render a
        // row with an empty value and a real provenance string: spec 7.3 says only fields that
        // received a value are present, so a stale document renders nothing.
        var harness = new Harness(row: SampleRow(medianHfr: null));
        harness.Panel.Load();
        harness.Settle();

        Assert.DoesNotContain(harness.Panel.DerivedMetrics, metric => metric.Label == "HFR");
    }

    [Fact]
    public void DerivedMetrics_AreInSpecSevenOneOrder()
    {
        // A handful of fields, deliberately out of spec 7.1's order on this row's own field
        // layout, so the assertion actually exercises the sort rather than an accidental match.
        var row = SampleRow(medianHfr: 2.30) with
        {
            Airmass = 1.5,
            SensorTemp = -10.0,
            ExposureTime = 300,
            DetectedStars = 1200,
        };

        var harness = new Harness(row: row);
        harness.Panel.Load();
        harness.Settle();

        // Fwhm is on the row too, but fwhm/median_fwhm never appear in this list (spec 7.1.1):
        // they render as the two dedicated rows below it instead.
        Assert.DoesNotContain(harness.Panel.DerivedMetrics, metric => metric.Label is "FWHM (arcsec)" or "Header FWHM");
        Assert.Equal(
            ["Exposure", "Sensor temp", "HFR", "Detected stars", "Airmass"],
            harness.Panel.DerivedMetrics.Select(metric => metric.Label));
    }

    [Fact]
    public void Fwhm_TheTwoRowsCarryDistinctLabels()
    {
        // The roadmap Verify line.
        var harness = new Harness();

        Assert.Equal("Header FWHM", harness.Panel.HeaderFwhmLabel);
        Assert.Equal("FWHM (arcsec)", harness.Panel.FwhmArcsecLabel);
        Assert.NotEqual(harness.Panel.HeaderFwhmLabel, harness.Panel.FwhmArcsecLabel);
    }

    [Fact]
    public void Fwhm_HeaderRowNamesItsSourceKeyword()
    {
        var harness = new Harness();
        harness.Panel.Load();
        harness.Settle();

        Assert.Contains("MEANFWHM", harness.Panel.HeaderFwhmText);
    }

    [Fact]
    public void Fwhm_HeaderRowAppendsNoUnit()
    {
        var harness = new Harness();
        harness.Panel.Load();
        harness.Settle();

        Assert.DoesNotContain("arcsec", harness.Panel.HeaderFwhmText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Fwhm_HeaderRowWithNoProvenance_OmitsTheParenthetical()
    {
        var harness = new Harness { Result = Sample() with { Provenance = new Dictionary<string, string>() } };

        harness.Panel.Load();
        harness.Settle();

        Assert.Equal("3.10", harness.Panel.HeaderFwhmText);
        Assert.DoesNotContain("(", harness.Panel.HeaderFwhmText);
    }

    [Fact]
    public void Fwhm_EitherRowAbsent_HidesOnlyThatRow()
    {
        var harness = new Harness { Result = Sample() with { MedianFwhm = null } };

        harness.Panel.Load();
        harness.Settle();

        Assert.False(harness.Panel.HasHeaderFwhm);
        Assert.Equal("", harness.Panel.HeaderFwhmText);
        Assert.True(harness.Panel.HasFwhmArcsec);
        Assert.Equal("2.85", harness.Panel.FwhmArcsecText);
    }
}
