namespace GalactiLog.Core.Targets;

// Raised by SimbadClient/SesameClient for any HTTP 4xx response other than 429 (rate limit),
// design-spec 9.6. Task 6's retry/cache wrapper must not retry this and must not write a
// cache row for it. Both clients throw the same exception type so Task 6 has one type to
// catch regardless of which client is in play.
public sealed class NonTransientCatalogException(string message) : Exception(message);
