using Xunit;

// AppHostTests builds real IHosts and disposes them, which calls Serilog.Log.CloseAndFlush()
// on the process-global static logger and SqliteConnection.ClearAllPools() on the
// process-global connection pool. WatcherServiceTests and ScanSchedulerTests open their own
// SQLite databases, so a ClearAllPools from another collection running concurrently would
// yank their pooled connections out from under them. xunit parallelizes collections by
// default; this turns that off for the assembly (phase 4 review item 7).
[assembly: CollectionBehavior(DisableTestParallelization = true)]
