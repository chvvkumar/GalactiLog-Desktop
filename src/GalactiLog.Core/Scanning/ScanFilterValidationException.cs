namespace GalactiLog.Core.Scanning;

// Thrown by ScanFilterConfig.Validate naming the first problem found (not an aggregate --
// one bad row is enough to stop a scan). See design-spec 2.3, 10.2.
public sealed class ScanFilterValidationException : Exception
{
    public ScanFilterValidationException(string message) : base(message)
    {
    }
}
