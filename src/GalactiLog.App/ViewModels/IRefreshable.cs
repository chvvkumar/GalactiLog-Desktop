using System.Windows.Input;

namespace GalactiLog.App.ViewModels;

/// <summary>
/// A rail page with its own refresh action. The shell's F5 runs it on whichever page is showing
/// (ux-flow F8); a page without one ignores the key.
/// </summary>
public interface IRefreshable
{
    ICommand RefreshCommand { get; }
}
