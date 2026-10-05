using System.Text;

namespace GalactiLog.Core.Text;

// Extracted from NinaCsvReader's former private SplitCsvLine (design-lessons rule 1: this
// is the second CSV parser in the codebase -- StaticCatalogLoader -- so the shared spine is
// built now rather than copied a second time). Minimal RFC-4180-enough splitter: a quoted
// field, a doubled-quote escape inside a quoted field, and a configurable single-character
// delimiter. No embedded newlines inside a field -- true of every source this project reads
// (N.I.N.A.'s CSV export, and every bundled catalog CSV).
internal static class CsvLine
{
    internal static IReadOnlyList<string> Split(string line, char delimiter = ',')
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"' && current.Length == 0)
            {
                inQuotes = true;
            }
            else if (c == delimiter)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        fields.Add(current.ToString());
        return fields;
    }
}
