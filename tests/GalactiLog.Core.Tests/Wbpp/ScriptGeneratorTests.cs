using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using GalactiLog.Core.Wbpp;
using Xunit;

namespace GalactiLog.Core.Tests.Wbpp;

/// <summary>
/// Spec 12.13's script contract. The parity cases compare both generators with the real web
/// generators' own output, byte for byte, from the sixteen golden files under
/// <c>tests/Fixtures/golden/</c> that
/// <c>docs/superpowers/work/phase16/oracle/scripts_oracle.py</c> produced by running
/// <c>wbpp_export.py</c> itself; not one expected byte is typed here. The departed golden is
/// compared with the Python's own file in the other direction too: undoing the declared
/// departures must recover the Python's text exactly, so a third departure that nobody ruled
/// turns these cases red.
/// </summary>
public class ScriptGeneratorTests
{
    private const string MountLine =
        "# Paths are written for a WSL mount, where the drive C: appears as /mnt/c. Edit the /mnt prefix if your own mount differs.";

    private static readonly string[] Nine =
    [
        "WBPP", "PixInsight", "finals", "WORK_AREA",
        "masters", "Masters", "MASTERS", "*CALIBRATED", "CALIBRATED",
    ];

    // -----------------------------------------------------------------------------------------
    // 8.1 Golden files, both shells
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("one-op", WbppScriptType.PowerShell)]
    [InlineData("one-op", WbppScriptType.Bash)]
    [InlineData("two-ops-excludes", WbppScriptType.PowerShell)]
    [InlineData("two-ops-excludes", WbppScriptType.Bash)]
    [InlineData("no-exclusions", WbppScriptType.PowerShell)]
    [InlineData("no-exclusions", WbppScriptType.Bash)]
    [InlineData("awkward", WbppScriptType.PowerShell)]
    [InlineData("awkward", WbppScriptType.Bash)]
    public void Generate_MatchesTheGoldenByteForByte(string name, WbppScriptType type)
    {
        // A failure looks like a plausible rewrite of the progress block that no reader would
        // question, which changes the script the user runs from the one the web has shipped.
        var expected = ReadGolden(name + Extension(type));
        var actual = ScriptGenerator.Generate(type, InputFor(name, type));

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("one-op", WbppScriptType.PowerShell)]
    [InlineData("one-op", WbppScriptType.Bash)]
    [InlineData("two-ops-excludes", WbppScriptType.PowerShell)]
    [InlineData("two-ops-excludes", WbppScriptType.Bash)]
    [InlineData("no-exclusions", WbppScriptType.PowerShell)]
    [InlineData("no-exclusions", WbppScriptType.Bash)]
    [InlineData("awkward", WbppScriptType.PowerShell)]
    [InlineData("awkward", WbppScriptType.Bash)]
    public void Generate_DiffersFromThePythonOnlyWhereADepartureWasRuled(string name, WbppScriptType type)
    {
        // The other half of W15. Undoing exactly the ruled departures, and nothing else, has to
        // recover the Python's own bytes. A fourth difference nobody ruled leaves a residue and
        // turns this red.
        var python = ReadGolden(name + ".python" + Extension(type));
        var ported = ScriptGenerator.Generate(type, InputFor(name, type));

        var undeparted = type == WbppScriptType.Bash
            ? UndoBashDepartures(ported, InputFor(name, type))
            : UndoPowerShellDepartures(ported);

        Assert.Equal(python, undeparted);
    }

    // -----------------------------------------------------------------------------------------
    // 8.1a The ruled departures
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void R2_ExclusionsMatchComponentsOfTheJobRelativePath()
    {
        // A failure looks like the Python's absolute form surviving, which hands a user whose
        // library sits under a folder named "masters" a script that copies nothing and reports
        // success.
        var input = new WbppScriptInput(
            [new CopyOperation(new DateOnly(2026, 7, 1), @"D:\masters\Astro\M31\2026-07-01", "2026-07-01", [])],
            @"D:\Staging\M 31",
            "M 31",
            Nine,
            "wbpp_M_31.ps1");

        var text = ScriptGenerator.Generate(WbppScriptType.PowerShell, input);

        Assert.Contains(
            @"-not ($_.FullName.Substring($Job.Src.Length).TrimStart('\', '/').Split([char[]]@('\', '/'))",
            text,
            StringComparison.Ordinal);
        Assert.DoesNotContain(@"-not ($_.FullName.Split(", text, StringComparison.Ordinal);
    }

    [Fact]
    public void R2_ThePredicateIsSelfContainedWhenNoPerFileExcludesAssignRelPath()
    {
        // The Python assigns $RelPath only in the per-file-exclude branch, so a predicate that
        // referenced it would emit a variable nothing assigned in the folder-only goldens.
        var text = ScriptGenerator.Generate(
            WbppScriptType.PowerShell, InputFor("one-op", WbppScriptType.PowerShell));

        var predicate = LineContaining(text, "-match");
        Assert.DoesNotContain("$RelPath", predicate, StringComparison.Ordinal);
        Assert.Contains("$Job.Src.Length", predicate, StringComparison.Ordinal);
    }

    [Fact]
    public void R3_EveryPathTheBashFlavourEmitsIsPosix()
    {
        // A failure looks like the mixed C:\a\b/c form the Python emits, which no rsync accepts,
        // so the script fails for the one class of user the Bash flavour exists for.
        var drive = ScriptGenerator.Generate(WbppScriptType.Bash, InputFor("two-ops-excludes", WbppScriptType.Bash));
        var unc = ScriptGenerator.Generate(WbppScriptType.Bash, InputFor("awkward", WbppScriptType.Bash));

        Assert.Contains("STAGING_ROOT='/mnt/d/Staging/M 31'", drive, StringComparison.Ordinal);
        Assert.Contains("'/mnt/d/Astro/M31/2026-07-01/'", drive, StringComparison.Ordinal);
        Assert.Contains("--exclude='/Ha/light_bad.fits'", drive, StringComparison.Ordinal);
        Assert.Contains(@"'//nas/astro/o'\''brien $HOME/Angle_71.61/'", unc, StringComparison.Ordinal);
        Assert.Contains(MountLine, drive, StringComparison.Ordinal);

        // No backslash survives on a path-carrying line. The Bash quoting idiom '\'' is itself a
        // backslash and is the one carve-out, so it is removed before the line is inspected.
        foreach (var text in new[] { drive, unc })
        {
            foreach (var line in Lines(text))
            {
                if (!line.StartsWith("STAGING_ROOT=", StringComparison.Ordinal)
                    && !line.StartsWith("copy_folder ", StringComparison.Ordinal)
                    && !line.StartsWith("# staging root: ", StringComparison.Ordinal))
                {
                    continue;
                }

                Assert.DoesNotContain(@"\", line.Replace(@"'\''", string.Empty), StringComparison.Ordinal);
            }
        }
    }

    [Theory]
    [InlineData(@"C:\Astro\M 31", "/mnt/c/Astro/M 31")]
    [InlineData(@"c:\Astro\M 31", "/mnt/c/Astro/M 31")]
    [InlineData(@"D:\", "/mnt/d/")]
    [InlineData(@"D:\Astro\", "/mnt/d/Astro/")]
    [InlineData(@"\\server\share\a", "//server/share/a")]
    [InlineData("/already/posix", "/already/posix")]
    [InlineData("C:/Astro", "/mnt/c/Astro")]
    [InlineData(@"Ha\bad.fits", "Ha/bad.fits")]
    [InlineData("", "")]
    public void ToPosixPath_TranslatesEveryShape(string input, string expected)
        => Assert.Equal(expected, ScriptGenerator.ToPosixPath(input));

    [Fact]
    public void R5_EveryPowerShellDiskSiteThatHasLiteralPathUsesIt()
    {
        // A folder genuinely named "M31 [HaOIII]" makes -Path match nothing, so the job copies
        // nothing, raises no error, and the footer reports a successful copy of zero files.
        // New-Item is the one site left on -Path: PowerShell gives it no -LiteralPath parameter at
        // all, and its -Path is a path to create rather than a pattern to resolve.
        var input = new WbppScriptInput(
            [new CopyOperation(new DateOnly(2026, 7, 1), @"D:\Astro\M31 [HaOIII]\2026-07-01", "2026-07-01", [])],
            @"D:\Staging\M31 [HaOIII]",
            "M31 [HaOIII]",
            Nine,
            "wbpp_M31_HaOIII.ps1");

        var text = ScriptGenerator.Generate(WbppScriptType.PowerShell, input);

        Assert.Contains("Test-Path -LiteralPath $StagingRoot", text, StringComparison.Ordinal);
        Assert.Contains("Test-Path -LiteralPath $TargetDir", text, StringComparison.Ordinal);
        Assert.Contains("Get-ChildItem -LiteralPath $Job.Src -Recurse -File", text, StringComparison.Ordinal);
        Assert.Contains("Copy-Item -LiteralPath $f.Source -Destination $f.Target -Force", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Test-Path $", text, StringComparison.Ordinal);
        Assert.DoesNotContain("-Path $Job.Src", text, StringComparison.Ordinal);
        Assert.DoesNotContain("-Path $f.Source", text, StringComparison.Ordinal);
        Assert.Contains(@"$StagingRoot = 'D:\Staging\M31 [HaOIII]'", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BashOperandsCannotBeReadAsOptions()
    {
        // A per-file exclude's relative path and an entry name are not absolute, so either could
        // begin with a dash. The --exclude=<pattern> form makes the pattern part of one argument
        // and the rsync operands sit after the end-of-options marker.
        var input = new WbppScriptInput(
            [new CopyOperation(new DateOnly(2026, 7, 1), @"D:\Astro\M31\2026-07-01", "2026-07-01", [@"-rf\bad.fits"])],
            @"D:\Staging\M 31",
            "M 31",
            Nine,
            "wbpp_M_31.sh");

        var text = ScriptGenerator.Generate(WbppScriptType.Bash, input);

        Assert.Contains("--exclude='/-rf/bad.fits'", text, StringComparison.Ordinal);
        Assert.Contains(@"""${@:4}"" -- ""$3"" ""$dest/""", text, StringComparison.Ordinal);
        Assert.Contains(@"mkdir -p -- ""$STAGING_ROOT""", text, StringComparison.Ordinal);
        Assert.Contains(@"mkdir -p -- ""$dest""", text, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------------------------
    // 8.2 Byte identity when there are no per-file excludes
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void NoPerFileExcludes_EmitsNoExcludeMachineryAtAll()
    {
        // A failure looks like an always-emitted empty ExcludeFiles = @(), which is harmless and
        // which makes the port and the web produce different text for the commonest case of all.
        var powerShell = ScriptGenerator.Generate(WbppScriptType.PowerShell, InputFor("one-op", WbppScriptType.PowerShell));
        var bash = ScriptGenerator.Generate(WbppScriptType.Bash, InputFor("one-op", WbppScriptType.Bash));

        Assert.Equal(ReadGolden("one-op.ps1"), powerShell);
        Assert.Equal(ReadGolden("one-op.sh"), bash);
        Assert.DoesNotContain("ExcludeFiles", powerShell, StringComparison.Ordinal);
        Assert.DoesNotContain("${@:4}", bash, StringComparison.Ordinal);
        Assert.DoesNotContain("--exclude='/", bash, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------------------------
    // 8.3 and 8.4 The exclusion group
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void ExclusionRegex_EscapesTheWayPythonEscapes()
    {
        // A failure looks like Regex.Escape being used, which yields WORK-AREA and a script that
        // behaves identically and compares unequal, so parity fails for a reason that looks like a
        // bug in the test.
        var input = InputWithExclusions(["WORK-AREA", "finals", "*CALIBRATED"]);

        var text = ScriptGenerator.Generate(WbppScriptType.PowerShell, input);

        Assert.Contains(@"""^(WORK\-AREA|finals|.*CALIBRATED)$""", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Exclusions_MatchComponentsAndAreAnchored()
    {
        // A failure looks like a substring match, which excludes a folder named "semifinals" and
        // silently drops half a library from the copy.
        var text = ScriptGenerator.Generate(WbppScriptType.PowerShell, InputWithExclusions(["finals"]));

        var predicate = LineContaining(text, "-match");
        Assert.Contains(@"Split([char[]]@('\', '/'))", predicate, StringComparison.Ordinal);
        Assert.Contains(@"""^(finals)$""", predicate, StringComparison.Ordinal);
        Assert.DoesNotContain("-notmatch", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyExclusions_EmitTheLiteralTruePredicate()
    {
        var text = ScriptGenerator.Generate(WbppScriptType.PowerShell, InputFor("no-exclusions", WbppScriptType.PowerShell));

        Assert.Contains("\n        $true\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("-match", text, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------------------------
    // 8.5 The run instructions
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void RunInstructions_CarryTheUnblockStepAndTheFileName()
    {
        // A failure looks like the unblock step being dropped, after which the script the user
        // just saved refuses to run and the export looks broken.
        var powerShell = ScriptGenerator.Generate(WbppScriptType.PowerShell, InputFor("awkward", WbppScriptType.PowerShell));
        var bash = ScriptGenerator.Generate(WbppScriptType.Bash, InputFor("awkward", WbppScriptType.Bash));

        Assert.Contains("ExecutionPolicy Bypass", powerShell, StringComparison.Ordinal);
        Assert.Contains("Unblock-File", powerShell, StringComparison.Ordinal);
        Assert.Contains("wbpp_M_81_-_Bode_s_Galaxy.ps1", powerShell, StringComparison.Ordinal);
        Assert.Contains(
            "chmod +x wbpp_M_81_-_Bode_s_Galaxy.sh && ./wbpp_M_81_-_Bode_s_Galaxy.sh",
            bash,
            StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------------------------
    // P2-c: the file name is the one raw value that lands in text a user pastes into a shell
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("a$(Write-Host PWNED).ps1")]
    [InlineData("a`whoami`.ps1")]
    [InlineData("wbpp_o'brien.ps1")]
    [InlineData("wbpp\"x\".ps1")]
    [InlineData("wbpp;whoami.ps1")]
    [InlineData("wbpp_\u2019.ps1")]
    [InlineData("wbpp\\..\\x.ps1")]
    [InlineData("-wbpp.ps1")]
    [InlineData(".wbpp.ps1")]
    [InlineData("wbpp_M_31.txt")]
    [InlineData("wbpp_M_31")]
    [InlineData(".ps1")]
    [InlineData("")]
    public void Generate_RefusesAFileNameOutsideTheAllowlist(string fileName)
    {
        // A failure looks like the run instruction in the header reading
        // powershell ... "Unblock-File -LiteralPath '.\a$(Write-Host PWNED).ps1'; ...", where the
        // single quotes have no quoting power because they sit inside a double-quoted -Command
        // string, so the subexpression runs the moment the user pastes the line the file told them
        // to paste. An allowlist rather than a refusal list, because a refusal list is only ever as
        // complete as its last review.
        var input = new WbppScriptInput(
            [new CopyOperation(new DateOnly(2026, 7, 1), @"D:\Astro\M31\2026-07-01", "2026-07-01", [])],
            @"D:\Staging\M 31",
            "M 31",
            Nine,
            fileName);

        var thrown = Assert.Throws<WbppUnsafeValueException>(() => ScriptGenerator.Generate(WbppScriptType.PowerShell, input));
        Assert.Equal("FileName", thrown.ValueName);
    }

    [Theory]
    [InlineData("wbpp_M_31.ps1")]
    [InlineData("wbpp_M_81_-_Bode_s_Galaxy.ps1")]
    [InlineData("wbpp_M_31 (1).ps1")]
    public void Generate_AcceptsAFileNameInsideTheAllowlist(string fileName)
    {
        var input = new WbppScriptInput(
            [new CopyOperation(new DateOnly(2026, 7, 1), @"D:\Astro\M31\2026-07-01", "2026-07-01", [])],
            @"D:\Staging\M 31",
            "M 31",
            Nine,
            fileName);

        var text = ScriptGenerator.Generate(WbppScriptType.PowerShell, input);

        // The run instruction interpolates the file name and nothing else at all.
        Assert.Contains(
            $@"#     powershell -ExecutionPolicy Bypass -Command ""Unblock-File -LiteralPath '.\{fileName}'; & '.\{fileName}'""",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_RefusesAFileNameWithTheWrongFlavoursExtension()
    {
        var input = InputFor("one-op", WbppScriptType.PowerShell);

        Assert.Throws<WbppUnsafeValueException>(() => ScriptGenerator.Generate(WbppScriptType.Bash, input));
        Assert.Throws<WbppUnsafeValueException>(
            () => ScriptGenerator.Generate(WbppScriptType.PowerShell, InputFor("one-op", WbppScriptType.Bash)));
    }

    [Theory]
    [InlineData("NGC 7000: North America Nebula")]
    [InlineData("M 81 - Bode's Galaxy")]
    [InlineData("M27 / \"Dumbbell\" *test*?")]
    [InlineData("::: ???")]
    [InlineData("a$(Write-Host PWNED)")]
    [InlineData("..\\..\\escape")]
    [InlineData("Barnard\u2019s Loop")]
    [InlineData("\U0001F52D \u4e2d \u00e9")]
    [InlineData("-leading dash")]
    [InlineData("...")]
    [InlineData("")]
    [InlineData("   ")]
    public void FileNameFor_AlwaysProducesANameInsideTheAllowlist(string targetName)
    {
        // The property the allowlist rests on: the one caller the page has cannot produce a name
        // Generate refuses, whatever the user called their target.
        foreach (var type in BothShells)
        {
            var fileName = ScriptGenerator.FileNameFor(type, targetName);
            var input = new WbppScriptInput(
                [new CopyOperation(new DateOnly(2026, 7, 1), @"D:\Astro\M31\2026-07-01", "2026-07-01", [])],
                @"D:\Staging\M 31",
                targetName,
                Nine,
                fileName);

            var text = ScriptGenerator.Generate(type, input);
            Assert.Contains(fileName, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CommentLines_CarryNoCharacterOutsidePrintableAscii()
    {
        // A comment line is inert to the parser and is not inert to the reader: it is what the user
        // reads to decide whether to run the file.
        var input = new WbppScriptInput(
            [new CopyOperation(new DateOnly(2026, 7, 1), @"D:\Astro\M31\2026-07-01", "2026-07-01", [])],
            "D:\\Staging\\M 31 \u202e",
            "Barnard\u2019s Loop \u202e\U0001F52D",
            Nine,
            "wbpp_x.ps1");

        var powerShell = ScriptGenerator.Generate(WbppScriptType.PowerShell, input);
        Assert.Contains("# Target: Barnard?s Loop ???", powerShell, StringComparison.Ordinal);
        Assert.Contains(@"# staging root: D:\Staging\M 31 ?", powerShell, StringComparison.Ordinal);

        var bash = ScriptGenerator.Generate(WbppScriptType.Bash, input with { FileName = "wbpp_x.sh" });
        Assert.Contains("# Target: Barnard?s Loop ???", bash, StringComparison.Ordinal);
        Assert.Contains("# staging root: /mnt/d/Staging/M 31 ?", bash, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------------------------
    // P3: the entry name is the other half of every destination
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(@"..\escaped")]
    [InlineData("../escaped")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData(@"C:\elsewhere")]
    [InlineData("sub/folder")]
    [InlineData(@"sub\folder")]
    [InlineData("")]
    public void Generate_RefusesAnEntryNameThatCouldLeaveTheStagingRoot(string entryName)
    {
        // Measured by the reviewer on disk: an entry name of "..\escaped" made Join-Path produce
        // a path outside the staging root and Copy-Item land the file there, and made
        // mkdir -p -- "$STAGING_ROOT/../escaped-sh" create a folder beside it. Neither flavour
        // resolves the name, so the guarantee needs the name as well as the root.
        var input = new WbppScriptInput(
            [new CopyOperation(new DateOnly(2026, 7, 1), @"D:\Astro\M31\2026-07-01", entryName, [])],
            @"D:\Staging\M 31",
            "M 31",
            Nine,
            "wbpp_M_31.ps1");

        foreach (var type in BothShells)
        {
            var thrown = Assert.Throws<WbppUnsafeValueException>(
                () => ScriptGenerator.Generate(type, ForShell(input, type)));
            Assert.Equal("EntryName", thrown.ValueName);
        }
    }

    [Theory]
    [InlineData("TargetName")]
    [InlineData("StagingRoot")]
    [InlineData("FileName")]
    [InlineData("EntryName")]
    public void Generate_RefusesALineBreakInEveryRawInterpolatedValue(string valueName)
    {
        // One row per guarded value, because only TargetName had a case before. What each row pins
        // is that the value is refused at all and that the page is handed that value's own name;
        // it deliberately does not pin which guard fired, because two of the four are guarded
        // twice. Measured, by deleting one guard at a time and running this suite: removing
        // Generate's own RefuseLineBreaks call for StagingRoot or for an EntryName leaves every
        // case green, because both values also pass through a quoter, which refuses them with the
        // same name; those two calls are deliberate redundancy at Generate's choke point and the
        // rows stay red only when the refusal itself is gone. TargetName and FileName have no
        // second guard: TargetName reaches a comment line raw and FileName reaches the run
        // instruction raw, and removing either of their guards turns its own row red on its own.
        const string breaking = "x\nRemove-Item -Recurse -Force";
        var input = new WbppScriptInput(
            [new CopyOperation(new DateOnly(2026, 7, 1), @"D:\Astro\M31\2026-07-01", valueName == "EntryName" ? breaking : "2026-07-01", [])],
            valueName == "StagingRoot" ? @"D:\Staging\" + breaking : @"D:\Staging\M 31",
            valueName == "TargetName" ? breaking : "M 31",
            Nine,
            valueName == "FileName" ? "wbpp_\n.ps1" : "wbpp_M_31.ps1");

        var thrown = Assert.Throws<WbppUnsafeValueException>(() => ScriptGenerator.Generate(WbppScriptType.PowerShell, input));
        Assert.Equal(valueName, thrown.ValueName);
    }

    // -----------------------------------------------------------------------------------------
    // 8.6 Quoting, the mandatory list (W8)
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Quoters_EscapeAnApostropheExactlyAsThePythonDoes()
    {
        // The two forms the seam review verified against the Python itself. A round trip alone
        // cannot see a quoter that stopped escaping, because undoing an escape that was never
        // applied is a no-op, so the escaped bytes are asserted literally.
        Assert.Equal(@"'D:\o''brien\x'", ScriptGenerator.PowerShellQuote(@"D:\o'brien\x"));
        Assert.Equal(@"'/mnt/o'\''brien/x'", ScriptGenerator.BashQuote("/mnt/o'brien/x"));
        Assert.Equal("''''", ScriptGenerator.PowerShellQuote("'"));
        Assert.Equal(@"''\'''", ScriptGenerator.BashQuote("'"));
    }

    [Theory]
    [InlineData("o'brien")]
    [InlineData("$(id)")]
    [InlineData("$HOME")]
    [InlineData("back`tick")]
    [InlineData("say \"hi\"")]
    [InlineData("two words")]
    [InlineData("semi;colon")]
    [InlineData("amp&ersand")]
    [InlineData("paren(s)")]
    [InlineData("M31 [HaOIII]")]
    [InlineData("star*quest?")]
    [InlineData("-dash")]
    [InlineData("~tilde")]
    [InlineData("Messier \u03b1 \u00e9toile")]
    [InlineData("trailing dot.")]
    [InlineData("trailing space ")]
    [InlineData("$HOME [raw]")]
    public void Quoters_AreLiteralAndReversibleForEveryShape(string value)
    {
        // The machine-checkable half of "a single-quoted literal cannot be escaped by any of
        // these": the quoted form is the value and nothing but the value, so undoing the one
        // escape each shell defines returns it exactly. Anything a metacharacter could have done
        // would show up as a value that no longer round trips. It is redundant beside
        // Quoters_EscapeAnApostropheExactlyAsThePythonDoes, which asserts the bytes, because a
        // round trip cannot see a quoter that stopped escaping; it is kept for the breadth of the
        // character list rather than for the assertion's strength.
        var powerShell = ScriptGenerator.PowerShellQuote(value);
        Assert.True(IsPrintableAscii(powerShell), $"quoted form is not printable ASCII: {powerShell}");
        Assert.Equal(value, EvaluatePowerShellExpression(powerShell));

        var bash = ScriptGenerator.BashQuote(value);
        Assert.StartsWith("'", bash, StringComparison.Ordinal);
        Assert.EndsWith("'", bash, StringComparison.Ordinal);
        Assert.Equal(value, bash[1..^1].Replace(@"'\''", "'"));
    }

    [Theory]
    [InlineData("o'brien")]
    [InlineData("$(id)")]
    [InlineData("$HOME")]
    [InlineData("back`tick")]
    [InlineData("say \"hi\"")]
    [InlineData("two words")]
    [InlineData("semi;colon")]
    [InlineData("amp&ersand")]
    [InlineData("paren(s)")]
    [InlineData("M31 [HaOIII]")]
    [InlineData("star*quest?")]
    [InlineData("-dash")]
    [InlineData("~tilde")]
    [InlineData("Messier \u03b1 \u00e9toile")]
    [InlineData("trailing dot.")]
    [InlineData("trailing space ")]
    [InlineData("$HOME [raw]")]
    public void Generate_QuotesEveryValueOnAllThreeRoutes(string fragment)
    {
        // The three routes into the text: the staging root, an operation's source path and the
        // target name. Each is asserted separately because each reaches the script differently.
        var source = @"D:\Astro\" + fragment + @"\2026-07-01";
        var staging = @"D:\Staging\" + fragment;
        var input = new WbppScriptInput(
            [new CopyOperation(new DateOnly(2026, 7, 1), source, "2026-07-01", [])],
            staging,
            fragment,
            Nine,
            "wbpp_x.ps1");

        var powerShell = ScriptGenerator.Generate(WbppScriptType.PowerShell, input);
        Assert.Contains("$StagingRoot = " + ScriptGenerator.PowerShellQuote(staging), powerShell, StringComparison.Ordinal);
        Assert.Contains("Src = " + ScriptGenerator.PowerShellQuote(source) + ";", powerShell, StringComparison.Ordinal);
        // The PowerShell flavour never composes a bare path into a command; every disk site reads
        // a variable, and both quoted literals carry an even number of apostrophes.
        Assert.Equal(0, CountOf(powerShell, "$StagingRoot = " + staging));
        Assert.Equal(0, LineContaining(powerShell, "$StagingRoot = ").Count(c => c == '\'') % 2);
        Assert.Equal(0, LineContaining(powerShell, "Src = ").Count(c => c == '\'') % 2);

        var bash = ScriptGenerator.Generate(WbppScriptType.Bash, ForShell(input, WbppScriptType.Bash));
        Assert.Contains(
            "STAGING_ROOT=" + ScriptGenerator.BashQuote(ScriptGenerator.ToPosixPath(staging)),
            bash,
            StringComparison.Ordinal);
        Assert.Contains(
            ScriptGenerator.BashQuote(ScriptGenerator.ToPosixPath(source) + "/"),
            bash,
            StringComparison.Ordinal);
        // The target name is a quoted printf argument and never part of a format string.
        Assert.Contains(
            @"printf '%s  Target: %s%s\n' ""$C_DIM"" " + ScriptGenerator.BashQuote(fragment),
            bash,
            StringComparison.Ordinal);
        Assert.DoesNotContain("STAGING_ROOT=\"", bash, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------------------------
    // P1: a PowerShell single-quoted literal can be closed by four Unicode characters
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData('\u2018')]
    [InlineData('\u2019')]
    [InlineData('\u201a')]
    [InlineData('\u201b')]
    [InlineData('\u201c')]
    [InlineData('\u201d')]
    [InlineData('\u201e')]
    [InlineData('\u201f')]
    [InlineData('\u00e9')]
    [InlineData('\u03b1')]
    [InlineData('\u4e2d')]
    [InlineData('\u202e')]
    [InlineData('\u0000')]
    [InlineData('\t')]
    public void PowerShellQuote_SplicesEveryNonPrintableCharacterOutOfTheLiteral(char c)
    {
        // A failure looks like a folder named Barnard's Loop with a right single quotation mark,
        // which is what copying a catalogue name out of a browser gives you, closing the literal so
        // that the rest of the path is parsed as script. Measured on Windows PowerShell 5.1:
        // U+2018, U+2019, U+201A and U+201B end a single-quoted string and U+201C, U+201D and
        // U+201E end a double-quoted one. Doubling is not a fix, because it yields an ASCII
        // apostrophe and therefore a silently different path.
        var value = @"D:\Astro\Barnard" + c + "s Loop";

        var quoted = ScriptGenerator.PowerShellQuote(value);

        Assert.True(IsPrintableAscii(quoted), $"quoted form is not printable ASCII: {quoted}");
        Assert.DoesNotContain(c.ToString(), quoted, StringComparison.Ordinal);
        Assert.Contains("[char]0x" + ((int)c).ToString("X4"), quoted, StringComparison.Ordinal);
        Assert.Equal(value, EvaluatePowerShellExpression(quoted));
    }

    [Fact]
    public void PowerShellQuote_SplicesACharacterAboveTheBasicPlaneAsOneCodePoint()
    {
        var value = "M31 \U0001F52D observing";

        var quoted = ScriptGenerator.PowerShellQuote(value);

        Assert.True(IsPrintableAscii(quoted));
        Assert.Contains("[char]::ConvertFromUtf32(0x1F52D)", quoted, StringComparison.Ordinal);
        Assert.Equal(value, EvaluatePowerShellExpression(quoted));
    }

    [Fact]
    public void PowerShellQuote_EvaluatesToAStringWhereverTheSpliceFalls()
    {
        // The splice puts a [char] term at the front, at the back or on its own depending on where
        // the non-ASCII character sits, and PowerShell's + is typed by its left operand. Measured
        // on Windows PowerShell 5.1: [char] + string and [char] + [char] are both String, and a
        // lone [char] term is a Char, which is why the lone case carries an empty literal in front.
        // A failure looks like $StagingRoot holding a Char, which compares unequal to the path it
        // came from wherever the script compares rather than coerces.
        Assert.Equal(@"([char]0x00E9 + 'toile')", ScriptGenerator.PowerShellQuote("étoile"));
        Assert.Equal("('Messier ' + [char]0x03B1)", ScriptGenerator.PowerShellQuote("Messier α"));
        Assert.Equal("('' + [char]0x00E9)", ScriptGenerator.PowerShellQuote("é"));
        Assert.Equal("([char]0x2019 + [char]0x2018)", ScriptGenerator.PowerShellQuote("’‘"));

        foreach (var value in new[] { "étoile", "Messier α", "é", "’‘" })
        {
            Assert.Equal(value, EvaluatePowerShellExpression(ScriptGenerator.PowerShellQuote(value)));
        }
    }

    [Fact]
    public void EveryRefusalCarriesBothTheKindOfInputAndTheInputItself()
    {
        // Spec 12.13's script contract says the page "names the file it refused", which needs the
        // value and not only the kind. One case over all five throw sites, so a site added later
        // that passes the wrong argument is visible here.
        const string lineBreak = "x\ny";
        var ok = new CopyOperation(new DateOnly(2026, 7, 1), @"D:\Astro\M31\2026-07-01", "2026-07-01", []);

        Assert.Equal(
            (nameof(WbppScriptInput.StagingRoot), lineBreak),
            NameAndValue(() => ScriptGenerator.PowerShellQuote(lineBreak, nameof(WbppScriptInput.StagingRoot))));
        Assert.Equal(
            (nameof(WbppScriptInput.StagingRoot), lineBreak),
            NameAndValue(() => ScriptGenerator.BashQuote(lineBreak, nameof(WbppScriptInput.StagingRoot))));

        var lone = char.ConvertFromUtf32(0x1F52D)[0].ToString();
        Assert.Equal(("SourcePath", lone), NameAndValue(() => ScriptGenerator.PowerShellQuote(lone, "SourcePath")));

        Assert.Equal(
            ("FileName", "wbpp_o'brien.ps1"),
            NameAndValue(() => ScriptGenerator.Generate(
                WbppScriptType.PowerShell,
                new WbppScriptInput([ok], @"D:\Staging\M 31", "M 31", Nine, "wbpp_o'brien.ps1"))));

        Assert.Equal(
            ("EntryName", @"..\escaped"),
            NameAndValue(() => ScriptGenerator.Generate(
                WbppScriptType.PowerShell,
                new WbppScriptInput(
                    [ok with { EntryName = @"..\escaped" }], @"D:\Staging\M 31", "M 31", Nine, "wbpp_M_31.ps1"))));

        Assert.Equal(
            ("exclusion pattern", @"masters\"),
            NameAndValue(() => ScriptGenerator.Generate(
                WbppScriptType.PowerShell,
                new WbppScriptInput([ok], @"D:\Staging\M 31", "M 31", [@"masters\"], "wbpp_M_31.ps1"))));

        Assert.Equal(
            ("TargetName", lineBreak),
            NameAndValue(() => ScriptGenerator.Generate(
                WbppScriptType.PowerShell,
                new WbppScriptInput([ok], @"D:\Staging\M 31", lineBreak, Nine, "wbpp_M_31.ps1"))));
    }

    [Fact]
    public void ARefusedValueIsNeverPutIntoTheExceptionMessage()
    {
        // The message can reach a log; the value is a user path and belongs on the property only.
        var thrown = Assert.Throws<WbppUnsafeValueException>(
            () => ScriptGenerator.PowerShellQuote("D:\\Astro\\secret\nplace", "SourcePath"));

        Assert.DoesNotContain("secret", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("secret", thrown.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void PowerShellQuote_RefusesALoneSurrogate()
    {
        // Built here rather than through InlineData, because xUnit's theory-data serialisation
        // replaces a lone surrogate and the case would then assert nothing.
        var high = char.ConvertFromUtf32(0x1F52D)[0];
        var low = char.ConvertFromUtf32(0x1F52D)[1];

        foreach (var value in new[] { high.ToString(), low.ToString(), "a" + high + "b", "a" + low + "b" })
        {
            var thrown = Assert.Throws<WbppUnsafeValueException>(
                () => ScriptGenerator.PowerShellQuote(value, "source path"));
            Assert.Equal("source path", thrown.ValueName);
        }
    }

    [Fact]
    public void PowerShellQuote_IsUnchangedForAPrintableAsciiValue()
    {
        // The whole of the shipped path today and every golden input, so the common form stays
        // byte for byte the Python's and no parenthesis appears where the web has none.
        Assert.Equal(@"'D:\Astro\M31\2026-07-01'", ScriptGenerator.PowerShellQuote(@"D:\Astro\M31\2026-07-01"));
        Assert.Equal(@"'D:\o''brien\x'", ScriptGenerator.PowerShellQuote(@"D:\o'brien\x"));
        Assert.Equal("''", ScriptGenerator.PowerShellQuote(string.Empty));
    }

    [Theory]
    [InlineData('\u2018')]
    [InlineData('\u2019')]
    [InlineData('\u201a')]
    [InlineData('\u201b')]
    [InlineData('\u201c')]
    [InlineData('\u201d')]
    [InlineData('\u201e')]
    [InlineData('\u201f')]
    public void BashQuote_GivesNoUnicodeCharacterAnyQuotingPower(char c)
    {
        // Bash gives no character but U+0027 any meaning inside a single-quoted string, measured
        // under bash as well as reasoned, so the Bash side needs no splice and the character is
        // carried through verbatim.
        var value = "/mnt/d/Astro/Barnard" + c + "s Loop";

        var quoted = ScriptGenerator.BashQuote(value);

        Assert.Equal("'" + value + "'", quoted);
        Assert.Equal(2, quoted.Count(x => x == '\''));
    }

    [Fact]
    public void PowerShellScript_IsPurePrintableAsciiEvenForANonAsciiName()
    {
        // This case replaces one that asserted the opposite and so pinned the P1 defect in place.
        var name = "Messier \u03b1 \u00e9toile \u2019 \U0001F52D \u4e2d";
        var input = new WbppScriptInput(
            [new CopyOperation(new DateOnly(2026, 7, 1), @"D:\Astro\" + name, "2026-07-01", [])],
            @"D:\Staging\" + name,
            name,
            Nine,
            "wbpp_x.ps1");

        var text = ScriptGenerator.Generate(WbppScriptType.PowerShell, input);

        Assert.True(IsPrintableAscii(text.Replace("\n", string.Empty)), "the generated script is not pure printable ASCII");
        Assert.Contains("[char]0x2588", text, StringComparison.Ordinal);
        Assert.Contains("[char]0x2019", text, StringComparison.Ordinal);
        Assert.Contains("[char]::ConvertFromUtf32(0x1F52D)", text, StringComparison.Ordinal);
        // The comment lines carry the same name with every unprintable character replaced, so the
        // line the user reads before running the file cannot hide or reorder itself.
        Assert.Contains("# Target: Messier ? ?toile ? ?? ?", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BothGenerators_EmitLineFeedOnlyAndNoTrailingNewline()
    {
        foreach (var name in GoldenNames)
        {
            foreach (var type in BothShells)
            {
                var text = ScriptGenerator.Generate(type, InputFor(name, type));
                Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
                Assert.False(text.EndsWith('\n'), $"{name}.{type} ends with a newline");
            }
        }
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    public void Quoters_RefuseACarriageReturnOrALineFeed(string breaking)
    {
        var value = "M31" + breaking + "Remove-Item -Recurse -Force";

        var powerShell = Assert.Throws<WbppUnsafeValueException>(() => ScriptGenerator.PowerShellQuote(value, "source path"));
        Assert.Equal("source path", powerShell.ValueName);

        var bash = Assert.Throws<WbppUnsafeValueException>(() => ScriptGenerator.BashQuote(value, "source path"));
        Assert.Equal("source path", bash.ValueName);
    }

    [Theory]
    [InlineData(WbppScriptType.PowerShell)]
    [InlineData(WbppScriptType.Bash)]
    public void Generate_RefusesALineBreakInTheTargetName(WbppScriptType type)
    {
        // A failure looks like a target name of "M31", a newline and "Remove-Item -Recurse -Force
        // D:\Astro" producing a script that deletes the user's library. The target name never
        // touches a quoter in the PowerShell flavour, which is why Generate checks it as well.
        var input = new WbppScriptInput(
            [new CopyOperation(new DateOnly(2026, 7, 1), @"D:\Astro\M31\2026-07-01", "2026-07-01", [])],
            @"D:\Staging\M 31",
            "M31\nRemove-Item -Recurse -Force D:\\Astro",
            Nine,
            "wbpp_M31.ps1");

        var thrown = Assert.Throws<WbppUnsafeValueException>(
            () => ScriptGenerator.Generate(type, ForShell(input, type)));
        Assert.Equal("TargetName", thrown.ValueName);
    }

    [Theory]
    [InlineData('"')]
    [InlineData('$')]
    [InlineData('`')]
    [InlineData('\\')]
    [InlineData('\r')]
    [InlineData('\n')]
    [InlineData('‘')]
    [InlineData('’')]
    [InlineData('‚')]
    [InlineData('‛')]
    [InlineData('“')]
    [InlineData('”')]
    [InlineData('„')]
    [InlineData('‟')]
    public void IsRefusedPatternChar_HoldsTheWholeSetInOnePlace(char c)
    {
        Assert.True(ScriptGenerator.IsRefusedPatternChar(c));
    }

    [Theory]
    [InlineData('\'')]
    [InlineData('*')]
    [InlineData('-')]
    [InlineData('_')]
    [InlineData('/')]
    [InlineData('é')]
    [InlineData('†')]
    public void IsRefusedPatternChar_AdmitsWhatAPatternLegitimatelyCarries(char c)
    {
        Assert.False(ScriptGenerator.IsRefusedPatternChar(c));
    }

    [Theory]
    [InlineData("pat\"tern")]
    [InlineData("pat$tern")]
    [InlineData("pat`tern")]
    [InlineData("pat\ntern")]
    [InlineData(@"masters\")]
    [InlineData(@"fin\als")]
    [InlineData("ma”;whoami;“sters")]
    [InlineData("ma’sters")]
    public void Generate_RefusesAnUnsafeExclusionPattern(string pattern)
    {
        // Exclusion patterns reach a double-quoted PowerShell regex group and a double-quoted rsync
        // --exclude="..." unquoted in both flavours, so each of these breaks out of the literal it
        // lands in. Measured by the reviewer: a single pattern "masters\" makes bash -n report an
        // unterminated quote and the script does nothing at all, while an even number of them
        // parses and silently collapses two --exclude arguments into one that matches nothing, so
        // every masters, WBPP and CALIBRATED folder is copied. A pattern carrying U+201D closes the
        // PowerShell regex group and the payload between it and a U+201C really did execute.
        var input = InputWithExclusions([pattern]);

        var thrown = Assert.Throws<WbppUnsafeValueException>(
            () => ScriptGenerator.Generate(WbppScriptType.PowerShell, input));
        Assert.Equal("exclusion pattern", thrown.ValueName);
        Assert.Throws<WbppUnsafeValueException>(
            () => ScriptGenerator.Generate(WbppScriptType.Bash, ForShell(input, WbppScriptType.Bash)));
    }

    // -----------------------------------------------------------------------------------------
    // 8.7 The no-destructive-verb scan (W7)
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void NeitherGeneratorEmitsADestructiveVerb()
    {
        // Over both flavours of every golden input and of every quoting input. A failure looks
        // like a script that removes, moves or renames something in the user's library.
        foreach (var input in EveryScannedInput())
        {
            foreach (var type in BothShells)
            {
                var text = ScriptGenerator.Generate(type, ForShell(input, type));
                var verb = FirstDestructiveVerb(text);
                Assert.True(verb is null, $"{type} script for '{input.TargetName}' contains the destructive verb '{verb}'");
            }
        }
    }

    [Fact]
    public void TheVerbScanIsTokenAwareAndSeesAnInjectedAlias()
    {
        // The scan's own proof, kept permanently so the pattern can never quietly stop matching:
        // it finds the PowerShell alias "ri" in script text, and it does not mistake a folder the
        // user genuinely named "rm -rf del" for one, because a quoted literal is inert data.
        Assert.Equal("ri", FirstDestructiveVerb("foreach ($f in $Files) { ri $f.Source }"));
        Assert.Equal("rni", FirstDestructiveVerb("rni $a $b"));
        Assert.Equal("--del", FirstDestructiveVerb("rsync -a --del \"$3\" \"$dest/\""));
        Assert.Equal("--remove-", FirstDestructiveVerb("rsync -a --remove-sent-files \"$3\""));
        // The move spellings the first list guarded only for delete.
        Assert.Equal(".MoveTo(", FirstDestructiveVerb("$f.MoveTo($t)"));
        Assert.Equal("File]::Move", FirstDestructiveVerb("[IO.File]::Move($a, $b)"));
        Assert.Equal("Directory]::Move", FirstDestructiveVerb("[IO.Directory]::Move($a, $b)"));
        Assert.Equal("File]::Replace", FirstDestructiveVerb("[IO.File]::Replace($a, $b, $c)"));
        Assert.Equal("Invoke-Expression", FirstDestructiveVerb("Invoke-Expression $cmd"));
        Assert.Equal("Remove-PSDrive", FirstDestructiveVerb("Remove-PSDrive X"));
        Assert.Equal("sdelete", FirstDestructiveVerb("sdelete -p 3 $f"));
        Assert.Equal("cipher", FirstDestructiveVerb("cipher /w:C"));
        Assert.Equal("format", FirstDestructiveVerb("format D: /q"));
        // and the hyphen rule that keeps the script's own display helpers out of it.
        Assert.Null(FirstDestructiveVerb("Write-Host (Format-Bytes $TotalBytes)"));
        Assert.Null(FirstDestructiveVerb("Write-Host (Format-Duration $eta)"));

        var input = new WbppScriptInput(
            [new CopyOperation(new DateOnly(2026, 7, 1), @"D:\Astro\rm -rf del\2026-07-01", "rm -rf del", [])],
            @"D:\Staging\M 31",
            "M 31",
            Nine,
            "wbpp_M_31.ps1");

        Assert.Null(FirstDestructiveVerb(ScriptGenerator.Generate(WbppScriptType.PowerShell, input)));
        Assert.Null(FirstDestructiveVerb(
            ScriptGenerator.Generate(WbppScriptType.Bash, ForShell(input, WbppScriptType.Bash))));

        // Every one of the sixteen golden files, the Python's own eight included, is clean too.
        foreach (var fileName in GoldenNames.SelectMany(
                     n => new[] { n + ".ps1", n + ".sh", n + ".python.ps1", n + ".python.sh" }))
        {
            Assert.Null(FirstDestructiveVerb(ReadGolden(fileName)));
        }
    }

    // -----------------------------------------------------------------------------------------
    // Write only under the staging root
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void EveryDestinationIsUnderTheStagingRootAndNoSourceIsOne()
    {
        foreach (var name in GoldenNames)
        {
            var powerShellInput = InputFor(name, WbppScriptType.PowerShell);
            var powerShell = ScriptGenerator.Generate(WbppScriptType.PowerShell, powerShellInput);

            // Exactly one destination expression in the file, and it is composed from $Job.Dst,
            // which is composed from $StagingRoot and nothing else.
            Assert.Equal(1, CountOf(powerShell, "-Destination "));
            Assert.Contains("Copy-Item -LiteralPath $f.Source -Destination $f.Target -Force", powerShell, StringComparison.Ordinal);
            Assert.Equal(1, CountOf(powerShell, "Target = (Join-Path $Job.Dst $RelPath)"));
            Assert.Equal(powerShellInput.Operations.Count, CountOf(powerShell, "Dst = (Join-Path $StagingRoot "));
            foreach (var operation in powerShellInput.Operations)
            {
                Assert.Contains(
                    "Dst = (Join-Path $StagingRoot " + ScriptGenerator.PowerShellQuote(operation.EntryName) + ")",
                    powerShell,
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    "-Destination " + ScriptGenerator.PowerShellQuote(operation.SourcePath),
                    powerShell,
                    StringComparison.Ordinal);
            }

            var bashInput = InputFor(name, WbppScriptType.Bash);
            var bash = ScriptGenerator.Generate(WbppScriptType.Bash, bashInput);
            Assert.Equal(1, CountOf(bash, "local dest="));
            Assert.Contains(@"local dest=""$STAGING_ROOT/$2""", bash, StringComparison.Ordinal);
            Assert.Equal(1, CountOf(bash, @"""$dest/"""));
        }
    }

    // -----------------------------------------------------------------------------------------
    // 8.8 SanitizeScriptName and FileNameFor
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("NGC 7000: North America Nebula", "NGC_7000_North_America_Nebula")]
    [InlineData("M 81 - Bode's Galaxy", "M_81_-_Bode_s_Galaxy")]
    [InlineData("M27 / \"Dumbbell\" *test*?", "M27_Dumbbell_test")]
    [InlineData("  ...Sh2-155...  ", "Sh2-155")]
    [InlineData("::: ???", "target")]
    public void SanitizeScriptName_MatchesThePythonsFiveAssertions(string input, string expected)
    {
        // A failure looks like a target name reaching the file name with a colon or a slash in it,
        // which the save dialog then rejects or, worse, reads as a path.
        Assert.Equal(expected, ScriptGenerator.SanitizeScriptName(input));
        Assert.Equal("wbpp_" + expected + ".ps1", ScriptGenerator.FileNameFor(WbppScriptType.PowerShell, input));
        Assert.Equal("wbpp_" + expected + ".sh", ScriptGenerator.FileNameFor(WbppScriptType.Bash, input));
    }

    // -----------------------------------------------------------------------------------------
    // 8.9 ParseOs and ToStored
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("powershell", WbppScriptType.PowerShell)]
    [InlineData("PowerShell", WbppScriptType.PowerShell)]
    [InlineData("bash", WbppScriptType.Bash)]
    [InlineData("BASH", WbppScriptType.Bash)]
    [InlineData(null, WbppScriptType.PowerShell)]
    [InlineData("", WbppScriptType.PowerShell)]
    [InlineData("windows", WbppScriptType.PowerShell)]
    [InlineData("posix", WbppScriptType.PowerShell)]
    [InlineData("POWERSHELL ", WbppScriptType.PowerShell)]
    [InlineData("bash ", WbppScriptType.PowerShell)]
    public void ParseOs_ReadsTheStoredLiteralOrTheDefault(string? stored, WbppScriptType expected)
    {
        // A failure looks like a stored "windows" from a hand edit selecting Bash by accident on a
        // Windows machine. An untrimmed literal reads as the default, which is what
        // FrameListFormats.Parse already does for its own key.
        Assert.Equal(expected, ScriptGenerator.ParseOs(stored));
    }

    [Fact]
    public void ToStored_RoundTripsBothTypes()
    {
        Assert.Equal("powershell", ScriptGenerator.ToStored(WbppScriptType.PowerShell));
        Assert.Equal("bash", ScriptGenerator.ToStored(WbppScriptType.Bash));
        Assert.Equal(WbppScriptType.PowerShell, ScriptGenerator.ParseOs(ScriptGenerator.ToStored(WbppScriptType.PowerShell)));
        Assert.Equal(WbppScriptType.Bash, ScriptGenerator.ParseOs(ScriptGenerator.ToStored(WbppScriptType.Bash)));
    }

    [Fact]
    public void DefaultExclusions_AreTheNineInSpecOrder()
        => Assert.Equal(Nine, ScriptGenerator.DefaultExclusions);

    // -----------------------------------------------------------------------------------------
    // 8.10 The staging root is required, and is never a source
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(WbppScriptType.PowerShell, "")]
    [InlineData(WbppScriptType.PowerShell, "   ")]
    [InlineData(WbppScriptType.Bash, "")]
    [InlineData(WbppScriptType.Bash, "   ")]
    public void Generate_RefusesAnEmptyStagingRoot(WbppScriptType type, string staging)
    {
        // A failure looks like a helpful default staging root, which writes a user's frames into
        // their own library beside the originals.
        var input = new WbppScriptInput(
            [new CopyOperation(new DateOnly(2026, 7, 1), @"D:\Astro\M31\2026-07-01", "2026-07-01", [])],
            staging,
            "M 31",
            Nine,
            "wbpp_M_31.ps1");

        var thrown = Assert.Throws<WbppStagingRootException>(
            () => ScriptGenerator.Generate(type, ForShell(input, type)));
        Assert.Null(thrown.SourcePath);
    }

    [Theory]
    [InlineData(@"D:\Astro\M31\2026-07-01")]
    [InlineData(@"D:\Astro\M31\2026-07-01\staging\here")]
    public void Generate_RefusesAStagingRootThatIsOrIsInsideASource(string staging)
    {
        // A failure looks like a script that copies a folder into itself, which on a second run
        // copies its own copies.
        const string source = @"D:\Astro\M31\2026-07-01";
        var input = new WbppScriptInput(
            [new CopyOperation(new DateOnly(2026, 7, 1), source, "2026-07-01", [])],
            staging,
            "M 31",
            Nine,
            "wbpp_M_31.ps1");

        foreach (var type in BothShells)
        {
            var thrown = Assert.Throws<WbppStagingRootException>(
                () => ScriptGenerator.Generate(type, ForShell(input, type)));
            Assert.Equal(source, thrown.SourcePath);
        }
    }

    [Fact]
    public void NoScriptNamesADerivedStagingFolder()
    {
        foreach (var name in GoldenNames)
        {
            foreach (var type in BothShells)
            {
                Assert.DoesNotContain("_WBPP_staging", ScriptGenerator.Generate(type, InputFor(name, type)), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Generate_RequiresAFileName()
    {
        var input = new WbppScriptInput(
            [new CopyOperation(new DateOnly(2026, 7, 1), @"D:\Astro\M31\2026-07-01", "2026-07-01", [])],
            @"D:\Staging\M 31",
            "M 31",
            Nine,
            "");

        // P4 item 1: a typed exception rather than a bare ArgumentException, which the page's
        // catch clauses would have missed and let through as a crash.
        Assert.Throws<WbppUnsafeValueException>(() => ScriptGenerator.Generate(WbppScriptType.PowerShell, input));
        Assert.Throws<WbppUnsafeValueException>(() => ScriptGenerator.Generate(WbppScriptType.Bash, input));
    }

    [Fact]
    public void SessionsHeaderIsBuiltFromTheOperationsOwnNights()
    {
        var text = ScriptGenerator.Generate(
            WbppScriptType.PowerShell, InputFor("two-ops-excludes", WbppScriptType.PowerShell));

        Assert.Contains("# Sessions: 2026-07-01, 2026-07-02", text, StringComparison.Ordinal);
    }

    // =========================================================================================
    // Fixtures and helpers
    // =========================================================================================

    private static readonly string[] GoldenNames = ["one-op", "two-ops-excludes", "no-exclusions", "awkward"];

    private static readonly WbppScriptType[] BothShells = [WbppScriptType.PowerShell, WbppScriptType.Bash];

    private static string Extension(WbppScriptType type) => type == WbppScriptType.Bash ? ".sh" : ".ps1";

    /// <summary>The same input with the file name carrying the flavour's own extension, which
    /// <c>Generate</c> now requires. Used wherever one input is rendered for both shells.</summary>
    private static WbppScriptInput ForShell(WbppScriptInput input, WbppScriptType type)
    {
        var stem = input.FileName.EndsWith(".ps1", StringComparison.Ordinal) ? input.FileName[..^4]
            : input.FileName.EndsWith(".sh", StringComparison.Ordinal) ? input.FileName[..^3]
            : input.FileName;
        return input with { FileName = stem + Extension(type) };
    }

    private static bool IsPrintableAscii(string value) => value.All(c => c is >= ' ' and <= '~');

    private static (string ValueName, string Value) NameAndValue(Action act)
    {
        var thrown = Assert.Throws<WbppUnsafeValueException>(act);
        return (thrown.ValueName, thrown.Value);
    }

    /// <summary>Evaluates what <c>PowerShellQuote</c> emitted, term by term, so the case asserts
    /// the value survives the splice rather than asserting a shape. It understands the three term
    /// kinds the quoter can produce and throws on anything else, which is what makes it a check
    /// rather than a paraphrase. It splits on the term separator, so a case whose value itself
    /// contains that exact sequence would need a different evaluator; none does.</summary>
    private static string EvaluatePowerShellExpression(string expression)
    {
        var body = expression.StartsWith('(') && expression.EndsWith(')')
            ? expression[1..^1]
            : expression;

        var result = new StringBuilder();
        foreach (var term in body.Split(" + "))
        {
            if (term.Length >= 2 && term[0] == '\'' && term[^1] == '\'')
            {
                result.Append(term[1..^1].Replace("''", "'"));
            }
            else if (term.StartsWith("[char]::ConvertFromUtf32(0x", StringComparison.Ordinal))
            {
                result.Append(char.ConvertFromUtf32(
                    int.Parse(term[27..^1], NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
            }
            else if (term.StartsWith("[char]0x", StringComparison.Ordinal))
            {
                result.Append((char)int.Parse(term[8..], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            }
            else
            {
                throw new InvalidOperationException($"unrecognised term in the quoted expression: '{term}'");
            }
        }

        return result.ToString();
    }

    private static WbppScriptInput InputFor(string name, WbppScriptType type)
    {
        var suffix = Extension(type);
        return name switch
        {
            "one-op" => new WbppScriptInput(
                [new CopyOperation(new DateOnly(2026, 7, 1), @"D:\Astro\M31\2026-07-01", "2026-07-01", [])],
                @"D:\Staging\M 31",
                "M 31",
                Nine,
                "wbpp_M_31" + suffix),
            "two-ops-excludes" => new WbppScriptInput(
                [
                    new CopyOperation(new DateOnly(2026, 7, 1), @"D:\Astro\M31\2026-07-01", "2026-07-01_M31", [@"Ha\light_bad.fits"]),
                    new CopyOperation(new DateOnly(2026, 7, 2), @"D:\Astro\M31\2026-07-02", "2026-07-02_M31", [@"OIII\light_bad.fits", @"Ha\other_bad.fits"]),
                ],
                @"D:\Staging\M 31",
                "M 31",
                Nine,
                "wbpp_M_31" + suffix),
            "no-exclusions" => new WbppScriptInput(
                [new CopyOperation(new DateOnly(2026, 7, 1), @"D:\Astro\M31\2026-07-01", "2026-07-01", [])],
                @"D:\Staging\M 31",
                "M 31",
                [],
                "wbpp_M_31" + suffix),
            "awkward" => new WbppScriptInput(
                [new CopyOperation(new DateOnly(2026, 7, 1), @"\\nas\astro\o'brien $HOME\Angle_71.61", "2026-07-01", [@"Ha\it's bad.fits"])],
                @"D:\WBPP Staging\o'brien",
                "M 81 - Bode's Galaxy",
                ["WBPP", "*CALIBRATED"],
                "wbpp_M_81_-_Bode_s_Galaxy" + suffix),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown golden input"),
        };
    }

    private static WbppScriptInput InputWithExclusions(IReadOnlyList<string> exclusions)
        => new(
            [new CopyOperation(new DateOnly(2026, 7, 1), @"D:\Astro\M31\2026-07-01", "2026-07-01", [])],
            @"D:\Staging\M 31",
            "M 31",
            exclusions,
            "wbpp_M_31.ps1");

    private static IEnumerable<WbppScriptInput> EveryScannedInput()
    {
        foreach (var name in new[] { "one-op", "two-ops-excludes", "no-exclusions", "awkward" })
        {
            yield return InputFor(name, WbppScriptType.PowerShell);
        }

        foreach (var fragment in new[]
                 {
                     "o'brien", "$(id)", "$HOME", "back`tick", "say \"hi\"", "two words", "semi;colon",
                     "amp&ersand", "paren(s)", "M31 [HaOIII]", "star*quest?", "-dash", "~tilde",
                     "Messier \u03b1 \u00e9toile", "trailing dot.", "trailing space ", "$HOME [raw]",
                 })
        {
            yield return new WbppScriptInput(
                [new CopyOperation(new DateOnly(2026, 7, 1), @"D:\Astro\" + fragment + @"\2026-07-01", "2026-07-01", [@"Ha\bad.fits"])],
                @"D:\Staging\" + fragment,
                fragment,
                Nine,
                "wbpp_x.ps1");
        }
    }

    // ---- the declared differences ----

    private static string UndoPowerShellDepartures(string ported)
        => ported
            .Replace(
                @"$_.FullName.Substring($Job.Src.Length).TrimStart('\', '/').Split([char[]]@('\', '/'))",
                @"$_.FullName.Split([char[]]@('\', '/'))",
                StringComparison.Ordinal)
            .Replace("Test-Path -LiteralPath $", "Test-Path $", StringComparison.Ordinal)
            .Replace("Get-ChildItem -LiteralPath $Job.Src", "Get-ChildItem -Path $Job.Src", StringComparison.Ordinal)
            .Replace("Copy-Item -LiteralPath $f.Source", "Copy-Item -Path $f.Source", StringComparison.Ordinal);

    private static string UndoBashDepartures(string ported, WbppScriptInput input)
    {
        var text = ported
            .Replace("\n" + MountLine, string.Empty, StringComparison.Ordinal)
            .Replace(@"mkdir -p -- """, @"mkdir -p """, StringComparison.Ordinal)
            .Replace(@" -- ""$3"" ""$dest/""", @" ""$3"" ""$dest/""", StringComparison.Ordinal);

        text = text
            .Replace(
                "STAGING_ROOT=" + ScriptGenerator.BashQuote(ScriptGenerator.ToPosixPath(input.StagingRoot)),
                "STAGING_ROOT=" + ScriptGenerator.BashQuote(input.StagingRoot),
                StringComparison.Ordinal)
            .Replace(
                "# staging root: " + ScriptGenerator.ToPosixPath(input.StagingRoot),
                "# staging root: " + input.StagingRoot,
                StringComparison.Ordinal);

        foreach (var operation in input.Operations)
        {
            text = text.Replace(
                ScriptGenerator.BashQuote(ScriptGenerator.ToPosixPath(operation.SourcePath) + "/"),
                ScriptGenerator.BashQuote(operation.SourcePath + "/"),
                StringComparison.Ordinal);
        }

        return text;
    }

    // ---- the verb scan ----

    // The .NET spellings are symmetric on delete and move: the review found Delete guarded and
    // Move unguarded, so a one-line edit to the generator could have moved a user's frames through
    // a spelling the scan did not know. Output redirection onto a path is missed and cannot be
    // added, because the Bash header line printf '%s  %s session folder(s) -> %s%s\n' carries an
    // arrow and every candidate pattern for it false-positives on that line.
    private static readonly string[] SubstringVerbs =
    [
        "Remove-Item", "Move-Item", "Rename-Item", "Clear-Content", "Clear-Item", "Set-Content",
        "Add-Content", "Out-File", "Remove-PSDrive", "Invoke-Expression",
        ".Delete(", ".MoveTo(", "File]::Delete", "Directory]::Delete", "File]::Move",
        "Directory]::Move", "File]::Replace",
        "robocopy", "sdelete", "cipher", "/MOV", "/MOVE", "/MIR", "/PURGE",
        "--delete", "--delete-after", "--delete-before", "--remove-source-files", "--inplace",
        "--del", "--remove-",
    ];

    private static readonly string[] WordVerbs =
    [
        "del", "erase", "rd", "rmdir", "rm", "mv", "unlink", "shred", "truncate", "format",
        "ri", "mi", "move", "ren", "rni",
    ];

    /// <summary>The name of the first destructive verb in a script's own code, or null. Quoted
    /// literals are stripped first, so a folder the user genuinely named "rm -rf del" is inert
    /// data rather than a false positive.</summary>
    private static string? FirstDestructiveVerb(string text)
    {
        var code = StripQuotedLiterals(text);

        foreach (var verb in SubstringVerbs)
        {
            if (code.Contains(verb, StringComparison.OrdinalIgnoreCase))
            {
                return verb;
            }
        }

        foreach (var verb in WordVerbs)
        {
            // Not followed by a hyphen, so the cmdlet spellings the script legitimately uses, such
            // as Format-Bytes and Format-Duration, are not read as the volume formatter. The
            // cmdlets that really are destructive are in the substring list under their full names,
            // so nothing is lost by the exclusion.
            if (Regex.IsMatch(code, @"\b" + Regex.Escape(verb) + @"\b(?!-)", RegexOptions.IgnoreCase))
            {
                return verb;
            }
        }

        return null;
    }

    private static string StripQuotedLiterals(string text)
    {
        // Bash's '\'' idiom and PowerShell's doubled apostrophe are flattened first, so what is
        // left is a plain alternation of single-quoted literals that one toggle can walk. The
        // toggle is reset at every line, because neither flavour has a literal that spans lines
        // and a stray apostrophe in a header comment must not swallow the code below it. The
        // bounded cost of that reset: a line with an odd apostrophe count, such as the awkward
        // input's "# Target: M 81 - Bode's Galaxy", drops its own remainder from the scan. It is
        // bounded to that one line, and a comment line carries no code.
        var builder = new StringBuilder(text.Length);
        foreach (var line in text.Split('\n'))
        {
            var flattened = line.Replace(@"'\''", string.Empty, StringComparison.Ordinal)
                .Replace("''", string.Empty, StringComparison.Ordinal);
            var inside = false;
            foreach (var c in flattened)
            {
                if (c == '\'')
                {
                    inside = !inside;
                    continue;
                }

                if (!inside)
                {
                    builder.Append(c);
                }
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    // ---- reading the goldens ----

    private static string ReadGolden(string fileName)
    {
        var path = Path.Combine(FindRepoRoot(), "tests", "Fixtures", "golden", fileName);
        Assert.True(File.Exists(path), $"golden file not found: {path}");
        return File.ReadAllText(path);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GalactiLog.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("GalactiLog.sln not found above test output directory.");
    }

    // ---- small text helpers ----

    private static string[] Lines(string text) => text.Split('\n');

    private static string LineContaining(string text, string marker)
        => Lines(text).FirstOrDefault(l => l.Contains(marker, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"no line containing '{marker}'");

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        var index = text.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
