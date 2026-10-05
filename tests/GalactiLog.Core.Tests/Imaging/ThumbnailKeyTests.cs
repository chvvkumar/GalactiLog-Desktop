using System.Globalization;
using GalactiLog.Core.Imaging;
using Xunit;

namespace GalactiLog.Core.Tests.Imaging;

// The roadmap's Phase 8 row 4 first Verify clause: the key changes when any of the four inputs
// changes. Pure function, no filesystem and no settings, so every case here is a direct call.
public class ThumbnailKeyTests
{
    private const string Path = @"C:\Astro\M31\LIGHT_0001.fits";
    private const long Size = 16781760;
    private const long Mtime = 1735689600;
    private const int Width = 800;

    [Fact]
    public void For_SameInputs_ProducesTheSameKey()
    {
        Assert.Equal(
            ThumbnailKey.For(Path, Size, Mtime, Width),
            ThumbnailKey.For(Path, Size, Mtime, Width));
    }

    [Fact]
    public void For_DifferentPath_ChangesTheKey()
    {
        Assert.NotEqual(
            ThumbnailKey.For(Path, Size, Mtime, Width),
            ThumbnailKey.For(@"C:\Astro\M31\LIGHT_0002.fits", Size, Mtime, Width));
    }

    [Fact]
    public void For_DifferentFileSize_ChangesTheKey()
    {
        Assert.NotEqual(
            ThumbnailKey.For(Path, Size, Mtime, Width),
            ThumbnailKey.For(Path, Size + 1, Mtime, Width));
    }

    [Fact]
    public void For_DifferentMtime_ChangesTheKey()
    {
        Assert.NotEqual(
            ThumbnailKey.For(Path, Size, Mtime, Width),
            ThumbnailKey.For(Path, Size, Mtime + 1, Width));
    }

    [Fact]
    public void For_DifferentWidth_ChangesTheKey()
    {
        Assert.NotEqual(
            ThumbnailKey.For(Path, Size, Mtime, Width),
            ThumbnailKey.For(Path, Size, Mtime, Width + 1));
    }

    // A separator shift would otherwise let two different tuples collide: without it,
    // ("a|1", 2, ...) and ("a", 12, ...) would hash the same bytes.
    [Fact]
    public void For_FieldBoundariesAreNotInterchangeable()
    {
        Assert.NotEqual(
            ThumbnailKey.For("a", 12, Mtime, Width),
            ThumbnailKey.For("a|1", 2, Mtime, Width));
    }

    [Fact]
    public void For_ReturnsThirtyTwoLowercaseHexCharacters()
    {
        var key = ThumbnailKey.For(Path, Size, Mtime, Width);

        Assert.Equal(32, key.Length);
        Assert.All(key, c => Assert.True("0123456789abcdef".Contains(c), $"'{c}' is not lowercase hex"));
    }

    [Fact]
    public void For_ContainsNoPathSeparator()
    {
        var key = ThumbnailKey.For(Path, Size, Mtime, Width);

        Assert.Equal(-1, key.IndexOfAny(['\\', '/']));
        Assert.Equal(-1, key.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()));
    }

    // A culture whose number format carries a digit grouping separator and a comma decimal mark
    // would change every key on a machine set to it if any of the three numbers were formatted
    // with the current culture.
    [Fact]
    public void For_IsInvariantOfTheCurrentCulture()
    {
        var expected = ThumbnailKey.For(Path, Size, Mtime, Width);
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(expected, ThumbnailKey.For(Path, Size, Mtime, Width));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // Records the decision rather than hiding it: the path is hashed as given, so the caller
    // normalizes with Path.GetFullPath before calling.
    [Fact]
    public void For_IsCaseSensitiveInThePath()
    {
        Assert.NotEqual(
            ThumbnailKey.For(@"C:\Astro\M31\LIGHT_0001.fits", Size, Mtime, Width),
            ThumbnailKey.For(@"c:\astro\m31\light_0001.fits", Size, Mtime, Width));
    }

    // Computed outside this codebase (PowerShell over the UTF-8 bytes of
    // "C:\Astro\M31\LIGHT_0001.fits|16781760|1735689600|800", first 16 bytes of SHA-256, lowercase
    // hex), so a future change to the separator, the field order or the byte count fails loudly
    // instead of agreeing with itself.
    [Fact]
    public void For_MatchesAHardcodedVector()
    {
        Assert.Equal("b31d5de6276e21f230c09e9114c3dc56", ThumbnailKey.For(Path, Size, Mtime, Width));
    }
}
