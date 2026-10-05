using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Threading;
using GalactiLog.App.ViewModels.Tray;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.Services.Tray;

/// <summary>
/// Spec 12.11 behaviour 4's notification-area icon, owned by the application through
/// Shell_NotifyIcon on a message-only window (ruling R30). Avalonia 11.3's TrayIcon shows its menu
/// in a managed popup that lands at the wrong screen position under DPI scaling (Avalonia issue
/// 8386), so the menu here is a native TrackPopupMenuEx at the cursor, in device pixels.
/// </summary>
/// <remarks>
/// Everything runs on the UI thread: the window is created there, so Avalonia's Win32 message
/// loop dispatches its messages, and the tooltip update is posted there. The icon is read with
/// ExtractIconEx from the running executable, which carries Assets/GalactiLog.ico as its
/// ApplicationIcon; nothing is written to disk (dispatch-common section 6).
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class NativeTrayIcon : IDisposable
{
    private const string ClassName = "GalactiLog.Tray";
    private const uint IconId = 1;
    private const uint WmTrayCallback = 0x8000 + 1; // WM_APP + 1

    private const uint WmNull = 0x0000;
    private const uint WmContextMenu = 0x007B;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonUp = 0x0205;
    private const uint NinSelect = 0x0400;
    private const uint NinKeySelect = 0x0401;

    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;
    private const uint NimSetVersion = 4;
    private const uint NifMessage = 0x1;
    private const uint NifIcon = 0x2;
    private const uint NifTip = 0x4;
    private const uint NotifyIconVersion4 = 4;

    private const uint MfString = 0x0;
    private const uint MfGrayed = 0x1;
    private const uint MfSeparator = 0x800;
    private const uint TpmLeftAlign = 0x0;
    private const uint TpmRightButton = 0x2;
    private const uint TpmBottomAlign = 0x20;
    private const uint TpmReturnCmd = 0x100;

    private const nint HwndMessage = -3;
    private const int IdiApplication = 32512;

    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    private readonly TrayIconViewModel _tray;
    private readonly ILogger _logger;

    // Rooted for the life of the window: the class holds only the raw function pointer.
    private readonly WndProcDelegate _wndProc;
    private readonly uint _taskbarCreated;
    private readonly nint _hwnd;
    private readonly nint _icon;
    private bool _version4;
    private bool _menuOpen;
    private bool _disposed;

    public NativeTrayIcon(TrayIconViewModel tray, ILogger? logger = null)
    {
        Dispatcher.UIThread.VerifyAccess();
        _tray = tray;
        _logger = logger ?? NullLogger.Instance;
        _wndProc = WndProc;
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");

        var instance = GetModuleHandleW(null);
        var wc = new WndClass
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = instance,
            lpszClassName = ClassName,
        };
        if (RegisterClassW(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassW");
        }

        _hwnd = CreateWindowExW(0, ClassName, string.Empty, 0, 0, 0, 0, 0, HwndMessage, 0, instance, 0);
        if (_hwnd == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowExW");
        }

        _icon = LoadApplicationIcon();
        AddIcon();
        _tray.PropertyChanged += OnTrayPropertyChanged;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tray.PropertyChanged -= OnTrayPropertyChanged;
        var data = Data();
        Shell_NotifyIconW(NimDelete, ref data);
        DestroyWindow(_hwnd);
        UnregisterClassW(ClassName, GetModuleHandleW(null));
        if (_icon != 0)
        {
            DestroyIcon(_icon);
        }
    }

    private void AddIcon()
    {
        var data = Data();
        data.uFlags = NifMessage | NifIcon | NifTip;
        data.uCallbackMessage = WmTrayCallback;
        data.hIcon = _icon;
        data.szTip = TrayMenuModel.ClipToolTip(_tray.ToolTipText);
        if (!Shell_NotifyIconW(NimAdd, ref data))
        {
            _logger.LogWarning("Shell_NotifyIcon NIM_ADD failed (error {Error})", Marshal.GetLastWin32Error());
            return;
        }

        data.uVersion = NotifyIconVersion4;
        _version4 = Shell_NotifyIconW(NimSetVersion, ref data);
    }

    private void UpdateToolTip()
    {
        if (_disposed)
        {
            return;
        }

        var data = Data();
        data.uFlags = NifTip;
        data.szTip = TrayMenuModel.ClipToolTip(_tray.ToolTipText);
        Shell_NotifyIconW(NimModify, ref data);
    }

    private void OnTrayPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(TrayIconViewModel.ToolTipText) or null))
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            UpdateToolTip();
        }
        else
        {
            Dispatcher.UIThread.Post(UpdateToolTip);
        }
    }

    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        // An exception leaving a window procedure takes the process down, so every handler is
        // caught here and logged.
        try
        {
            if (msg == WmTrayCallback)
            {
                // Version 4 packs the event in the low word; earlier versions pass it whole.
                OnTrayEvent((uint)(lParam & 0xFFFF));
                return 0;
            }

            if (msg == _taskbarCreated)
            {
                AddIcon();
                return 0;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The tray icon's message handler failed");
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void OnTrayEvent(uint evt)
    {
        if (_disposed)
        {
            return;
        }

        // Version 4 sends WM_CONTEXTMENU after WM_RBUTTONUP for one right click, and NIN_SELECT
        // after WM_LBUTTONUP for one left click; the menu is shown once, Open is idempotent.
        switch (evt)
        {
            case WmLButtonUp or NinSelect or NinKeySelect:
                _tray.OpenCommand.Execute(null);
                break;
            case WmContextMenu when _version4:
            case WmRButtonUp when !_version4:
                ShowMenu();
                break;
        }
    }

    private void ShowMenu()
    {
        if (_menuOpen)
        {
            return;
        }

        var entries = TrayMenuModel.Build(_tray);
        var menu = CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        _menuOpen = true;
        try
        {
            foreach (var entry in entries)
            {
                if (entry.Label is null)
                {
                    AppendMenuW(menu, MfSeparator, 0, null);
                }
                else
                {
                    AppendMenuW(menu, MfString | (entry.Enabled ? 0 : MfGrayed), (nuint)entry.Id, entry.Label);
                }
            }

            // Both calls are what the TrackPopupMenu documentation asks of a notification-area
            // menu: the foreground window so the menu dismisses on a click elsewhere, and WM_NULL
            // afterwards so the window leaves menu mode.
            SetForegroundWindow(_hwnd);
            GetCursorPos(out var cursor);
            var chosen = TrackPopupMenuEx(
                menu, TpmReturnCmd | TpmRightButton | TpmBottomAlign | TpmLeftAlign, cursor.X, cursor.Y, _hwnd, 0);
            PostMessageW(_hwnd, WmNull, 0, 0);
            TrayMenuModel.Run(entries, chosen);
        }
        finally
        {
            _menuOpen = false;
            DestroyMenu(menu);
        }
    }

    private nint LoadApplicationIcon()
    {
        if (Environment.ProcessPath is { } exe &&
            ExtractIconExW(exe, 0, out var large, out var small, 1) > 0)
        {
            if (small != 0 && large != 0)
            {
                DestroyIcon(large);
            }

            return small != 0 ? small : large;
        }

        _logger.LogWarning("The executable carries no icon; the tray shows the stock application icon");
        return LoadIconW(0, IdiApplication);
    }

    private NotifyIconData Data() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = _hwnd,
        uID = IconId,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NotifyIconData lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string lpszFile, int nIconIndex, out nint phiconLarge, out nint phiconSmall, uint nIcons);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WndClass lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClassW(string lpClassName, nint hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(nint hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(nint hMenu, uint uFlags, int x, int y, nint hWnd, nint lptpm);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadIconW(nint hInstance, nint lpIconName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? lpModuleName);
}
