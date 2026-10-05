using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using GalactiLog.Core.Io;

namespace GalactiLog.Core.Wbpp;

/// <summary>A value that cannot be written into a generated script at all. Thrown by the two
/// quoters for a carriage return or a line feed, which would end the quoted literal and turn the
/// rest of the value into script text, and by <see cref="ScriptGenerator.Generate"/> for the four
/// values the Python interpolates raw, for a file name outside the allowlist, for an entry name
/// that could leave the staging root, and for an exclusion pattern carrying a character
/// <see cref="ScriptGenerator.IsRefusedPatternChar"/> refuses. Carries both the kind of input
/// (<see cref="ValueName"/>) and the input itself (<see cref="Value"/>), so the page can pick its
/// sentence and name the file it refused without parsing a message.</summary>
public sealed class WbppUnsafeValueException(string valueName, string value, string reason)
    : ArgumentException($"The value supplied as {valueName} cannot be written into an export script: {reason}.", valueName)
{
    /// <summary>The kind of input that carried the refused character, such as
    /// <c>SourcePath</c>, <c>FileName</c> or <c>exclusion pattern</c>. A token the page can switch
    /// on, never something it has to parse out of a message.</summary>
    public string ValueName { get; } = valueName;

    /// <summary>The offending input itself, exactly as it was given, so the page can name the file
    /// or pattern it refused as spec 12.13's script contract requires. A value refused for a
    /// carriage return or a line feed still carries those characters: making a control character
    /// visible is the display's job, not this type's. The value is deliberately absent from
    /// <see cref="Exception.Message"/>, which can reach a log.</summary>
    public string Value { get; } = value;

    /// <summary>Why the value was refused, in one clause, with no part of the value in it.</summary>
    public string Reason { get; } = reason;
}

/// <summary>A staging root a script may not be generated against: empty, or equal to or inside one
/// of the source folders the generator was handed. The second is the generator's own half of spec
/// 12.13's "no source path is ever a destination", held at the one point every script passes
/// through; the page's wider rule against a staging folder under a scan root is enforced when the
/// field is committed and is not this type's.</summary>
public sealed class WbppStagingRootException(string message, string? sourcePath)
    : ArgumentException(message, nameof(WbppScriptInput.StagingRoot))
{
    /// <summary>The source folder the staging root collided with, or null when the staging root was
    /// simply empty.</summary>
    public string? SourcePath { get; } = sourcePath;
}

/// <summary>Turns a list of copy operations into the text of one PowerShell or Bash script that
/// copies those folders into a staging root and nothing else. A port of
/// <c>generate_powershell_script</c>, <c>generate_shell_script</c>, <c>_ps_quote</c>,
/// <c>_sh_quote</c> and <c>sanitize_script_name</c> in the web application's
/// <c>backend/app/services/wbpp_export.py</c>, with the departures listed on
/// <see cref="Generate"/>. The script is a pure text value: nothing in this file opens, writes or
/// names a file.</summary>
public static partial class ScriptGenerator
{
    /// <summary>The nine folder patterns a new library starts with, verbatim and in spec 5.8.1's
    /// order, which is the Python's <c>DEFAULT_EXCLUSIONS</c> order. The three spellings of masters
    /// are redundant in the PowerShell flavour, whose matching is case insensitive, and are not
    /// redundant in the Bash flavour, whose rsync patterns are case sensitive. One case in Task 3a
    /// pins <c>GeneralSettings</c>' own default against this list.</summary>
    public static IReadOnlyList<string> DefaultExclusions { get; } =
    [
        "WBPP", "PixInsight", "finals", "WORK_AREA",
        "masters", "Masters", "MASTERS", "*CALIBRATED", "CALIBRATED",
    ];

    private const string PowerShellLiteral = "powershell";
    private const string BashLiteral = "bash";

    // Python's re.escape since 3.7 escapes exactly these 24 characters and passes everything else
    // through. Regex.Escape is NOT the same set (.NET leaves ] } - & ~ unescaped), and an exclusion
    // pattern containing one of those would produce a different, equally valid, non-identical
    // script, which is a byte-parity failure that reads like a bug in the test.
    private const string PythonRegexSpecials = "()[]{}?*+-|^$\\.&~# \t\n\r\u000b\f";

    /// <summary>Reads spec 5.8.1's stored <c>wbpp_default_os</c> literal. Ordinal ignore case, and
    /// anything outside the set, including null, empty and an untrimmed literal, reads as the key's
    /// default of <see cref="WbppScriptType.PowerShell"/>, which is what
    /// <c>FrameListFormats.Parse</c> already does for its own key.</summary>
    public static WbppScriptType ParseOs(string? stored)
        => string.Equals(stored, BashLiteral, StringComparison.OrdinalIgnoreCase)
            ? WbppScriptType.Bash
            : WbppScriptType.PowerShell;

    /// <summary>The literal spec 5.8.1 stores for the given script type.</summary>
    public static string ToStored(WbppScriptType type)
        => type == WbppScriptType.Bash ? BashLiteral : PowerShellLiteral;

    /// <summary>Makes a target name safe to use as a file name component, the port of
    /// <c>sanitize_script_name</c>: every run of characters outside <c>A-Za-z0-9._-</c> becomes one
    /// underscore, leading and trailing dots and underscores are trimmed, and a name with nothing
    /// usable left falls back to the literal <c>target</c>. The replaced set covers the
    /// Windows-illegal characters and the apostrophe, so the result never needs quoting.</summary>
    public static string SanitizeScriptName(string targetName)
    {
        var cleaned = UnsafeNameRun().Replace(targetName ?? string.Empty, "_").Trim('.', '_');
        return cleaned.Length == 0 ? "target" : cleaned;
    }

    /// <summary>The suggested file name for a flavour, the web's <c>f"wbpp_{safe}.ps1"</c> and
    /// <c>f"wbpp_{safe}.sh"</c>.</summary>
    public static string FileNameFor(WbppScriptType type, string targetName)
        => "wbpp_" + SanitizeScriptName(targetName) + (type == WbppScriptType.Bash ? ".sh" : ".ps1");

    /// <summary>Quotes a value as one PowerShell expression that evaluates to it. A run of
    /// printable ASCII is a single-quoted literal with every embedded apostrophe doubled, which is
    /// the port of <c>_ps_quote</c>; every other character is spliced out of the literal by code
    /// point and the whole is parenthesised, so the emitted text is always pure printable ASCII and
    /// is valid anywhere a literal was.
    /// <para>The splice is not decoration. Windows PowerShell treats U+2018, U+2019, U+201A and
    /// U+201B as single-quote characters, so one of them in a folder name closes the literal and
    /// the rest of the value is parsed as script, which is arbitrary code execution from a folder
    /// name; measured on 5.1, and a value carrying one really did execute an injected command.
    /// Doubling them is not a fix, because <c>'A</c>U+2019U+2019<c>B'</c> parses and yields an
    /// ASCII apostrophe, a silently different path. Refusing them would refuse a real user folder,
    /// which is what R5 declined to do for brackets. Keeping the file ASCII also removes the
    /// encoding hazard, since Windows PowerShell reads a byte-order-mark-less script as the system
    /// ANSI code page.</para>
    /// <para>Quoting stops the value being parsed as script; it does not stop wildcard expansion,
    /// which is why every disk site in the generated script reads <c>-LiteralPath</c>.</para></summary>
    /// <param name="value">The value to quote.</param>
    /// <param name="valueName">What to call the value if it has to be refused.</param>
    /// <exception cref="WbppUnsafeValueException">The value contains a carriage return or a line
    /// feed, or an unpaired surrogate, which has no code point to splice. The line-break check
    /// lives here, at the one point every quoted value passes, so no caller can reach the script
    /// text around it. A NUL and an escape character are neither refused nor escaped in the Bash
    /// quoter, because bash drops a NUL from a script silently and an escape in a comment line is
    /// emitted raw; on this side both are spliced out by code point like any other character
    /// outside the printable range.</exception>
    public static string PowerShellQuote(string value, string valueName = "value")
    {
        RefuseLineBreaks(value, valueName);

        if (IsPrintableAscii(value))
        {
            // The whole of the shipped path today, and every golden input, so the common form is
            // byte for byte the Python's.
            return "'" + value.Replace("'", "''") + "'";
        }

        var terms = new List<string>();
        var run = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c is >= ' ' and <= '~')
            {
                run.Append(c == '\'' ? "''" : c.ToString());
                continue;
            }

            if (run.Length > 0)
            {
                terms.Add("'" + run + "'");
                run.Clear();
            }

            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                terms.Add(
                    "[char]::ConvertFromUtf32(0x"
                    + char.ConvertToUtf32(c, value[i + 1]).ToString("X", CultureInfo.InvariantCulture)
                    + ")");
                i++;
                continue;
            }

            if (char.IsSurrogate(c))
            {
                throw new WbppUnsafeValueException(
                    valueName,
                    value,
                    "an unpaired surrogate has no code point, so it cannot be written into a script as one");
            }

            terms.Add("[char]0x" + ((int)c).ToString("X4", CultureInfo.InvariantCulture));
        }

        if (run.Length > 0)
        {
            terms.Add("'" + run + "'");
        }

        if (terms.Count == 1 && terms[0].StartsWith("[char]0x", StringComparison.Ordinal))
        {
            // Measured on 5.1: a sum whose left operand is a [char] is a String, so every
            // multi-term expression already answers one, but a lone [char] term on its own is a
            // Char. A folder named with one non-ASCII character is the only value that produces
            // it, and an empty literal in front makes the member total rather than correct only
            // for values with an ASCII character somewhere in them.
            terms.Insert(0, "''");
        }

        // One parenthesised expression, so a hashtable value, a Join-Path argument, an array
        // element and an assignment all still take exactly one operand. Measured on 5.1: the
        // result is a String even when the first term is a [char].
        return "(" + string.Join(" + ", terms) + ")";
    }

    /// <summary>Quotes a value as one Bash literal, the port of <c>_sh_quote</c>: single quotes
    /// with every embedded apostrophe closed, escaped and reopened through the <c>'\''</c> idiom. A
    /// single-quoted Bash literal stops command substitution, variable expansion, globbing and
    /// every other metacharacter in a path that came off the file system.</summary>
    /// <param name="value">The value to quote.</param>
    /// <param name="valueName">What to call the value if it has to be refused.</param>
    /// <exception cref="WbppUnsafeValueException">The value contains a carriage return or a line
    /// feed; see <see cref="PowerShellQuote"/> for why the check lives in the quoter and why the
    /// refusal set is exactly those two characters. No splicing happens on this side: bash gives
    /// no Unicode character any quoting power inside a single-quoted string, only U+0027 ends one,
    /// measured under bash as well as reasoned.</exception>
    public static string BashQuote(string value, string valueName = "value")
    {
        RefuseLineBreaks(value, valueName);
        return "'" + value.Replace("'", "'\\''") + "'";
    }

    /// <summary>The one home for "this character cannot appear in an exclusion pattern". A pattern
    /// is interpolated into a double-quoted PowerShell regex group and into a double-quoted rsync
    /// <c>--exclude="..."</c> without passing a quoter, so each of these would break out of the
    /// literal it lands in: the double quote ends both, the dollar sign and the backtick are live
    /// inside a PowerShell double-quoted string, a <b>backslash</b> is the escape character inside
    /// a Bash double-quoted string (a pattern ending in one swallows the closing quote and
    /// collapses two <c>--exclude</c> arguments into one that matches nothing, so every excluded
    /// folder is copied), a carriage return or a line feed ends the line, and U+2018 to U+201F are
    /// the Unicode quote characters, of which Windows PowerShell reads U+201C, U+201D and U+201E
    /// as a double quote. U+201F was measured inert on 5.1; the range is kept whole because one
    /// contiguous range is cheaper to reason about than a list with a hole in it.
    /// <para><see cref="Generate"/> refuses a pattern carrying one, and
    /// <c>WbppSettingsRead.Exclusions</c> drops a stored entry carrying one, both through this
    /// member, so the editor, the reader and the generator cannot disagree about the set.</para></summary>
    public static bool IsRefusedPatternChar(char c)
        => c is '"' or '$' or '`' or '\\' or '\r' or '\n' or (>= '‘' and <= '‟');

    /// <summary>Ruling R3, the one translation the Bash flavour applies to every path it emits and
    /// nothing else applies at all: a drive path <c>C:\Astro\M 31</c> becomes
    /// <c>/mnt/c/Astro/M 31</c> with the drive letter lower cased, a UNC path
    /// <c>\\server\share\a</c> becomes <c>//server/share/a</c>, a trailing separator survives as a
    /// trailing slash, and every separator becomes a forward slash. The input is always a
    /// <c>Path.GetFullPath</c> result, so a forward-slash drive form cannot arise; the member is
    /// nonetheless total over any input, because the separator pass runs first and the drive rule
    /// then applies to whatever it produced.</summary>
    public static string ToPosixPath(string windowsPath)
    {
        if (string.IsNullOrEmpty(windowsPath))
        {
            return windowsPath ?? string.Empty;
        }

        var slashed = windowsPath.Replace('\\', '/');
        if (slashed.Length < 2 || slashed[1] != ':' || !char.IsAsciiLetter(slashed[0]))
        {
            // A UNC path already starts with two separators, which are now two slashes, and a path
            // that was POSIX to begin with is returned unchanged.
            return slashed;
        }

        var drive = char.ToLowerInvariant(slashed[0]);
        var rest = slashed[2..];
        if (rest.Length == 0)
        {
            return "/mnt/" + drive;
        }

        return rest[0] == '/' ? "/mnt/" + drive + rest : "/mnt/" + drive + "/" + rest;
    }

    /// <summary>Renders one script. Returns the text and touches no file; the caller performs the
    /// single write of this phase, so the saved file, the clipboard and the shown text are one
    /// string and cannot disagree. The line separator is <c>"\n"</c>, never
    /// <see cref="Environment.NewLine"/>, and there is no trailing newline, because the Python
    /// joins with <c>"\n"</c> and the golden files are compared byte for byte.
    /// <para>Departures from the Python, all ruled: R2, the PowerShell exclusion predicate matches
    /// components of the path relative to the job's own source folder rather than of the absolute
    /// path; R3, every path the Bash flavour emits goes through <see cref="ToPosixPath"/> and its
    /// header gains one line about the mount prefix; R5, every PowerShell site that reaches the
    /// file system and has a <c>-LiteralPath</c> parameter uses it; the Bash rsync invocation
    /// carries <c>--</c> before its operands and uses the <c>--exclude=&lt;pattern&gt;</c> form;
    /// the staging root is required and never derived; and the <c>Sessions</c> header line is built
    /// from the operations' own nights.</para></summary>
    /// <exception cref="WbppStagingRootException">The staging root is empty or whitespace, or it
    /// equals or sits inside one of the source folders. The other direction, a staging root that
    /// contains a source, is the page's rule and is enforced when the field is committed; the
    /// guard here is deliberately one half of it, as core-shapes.md section 7 rules.</exception>
    /// <exception cref="WbppUnsafeValueException">Any value carries a carriage return or a line
    /// feed; the file name falls outside the allowlist; an entry name would leave the staging root;
    /// or an exclusion pattern carries a character <see cref="IsRefusedPatternChar"/> refuses.</exception>
    public static string Generate(WbppScriptType type, WbppScriptInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateInput(type, input);

        var sessionDates = string.Join(
            ", ",
            input.Operations.Select(o => o.Night.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));

        return type == WbppScriptType.Bash
            ? GenerateBash(input, sessionDates)
            : GeneratePowerShell(input, sessionDates);
    }

    // -----------------------------------------------------------------------------------------
    // Validation. The quoters are the gate for every value that reaches a quoted literal; what is
    // checked here is the rest: the four values the Python interpolates raw and therefore never
    // offers to a quoter, the exclusion patterns, which reach a double-quoted PowerShell string and
    // a double-quoted rsync argument unquoted in both flavours, and the two staging root rules.
    // -----------------------------------------------------------------------------------------

    private static void ValidateInput(WbppScriptType type, WbppScriptInput input)
    {
        // The file name is the one raw-interpolated value that lands in text a user is told to
        // paste into a shell, so it is validated by allowlist rather than by refusal: a refusal
        // list is only ever as complete as the last review of it. Ruled departure: the Python
        // falls back to "this script" and "wbpp_export.sh"; the port requires a name, and
        // FileNameFor always supplies one that is inside this set by construction.
        var extension = type == WbppScriptType.Bash ? ".sh" : ".ps1";
        if (input.FileName.Length <= extension.Length
            || input.FileName[0] is '-' or '.'
            || !input.FileName.EndsWith(extension, StringComparison.Ordinal)
            || !input.FileName.All(IsAllowedFileNameChar))
        {
            throw new WbppUnsafeValueException(
                nameof(input.FileName),
                input.FileName,
                "a script file name may hold only ASCII letters, digits, spaces, dots, underscores, hyphens and parentheses, must not begin with a dash or a dot, and must end in " + extension);
        }

        if (string.IsNullOrWhiteSpace(input.StagingRoot))
        {
            throw new WbppStagingRootException(
                "A WBPP export script needs a staging folder; the generator derives none.", null);
        }

        // Spec 12.13's "no source path is ever a destination", held at the generator's own choke
        // point rather than resting on the page alone. One rule, PathConfinement.IsUnderOrEqual,
        // the same comparison the rest of the solution makes about paths.
        foreach (var operation in input.Operations)
        {
            if (PathConfinement.IsUnderOrEqual(operation.SourcePath, input.StagingRoot))
            {
                throw new WbppStagingRootException(
                    "The staging folder is one of the folders being copied, or sits inside one, so the copy would write into its own source.",
                    operation.SourcePath);
            }
        }

        // TargetName is the one of these that has no second guard: it reaches a comment line
        // without passing a quoter. StagingRoot and, below, EntryName both reach a quoter as well,
        // which refuses the same two characters under the same name, so the calls here are
        // deliberate redundancy: they put the refusal at Generate's own choke point, before any
        // text is composed, rather than leaving it to hold only as long as every future emission
        // site keeps going through a quoter.
        RefuseLineBreaks(input.TargetName, nameof(input.TargetName));
        RefuseLineBreaks(input.StagingRoot, nameof(input.StagingRoot));
        foreach (var operation in input.Operations)
        {
            // A night is a DateOnly and cannot carry a line break; the joined header line is
            // therefore safe by construction, and the entry name is checked because it reaches the
            // Bash copy_folder call and the PowerShell Join-Path through a quoter that would name
            // it far less helpfully.
            RefuseLineBreaks(operation.EntryName, nameof(operation.EntryName));

            // The entry name is the other half of every destination the script writes, and both
            // flavours join it to the staging root without resolving it. Measured: an entry name of
            // "..\escaped" made Copy-Item land the file beside the staging root and made
            // mkdir -p create a folder beside it, so the staging guarantee this file holds at its
            // own choke point needs the name as well as the root. FolderLevels.StagingNames returns
            // folder basenames and cannot produce one of these, which is exactly why the guard
            // belongs here rather than there.
            if (operation.EntryName.Length == 0
                || operation.EntryName.IndexOfAny(['\\', '/', ':']) >= 0
                || operation.EntryName is "." or "..")
            {
                throw new WbppUnsafeValueException(
                    nameof(operation.EntryName),
                    operation.EntryName,
                    "a staging entry name is one folder name: it may not be rooted, carry a separator, or be a relative-path component");
            }
        }

        foreach (var pattern in input.Exclusions)
        {
            if (pattern.Any(IsRefusedPatternChar))
            {
                throw new WbppUnsafeValueException(
                    "exclusion pattern",
                    pattern,
                    "a folder pattern cannot contain a quote of any kind, a dollar sign, a backtick, a backslash or a line break, because it reaches both scripts inside a double-quoted literal");
            }
        }
    }

    // Spec 12.13's file name shape, as an allowlist. Parentheses are here because a save dialog
    // hands back "wbpp_M_31 (1).ps1" when the user keeps a second copy, and a space for the same
    // reason. Neither can break out of anything: the PowerShell run instruction wraps the name in a
    // single-quoted literal inside its -Command string, and the Bash chmod line, which the Python
    // leaves unquoted, can at worst be a comment line the user has to correct by hand.
    private static bool IsAllowedFileNameChar(char c)
        => char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or '_' or '-' or '(' or ')';

    private static bool IsPrintableAscii(string value)
    {
        foreach (var c in value)
        {
            if (c is < ' ' or > '~')
            {
                return false;
            }
        }

        return true;
    }

    // A header comment is inert text, but it is text the user reads to decide whether to run the
    // file, and a character outside the printable range can reorder or hide what that line says.
    // Carriage return and line feed are refused before this runs; everything else outside the
    // printable ASCII range becomes a question mark. Every golden input is printable ASCII, so this
    // changes no byte of the parity comparison.
    private static string CommentText(string value)
    {
        if (IsPrintableAscii(value))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(c is >= ' ' and <= '~' ? c : '?');
        }

        return builder.ToString();
    }

    private static void RefuseLineBreaks(string value, string valueName)
    {
        if (value is not null && value.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            throw new WbppUnsafeValueException(
                valueName,
                value,
                "a carriage return or a line feed would end the quoted literal and turn the rest of the value into script");
        }
    }

    // -----------------------------------------------------------------------------------------
    // The PowerShell flavour
    // -----------------------------------------------------------------------------------------

    private static string GeneratePowerShell(WbppScriptInput input, string sessionDates)
    {
        var patterns = EscapedExclusionGroup(input.Exclusions);
        var hasPerFileExcludes = HasPerFileExcludes(input);

        // The run instruction is the one line of this file that is meant to be pasted into a shell,
        // so it interpolates nothing but the allowlisted file name. Ruled departure: the Python
        // doubles the name's apostrophes inline here; the allowlist does not admit an apostrophe,
        // so the doubling is unreachable and is not ported. No golden file name carries one, so
        // the emitted bytes are unchanged.
        var lines = new List<string>
        {
            "# WBPP Session Export",
            $"# Target: {CommentText(input.TargetName)}",
            $"# Sessions: {sessionDates}",
            "#",
            "# To run this script:",
            "#   Open PowerShell in this folder. Files downloaded via a browser are blocked",
            "#   by Windows (Mark of the Web); this unblocks and runs the script in one step:",
            $@"#     powershell -ExecutionPolicy Bypass -Command ""Unblock-File -LiteralPath '.\{input.FileName}'; & '.\{input.FileName}'""",
            "#",
            "# When it finishes, open PixInsight WBPP and use 'Add Directory' on the",
            $"# staging root: {CommentText(input.StagingRoot)}",
            "",
            $"$StagingRoot = {PowerShellQuote(input.StagingRoot, nameof(input.StagingRoot))}",
            "$ErrorActionPreference = 'Stop'",
            // R5: Test-Path reads -LiteralPath. New-Item keeps -Path because PowerShell gives it no
            // -LiteralPath parameter at all and its -Path is a path to create rather than a pattern
            // to resolve, so a bracketed name is already created literally.
            "if (-not (Test-Path -LiteralPath $StagingRoot)) { New-Item -ItemType Directory -Force -Path $StagingRoot | Out-Null }",
            "",
            "$Jobs = @(",
        };

        foreach (var operation in input.Operations)
        {
            var job = "    @{ Src = " + PowerShellQuote(operation.SourcePath, nameof(operation.SourcePath))
                + "; Dst = (Join-Path $StagingRoot " + PowerShellQuote(operation.EntryName, nameof(operation.EntryName)) + ")";
            if (hasPerFileExcludes)
            {
                // The port's relative paths already carry backslashes, so the Python's separator
                // replacement is a no-op here; it is kept because it is the Python's rule and it
                // makes the member total over a path that arrived slash separated.
                var quoted = string.Join(
                    ", ",
                    operation.ExcludedRelativePaths.Select(e =>
                        PowerShellQuote(e.Replace("/", "\\"), "excluded file")));
                job += $"; ExcludeFiles = @({quoted})";
            }

            lines.Add(job + " }");
        }

        lines.AddRange(
        [
            ")",
            "",
            "# ---------------------------------------------------------------------------",
            "# Display helpers",
            "# ---------------------------------------------------------------------------",
            "# Render block glyphs from code points so this script stays ASCII on disk;",
            "# the console renders them as Unicode once OutputEncoding is UTF-8.",
            "$Glyph = @{",
            "    Full  = [char]0x2588   # full block",
            "    Light = [char]0x2591   # light shade",
            "    CapL  = [char]0x2595   # right one-eighth block (left edge cap)",
            "    CapR  = [char]0x258F   # left one-eighth block (right edge cap)",
            "}",
            "try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }",
            "",
            "function Format-Bytes {",
            "    param([double]$Bytes)",
            "    if ($Bytes -ge 1GB) { return ('{0:N1} GB' -f ($Bytes / 1GB)) }",
            "    elseif ($Bytes -ge 1MB) { return ('{0:N1} MB' -f ($Bytes / 1MB)) }",
            "    elseif ($Bytes -ge 1KB) { return ('{0:N0} KB' -f ($Bytes / 1KB)) }",
            "    else { return ('{0:N0} B' -f $Bytes) }",
            "}",
            "",
            "function Format-Duration {",
            "    param([double]$Seconds)",
            "    if ($Seconds -lt 0 -or [double]::IsNaN($Seconds) -or [double]::IsInfinity($Seconds)) { return '--:--' }",
            "    $ts = [TimeSpan]::FromSeconds([Math]::Round($Seconds))",
            "    if ($ts.TotalHours -ge 1) {",
            "        return ('{0:d2}:{1:d2}:{2:d2}' -f [int]$ts.TotalHours, $ts.Minutes, $ts.Seconds)",
            "    }",
            "    return ('{0:d2}:{1:d2}' -f $ts.Minutes, $ts.Seconds)",
            "}",
            "",
            "# Pass 1: gather the full file list (so progress can show an accurate total).",
            "Write-Host ''",
            "Write-Host '  Scanning source folders...' -ForegroundColor Cyan",
            "$Files = @()",
            "$TotalBytes = [long]0",
            "foreach ($Job in $Jobs) {",
            // R5 again: -LiteralPath, so a source folder genuinely named "M31 [HaOIII]" is read as
            // itself rather than as a wildcard pattern that matches nothing.
            "    Get-ChildItem -LiteralPath $Job.Src -Recurse -File | Where-Object {",
        ]);

        // R2, the second block that is deliberately not the Python's. The Python splits
        // $_.FullName, the ABSOLUTE path, so a library sitting under a folder named "masters"
        // excludes every file of every job and the script reports a successful copy of nothing.
        // The port cuts the path at the job's own source root first, which is the same domain the
        // per-file exclusions already use. The expression is self-contained rather than a reference
        // to $RelPath, because the folder-only branch below emits no assignment.
        var basePred = input.Exclusions.Count > 0
            ? $@"-not ($_.FullName.Substring($Job.Src.Length).TrimStart('\', '/').Split([char[]]@('\', '/')) | Where-Object {{ $_ -match ""^({patterns})$"" }})"
            : "$true";

        if (hasPerFileExcludes)
        {
            lines.Add(@"        $RelPath = $_.FullName.Substring($Job.Src.Length).TrimStart('\', '/').Replace('/', '\')");
            lines.Add($"        ({basePred}) -and (-not ($Job.ExcludeFiles -contains $RelPath))");
        }
        else
        {
            lines.Add("        " + basePred);
        }

        lines.AddRange(
        [
            "    } | ForEach-Object {",
            @"        $RelPath = $_.FullName.Substring($Job.Src.Length).TrimStart('\', '/')",
            "        $TotalBytes += $_.Length",
            "        $Files += [pscustomobject]@{ Source = $_.FullName; Target = (Join-Path $Job.Dst $RelPath); Size = $_.Length }",
            "    }",
            "}",
            "",
            "# Pass 2: copy with an inline progress bar.",
            "$Total = $Files.Count",
            "Write-Host ('  Found {0} file(s), {1}' -f $Total, (Format-Bytes $TotalBytes)) -ForegroundColor Gray",
            "Write-Host ('  Destination: {0}' -f $StagingRoot) -ForegroundColor DarkGray",
            "Write-Host ''",
            "",
            "# Decide whether we can drive the cursor for in-place redraws.",
            "$CanDraw = $false",
            "$OriginRow = 0",
            "try {",
            "    if (-not [Console]::IsOutputRedirected) {",
            "        [Console]::CursorVisible = $false",
            "        Write-Host ''   # reserve line 1 (stats)",
            "        Write-Host ''   # reserve line 2 (bar)",
            "        $OriginRow = [Console]::CursorTop - 2",
            "        $CanDraw = $true",
            "    }",
            "} catch { $CanDraw = $false }",
            "",
            "# Bar width adapts to the window, clamped to a sane range.",
            "$BarWidth = 40",
            "try { $BarWidth = [Math]::Max(20, [Math]::Min(50, [Console]::WindowWidth - 38)) } catch { }",
            "",
            "function Show-CopyProgress {",
            "    param(",
            "        [int]$Index, [int]$Total, [long]$Copied, [long]$TotalBytes, [double]$ElapsedSec, [switch]$Done",
            "    )",
            "    if ($TotalBytes -gt 0) { $frac = $Copied / $TotalBytes } else { $frac = $Index / [Math]::Max($Total, 1) }",
            "    if ($frac -gt 1) { $frac = 1 }",
            "    if ($frac -lt 0) { $frac = 0 }",
            "    $pct = [int][Math]::Floor($frac * 100)",
            "",
            "    if ($ElapsedSec -gt 0) { $rate = $Copied / $ElapsedSec } else { $rate = 0 }",
            "    if ($rate -gt 0 -and -not $Done) { $eta = ($TotalBytes - $Copied) / $rate } else { $eta = -1 }",
            "    if ($Done) { $eta = 0 }",
            "",
            "    $cells = [int][Math]::Round($frac * $BarWidth)",
            "    if ($cells -gt $BarWidth) { $cells = $BarWidth }",
            "    $filled = ([string]$Glyph.Full) * $cells",
            "    $empty = ([string]$Glyph.Light) * ($BarWidth - $cells)",
            "    if ($Done) { $barColor = 'Green' } else { $barColor = 'Cyan' }",
            "",
            "    $rest = ('   {0}/{1}   {2,3}%   {3} / {4}   {5}/s   ETA {6}' -f `",
            "        $Index, $Total, $pct, (Format-Bytes $Copied), (Format-Bytes $TotalBytes), `",
            "        (Format-Bytes $rate), (Format-Duration $eta))",
            "",
            "    if ($CanDraw) {",
            "        try {",
            "            $w = [Console]::WindowWidth",
            "            [Console]::SetCursorPosition(0, $OriginRow)",
            "",
            "            # Line 1: title accent + stats, padded to clear any previous frame.",
            "            Write-Host -NoNewline '  '",
            "            Write-Host -NoNewline 'WBPP staging copy' -ForegroundColor Cyan",
            "            $line1Len = 2 + 17 + $rest.Length",
            "            $pad1 = [Math]::Max(0, $w - 1 - $line1Len)",
            "            Write-Host ($rest + (' ' * $pad1)) -ForegroundColor Gray",
            "",
            "            # Line 2: the bar.",
            "            Write-Host -NoNewline '  '",
            "            Write-Host -NoNewline ([string]$Glyph.CapL) -ForegroundColor DarkGray",
            "            Write-Host -NoNewline $filled -ForegroundColor $barColor",
            "            Write-Host -NoNewline $empty -ForegroundColor DarkGray",
            "            Write-Host -NoNewline ([string]$Glyph.CapR) -ForegroundColor DarkGray",
            "            $line2Len = 2 + 1 + $BarWidth + 1",
            "            $pad2 = [Math]::Max(0, $w - 1 - $line2Len)",
            "            Write-Host (' ' * $pad2)",
            "        } catch {",
            "            $script:CanDraw = $false",
            "        }",
            "    }",
            "    if (-not $CanDraw) {",
            "        Write-Host ('  WBPP staging copy   {0}' -f $rest.Trim())",
            "    }",
            "}",
            "",
            "Write-Host '  Copying frames to WBPP staging' -ForegroundColor Cyan",
            "$sw = [System.Diagnostics.Stopwatch]::StartNew()",
            "$i = 0",
            "$CopiedBytes = [long]0",
            "$lastPct = -1",
            "$lastDrawMs = [long](-1000)",
            "foreach ($f in $Files) {",
            "    $i++",
            "    $TargetDir = Split-Path $f.Target -Parent",
            // R5 once more on Test-Path; New-Item keeps -Path for the reason given above.
            "    if (-not (Test-Path -LiteralPath $TargetDir)) { New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null }",
            // R5 on the copy itself. -Destination does not glob, so it needs no second parameter
            // and none is invented for it.
            "    Copy-Item -LiteralPath $f.Source -Destination $f.Target -Force",
            "    $CopiedBytes += $f.Size",
            "",
            "    $nowMs = $sw.ElapsedMilliseconds",
            "    $pctNow = [int][Math]::Floor((($CopiedBytes / [Math]::Max($TotalBytes, 1)) * 100))",
            "    if ($i -eq $Total -or $pctNow -ne $lastPct -or ($nowMs - $lastDrawMs) -ge 100) {",
            "        Show-CopyProgress -Index $i -Total $Total -Copied $CopiedBytes -TotalBytes $TotalBytes -ElapsedSec $sw.Elapsed.TotalSeconds",
            "        $lastPct = $pctNow",
            "        $lastDrawMs = $nowMs",
            "    }",
            "}",
            "$sw.Stop()",
            "Show-CopyProgress -Index $Total -Total $Total -Copied $TotalBytes -TotalBytes $TotalBytes -ElapsedSec $sw.Elapsed.TotalSeconds -Done",
            "if ($CanDraw) { try { [Console]::CursorVisible = $true } catch { } }",
            "",
            "Write-Host ''",
            "Write-Host ('  Done. Copied {0} file(s), {1} in {2}.' -f $Total, (Format-Bytes $TotalBytes), (Format-Duration $sw.Elapsed.TotalSeconds)) -ForegroundColor Green",
            "Write-Host ('  Open WBPP and use Add Directory on: {0}' -f $StagingRoot) -ForegroundColor Gray",
        ]);

        return string.Join("\n", lines);
    }

    // -----------------------------------------------------------------------------------------
    // The Bash flavour
    // -----------------------------------------------------------------------------------------

    private static string GenerateBash(WbppScriptInput input, string sessionDates)
    {
        var hasPerFileExcludes = HasPerFileExcludes(input);
        var exclusionArgs = input.Exclusions.Select(e => $"--exclude=\"{e}\"").ToList();

        // R3: every path this flavour emits goes through ToPosixPath, so a source and the staging
        // root can never be translated differently. The target name and the file name are not
        // paths and are not translated.
        var stagingRoot = ToPosixPath(input.StagingRoot);

        // The rsync operands take a "--" end-of-options marker and the per-file excludes use the
        // --exclude=<pattern> form, so a name beginning with a dash is part of one argument and can
        // never be read as an option. The Python has neither.
        var rsyncParts = new List<string> { "rsync", "-a", "--copy-links", "--info=progress2" };
        rsyncParts.AddRange(exclusionArgs);
        string helperComment;
        if (hasPerFileExcludes)
        {
            helperComment = "    # $1 = index, $2 = entry name, $3 = source dir (with trailing slash), $4+ = extra rsync args";
            rsyncParts.Add("\"${@:4}\"");
        }
        else
        {
            helperComment = "    # $1 = index, $2 = entry name, $3 = source dir (with trailing slash)";
        }

        rsyncParts.Add("--");
        rsyncParts.Add("\"$3\"");
        rsyncParts.Add("\"$dest/\"");
        var rsyncCommand = string.Join(" ", rsyncParts);

        var lines = new List<string>
        {
            "#!/usr/bin/env bash",
            "# WBPP Session Export",
            $"# Target: {CommentText(input.TargetName)}",
            $"# Sessions: {sessionDates}",
            "#",
            "# To run this script:",
            $"#   chmod +x {input.FileName} && ./{input.FileName}",
            // The one line R3 adds to the Python's header.
            "# Paths are written for a WSL mount, where the drive C: appears as /mnt/c. Edit the /mnt prefix if your own mount differs.",
            "#",
            "# When it finishes, open PixInsight WBPP and use 'Add Directory' on the",
            $"# staging root: {CommentText(stagingRoot)}",
            "",
            "set -euo pipefail",
            "",
            "# Colors only when writing to a terminal (skipped when piped/redirected).",
            "if [ -t 1 ]; then",
            @"    C_TITLE=$'\033[36m'; C_DIM=$'\033[90m'; C_OK=$'\033[32m'; C_RESET=$'\033[0m'",
            "else",
            "    C_TITLE=''; C_DIM=''; C_OK=''; C_RESET=''",
            "fi",
            "",
            $"STAGING_ROOT={BashQuote(stagingRoot, nameof(input.StagingRoot))}",
            @"mkdir -p -- ""$STAGING_ROOT""",
            "",
            $"TOTAL={input.Operations.Count.ToString(CultureInfo.InvariantCulture)}",
            @"printf '%s%s%s\n' ""$C_TITLE"" 'WBPP staging copy' ""$C_RESET""",
            // The target name is a quoted %s argument and never part of a format string, which is
            // the Python's own hardening.
            $@"printf '%s  Target: %s%s\n' ""$C_DIM"" {BashQuote(input.TargetName, nameof(input.TargetName))} ""$C_RESET""",
            @"printf '%s  %s session folder(s) -> %s%s\n' ""$C_DIM"" ""$TOTAL"" ""$STAGING_ROOT"" ""$C_RESET""",
            @"printf '\n'",
            "",
            "copy_folder() {",
            helperComment,
            @"    printf '%s[%s/%s] %s%s\n' ""$C_TITLE"" ""$1"" ""$TOTAL"" ""$2"" ""$C_RESET""",
            @"    local dest=""$STAGING_ROOT/$2""",
            @"    mkdir -p -- ""$dest""",
            "    " + rsyncCommand,
            @"    printf '\n'",
            "}",
            "",
        };

        var index = 1;
        foreach (var operation in input.Operations)
        {
            var call = "copy_folder " + index.ToString(CultureInfo.InvariantCulture)
                + " " + BashQuote(operation.EntryName, nameof(operation.EntryName))
                + " " + BashQuote(ToPosixPath(operation.SourcePath) + "/", nameof(operation.SourcePath));
            if (hasPerFileExcludes)
            {
                foreach (var excluded in operation.ExcludedRelativePaths)
                {
                    // The Bash side is a POSIX tool reading a POSIX pattern, so the port's native
                    // separators become slashes first; the leading slash anchors the pattern to
                    // this operation's own transfer root.
                    var pattern = "/" + ToPosixPath(excluded).TrimStart('/');
                    call += " --exclude=" + BashQuote(pattern, "excluded file");
                }
            }

            lines.Add(call);
            index++;
        }

        var total = input.Operations.Count.ToString(CultureInfo.InvariantCulture);
        lines.AddRange(
        [
            "",
            $@"printf '%s%s%s\n' ""$C_OK"" 'Done. Copied {total} folder(s).' ""$C_RESET""",
            @"printf '  Open WBPP and use Add Directory on: %s\n' ""$STAGING_ROOT""",
        ]);

        return string.Join("\n", lines);
    }

    // -----------------------------------------------------------------------------------------
    // Shared helpers
    // -----------------------------------------------------------------------------------------

    // The Python switches on bool(excluded_by_op). Its dict is never empty-valued on the real path,
    // because map_excluded_to_ops only creates a key when it appends, so "any operation has an
    // exclude" and "the dict is non-empty" agree on every input the pipeline produces.
    private static bool HasPerFileExcludes(WbppScriptInput input)
        => input.Operations.Any(o => o.ExcludedRelativePaths.Count > 0);

    // "|".join(re.escape(e).replace(r"\*", ".*") for e in exclusions): each pattern is escaped the
    // way Python escapes it and the escaped star is then restored as the glob the user meant.
    private static string EscapedExclusionGroup(IReadOnlyList<string> exclusions)
        => string.Join("|", exclusions.Select(e => PythonRegexEscape(e).Replace("\\*", ".*")));

    private static string PythonRegexEscape(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (PythonRegexSpecials.Contains(c))
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    [GeneratedRegex("[^A-Za-z0-9._-]+")]
    private static partial Regex UnsafeNameRun();
}
