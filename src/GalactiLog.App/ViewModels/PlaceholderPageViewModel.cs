namespace GalactiLog.App.ViewModels;

/// <summary>
/// Stands in for a rail destination whose real page lands in a later phase, so the shell has the
/// five destinations design-spec 12 names from the moment it exists. Deleted entry by entry as
/// each real page arrives: one line in <see cref="MainWindowViewModel.Items"/> and one
/// <c>DataTemplate</c> in App.axaml change, and nothing else in the shell does.
/// </summary>
public sealed record PlaceholderPageViewModel(string Title, string Message);
