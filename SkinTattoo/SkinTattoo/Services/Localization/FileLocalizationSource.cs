using System;
using System.IO;

namespace SkinTattoo.Services.Localization;

public sealed class FileLocalizationSource(string directoryPath) : ILocalizationSource
{
    private readonly string directoryPath = Path.GetFullPath(directoryPath);

    private FileSystemWatcher? watcher;
    private EventHandler<LocalizationSourceChangedEventArgs>? resourceChanged;

    public bool SupportsChangeNotifications => true;

    public event EventHandler<LocalizationSourceChangedEventArgs>? ResourceChanged
    {
        add
        {
            resourceChanged += value;
            EnsureWatcher();
        }
        remove
        {
            resourceChanged -= value;
            if (resourceChanged == null)
                DisposeWatcher();
        }
    }

    public bool Exists(string languageCode, string resourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        return File.Exists(GetFilePath(resourceName));
    }

    public Stream? OpenRead(string languageCode, string resourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        var filePath = GetFilePath(resourceName);
        if (!File.Exists(filePath))
            return null;
        return new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }

    public void Dispose() => DisposeWatcher();

    private string GetFilePath(string resourceName) => Path.Join(directoryPath, resourceName);

    private void EnsureWatcher()
    {
        if (watcher != null) return;
        Directory.CreateDirectory(directoryPath);

        watcher = new FileSystemWatcher(directoryPath)
        {
            Filter = "*.*",
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };

        watcher.Changed += OnWatcherChanged;
        watcher.Created += OnWatcherChanged;
        watcher.Renamed += OnWatcherChanged;
        watcher.Deleted += OnWatcherChanged;
    }

    private void DisposeWatcher()
    {
        if (watcher == null) return;
        watcher.Changed -= OnWatcherChanged;
        watcher.Created -= OnWatcherChanged;
        watcher.Renamed -= OnWatcherChanged;
        watcher.Deleted -= OnWatcherChanged;
        watcher.Dispose();
        watcher = null;
    }

    private void OnWatcherChanged(object sender, FileSystemEventArgs e)
    {
        var handler = resourceChanged;
        handler?.Invoke(this, new LocalizationSourceChangedEventArgs(e.Name ?? string.Empty, e.ChangeType));
    }
}
