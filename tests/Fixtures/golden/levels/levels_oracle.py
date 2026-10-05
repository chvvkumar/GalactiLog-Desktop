"""Phase 16 Task 2a oracle: the real wbpp_export.py, run on the port's own case inputs.

Writes levels.json beside this file. The port's FolderLevelsTests reads that file and compares
its own answers after mapping the POSIX inputs to Windows paths ("/root" for "D:\\Astro").

Reproducibility: RUN WITH PYTHONHASHSEED=0. compute_session_levels depends on the hash seed at TWO
sites, not one. The first is the level order: it sorts a set by slash count alone, so the order of
equal-depth siblings follows the seed. The second is current_target, drawn with next() from the
occupant SET of sorted_ancestors[-1], so a deepest folder occupied by two targets on the session
date can make the Python name the session's own target as a contaminant. Every case here is
single-leaf (every depth holds exactly one folder) AND has a single occupant in its deepest folder,
so neither site can show; the variable is set anyway so a re-run is byte identical. The port's
ruled departure 4, the current target as an argument, is what removes the second site for good.

Not covered by this oracle, and not covered by anything else either:
  - the byte total, because ruling R4 replaces the quantity subtree_bytes computes;
  - the multi-leaf level order, the UNC and drive roots, the case-only difference and ruling R6's
    distinctness pass, which are all the port's own (task2.md section 6).

Usage, from the repository root:
    PYTHONHASHSEED=0 py docs/superpowers/work/phase16/golden/levels/levels_oracle.py
"""
import importlib.util
import json
import os
import pathlib
import sys

WEB = r"C:\Users\Challa\git\GalactiLog\backend\app\services\wbpp_export.py"

if os.environ.get("PYTHONHASHSEED") != "0":
    sys.exit("Set PYTHONHASHSEED=0 in the environment of the run.")

spec = importlib.util.spec_from_file_location("wbpp_export", WEB)
wbpp = importlib.util.module_from_spec(spec)
spec.loader.exec_module(wbpp)

ROOT = "/root"


def levels_case(name, session_date, files, catalogue):
    """One compute_session_levels + pick_default_level answer.

    `catalogue` maps (target name, date string) to the container paths that pair owns; it is the
    Python's all_paths_by_target_date. library_root is the fits root and target_os is posix, so
    FolderLevel.path comes back as the container path itself and no translation shows.
    """
    levels = wbpp.compute_session_levels(
        session_date, files, catalogue, ROOT, ROOT, "posix", None
    )
    return {
        "name": name,
        "session_date": session_date,
        "files": files,
        # The catalogue flattened one row per file, so the port can rebuild its own index from the
        # same input this run used. The target name doubles as the target key in these cases.
        "catalogue": [
            {"path": p, "target": tname, "date": dstr}
            for (tname, dstr), tpaths in catalogue.items()
            for p in tpaths
        ],
        "levels": [
            {
                "path": lv.path,
                "depth_from_root": lv.depth_from_root,
                "frame_count": lv.frame_count,
                "other_targets": lv.other_targets,
                "other_dates": lv.other_dates,
                "is_contaminated": lv.is_contaminated,
            }
            for lv in levels
        ],
        "default_level_index": wbpp.pick_default_level(levels),
    }


def staging_case(name, paths, dates):
    return {
        "name": name,
        "paths": paths,
        "dates": dates,
        "names": wbpp.disambiguate_staging_names(paths, dates),
    }


def chain_case(path):
    return {"path": path, "chain": wbpp.compute_ancestor_chain(path, ROOT)}


# Case A: one night, one leaf, no other occupant. The plain shape of section 7.1.
a_files = [f"{ROOT}/M31/2026-07-01/Ha/a.fits", f"{ROOT}/M31/2026-07-01/Ha/b.fits"]
case_a = levels_case("plain_single_leaf", "2026-07-01", a_files, {("M31", "2026-07-01"): a_files})

# Case B: the fixture's own shape. M 31's night 3 is the session; M 33 shares the 2025-03-20 and
# LIGHT folders, and M 31's other two nights share the M 31 folder. Section 7.3 and the fixture
# README's night 3 table.
n1 = [f"{ROOT}/M 31/2025-01-10/LIGHT/Ha/n1_{i}.fits" for i in range(10)]
n2 = [f"{ROOT}/M 31/2025-02-14/LIGHT/OIII/n2_{i}.fits" for i in range(8)]
n3 = [f"{ROOT}/M 31/2025-03-20/LIGHT/Ha/n3_{i}.fits" for i in range(22)]
m33 = [f"{ROOT}/M 31/2025-03-20/LIGHT/M33 $secondary/m33_{i}.fits" for i in range(6)]
fixture_catalogue = {
    ("M 31", "2025-01-10"): n1,
    ("M 31", "2025-02-14"): n2,
    ("M 31", "2025-03-20"): n3,
    ("M 33", "2025-03-20"): m33,
}
case_b = levels_case("fixture_night3", "2025-03-20", n3, fixture_catalogue)
case_b2 = levels_case("fixture_m33", "2025-03-20", m33, fixture_catalogue)
case_b3 = levels_case("fixture_night1", "2025-01-10", n1, fixture_catalogue)

# Case C: the shallowest level is contaminated, the level below it is clean and holds every frame,
# and the deepest holds fewer. Section 7.4's first pick. Still single-leaf: each depth holds one
# folder, because the second frame sits directly in the date folder.
c_files = [f"{ROOT}/M31/2026-07-01/loose.fits", f"{ROOT}/M31/2026-07-01/Ha/a.fits"]
c_other = [f"{ROOT}/M31/2026-06-01/other.fits"]
case_c = levels_case(
    "default_pick_middle",
    "2026-07-01",
    c_files,
    {("M31", "2026-07-01"): c_files, ("NGC 7000", "2026-06-01"): c_other},
)

# Case D: every level holding every frame is contaminated, so the contaminated fallback fires and
# the pick is still the deepest level holding every frame. Section 7.4's second pick.
d_other = [f"{ROOT}/M31/2026-07-01/other.fits"]
case_d = levels_case(
    "default_pick_all_contaminated",
    "2026-07-01",
    c_files,
    {("M31", "2026-07-01"): c_files, ("NGC 7000", "2026-06-01"): d_other},
)

staging = [
    staging_case("both_collide", ["/a/M31", "/b/M31"], ["2026-07-01", "2026-07-02"]),
    staging_case("no_collision", ["/a/M31", "/b/NGC 7000"], ["2026-07-01", "2026-07-02"]),
    staging_case(
        "two_of_three_collide",
        ["/a/Ha", "/b/OIII", "/c/Ha"],
        ["2026-07-01", "2026-07-02", "2026-07-03"],
    ),
    staging_case(
        "fixture_two_ha_leaves",
        [
            f"{ROOT}/M 31/2025-01-10/LIGHT/Ha",
            f"{ROOT}/M 31/2025-02-14/LIGHT/OIII",
            f"{ROOT}/M 31/2025-03-20/LIGHT/Ha",
            f"{ROOT}/M 31/2025-03-20/LIGHT/M33 $secondary",
        ],
        ["2025-01-10", "2025-02-14", "2025-03-20", "2025-03-20"],
    ),
    # Evidence only, not a parity golden. The port departs on both of these.
    # R6: the date prefix collides with a folder already named that way, and the Python returns
    # two identical names.
    staging_case(
        "r6_prefix_collides_python_answer",
        ["/a/Ha", "/b/Ha", "/c/2026-07-01_Ha"],
        ["2026-07-01", "2026-07-02", "2026-07-03"],
    ),
    # Q4: the Python counts case sensitively, so Ha and HA are not seen as a collision at all.
    staging_case("q4_case_only_python_answer", ["/a/Ha", "/b/HA"], ["2026-07-01", "2026-07-02"]),
]

out = {
    "source": WEB,
    "pythonhashseed": os.environ["PYTHONHASHSEED"],
    "python_version": sys.version,
    "root": ROOT,
    "chains": [
        chain_case(f"{ROOT}/M31/2026-07-01/Ha/a.fits"),
        chain_case(f"{ROOT}/a.fits"),
    ],
    "sessions": [case_a, case_b, case_b2, case_b3, case_c, case_d],
    "staging": staging,
}

path = pathlib.Path(__file__).with_name("levels.json")
path.write_text(json.dumps(out, indent=2) + "\n", encoding="utf-8")
print(f"wrote {path}")
