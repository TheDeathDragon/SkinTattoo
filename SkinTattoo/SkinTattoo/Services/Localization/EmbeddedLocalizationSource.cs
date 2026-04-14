using System;
using System.IO;
using System.Reflection;

namespace SkinTattoo.Services.Localization;

public sealed class EmbeddedLocalizationSource(Assembly assembly, string resourcePrefix) : ILocalizationSource
{
    private readonly Assembly assembly = assembly;
    private readonly string resourcePrefix = resourcePrefix.EndsWith('.') ? resourcePrefix : resourcePrefix + ".";

    public bool SupportsChangeNotifications => false;

    public event EventHandler<LocalizationSourceChangedEventArgs>? ResourceChanged
    {
        add { }
        remove { }
    }

    public bool Exists(string languageCode, string resourceName)
    {
        var full = resourcePrefix + resourceName;
        using var stream = assembly.GetManifestResourceStream(full);
        return stream != null;
    }

    public Stream? OpenRead(string languageCode, string resourceName)
    {
        var full = resourcePrefix + resourceName;
        return assembly.GetManifestResourceStream(full);
    }

    public void Dispose() { }
}
