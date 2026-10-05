using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Settings;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// The shared chart selection (spec 5.8.3). No database and no window: the writer is a pair of
// delegates over an in-memory document, the alias map a Func<>.
public class ChartSelectionViewModelTests
{
    private sealed class Harness
    {
        public GraphSettings Document;

        /// <summary>Review finding 6: "nothing was written" is asserted by counting saves, not by
        /// value equality against a document that happens to already hold the expected value.
        /// </summary>
        public int Saves;

        public readonly ChartSelectionViewModel Selection;

        public Harness(
            GraphSettings? initial = null,
            Dictionary<string, FilterSetting>? filters = null,
            Func<AliasMap>? aliases = null)
        {
            Document = initial ?? new GraphSettings();
            var writer = new GraphSettingsWriter(
                () => Document,
                value =>
                {
                    Document = value;
                    Saves++;
                });
            Selection = new ChartSelectionViewModel(
                Document,
                writer,
                aliases ?? (() => new AliasMap(filters ?? [], new EquipmentSettings())));
        }

        public Task Pending => Selection.PendingPersist;
    }

    [Fact]
    public async Task Metrics_SeedFromEnabledMetrics()
    {
        var harness = new Harness(new GraphSettings { EnabledMetrics = ["fwhm", "detected_stars"] });

        Assert.Equal(
            ["hfr", "eccentricity", "fwhm", "guiding_rms", "detected_stars"],
            harness.Selection.Metrics.Select(pill => pill.Key));
        Assert.Equal(
            ["fwhm", "detected_stars"],
            harness.Selection.Metrics.Where(pill => pill.IsSelected).Select(pill => pill.Key));

        // Seeding is not a change: nothing was written.
        await harness.Pending;
        Assert.Equal(0, harness.Saves);
    }

    // Review finding 2: AliasMapCache.Current can fall through to a synchronous SQLite settings
    // read, and this constructor runs on the UI thread at page construction. A throwing delegate
    // is the only assertion that cannot pass by accident.
    [Fact]
    public void Constructor_NeverResolvesTheAliasMap()
    {
        var harness = new Harness(
            new GraphSettings { EnabledFilters = ["overall", "Ha"] },
            aliases: () => throw new InvalidOperationException("the alias map must not be resolved here"));

        Assert.Equal(["overall", "Ha"], harness.Selection.Filters.Select(pill => pill.Key));
        Assert.All(harness.Selection.Filters, pill => Assert.False(pill.HasTint));
    }

    // Spec 13's closing paragraph: enabled_metrics defaults to hfr, eccentricity, fwhm,
    // guiding_rms. detected_stars is chartable but off by default.
    [Fact]
    public void Metrics_DefaultDocument_EnablesTheFourDocumentedKeys()
    {
        var harness = new Harness();

        Assert.Equal(
            ["hfr", "eccentricity", "fwhm", "guiding_rms"],
            harness.Selection.Metrics.Where(pill => pill.IsSelected).Select(pill => pill.Key));
        Assert.False(harness.Selection.Metrics.Single(pill => pill.Key == "detected_stars").IsSelected);
    }

    // Roadmap row 7's third Verify clause: toggling a metric writes graph.enabled_metrics.
    [Fact]
    public async Task Metrics_TogglingAMetric_WritesEnabledMetrics()
    {
        var harness = new Harness();

        harness.Selection.Metrics.Single(pill => pill.Key == "detected_stars").IsSelected = true;
        await harness.Pending;

        Assert.Equal(
            ["hfr", "eccentricity", "fwhm", "guiding_rms", "detected_stars"],
            harness.Document.EnabledMetrics);

        harness.Selection.Metrics.Single(pill => pill.Key == "hfr").IsSelected = false;
        await harness.Pending;

        Assert.Equal(
            ["eccentricity", "fwhm", "guiding_rms", "detected_stars"],
            harness.Document.EnabledMetrics);
    }

    // A chart must not wait on SQLite to re-render: Changed is raised on the calling thread,
    // before the queued write has had a chance to run.
    [Fact]
    public void Metrics_TogglingAMetric_RaisesChangedSynchronously()
    {
        var harness = new Harness();
        var raisedOnThread = 0;
        harness.Selection.Changed += (_, _) => raisedOnThread = Environment.CurrentManagedThreadId;

        harness.Selection.Metrics[0].IsSelected = false;

        Assert.Equal(Environment.CurrentManagedThreadId, raisedOnThread);
    }

    // Review finding 7: both projections are bindable, so a view or a chart can bind them instead
    // of subscribing to Changed and recomputing.
    [Fact]
    public void Toggling_RaisesPropertyChangedForTheProjections()
    {
        var harness = new Harness();
        harness.Selection.OfferFilters(["Ha"]);
        var raised = new List<string?>();
        harness.Selection.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        harness.Selection.Metrics.Single(pill => pill.Key == "detected_stars").IsSelected = true;
        Assert.Contains(nameof(ChartSelectionViewModel.EnabledMetrics), raised);

        raised.Clear();
        harness.Selection.Filters.Single(pill => pill.Key == "Ha").IsSelected = true;
        Assert.Contains(nameof(ChartSelectionViewModel.EnabledFilters), raised);

        raised.Clear();
        harness.Selection.OfferFilters(["Ha", "OIII"]);
        Assert.Contains(nameof(ChartSelectionViewModel.EnabledFilters), raised);
    }

    // The failure mode: a document written by a different build, or hand-edited, names a
    // metric this build does not chart. It is ignored rather than rendered as an empty pill.
    [Fact]
    public void Metrics_UnknownStoredKey_IsIgnoredNotRendered()
    {
        var harness = new Harness(new GraphSettings { EnabledMetrics = ["hfr", "stars", "airmass"] });

        Assert.Equal(5, harness.Selection.Metrics.Count);
        Assert.DoesNotContain("stars", harness.Selection.Metrics.Select(pill => pill.Key));
        Assert.DoesNotContain("airmass", harness.Selection.Metrics.Select(pill => pill.Key));
        Assert.Equal(["hfr"], harness.Selection.Metrics.Where(pill => pill.IsSelected).Select(pill => pill.Key));
    }

    // enabled_filters defaults to ["overall"], the sentinel meaning one series over every frame,
    // coloured by the metric token rather than by a filter colour.
    [Fact]
    public void Filters_DefaultDocument_EnablesOverall()
    {
        var harness = new Harness();

        var overall = Assert.Single(harness.Selection.Filters);
        Assert.Equal(ChartSelectionViewModel.OverallFilterKey, overall.Key);
        Assert.True(overall.IsSelected);
        Assert.False(overall.HasTint);
    }

    [Fact]
    public async Task Filters_OfferFilters_KeepsEnabledEntriesThatArePresent()
    {
        var harness = new Harness(
            new GraphSettings { EnabledFilters = ["overall", "Ha"] },
            new Dictionary<string, FilterSetting> { ["Ha"] = new() { Color = "#FF0000" } });

        harness.Selection.OfferFilters(["Ha", "OIII"]);

        Assert.Equal(["overall", "Ha", "OIII"], harness.Selection.Filters.Select(pill => pill.Key));
        Assert.Equal(["overall", "Ha"], harness.Selection.EnabledFilters);
        // A canonical filter is tinted with its configured colour; the sentinel is not.
        Assert.True(harness.Selection.Filters.Single(pill => pill.Key == "Ha").HasTint);
        Assert.False(harness.Selection.Filters.Single(pill => pill.Key == "overall").HasTint);

        // Nothing was dropped, so nothing was written.
        await harness.Pending;
        Assert.Equal(0, harness.Saves);
    }

    // FIXER LIST F17. An empty offer is a page that has not finished loading its sessions, not a
    // library with no filters, and taking it literally dropped every enabled filter and wrote the
    // loss to graph.enabled_filters. The guard is inside OfferFilters so no caller has to
    // remember it.
    [Fact]
    public async Task Filters_OfferFilters_WithAnEmptyList_ChangesAndPersistsNothing()
    {
        var harness = new Harness(new GraphSettings { EnabledFilters = ["overall", "Ha"] });
        var changed = 0;
        harness.Selection.Changed += (_, _) => changed++;

        harness.Selection.OfferFilters([]);

        Assert.Equal(["overall", "Ha"], harness.Selection.Filters.Select(pill => pill.Key));
        Assert.Equal(["overall", "Ha"], harness.Selection.EnabledFilters);
        Assert.Equal(0, changed);

        await harness.Pending;
        Assert.Equal(0, harness.Saves);
    }

    [Fact]
    public async Task Filters_OfferFilters_DropsEntriesThatAreGone()
    {
        var harness = new Harness(new GraphSettings { EnabledFilters = ["overall", "Ha"] });
        var changed = 0;
        harness.Selection.Changed += (_, _) => changed++;

        harness.Selection.OfferFilters(["OIII"]);

        Assert.Equal(["overall", "OIII"], harness.Selection.Filters.Select(pill => pill.Key));
        Assert.Equal(["overall"], harness.Selection.EnabledFilters);
        Assert.Equal(1, changed);

        await harness.Pending;
        Assert.Equal(["overall"], harness.Document.EnabledFilters);
    }

    [Fact]
    public async Task Filters_TogglingAFilter_WritesEnabledFilters()
    {
        var harness = new Harness();
        harness.Selection.OfferFilters(["Ha", "OIII"]);

        harness.Selection.Filters.Single(pill => pill.Key == "OIII").IsSelected = true;
        await harness.Pending;

        Assert.Equal(["overall", "OIII"], harness.Document.EnabledFilters);

        harness.Selection.Filters.Single(pill => pill.Key == "overall").IsSelected = false;
        await harness.Pending;

        Assert.Equal(["OIII"], harness.Document.EnabledFilters);
    }

    // The document is user-editable JSON and zero sessions is a chart with nothing in it.
    [Fact]
    public async Task DefaultChartSessions_IsClampedToAtLeastOne()
    {
        var harness = new Harness(new GraphSettings { DefaultChartSessions = 0 });
        Assert.Equal(1, harness.Selection.DefaultChartSessions);

        harness.Selection.DefaultChartSessions = 5;
        await harness.Pending;
        Assert.Equal(5, harness.Document.DefaultChartSessions);

        harness.Selection.DefaultChartSessions = -3;
        await harness.Pending;
        Assert.Equal(1, harness.Selection.DefaultChartSessions);
        Assert.Equal(1, harness.Document.DefaultChartSessions);
    }
}
