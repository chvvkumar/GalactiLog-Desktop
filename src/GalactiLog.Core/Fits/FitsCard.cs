namespace GalactiLog.Core.Fits;

// One parsed FITS header card (spec 6.1.2). Value is exactly one of long, double, bool,
// string, or null - never int/float/other numeric widths.
public sealed record FitsCard(string Keyword, object? Value, string? Comment);

// Result of reading a primary HDU header (spec 6.1.1-6.1.5). Cards is empty when Accepted
// is false. HeaderBlockCount is meaningful only when Accepted is true; Task 3 uses it to
// seek to the data segment (offset = HeaderBlockCount * 2880) without re-parsing the header.
public sealed record FitsHeaderResult(
    bool Accepted,
    string? RejectionReason,
    IReadOnlyList<FitsCard> Cards,
    int HeaderBlockCount);
