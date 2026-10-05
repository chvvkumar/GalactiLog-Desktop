namespace GalactiLog.Core.Io;

// Thrown by AppWriter when a caller asks it to write outside the app data root, the
// thumbnail cache root, or (for a scoped export writer) the one export destination it was
// opened for. See spec 2.1.1.
public sealed class UnauthorizedPathException : Exception
{
    public string Path { get; }

    public UnauthorizedPathException(string path)
        : base($"Path '{path}' is not authorized for writing.")
    {
        Path = path;
    }
}
