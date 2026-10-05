using System.Text.Json;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.Core.Settings;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GalactiLog.App.Tests.Services;

// The one writer of user_settings.graph (spec 5.8.3). No database: the getter and the setter are
// delegates over an in-memory document, which is exactly how AppHost wires SettingsStore's method
// groups in.
public class GraphSettingsWriterTests
{
    private sealed class FakeStore
    {
        private readonly Lock _gate = new();
        public GraphSettings Document = new();
        public int Saves;

        public GraphSettings Get()
        {
            lock (_gate) { return Document; }
        }

        public void Save(GraphSettings value)
        {
            lock (_gate)
            {
                Document = value;
                Saves++;
            }
        }
    }

    [Fact]
    public async Task Write_PersistsTheMutation()
    {
        var store = new FakeStore();
        var writer = new GraphSettingsWriter(store.Get, store.Save);

        writer.Write(graph => graph with { EnabledMetrics = ["fwhm"] });
        await writer.Pending;

        Assert.Equal(["fwhm"], store.Document.EnabledMetrics);
    }

    [Fact]
    public async Task Write_PreservesTheOtherGraphKeys()
    {
        var store = new FakeStore
        {
            Document = new GraphSettings
            {
                EnabledFilters = ["overall", "Ha"],
                DefaultChartSessions = 7,
            },
        };
        var writer = new GraphSettingsWriter(store.Get, store.Save);

        writer.Write(graph => graph with { EnabledMetrics = ["hfr"] });
        await writer.Pending;

        Assert.Equal(["hfr"], store.Document.EnabledMetrics);
        Assert.Equal(["overall", "Ha"], store.Document.EnabledFilters);
        Assert.Equal(7, store.Document.DefaultChartSessions);
    }

    // Spec 5.8: "an unrecognized key is preserved on write, never dropped". The record's
    // JsonExtensionData carries it and `with` copies it, so the writer never has to know about it.
    [Fact]
    public async Task Write_PreservesUnrecognizedKeys()
    {
        var store = new FakeStore
        {
            Document = new GraphSettings
            {
                ExtensionData = new Dictionary<string, JsonElement>
                {
                    ["future_key"] = JsonDocument.Parse("42").RootElement,
                },
            },
        };
        var writer = new GraphSettingsWriter(store.Get, store.Save);

        writer.Write(graph => graph with { DefaultChartSessions = 3 });
        await writer.Pending;

        Assert.NotNull(store.Document.ExtensionData);
        Assert.Equal(42, store.Document.ExtensionData["future_key"].GetInt32());
    }

    // A toggle must not block the UI thread on SQLite, so both the load and the save run on the
    // thread pool rather than on the caller. F18 follow-up: proven by parking the save and
    // observing that Write returned anyway, rather than by comparing thread ids after an await
    // that frees the calling thread for the pool to reuse (TRACKING section 2 item 8).
    [Fact]
    public async Task Write_RunsOffTheCallingThread()
    {
        var store = new FakeStore();
        using var release = new ManualResetEventSlim(false);
        var saves = 0;
        var writer = new GraphSettingsWriter(
            store.Get,
            graph =>
            {
                Interlocked.Increment(ref saves);
                release.Wait(TimeSpan.FromSeconds(30));
                store.Save(graph);
            });

        writer.Write(graph => graph with { DefaultChartSessions = 3 });

        // Write returned while the save is parked: a synchronous save would have parked Write.
        Assert.False(writer.Pending.IsCompleted);

        release.Set();
        await writer.Pending;

        Assert.Equal(1, Volatile.Read(ref saves));
        Assert.Equal(3, store.Get().DefaultChartSessions);
    }

    // Two toggles in the same click cannot interleave: the second write waits for the first, so
    // there is no lost update between the load and the save.
    [Fact]
    public async Task Write_QueuedWritesAreSerialised()
    {
        var store = new FakeStore();
        var order = new List<int>();
        var gate = new TaskCompletionSource();
        var writer = new GraphSettingsWriter(
            store.Get,
            graph =>
            {
                order.Add(graph.DefaultChartSessions);
                store.Save(graph);
            });

        // The first write is held inside its mutation until the second has already been queued,
        // so the queue order is the only thing that can decide which save lands first.
        writer.Write(graph =>
        {
            gate.Task.Wait(TimeSpan.FromSeconds(10));
            return graph with { DefaultChartSessions = 1 };
        });
        writer.Write(graph => graph with { DefaultChartSessions = 2 });
        gate.SetResult();
        await writer.Pending;

        Assert.Equal([1, 2], order);
        Assert.Equal(2, store.Saves);
    }

    // The load happens inside the queued write rather than at queue time, which is what keeps a
    // metric toggle from clobbering a filter toggle queued a moment earlier.
    [Fact]
    public async Task Write_TwoConcurrentTogglesBothSurvive()
    {
        var store = new FakeStore();
        var writer = new GraphSettingsWriter(store.Get, store.Save);

        writer.Write(graph => graph with { EnabledMetrics = ["fwhm"] });
        writer.Write(graph => graph with { EnabledFilters = ["overall", "Ha"] });
        await writer.Pending;

        Assert.Equal(["fwhm"], store.Document.EnabledMetrics);
        Assert.Equal(["overall", "Ha"], store.Document.EnabledFilters);
    }

    // A locked database must not take the window down: the toggle has already changed what is on
    // screen and the next one tries again.
    [Fact]
    public async Task Write_Throwing_IsLoggedAndDropped()
    {
        var logger = new RecordingLogger();
        var writer = new GraphSettingsWriter(
            () => new GraphSettings(),
            _ => throw new InvalidOperationException("database is locked"),
            logger);

        writer.Write(graph => graph with { EnabledMetrics = ["hfr"] });
        await writer.Pending;

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.IsType<InvalidOperationException>(entry.Exception);

        // The chain survives the failure: the next write still runs.
        writer.Write(graph => graph);
        await writer.Pending;
        Assert.Equal(2, logger.Entries.Count);
    }
}
