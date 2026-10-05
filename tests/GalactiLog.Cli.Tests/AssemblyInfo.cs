using Xunit;

// Every test in this assembly drives CliDispatcher through the real Console, redirecting the
// process-global Console.Out/Console.Error, and several dispose an IHost, which calls
// Serilog.Log.CloseAndFlush() on the process-global static logger. None of that is
// per-collection state, so two collections running concurrently would capture each other's
// output or flush each other's logger. xunit parallelizes collections by default; this turns
// that off for the assembly (Task 8 review, item 3).
[assembly: CollectionBehavior(DisableTestParallelization = true)]
