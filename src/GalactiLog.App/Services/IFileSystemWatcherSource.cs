namespace GalactiLog.App.Services;

// The one test seam WatcherService needs (design-spec 10.7): a real FileSystemWatcher is
// driven by the OS, so a unit test cannot make it raise an event at a chosen moment. Every
// other collaborator of WatcherService is either a real object (SettingsStore) or an
// injected delegate.
public interface IFileSystemWatcherSource : IDisposable
{
    // Full path of a file that was created, written to, or renamed. One event per raw OS
    // notification; WatcherService does the debouncing.
    event EventHandler<string>? Changed;

    // The watcher lost notifications (internal buffer overflow, or the watched directory
    // became unreachable). Nothing can be recovered from the event stream at that point, so
    // WatcherService escalates to a full scan.
    event EventHandler? Error;

    void Start();
}

// Production implementation: one real FileSystemWatcher over one scan root. Read-only
// observation -- FileSystemWatcher never touches the files it reports (spec 2.1.1).
public sealed class FileSystemWatcherSource : IFileSystemWatcherSource
{
    private readonly FileSystemWatcher _watcher;

    public FileSystemWatcherSource(string path)
    {
        _watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = true,
            // 64 KB is the documented maximum that still works over a network share; a
            // larger buffer makes the Error path (overflow) rarer but never impossible,
            // which is why the full-scan escalation exists.
            InternalBufferSize = 65536,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };

        _watcher.Created += OnFileEvent;
        _watcher.Changed += OnFileEvent;
        _watcher.Renamed += (sender, e) => OnFileEvent(sender, e);
        _watcher.Error += (_, _) => Error?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler<string>? Changed;
    public event EventHandler? Error;

    public void Start() => _watcher.EnableRaisingEvents = true;

    public void Dispose() => _watcher.Dispose();

    private void OnFileEvent(object sender, FileSystemEventArgs e) => Changed?.Invoke(this, e.FullPath);
}
