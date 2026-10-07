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
        new("page.statistics", "Statistics",
            "Aggregate figures across the whole library, in ten sections: overview, equipment "
            + "performance, guiding, equipment inventory, filter usage, top targets, timeline or "
            + "calendar, data quality, storage and ingest history. Timeline and Calendar are two "
            + "views of one section, switched by the buttons at its top. Open the glyph beside a "
            + "section for what that section counts."),

        // Phase 17 Task 7. The sixth rail destination, placed after Statistics and before
        // Activity (spec 12.14), so it sits here in the page.* group rather than at the end.
        new("page.analysis", "Analysis",
            "Explores relationships and trends across the frames GalactiLog has catalogued. The "
            + "Shared filters card sets the scope once: equipment combination, filter, "
            + "granularity and the date range, which bounds the imaging night. Four of the five "
            + "tabs are views of that one slice: Correlation plots two metrics as a scatter, "
            + "Distributions shows one metric as a histogram or box plot, Time Series plots a "
            + "nightly median over time, and Matrix grids the Pearson correlations between the X "
            + "and Y metrics. Compare is the exception: it picks its own two groups, takes only "
            + "the shared date range, and places them side by side."),

        new("page.activity", "Activity",
            "Everything the application did, newest first: scans, rebuilds, thumbnail work, "
            + "enrichment, migrations, your own actions such as renames, and system events, each "
            + "with its severity. The severity pills, the category pills and the search box "
            + "narrow the list, and a new badge marks events you have not seen. A scan row "
            + "expands to its sub-tasks, and a row with details opens on its chevron. Refresh "
            + "re-reads the list, Load older fetches the next page, and Prune now applies the "
            + "activity retention window immediately; an event older than that window is gone."),

        new("page.diagnostics", "Diagnostics",
            "The state of this installation in seven groups: Database, Scan, Resolver, "
            + "Unresolved, Errors, Versions and Paths, with the log viewer under them. Refresh "
            + "re-reads every figure. Export diagnostics bundle writes all of it to one file you "
            + "choose, for a bug report. The readouts only report; the actions that change "
            + "anything are Retry unresolved, Create target and Assign to target in the "
            + "Unresolved group, and the log viewer's own buttons."),

        new("page.settings", "Settings",
            "Settings configure how GalactiLog catalogues and displays your FITS data, and each "
            + "tab in the strip on the left covers one area. Open the glyph beside any section "
            + "heading inside a tab for what that section does. Changes are written to this "
            + "profile's settings document as you make them, so there is no save button."),

        new("page.target", "Target detail",
            "Everything GalactiLog knows about one target: the identity line, the log line with "
            + "the totals, the Details panel with the catalogue record and the notes, and the "
            + "nights list. The page is one layout, Question Modes, with a mode switch under "
            + "the header: Night review, Compare nights, and Integration. The nights list is a "
            + "sidebar: drag the handle on its right edge, or press Left and Right on it, to set "
            + "its width, and the chevron in its header collapses it to the check boxes and the "
            + "dates. Both are kept per profile."),

        new("page.preview", "File preview",
            "A rendered view of one frame, at the configured preview resolution. The wheel "
            + "zooms, a drag pans, and a double-click or the 0 key fits. Left and Right, or "
            + "Previous and Next, step through the frames of the list this preview was opened "
            + "from. The metadata strip under the frame name carries that frame's filter, "
            + "exposure and graded metrics and updates on every step, and Headers opens the "
            + "frame's raw FITS header panel beside the image. Render full preview on navigation "
            + "is on by default, so every step renders a fresh full-resolution preview; turned "
            + "off, stepping shows each frame's cached thumbnail, which is faster across a few "
            + "hundred frames."),

        new("page.sky-view", "Sky view",
            "A still image from the CDS hips2fits service, centred on this target. Drag to pan, "
            + "the wheel or the plus and minus keys to zoom, over a combo box of five surveys: "
            + "DSS2 Color, DSS2 Red, 2MASS Color, PanSTARRS DR1 and AllWISE Color. Reset returns "
            + "to the target's own centre and field, and Refresh re-fetches the current view. "
            + "Images are fetched from alasky.cds.unistra.fr only while Survey downloads is on, "
            + "on the General tab of Settings; while it is off the window says so and fetches "
            + "nothing."),

        new("page.merge", "Preview merge",
            "A preview of what merging two targets does before it does it. The search box finds "
            + "a target for either side and Swap exchanges them; the two columns show which "
            + "target survives and which is merged away, with their aliases, frames, integration "
            + "and sessions. What will happen lists the frames that move and the aliases the "
            + "winner gains, and Sessions with notes on both sides names the dates where the "
            + "loser's note is appended to the winner's. Merge commits and Cancel closes without "
            + "a change. A merge is recorded and Undo in the merge history reverses it."),

        new("page.frame-list", "Copy frame list",
            "Copies the file list for the nights checked in the nights list. Good copies the frames "
            + "the grading did not reject and Bad copies the frames it did. A frame with no "
            + "recorded quality data counts as unmeasured: it is never bad, and it joins the good "
            + "list only while Include unmeasured frames is checked. Absolute paths gives one "
            + "full path per line, File names gives one bare file name per line, and Explorer "
            + "search gives a string to paste into the Windows Explorer search box, which works "
            + "best up to a few dozen files. Copy writes the clipboard and nothing else; no file "
            + "is moved, renamed or deleted."),

        new("page.export-stacking", "Export for stacking",
            "A six-step wizard: Folders to copy, Quality filter (optional), Staging folder, "
            + "Copy or script, Review and Result. For each checked night pick which folder gets "
            + "copied and optionally filter which light frames go with it. Choose the staging "
            + "folder the copy lands in, then either let GalactiLog copy the folders now or "
            + "generate a PowerShell or Shell script to run yourself. Nothing is written until "
            + "you press the commit button on the Review step. GalactiLog only creates new files "
            + "in the staging folder, which may not sit inside a scan root; nothing in your "
            + "library is ever written, moved or deleted."),

        new("setup.scan-folders", "Scan folders",
            "The folders GalactiLog walks looking for FITS files. Add folder... picks one and "
            + "Remove drops it. Each folder is its own boundary, so one folder may not sit inside "
            + "another. The count beside each folder, and the total under the list, come from a "
            + "shallow probe rather than a full walk, so they are an estimate rather than a "
            + "total. Folders to skip are configured later under Settings, Library, in the "
            + "exclude paths and name rules."),

        new("setup.storage", "Storage locations",
            "Where this installation keeps its own files. The data location holds the database, "
            + "the logs and the settings; the thumbnail cache holds generated thumbnails and "
            + "previews and defaults to a folder inside the data location. Browse... picks either "
            + "path. A new data location takes effect the next time GalactiLog starts: it copies "
            + "its data there and deletes nothing from the old location, and Cancel move "
            + "withdraws the change before then. Neither path is inside your FITS library, and "
            + "nothing is ever written there."),

        new("setup.observer", "Observer location",
            "Your site latitude and longitude in decimal degrees, positive north and east, and "
            + "the timezone your capture computer's clock is set to. The coordinates give the "
            + "dark hours on the Statistics timeline and calendar and the night bounds on a "
            + "target page, and stand in for a PHD2 profile that carries no site of its own. "
            + "Group frames by imaging night splits nights at local noon, computed from a "
            + "longitude: the frame's own SITELONG header first, then this field, then an offset "
            + "guessed from the timezone. With none of them the boundary is UTC midnight, which "
            + "splits most nights in two. The timezone also places PHD2 guide log timestamps, "
            + "which carry no zone of their own."),

        new("setup.scan-options", "Scan options",
            "How GalactiLog keeps the catalogue current. Catalog calibration frames adds darks, "
            + "flats and bias frames alongside light frames, and Watch the chosen folders for new "
            + "files notices frames that arrive between scans. Scan automatically on a schedule "
            + "re-walks the library at the chosen Scan interval, from 1 hour to 24 hours. A "
            + "shorter interval picks up new frames sooner at the cost of more disk activity, and "
            + "a large library on a network share is happier with a longer one. Each scan reads "
            + "only files that are new or changed since the last run."),

        new("setup.first-scan", "First scan",
            "Run the first scan now, or finish and start it later from Settings. The first scan "
            + "reads every file it finds, so it takes far longer than later scans, which pick up "
            + "only new or changed files. Start scan begins it, Cancel stops it, and its progress "
            + "and outcome show here. Everything configured in this wizard is already saved, so "
            + "starting the scan later from Settings loses nothing."),

        new("stats.overview", "Overview",
            "Six library-wide figures: Total Integration, Total Frames, Catalogued Size, Active "
            + "Span, Avg Rig-Session Length and Avg per Target. Total Integration and Total "
            + "Frames count light frames only. Catalogued Size is the sum of the file sizes "
            + "recorded in the database, not a measurement of your disk. Active Span is the time "
            + "between the first and last imaging night in the library. The two averages divide "
            + "total integration by the number of rig-sessions and by the number of resolved "
            + "targets."),

        new("stats.performance", "Equipment performance",
            "Metric summaries per telescope and camera combination: frames, average session, "
            + "integration, median and best HFR in pixels, median eccentricity, median FWHM in "
            + "arcseconds and the filters used. Expand a row with its + button for a per-filter "
            + "breakdown. HFR is measured in pixels and is comparable only within one optical "
            + "train; use FWHM in arcseconds to compare across telescopes. The Med Ecc and Med "
            + "FWHM cells are coloured by how far they sit from the median of every combination's "
            + "own median, and the colour is withheld while fewer than eight combinations carry "
            + "that figure. A (grouped) marker means several header spellings are combined under "
            + "one name."),

        new("stats.guiding", "Guiding",
            "Two cards built from PHD2 guide logs. A rig is the telescope the PHD2 profile is "
            + "mapped to, so two cameras under one telescope share a row; a session whose profile "
            + "is mapped to no telescope joins the unmapped tally and appears on neither card. "
            + "RMS figures are frame-count weighted and skip sessions under 100 guide frames, "
            + "counted in the Sessions column as too short to score. The coloured cells compare "
            + "each rig's figure against the middle of your rigs and need at least eight values; "
            + "a value is one rig, so it means eight telescopes, and with one rig every cell is "
            + "neutral while the figures stay correct. The altitude card splits each rig's RMS "
            + "into bands below 30, 30 to 60 and above 60 degrees, reading altitude from the "
            + "pointing line in the PHD2 log, so it needs no observer coordinates; Table view "
            + "lists the same rows."),

        new("stats.inventory", "Equipment inventory",
            "Every camera and telescope seen in the library, cameras first, with the frames, "
            + "nights, targets, average session, integration, median FWHM in arcseconds and "
            + "median guiding RMS recorded against each. The n beside a median FWHM is the number "
            + "of frames carrying one. A name marked (grouped) is a canonical name standing for "
            + "several raw header spellings, configured on the Equipment settings tab."),

        new("stats.filter-usage", "Filter usage",
            "Integration time per canonical filter, one bar each, tinted with that filter's "
            + "configured colour and ordered by integration. It counts light frames only, and an "
            + "alias mapped to a canonical filter is counted under the canonical name. A frame "
            + "with no filter in its header is not counted."),

        new("stats.top-targets", "Top targets",
            "The ten targets with the most integration time, ranked, as a bar chart and a list. "
            + "It counts light frames only and it counts every night, so a target imaged briefly "
            + "over many nights can outrank one imaged hard on a single night."),

        new("stats.timeline", "Timeline and calendar",
            "When imaging happened, as a Timeline or a Calendar. Timeline is a column chart of "
            + "integration per period at Monthly, Weekly or Daily granularity; the All, 1Y, Q, M "
            + "and W buttons set the range and Imaged only hides periods with no imaging. Ctrl "
            + "with the mouse wheel steps the granularity, a drag pans the range and clicking a "
            + "bar opens the dashboard with that period's date range applied. The efficiency "
            + "percentage is exposure time divided by astronomical dark hours, per rig on "
            + "multi-rig nights, and appears only when your observer coordinates are configured. "
            + "Calendar is a grid of nights shaded by integration over the last 12 months or one "
            + "chosen year; hovering an imaged night shows its date, integration, frames and "
            + "targets, with dark hours when observer coordinates are configured."),

        new("stats.data-quality", "Data quality",
            "The distribution of HFR across the library as two histograms, in pixels and in "
            + "arcseconds, with the average and best of both HFR figures and the average "
            + "eccentricity. No plate scale counts the frames carrying an HFR with no derivable "
            + "arcseconds per pixel; the arcsecond figures skip exactly those frames. "
            + "Eccentricity excluded counts the frames whose eccentricity came from a source "
            + "other than the one pooled, which is named above the figures."),

        new("stats.storage", "Storage",
            "Three figures and a pie: Catalogued FITS, the file sizes summed from the database; "
            + "Thumbnail cache, the rendered thumbnails and previews on disk; and Database, the "
            + "SQLite file and its write-ahead log. There is no total disk usage figure: the "
            + "application does not walk your library folders to measure them."),

        new("stats.ingest", "Ingest history",
            "New files added per day, one bar per day, over the last 30 days on which a scan "
            + "completed. It counts files added, not files read, so a scan that re-read a changed "
            + "file adds nothing here, and a cancelled or failed scan is not counted."),

        // Phase 17 Task 7. The five tab topics plus the shared filters topic for the Analysis
        // page, grouped together the way the stats.* group follows page.statistics.
        new("analysis.filters", "Shared filters",
            "Five controls set the scope for the tabs below. Equipment combination picks one "
            + "telescope and camera pairing or All equipment, Filter restricts to a single "
            + "optical filter, Granularity switches between Per Frame and Per Session, and Date "
            + "from and Date to bound the imaging night, both ends inclusive. Granularity reaches "
            + "only the Correlation tab and the Distributions histogram; the Distributions box "
            + "plot, Time Series, Matrix and Compare ignore it, and Compare also ignores the "
            + "equipment and filter choices, reading only the date range. A per-session point is "
            + "one night and one target. A combination marked (grouped) folds several header "
            + "spellings into one, as configured on the Equipment settings tab."),

        new("analysis.correlation", "Correlation",
            "A scatter of one X Axis metric against one Y Axis metric across the filtered "
            + "frames, with a trend line, a shaded confidence band, a verdict sentence and two "
            + "stats cards; six preset buttons set common pairs. The trend, the band, the cards "
            + "and the point count use every point that matches the filters, and past 5,000 "
            + "points the chart draws an even sample while those figures still use every frame. "
            + "Hide Outliers hides only the points outside 1.5 times the interquartile range on "
            + "either axis and changes none of those figures. The band is a rough guide rather "
            + "than a true 95 percent interval, and it is narrower than that interval on a small "
            + "point set. The Guiding (PHD2) metrics on the X Axis are night-level figures joined "
            + "by rig and imaging night; a night with no mapped PHD2 profile is omitted."),

        new("analysis.distributions", "Distributions",
            "Histogram bins one metric across the filtered frames with a dashed line at the "
            + "median; Box Plot draws one box per group, grouped By Filter, By Equipment, By "
            + "Month or By Target, with rings for the outliers. A group with fewer than four "
            + "frames is not drawn, and when no group has enough the tab says so. By Month groups "
            + "by the calendar month of the frame's capture date, while the shared date range "
            + "filters by imaging night, so a session that crosses midnight can fall in a "
            + "different month from its night. HFR is a pixel figure comparable only within one "
            + "optical train; use FWHM in arcseconds across telescopes."),

        new("analysis.timeseries", "Time Series",
            "One point per imaging night: the median of that night's values across every target "
            + "and rig the filters admit, and a night imaged under more than one target reads "
            + "Mixed in its tooltip. Per Frame and Per Session draw the same chart here, since "
            + "the granularity segment does not reach this tab. Each point is tinted by how far "
            + "it sits from the median of your nights. The shaded band is that median plus or "
            + "minus one MAD, with a dashed line at three MAD on the worse side; the band and the "
            + "tints need at least eight nights. Smoothing adds a 7-Night MA or 30-Night MA line; "
            + "both windows count nights that have a frame, not calendar days, and the line "
            + "starts once a full window exists."),

        new("analysis.matrix", "Matrix",
            "A grid of Pearson correlation coefficients for every pair of the ten X metrics on "
            + "the columns and the ten Y metrics on the rows, always at frame granularity. A cell "
            + "is blank, meaning no answer rather than a zero, when the pair has fewer than 10 "
            + "frames carrying both metrics or when one of the two never varies across those "
            + "frames. Every other cell prints r with its sign; the colour runs from negative r "
            + "through neutral to positive r and is a reading aid only. Hovering a cell shows r "
            + "to three decimals with the pair's frame count. Clicking a cell that carries a "
            + "figure opens that pair on the Correlation tab; a blank cell cannot be clicked."),

        new("analysis.compare", "Compare",
            "Compares Group A against Group B, two equipment combinations or two filters chosen "
            + "under Compare by, over one Metric and the shared date range, which bounds the "
            + "imaging night. The filter bar's equipment and filter choices are ignored here, "
            + "because applying them on top of your two groups could ask you to compare two "
            + "groups the bar had already emptied. Each group needs at least four frames before "
            + "the box plot, the two stats cards and the verdict are drawn, and the tab names a "
            + "group that is short. When the Metric is HFR, the verdict alone converts each frame "
            + "to arcseconds through its own plate scale; the box plot and the stats cards stay "
            + "in pixels. When either group then has fewer than four frames with a known plate "
            + "scale, no improvement figure is shown."),

        new("diagnostics.database", "Database",
            "The database file, its size and page count, the write-ahead log size, the row "
            + "count of each table, and the PHD2 data size with the method that measured it. A "
            + "write-ahead log of 0 bytes is normal: SQLite removes it on a clean close. A WAL "
            + "far larger than the database usually means a long-running reader is holding a "
            + "checkpoint back, which the next clean shutdown clears."),

        new("diagnostics.scan", "Scan",
            "Whether a scan is running, with its current task, message and progress, then the "
            + "last run's trigger, finish time, duration and results, and the next scheduled "
            + "scan. One row per library folder says whether it is watched and whether it is "
            + "reachable right now. An unreachable folder is skipped rather than treated as "
            + "empty, so nothing is pruned from the catalogue while a drive is offline."),

        new("diagnostics.resolver", "Resolver",
            "The catalogue cache and how it is performing: the positive and negative rows it "
            + "holds, the negative rows that have expired, and the hits and misses this session. "
            + "A hit is a lookup answered from the cache with no network call, which includes a "
            + "negative row that has not expired. A lookup that found an expired negative row "
            + "counts as a miss. The counters reset when GalactiLog restarts."),

        new("diagnostics.unresolved", "Unresolved",
            "Every distinct OBJECT string in the library that no catalogue entry matched, with "
            + "the frames behind each. Retry unresolved clears the negative cache rows and "
            + "re-runs the resolver over every name, assigning the frames where it now succeeds. "
            + "Create target opens the Targets tab's create form with the name filled in, and "
            + "Assign to target opens the merge dialog with the name as the merged-away side. A "
            + "name that never resolves is usually a comet, a sketch object or a field with no "
            + "catalogue entry, and it still appears on the dashboard as its own row."),

        new("diagnostics.errors", "Errors",
            "The newest 50 warning and error entries from the application's own log, newest "
            + "first, held in memory since GalactiLog started. Each row shows the time, the level "
            + "and the message. The log viewer below holds the full log at every level."),

        new("diagnostics.versions", "Versions",
            "The GalactiLog version, the git commit it was built from and its update channel, "
            + "then the .NET runtime, Avalonia, SQLite and operating system versions under it. "
            + "Quote them when reporting a problem; the diagnostics bundle carries them too, so "
            + "attaching it is enough."),

        new("diagnostics.paths", "Paths",
            "Every directory this installation uses: the application data folder, the database, "
            + "the logs, the thumbnail cache and the catalogues. Data location source says where "
            + "the data location came from: a default, a pointer file, an environment variable or "
            + "a command-line override. The last two fields state whether the Startup shortcut "
            + "exists and whether this process started with no window."),

        new("diagnostics.log-viewer", "Log viewer",
            "The application's own log, newest first, read from the current file and the "
            + "retained rolled files. Show narrows what is displayed and Capture changes what is "
            + "recorded; they are separate controls. Copy selection and Copy all put entries on "
            + "the clipboard, Open log folder reveals the files, and Save log as writes the lines "
            + "loaded above to a file. Clear log deletes the rolled files only, after stating "
            + "what it will remove; the file being written now is never touched. Load more "
            + "fetches the next older page, up to the viewer row cap."),

        new("logviewer.show", "Show",
            "Narrows what the list displays from what was already captured: a minimum level "
            + "that hides every entry below it, and a search box that matches the message text "
            + "only. Follow tail re-reads the log every two seconds so the newest lines keep "
            + "arriving, and Refresh re-reads once. None of it changes what is being written."),

        new("logviewer.capture", "Capture",
            "Sets the minimum level that gets written to the log in the first place, from "
            + "Verbose up to Fatal. Leave it at Information for normal use and switch to Debug or "
            + "Verbose only while you are chasing something, then turn it back. It takes effect "
            + "from the moment it is changed and needs no restart. Beside it, Activity retention "
            + "and Viewer row cap apply immediately, and Log file retention applies the next time "
            + "GalactiLog starts."),

        new("settings.library.scan-roots", "Library folders",
            "The folders GalactiLog walks for FITS files. Each folder is its own boundary, and "
            + "one library folder may not sit inside another. Add takes a folder from Browse or a "
            + "typed path, and Remove stops that folder being walked. Removing a folder deletes "
            + "nothing from the catalogue and nothing from disk."),

        new("settings.library.include-paths", "Include paths",
            "Folders the scan is limited to, each inside a library folder. When the list is "
            + "empty every path under every library folder is eligible, which is the usual case; "
            + "otherwise only files under one of these folders are read. A path that an exclude "
            + "path covers is skipped even when an include path also covers it."),

        new("settings.library.exclude-paths", "Exclude paths",
            "Subtrees the scan skips entirely, each inside a library folder. Calibration "
            + "masters, stacking work areas and processing output belong here, so the walk never "
            + "reads them. Exclude always wins over include: a folder listed here is skipped even "
            + "when an include path covers it."),

        new("settings.library.name-rules", "Name rules",
            "Rules matched against a file name or a folder name rather than a full path. Each "
            + "rule is a wildcard, a substring or a regular expression, applies to files or to "
            + "folders, and either excludes or includes. Exclude rules run first and include "
            + "rules narrow what is left: with file include rules a file must match one of them, "
            + "and with folder include rules one of its parent folders must match one. The first "
            + "run seeds five exclude rules for common processing folders, which you can edit, "
            + "switch off or remove like any other rule. Rules take effect when you press Save "
            + "rules; Revert discards unsaved edits."),

        new("settings.library.test-path", "Test a path",
            "Paste a file or folder path to see how the current rules treat it: Will be "
            + "scanned, or Skipped with the path or rule that decided. The path does not need to "
            + "exist on disk, and the test reads nothing and changes nothing. Choose file, folder "
            + "or auto to say what the path is. It answers the saved rules only: save unsaved "
            + "edits first to test them."),

        new("settings.library.scanning", "Scanning",
            "Options every scan follows. Include calibration frames catalogues calibration "
            + "frames alongside light frames; when it is off only LIGHT frames are catalogued. "
            + "Enable automatic scanning re-walks the library at the chosen Scan interval, and a "
            + "shorter interval picks up frames sooner at the cost of more disk activity. Watch "
            + "library folders for new files picks files up as they land. Read PHD2 guide logs "
            + "reads files named PHD2_GuideLog_*.txt found under your library folders for guiding "
            + "measurements; turning it off stops future scans reading them and deletes nothing."),

        new("settings.library.manual-scan", "Manual scan",
            "Scan library runs one scan now over the library folders with the saved rules and "
            + "reports its progress, reading in full only files that are new or changed since the "
            + "last scan. All frames and Light frames only set the scope of this run without "
            + "changing the Scanning setting above, Stop cancels the scan, and the rest of the "
            + "application stays usable while it runs. GalactiLog deletes database rows only: no "
            + "file on disk is ever moved, renamed or deleted. When half or more of a folder's "
            + "catalogued files are missing, the scan skips that folder's cleanup and records it "
            + "in the Activity feed. Tick Remove catalogue rows for missing files past the safety "
            + "limit to clean up anyway."),

        new("settings.library.setup", "Setup",
            "Run setup again reopens the first-run wizard, walking through the library folders, "
            + "the thumbnail cache, the observer location and the scan options so you can correct "
            + "one of them. Your scan filters and name rules are left alone: a re-run seeds no "
            + "default rules and restores nothing."),

        new("settings.targets.duplicates", "Duplicate suggestions",
            "Targets GalactiLog believes are the same object under two names, each with its "
            + "frame count, the suggested target, the similarity score and the method behind it. "
            + "Accept opens the merge preview, and nothing is merged until you confirm that "
            + "preview. Dismiss records the dismissal and drops the suggestion. Edit target "
            + "points a suggestion at a different existing target, and the suggestion stays "
            + "pending with that target in place. New target opens a form that creates a target "
            + "by hand."),

        new("settings.targets.unresolved", "Unresolved names",
            "OBJECT names from your headers that no catalogue matched, most frames first. "
            + "Frames under an unresolved name are still catalogued and still counted. Retry "
            + "unresolved re-runs the resolver over every name, and is unavailable while a scan "
            + "is running. Assign to target attaches a name's frames to an existing target "
            + "through the merge preview, and Create target makes a new target from the name."),

        new("settings.targets.rename-history", "Rename history",
            "The most recent target renames, newest first, each showing the old name and the "
            + "new one. The list is read from the activity log, so a rename older than the "
            + "activity retention window no longer appears."),

        new("settings.targets.merge-history", "Merge history",
            "Every merge this installation performed, newest first, showing which target was "
            + "merged into which. Undo restores the merged-away target and moves its frames and "
            + "notes back, after which the entry leaves the list. Resetting the database removes "
            + "the merge records, so a merge cannot be undone afterwards."),

        new("settings.filters.groups", "Filter groups",
            "Canonical filter names, the raw FILTER header spellings that map to each, and the "
            + "colour used for that filter in badges, charts and the night strip. Grouping Ha, "
            + "H-alpha and Halpha under one canonical name makes every page count them as the "
            + "same filter. A filter with no colour of its own takes its category's default until "
            + "you pick one. When GalactiLog finds likely alias pairs it lists them above: Merge "
            + "folds them into one group and Dismiss hides the pair. Add Filter creates a new "
            + "canonical name, and nothing is written until you press Save."),

        new("settings.equipment.cameras", "Cameras",
            "Canonical camera names and their aliases. An alias is the raw string as it appears "
            + "in the INSTRUME header; the canonical name is what GalactiLog displays and groups "
            + "by. Grouping two spellings of one camera is what makes per-camera statistics add "
            + "up. Suggested groupings can be merged or dismissed, and nothing is written until "
            + "you press Save."),

        new("settings.equipment.telescopes", "Telescopes",
            "Canonical telescope names and their aliases from the TELESCOP header. Grouping "
            + "consolidates spellings of one optical train into one entry, so rig-level figures "
            + "stay coherent across nights where the capture profile wrote the name differently. "
            + "The canonical names here are also what the PHD2 profiles panel offers when you map "
            + "a profile to a telescope. Nothing is written until you press Save."),

        new("settings.equipment.phd2-profiles", "PHD2 profiles",
            "The equipment profiles your PHD2 guide logs named, with each profile's guide "
            + "camera, focal length, pixel scale, session count and first and last seen dates. A "
            + "profile appears the first time a scan reads a guide log that names it; this is not "
            + "a list you maintain. Telescope maps the profile to a rig so its guiding joins that "
            + "rig's nights. Timezone, Latitude and Longitude are needed only when this rig's "
            + "PHD2 clock or site differs from your observer defaults; leave Timezone on Use the "
            + "observer timezone and the coordinates blank to inherit them. Save queues a guiding "
            + "re-check of your frames in the background, which can bring previously unreachable "
            + "guiding data onto the nights it belongs to."),

        new("settings.maintenance", "Maintenance actions",
            "Repair and rebuild actions over the catalogue: Rebuild targets, Retry unresolved "
            + "names, Smart rebuild, Catalog identity backfill, Reference thumbnails, Frame "
            + "thumbnails, Prune activity log and Reset database. They run one at a time, and "
            + "none is available while a scan is running. None of them touches a file in your "
            + "library: they rewrite database rows and the thumbnail cache only. Reset database "
            + "itemises what it deletes and what it keeps and asks you to type a confirmation "
            + "phrase; your settings and the shipped catalogues stay, and merges cannot be undone "
            + "afterwards."),

        new("settings.location.observer", "Observer location",
            "Your site Name, Latitude and Longitude, and the Observer timezone. Darkness hours "
            + "and target altitude are computed from these coordinates. Imaging night grouping "
            + "uses a frame's own header longitude first and falls back to yours. The observer "
            + "timezone stands in for the longitude when none is set, and it is the clock PHD2 "
            + "guide logs are read against unless a profile sets its own. It is not the display "
            + "timezone below, which changes formatting only."),

        new("settings.location.grouping", "Grouping and clock",
            "Group frames by imaging night keeps a session that crosses midnight as one night, "
            + "with the boundary at local noon computed from the longitude. Without a longitude "
            + "the observer timezone sets the boundary, and without either the boundary falls "
            + "back to UTC midnight, which splits most nights in two. Grouping is recorded when a "
            + "frame is scanned, so a change applies from the next scan and existing frames keep "
            + "the grouping they have. Display timezone and Use a 24 hour clock change only how "
            + "already-recorded times are shown, never how they are stored and never which night "
            + "a frame belongs to. Leave Display timezone on Same as observer timezone to follow "
            + "the observer setting."),

        new("settings.display.appearance", "Appearance",
            "Theme, Text size and Content width. Text size, from Small to Extra Large, scales "
            + "every piece of text together, including table cells and chart labels. Content "
            + "width caps how wide the content area grows on a large monitor: Normal (1200 px), "
            + "Wide (1600 px) or Extra wide (unbounded). Each change is saved as you make it, and "
            + "none of it changes any stored figure."),

        new("settings.display.defaults", "Defaults",
            "Defaults the pages start from. Targets per page sets how many targets a dashboard "
            + "page shows. Target chart opens on sets how many of the latest sessions the target "
            + "chart pre-selects, from the latest session alone to the latest 20. Each page keeps "
            + "its own choices afterwards, and the chart's own All sessions toggle widens the "
            + "scope for the rest of the session without being saved."),

        new("settings.display.metrics", "Metric visibility",
            "Which metric groups and which fields inside them appear on the target detail page, "
            + "with the column pickers for the dashboard, the frame table, the Nights list and "
            + "mosaics below. A group that is off hides its columns everywhere, whatever the "
            + "column pickers say, so the group toggle wins and a column whose group is off is "
            + "disabled in the pickers. Hiding a metric hides the column; it does not stop the "
            + "value being read or stored. Metric visibility is written when you press Save "
            + "metric visibility, and Revert reloads what is stored."),

        new("settings.display.ledger-columns", "Nights list columns",
            "Chooses which of your own night columns appear on the Nights list of a target "
            + "page. Every custom night column starts switched off here. When the window is "
            + "narrow these columns are the first thing dropped, so the columns already on that "
            + "list never lose room. The built-in columns are always shown and are not in this "
            + "list. With no night column defined the list reads No custom columns yet."),

        // The gates are stated per scope, not as one blanket sentence: spec 12.15's "Where the
        // cells appear" table puts a night column in the dashboard's night expander and a rig
        // column on a night's rig lines with no picker at all, so only the target and night
        // columns wait to be switched on.
        new("settings.custom-columns.add", "Add a column",
            "A field of your own for something no header records: Type is Checkbox, Text or "
            + "Dropdown, and Applies to is Target, Night or Rig. Type and scope are fixed once "
            + "the column exists. A second column with the same name is refused. A new Target "
            + "column stays hidden on the dashboard until you switch it on in the column picker, "
            + "and a new Night column stays hidden on the Nights list until you switch it on in "
            + "Settings, under Display. Night columns appear in the dashboard's night expander "
            + "and Rig columns appear on a night's rig lines straight away."),

        // The Delete sentence says that the first press states the count, which is spec 12.15's
        // two-press pattern and matches the removed-choice sentence in the same paragraph.
        new("settings.custom-columns.table", "Columns",
            "Every column you have defined, with its type, scope, options and the number of "
            + "values stored against it. Move up and Move down change the order the columns "
            + "appear in everywhere. Edit changes the name and, for a Dropdown, the options; type "
            + "and scope cannot be changed. Removing an option that values still use is refused "
            + "and says how many. The first press of Delete arms it and states how many stored "
            + "values will go with the column; the second press removes the column and every one "
            + "of those values, and Cancel disarms it."),

        // Phase 21 Task 6. The four External Tools topics (spec 12.12 amendment 5c). Placed by
        // Task 4 in ExternalToolsTabView.axaml, which sits after Custom Columns and before
        // Storage in the settings strip, so the four land in that position here too.
        new("settings.external-tools.astrobin-filters", "AstroBin filter ids",
            "The AstroBin equipment id for each filter, which the AstroBin CSV needs in place "
            + "of the filter's name. Open the filter on AstroBin and read the number out of the "
            + "address: Chroma LRGB L sits at app.astrobin.com/equipment/explorer/filter/4649, so "
            + "its id is 4649. The list shows every filter name this library has seen, grouped or "
            + "not, and an id entered against a canonical name covers every alias in its group. A "
            + "filter you leave blank still gets its own row in the AstroBin CSV, with the filter "
            + "cell empty, so you can fill it in on AstroBin afterwards."),

        new("settings.external-tools.bortle", "Bortle class",
            "The Bortle class of your sky, a whole number from 1 at a site with no light "
            + "pollution to 9 in a city centre. It is written into every row of the AstroBin CSV, "
            + "so an upload carries the sky it was shot under, and a blank leaves that cell "
            + "empty. It is one number for the whole library: if you image from two sites, set "
            + "the one you use most and correct the other's rows on AstroBin."),

        new("settings.external-tools.nina", "NINA instances",
            "Each instance is one copy of NINA on your network. Give it a Name you will "
            + "recognise in a menu and the address of its API: http:// then the machine and then "
            + "the port the API is listening on, usually 1888. An instance that is On and has "
            + "both a name and an address appears on a target's menu as Send to NINA. Choosing it "
            + "opens the target in NINA's framing assistant at its coordinates and, when the "
            + "target has a known orientation, rotates the frame to match. Nothing listens on "
            + "this machine: GalactiLog only ever makes the call."),

        new("settings.external-tools.stellarium", "Stellarium instances",
            "Each instance is one copy of Stellarium running the Remote Control plugin. Its "
            + "address is http:// then the machine and then the port the plugin is listening on, "
            + "usually 8090. An instance that is On and has both a Name and an address appears on "
            + "a target's menu as Slew Stellarium. Choosing it points Stellarium at the target by "
            + "its catalogue name where Stellarium knows it, and by its coordinates where it does "
            + "not. It then sets the field of view to 20 degrees so the object is framed rather "
            + "than filling the screen."),

        new("settings.storage.data-location", "Data location",
            "Where GalactiLog keeps its database, settings, logs and, by default, the thumbnail "
            + "cache. It sits outside the folder the installer manages, so uninstalling does not "
            + "remove it. Browse chooses a new folder, which must be empty and must not sit "
            + "inside the current one or contain it. GalactiLog copies its data there the next "
            + "time it starts; nothing is deleted from the old folder, which stays named here "
            + "until you remove it yourself. Cancel move withdraws the change before that "
            + "restart."),

        new("settings.storage.thumbnail-cache", "Thumbnail cache location",
            "Where generated thumbnails and previews are written, with the free space on that "
            + "volume. Leave it empty to use the default folder under the data location. Changing "
            + "the location moves no existing files: the new folder fills on demand and the old "
            + "one is left alone. Everything here is regenerable, so deleting the cache costs "
            + "rendering time and no data. It is never inside your FITS library."),

        new("settings.storage.file-preview", "File preview",
            "Preview resolution is the size the zoomed-in preview is rendered at: 1600 px, 2400 "
            + "px, 4000 px or Native, which matches the camera's full resolution. Preview cache "
            + "size (MB) bounds how much of that rendering is kept on disk; when the limit is "
            + "reached the oldest previews are removed. Values below 100 MB are treated as 100 MB "
            + "and the maximum is 51200 MB. Eviction applies to previews only; frame and "
            + "reference thumbnails are not evicted."),

        new("settings.storage.thumbnails", "Thumbnails",
            "Thumbnail width (px) is the width new thumbnails are generated at, and it must be "
            + "greater than zero. Changing it needs no purge: new thumbnails are written at the "
            + "new width and the old ones age out. To redo every thumbnail at the new width now, "
            + "use the Maintenance tab's Frame thumbnails action, which deletes every cached "
            + "frame thumbnail so each is generated again on demand."),

        new("settings.general.window", "Window behaviour",
            "What closing and minimising the window do. With Close to the notification area on, "
            + "closing hides GalactiLog in the notification area and it keeps scanning; the tray "
            + "icon's Open brings it back and its Exit ends the process. With it off, closing "
            + "ends the process. With Minimize to the notification area on, minimising hides the "
            + "window too, and reopening restores it at the size it had."),

        new("settings.general.startup", "Startup",
            "Start GalactiLog when I sign in places a shortcut in your Startup folder, and "
            + "Start in the notification area makes that launch open with no window while "
            + "scanning runs on its normal schedule. Starting with Windows is offered only on a "
            + "build the updater installed."),

        new("settings.general.notifications", "Notifications",
            "Report a finished scan while the window is hidden puts the finished scan's outcome "
            + "and its non-zero counts on the tray icon's tooltip while no window is on screen, "
            + "until the next scan starts. It is not a Windows notification and it adds no "
            + "activity event: the Activity page already records every scan."),

        new("settings.general.survey-downloads", "Survey downloads",
            "Fetch survey images for Sky view lets Sky view on a target's page fetch survey "
            + "images of the sky around that target from alasky.cds.unistra.fr. While it is off, "
            + "GalactiLog makes no request to that host and nothing else in the application "
            + "changes. Catalogue resolution is a separate lookup and keeps working either way."),

        new("settings.about", "About",
            "The Version, Git SHA and Release channel of this build, the update status with "
            + "Check for updates and the release notes of the newest version, Open log folder, "
            + "and View releases on GitHub. Check for updates runs only on a build the updater "
            + "installed. Quote the version and the Git SHA when reporting a problem."),

        new("target.about", "About",
            "The identity of this target as the catalogues describe it: aliases, object type "
            + "and category, constellation, coordinates, size, position angle, magnitude, surface "
            + "brightness, the SAC notes, the catalogue memberships and the mean HFR in "
            + "arcseconds. The pencil beside Object type opens a list of categories; choosing one "
            + "saves at once and changes how the target groups under the dashboard's Object Type "
            + "filter and in the target list. Correcting it changes no frame."),

        new("target.notes", "Notes",
            "Free-form notes about this target, saved a second after you stop typing, with a "
            + "Saving indicator while the write runs. A failed save keeps your text and retries "
            + "on the next edit. Nothing reads these notes: no figure on this page is derived "
            + "from them."),

        new("target.merge-history", "Merge history",
            "Every merge that produced this target, with what moved. Undo restores the "
            + "merged-away target and moves its frames and notes back, after which the entry "
            + "leaves the list. Resetting the database removes the merge records, so a merge "
            + "cannot be undone afterwards."),

        // The caveat is narrower than it was, because the cross-session chart keeps an unsplit
        // whole-night series beside its per-rig ones (phase review P2-1, ruled option 1). The
        // unsplit line plots every night from the overview; only a split series depends on a
        // night's detail being loaded. Spec 12.12's paragraph table carries the same text.
        new("target.trend", "Trend across nights",
            "Each selected metric has its own lane: every frame is a small dot inside its "
            + "night's band, a short line marks that night's median, and on a multi-rig target "
            + "each rig's median is a dashed line. Nights with no value for a metric leave a gap "
            + "rather than a joined line. All nights plots every night and Checked nights plots "
            + "only the checked ones. The table under the lanes lists each filter's five medians "
            + "for every plotted night, empty where a filter has no value. A per-rig or "
            + "per-filter split plots only the nights whose detail has been loaded in this visit, "
            + "so opening a night fills its points in."),

        new("target.nights", "Nights",
            "Every night this target was imaged, newest first, with that night's medians beside "
            + "the target's means in the top row. A night's figure is marked only when it is "
            + "worse than the target mean by more than one unit of the last displayed decimal; "
            + "better stays silent. The check box on a row selects that night and the lit row is "
            + "the night the pane shows. A plain click lights one night and clears the checks, "
            + "Ctrl and click adds or removes that night, and Shift and click adds the range from "
            + "the lit night to it."),

        // The thumbnail sentences moved to target.night-detail with the strip itself (the user's
        // B1 ruling of 2026-09-18): the paragraph describes the section the reader is looking at,
        // and the thumbnails are no longer under the facts line.
        new("target.night", "The night",
            "This night's own facts: the gain, the exposure lengths present, the first and last "
            + "frame times, and the median airmass, ambient temperature and humidity. Under it, "
            + "the Session metrics section holds the per-filter table, the ranges, the comparison "
            + "line and the sharpest frame, and keeps its open state; then come the timeline, the "
            + "chart and the frames. Session notes are in the Details panel. When several nights "
            + "are checked they show here as one night, laid end to end on one time axis with a "
            + "dashed line at the start of each night after the first."),

        new("target.session-metrics", "Metrics over the night",
            "One point per frame per selected metric, in capture order, with a line at the "
            + "primary metric's night median. The Guiding pill at the end of the pill row swaps "
            + "the dots for the night's guide trace on the same axis. Drag the handle between the "
            + "night charts and the frames table to trade chart height for frame rows; Up and "
            + "Down move it from the keyboard, and a double click returns it to automatic. The "
            + "height is kept per profile. The chart takes what the handle leaves under the "
            + "timeline, never under 120 pixels; automatic gives it 180."),

        new("target.guiding", "Guiding",
            "This night's PHD2 guide log, drawn in the chart's slot while the Guiding pill is "
            + "on; nothing is read until the night opens. Every guide session shares the time "
            + "axis and the figures report the whole night; a session under 100 frames is too "
            + "short to grade, so it counts in the totals but leaves no RMS figure. Legend "
            + "entries hide RA, Dec, Star lost, Dither or the bands where guiding was settling. "
            + "Ctrl and the wheel zooms time, with Shift it zooms arcseconds, a drag pans, a "
            + "double click resets, and a plain wheel scrolls the pane."),

        new("target.night-detail", "Night detail",
            "It opens with this night's sharpest light frame, the one with the lowest recorded "
            + "HFR, shown whole at the frame's own aspect ratio and never cropped. A night imaged "
            + "by more than one rig shows one per rig, and clicking one opens that frame in the "
            + "preview. Then the per-filter table: each filter's median HFR, eccentricity, FWHM, "
            + "guiding RMS and star count, with its exposure, frame count and integration. The "
            + "ranges table gives the minimum, median and maximum of each metric across the "
            + "night, and on a night imaged by more than one rig both tables split per rig under "
            + "a rig label row. The comparison line names the metrics where this night's median "
            + "is worse than the target's mean, and this night's guiding is the Guiding pill on "
            + "the night chart."),

        new("target.frames", "Per-frame grading",
            "Each frame is graded by its distance from a typical baseline, not a fixed "
            + "threshold; Compare to picks it: This session is the other frames of the night, "
            + "This rig is every frame with the same telescope, camera and filter across your "
            + "library. Deviation is how many median absolute deviations a frame sits from its "
            + "group's median, in raw MAD units and not sigma; one MAD unit is about 0.67 sigma, "
            + "and a few bad frames cannot drag a median. A metric with under 8 measured values "
            + "in its group, or identical values, is left ungraded; star count, background ADU "
            + "and guiding are always compared within the same night. The colour states the "
            + "verdict: better than typical, normal, watch, likely reject; a pill per metric "
            + "shows only the frames the night's outlier rule flagged, and Clear filters restores "
            + "every row. Grading is advisory: no frame is deleted or hidden."),

        new("target.session-notes", "Notes",
            "Notes about this night, saved a second after you stop typing, with a Saving "
            + "indicator while the write runs; a failed save keeps your text and retries on the "
            + "next edit. When several nights are checked there is one box per night. Nothing "
            + "reads these notes: no figure on this page is derived from them."),

        // Phase 16 Task 6, the Export for stacking page (spec 12.13). page.export-stacking sits
        // with the other page.* records above; these four cover its sections.
        new("export.folders", "Folders to copy",
            "Each night lists its frames' folders, from the top of your library down to the "
            + "folder the frames sit in; Change folder opens the list, and the chosen folder is "
            + "copied whole. The default pick is the deepest folder that holds every one of that "
            + "night's frames and drags nothing else along. A badge reading \"+2 other nights\" "
            + "or \"+1 other target\" means that folder also holds other frames, copied too; it "
            + "is advisory, and choosing such a folder is how you deliberately copy a date folder "
            + "two targets share. Frames counts this target's light frames; the size counts every "
            + "catalogued file in the chosen folders, whatever target or frame type, and reads "
            + "unknown rather than a partial total when any file's size was never recorded. "
            + "Sidecars and files GalactiLog never read are copied too and are not counted, and a "
            + "night with no folder to copy contributes nothing."),

        new("export.quality", "Quality filter",
            "Enable filters turns the chips on; each chip is an absolute limit you type, judged "
            + "per light frame on the metrics that frame carries. Type a limit with a decimal "
            + "point, such as 3.5, whatever your regional format; the eccentricity chip offers "
            + "the presets Strict 0.55, Balanced 0.65 and Relaxed 0.75. A limit on a metric a "
            + "frame does not carry is skipped, never failed, so a guiding limit keeps unguided "
            + "frames; a frame carrying none of the limited metrics reads Unmeasured and is not "
            + "copied, and the Copy box on its row copies one anyway or leaves out one that "
            + "passed. The cell colours grade each frame against the baseline Compare to chooses "
            + "and decide nothing: the limits alone decide the verdict. The limits are remembered "
            + "per rig, and the tally counts every light frame of the checked nights, a wider set "
            + "than the review step's totals."),

        new("export.settings", "Settings",
            "The staging folder is where the copy lands; type it or Browse to it, because "
            + "GalactiLog never picks one for you. It is remembered the moment you choose it; a "
            + "folder inside your library, one that contains it, or one of the folders this "
            + "export copies from is refused. The excluded folder patterns are the names the copy "
            + "skips inside each copied folder, one per line: a pattern matches a whole folder or "
            + "file name, * matches any run of characters, and case is ignored except by the "
            + "shell script. A pattern carrying a quote mark, a dollar sign, a backtick or a "
            + "backslash is refused as you type it. Save as defaults keeps the patterns for next "
            + "time."),

        new("export.script", "Script",
            "The script is a text file GalactiLog wrote once and never runs; you run it, and it "
            + "only copies: no delete, no move, no rename, and it writes only under the staging "
            + "folder. The run command beneath it is the line to paste, and the PowerShell one "
            + "unblocks the file first, which is harmless when the file was never marked. Copy "
            + "script, Show script and the file all carry the same text, and Start another export "
            + "withdraws this section without touching the file already written. If a file of the "
            + "same name is already in the staging folder, the script overwrites it, where Copy "
            + "now skips it; nothing in your library is ever overwritten, moved or deleted. "
            + "GalactiLog counts a kilobyte as 1000 bytes and the script's own console counts it "
            + "as 1024, so the two state slightly different totals for the same files."),

        // The export wizard's steps 3 to 6; export.folders and export.quality above are steps 1
        // and 2. The window's one bound glyph places all six.
        new("export.destination", "Staging folder",
            "The folder the copy lands in, outside your library; type it or Browse to it, and "
            + "it is remembered at once. With the subfolder box on, this export goes into a "
            + "folder named after the target inside it, so exports of several targets can share "
            + "one staging folder; the box is on by default and is not remembered. Nothing is "
            + "created until you commit on the review step. The excluded folder patterns and Save "
            + "as defaults are on this step too."),

        new("export.method", "Copy or script",
            "Copy now has GalactiLog copy the chosen folders itself, with a progress bar and a "
            + "Cancel button, and it never overwrites a file already in the staging folder. "
            + "Generate a script writes a PowerShell (.ps1) or Shell (.sh) script that you run "
            + "yourself, which suits a copy to another machine. Both write into the same staging "
            + "folder and skip the same folders and frames."),

        new("export.review", "Review",
            "What the commit will do; nothing has been written yet. A destination inside your "
            + "library, among the folders being copied or at the top of a drive, or an export "
            + "left with no frames, blocks the commit. Warnings about free space, paths of 260 "
            + "characters or more and files already in the destination do not block it. The "
            + "commit button reads Copy n folders (n frames) or Write script. Once you commit, "
            + "the wizard locks until the copy ends; only Cancel acts, and a cancelled copy keeps "
            + "the files already copied."),

        new("export.result", "Result",
            "What the commit did: the files copied with their size, the files skipped and the "
            + "files that failed. Copy now never overwrites a file already in the staging folder: "
            + "one of the same size counts as already present, and one of a different size is "
            + "listed by path and left untouched. A cancelled or stopped copy lists the files it "
            + "left part written. Running the same export again copies only the files that are "
            + "missing; a part written file is a different size, so it is skipped too until you "
            + "delete it. Open folder and Copy path act on the destination, Start another export "
            + "returns to the first step over the same nights, and Save report writes these lists "
            + "to a text file when something was skipped or failed."),

        // Phase 18 Task 4: the Mosaics page's four glyphs (spec 12.12, 12.17).
        new("mosaics.about", "Mosaics",
            "A mosaic collects the panels of one large field, each panel a set of nights from any "
            + "target. It totals the integration of each panel so you can see which one needs more "
            + "time. Suggestions come from Run Detection and from every scan, and Create mosaic makes "
            + "one by hand. Only light frames are counted."),

        new("mosaics.keywords", "Detection keywords",
            "A keyword is a word that introduces a panel number in a target's name, such as "
            + "Panel in \"M 31 Panel 2\" or P in \"NGC 7000 P3\". A name that ends in a row and "
            + "column pair such as 2-3 is matched without a keyword. Detection reads these tokens "
            + "to group targets into suggested mosaics. Position tolerance (arcmin) is the least "
            + "separation two panel centres need to count as distinct, and closer panels earn a "
            + "review note; 0 derives it from the field of view. A change applies at the next Run "
            + "Detection or scan, not at once."),

        new("mosaics.suggestions", "Suggestions",
            "Each suggestion groups targets that look like panels of one mosaic, found by name, "
            + "position or both, as the source badge says. High confidence means name and "
            + "position agree; low means review the notes before accepting. The Campaign gap "
            + "selector splits nights more than the gap apart into separate suggestions at the "
            + "next Run Detection. Accept creates the mosaic from the checked panels; Accept all "
            + "and Dismiss all act on the checked rows, or on every row when none is checked. "
            + "Dismiss asks twice, and a dismissed suggestion comes back only when new nights of "
            + "its panels are catalogued."),

        new("mosaics.table", "Mosaics table",
            "Every mosaic with its panel count, integration, frames and date range, counting "
            + "light frames only. A click on a row opens the mosaic's page. A header click on a "
            + "built-in column sorts by it, a second click reverses it, and the choice is kept "
            + "for your next visit; a custom column's header does not sort. The column picker "
            + "chooses which columns show, including your own mosaic columns. Expand opens "
            + "Rename, the panel list with Remove, and Add panel; Delete and Delete selected ask "
            + "twice before removing a mosaic and its panels, and no frame is ever touched."),

        // Phase 18 Task 5: the mosaic detail page's four glyphs (spec 12.12, 12.17).
        new("mosaic.about", "Mosaic",
            "This mosaic's panels and the nights that count toward each. The summary line gives "
            + "the panel count, the total integration and the total frames, and each panel row "
            + "shows its own figures, counting included nights only. The Deficit column shows how "
            + "far a panel's integration is behind the leading panel, the one with the most, as a "
            + "time such as 2h 10m behind. Composite builds the panels' best frames in the "
            + "arranger's filter into one image, and the overflow menu holds Export panels (CSV) "
            + "and Delete mosaic, which asks twice."),

        new("mosaic.notes", "Notes",
            "Free-form notes about this mosaic. They are saved one second after you stop "
            + "typing, and an emptied box clears the note. A failed save keeps your text and "
            + "retries on the next edit. No figure on this page is derived from them."),

        new("mosaic.labels", "New panel labels",
            "A panel label is the label carried by a target's frames, such as Panel 2. This "
            + "banner lists labels found on this mosaic's targets that no panel of the mosaic "
            + "carries yet. Their frames count nowhere yet. Add panel creates that panel and "
            + "includes the target's nights that carry the label, skipping any night another "
            + "panel already holds."),

        // Phase 19A Task 5: the arranger's glyph (spec 12.12, 12.17).
        new("mosaic.arranger", "Panels",
            "Each tile is a panel's best frame in the chosen filter, with its label, its "
            + "integration and, when it trails the leading panel by more than a minute, a deficit "
            + "badge coloured success at up to 20 percent behind, warning up to 60, error beyond. "
            + "A panel with no frame in the filter reads, for example, No Ha frames. Drag a tile "
            + "to place it, select or right-click it for Rotate CW and Flip H, turn the whole "
            + "group with the Rotation slider, and Reset all clears every rotation and flip but "
            + "keeps positions. Positions, rotations, flips and the group rotation are saved a "
            + "moment after your last change, with Saving shown; tile opacity, zoom, Labels and "
            + "the filter are not. Fit, the zoom buttons and the wheel change the view only, and "
            + "the layout changes no figure and no composite."),

        // Phase 19B Task 5: the composite lightbox's glyph (spec 12.12, 12.17).
        new("mosaic.composite", "Composite",
            "The composite places every panel's best frame in the chosen filter by its recorded "
            + "sky position and camera angle, not by the arranger's layout. A panel with no frame "
            + "in the filter, or no recorded position, is left out and named under the image. The "
            + "build is a job in the status bar's list, and closing the window cancels it; the "
            + "last 20 composites stay in memory, so reopening one is instant until its best "
            + "frames change. Wheel zooms, drag pans, double-click or 0 fits. Download saves the "
            + "image shown as a JPEG where you choose, writing nothing else."),

        new("mosaic.sessions", "Panels and nights",
            "Each panel lists its Included nights, and its targets' other nights as Available. "
            + "A night is one target, one date and one frame label, and that triple counts in one "
            + "panel only. Include and Remove move a night between the lists, Include all takes "
            + "every available night, and As new panel gives an available night a panel of its "
            + "own. Add nights from any target lists another target's nights as Available; Add "
            + "panel puts a target's nights into a panel of the label you give. Delete panel asks "
            + "twice and is enabled once the panel has no included night."),

        new("mosaic.create", "Create mosaic",
            "Makes a new mosaic, or adds to an existing one that already includes this target, "
            + "from the nights checked on this target. The table has one row per night and frame "
            + "label, and rows given the same panel label combine into one panel; the label "
            + "starts as the frame label when the frames carry one. The name starts as the "
            + "target's base name with the date range and stays until you edit it. Nothing is "
            + "written until you press Create, and a refusal writes nothing."),
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
