"""Phase 17 Analysis oracle: the web's own pure functions, run over the real library's rows.

Every function and endpoint body below is lifted from
`GalactiLog/backend/app/api/analysis.py` at 591234b, line ranges cited above each block, with
two mechanical changes and no other:

1. The pydantic response models are replaced by plain attribute-carrying objects of the same
   field names, because importing the FastAPI application would drag in the database session,
   the redis cache and the auth dependency.
2. Night, target and rig are ordinals rather than a date, a target id and an equipment pair, so
   that nothing committed here names a place, a person, a rig or a target. The grouping keys and
   the group labels are built from the ordinals; the arithmetic is untouched.

The arithmetic, the ordering, the rounding and the guards are otherwise byte-for-byte the
Python's.

Input: `inputs.json` beside this file, and nothing else. No path under `C:\\tmp`, no database
and no library file is read. `inputs.json` carries the rows in the PORT's order
(`ORDER BY session_date, id`, id compared ordinally as a string) plus `export_order_index`, the
permutation that recovers the exporter's own order (`ORDER BY capture_date, id`), which is the
order every scenario without the `_port_order` suffix is computed in.

Output: `oracle.json` beside this file.

Run: py oracle.py
"""

import json
import math
import os
import statistics
import sys
from collections import defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))


# ---------------------------------------------------------------------------
# Stand-ins for the pydantic models of backend/app/schemas/analysis.py. Plain
# containers: field names and nothing else.
# ---------------------------------------------------------------------------

class Rec:
    def __init__(self, **kw):
        self.__dict__.update(kw)

    def dump(self):
        out = {}
        for k, v in self.__dict__.items():
            if isinstance(v, Rec):
                out[k] = v.dump()
            elif isinstance(v, list):
                out[k] = [x.dump() if isinstance(x, Rec) else x for x in v]
            else:
                out[k] = v
        return out


SummaryStats = BoxPlotGroup = CorrelationPoint = ConfidenceBandPoint = Rec
TrendLine = HistogramBin = MovingAveragePoint = TimeSeriesPoint = MatrixCell = Rec


# ---------------------------------------------------------------------------
# analysis.py lines 41 to 47, 80 to 98: the constants.
# ---------------------------------------------------------------------------

_CORRELATION_POINT_CAP = 5000
_PIXEL_METRICS = {"hfr"}
X_METRICS = [
    "humidity", "wind_speed", "ambient_temp", "dew_point", "pressure",
    "cloud_cover", "sky_quality", "focuser_temp", "airmass", "sensor_temp",
]
Y_METRICS = [
    "hfr", "fwhm", "eccentricity", "guiding_rms", "guiding_rms_ra",
    "guiding_rms_dec", "detected_stars", "adu_mean", "adu_median", "adu_stdev",
]
PHD2_X_METRICS = [
    "phd2_rms_total", "phd2_rms_ra", "phd2_rms_dec",
    "phd2_star_lost_pct", "phd2_snr_mean",
]
_MATRIX_MIN_POINTS = 10  # analysis.py:810

with open(os.path.join(HERE, "inputs.json"), encoding="utf-8") as _f:
    INPUTS = json.load(_f)

# analysis.py lines 51 to 74, METRIC_MAP, expressed as the images column each metric reads.
METRIC_COLUMN = INPUTS["metric_column"]


# ---------------------------------------------------------------------------
# analysis.py lines 183 to 197, verbatim.
# ---------------------------------------------------------------------------

def _compute_summary_stats(values):
    if len(values) < 2:
        return None
    s = sorted(values)
    n = len(s)
    mean = sum(s) / n
    variance = sum((v - mean) ** 2 for v in s) / (n - 1)
    return SummaryStats(
        count=n,
        min=round(s[0], 6),
        max=round(s[-1], 6),
        mean=round(mean, 6),
        median=round(statistics.median(s), 6),
        std_dev=round(math.sqrt(variance), 6),
    )


# analysis.py lines 200 to 223, verbatim.
def _compute_box_plot(values, group_name):
    if len(values) < 4:
        return None
    s = sorted(values)
    n = len(s)
    q1 = statistics.median(s[: n // 2])
    q3 = statistics.median(s[(n + 1) // 2:])
    med = statistics.median(s)
    iqr = q3 - q1
    lower_fence = q1 - 1.5 * iqr
    upper_fence = q3 + 1.5 * iqr
    whisker_low = min(v for v in s if v >= lower_fence)
    whisker_high = max(v for v in s if v <= upper_fence)
    outliers = [v for v in s if v < lower_fence or v > upper_fence]
    return BoxPlotGroup(
        group_name=group_name,
        min=round(whisker_low, 6),
        q1=round(q1, 6),
        median=round(med, 6),
        q3=round(q3, 6),
        max=round(whisker_high, 6),
        outliers=[round(v, 6) for v in outliers],
        count=n,
    )


# analysis.py lines 226 to 236, verbatim.
def _is_outlier_iqr(x, y, xs, ys):
    for vals, val in [(xs, x), (ys, y)]:
        s = sorted(vals)
        n = len(s)
        q1 = statistics.median(s[: n // 2])
        q3 = statistics.median(s[(n + 1) // 2:])
        iqr = q3 - q1
        if val < q1 - 1.5 * iqr or val > q3 + 1.5 * iqr:
            return True
    return False


# analysis.py lines 239 to 250, verbatim.
def _pearson_r(xs, ys):
    n = len(xs)
    if n < 3:
        return 0.0
    mx = sum(xs) / n
    my = sum(ys) / n
    num = sum((x - mx) * (y - my) for x, y in zip(xs, ys))
    dx = math.sqrt(sum((x - mx) ** 2 for x in xs))
    dy = math.sqrt(sum((y - my) ** 2 for y in ys))
    if dx < 1e-12 or dy < 1e-12:
        return 0.0
    return num / (dx * dy)


# analysis.py lines 253 to 274, verbatim.
def _spearman_rho(xs, ys):
    n = len(xs)
    if n < 3:
        return 0.0

    def _rank(vals):
        indexed = sorted(range(n), key=lambda i: vals[i])
        ranks = [0.0] * n
        i = 0
        while i < n:
            j = i
            while j < n - 1 and vals[indexed[j]] == vals[indexed[j + 1]]:
                j += 1
            avg_rank = (i + j) / 2.0 + 1
            for k in range(i, j + 1):
                ranks[indexed[k]] = avg_rank
            i = j + 1
        return ranks

    rx = _rank(xs)
    ry = _rank(ys)
    return _pearson_r(rx, ry)


# analysis.py lines 277 to 330, verbatim.
def _compute_trend(points):
    if len(points) < 3:
        return None
    xs = [p.x for p in points]
    ys = [p.y for p in points]
    n = len(xs)
    sum_x = sum(xs)
    sum_y = sum(ys)
    sum_xy = sum(x * y for x, y in zip(xs, ys))
    sum_x2 = sum(x * x for x in xs)

    denom = n * sum_x2 - sum_x * sum_x
    if abs(denom) < 1e-12:
        return None

    slope = (n * sum_xy - sum_x * sum_y) / denom
    intercept = (sum_y - slope * sum_x) / n

    mean_y = sum_y / n
    ss_tot = sum((y - mean_y) ** 2 for y in ys)
    ss_res = sum((y - (slope * x + intercept)) ** 2 for x, y in zip(xs, ys))
    r_squared = 1 - ss_res / ss_tot if ss_tot > 0 else 0.0

    mean_x = sum_x / n
    se = math.sqrt(ss_res / (n - 2)) if n > 2 and ss_res > 0 else 0
    t_val = 1.96 if n > 30 else 2.0

    x_sorted = sorted(xs)
    x_min, x_max = x_sorted[0], x_sorted[-1]
    n_band = min(50, n)
    x_step = (x_max - x_min) / max(n_band - 1, 1)
    band_xs = [x_min + i * x_step for i in range(n_band)]

    sx2 = sum((x - mean_x) ** 2 for x in xs)
    upper, lower = [], []
    for bx in band_xs:
        y_hat = slope * bx + intercept
        h = 1 / n + (bx - mean_x) ** 2 / sx2 if sx2 > 0 else 1 / n
        margin = t_val * se * math.sqrt(h)
        upper.append(ConfidenceBandPoint(x=round(bx, 6), y=round(y_hat + margin, 6)))
        lower.append(ConfidenceBandPoint(x=round(bx, 6), y=round(y_hat - margin, 6)))

    return TrendLine(
        slope=round(slope, 6),
        intercept=round(intercept, 6),
        r_squared=round(r_squared, 4),
        pearson_r=round(_pearson_r(xs, ys), 4),
        spearman_rho=round(_spearman_rho(xs, ys), 4),
        confidence_upper=upper,
        confidence_lower=lower,
    )


# ---------------------------------------------------------------------------
# Row access. The web reads columns off SQLAlchemy rows; here the same columns
# come off the rows of inputs.json, and the WHERE clauses each endpoint applies
# are written out as the same predicates. Every row in inputs.json is a LIGHT
# frame with a capture date, so `image_type == "LIGHT"` and
# `capture_date IS NOT NULL` are true of all of them by construction.
# ---------------------------------------------------------------------------

PHD2_INDEX = {(n["night"], n["rig"]): n for n in INPUTS["phd2_nights"]}


def port_rows():
    return list(INPUTS["rows"])


def export_rows():
    rows = INPUTS["rows"]
    return [rows[i] for i in INPUTS["export_order_index"]]


def x_value(row, x_metric):
    if x_metric in PHD2_X_METRICS:
        night = PHD2_INDEX.get((row["night"], row["rig"]))
        return None if night is None else night[x_metric]
    return row[METRIC_COLUMN[x_metric]]


def rows_for(rows, x_metric, y_metric):
    """analysis.py lines 386 to 409: the select, the LEFT JOIN on the PHD2 night
    subquery, image_type == LIGHT, x IS NOT NULL, y IS NOT NULL, capture_date IS NOT NULL."""
    ycol = METRIC_COLUMN[y_metric]
    out = []
    for f in rows:
        x = x_value(f, x_metric)
        if x is None or f[ycol] is None:
            continue
        out.append(Rec(x_val=x, y_val=f[ycol], night=f["night"], resolved_target_id=f["target"]))
    return out


# ---------------------------------------------------------------------------
# /correlation, analysis.py lines 411 to 481, lifted line for line.
# ---------------------------------------------------------------------------

def correlation(rows, x_metric, y_metric, granularity):
    rows = rows_for(rows, x_metric, y_metric)

    if granularity == "frame":
        target_ids = {r.resolved_target_id for r in rows if r.resolved_target_id}
        target_names = {t: "target %d" % t for t in target_ids}
        raw_points = [
            (float(r.x_val), float(r.y_val), str(r.night), r.resolved_target_id)
            for r in rows
        ]
    else:
        session_groups = defaultdict(lambda: {"xs": [], "ys": [], "target_id": None})
        for r in rows:
            key = (str(r.night), r.resolved_target_id)
            session_groups[key]["xs"].append(float(r.x_val))
            session_groups[key]["ys"].append(float(r.y_val))
            session_groups[key]["target_id"] = r.resolved_target_id

        target_ids = {g["target_id"] for g in session_groups.values() if g["target_id"]}
        target_names = {t: "target %d" % t for t in target_ids}
        raw_points = [
            (
                statistics.median(g["xs"]),
                statistics.median(g["ys"]),
                night,
                g["target_id"],
            )
            for (night, _), g in session_groups.items()
        ]

    all_xs = [p[0] for p in raw_points]
    all_ys = [p[1] for p in raw_points]

    points = [
        CorrelationPoint(
            x=x, y=y, date=d,
            target_id=str(tid) if tid is not None else None,
            outlier=_is_outlier_iqr(x, y, all_xs, all_ys) if len(raw_points) >= 4 else False,
        )
        for x, y, d, tid in raw_points
    ]

    trend = _compute_trend(points)
    x_stats = _compute_summary_stats(all_xs)
    y_stats = _compute_summary_stats(all_ys)

    total_count = len(points)
    if total_count > _CORRELATION_POINT_CAP:
        step = total_count / _CORRELATION_POINT_CAP
        returned_points = [points[int(i * step)] for i in range(_CORRELATION_POINT_CAP)]
    else:
        returned_points = points

    return Rec(
        points=returned_points,
        trend=trend,
        x_metric=x_metric,
        y_metric=y_metric,
        granularity=granularity,
        x_stats=x_stats,
        y_stats=y_stats,
        target_names={str(k): v for k, v in sorted(target_names.items())},
        total_count=total_count,
        sampled_count=len(returned_points),
        outlier_count=sum(1 for p in points if p.outlier),
        x_stats_full=unrounded_stats(all_xs),
        y_stats_full=unrounded_stats(all_ys),
        pearson_r_full=_pearson_r(all_xs, all_ys),
        spearman_rho_full=_spearman_rho(all_xs, all_ys),
    )


# ---------------------------------------------------------------------------
# /distribution, analysis.py lines 509 to 559, lifted line for line. The
# unrounded bin edges and the U1 count are published beside the Python's own
# figures; neither changes what the Python computes (P2-4, ruling S8).
# ---------------------------------------------------------------------------

def _distribution_over(values, metric):
    if len(values) < 2:
        return Rec(error="Not enough data points for distribution")

    stats = _compute_summary_stats(values)

    n_bins = max(1, int(math.ceil(math.log2(len(values)) + 1)))
    v_min, v_max = min(values), max(values)
    bin_width = (v_max - v_min) / n_bins if v_max > v_min else 1.0

    bins = []
    edges_full = []
    for i in range(n_bins):
        b_start = v_min + i * bin_width
        b_end = b_start + bin_width
        count = sum(1 for v in values if (b_start <= v < b_end) or (i == n_bins - 1 and v == b_end))
        bins.append(HistogramBin(bin_start=round(b_start, 6), bin_end=round(b_end, 6), count=count))
        edges_full.append({"bin_start": b_start, "bin_end": b_end})

    # U1: the last edge is v_max rather than the accumulated v_min + n * width, and only when
    # v_max > v_min (ruling S8). Everything else is unchanged, so the only observable difference
    # is the count of the last bin.
    u1_bins = []
    for i in range(n_bins):
        b_start = v_min + i * bin_width
        b_end = v_max if (i == n_bins - 1 and v_max > v_min) else b_start + bin_width
        count = sum(1 for v in values if (b_start <= v < b_end) or (i == n_bins - 1 and v == b_end))
        u1_bins.append({"bin_start": round(b_start, 6), "bin_end": round(b_end, 6), "count": count,
                        "bin_start_full": b_start, "bin_end_full": b_end})

    mean = stats.mean
    std = stats.std_dev
    n = len(values)
    if std > 0 and n > 2:
        skewness = (n / ((n - 1) * (n - 2))) * sum(((v - mean) / std) ** 3 for v in values)
    else:
        skewness = 0.0

    full = unrounded_stats(values)
    if full["std_dev"] > 0 and n > 2:
        skew_unrounded_inputs = (n / ((n - 1) * (n - 2))) * sum(
            ((v - full["mean"]) / full["std_dev"]) ** 3 for v in values)
    else:
        skew_unrounded_inputs = 0.0

    return Rec(
        bins=bins,
        stats=stats,
        metric=metric,
        skewness=round(skewness, 4),
        bin_count_sum=sum(b.count for b in bins),
        value_count=n,
        v_min=v_min,
        v_max=v_max,
        bin_width_full=bin_width,
        bin_edges_full=edges_full,
        last_edge_full=edges_full[-1]["bin_end"],
        last_edge_equals_v_max=(edges_full[-1]["bin_end"] == v_max),
        u1_bins=u1_bins,
        u1_bin_count_sum=sum(b["count"] for b in u1_bins),
        stats_full=full,
        skewness_full=skewness,
        skewness_from_unrounded_mean_and_std=skew_unrounded_inputs,
        skewness_input_note=(
            "analysis.py 546 to 550 feeds the ALREADY ROUNDED stats.mean and stats.std_dev into "
            "the cubed sum. `skewness` and `skewness_full` are that figure; "
            "`skewness_from_unrounded_mean_and_std` is what the full precision inputs would give "
            "and is NOT what the web returns."),
    )


def distribution(rows, metric, granularity):
    col = METRIC_COLUMN[metric]
    rows = [f for f in rows if f[col] is not None]

    if granularity == "session":
        groups = defaultdict(list)
        for r in rows:
            groups[(str(r["night"]), r["target"])].append(float(r[col]))
        values = [statistics.median(vs) for vs in groups.values()]
    else:
        values = [float(r[col]) for r in rows]

    return _distribution_over(values, metric)


# ---------------------------------------------------------------------------
# /boxplot, analysis.py lines 589 to 654, lifted line for line. The alias maps
# are empty on this library, so normalize_equipment and normalize_filter are the
# identity (normalization.py lines 69 to 80); the equipment key is the rig
# ordinal and the target key the target ordinal.
# ---------------------------------------------------------------------------

def boxplot(rows, metric, group_by):
    col = METRIC_COLUMN[metric]
    rows = [f for f in rows if f[col] is not None]

    grouped = defaultdict(list)
    target_id_map = {}
    for r in rows:
        if group_by == "equipment":
            key = "rig %d" % r["rig"]
        elif group_by == "filter":
            f_ = r["filter"]
            if not f_:
                continue
            key = f_
        elif group_by == "month":
            month_grp = r["capture_month"]
            if not month_grp:
                continue
            key = str(month_grp)
        else:
            if not r["target"]:
                continue
            key = str(r["target"])
            target_id_map[key] = r["target"]
        grouped[key].append(float(r[col]))

    if group_by == "target":
        resolved_groups = {}
        for key, vals in grouped.items():
            tid = target_id_map.get(key)
            name = ("target %d" % tid) if tid else key
            resolved_groups.setdefault(name, []).extend(vals)
        grouped = resolved_groups

    groups = []
    dropped = []
    for name, vals in sorted(grouped.items()):
        bp = _compute_box_plot(vals, name)
        if bp:
            groups.append(bp)
        else:
            dropped.append({"group_name": name, "count": len(vals)})

    return Rec(groups=groups, metric=metric, group_by=group_by, dropped_under_4=dropped)


# ---------------------------------------------------------------------------
# /timeseries, analysis.py lines 681 to 752, lifted line for line. The moving
# average sums the nightly medians ALREADY ROUNDED to 6 at line 716, which is
# what `raw_values = [p.value for p in points]` at line 721 reads (ruling S10).
# ---------------------------------------------------------------------------

def timeseries(rows, metric):
    col = METRIC_COLUMN[metric]
    rows = [f for f in rows if f[col] is not None]

    nightly = defaultdict(lambda: {"vals": [], "target_ids": set(), "months": set()})
    for r in rows:
        night = r["night"]
        nightly[night]["vals"].append(float(r[col]))
        nightly[night]["months"].add(r["session_month"])
        if r["target"]:
            nightly[night]["target_ids"].add(r["target"])

    sorted_nights = sorted(nightly.keys())
    points = []
    for night in sorted_nights:
        g = nightly[night]
        tid_list = list(g["target_ids"])
        target_name = ("target %d" % tid_list[0]) if tid_list else None
        points.append(TimeSeriesPoint(
            date=str(night),
            value=round(statistics.median(g["vals"]), 6),
            target_name=target_name,
            frame_count=len(g["vals"]),
        ))

    raw_values = [p.value for p in points]

    def _moving_avg(vals, window):
        result = []
        for i in range(len(vals)):
            start = max(0, i - window + 1)
            chunk = vals[start: i + 1]
            if len(chunk) >= window:
                result.append(MovingAveragePoint(
                    date=points[i].date,
                    value=round(sum(chunk) / len(chunk), 6),
                ))
        return result

    ma_7 = _moving_avg(raw_values, 7)
    ma_30 = _moving_avg(raw_values, 30)

    months_seen = set()
    month_boundaries = []
    for night in sorted_nights:
        ym = sorted(nightly[night]["months"])[0]
        if ym not in months_seen:
            months_seen.add(ym)
            month_boundaries.append(str(night))

    return Rec(
        points=points,
        ma_7=ma_7,
        ma_30=ma_30,
        metric=metric,
        month_boundaries=month_boundaries,
        multi_target_nights=[str(n) for n in sorted_nights if len(nightly[n]["target_ids"]) > 1],
        nightly_medians_unrounded=[statistics.median(nightly[n]["vals"]) for n in sorted_nights],
        moving_average_input=(
            "the nightly medians ALREADY rounded to 6 (analysis.py 716, read at 721), not the "
            "unrounded medians published beside them"),
        distinct_plate_scales=sorted({r["arcsec_per_pixel"] for r in rows
                                      if r["arcsec_per_pixel"] is not None}),
    )


# ---------------------------------------------------------------------------
# /matrix, analysis.py lines 776 to 815. PostgreSQL's corr(Y, X) has no Python
# body to lift, so the cell is the mean-centred sample Pearson over the rows
# where both columns are non-null, which is the form current PostgreSQL computes
# and the form `MatrixPearson` implements (ruling S3). It answers no value when
# either axis is constant, tested as min equals max on that axis BEFORE any
# arithmetic so the gate cannot depend on which literal the constant is, and
# when the pair count is below 10 (analysis.py:810). `_pearson_r`'s answer on
# the same pair is recorded beside it as the secondary field.
# ---------------------------------------------------------------------------

def _pg_corr(xs, ys):
    n = len(xs)
    if n < _MATRIX_MIN_POINTS:
        return None
    if min(xs) == max(xs) or min(ys) == max(ys):
        return None
    mx = sum(xs) / n
    my = sum(ys) / n
    sxy = sum((x - mx) * (y - my) for x, y in zip(xs, ys))
    sxx = sum((x - mx) ** 2 for x in xs)
    syy = sum((y - my) ** 2 for y in ys)
    if sxx == 0.0 or syy == 0.0:
        return None
    return sxy / math.sqrt(sxx * syy)


def matrix(rows, x_metrics, y_metrics):
    cells = []
    for xm in x_metrics:
        for ym in y_metrics:
            ycol = METRIC_COLUMN[ym]
            xs, ys = [], []
            for f in rows:
                x = x_value(f, xm)
                y = f[ycol]
                if x is not None and y is not None:
                    xs.append(float(x))
                    ys.append(float(y))
            n_points = len(xs)
            r_val = _pg_corr(xs, ys)
            cells.append(MatrixCell(
                x_metric=xm, y_metric=ym,
                pearson_r=(round(float(r_val), 4) if r_val is not None else None),
                n_points=n_points,
                r_full=r_val,
                r_via_pearson_r_helper=(round(_pearson_r(xs, ys), 4) if xs else None),
            ))
    return Rec(cells=cells, x_metrics=x_metrics, y_metrics=y_metrics)


# ---------------------------------------------------------------------------
# /compare, analysis.py lines 835 to 933, lifted line for line. `group` is the
# rig ordinal as a string in equipment mode and the filter name in filter mode;
# the web's "tel|||cam" split becomes the one ordinal, which changes no
# arithmetic. The U2 percentage, which divides by the LARGER median, is
# published beside the web's own.
# ---------------------------------------------------------------------------

def compare(rows, metric, mode, group_a, group_b):
    col = METRIC_COLUMN[metric]
    is_pixel_metric = metric in _PIXEL_METRICS

    def _fetch_values(group):
        selected = [f for f in rows if f[col] is not None]
        if mode == "equipment":
            selected = [f for f in selected if str(f["rig"]) == group]
        else:
            selected = [f for f in selected if f["filter"] == group]

        vals = [float(f[col]) for f in selected]
        arcsec_vals = [
            float(f[col]) * float(f["arcsec_per_pixel"])
            for f in selected
            if f["arcsec_per_pixel"] is not None
        ] if is_pixel_metric else []
        scales = sorted({f["arcsec_per_pixel"] for f in selected
                         if f["arcsec_per_pixel"] is not None})
        return vals, arcsec_vals, scales

    vals_a, arcsec_a, scales_a = _fetch_values(group_a)
    vals_b, arcsec_b, scales_b = _fetch_values(group_b)

    if len(vals_a) < 4 or len(vals_b) < 4:
        return Rec(error="Not enough data in one or both groups (need at least 4 points each)",
                   count_a=len(vals_a), count_b=len(vals_b))

    box_a = _compute_box_plot(vals_a, group_a)
    box_b = _compute_box_plot(vals_b, group_b)
    stats_a = _compute_summary_stats(vals_a)
    stats_b = _compute_summary_stats(vals_b)

    def _pct(med_a, med_b):
        if med_a != 0:
            return abs(med_a - med_b) / abs(med_a) * 100
        elif med_b != 0:
            return abs(med_a - med_b) / abs(med_b) * 100
        return 0

    def _pct_u2(med_a, med_b):
        """U2: the percentage the sentence claims, which divides by the LARGER median."""
        bigger = max(abs(med_a), abs(med_b))
        return abs(med_a - med_b) / bigger * 100 if bigger != 0 else 0

    def _pct_verdict(med_a, med_b, unit=""):
        pct_diff = _pct(med_a, med_b)
        if med_a < med_b:
            return (f"{group_a} has {pct_diff:.0f}% lower median{unit} than {group_b} "
                    f"(N={stats_a.count} vs N={stats_b.count})")
        elif med_b < med_a:
            return (f"{group_b} has {pct_diff:.0f}% lower median{unit} than {group_a} "
                    f"(N={stats_b.count} vs N={stats_a.count})")
        return f"Both groups have identical median values (N={stats_a.count} vs N={stats_b.count})"

    comparable = True
    median_arcsec_a = None
    median_arcsec_b = None
    if is_pixel_metric:
        if len(arcsec_a) >= 4 and len(arcsec_b) >= 4:
            median_arcsec_a = round(statistics.median(arcsec_a), 3)
            median_arcsec_b = round(statistics.median(arcsec_b), 3)
            verdict = _pct_verdict(median_arcsec_a, median_arcsec_b, unit=" (arcsec)")
            pct_web = _pct(median_arcsec_a, median_arcsec_b)
            pct_u2 = _pct_u2(median_arcsec_a, median_arcsec_b)
        else:
            comparable = False
            pct_web = pct_u2 = None
            verdict = (
                f"{group_a} and {group_b} cannot be compared: {metric} is measured in pixels "
                "and one or both groups lack the plate-scale headers (XPIXSZ/FOCALLEN) "
                "needed to convert to arcseconds"
            )
    else:
        verdict = _pct_verdict(stats_a.median, stats_b.median)
        pct_web = _pct(stats_a.median, stats_b.median)
        pct_u2 = _pct_u2(stats_a.median, stats_b.median)

    return Rec(
        group_a=Rec(name=group_a, box=box_a, stats=stats_a),
        group_b=Rec(name=group_b, box=box_b, stats=stats_b),
        metric=metric,
        mode=mode,
        verdict=verdict,
        comparable=comparable,
        median_hfr_arcsec_a=median_arcsec_a,
        median_hfr_arcsec_b=median_arcsec_b,
        median_arcsec_a_full=(statistics.median(arcsec_a) if arcsec_a else None),
        median_arcsec_b_full=(statistics.median(arcsec_b) if arcsec_b else None),
        pct_web=pct_web,
        pct_web_rounded=(None if pct_web is None else float(f"{pct_web:.0f}")),
        pct_u2=pct_u2,
        pct_u2_rounded=(None if pct_u2 is None else float(f"{pct_u2:.0f}")),
        pct_note=(
            "pct_web is analysis.py 890 to 896, which divides by med_a whenever med_a is non-zero "
            "and can exceed 100 for a sentence that says 'lower'. pct_u2 divides by the larger "
            "median, which is the figure the sentence claims."),
        distinct_plate_scales_a=scales_a,
        distinct_plate_scales_b=scales_b,
    )


# ---------------------------------------------------------------------------
# Full precision beside the rounded figures.
# ---------------------------------------------------------------------------

def unrounded_stats(values):
    if len(values) < 2:
        return None
    s = sorted(values)
    n = len(s)
    mean = sum(s) / n
    variance = sum((v - mean) ** 2 for v in s) / (n - 1)
    return {
        "count": n, "min": s[0], "max": s[-1], "mean": mean,
        "median": statistics.median(s), "std_dev": math.sqrt(variance),
    }


# ---------------------------------------------------------------------------
# Scenario registry. Every row-order dependent scenario is computed twice: once
# over the exporter's order, published under its own name, and once over the
# port's order, published under the same name with `_port_order` appended and
# carrying its own agreement verdict.
# ---------------------------------------------------------------------------

SCENARIOS = {
    "correlation_humidity_hfr_frame": lambda r: correlation(r, "humidity", "hfr", "frame"),
    "correlation_humidity_hfr_session": lambda r: correlation(r, "humidity", "hfr", "session"),
    "correlation_phd2_rms_total_hfr_frame":
        lambda r: correlation(r, "phd2_rms_total", "hfr", "frame"),
    "distribution_hfr_frame": lambda r: distribution(r, "hfr", "frame"),
    "distribution_hfr_session": lambda r: distribution(r, "hfr", "session"),
    "boxplot_hfr_by_filter": lambda r: boxplot(r, "hfr", "filter"),
    "boxplot_hfr_by_equipment": lambda r: boxplot(r, "hfr", "equipment"),
    "boxplot_hfr_by_month": lambda r: boxplot(r, "hfr", "month"),
    "boxplot_hfr_by_target": lambda r: boxplot(r, "hfr", "target"),
    "timeseries_hfr": lambda r: timeseries(r, "hfr"),
    "timeseries_humidity": lambda r: timeseries(r, "humidity"),
    "matrix_10x10": lambda r: matrix(r, X_METRICS, Y_METRICS),
    "matrix_15x10_not_shipped": lambda r: matrix(r, X_METRICS + PHD2_X_METRICS, Y_METRICS),
    "compare_two_rigs_hfr": lambda r: compare(r, "hfr", "equipment", "1", "2"),
    "compare_two_rigs_fwhm": lambda r: compare(r, "fwhm", "equipment", "1", "2"),
    "compare_ha_oiii_hfr": lambda r: compare(r, "hfr", "filter", "Ha", "Oiii"),
}


def diff_paths(a, b, path=""):
    """Every leaf path at which two dumps differ."""
    out = []
    if isinstance(a, dict) and isinstance(b, dict):
        for k in sorted(set(a) | set(b)):
            out += diff_paths(a.get(k), b.get(k), f"{path}.{k}" if path else str(k))
    elif isinstance(a, list) and isinstance(b, list):
        if len(a) != len(b):
            out.append(f"{path}[len {len(a)} vs {len(b)}]")
        else:
            for i, (x, y) in enumerate(zip(a, b)):
                out += diff_paths(x, y, f"{path}[{i}]")
    elif a != b:
        out.append(f"{path}: {a!r} vs {b!r}")
    return out


def trim(name, d, points_key="points"):
    """Keep the file readable: full point arrays only where they are short."""
    if points_key in d and isinstance(d[points_key], list) and len(d[points_key]) > 12:
        d[points_key + "_sample_first_5"] = d[points_key][:5]
        d[points_key + "_sample_last_5"] = d[points_key][-5:]
        del d[points_key]
    return d


def main():
    ex = export_rows()
    po = port_rows()
    out = {
        "python_version": sys.version,
        "source": "GalactiLog/backend/app/api/analysis.py at 591234b",
        "inputs": "inputs.json beside this file; nothing under C:/tmp is read",
        "input": {
            "frames": len(po),
            "phd2_night_rows": len(INPUTS["phd2_nights"]),
            "targets": len(INPUTS["ordinals"]["target"]),
            "nights": len(INPUTS["ordinals"]["night"]),
            "rigs": len(INPUTS["ordinals"]["rig"]),
            "export_row_order": "capture_date, id",
            "port_row_order": INPUTS["row_order"],
        },
        "order_agreement": {},
        "scenarios": {},
    }
    s = out["scenarios"]

    for name, fn in SCENARIOS.items():
        a_full = fn(ex).dump()
        b_full = fn(po).dump()

        # A differing path under `points` is the point SEQUENCE, which the row order decides by
        # construction. Every other path is a figure, and a figure that differs is a figure the
        # row order changed.
        differences = [d for d in diff_paths(a_full, b_full) if not d.startswith("points")]
        seq = diff_paths(a_full.get("points"), b_full.get("points"))
        if isinstance(a_full.get("points"), list):
            def key(p):
                return json.dumps(p, sort_keys=True)
            permutation = sorted(a_full["points"], key=key) == sorted(b_full["points"], key=key)
        else:
            permutation = None

        a = trim(name, a_full)
        b = trim(name, b_full)
        a["computed_over"] = "export order: ORDER BY capture_date, id"
        b["computed_over"] = "port order: ORDER BY session_date, id"
        b["agrees_with_export_order"] = not differences
        b["differing_figures"] = differences
        b["point_sequence_differs"] = bool(seq)
        b["points_are_the_same_multiset"] = permutation
        s[name] = a
        s[name + "_port_order"] = b
        out["order_agreement"][name] = {
            "agrees": not differences,
            "differing_figure_count": len(differences),
            "differing_figures": differences,
            "point_sequence_differs": bool(seq),
            "points_are_the_same_multiset": permutation,
        }

    # Constructed scenarios. No row order to vary, so each is computed once.
    constants = [12.7, 3.14159, 1013.2, 999.99, 0.3, -10.0]
    ys = [1.0 + 0.01 * i for i in range(40)]
    const_cells = []
    for c in constants:
        xs = [c] * 40
        const_cells.append({
            "constant": c,
            "n_points": 40,
            "pg_corr": _pg_corr(xs, ys),
            "pg_corr_axis_swapped": _pg_corr(ys, xs),
            "pearson_r_helper": _pearson_r(xs, ys),
            "sum_over_n_recovers_the_constant": (sum(xs) / len(xs)) == c,
            "sxx_mean_centred": sum((x - sum(xs) / len(xs)) ** 2 for x in xs),
            "sxx_computational_form": sum(x * x for x in xs) - sum(xs) ** 2 / len(xs),
        })
    s["matrix_constant_column"] = {
        "note": (
            "Ruling S3: the cell answers no value when either axis is constant, tested as min "
            "equals max BEFORE any arithmetic, so the gate cannot depend on which literal the "
            "constant is. Every row below answers None on both axes. The two Sxx fields are why "
            "the gate is a min-equals-max test and not a zero test on an accumulation: the "
            "mean-centred Sxx recovers 0.0 for every literal, while the computational form "
            "sum(x*x) - sum(x)^2/n does not, so a gate built on the computational form would "
            "answer differently depending on which constant the column holds."),
        "y_values": "40 points, 1.0 stepping by 0.01",
        "cells": const_cells,
        "all_none": all(c["pg_corr"] is None and c["pg_corr_axis_swapped"] is None
                        for c in const_cells),
    }
    s["matrix_below_minimum_points"] = {
        "note": "analysis.py:810, fewer than 10 paired points answers no value.",
        "n_9": _pg_corr([float(i) for i in range(9)], [float(i * 2) for i in range(9)]),
        "n_10": _pg_corr([float(i) for i in range(10)], [float(i * 2) for i in range(10)]),
    }

    const_hist = _distribution_over([3.5] * 10, "constructed_constant_metric").dump()
    const_hist["note"] = (
        "Ruling S8: v_max equals v_min, so bin_width is the Python's literal 1.0 and U1's "
        "last-edge rule does not apply. The Python's own shape is kept: 5 bins of width 1.0 from "
        "3.5, every value in the first, and the last bin's start above every value.")
    s["distribution_constant_metric"] = const_hist

    with open(os.path.join(HERE, "oracle.json"), "w", encoding="utf-8") as f:
        json.dump(out, f, indent=1)

    agree = sum(1 for v in out["order_agreement"].values() if v["agrees"])
    print("scenarios", len(s))
    print("row-order pairs", len(out["order_agreement"]), "agreeing", agree)
    for k, v in out["order_agreement"].items():
        if not v["agrees"]:
            print("  differs:", k, v["differing_figure_count"], "figures")


if __name__ == "__main__":
    main()
