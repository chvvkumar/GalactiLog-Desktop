namespace GalactiLog.Core.Io;

// The one "is this path inside that root" rule for the whole solution (phase 4 review item 4).
// AppWriter's write authorization, ScanFilterConfig's include/exclude containment,
// FileWalker's reparse-point check and OrphanPruner's row selection all route through here,
// so a fix to the rule cannot land in three places out of four.
//
// Pure string and Path work: no filesystem access at all, so this file needs no FileSafetyTest
// allowlist entry and must never be given one.
public static class PathConfinement
{
    /// <summary>
    /// True when <paramref name="path"/> is <paramref name="root"/> itself or sits beneath it.
    /// Case-insensitive, matching Windows. Both arguments are expected to be
    /// <see cref="Path.GetFullPath"/> results; a trailing separator on either side is
    /// tolerated, so the comparison is symmetric (FIXER LIST 1) instead of depending on
    /// whether the caller happened to normalize.
    /// </summary>
    public static bool IsUnderOrEqual(string root, string path)
    {
        // A drive or share root IS its trailing separator ("C:\" trimmed to "C:" means the
        // process's current directory on C:, an entirely different location), so it is the one
        // shape that must not be trimmed. AppWriter.IsDriveOrShareRoot is the existing rule
        // for recognizing it.
        var normalizedRoot = AppWriter.IsDriveOrShareRoot(root) ? root : TrimTrailingSeparator(root);
        var rootWithSeparator = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;

        return TrimTrailingSeparator(path).Equals(TrimTrailingSeparator(normalizedRoot), StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimTrailingSeparator(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
