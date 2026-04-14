using System;
using System.IO;

namespace SkinTattoo.Services.Localization;

public interface ILocalizationSource : IDisposable
{
    bool SupportsChangeNotifications { get; }

    event EventHandler<LocalizationSourceChangedEventArgs>? ResourceChanged;

    bool Exists(string languageCode, string resourceName);

    Stream? OpenRead(string languageCode, string resourceName);
}

public sealed class LocalizationSourceChangedEventArgs(
    string resourceName,
    WatcherChangeTypes changeType) : EventArgs
{
    public string ResourceName { get; } = resourceName;
    public WatcherChangeTypes ChangeType { get; } = changeType;
}
