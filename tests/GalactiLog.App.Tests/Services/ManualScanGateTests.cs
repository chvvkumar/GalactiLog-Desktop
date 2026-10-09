using System.ComponentModel;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels;
using GalactiLog.Data.Ingest;
using Xunit;

namespace GalactiLog.App.Tests.Services;

// The one gate every manual scan button goes through. The scan reads the settings on disk, so a
// scan started while an edit is staged would silently ignore it (the origin bug: an added library
// folder that no scan ever visited). The post seam is synchronous here, as JobRegistryTests does.
public class ManualScanGateTests
{
    private sealed class Dirty : IPendingEdits
    {
        private bool _pending;
        public event PropertyChangedEventHandler? PropertyChanged;
        public string Label => "Library";
        public string NavigationKey => "library";
        public string? SaveRefusal => null;
        public bool HasPendingEdits
        {
            get => _pending;
            set { _pending = value; PropertyChanged?.Invoke(this, new(nameof(HasPendingEdits))); }
        }
        public Task SaveAsync() => Task.CompletedTask;
        public void Discard() => HasPendingEdits = false;
    }

    [Fact]
    public async Task Refuses_and_sets_the_notice_while_an_edit_is_pending()
    {
        var registry = new PendingEditsRegistry();
        var source = new Dirty { HasPendingEdits = true };
        registry.Register(source);
        var runs = 0;
        var gate = new ManualScanGate(registry, (_, _) => { runs++; return Task.CompletedTask; }, action => action());

        await gate.RunAsync(null, CancellationToken.None);

        Assert.Equal(0, runs);
        Assert.Equal("Save or discard your changes before scanning.", registry.Notice);

        // The registry clears the notice once nothing is pending; the gate adds no rule of its own.
        source.Discard();
        Assert.Null(registry.Notice);
    }

    [Fact]
    public async Task Forwards_the_options_when_nothing_is_pending()
    {
        var registry = new PendingEditsRegistry();
        registry.Register(new Dirty());
        var options = new ScanRunOptions(IncludeCalibration: true, ForceOrphanCleanup: false);
        ScanRunOptions? seen = null;
        var gate = new ManualScanGate(registry, (o, _) => { seen = o; return Task.CompletedTask; }, action => action());

        await gate.RunAsync(options, CancellationToken.None);

        Assert.Same(options, seen);
        Assert.Null(registry.Notice);
    }
}
