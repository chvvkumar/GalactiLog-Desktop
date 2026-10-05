namespace GalactiLog.Core.Settings;

// Thrown by SettingsStore before a write is persisted. Phase 9's Settings screens catch this
// and show the message inline; this task only throws.
public sealed class SettingsValidationException : Exception
{
    public SettingsValidationException(string message) : base(message) { }
}
