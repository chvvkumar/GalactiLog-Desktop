using System.Text.Json;

namespace GalactiLog.Core.Tests.Metrics;

/// <summary>
/// Reads the two Phase 17 real-data files, <c>tests/Fixtures/realdata/inputs.json</c>
/// and <c>oracle.json</c>, from the REPOSITORY, the way <c>ScriptGeneratorTests</c> reads its
/// goldens: no csproj change, no copied content item and no path outside the clone, so every case
/// built on them runs on a clean copy and on another machine (task2.md section 8, HANDOFF section
/// 3). The values live in <c>inputs.json</c> and the expected figures in <c>oracle.json</c>;
/// neither file is written here or anywhere else in the test tree.
/// </summary>
/// <remarks>
/// Both documents are parsed once and held for the process. They are read-only and a
/// <c>JsonDocument</c> that is never disposed is the same trade the golden readers beside this one
/// take with their file text.
/// <para>
/// Every group label in <c>oracle.json</c> is an ordinal, so a case built on this reader asserts
/// figures and structure and never a rig, target or filter name.
/// </para>
/// </remarks>
internal static class AnalysisOracle
{
    private static readonly JsonDocument InputsDocument = Load("inputs.json");
    private static readonly JsonDocument OracleDocument = Load("oracle.json");

    /// <summary>The root of <c>inputs.json</c>: the 416 rows in the port's own order, the five
    /// PHD2 night rows and the metric key to column map.</summary>
    public static JsonElement Inputs => InputsDocument.RootElement;

    /// <summary>The root of <c>oracle.json</c>.</summary>
    public static JsonElement Oracle => OracleDocument.RootElement;

    /// <summary>The interpreter that produced the oracle. Every figure depends on the builtin
    /// <c>sum()</c>'s Neumaier compensation, which is a CPython 3.12 change, so a case records
    /// this rather than assuming it.</summary>
    public static string PythonVersion => Oracle.GetProperty("python_version").GetString()!;

    /// <summary>The rows of <c>inputs.json</c>, in its own <c>row_order</c>, which is the port's
    /// <c>ORDER BY session_date, id</c>.</summary>
    public static JsonElement Rows => Inputs.GetProperty("rows");

    /// <summary>One named scenario of <c>oracle.json</c>. Fails with the name rather than a
    /// <c>KeyNotFoundException</c>, so a renamed scenario reads as a renamed scenario.</summary>
    public static JsonElement Scenario(string name)
        => Oracle.GetProperty("scenarios").TryGetProperty(name, out var scenario)
            ? scenario
            : throw new InvalidOperationException($"oracle.json carries no scenario '{name}'.");

    /// <summary>One metric's value for every row, in row order, null where the row carries none.
    /// The metric key is the web's own, and <c>inputs.json</c>'s <c>metric_column</c> holds the
    /// column it reads; a <c>phd2_</c> key is read from the row's PHD2 night instead, which is
    /// what the endpoint's join gives (core-shapes.md section 6.4).</summary>
    public static IReadOnlyList<double?> Column(string metricKey)
    {
        if (metricKey.StartsWith("phd2_", StringComparison.Ordinal))
        {
            return [.. Rows.EnumerateArray().Select(row => Phd2Value(row, metricKey))];
        }

        var column = Inputs.GetProperty("metric_column").GetProperty(metricKey).GetString()!;
        return [.. Rows.EnumerateArray().Select(row => Number(row.GetProperty(column)))];
    }

    /// <summary>One metric's values with the rows that carry none dropped, in row order. This is
    /// what the distribution, box plot and time series endpoints hand the helpers.</summary>
    public static IReadOnlyList<double> Values(string metricKey)
        => [.. Column(metricKey).Where(value => value is not null).Select(value => value!.Value)];

    /// <summary>The rows where BOTH metrics carry a value, in row order, as the correlation
    /// endpoint's paired filtering leaves them. The order is part of the answer (task2.md section
    /// 4), so the two lists come back in the file's own row order and nothing sorts them.</summary>
    public static (IReadOnlyList<double> Xs, IReadOnlyList<double> Ys) Pairs(string xKey, string yKey)
    {
        var xs = Column(xKey);
        var ys = Column(yKey);
        var pairedXs = new List<double>(xs.Count);
        var pairedYs = new List<double>(ys.Count);

        for (var i = 0; i < xs.Count; i++)
        {
            if (xs[i] is { } x && ys[i] is { } y)
            {
                pairedXs.Add(x);
                pairedYs.Add(y);
            }
        }

        return (pairedXs, pairedYs);
    }

    /// <summary>Every number of a JSON array, in its own order.</summary>
    public static IReadOnlyList<double> Doubles(JsonElement array)
        => [.. array.EnumerateArray().Select(value => value.GetDouble())];

    /// <summary>A number property that the oracle publishes as JSON <c>null</c> when the figure
    /// has no value, which is how a matrix cell says it shows nothing.</summary>
    public static double? Number(JsonElement value)
        => value.ValueKind == JsonValueKind.Null ? null : value.GetDouble();

    // The frame's PHD2 night figure: the night row whose night and rig ordinals match the frame's.
    // A frame whose pair has no PHD2 night carries no value, which is the port's equivalent of the
    // endpoint's inner join (lines 396 and 404).
    private static double? Phd2Value(JsonElement row, string metricKey)
    {
        var night = row.GetProperty("night").GetInt32();
        var rig = row.GetProperty("rig").GetInt32();

        foreach (var phd2Night in Inputs.GetProperty("phd2_nights").EnumerateArray())
        {
            if (phd2Night.GetProperty("night").GetInt32() == night
                && phd2Night.GetProperty("rig").GetInt32() == rig)
            {
                return Number(phd2Night.GetProperty(metricKey));
            }
        }

        return null;
    }

    private static JsonDocument Load(string fileName)
    {
        var path = Path.Combine(
            FindRepoRoot(), "tests", "Fixtures", "realdata", fileName);

        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GalactiLog.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("GalactiLog.sln not found above the test output directory.");
    }
}
