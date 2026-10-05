using System.Text;

namespace GalactiLog.Core.Text;

/// <summary>The output format the Copy Frame List dialog renders (spec 12.4, ruling C6). The
/// stored literals are spec 5.8.2's <c>display.target_page.frame_list_format</c> values.</summary>
public enum FrameListFormat
{
    /// <summary>One absolute path per line. The default, and byte for byte what the page copied
    /// before the dialog existed.</summary>
    Paths,

    /// <summary>One bare file name per line, same order, same trailing newline.</summary>
    Names,

    /// <summary>Each bare file name quoted and joined with the literal " OR ", one line, no
    /// trailing newline. For the Windows Explorer search box.</summary>
    Explorer,
}

/// <summary>Which side of the grading the Copy Frame List dialog lists (spec 12.4). The stored
/// literals are spec 5.8.2's <c>display.target_page.frame_list_mode</c> values.</summary>
public enum FrameListMode
{
    /// <summary>The frames the grading did not reject. The default.</summary>
    Good,

    /// <summary>The frames the grading rejected.</summary>
    Bad,
}

/// <summary>Renders a list of absolute frame paths in spec 12.4's three formats, and parses spec
/// 5.8.2's stored literals. Pure: no Avalonia, no IO, no clock. The dialog writes the clipboard
/// and nothing else, so nothing here touches the file system.</summary>
public static class FrameListFormats
{
    /// <summary>The separator between the quoted names of the <see cref="FrameListFormat.Explorer"/>
    /// form: a space, the two letters OR, and a space. Matches the web's
    /// <c>explorerSearchString</c> exactly.</summary>
    public const string ExplorerSeparator = " OR ";

    private const string PathsLiteral = "paths";
    private const string NamesLiteral = "names";
    private const string ExplorerLiteral = "explorer";
    private const string GoodLiteral = "good";
    private const string BadLiteral = "bad";

    /// <summary>Renders the given absolute paths in the given format. The bare names of the
    /// <see cref="FrameListFormat.Names"/> and <see cref="FrameListFormat.Explorer"/> forms are
    /// derived here with <see cref="Path.GetFileName(string)"/> rather than taken from a second
    /// list, so the two name forms cannot disagree about which file a line is.
    ///
    /// <para>The line separator is <see cref="Environment.NewLine"/>, not a bare line feed, because
    /// spec 12.4 says the paths form is byte for byte what the page copied before the dialog
    /// existed and <c>ShellIntegration.CopyFrameListAsync</c> writes
    /// <c>string.Join(Environment.NewLine, framePaths) + Environment.NewLine</c>. The names form
    /// takes the same separator so a user pasting into Notepad does not get one format that works
    /// and one that does not.</para>
    ///
    /// <para>A name containing a double quote is emitted as it is. Explorer's search box has no
    /// escape for one, and inventing one would produce a string that finds nothing (spec 12.4).
    /// This reads like a bug and is not one.</para>
    ///
    /// <para>An empty list renders the empty string in every format, and never a lone
    /// newline.</para></summary>
    public static string Render(FrameListFormat format, IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            return string.Empty;
        }

        return format switch
        {
            FrameListFormat.Names => JoinLines(NamesOf(paths)),
            FrameListFormat.Explorer => JoinExplorer(NamesOf(paths)),
            _ => JoinLines(paths),
        };
    }

    /// <summary>Reads spec 5.8.2's stored literal. Ordinal ignore case, and anything the set does
    /// not hold reads as the key's default, which is 5.8.2's "a stored value outside a key's listed
    /// set reads as that key's default".</summary>
    public static FrameListFormat Parse(string? stored)
        => string.Equals(stored, NamesLiteral, StringComparison.OrdinalIgnoreCase) ? FrameListFormat.Names
            : string.Equals(stored, ExplorerLiteral, StringComparison.OrdinalIgnoreCase) ? FrameListFormat.Explorer
            : FrameListFormat.Paths;

    /// <summary>The literal spec 5.8.2 stores for the given format.</summary>
    public static string ToStored(FrameListFormat format)
        => format switch
        {
            FrameListFormat.Names => NamesLiteral,
            FrameListFormat.Explorer => ExplorerLiteral,
            _ => PathsLiteral,
        };

    /// <summary>Reads spec 5.8.2's stored literal for the mode. Anything outside the set reads as
    /// <see cref="FrameListMode.Good"/>, the key's default.</summary>
    public static FrameListMode ParseMode(string? stored)
        => string.Equals(stored, BadLiteral, StringComparison.OrdinalIgnoreCase)
            ? FrameListMode.Bad
            : FrameListMode.Good;

    /// <summary>The literal spec 5.8.2 stores for the given mode.</summary>
    public static string ToStored(FrameListMode mode)
        => mode == FrameListMode.Bad ? BadLiteral : GoodLiteral;

    private static List<string> NamesOf(IReadOnlyList<string> paths)
    {
        var names = new List<string>(paths.Count);
        for (var i = 0; i < paths.Count; i++)
        {
            names.Add(Path.GetFileName(paths[i]));
        }
        return names;
    }

    private static string JoinLines(IReadOnlyList<string> lines)
        => string.Join(Environment.NewLine, lines) + Environment.NewLine;

    private static string JoinExplorer(IReadOnlyList<string> names)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < names.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(ExplorerSeparator);
            }
            builder.Append('"').Append(names[i]).Append('"');
        }
        return builder.ToString();
    }
}
