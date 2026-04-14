namespace SkinTattoo.Services.Localization;

// Short facade for UI call sites: Strings.T("key") / Strings.T("key", arg0)
public static class Strings
{
    private static LocalizationManager? manager;

    public static LocalizationManager Manager =>
        manager ?? throw new System.InvalidOperationException("Strings.Manager not initialized");

    public static void Attach(LocalizationManager m) => manager = m;

    public static string T(string key) =>
        manager == null ? key : manager.Get(key);

    public static string T(string key, params object[] args) =>
        manager == null ? key : manager.Get(key, args);
}
