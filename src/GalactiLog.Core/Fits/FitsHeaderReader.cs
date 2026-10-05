using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GalactiLog.Core.Fits;

// Parses a FITS primary header from a Stream (spec 6.1.1-6.1.3, 6.1.5). Static reader over
// spans, per the ponytail rule: one FitsCard record, no visitor/strategy abstraction. Takes
// a Stream so tests can hand it a MemoryStream and production callers hand it whatever
// UserFiles.OpenRead returns; this file never touches the filesystem itself.
public static class FitsHeaderReader
{
    private const int BlockSize = 2880;
    private const int CardSize = 80;
    private const int CardsPerBlock = BlockSize / CardSize;
    private const int MaxBlocks = 200;

    // FITS 4.0's hard limit on the number of axes.
    private const long MaxNaxis = 999;

    private static readonly HashSet<long> AcceptedBitpix = new() { 8, 16, 32, 64, -32, -64 };

    public static FitsHeaderResult Read(Stream stream)
    {
        var scan = ScanHeader(stream);
        if (!scan.FoundEnd)
        {
            return Rejected("header not terminated");
        }

        var cards = scan.Cards;
        var headerBlockCount = scan.BlocksRead;

        // 1. SIMPLE must be the first card and true.
        if (cards.Count == 0 || cards[0].Keyword != "SIMPLE" || cards[0].Value is not true)
        {
            return Rejected("not a simple FITS file");
        }

        // 2. BITPIX must be present and one of the accepted values. BITPIX = 64 is accepted
        // here per the coordinator ruling: the header reader ingests it, the pixel reader
        // (Task 3) is the one that declines to decode it.
        var bitpixValue = GetValue(cards, "BITPIX");
        if (bitpixValue is not long bitpix || !AcceptedBitpix.Contains(bitpix))
        {
            var display = bitpixValue switch
            {
                null => "missing",
                long l => l.ToString(CultureInfo.InvariantCulture),
                _ => bitpixValue.ToString() ?? "invalid",
            };
            return Rejected($"unsupported BITPIX: {display}");
        }

        // 3. NAXIS must be present and an integer (a non-numeric NAXIS is as unusable as a
        // missing one, so it shares the same rejection reason rather than throwing).
        if (GetValue(cards, "NAXIS") is not long naxis)
        {
            return Rejected("missing NAXIS");
        }

        // FITS 4.0 caps NAXIS at 999. Without this the truncation loop below would spin
        // once per declared axis, so a header claiming NAXIS = 999999999999 would hang.
        if (naxis < 0 || naxis > MaxNaxis)
        {
            return Rejected("unsupported NAXIS");
        }

        // 4. Compressed-FITS detection, from the primary header alone.
        if (GetValue(cards, "ZIMAGE") is true)
        {
            return Rejected("compressed FITS (fpack/RICE) not supported, skipping");
        }

        if (naxis == 0)
        {
            // The stream position is already exactly at the next 2880-byte boundary
            // (ScanHeader only ever consumes whole blocks), so scanning again picks up
            // any first-extension header with no offset arithmetic needed.
            var extensionScan = ScanHeader(stream);
            if (extensionScan.FoundEnd && extensionScan.Cards.Count > 0)
            {
                var firstExtensionCard = extensionScan.Cards[0];
                if (firstExtensionCard.Keyword == "XTENSION" &&
                    firstExtensionCard.Value is string xtension &&
                    xtension.Trim() == "BINTABLE" &&
                    GetValue(extensionScan.Cards, "ZCMPTYPE") is not null)
                {
                    return Rejected("compressed FITS (fpack/RICE) not supported, skipping");
                }
            }
            // Otherwise: no extension present, or not a compressed BINTABLE - not an
            // error, fall through (truncation check below is skipped for NAXIS == 0).
        }

        // 5. Truncation check, only meaningful when there is declared pixel data.
        if (naxis > 0)
        {
            var bytesPerSample = Math.Abs(bitpix) / 8;
            long requiredLength;
            try
            {
                // Three large NAXISi values multiply past long.MaxValue and wrap silently
                // without this, producing a small required length that any file satisfies.
                // Matches FitsImageReader's overflow handling.
                var dataBytes = bytesPerSample;
                for (var i = 1; i <= naxis; i++)
                {
                    var dim = GetValue(cards, $"NAXIS{i}") is long naxisI ? naxisI : 1;
                    dataBytes = checked(dataBytes * dim);
                }
                requiredLength = checked((long)headerBlockCount * BlockSize + RoundUpToBlock(dataBytes));
            }
            catch (OverflowException)
            {
                return Rejected("truncated file");
            }
            if (stream.Length < requiredLength)
            {
                return Rejected("truncated file");
            }
        }

        return new FitsHeaderResult(true, null, cards, headerBlockCount);
    }

    public static object? GetValue(IReadOnlyList<FitsCard> cards, string keyword)
    {
        object? result = null;
        foreach (var card in cards)
        {
            if (string.Equals(card.Keyword, keyword, StringComparison.Ordinal))
            {
                result = card.Value;
            }
        }
        return result;
    }

    public static JsonObject BuildRawHeaders(IReadOnlyList<FitsCard> cards)
    {
        var obj = new JsonObject();
        var freeTextArrays = new Dictionary<string, JsonArray>(StringComparer.Ordinal);

        foreach (var card in cards)
        {
            if (card.Keyword == "COMMENT" || card.Keyword == "HISTORY")
            {
                if (!freeTextArrays.TryGetValue(card.Keyword, out var array))
                {
                    array = new JsonArray();
                    freeTextArrays[card.Keyword] = array;
                }
                array.Add(JsonValue.Create((string?)card.Value));
                continue;
            }

            obj[card.Keyword] = card.Value switch
            {
                string s => JsonValue.Create(s),
                long l => JsonValue.Create(l),
                bool b => JsonValue.Create(b),
                double d when double.IsFinite(d) => JsonValue.Create(d),
                double d => JsonValue.Create(d.ToString(CultureInfo.InvariantCulture)),
                null => JsonValue.Create((string?)null),
                _ => JsonValue.Create(card.Value.ToString()),
            };
        }

        foreach (var (keyword, array) in freeTextArrays)
        {
            obj[keyword] = array;
        }

        return obj;
    }

    private static FitsHeaderResult Rejected(string reason) => new(false, reason, Array.Empty<FitsCard>(), 0);

    private static long RoundUpToBlock(long bytes) => checked(((bytes + BlockSize - 1) / BlockSize) * BlockSize);

    private readonly record struct HeaderScanResult(List<FitsCard> Cards, int BlocksRead, bool FoundEnd);

    // Reads 2880-byte blocks from stream on demand until END is found, MaxBlocks is hit, or
    // the stream runs out. Cards can be peeked/consumed one at a time so CONTINUE handling
    // can look ahead across a block boundary without re-reading anything.
    private static HeaderScanResult ScanHeader(Stream stream)
    {
        var cards = new List<FitsCard>();
        var queue = new Queue<byte[]>();
        var blocksRead = 0;
        var blockBuffer = new byte[BlockSize];

        bool EnsureAvailable()
        {
            while (queue.Count == 0)
            {
                if (blocksRead >= MaxBlocks)
                {
                    return false;
                }
                var read = ReadFully(stream, blockBuffer);
                if (read < BlockSize)
                {
                    return false;
                }
                blocksRead++;
                for (var i = 0; i < CardsPerBlock; i++)
                {
                    var card = new byte[CardSize];
                    Buffer.BlockCopy(blockBuffer, i * CardSize, card, 0, CardSize);
                    queue.Enqueue(card);
                }
            }
            return true;
        }

        byte[]? NextRaw() => EnsureAvailable() ? queue.Dequeue() : null;
        byte[]? PeekRaw() => EnsureAvailable() ? queue.Peek() : null;

        var foundEnd = false;
        while (true)
        {
            var raw = NextRaw();
            if (raw is null)
            {
                break;
            }

            var keyword = Ascii(raw.AsSpan(0, 8)).TrimEnd(' ');

            if (keyword == "END")
            {
                foundEnd = true;
                break;
            }

            if (keyword.Length == 0)
            {
                continue;
            }

            if (keyword == "COMMENT" || keyword == "HISTORY")
            {
                var text = Ascii(raw.AsSpan(8, 72)).TrimEnd(' ');
                cards.Add(new FitsCard(keyword, text, null));
                continue;
            }

            if (keyword == "HIERARCH")
            {
                var tail = Ascii(raw.AsSpan(8, 72));
                var eqIndex = tail.IndexOf('=');
                if (eqIndex < 0)
                {
                    // Tolerated malformation (spec 6.1.2 step 4): skip, never reject.
                    continue;
                }
                var realKeyword = CollapseWhitespace(tail[..eqIndex].Trim());
                var initial = ParseValueField(tail[(eqIndex + 1)..]);
                var (value, comment) = ResolveContinuation(initial, PeekRaw, NextRaw);
                cards.Add(new FitsCard(realKeyword, value, comment));
                continue;
            }

            if (raw[8] != (byte)'=' || raw[9] != (byte)' ')
            {
                continue;
            }

            var valueField = Ascii(raw.AsSpan(10, 70));
            var initialStd = ParseValueField(valueField);
            var (stdValue, stdComment) = ResolveContinuation(initialStd, PeekRaw, NextRaw);
            cards.Add(new FitsCard(keyword, stdValue, stdComment));
        }

        return new HeaderScanResult(cards, blocksRead, foundEnd);
    }

    // Follows CONTINUE cards (spec 6.1.2's CONTINUE convention) while the accumulated
    // string keeps ending in '&'. Each consumed CONTINUE card is removed from the card
    // stream: it never becomes its own FitsCard.
    private static (object? Value, string? Comment) ResolveContinuation(
        ValueFieldResult initial, Func<byte[]?> peekRaw, Func<byte[]?> nextRaw)
    {
        if (!initial.PendingContinuation)
        {
            return (initial.Value, initial.Comment);
        }

        var accumulated = (string)initial.Value!;
        var pending = true;
        while (pending)
        {
            var peeked = peekRaw();
            if (peeked is null)
            {
                break;
            }
            var peekKeyword = Ascii(peeked.AsSpan(0, 8)).TrimEnd(' ');
            if (peekKeyword != "CONTINUE")
            {
                break;
            }
            nextRaw(); // consume it - it never becomes its own FitsCard

            var contField = Ascii(peeked.AsSpan(10, 70));
            var trimmedStart = contField.TrimStart(' ');
            if (trimmedStart.Length == 0 || trimmedStart[0] != '\'')
            {
                // Unspecified edge case with no fixture: treat as consumed with an empty
                // fragment and stop continuing, rather than throw.
                break;
            }

            var contResult = ParseValueField(contField);
            accumulated += (string)contResult.Value!;
            pending = contResult.PendingContinuation;
        }

        return (accumulated, initial.Comment);
    }

    private readonly record struct ValueFieldResult(object? Value, string? Comment, bool PendingContinuation);

    // Parses the raw ASCII value-field text of a card (byte 10 onward for a standard card,
    // or after the '=' for HIERARCH). Spec 6.1.2's value-field grammar.
    private static ValueFieldResult ParseValueField(string field)
    {
        var i = 0;
        while (i < field.Length && field[i] == ' ')
        {
            i++;
        }

        if (i < field.Length && field[i] == '\'')
        {
            return ParseStringValue(field, i);
        }

        var remaining = i < field.Length ? field[i..] : string.Empty;
        string valueText;
        string? comment;
        var slashIndex = remaining.IndexOf('/');
        if (slashIndex >= 0)
        {
            valueText = remaining[..slashIndex].Trim();
            comment = remaining[(slashIndex + 1)..].Trim();
        }
        else
        {
            valueText = remaining.Trim();
            comment = null;
        }

        if (valueText is "T" or "F")
        {
            return new ValueFieldResult(valueText == "T", comment, false);
        }

        if (long.TryParse(valueText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var longVal))
        {
            return new ValueFieldResult(longVal, comment, false);
        }

        // FITS permits D/d as the exponent marker; .NET's parsers do not, so rewrite it
        // only for this floating-point attempt.
        var dReplaced = valueText.Replace('D', 'E').Replace('d', 'E');
        if (double.TryParse(dReplaced, NumberStyles.Float, CultureInfo.InvariantCulture, out var dblVal))
        {
            return new ValueFieldResult(dblVal, comment, false);
        }

        // Covers complex-number literals like "(1.0, 2.0)" and any other unparseable token:
        // stored as raw trimmed text, per spec 6.1.2.
        return new ValueFieldResult(valueText, comment, false);
    }

    private static ValueFieldResult ParseStringValue(string field, int quoteIndex)
    {
        var content = new StringBuilder();
        var j = quoteIndex + 1;
        while (j < field.Length)
        {
            if (field[j] == '\'')
            {
                if (j + 1 < field.Length && field[j + 1] == '\'')
                {
                    content.Append('\'');
                    j += 2;
                    continue;
                }
                j++; // consumed the closing quote
                break;
            }
            content.Append(field[j]);
            j++;
        }

        var stringValue = content.ToString().TrimEnd(' ');
        string? comment = null;
        if (j < field.Length)
        {
            var remainder = field[j..];
            var slashIndex = remainder.IndexOf('/');
            if (slashIndex >= 0)
            {
                comment = remainder[(slashIndex + 1)..].Trim();
            }
        }

        var pending = stringValue.EndsWith('&');
        if (pending)
        {
            stringValue = stringValue[..^1];
        }

        return new ValueFieldResult(stringValue, comment, pending);
    }

    private static int ReadFully(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                break;
            }
            total += read;
        }
        return total;
    }

    private static string Ascii(ReadOnlySpan<byte> bytes) => Encoding.ASCII.GetString(bytes);

    private static string CollapseWhitespace(string value) => Regex.Replace(value, @"\s+", " ");
}
