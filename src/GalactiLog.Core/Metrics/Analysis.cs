namespace GalactiLog.Core.Metrics;

// Port of backend/app/api/analysis.py (933 lines) and backend/app/schemas/analysis.py in the
// sibling clone ../GalactiLog at 591234b (core-shapes.md, phase17). Records only, landed by the
// records-first pre-step with no logic: the static Analysis class and its members are Task 2's.

/// <summary>The five-figure summary of one metric's values (spec 12.14). Port of
/// <c>schemas/analysis.py</c>'s <c>SummaryStats</c>, field for field, with <c>std_dev</c> renamed
/// <see cref="StdDev"/>.</summary>
/// <param name="Count">How many values the summary was built from.</param>
/// <param name="Min">The smallest value.</param>
/// <param name="Max">The largest value.</param>
/// <param name="Mean">The arithmetic mean.</param>
/// <param name="Median">The median, from <see cref="Statistics.Median(IEnumerable{double})"/>
/// (ruling A5: one median in this solution).</param>
/// <param name="StdDev">The sample standard deviation.</param>
public sealed record SummaryStats(
    int Count, double Min, double Max, double Mean, double Median, double StdDev);

/// <summary>One group's box plot figures (spec 12.14's Distributions tab, box plot mode). Port of
/// <c>schemas/analysis.py</c>'s <c>BoxPlotGroup</c>, renamed because this port has no other box
/// type.</summary>
/// <param name="GroupName">The filter, equipment, month or target name the group was built
/// from.</param>
/// <param name="Min">The lower WHISKER end, not the group's true minimum (<c>analysis.py</c> line
/// 211).</param>
/// <param name="Q1">The first quartile, from <see cref="Quartiles"/>.</param>
/// <param name="Median">The group's median.</param>
/// <param name="Q3">The third quartile, from <see cref="Quartiles"/>.</param>
/// <param name="Max">The upper WHISKER end, not the group's true maximum (<c>analysis.py</c> line
/// 212).</param>
/// <param name="Outliers">The values outside <see cref="OutlierFences"/>.</param>
/// <param name="Count">How many values the group holds, outliers included.</param>
public sealed record BoxPlot(
    string GroupName, double Min, double Q1, double Median, double Q3, double Max,
    IReadOnlyList<double> Outliers, int Count);

/// <summary>One bin of a histogram (spec 12.14's Distributions tab, histogram mode). Port of
/// <c>schemas/analysis.py</c>'s <c>HistogramBin</c>.</summary>
/// <param name="BinStart">The bin's lower edge, inclusive.</param>
/// <param name="BinEnd">The bin's upper edge. Inclusive only for the last bin, whose edge is the
/// data's own maximum under user ruling U1 (choice 19): every value is counted, where the web's
/// own accumulated edge leaves the maximum value out of every bin.</param>
/// <param name="Count">How many values fell in the bin.</param>
public sealed record HistogramBin(double BinStart, double BinEnd, int Count);

/// <summary>One point of a confidence band around a trend line. Port of
/// <c>schemas/analysis.py</c>'s <c>ConfidenceBandPoint</c>, renamed because the port has only one
/// band shape.</summary>
public sealed record BandPoint(double X, double Y);

/// <summary>A least-squares trend line with its correlation figures and confidence band (spec
/// 12.14's Correlation tab). Port of <c>schemas/analysis.py</c>'s <c>TrendLine</c>, field for
/// field.</summary>
/// <param name="Slope">The line's slope.</param>
/// <param name="Intercept">The line's intercept.</param>
/// <param name="RSquared">The coefficient of determination.</param>
/// <param name="PearsonR">Pearson's r, from <c>Analysis.PearsonR</c>.</param>
/// <param name="SpearmanRho">Spearman's rho, from <c>Analysis.SpearmanRho</c>.</param>
/// <param name="ConfidenceUpper">The band's upper points, capped at
/// <c>Analysis.ConfidenceBandMaxPoints</c>.</param>
/// <param name="ConfidenceLower">The band's lower points, capped the same way.</param>
public sealed record TrendLine(
    double Slope, double Intercept, double RSquared, double PearsonR, double SpearmanRho,
    IReadOnlyList<BandPoint> ConfidenceUpper, IReadOnlyList<BandPoint> ConfidenceLower);

/// <summary>The first and third quartiles of a value list. No web counterpart: it exists so the
/// one quartile rule (<c>analysis.py</c> lines 205, 206, 231 and 232) is written once and reused
/// by <c>Analysis.Box</c> and <c>Analysis.Fences</c> alike.</summary>
public sealed record Quartiles(double Q1, double Q3);

/// <summary>The low and high fences an outlier is measured against, 1.5 times the interquartile
/// range beyond <see cref="Quartiles.Q1"/> and <see cref="Quartiles.Q3"/>. No web
/// counterpart, for the same reason as <see cref="Quartiles"/>.</summary>
public sealed record OutlierFences(double Low, double High);

/// <summary>Why <c>Analysis.PearsonR</c> answered exactly <c>0.0</c>, when it did. Has no
/// web counterpart: it is the port's answer to defect D6 (<c>realdata-prep-report.md</c>), because
/// <c>_pearson_r</c> returns <c>0.0</c> for three different reasons, fewer than 3 points, a
/// constant X and a constant Y, and a real uncorrelated pair returns <c>0.0</c> too, so nothing
/// downstream can tell them apart and spec 12.14's states table needs to. The displayed r stays
/// the web's; this rides beside it.</summary>
public enum CorrelationQuality { Ok, TooFewPoints, ConstantX, ConstantY }
