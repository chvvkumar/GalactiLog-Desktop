using System.Runtime.CompilerServices;

// Lets GalactiLog.Data.Tests drive ScanCoordinator.RaiseProgress directly, so the spec 10.4
// throttle can be asserted on an event count rather than raced against a wall clock.
[assembly: InternalsVisibleTo("GalactiLog.Data.Tests")]

// Phase 5 Task 5: GalactiLog.App.Tests drives the same RaiseProgress seam to assert
// ScanStatusService's envelope marshalling on an event it fully controls, rather than
// coupling those tests to a real scan's message text and step counts.
[assembly: InternalsVisibleTo("GalactiLog.App.Tests")]
