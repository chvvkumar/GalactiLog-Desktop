using System.Reflection;
using Avalonia.Controls;

namespace GalactiLog.App.Tests.TestSupport;

/// <summary>
/// Drives a window's <c>OnClosing</c> override for a chosen <see cref="WindowCloseReason"/>.
/// </summary>
/// <remarks>
/// <c>OnClosing</c> is protected, <see cref="WindowClosingEventArgs"/> carries no public
/// constructor, and the headless platform offers no way to raise a close for a chosen reason, so
/// the reason is driven straight at the override under test. Shared rather than written once per
/// window suite: three dialogs declare a close policy now (FIXER LIST F20).
/// </remarks>
internal static class WindowClosingProbe
{
    public static WindowClosingEventArgs RaiseClosing(Window window, WindowCloseReason reason)
    {
        var args = (WindowClosingEventArgs)Activator.CreateInstance(
            typeof(WindowClosingEventArgs),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [reason, false],
            culture: null)!;

        var closing = typeof(Window).GetMethod(
            "OnClosing",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(WindowClosingEventArgs)],
            modifiers: null)
            ?? throw new InvalidOperationException("Window.OnClosing was not found.");

        closing.Invoke(window, [args]);
        return args;
    }
}
