using System.Diagnostics.CodeAnalysis;

namespace GalactiLog.Core.Help;

/// <summary>
/// Spec 12.12's contextual help table: one row per topic id, seeded verbatim from that
/// section's two tables and shipped in source rather than in a resource file or a database
/// row, so a paragraph that disagrees with the behaviour is a source change reviewed with the
/// behaviour.
/// </summary>
/// <remarks>
/// <para>
/// It lives in Core and references no toolkit, so the CLI and the tests read it without
/// Avalonia. Nothing here formats, colours or measures anything.
/// </para>
/// <para>
/// Lookup is ordinal and a missing id throws rather than rendering an empty panel, which is
/// safe because <c>HelpPlacementCensusTest</c> asserts that every id in this table is named by
/// exactly one <c>HelpButton</c> in markup and that no <c>HelpButton</c> names an id that is not
/// here. The census is what keeps a build from reaching the throw, and the throw is what makes
/// the census load bearing.
/// </para>
/// <para>
/// Line breaks inside a paragraph are not significant (spec 12.12), so the longer paragraphs
/// below are wrapped across source lines and joined with single spaces. The resulting string
/// is the spec's text exactly; <c>HelpTopicsTests</c> asserts that for the longest of them
/// against a literal rather than trusting the eye.
/// </para>
/// </remarks>
public static class HelpTopics
{
    /// <summary>Every topic, in spec 12.12's table order.</summary>
    public static IReadOnlyList<HelpTopic> All { get; } =
    [
        new("page.dashboard", "Dashboard",
            "Every target GalactiLog has catalogued, one row each, with the integration, frame count, "
            + "filters and equipment it has recorded. The filter panel on the left narrows the list; "
            + "the column picker chooses which figures the rows carry. Opening a row opens that "
            + "target's detail page."),

        new("page.statistics", "Statistics",
            "Aggregate figures across the whole library, in ten sections: overview, equipment "
            + "performance, guiding, equipment inventory, filter usage, top targets, timeline or "
            + "calendar, data quality, storage and ingest history. Open the glyph beside a section for "
            + "what that section counts."),

        // Phase 17 Task 7. The sixth rail destination, placed after Statistics and before
        // Activity (spec 12.14), so it sits here in the page.* group rather than at the end.
        new("page.analysis", "Analysis",
            "Explores relationships and trends across the frames GalactiLog has catalogued. Scope "
            + "is set once in the Shared filters card, and four of the five tabs below are views of "
            + "that one slice: Correlation as a scatter of two metrics, Distributions as a histogram "
            + "or box plot of one, Time Series as a nightly median plotted over time, and Matrix as a "
            + "grid of Pearson correlations between the X and Y metrics. Compare is the exception: it "
            + "picks its own two groups and takes only the shared date range, which bounds the "
            + "imaging night, then places them side by side."),

        new("page.activity", "Activity",
            "Everything the application did, newest first: scans, merges, renames, maintenance "
            + "actions and their failures. A scan row expands to its child events. The list is bounded "
            + "by the activity retention window, so an event older than that window is gone."),

        new("page.diagnostics", "Diagnostics",
            "The state of this installation: the database, the last scan and the watchers, the "
            + "resolver cache, unresolved names, recent errors, versions and paths. Nothing here "
            + "changes anything. The Export JSON bundle button writes all of it to one file for a bug "
            + "report."),

        new("page.settings", "Settings",
            "Settings configure how GalactiLog catalogues and displays your FITS data, and each tab "
            + "covers one area. Open the glyph beside any section heading inside a tab for what that "
            + "section does. Changes are written to this profile's settings document as you make them."),

        new("page.target", "Target detail",
            "Shows the identity line and the Details panel with the catalogue record, the log line "
            + "with the totals, and the nights ledger. The page is one layout, Question Modes, with a "
            + "mode switch under the header: Night review, Compare nights, and Integration. The "
            + "ledger is a sidebar: drag the handle on its right edge, or press Left and Right on it, "
            + "to set its width, and the chevron in its header collapses it to the check boxes and "
            + "the dates. Both are kept per profile."),

        new("page.preview", "File preview",
            "A rendered view of one frame, at the configured preview resolution. The wheel zooms, a "
            + "drag pans, and a double-click or the 0 key fits. The left and right arrows step through "
            + "the frames of the list this preview was opened from. The metadata strip under the frame "
            + "name carries that frame's filter, exposure and graded metrics, and it updates on every "
            + "step. Render full preview on navigation is on by default, so every step renders a fresh "
            + "full-resolution preview; turned off, stepping shows each frame's cached thumbnail, "
            + "which is faster across a few hundred frames."),

        new("page.sky-view", "Sky view",
            "A still image from the CDS hips2fits service, centred on this target. Drag to pan, "
            + "the wheel or the plus and minus keys to zoom, over a combo box of five surveys. "
            + "Reset returns to the target's own centre and field, and Refresh re-fetches the "
            + "current view. Images are fetched from alasky.cds.unistra.fr only while Survey "
            + "downloads is on."),

        new("page.merge", "Preview merge",
            "A preview of what merging two targets does before it does it: which target survives, "
            + "which is merged away, and how many frames, sessions and aliases move. A merge is "
            + "recorded and can be undone from the merge history."),

        new("page.frame-list", "Copy frame list",
            "Copies the file list for the nights checked in the ledger. Good copies the frames the "
            + "grading did not reject and Bad copies the frames it did. A frame with no recorded "
            + "quality data counts as unmeasured: it is never bad, and it joins the good list only "
            + "while Include unmeasured is checked. Absolute paths gives one full path per line, Names "
            + "gives one bare file name per line, and Explorer search gives a string to paste into the "
            + "Windows Explorer search box, which works best up to a few dozen files. This dialog "
            + "writes the clipboard and nothing else; it moves, renames and deletes no file."),

        new("page.export-stacking", "Export for stacking",
            "Pick, for each checked night, which folder gets copied, then optionally filter which "
            + "light frames go with it, choose the staging folder the copy lands in, and either let "
            + "GalactiLog copy the folders now or generate a script and run it yourself. Nothing is "
            + "written until you commit on the review step. GalactiLog only creates new files in the "
            + "staging folder; nothing in your library is ever written, moved or deleted."),

        new("setup.scan-folders", "Scan folders",
            "The folders GalactiLog walks looking for FITS files. Each folder is its own boundary, so "
            + "one folder may not sit inside another. The count beside the list is a shallow probe "
            + "rather than a full walk, so it is an indication rather than a total. Folders to skip are "
            + "configured later under Settings, Library, in the exclude paths and name rules."),

        new("setup.storage", "Storage locations",
            "Where this installation keeps its own files: the data location holds the database, the "
            + "logs and the settings, and the thumbnail cache holds generated thumbnails and previews. "
            + "A change to the data location takes effect the next time GalactiLog starts and deletes "
            + "nothing from the old location. Neither path is inside your FITS library, and nothing is "
            + "ever written there."),

        new("setup.observer", "Observer location",
            "Your site latitude and longitude in decimal degrees, positive north and east, and the "
            + "timezone your clock is set to. They are used as a fallback when a FITS header carries no "
            + "site coordinates, to compute darkness hours and target altitude, and to compute the "
            + "local noon boundary that imaging night grouping uses. Without a longitude that boundary "
            + "falls back to UTC midnight, which splits most nights in two."),

        new("setup.scan-options", "Scan options",
            "How often GalactiLog re-walks your library on its own, and whether it catalogues "
            + "calibration frames alongside light frames. A shorter interval picks up new frames sooner "
            + "at the cost of more disk activity; a large library on a network share is happier with a "
            + "longer one. Each scan reads only files that are new or changed since the last run."),

        new("setup.first-scan", "First scan",
            "The first scan reads every file it finds, so it takes far longer than later scans, which "
            + "pick up only new or changed files. Everything configured in this wizard is already "
            + "saved, so starting the scan later from Settings loses nothing."),

        new("dashboard.filters", "Filters",
            "Narrows the target list. Text matches the name and the aliases; the date range matches "
            + "the session dates; the metric ranges are applied per frame, so a target is shown only "
            + "when it has a frame inside every range you set, not when its average falls inside them. "
            + "Clearing a filter widens the list again; nothing is deleted."),

        new("dashboard.targets", "Targets",
            "One row per target, or per unresolved OBJECT name where no target could be resolved. "
            + "Click a column header to sort by it, click again to reverse. The column picker chooses "
            + "which figures the rows carry, and the Name column cannot be hidden. Expanding a row "
            + "lists that target's nights."),

        // The filter block for the user's own columns. It carries the two facts about clearing a
        // value, because this is the topic where those values are already the subject.
        new("dashboard.custom", "Custom",
            "Narrows the list by the values you have filled in yourself. Checkbox columns offer Any, "
            + "Yes or No; text columns match anything containing what you type; list columns match "
            + "one exact choice. Clearing a text value removes it entirely, and choosing \"Not set\" "
            + "on a list column clears its value the same way, so a cleared value filters exactly "
            + "like one that was never filled in. A column that belongs to a night or to a rig still "
            + "shows the whole target: the answer is \"this target has at least one night like "
            + "that\", and the list never hides part of a target. This block appears only once you "
            + "have defined a column of your own."),

        new("stats.overview", "Overview",
            "Library-wide totals: integration time, frame count, catalogued bytes, the span between "
            + "the first and last capture, the average rig-session length and the average integration "
            + "per target. Catalogued bytes is the sum of the file sizes recorded in the database, not "
            + "a measurement of your disk."),

        new("stats.performance", "Equipment performance",
            "Metric summaries per telescope and camera combination, with a per-filter breakdown under "
            + "each. HFR is measured in pixels and is comparable only within one optical train; use "
            + "FWHM in arcseconds to compare across telescopes. The MAD columns state the spread, so a "
            + "median with a wide MAD is a less settled figure than the same median with a narrow one."),

        new("stats.guiding", "Guiding",
            "Each rig here is the telescope your PHD2 profile is mapped to, through the same "
            + "equipment alias map the rest of the application uses, so two spellings of one scope "
            + "become one row and two cameras sharing one telescope share that row too. A session "
            + "whose profile is mapped to no telescope belongs to no rig: it is counted in the "
            + "unmapped tally and appears on neither card. The coloured cells compare each rig's "
            + "figure against the middle of your rigs, and that comparison refuses to judge "
            + "anything until it has at least eight values to compare. Here a value is one rig, so it "
            + "means eight telescopes: a library with one, two or seven rigs sees plain uncoloured "
            + "figures, and with one rig every cell is neutral. The figures themselves are correct "
            + "and comparable either way; only the colour is withheld. The frame table's own grading "
            + "counts differently: there a group is a rig and a filter, a sample is a frame, and "
            + "eight samples per group is enough. This table is not that table. The altitude card "
            + "reads each session's altitude from the pointing line PHD2 wrote in its own log, so it "
            + "needs no observer coordinates and works on a library that has configured none. The "
            + "table beneath the arcs lists the same rows the arcs draw, one row per rig and altitude "
            + "band, for a reader who wants the figures rather than the shapes."),

        new("stats.inventory", "Equipment inventory",
            "Every camera and telescope seen in the library, with the frames, nights, targets and "
            + "integration recorded against each. A name marked as grouped is a canonical name standing "
            + "for several raw header spellings, which are configured on the Equipment settings tab."),

        new("stats.filter-usage", "Filter usage",
            "Integration time per canonical filter, one bar each, tinted with that filter's "
            + "configured colour. It counts light frames only, and an alias mapped to a canonical "
            + "filter is counted under the canonical name."),

        new("stats.top-targets", "Top targets",
            "The targets with the most integration time, ranked. It counts light frames only and it "
            + "counts every night, so a target imaged briefly over many nights can outrank one imaged "
            + "hard on a single night."),

        new("stats.timeline", "Timeline and calendar",
            "When imaging happened. Timeline is a chronological bar per month, week or day; Calendar "
            + "is a grid of nights shaded by integration. Clicking a bar opens the dashboard with that "
            + "period's date range applied. The efficiency percentage above a bar is exposure time "
            + "divided by astronomical dark hours, and it appears only when your observer coordinates "
            + "are configured."),

        new("stats.data-quality", "Data quality",
            "The distribution of HFR across the library, in pixels and in arcseconds, with the "
            + "average and the best of both HFR and eccentricity. Frames with no derivable plate scale "
            + "are outside the arcsecond figures and are counted separately, and so are the frames "
            + "excluded from the eccentricity average for using a different source."),

        new("stats.storage", "Storage",
            "Three figures: the catalogued FITS bytes from the database, the thumbnail cache on disk "
            + "and the database file itself. There is no total disk usage figure, because measuring it "
            + "means walking every library folder on a page load."),

        new("stats.ingest", "Ingest history",
            "New files added per day over the last 30 days with a completed scan. It counts files "
            + "added, not files read, so a scan that re-read a changed file adds nothing here."),

        // Phase 17 Task 7. The five tab topics plus the shared filters topic for the Analysis
        // page, grouped together the way the stats.* group follows page.statistics.
        new("analysis.filters", "Shared filters",
            "Five controls set the scope for the tabs below, though not every tab takes every one. "
            + "Equipment picks one telescope and camera combination or all of them, Filter "
            + "restricts to a single optical filter, Granularity "
            + "switches between per frame and per session, and the date range bounds the imaging "
            + "night. Granularity reaches only the Correlation tab and the Distributions histogram; "
            + "every other tab, including the Distributions box plot, ignores it. A per-session "
            + "point is one night and one target. A night on which two rigs imaged the same target "
            + "still gives one point, not two, because the grouping key is the night and the target, "
            + "never the rig. Compare ignores the equipment and filter controls entirely and reads "
            + "only the date range, because its own two group pickers are the equipment or filter "
            + "selection. Grouping two cameras together on the Equipment settings tab can fold two "
            + "equipment combinations into one, merging two rigs whose plate scales differ, so this "
            + "equipment list is only as separate as the alias groups you have configured."),

        new("analysis.correlation", "Correlation",
            "A scatter of one X metric against one Y metric across the filtered frames, drawn with a "
            + "trend line and a shaded confidence band. The trend, the band, the two stats cards and "
            + "the point counts are all computed over every point that matches the filters. Hide "
            + "Outliers removes only the points sitting outside 1.5 times the interquartile range on "
            + "either axis from what is drawn, changing none of those figures. The band is a rough "
            + "guide rather than a true 95 percent interval, and it is narrower than that interval "
            + "on a small point set. When a PHD2 guiding metric is chosen on the X axis, its values "
            + "are night-level figures joined by rig and imaging night, and a night with no mapped "
            + "PHD2 profile is omitted rather than shown as a gap. Past 5,000 points the chart draws "
            + "an even sample for speed, while the trend, the band and both stats cards still use "
            + "every point that matched."),

        new("analysis.distributions", "Distributions",
            "Histogram bins one metric across the filtered frames with a dashed line at the median; "
            + "Box Plot draws one box per group, grouped By Filter, By Equipment, By Month or By "
            + "Target. A group with fewer than four frames is not drawn at all, so a reader who "
            + "filtered down to three frames per filter is not left thinking the chart failed. By "
            + "Month groups by the calendar month of the exposure itself, while the shared date "
            + "range still filters by imaging night, and the two are not the same day when a session "
            + "crosses midnight. HFR is a per-train pixel figure: it is only comparable within one "
            + "optical train, and FWHM in arcseconds is the metric to use across telescopes."),

        new("analysis.timeseries", "Time Series",
            "One point per imaging night, always the median of that night's values across every "
            + "target and rig the filters admit; the granularity segment does not reach this tab, so "
            + "switching it between per frame and per session changes nothing here. The shaded band "
            + "is the middle of your own nights plus or minus one MAD, and it needs at least eight "
            + "nights before it is drawn. Both moving averages count points, meaning nights that "
            + "have a frame, not calendar days, so a month's gap between two imaging nights does not "
            + "widen the window. A night imaged under more than one target reads Mixed rather than "
            + "naming one of them."),

        new("analysis.matrix", "Matrix",
            "A grid of Pearson correlation coefficients for every pair of the ten metrics on the "
            + "columns and the ten on the rows, always at frame granularity. A cell is blank, meaning "
            + "no answer rather than an answer of zero, when the pair has fewer than 10 frames "
            + "carrying both metrics or when one of the two never varies across those frames; "
            + "otherwise it carries a figure. The printed figure carries the sign of the correlation; "
            + "the cell's colour is only a reading aid, not a second source "
            + "of information. Clicking a cell that carries a figure opens that pair on the "
            + "Correlation tab; a blank cell cannot be clicked."),

        new("analysis.compare", "Compare",
            "Compares two groups you choose directly, either two equipment combinations or two "
            + "filters, over one metric and the shared date range, which bounds the imaging night. "
            + "The filter bar's own equipment and filter selections are ignored here, because "
            + "applying them on top of two groups you already picked could ask you to compare two "
            + "groups the bar had already emptied. When the metric is HFR, the verdict figure alone "
            + "converts each frame to arcseconds through its own plate scale before comparing, "
            + "because pixel HFR from two different optical trains is not the same unit; the box "
            + "plot and both stats cards on this tab stay in pixels, as every other tab does. Each "
            + "group needs at least four frames before a comparison is drawn."),

        new("diagnostics.database", "Database",
            "The database file and its size, the write-ahead log, and the row count of each table. A "
            + "WAL far larger than the database usually means a long-running reader is holding a "
            + "checkpoint back, which the next clean shutdown clears."),

        new("diagnostics.scan", "Scan",
            "The current scan state and progress, the last run's trigger, timings and counters, one "
            + "row per watched root with whether that root is reachable, and the next scheduled scan "
            + "time. An unreachable root is skipped rather than treated as empty, so nothing is removed "
            + "from the catalogue while a drive is offline."),

        new("diagnostics.resolver", "Resolver",
            "The catalogue cache and how it is performing. A hit is a lookup answered from the cache "
            + "with no network call, which includes a negative row that has not expired. A lookup that "
            + "found an expired negative row counts as a miss."),

        new("diagnostics.unresolved", "Unresolved",
            "Every distinct OBJECT string in the library that no catalogue entry matched, with the "
            + "frames behind each. The retry button re-runs the resolver over them. A name that never "
            + "resolves is usually a comet, a sketch object or a field with no catalogue entry, and it "
            + "still appears on the dashboard as its own row."),

        new("diagnostics.errors", "Errors",
            "The last 50 error and warning events, newest first. They are the same events the "
            + "Activity page lists, narrowed to the two severities that need attention."),

        new("diagnostics.versions", "Versions",
            "The application version and git commit, and the runtime, toolkit, SQLite and operating "
            + "system versions under it. Quote them when reporting a problem."),

        new("diagnostics.paths", "Paths",
            "Every directory this installation uses, and where the data location came from: a "
            + "default, a pointer file, an environment variable or a command-line override. The last "
            + "two fields state whether the Startup shortcut exists and whether this process started "
            + "with no window."),

        new("diagnostics.log-viewer", "Log viewer",
            "The application's own log, newest first, read from the current file and the retained "
            + "rolled files. The level filter shows the level you pick and everything more severe. The "
            + "search box matches the message text only. Follow tail re-reads every two seconds. This "
            + "filters what is displayed, not what was recorded."),

        new("logviewer.show", "Show",
            "Narrows what the list displays: a minimum severity, a text match on the message, and "
            + "whether the view follows the tail of the file. None of it changes what is being written."),

        new("logviewer.capture", "Capture",
            "Sets the minimum severity that gets written to the log in the first place. Leave it at "
            + "Information for normal use and turn it up to Debug or Verbose only while you are chasing "
            + "something, then turn it back down. It takes effect from the moment it is changed and "
            + "needs no restart."),

        new("settings.library.scan-roots", "Library folders",
            "The folders GalactiLog walks. Each is its own boundary and they may not be nested. "
            + "Removing a root stops it being walked and removes nothing from the catalogue and nothing "
            + "from disk."),

        new("settings.library.include-paths", "Include paths",
            "Restricts the walk to paths containing one of these fragments. An empty list means every "
            + "path under a library folder is eligible, which is the usual case. Include is applied before "
            + "exclude."),

        new("settings.library.exclude-paths", "Exclude paths",
            "Skips any path containing one of these fragments. This is where calibration masters, "
            + "stacking work areas and processing output belong, so the walk never reads them. Exclude "
            + "wins over include."),

        new("settings.library.name-rules", "Name rules",
            "Rules matched against the file name rather than the path, each either a substring or a "
            + "regular expression, and each either an include or an exclude. A first run seeds five "
            + "exclude rules for the common processing folders, and they can be edited or removed like "
            + "any other."),

        new("settings.library.test-path", "Test a path",
            "Paste a path here to see whether the current rules would let the scanner read it, and "
            + "which rule decided. It reads nothing and changes nothing; it answers the rules as they "
            + "are configured right now."),

        new("settings.library.scanning", "Scanning",
            "Whether GalactiLog re-walks the library on its own and how often, whether the file "
            + "watcher picks up files as they land, and whether calibration frames are catalogued "
            + "alongside light frames. A shorter interval picks up frames sooner at the cost of more "
            + "disk activity."),

        new("settings.library.manual-scan", "Manual scan",
            "Runs one scan now, over the configured roots and with the configured rules, and reports "
            + "its progress. It reads only files that are new or changed since the last run. It can be "
            + "cancelled and it can be left running while you use the rest of the application."),

        new("settings.library.setup", "Setup",
            "Re-runs the first-run wizard so you can correct a setting from it. A re-run seeds no "
            + "default rules: the five the first run added may have been tuned since, and a re-run "
            + "exists to change one field rather than to restore defaults."),

        new("settings.targets.duplicates", "Duplicate suggestions",
            "Targets GalactiLog believes are the same object under two names, with the method and the "
            + "score behind each suggestion. Accepting one opens the merge preview; dismissing one "
            + "remembers the dismissal and does not offer it again. Nothing is merged until you accept "
            + "a preview."),

        new("settings.targets.unresolved", "Unresolved names",
            "OBJECT names from your headers that no catalogue matched. Retry re-runs the resolver "
            + "over them, and assigning one to an existing target attaches its frames to that target. "
            + "Frames of an unresolved name are still catalogued and still counted."),

        new("settings.targets.rename-history", "Rename history",
            "The most recent target renames, read from the activity log. Because it is read from the "
            + "activity log, it is bounded by the activity retention window, and a rename older than "
            + "that window is gone."),

        new("settings.targets.merge-history", "Merge history",
            "Every merge this installation performed, newest first, with what moved. Undo restores "
            + "the merged-away target and moves its frames back. A merge that has been undone stays in "
            + "the list as a record."),

        new("settings.filters.groups", "Filter groups",
            "Canonical filter names, the raw FILTER header spellings that map to each, and the colour "
            + "used for that filter in badges, charts and the night strip. Grouping Ha, H-alpha and "
            + "Halpha under one canonical name makes every page count them as the same filter. A filter "
            + "with no colour of its own takes its category's default until you pick one."),

        new("settings.equipment.cameras", "Cameras",
            "Canonical camera names and their aliases. An alias is the raw string as it appears in "
            + "the INSTRUME header; the canonical name is what GalactiLog displays and groups by. "
            + "Grouping two spellings of one camera is what makes per-camera statistics add up."),

        new("settings.equipment.telescopes", "Telescopes",
            "Canonical telescope names and their aliases from the TELESCOP header. Grouping "
            + "consolidates spellings of one optical train into one entry, which keeps rig-level "
            + "figures coherent across nights where the capture profile wrote the name differently."),

        new("settings.equipment.phd2-profiles", "PHD2 profiles",
            "The equipment profiles your PHD2 guide logs themselves named, not a list you "
            + "maintain: a new profile appears here the first time a scan reads a guide log that "
            + "names it. Mapping a telescope tells GalactiLog which rig a profile's guiding "
            + "belongs to, which is what lets its measurements join that rig's nights. A "
            + "per-profile timezone or site is only needed when this rig's PHD2 clock or location "
            + "differs from your observer defaults; leave either blank to inherit them. Saving "
            + "re-checks your frames, so a corrected mapping or zone can bring guiding data that "
            + "was previously unreachable back onto the nights it belongs to."),

        new("settings.maintenance", "Maintenance actions",
            "Repair and rebuild actions over the catalogue. They run one at a time and none of them "
            + "is available while a scan is running. None of them touches a file in your library: they "
            + "rewrite database rows and the thumbnail cache only. Reset database asks you to type a "
            + "confirmation and itemises exactly what it clears and what it keeps."),

        new("settings.location.observer", "Observer location",
            "Your site latitude, longitude and name. They are used as a fallback when a FITS header "
            + "carries no site coordinates, to compute darkness hours and target altitude, and to "
            + "compute the local noon boundary that imaging night grouping uses."),

        new("settings.location.grouping", "Grouping and clock",
            "Imaging night grouping keeps a session that crosses midnight as one night, with the "
            + "boundary at local noon computed from your longitude; without a longitude the boundary "
            + "falls back to UTC midnight, which splits most nights in two. The display timezone and "
            + "the 24-hour clock change only how already-recorded times are shown, never how they are "
            + "stored and never which night a frame is grouped into."),

        new("settings.display.appearance", "Appearance",
            "The theme, the text size and the content width. Text size scales every piece of text "
            + "together, including table cells and chart labels. Content width caps how wide the "
            + "content area grows on a large monitor. None of it changes any stored figure."),

        new("settings.display.defaults", "Defaults",
            "Defaults the pages start from: the dashboard page size, the display timezone, the "
            + "24-hour clock, and how many nights the target chart pre-selects. Each page keeps its own "
            + "choices afterwards."),

        new("settings.display.metrics", "Metric visibility",
            "Which metric groups and which fields inside them appear on the target detail page. "
            + "Turning a group off hides its columns everywhere, whatever the column picker says, so "
            + "the group toggle wins. Hiding a metric hides the column; it does not stop the value "
            + "being read or stored."),

        new("settings.display.ledger-columns", "Nights ledger columns",
            "Chooses which of your own night columns appear on the Nights list of a target page. "
            + "They start switched off, and when the window is narrow they are the first thing "
            + "dropped, so the columns already on that list never lose room. The built-in "
            + "columns are always shown and are not in this list."),

        // The gates are stated per scope, not as one blanket sentence: spec 12.15's "Where the
        // cells appear" table puts a night column in the dashboard's night expander and a rig
        // column on a night's rig lines with no picker at all, so only the target and night
        // columns wait to be switched on.
        new("settings.custom-columns.add", "Add a column",
            "Creates a field of your own for something no camera or telescope records: whether a "
            + "target has been processed, a note, a priority. Choose a checkbox, a text box or a "
            + "pick-from-a-list, and choose whether it belongs to a target, to one night of a "
            + "target, or to one telescope and camera pair on one night. The kind and what it "
            + "belongs to are fixed once the column exists, because everything already filled in "
            + "was entered under the old rule; to change either, make a new column. A second "
            + "column with the same name is refused. A new Target column stays hidden on the "
            + "dashboard until you switch it on in the column picker, and a new Night column stays "
            + "hidden on the Nights list until you switch it on in Settings, under Display. Night "
            + "columns appear in the "
            + "dashboard's night expander and Rig columns appear on a night's rig lines straight "
            + "away."),

        // The Delete sentence says that the first press states the count, which is spec 12.15's
        // two-press pattern and matches the removed-choice sentence in the same paragraph.
        new("settings.custom-columns.table", "Columns",
            "Every column you have defined, with the number of values stored against it. The "
            + "arrows change the order the columns appear in everywhere. Edit changes the name and, "
            + "for a pick-from-a-list, the choices; removing a choice that values still use is "
            + "refused and says how many. The first press of Delete arms it and states how many "
            + "stored values will go with the column; the second press removes the column and "
            + "every one of those values."),

        // Phase 21 Task 6. The four External Tools topics (spec 12.12 amendment 5c). Placed by
        // Task 4 in ExternalToolsTabView.axaml, which sits after Custom Columns and before
        // Storage in the settings strip, so the four land in that position here too.
        new("settings.external-tools.astrobin-filters", "AstroBin filter ids",
            "AstroBin wants a number for each filter rather than its name, so a filter has to be "
            + "matched to AstroBin's own equipment list once. Open the filter on AstroBin and read "
            + "the number out of the address: Chroma LRGB L sits at "
            + "app.astrobin.com/equipment/explorer/filter/4649, so its id is 4649. A filter you "
            + "leave blank still gets its own row in the AstroBin CSV, with the filter cell empty, "
            + "so you can fill the number in on AstroBin's side afterwards. The list shows every "
            + "filter name this library has seen, grouped or not, and a number you enter against a "
            + "group covers every name in it."),

        new("settings.external-tools.bortle", "Bortle class",
            "The Bortle class describes how dark your sky is, from 1 at a site with no light "
            + "pollution to 9 in a city centre. It goes into every row of the AstroBin CSV, so an "
            + "upload carries the sky it was shot under. It is one number for the whole library: if "
            + "you image from two sites, set the one you use most and correct the other's rows on "
            + "AstroBin."),

        new("settings.external-tools.nina", "NINA instances",
            "Each instance here is one copy of NINA on your network. Give it a name you will "
            + "recognise in a menu and the address of its API, which is http:// then the machine "
            + "and then the port the API is listening on, usually 1888. An instance that is "
            + "switched on and has both a name and an address appears on a target's menu as "
            + "\"Send to NINA\". Choosing it opens that target in NINA's framing assistant at the "
            + "target's coordinates and, when the target has a known orientation, rotates the frame "
            + "to it. Nothing listens on this machine: GalactiLog only ever makes the call."),

        new("settings.external-tools.stellarium", "Stellarium instances",
            "Each instance here is one copy of Stellarium running the Remote Control plugin. Its "
            + "address is http:// then the machine and then the port the plugin is listening on, "
            + "usually 8090. An instance that is switched on and has both a name and an address "
            + "appears on a target's menu as \"Slew Stellarium\". Choosing it points Stellarium at "
            + "the target by its catalogue name where Stellarium knows it, by its coordinates where "
            + "it does not, and then sets the field of view to 20 degrees so the object is framed "
            + "rather than filling the screen."),

        new("settings.storage.data-location", "Data location",
            "Where the database, the logs and this settings document live. A change takes effect the "
            + "next time GalactiLog starts and copies rather than moves: the previous folder still "
            + "holds a copy and is named here until you delete it yourself. The application never "
            + "deletes it for you."),

        new("settings.storage.thumbnail-cache", "Thumbnail cache location",
            "Where generated thumbnails and previews are written, with the free space on that volume. "
            + "Everything here is regenerable: deleting the cache costs rendering time and no data. It "
            + "is never inside your FITS library."),

        new("settings.storage.file-preview", "File preview",
            "Preview resolution is the size a full preview is rendered at, with 0 meaning the frame's "
            + "native resolution, and the cache size bounds how much of that rendering is kept on disk. "
            + "A cache smaller than 100 MB is treated as 100 MB, because a smaller bound evicts each "
            + "preview as it is written and turns the preview modal into a render loop."),

        new("settings.storage.thumbnails", "Thumbnails",
            "The width thumbnails are generated at. Changing it does not regenerate what already "
            + "exists; the Maintenance tab's regenerate action does that."),

        new("settings.general.window", "Window behaviour",
            "What closing and minimising the window do. With close to tray on, closing hides "
            + "GalactiLog in the notification area and it keeps scanning; the tray icon's Open brings "
            + "it back and its Exit ends the process. With it off, closing ends the process."),

        new("settings.general.startup", "Startup",
            "Whether a shortcut in your Startup folder launches GalactiLog when you sign in, and "
            + "whether it starts into the tray with no window. The control is offered only on a build "
            + "the updater installed, because the shortcut targets an install-root stub that no other "
            + "build has."),

        new("settings.general.notifications", "Notifications",
            "Whether a finished scan reports its outcome on the tray icon's tooltip while no window "
            + "is on screen. It is reported once per finished scan and only while hidden. It adds no "
            + "activity event: the Activity page already records every scan."),

        new("settings.general.survey-downloads", "Survey downloads",
            "Whether Sky view on a target's page may fetch survey images of the sky around that "
            + "target from alasky.cds.unistra.fr. While this is off, GalactiLog makes no request to "
            + "that host. Catalogue resolution is a separate lookup and keeps working either way."),

        new("settings.about", "About",
            "The version, commit and release channel of this build, the update check and its release "
            + "notes, and a link to the log folder. Quote the version and the commit when reporting a "
            + "problem."),

        new("target.about", "About",
            "The identity of this target as the catalogues describe it: names and aliases, object "
            + "type and category, coordinates, angular size, magnitude and the catalogue notes. The "
            + "object type can be corrected here with the pencil, which changes how the target groups "
            + "under the dashboard's Object Type filter and in the target list. Correcting it changes "
            + "no frame."),

        new("target.notes", "Notes",
            "Free-form notes about this target, saved a second after you stop typing. They are yours "
            + "and nothing reads them: no figure on this page is derived from them."),

        new("target.merge-history", "Merge history",
            "Every merge that produced this target, with what moved. Undo restores the merged-away "
            + "target and moves its frames back. The list is the record; an undone merge stays in it."),

        // The caveat is narrower than it was, because the cross-session chart keeps an unsplit
        // whole-night series beside its per-rig ones (phase review P2-1, ruled option 1). The
        // unsplit line plots every night from the overview; only a split series depends on a
        // night's detail being loaded. Spec 12.12's paragraph table carries the same text.
        new("target.trend", "Trend across nights",
            "Each selected metric has its own lane. Every frame is a small dot inside its night's band "
            + "and a line marks that night's median, so a slow drift across a season is visible where "
            + "a single night is not. Nights with no value for a metric leave a gap rather than a "
            + "joined line. The table under the lanes lists each filter's five medians for every "
            + "plotted night, empty where a filter has no value. A per-rig or per-filter split plots "
            + "only the nights whose detail has been loaded in this visit, so opening a night fills "
            + "its points in."),

        new("target.nights", "Nights",
            "Every night this target was imaged, newest first, with that night's medians beside the "
            + "target's own means in the top row. A night's figure is marked only when it is worse than "
            + "the target mean by more than one unit of the last displayed decimal; better stays "
            + "silent. A night median against a target mean is not a like-for-like comparison, which is "
            + "why only the clearly worse figures are marked. The check box on a row selects that night "
            + "for an action; the lit row is the night the pane beside it is showing, and the two are "
            + "different marks. A plain click lights one night and clears the checks, Ctrl and click "
            + "adds or removes that night, and Shift and click adds the range from the lit night to it."),

        // The thumbnail sentences moved to target.night-detail with the strip itself (the user's
        // B1 ruling of 2026-09-18): the paragraph describes the section the reader is looking at,
        // and the thumbnails are no longer under the facts line.
        new("target.night", "The night",
            "This night's own facts: the gain, the exposure lengths present, the first and last frame "
            + "times, and the median airmass, ambient temperature and humidity. Under it, the Night "
            + "metrics section holds the per-filter table, the ranges, the comparison line and the "
            + "sharpest frame; then the timeline, the chart and the frames, with Session "
            + "notes as a section at the bottom of the lanes. Both sections remember whether they "
            + "are open. When several nights are checked they show here as one night, with the chart "
            + "and the timeline laid side by side and a dashed line at the start of each night."),

        new("target.session-metrics", "Metrics over the night",
            "One point per frame across the night for each selected metric, in capture order, with a "
            + "reference line at the night's median for the primary metric. It is where a focus drift, "
            + "a guiding failure or a passing cloud shows as a shape rather than as a number. The "
            + "Guiding pill at the end of the pill row swaps the dots for the night's guide trace on "
            + "the same time axis; the metric pills keep their set while it is on. Drag the "
            + "handle between the night charts and the frames table to trade chart height for frame "
            + "rows; Up and Down move it from the keyboard, and a double click returns it to "
            + "automatic. The height is kept per profile. The chart takes what the handle leaves "
            + "under the timeline, never under 120 pixels, and automatic gives it 180."),

        new("target.guiding", "Guiding",
            "This night's guiding, read from the PHD2 guide log a scan catalogued for it and not "
            + "from any frame's FITS headers, drawn in the night chart's slot while the Guiding pill "
            + "is on. Nothing is read from the log until the night is opened. Every guide session of "
            + "the night sits on the shared time axis, first frame to last, and the figures above the "
            + "plot report the whole night; a session under 100 frames is too short to grade, so it "
            + "counts in the totals and leaves no RMS figure. Five layers, back to front: the bands "
            + "where guiding was settling, the axis grid, the RA and Dec error traces, the points "
            + "marking a lost star, and the dither lines; a legend entry hides its layer. Four "
            + "gestures: Ctrl with the wheel zooms time, Ctrl and Shift with the wheel zoom the "
            + "arcsecond scale, a drag pans, and a double click resets both; a plain wheel scrolls "
            + "the pane."),

        new("target.night-detail", "Night detail",
            "It opens with this night's sharpest light frame, the one with the lowest recorded HFR, "
            + "shown whole at the frame's own aspect ratio and never cropped, and on a night imaged "
            + "by more than one rig there is one per rig; clicking one opens that frame in the "
            + "preview. Then the per-filter figures for this night and the ranges across it. HFR "
            + "and eccentricity in the per-filter table are "
            + "graded against this rig overall, meaning every frame captured with the same telescope, "
            + "camera and filter across your whole library, whatever the target. Integration, frame "
            + "counts and exposure are not graded. On a night imaged by more than one rig the table "
            + "splits per rig under a rig label row. This night's guiding is the Guiding pill on "
            + "the night chart."),

        new("target.frames", "Per-frame grading",
            "Each frame is graded by how far its metrics sit from a typical baseline, not against a "
            + "fixed threshold. Compare to picks it: This session is the other frames of the same "
            + "night, and This rig is every frame captured with the same telescope, camera and filter "
            + "across your whole library, whatever the target. Deviation is how many median absolute "
            + "deviations a frame sits from its group's median, in raw MAD units and not sigma; one MAD"
            + " unit is about 0.67 sigma, so they run smaller than a standard deviation score. Both "
            + "ignore outliers, so a few bad frames cannot drag the baseline. A group under 8 frames, "
            + "or one whose values are all identical, is left ungraded: there is no trustworthy spread "
            + "to measure against. The colour states the verdict in both directions: better than "
            + "typical, normal, watch, and likely reject. Signal metrics (star count, background ADU "
            + "and guiding) are always compared within the same night whichever baseline is selected, "
            + "because they drift with the sky. A Filter pill per measured metric shows only the frames"
            + " the night's own outlier rule flagged on it, with their count; a pill at zero is "
            + "disabled, and Clear filters restores every row. Grading is advisory. No frame is deleted"
            + " or hidden."),

        new("target.session-notes", "Notes",
            "Notes about this night, saved a second after you stop typing. They are yours and nothing "
            + "reads them: no figure on this page is derived from them."),

        // Phase 16 Task 6, the Export for stacking page (spec 12.13). page.export-stacking sits
        // with the other page.* records above; these four cover its sections.
        new("export.folders", "Folders to copy",
            "The rows under a night are that night's own frames' folders, from the top of your "
            + "library down to the folder the frames sit in, and one of them is copied whole. The "
            + "default pick is the deepest folder that holds every one of that night's frames and "
            + "drags nothing else along. A badge reading \"+2 other nights\" means that folder also "
            + "holds frames from two other nights, which are copied too; it is advisory, and choosing "
            + "such a folder is how you deliberately copy a date folder two targets share. The size "
            + "figure counts every catalogued file in that folder, whatever target it belongs to and "
            + "whatever kind of frame it is, because it answers how much the copy will move: it reads "
            + "unknown rather than a partial total when any one file's size was never recorded, and "
            + "sidecars and files GalactiLog never reads are copied too and are not counted. A night "
            + "that says it has no folder to copy contributes nothing to the export."),

        new("export.quality", "Quality filter",
            "The chips are absolute limits you type, judged per frame on the metrics that frame "
            + "actually carries. Type a limit with a decimal point, such as 3.5, whatever your "
            + "regional number format is. A limit on a metric a frame does not carry is skipped, "
            + "never failed, so a guiding limit does not silently drop every unguided frame. A "
            + "frame carrying none "
            + "of the limited metrics reads Unmeasured, and Unmeasured is not copied; the Copy box on "
            + "its row is how you copy one anyway. The cell colours grade each frame against the "
            + "baseline the segment chooses and decide nothing: the limits alone decide a verdict, and "
            + "the colours are advisory. The limits are remembered per rig, so two rigs keep two sets, "
            + "and the tally counts every light frame of the checked nights, a wider set than the "
            + "totals the review step shows."),

        new("export.settings", "Settings",
            "The staging folder is where the copy lands. GalactiLog never picks one for you, "
            + "because a folder the application chose would put a second copy of your frames somewhere "
            + "you did not choose, and the copy is large. It is remembered the moment you choose it; "
            + "you do not have to press Save as defaults for that. A folder inside your library, or "
            + "one that contains it, is refused, because the next scan would catalogue the staged copy "
            + "as a second set of frames. The folder patterns are the folder names the script skips "
            + "inside each copied folder, one per line, matched against the path under the folder "
            + "being copied and not against your whole library path. Save as defaults keeps the "
            + "patterns for next time."),

        new("export.script", "Script",
            "The script is a text file GalactiLog wrote once and never runs; you run it. It only "
            + "copies: it contains no delete, no move and no rename, and it writes only under the "
            + "staging folder. The run command beneath it is the line to paste; the PowerShell one "
            + "unblocks the file first, which is harmless when the file was never marked. Copy "
            + "script, Show script and the file all carry the same text. Starting another export "
            + "withdraws this section, because it would otherwise describe a plan that is no "
            + "longer on screen; the file already written is not touched. If a file of the same "
            + "name is already in the staging folder, the script overwrites it; nothing in your "
            + "library is ever overwritten, moved or deleted. GalactiLog counts a kilobyte as 1000 "
            + "bytes and the script's own console counts it as 1024, so the two state slightly "
            + "different totals for the same files."),

        // The export wizard's steps 3 to 6; export.folders and export.quality above are steps 1
        // and 2. The window's one bound glyph places all six.
        new("export.destination", "Staging folder",
            "The folder the copy lands in, outside your library. With the subfolder box on, this "
            + "export goes into a folder named after the target inside it, so exports of several "
            + "targets can share one staging folder. The subfolder is created only when you commit "
            + "on the review step, never when you open this step."),

        new("export.method", "Copy or script",
            "Copy now has GalactiLog copy the chosen folders itself, with a progress bar and a "
            + "Cancel button. Generate script writes a PowerShell or Bash script that you run "
            + "yourself, which suits a copy to another machine. Both write into the same staging "
            + "folder and skip the same folders and frames."),

        new("export.review", "Review",
            "What the commit will do, and nothing has been written yet. A destination inside your "
            + "library, the top of a drive, or an export left with no frames blocks the commit. The "
            + "warnings about free space, very long paths and files already in the destination do "
            + "not. Once you commit, the wizard locks until the copy ends; only Cancel acts."),

        new("export.result", "Result",
            "What the commit did. A file already in the staging folder is never overwritten: one of "
            + "the same size is counted as already present, and one of a different size is listed "
            + "by path. A cancelled or stopped copy lists the files it left part written. Running "
            + "the same export again fills in only what is missing. Save report writes these lists "
            + "to a text file."),

        // Phase 18 Task 4: the Mosaics page's four glyphs (spec 12.12, 12.17).
        new("mosaics.about", "Mosaics",
            "A mosaic collects the panels of one large field, each panel a set of nights from any "
            + "target, and totals their integration so you can see which panel needs more time; "
            + "suggestions come from Run Detection and every scan, and Create mosaic makes one by hand."),

        new("mosaics.keywords", "Detection keywords",
            "The words that introduce a panel number in a target's name, such as Panel in \"M 31 "
            + "Panel 2\" or P in \"NGC 7000 P3\"; a change is applied to your frames at the next Run "
            + "Detection or scan."),

        new("mosaics.suggestions", "Suggestions",
            "Groups of targets that look like panels of one mosaic, found by name, by sky position or "
            + "by both, where high confidence means name and position agree and low means review the "
            + "notes first; Accept creates the mosaic from the checked panels, and Dismiss hides it "
            + "until new nights appear."),

        new("mosaics.table", "Mosaics table",
            "Every mosaic with its panel count, integration, frames and date range, where a header "
            + "click sorts, the column picker chooses the columns, and Expand renames the mosaic and "
            + "edits its panels."),

        // Phase 18 Task 5: the mosaic detail page's four glyphs (spec 12.12, 12.17).
        new("mosaic.about", "Mosaic",
            "This mosaic's panels and the nights that count toward each, with the totals across all "
            + "of them; the figures count only included nights."),

        new("mosaic.notes", "Notes",
            "Free-form notes about this mosaic, saved a second after you stop typing."),

        new("mosaic.labels", "New panel labels",
            "Panel labels found in the names of this mosaic's targets that no panel has yet; their "
            + "frames count nowhere until you add a panel for the label."),

        new("mosaic.sessions", "Panels and nights",
            "Each panel lists the nights it counts as Included and the other nights of its targets as "
            + "Available, each night with the panel label its frames carry, and frames of one target, "
            + "night and label can count in only one panel of a mosaic."),

        new("mosaic.create", "Create mosaic",
            "Makes a new mosaic, or grows an existing one, from the nights checked on this target, "
            + "where nights given the same panel label become one panel."),
    ];

    /// <summary>The table indexed by id, ordinal. Built with <c>ToDictionary</c> on purpose: a
    /// duplicate id throws at type initialization rather than silently winning.</summary>
    private static readonly Dictionary<string, HelpTopic> ById =
        All.ToDictionary(topic => topic.Id, StringComparer.Ordinal);

    /// <summary>Every topic id, ordinal, for the census to compare the placed set against.
    /// </summary>
    public static IReadOnlySet<string> Ids { get; } = ById.Keys.ToHashSet(StringComparer.Ordinal);

    /// <summary>The topic with this id.</summary>
    /// <exception cref="KeyNotFoundException">The id is not in the table. It is not swallowed
    /// and no empty panel is rendered in its place: spec 12.12 says a missing id throws.
    /// </exception>
    public static HelpTopic Get(string id) => ById.TryGetValue(id, out var topic)
        ? topic
        : throw new KeyNotFoundException(
            $"No help topic {id}. Every topic is declared in HelpTopics.All, which is "
            + "spec 12.12's table.");

    /// <summary>The topic with this id, or false.</summary>
    public static bool TryGet(string id, [MaybeNullWhen(false)] out HelpTopic topic)
        => ById.TryGetValue(id, out topic);
}
