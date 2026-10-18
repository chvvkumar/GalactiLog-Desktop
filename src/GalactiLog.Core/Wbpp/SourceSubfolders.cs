using GalactiLog.Core.Io;

namespace GalactiLog.Core.Wbpp;

/// <summary>The export page's only disk read on the source side: the direct child directories of
/// one folder. The view-models never call it; they receive <see cref="OnDisk"/> as a delegate.</summary>
public static class SourceSubfolders
{
    /// <summary>The folder's direct child directories as absolute paths, ordered by name ignoring
    /// case. Files and reparse-point directories are left out, as the copier leaves them out.</summary>
    /// <remarks>Null when <paramref name="enumerate"/> throws <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> (a missing or unreadable folder). Other exceptions
    /// propagate.</remarks>
    public static IReadOnlyList<string>? Children(
        string folder, Func<string, IEnumerable<FileSystemInfo>> enumerate)
    {
        try
        {
            return enumerate(folder)
                .OfType<DirectoryInfo>()
                .Where(d => (d.Attributes & FileAttributes.ReparsePoint) == 0)
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Select(d => d.FullName)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary><see cref="Children"/> over the real disk, read through <see cref="UserFiles"/>.</summary>
    public static IReadOnlyList<string>? OnDisk(string folder)
        => Children(folder, UserFiles.EnumerateFileSystemEntries);
}
