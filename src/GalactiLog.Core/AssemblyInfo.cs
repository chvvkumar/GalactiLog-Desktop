using System.Runtime.CompilerServices;

// Lets GalactiLog.Core.Tests exercise internal members directly (AppWriter.IsUnder,
// review item 7) instead of only indirectly through public callers.
[assembly: InternalsVisibleTo("GalactiLog.Core.Tests")]
