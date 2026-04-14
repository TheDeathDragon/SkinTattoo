using System;
using System.IO;

namespace SkinTattoo.Services.Localization;

// Tries overlay (user-editable files) first, then falls back to base (embedded).
public sealed class LayeredLocalizationSource(ILocalizationSource overlay, ILocalizationSource baseSource) : ILocalizationSource
{
    public bool SupportsChangeNotifications => overlay.SupportsChangeNotifications || baseSource.SupportsChangeNotifications;

    public event EventHandler<LocalizationSourceChangedEventArgs>? ResourceChanged
    {
        add
        {
            overlay.ResourceChanged += value;
            baseSource.ResourceChanged += value;
        }
        remove
        {
            overlay.ResourceChanged -= value;
            baseSource.ResourceChanged -= value;
        }
    }

    public bool Exists(string languageCode, string resourceName) =>
        overlay.Exists(languageCode, resourceName) || baseSource.Exists(languageCode, resourceName);

    public Stream? OpenRead(string languageCode, string resourceName) =>
        overlay.OpenRead(languageCode, resourceName) ?? baseSource.OpenRead(languageCode, resourceName);

    public void Dispose()
    {
        overlay.Dispose();
        baseSource.Dispose();
    }
}
