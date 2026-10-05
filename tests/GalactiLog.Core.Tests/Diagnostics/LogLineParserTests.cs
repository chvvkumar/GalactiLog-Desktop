using System.Globalization;
using GalactiLog.Core.Diagnostics;
using Xunit;

namespace GalactiLog.Core.Tests.Diagnostics;

// The parser's whole contract is design-spec 16.1's output template, which AppHost passes to the
// Serilog file sink verbatim:
//
//   {Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}
//
// Every literal in these cases is written the way that template writes it, so a change to the
// template breaks these tests rather than the log viewer.
public class LogLineParserTests
{
    private const string Header =
        "2026-09-15 12:34:56.789 +05:30 [INF] GalactiLog.App.AppHost Started";

    [Theory]
    [InlineData("VRB")]
    [InlineData("DBG")]
    [InlineData("INF")]
    [InlineData("WRN")]
    [InlineData("ERR")]
    [InlineData("FTL")]
    public void IsEntryStart_AcceptsALineWrittenByTheSpec161Template(string level)
    {
        var line = $"2026-09-15 12:34:56.789 +05:30 [{level}] GalactiLog.App.AppHost Started";

        Assert.True(LogLineParser.IsEntryStart(line));
    }

    [Fact]
    public void IsEntryStart_RejectsAStackFrameLine()
        => Assert.False(LogLineParser.IsEntryStart("   at GalactiLog.App.Program.Main()"));

    [Fact]
    public void IsEntryStart_RejectsAnExceptionHeaderLine()
        => Assert.False(LogLineParser.IsEntryStart("System.IO.IOException: access denied"));

    [Fact]
    public void IsEntryStart_RejectsAPartialFirstLine()
    {
        // What a reader sees when it opens the file part-way through an entry: the tail of a
        // timestamp with no level token behind it.
        Assert.False(LogLineParser.IsEntryStart("56.789 +05:30 [INF] GalactiLog.App.AppHost Started"));
        Assert.False(LogLineParser.IsEntryStart("2026-09-15 12:34:56.789 +05:30 [IN"));
    }

    [Fact]
    public void TryParseHeader_ParsesTheTimestampWithTheOffset()
    {
        Assert.True(LogLineParser.TryParseHeader(
            Header, out var timestamp, out var level, out var sourceContext, out var message));

        Assert.Equal(
            new DateTimeOffset(2026, 9, 15, 12, 34, 56, 789, TimeSpan.FromMinutes(330)),
            timestamp);
        Assert.Equal(TimeSpan.FromMinutes(330), timestamp.Offset);
        Assert.Equal(LogLineLevel.Information, level);
        Assert.Equal("GalactiLog.App.AppHost", sourceContext);
        Assert.Equal("Started", message);
    }

    [Fact]
    public void TryParseHeader_UsesTheInvariantCulture_UnderANonInvariantCurrentCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            // A comma-decimal, non-Gregorian-friendly locale. Serilog writes with the invariant
            // culture whatever the machine is set to, so a parser that used the current culture
            // would fail to read this process's own log file.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            Assert.True(LogLineParser.TryParseHeader(
                Header, out var timestamp, out _, out _, out _));
            Assert.Equal(789, timestamp.Millisecond);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void TryParseHeader_ParsesAnEmptySourceContext()
    {
        // Serilog renders an absent SourceContext as the empty string, which leaves two adjacent
        // spaces before the message.
        var line = "2026-09-15 12:34:56.789 +05:30 [WRN]  A message with no source context";

        Assert.True(LogLineParser.TryParseHeader(
            line, out _, out var level, out var sourceContext, out var message));

        Assert.Equal(LogLineLevel.Warning, level);
        Assert.Equal("", sourceContext);
        Assert.Equal("A message with no source context", message);
    }

    [Fact]
    public void TryParseHeader_ParsesAMessageContainingBrackets()
    {
        var line =
            "2026-09-15 12:34:56.789 +05:30 [ERR] GalactiLog.Core.Io.FileWalker Refused [INF] \"C:\a [b]\"";

        Assert.True(LogLineParser.TryParseHeader(
            line, out _, out var level, out var sourceContext, out var message));

        Assert.Equal(LogLineLevel.Error, level);
        Assert.Equal("GalactiLog.Core.Io.FileWalker", sourceContext);
        Assert.Equal("Refused [INF] \"C:\a [b]\"", message);
    }

    [Fact]
    public void TryParseHeader_RejectsALineIsEntryStartRejects()
    {
        Assert.False(LogLineParser.TryParseHeader(
            "   at GalactiLog.App.Program.Main()", out _, out _, out _, out _));
    }

    [Theory]
    [InlineData("VRB", LogLineLevel.Verbose)]
    [InlineData("DBG", LogLineLevel.Debug)]
    [InlineData("INF", LogLineLevel.Information)]
    [InlineData("WRN", LogLineLevel.Warning)]
    [InlineData("ERR", LogLineLevel.Error)]
    [InlineData("FTL", LogLineLevel.Fatal)]
    public void TryParseLevel_MapsAllSixThreeLetterForms(string token, LogLineLevel expected)
    {
        Assert.True(LogLineParser.TryParseLevel(token.AsSpan(), out var level));
        Assert.Equal(expected, level);
    }

    [Theory]
    [InlineData("Ver")]
    [InlineData("Deb")]
    [InlineData("Inf")]
    [InlineData("War")]
    [InlineData("Fat")]
    public void TryParseLevel_RejectsATruncatedEnumName(string truncated)
    {
        // Verbose truncates to "Ver", not "VRB". A map derived by truncating the enum name would
        // accept these and reject the tokens Serilog actually writes.
        Assert.False(LogLineParser.TryParseLevel(truncated.AsSpan(), out _));
    }
}
