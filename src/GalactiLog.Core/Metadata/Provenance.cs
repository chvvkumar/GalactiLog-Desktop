namespace GalactiLog.Core.Metadata;

// Small mutable builder used while extracting (spec 7.3): each stored field's provenance is
// recorded by the same code path that resolves its value, never by a second pass. Exposed on
// the finished ExtractedMetadata as a read-only snapshot.
public sealed class Provenance
{
    private readonly Dictionary<string, string> _values = new();

    public void Set(string field, string source) => _values[field] = source;

    public IReadOnlyDictionary<string, string> Snapshot() =>
        new Dictionary<string, string>(_values);
}
