using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GalactiLog.Core.Imaging;

/// <summary>
/// Spec 11.3's cache key: the lowercase hex of the first 16 bytes of
/// <c>SHA-256(fullPath + "|" + fileSize + "|" + mtimeUnixSeconds + "|" + width)</c>.
/// </summary>
/// <remarks>
/// Including size and modification time means a re-saved or replaced frame produces a new key
/// rather than serving a stale image, so the cache never needs invalidation. Including the width
/// means changing <c>general.thumbnail_width</c> or <c>general.preview_resolution</c> needs no
/// purge. The web application's key is <c>md5(path)[:12]</c> plus the file stem, which reacts to
/// none of that; the port deliberately strengthens it (spec 11.3).
/// <para>
/// One definition, three callers: the frame thumbnail, the preview and the reference pass. It
/// lives in Core rather than beside <c>ThumbnailCache</c> in App because <c>GalactiLog.Data</c>
/// cannot reference <c>GalactiLog.App</c> (questions.md Q2).
/// </para>
/// </remarks>
public static class ThumbnailKey
{
    /// <param name="fullPath">Used <b>as given</b>: not case-folded and not normalized. The caller
    /// passes <c>Path.GetFullPath(framePath)</c>, because normalizing here would hide from the
    /// caller that two spellings of one path key differently.</param>
    /// <param name="fileSize">The frame's length in bytes.</param>
    /// <param name="mtimeUnixSeconds">Whole seconds from the frame's <c>LastWriteTimeUtc</c>, not
    /// ticks: <c>images.file_mtime</c> already carries a one-second classification tolerance
    /// (spec 10.3), and a sub-second key would churn on an SMB share that rounds timestamps.</param>
    /// <param name="width">The target width the render used. 0 (native resolution) is a distinct
    /// cache entry, which is exactly what including the width buys.</param>
    /// <returns>32 lowercase hex characters. A filename component: it contains no path separator
    /// and needs no escaping.</returns>
    public static string For(string fullPath, long fileSize, long mtimeUnixSeconds, int width)
    {
        ArgumentNullException.ThrowIfNull(fullPath);

        // InvariantCulture on the three numbers: a culture with a digit grouping separator would
        // otherwise change every key on a machine set to it.
        var input = string.Create(
            CultureInfo.InvariantCulture,
            $"{fullPath}|{fileSize}|{mtimeUnixSeconds}|{width}");

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }
}
