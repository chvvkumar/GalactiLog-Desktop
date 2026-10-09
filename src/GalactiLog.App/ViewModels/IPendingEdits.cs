namespace GalactiLog.App.ViewModels;

/// <summary>One editable surface whose edits are staged until saved.</summary>
public interface IPendingEdits : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>Shown in the save bar. Example: "Library folders, paths and rules".</summary>
    string Label { get; }

    /// <summary>Settings tab key, for a later "go to" affordance. Example: "library".</summary>
    string NavigationKey { get; }

    /// <summary>True while an unsaved edit exists. Raises PropertyChanged when it flips.</summary>
    bool HasPendingEdits { get; }

    /// <summary>Null when SaveAsync may run; otherwise the sentence saying why not. Raises
    /// PropertyChanged.</summary>
    string? SaveRefusal { get; }

    /// <summary>Persists the staged edits. A no-op when HasPendingEdits is false or SaveRefusal is
    /// non-null.</summary>
    Task SaveAsync();

    /// <summary>Drops the staged edits and restores the stored document.</summary>
    void Discard();
}
