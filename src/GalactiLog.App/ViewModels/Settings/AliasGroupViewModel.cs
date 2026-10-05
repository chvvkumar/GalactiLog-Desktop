using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.Core.Aliases;

namespace GalactiLog.App.ViewModels.Settings;

/// <summary>
/// One row of the web's <c>GroupingEditor</c> right column ("Groups"): a canonical filter,
/// camera or telescope name with its alias list, and for filters only, its stored colour. This
/// is also the row of spec 12.7's "table of canonical filters, each with a colour swatch and an
/// alias list editor" -- <see cref="GroupingEditorViewModel.Groups"/> renders as that table when
/// its section shows the colour swatch.
/// </summary>
/// <remarks>
/// A view-model that holds a brush holds an <see cref="ImmutableSolidColorBrush"/>
/// (design-spec 14.4); <see cref="SwatchBrush"/> is built from the plain hex string in
/// <see cref="ResolvedColor"/>, which is user data, never a theme token, so nothing here touches
/// a <c>DynamicResource</c> and no UI-thread affinity is required to compute it.
/// </remarks>
public sealed partial class AliasGroupViewModel : ObservableObject
{
    /// <param name="color">The stored colour, or null for "no colour stored", which is what lets
    /// the seeded palette resolve (P13 R2a). What it is handed is what it keeps: nothing is
    /// substituted here, because substituting a grey at construction is exactly what made the
    /// palette unreachable before this phase.</param>
    public AliasGroupViewModel(string canonical, string? color, IEnumerable<string> aliases)
    {
        Canonical = canonical;
        Color = color;
        Aliases = new ObservableCollection<string>(aliases);
        // Review P2-1: an alias can be what carries the category, so adding or removing one
        // repaints the swatch. ResolvedColor is computed, so the collection has to say so.
        Aliases.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ResolvedColor));
            OnPropertyChanged(nameof(SwatchBrush));
        };
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResolvedColor))]
    [NotifyPropertyChangedFor(nameof(SwatchBrush))]
    public partial string Canonical { get; set; }

    /// <summary>The stored colour, or null when this filter has none and resolves through the
    /// seeded palette instead. This, not <see cref="ResolvedColor"/>, is what the Filters tab
    /// writes back to the document.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResolvedColor))]
    [NotifyPropertyChangedFor(nameof(SwatchBrush))]
    public partial string? Color { get; set; }

    /// <summary>
    /// What the swatch and the picker both show, resolved through the one spine
    /// <c>AliasMap.FilterColor</c> also calls, <see cref="FilterColor.Resolve"/>: the stored
    /// colour when it carries intent, else the seeded palette entry for the category
    /// <see cref="Canonical"/> folds to, else the entry for the first alias that folds to one,
    /// else spec 5.8.4's grey. It follows a rename and an alias edit, because both can change the
    /// category and therefore the swatch.
    /// </summary>
    /// <remarks>Review P2-1: this was a three-step copy of that order and so disagreed with the
    /// ledger dots, the night strip ticks and the chart pills for a group whose alias, not its
    /// canonical name, carried the category. There is no second order here now.</remarks>
    public string ResolvedColor => FilterColor.Resolve(Color, Canonical, Aliases);

    /// <summary>The colour rendered as a brush, through the App layer's one parse
    /// (<see cref="Dashboard.TargetRowViewModel.ParseTint"/>), which falls back to
    /// <see cref="FilterColor.Fallback"/> when <see cref="ResolvedColor"/> is not parseable, so a
    /// swatch never throws mid-edit.</summary>
    public IImmutableSolidColorBrush SwatchBrush => Dashboard.TargetRowViewModel.ParseTint(ResolvedColor);

    public ObservableCollection<string> Aliases { get; }

    [ObservableProperty]
    public partial bool IsRenaming { get; set; }

    [ObservableProperty]
    public partial string RenameText { get; set; } = "";

    /// <summary>Raised when a colour committed through <see cref="TrySetColor"/> is refused, so
    /// the tab can show why.</summary>
    public event EventHandler<string>? ColorRefused;

    /// <summary>Raised when the last alias is removed, so the owning
    /// <see cref="GroupingEditorViewModel"/> can delete the group entirely (the web's own rule).
    /// </summary>
    public event EventHandler? AliasesEmptied;

    /// <summary>Raised when a rename is committed to a non-blank, different name, so the owning
    /// editor can check it against every other group's canonical name before applying it (a
    /// rename that collides is refused there, not here, because only the editor knows every
    /// other group).</summary>
    public event EventHandler<string>? RenameRequested;

    /// <summary>
    /// Sets <see cref="Color"/> when <paramref name="candidate"/> is a six-digit
    /// <c>#RRGGBB</c> hex string, and refuses (raising <see cref="ColorRefused"/>) otherwise --
    /// "validate the text form on commit and refuse anything else rather than writing a string
    /// that FilterColor will later have to guess at."
    /// </summary>
    /// <remarks>Review P3-3: since the picker replaced the hex <c>TextBox</c>, the only caller in
    /// the application is <c>GroupingEditorView.OnGroupColorChanged</c>, which hands over a value
    /// that is six-digit hex by construction and so can never be refused. The validation and
    /// <see cref="ColorRefused"/> stay as the guard on a public entry point, not as a live
    /// user-facing refusal path.</remarks>
    public bool TrySetColor(string candidate)
    {
        if (!IsSixDigitHex(candidate))
        {
            ColorRefused?.Invoke(this, candidate);
            return false;
        }

        Color = candidate;
        return true;
    }

    private static bool IsSixDigitHex(string candidate)
    {
        if (candidate.Length != 7 || candidate[0] != '#')
        {
            return false;
        }

        for (var i = 1; i < candidate.Length; i++)
        {
            if (!Uri.IsHexDigit(candidate[i]))
            {
                return false;
            }
        }

        return true;
    }

    [RelayCommand]
    private void RemoveAlias(string alias)
    {
        // TRACKING item 13: the guard belongs in the body too, not only in a CanExecute, for the
        // commands whose correctness depends on it. Removing an alias that is not present is a
        // no-op rather than an empty-group deletion.
        if (!Aliases.Remove(alias))
        {
            return;
        }

        if (Aliases.Count == 0)
        {
            AliasesEmptied?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private void BeginRename()
    {
        RenameText = Canonical;
        IsRenaming = true;
    }

    [RelayCommand]
    private void CommitRename()
    {
        var trimmed = RenameText.Trim();
        IsRenaming = false;
        if (trimmed.Length == 0 || string.Equals(trimmed, Canonical, StringComparison.Ordinal))
        {
            return;
        }

        RenameRequested?.Invoke(this, trimmed);
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;
}
