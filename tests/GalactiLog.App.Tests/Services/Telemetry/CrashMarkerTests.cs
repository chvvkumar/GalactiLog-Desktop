using System.Runtime.CompilerServices;
using System.Text.Json;
using GalactiLog.App.Services.Telemetry;
using Xunit;

namespace GalactiLog.App.Tests.Services.Telemetry;

public sealed class CrashMarkerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("galactilog-crash-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Write_ThenTryRead_RoundTripsTheCrashObject()
    {
        var ex = Thrown(new IOException(@"Could not find file 'C:\Users\kumar\Pictures\M31\light.fits'."));

        CrashMarker.Write(ex, "appdomain", _root, "1.2.3", "abc123", "targets", 42);
        var crash = CrashMarker.TryRead(_root);

        Assert.NotNull(crash);
        var c = crash!.Value;
        Assert.Equal("System.IO.IOException", c.GetProperty("type").GetString());
        Assert.Equal(ex.HResult, c.GetProperty("hresult").GetInt32());
        Assert.Equal("Could not find file '<path>'.", c.GetProperty("message").GetString());
        Assert.DoesNotContain("kumar", c.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("1.2.3", c.GetProperty("version").GetString());
        Assert.Equal("abc123", c.GetProperty("sha").GetString());
        Assert.Equal("targets", c.GetProperty("page").GetString());
        Assert.Equal(42, c.GetProperty("uptime_s").GetInt64());
        Assert.Equal("appdomain", c.GetProperty("source").GetString());
        Assert.True(DateTime.TryParse(c.GetProperty("ts").GetString(), out _));
    }

    [Fact]
    public void Frames_PutGalactiLogFirst_AndCarryNoFileOrLine()
    {
        // The throw happens inside a System frame (Linq) called from this GalactiLog test, so the
        // stack is System frames first; the marker must reorder.
        Exception ex;
        try
        {
            _ = new[] { 1 }.Select(Throws).ToList();
            throw new InvalidOperationException("unreachable");
        }
        catch (Exception caught)
        {
            ex = caught;
        }

        CrashMarker.Write(ex, "dispatcher", _root, "v", "s", null, 0);
        var frames = CrashMarker.TryRead(_root)!.Value.GetProperty("frames")
            .EnumerateArray().Select(f => f.GetString()!).ToList();

        Assert.NotEmpty(frames);
        Assert.StartsWith("GalactiLog.", frames[0], StringComparison.Ordinal);
        Assert.Contains(frames, f => f.StartsWith("System.", StringComparison.Ordinal));
        Assert.True(frames.Count <= CrashMarker.MaxFrames);
        Assert.All(frames, f =>
        {
            Assert.DoesNotContain(".cs", f, StringComparison.Ordinal);
            Assert.DoesNotContain(":line", f, StringComparison.Ordinal);
        });
        Assert.Equal("", CrashMarker.TryRead(_root)!.Value.GetProperty("page").GetString());
    }

    [Fact]
    public void InnerChain_IsCappedAtFive()
    {
        Exception ex = new FormatException("innermost");
        for (var i = 0; i < 7; i++)
        {
            ex = new InvalidOperationException($"level {i}", ex);
        }

        CrashMarker.Write(ex, "appdomain", _root, "v", "s", "", 0);
        var inner = CrashMarker.TryRead(_root)!.Value.GetProperty("inner");

        Assert.Equal(5, inner.GetArrayLength());
        Assert.All(inner.EnumerateArray(), e => Assert.Equal("System.InvalidOperationException", e.GetString()));
    }

    [Fact]
    public void AnAggregate_ReportsItsFirstFlattenedInner()
    {
        // Flatten lists an aggregate's direct inners before the inners of nested aggregates.
        var ex = new AggregateException(new AggregateException(new TimeoutException("t"), new FormatException()));

        CrashMarker.Write(ex, "unobserved", _root, "v", "s", "", 0);

        Assert.Equal("System.TimeoutException", CrashMarker.TryRead(_root)!.Value.GetProperty("type").GetString());
    }

    [Fact]
    public void AFormatException_DropsItsMessage()
    {
        CrashMarker.Write(new FormatException("The input string 'M 31 secret' was not in a correct format."),
            "appdomain", _root, "v", "s", "", 0);

        var crash = CrashMarker.TryRead(_root)!.Value;
        Assert.Equal("System.FormatException", crash.GetProperty("type").GetString());
        Assert.Equal("", crash.GetProperty("message").GetString());
        Assert.DoesNotContain("secret", crash.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void AKeyNotFoundException_DropsItsMessage()
    {
        CrashMarker.Write(new KeyNotFoundException("The given key 'M31 secret' was not present."),
            "appdomain", _root, "v", "s", "", 0);

        Assert.Equal("", CrashMarker.TryRead(_root)!.Value.GetProperty("message").GetString());
    }

    [Fact]
    public void AnArgumentNullException_KeepsItsMessage()
    {
        var ex = new ArgumentNullException("path");
        CrashMarker.Write(ex, "appdomain", _root, "v", "s", "", 0);

        Assert.Equal(ex.Message, CrashMarker.TryRead(_root)!.Value.GetProperty("message").GetString());
    }

    [Theory]
    [MemberData(nameof(AllowlistCases))]
    public void IsMessageAllowed_FollowsTheAllowlist(Exception ex, bool allowed)
        => Assert.Equal(allowed, CrashMarker.IsMessageAllowed(ex));

    public static TheoryData<Exception, bool> AllowlistCases() => new()
    {
        { new IOException("io"), true },
        { new FileNotFoundException("missing"), true },
        { new ArgumentNullException("name"), true },
        { new ArgumentOutOfRangeException("index"), true },
        { new InvalidOperationException("state"), true },
        { new System.Text.Json.JsonException("json"), true },
        { new Microsoft.Data.Sqlite.SqliteException("database is locked", 5), true },
        { new FormatException("input"), false },
        { new KeyNotFoundException("key"), false },
        { new System.Net.Http.HttpRequestException("host"), false },
        { new ObjectDisposedException("x"), false },
    };

    [Fact]
    public void Delete_RemovesTheMarker()
    {
        CrashMarker.Write(new InvalidOperationException(), "appdomain", _root, "v", "s", "", 0);

        CrashMarker.Delete(_root);

        Assert.False(File.Exists(CrashMarker.PathFor(_root)));
        Assert.Null(CrashMarker.TryRead(_root));
    }

    [Fact]
    public void TryRead_AMissingMarker_IsNull() => Assert.Null(CrashMarker.TryRead(_root));

    [Fact]
    public void TryRead_ACorruptMarker_IsNull()
    {
        File.WriteAllText(CrashMarker.PathFor(_root), "{ not json");

        Assert.Null(CrashMarker.TryRead(_root));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Throws(int _) => throw new InvalidOperationException("boom");

    private static Exception Thrown(Exception ex)
    {
        try
        {
            throw ex;
        }
        catch (Exception caught)
        {
            return caught;
        }
    }
}
