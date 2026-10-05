using System.Runtime.CompilerServices;

// Lets GalactiLog.App.Tests reach AppHost's internal Build overload and its two Phase 10 Task 9
// seams, pointerPathOverride and folderOverride, so a startup test drives the pointer, the pending
// move and the legacy adoption without touching the real %APPDATA%\GalactiLog\datapath.json,
// %LOCALAPPDATA%\GalactiLogData or %LOCALAPPDATA%\GalactiLog. The predicate this comment used to
// name, AppHost.IsUsableAppDataOverride, moved to AppDataRootResolver.IsUsableRoot and is public.
[assembly: InternalsVisibleTo("GalactiLog.App.Tests")]
