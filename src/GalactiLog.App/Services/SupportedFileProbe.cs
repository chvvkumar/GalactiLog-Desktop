using System.Globalization;
using GalactiLog.Core.Io;
using GalactiLog.Core.Metadata;

namespace GalactiLog.App.Services;

/// <summary>
/// Design-spec 12.1's shallow probe: an approximate count of supported files under a folder,
/// cheap enough to run while the user is still choosing folders. Never a full walk, because a NAS
/// share with a hundred thousand frames must not block the wizard.
/// </summary>
/// <remarks>
/// <para>
/// One probe, two callers (design-lessons rule 1, at the second occurrence): the setup wizard's
/// step 1 and the Settings Library tab's scan roots list both count through this type, so the two
/// surfaces cannot report different numbers for the same folder. Task 5 exposed the seam
/// (<c>LibraryTabViewModel.ProbeSupportedFiles</c>); this is what fills it.
/// </para>
/// <para>
/// Reads only. It enumerates directory entries and tests their extensions; it opens no file, it
/// creates nothing, and it deletes nothing. Every enumeration goes through
/// <see cref="UserFiles"/>, the read-only half of the file-safety choke point (spec 2.1.1), and
/// the extension test goes through <see cref="FrameReader.IsSupported"/>, which is the same gate
/// <c>FileWalker</c> applies, so the probe's four extensions and the scanner's four extensions are
/// one list and not two (spec 10.2).
/// </para>
/// <para>
/// An unreadable directory is skipped and the walk continues, exactly as <c>FileWalker</c> already
/// does for an inaccessible subtree, and an unreachable network path yields 0 rather than an
/// exception: this count is an aid to choosing a folder, not a verdict on it.
/// </para>
/// </remarks>
public sealed class SupportedFileProbe
{
    /// <summary>
    /// The probe's ceiling. Counting stops here and the caller renders "1000 or more".
    /// </summary>
    /// <remarks>A thousand is enough to tell "this is my library" from "this is the wrong
    /// folder", which is the only question the number answers, and it bounds the wall-clock cost
    /// on a slow share while the user is waiting to click Next (questions.md Q33).</remarks>
    public const int MaxCounted = 1000;

    /// <summary>
    /// Directory levels below the chosen folder the probe descends. Spec 12.1's "shallow".
    /// </summary>
    /// <remarks>Three reaches <c>&lt;root&gt;/2025/M31/&lt;files&gt;</c> and
    /// <c>&lt;root&gt;/Telescope/2025/M31/&lt;files&gt;</c>, which covers the common capture
    /// layouts. A directory at this depth is counted; its children are not visited
    /// (questions.md Q33).</remarks>
    public const int MaxDepth = 3;

    // Non-recursive, and deliberately not IgnoreInaccessible: an inaccessible directory must
    // throw so it can be caught and skipped, rather than return an empty sequence that looks
    // exactly like an empty folder. The same reasoning UserFiles.EnumerateFileSystemEntries
    // records for the scan walker.
    private static readonly EnumerationOptions ProbeOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        MatchCasing = MatchCasing.CaseInsensitive,
        AttributesToSkip = 0,
    };

    private readonly Func<string, IEnumerable<string>> _files;
    private readonly Func<string, IEnumerable<string>> _directories;

    /// <summary>The production probe, reading through <see cref="UserFiles"/>.</summary>
    public SupportedFileProbe()
        : this(null, null)
    {
    }

    /// <param name="files">How one directory's files are listed. Null uses
    /// <c>UserFiles.EnumerateFiles</c>.</param>
    /// <param name="directories">How one directory's subdirectories are listed. Null uses
    /// <c>UserFiles.EnumerateDirectories</c>.</param>
    /// <remarks>Internal, and the only reason it exists is that "an unreadable subtree is skipped
    /// and the walk continues" cannot be arranged on a real filesystem in a test that must pass
    /// on any machine, with or without an elevated token. The production constructor above takes
    /// no seam at all.</remarks>
    internal SupportedFileProbe(
        Func<string, IEnumerable<string>>? files,
        Func<string, IEnumerable<string>>? directories)
    {
        _files = files ?? (directory => UserFiles.EnumerateFiles(directory, "*", ProbeOptions));
        _directories = directories ?? UserFiles.EnumerateDirectories;
    }

    /// <summary>
    /// Counts supported files under <paramref name="folder"/>, descending at most
    /// <see cref="MaxDepth"/> levels and stopping at <see cref="MaxCounted"/>.
    /// </summary>
    /// <remarks>Call it off the UI thread: even a bounded enumeration can block for seconds on a
    /// disconnected share. Cancellable, because the user may choose another folder while it runs.
    /// </remarks>
    /// <returns>The count, never above <see cref="MaxCounted"/>. Zero for a blank path, a missing
    /// folder or an unreachable one.</returns>
    public int Count(string folder, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return 0;
        }

        var total = 0;

        // An explicit stack rather than recursion: the depth is capped at three, but the shape
        // also keeps both limits readable in one loop.
        var pending = new Stack<(string Directory, int Depth)>();
        pending.Push((folder, 0));

        while (pending.Count > 0 && total < MaxCounted)
        {
            ct.ThrowIfCancellationRequested();
            var (directory, depth) = pending.Pop();

            total += CountFilesIn(directory, MaxCounted - total, ct);
            if (total >= MaxCounted)
            {
                break;
            }

            if (depth >= MaxDepth)
            {
                continue;
            }

            foreach (var child in ListDirectories(directory, ct))
            {
                pending.Push((child, depth + 1));
            }
        }

        return Math.Min(total, MaxCounted);
    }

    /// <summary>
    /// The count as the wizard and the Library tab render it: the plain number, or
    /// "1000 or more" once the ceiling was reached (questions.md Q33).
    /// </summary>
    public static string Format(int count)
        => count >= MaxCounted
            ? $"{MaxCounted} or more"
            : count.ToString(CultureInfo.CurrentCulture);

    // Counts at most `remaining` supported files in one directory. Enumeration is lazy, so a
    // permission or IO fault surfaces from MoveNext rather than from the call that built the
    // sequence; both are caught here and end this directory without ending the walk.
    private int CountFilesIn(string directory, int remaining, CancellationToken ct)
    {
        var counted = 0;
        IEnumerator<string> entries;
        try
        {
            entries = _files(directory).GetEnumerator();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 0;
        }

        using (entries)
        {
            while (counted < remaining)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    if (!entries.MoveNext())
                    {
                        break;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    break;
                }

                // The one extension gate in this application (spec 10.2). FileWalker applies the
                // same call, so a format added there is counted here with no second edit.
                if (FrameReader.IsSupported(entries.Current))
                {
                    counted++;
                }
            }
        }

        return counted;
    }

    // Materialized, unlike the file enumeration above: the list is one directory's children, the
    // caller pushes them onto a stack anyway, and it keeps the per-directory catch around the
    // whole enumeration rather than around each step of it.
    private List<string> ListDirectories(string directory, CancellationToken ct)
    {
        try
        {
            var children = new List<string>();
            foreach (var child in _directories(directory))
            {
                ct.ThrowIfCancellationRequested();
                children.Add(child);
            }

            return children;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }
}
