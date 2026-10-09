using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels;

/// <summary>
/// App-wide list of surfaces with staged, unsaved edits. Exists so one save bar can answer "is
/// anything unsaved" and save or discard all of it, instead of every tab hiding its own Save
/// button where the user (and the scan, which reads disk) cannot see it. Sources raise on the UI
/// thread, so the registry does no marshalling and holds no lock.
/// </summary>
public sealed partial class PendingEditsRegistry : ObservableObject
{
    private readonly ILogger _logger;
    private readonly List<IPendingEdits> _sources = [];

    public PendingEditsRegistry(ILogger<PendingEditsRegistry>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public IReadOnlyList<IPendingEdits> Pending { get; private set; } = [];

    public bool AnyPending { get; private set; }

    public string Summary { get; private set; } = "";

    public string? SaveRefusal { get; private set; }

    public bool CanSave => AnyPending && SaveRefusal is null;

    /// <summary>A one-line message another part of the app wants shown beside the bar (the scan
    /// gate uses it). Cleared automatically when <see cref="AnyPending"/> becomes false.</summary>
    [ObservableProperty]
    private string? _notice;

    public void Register(IPendingEdits source)
    {
        if (_sources.Contains(source)) return;
        _sources.Add(source);
        source.PropertyChanged += OnSourceChanged;
        Recompute();
    }

    public void Unregister(IPendingEdits source)
    {
        if (!_sources.Remove(source)) return;
        source.PropertyChanged -= OnSourceChanged;
        Recompute();
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IPendingEdits.HasPendingEdits) or nameof(IPendingEdits.SaveRefusal)
            or null or "")
        {
            Recompute();
        }
    }

    private void Recompute()
    {
        var wasPending = AnyPending;
        Pending = _sources.Where(s => s.HasPendingEdits).ToList();
        AnyPending = Pending.Count > 0;
        Summary = AnyPending ? "Unsaved changes: " + string.Join(", ", Pending.Select(p => p.Label)) : "";
        SaveRefusal = Pending.Select(p => p.SaveRefusal).FirstOrDefault(r => r is not null);

        OnPropertyChanged(nameof(Pending));
        if (wasPending != AnyPending) OnPropertyChanged(nameof(AnyPending));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(SaveRefusal));
        OnPropertyChanged(nameof(CanSave));
        SaveAllCommand.NotifyCanExecuteChanged();
        DiscardAllCommand.NotifyCanExecuteChanged();

        if (!AnyPending) Notice = null;
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAllAsync()
    {
        foreach (var source in Pending.ToList())
        {
            try { await source.SaveAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "Saving {Label} failed", source.Label); }
        }
    }

    [RelayCommand(CanExecute = nameof(AnyPending))]
    private void DiscardAll()
    {
        foreach (var source in Pending.ToList()) source.Discard();
    }
}
