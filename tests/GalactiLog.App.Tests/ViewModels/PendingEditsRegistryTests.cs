using System.ComponentModel;
using GalactiLog.App.ViewModels;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

public class PendingEditsRegistryTests
{
    private sealed class Fake(string label, List<string>? log = null) : IPendingEdits
    {
        private bool _dirty;
        private string? _refusal;
        public event PropertyChangedEventHandler? PropertyChanged;
        public string Label { get; } = label;
        public string NavigationKey => Label.ToLowerInvariant();
        public bool Throws { get; set; }
        public bool HasPendingEdits { get => _dirty; set { _dirty = value; Raise(nameof(HasPendingEdits)); } }
        public string? SaveRefusal { get => _refusal; set { _refusal = value; Raise(nameof(SaveRefusal)); } }
        public Task SaveAsync()
        {
            log?.Add("save " + Label);
            if (Throws) throw new InvalidOperationException("boom");
            return Task.CompletedTask;
        }
        public void Discard() => log?.Add("discard " + Label);
        private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    [Fact]
    public void One_dirty_source_is_pending_and_summarised()
    {
        var r = new PendingEditsRegistry();
        var a = new Fake("A"); var b = new Fake("B");
        r.Register(a); r.Register(b);
        b.HasPendingEdits = true;
        Assert.True(r.AnyPending);
        Assert.Single(r.Pending);
        Assert.Equal("Unsaved changes: B", r.Summary);
    }

    [Fact]
    public void Summary_follows_registration_order()
    {
        var r = new PendingEditsRegistry();
        var a = new Fake("A"); var b = new Fake("B");
        r.Register(a); r.Register(b);
        b.HasPendingEdits = true; a.HasPendingEdits = true;
        Assert.Equal("Unsaved changes: A, B", r.Summary);
    }

    [Fact]
    public void Refusal_blocks_save_until_cleared()
    {
        var r = new PendingEditsRegistry();
        var a = new Fake("A");
        r.Register(a);
        a.HasPendingEdits = true;
        Assert.True(r.CanSave);
        a.SaveRefusal = "Fix the path first.";
        Assert.False(r.CanSave);
        Assert.Equal("Fix the path first.", r.SaveRefusal);
        a.SaveRefusal = null;
        Assert.True(r.CanSave);
    }

    [Fact]
    public async Task SaveAll_saves_dirty_in_order_and_survives_a_throw()
    {
        var log = new List<string>();
        var r = new PendingEditsRegistry();
        var a = new Fake("A", log) { Throws = true }; var b = new Fake("B", log); var c = new Fake("C", log);
        r.Register(a); r.Register(b); r.Register(c);
        a.HasPendingEdits = true; c.HasPendingEdits = true;
        await r.SaveAllCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "save A", "save C" }, log);
    }

    [Fact]
    public void DiscardAll_discards_dirty_only()
    {
        var log = new List<string>();
        var r = new PendingEditsRegistry();
        var a = new Fake("A", log); var b = new Fake("B", log);
        r.Register(a); r.Register(b);
        b.HasPendingEdits = true;
        r.DiscardAllCommand.Execute(null);
        Assert.Equal(new[] { "discard B" }, log);
    }

    [Fact]
    public void Notice_clears_when_last_source_goes_clean()
    {
        var r = new PendingEditsRegistry();
        var a = new Fake("A");
        r.Register(a);
        a.HasPendingEdits = true;
        r.Notice = "Save first.";
        Assert.Equal("Save first.", r.Notice);
        a.HasPendingEdits = false;
        Assert.Null(r.Notice);
    }

    [Fact]
    public void Register_is_idempotent()
    {
        var r = new PendingEditsRegistry();
        var a = new Fake("A");
        r.Register(a); r.Register(a);
        a.HasPendingEdits = true;
        Assert.Single(r.Pending);
        Assert.Equal("Unsaved changes: A", r.Summary);
    }

    [Fact]
    public void Unregister_stops_reacting()
    {
        var r = new PendingEditsRegistry();
        var a = new Fake("A");
        r.Register(a);
        r.Unregister(a);
        a.HasPendingEdits = true;
        Assert.False(r.AnyPending);
    }

    [Fact]
    public void AnyPending_raises_once_per_flip()
    {
        var r = new PendingEditsRegistry();
        var a = new Fake("A");
        r.Register(a);
        var count = 0;
        r.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PendingEditsRegistry.AnyPending)) count++; };
        a.HasPendingEdits = true;
        Assert.Equal(1, count);
        a.SaveRefusal = "x";
        Assert.Equal(1, count);
        a.HasPendingEdits = false;
        Assert.Equal(2, count);
    }
}
