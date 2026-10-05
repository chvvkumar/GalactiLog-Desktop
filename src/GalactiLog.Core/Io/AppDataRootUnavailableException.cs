namespace GalactiLog.Core.Io;

/// <summary>
/// The data location pointer names a root the application cannot use, and falling back to the
/// default would open a second, empty catalogue beside the user's real one. Thrown instead of
/// falling back (spec 17.2). Carries the path and the reason so the caller can show both.
/// </summary>
public sealed class AppDataRootUnavailableException(string path, string pointerPath, string reason)
    : Exception($"GalactiLog's data location {path} is not available: {reason}")
{
    public string Path { get; } = path;

    public string PointerPath { get; } = pointerPath;

    public string Reason { get; } = reason;

    /// <summary>
    /// The whole message the GUI message box and the CLI's stderr both show (spec 17.2, 15).
    /// Composed here rather than at the two call sites so the two surfaces cannot say different
    /// things, and so a user who reads one of them is told the same recovery either way.
    /// </summary>
    public string UserMessage =>
        "GalactiLog's data location is not available."
        + Environment.NewLine + Environment.NewLine
        + $"Location: {Path}" + Environment.NewLine
        + $"Reason: {Reason}" + Environment.NewLine + Environment.NewLine
        + "GalactiLog did not start, so that no second, empty library is created beside your own. "
        + "Reconnect the drive that holds this folder and start GalactiLog again. To choose a "
        + "different location, edit or delete this file and start GalactiLog again:"
        + Environment.NewLine + Environment.NewLine
        + PointerPath;
}
