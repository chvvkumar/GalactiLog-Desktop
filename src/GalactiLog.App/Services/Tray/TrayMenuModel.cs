using System.Windows.Input;
using GalactiLog.App.ViewModels.Tray;

namespace GalactiLog.App.Services.Tray;

/// <summary>
/// One row of the native tray menu. A null <see cref="Label"/> is the separator.
/// </summary>
public sealed record TrayMenuEntry(int Id, string? Label, bool Enabled, Action? Execute);

/// <summary>
/// Spec 12.11 behaviour 4's menu, as data: the four items in spec order with the separator before
/// Exit, each enabled by its command's CanExecute. Free of Win32 so the headless suite can pin the
/// order, the labels, the enabled state and the id round trip that <see cref="NativeTrayIcon"/>
/// performs through TrackPopupMenuEx.
/// </summary>
public static class TrayMenuModel
{
    public const int OpenId = 1;
    public const int ScanNowId = 2;
    public const int CheckForUpdatesId = 3;
    public const int ExitId = 4;

    /// <summary>Windows stores a notification-area tip in 128 characters including the terminator.</summary>
    public const int ToolTipMaxLength = TrayIconViewModel.ToolTipMaxLength;

    public static IReadOnlyList<TrayMenuEntry> Build(TrayIconViewModel tray) =>
    [
        Entry(OpenId, "Open", tray.OpenCommand),
        Entry(ScanNowId, "Scan now", tray.ScanNowCommand),
        Entry(CheckForUpdatesId, "Check for updates", tray.CheckForUpdatesCommand),
        new TrayMenuEntry(0, null, false, null),
        Entry(ExitId, "Exit", tray.ExitCommand),
    ];

    /// <summary>Runs the entry TrackPopupMenuEx returned; 0 (dismissed) and unknown ids do nothing.</summary>
    public static void Run(IReadOnlyList<TrayMenuEntry> entries, int id)
    {
        foreach (var entry in entries)
        {
            if (entry.Id == id && entry.Enabled)
            {
                entry.Execute?.Invoke();
                return;
            }
        }
    }

    public static string ClipToolTip(string text)
        => text.Length <= ToolTipMaxLength ? text : text[..ToolTipMaxLength];

    private static TrayMenuEntry Entry(int id, string label, ICommand command)
        => new(id, label, command.CanExecute(null), () => command.Execute(null));
}
