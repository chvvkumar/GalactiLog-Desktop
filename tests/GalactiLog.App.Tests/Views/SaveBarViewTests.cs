using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.ViewModels;
using GalactiLog.App.Views;
using Xunit;

namespace GalactiLog.App.Tests.Views;

// The app-wide save bar. It exists because a per-tab Save button four sections below an edit was
// never found, so the two things worth pinning are that it appears exactly when something is
// unsaved and that its Save button really saves.
public class SaveBarViewTests
{
    private sealed class Source : IPendingEdits
    {
        private bool _pending;
        public event PropertyChangedEventHandler? PropertyChanged;
        public int Saves { get; private set; }
        public string Label => "Library folders, paths and rules";
        public string NavigationKey => "library";
        public string? SaveRefusal => null;
        public bool HasPendingEdits
        {
            get => _pending;
            set { _pending = value; PropertyChanged?.Invoke(this, new(nameof(HasPendingEdits))); }
        }
        public Task SaveAsync() { Saves++; HasPendingEdits = false; return Task.CompletedTask; }
        public void Discard() => HasPendingEdits = false;
    }

    private static (Window Window, SaveBarView View, PendingEditsRegistry Registry, Source Source) Show()
    {
        var registry = new PendingEditsRegistry();
        var source = new Source();
        registry.Register(source);
        var view = new SaveBarView { DataContext = registry };
        var window = new Window { Width = 1280, Height = 120, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view, registry, source);
    }

    private static Button ButtonNamed(SaveBarView view, string name)
        => view.GetVisualDescendants().OfType<Button>().Single(button => button.Name == name);

    [AvaloniaFact]
    public void Bar_IsHidden_WithNothingPending_AndShown_WithAPendingSource()
    {
        var (_, view, _, source) = Show();
        var bar = view.GetControl<ContentControl>("SaveBarCallout");
        Assert.False(bar.IsVisible);

        source.HasPendingEdits = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(bar.IsEffectivelyVisible);
        Assert.Contains(
            view.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "Unsaved changes: Library folders, paths and rules");
    }

    [AvaloniaFact]
    public void SaveButton_InvokesSaveAll()
    {
        var (_, view, registry, source) = Show();
        source.HasPendingEdits = true;
        Dispatcher.UIThread.RunJobs();

        var save = ButtonNamed(view, "SaveAllButton");
        Assert.Same(registry.SaveAllCommand, save.Command);
        save.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, source.Saves);
        Assert.False(registry.AnyPending);
    }

    [AvaloniaFact]
    public void Notice_IsShown_WhenTheScanGateRefuses()
    {
        var (_, view, registry, source) = Show();
        source.HasPendingEdits = true;
        registry.Notice = "Save or discard your changes before scanning.";
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(
            view.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "Save or discard your changes before scanning." && text.IsEffectivelyVisible);
    }
}
