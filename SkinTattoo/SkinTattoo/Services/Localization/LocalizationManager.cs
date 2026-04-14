using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace SkinTattoo.Services.Localization;

public sealed class LocalizationManager : IDisposable
{
    private static readonly FrozenDictionary<string, string> EmptyResource =
        new Dictionary<string, string>(StringComparer.Ordinal).ToFrozenDictionary(StringComparer.Ordinal);

    private LocalizationOptions? options;
    private LanguageSnapshot currentSnapshot = LanguageSnapshot.Empty;
    private EventHandler<LocalizationSourceChangedEventArgs>? changeHandler;

    public event Action? LanguageChanged;

    public bool IsConfigured => Volatile.Read(ref options) != null;

    public FrozenDictionary<string, string> SupportedLanguages =>
        GetOptions().SupportedLanguages;

    public FrozenDictionary<string, string> AvailableLanguages =>
        Volatile.Read(ref currentSnapshot).AvailableLanguages;

    public string CurrentLanguage =>
        Volatile.Read(ref currentSnapshot).Language;

    public void Configure(LocalizationOptions opts, string initialLanguage)
    {
        ArgumentNullException.ThrowIfNull(opts);
        ValidateOptions(opts);

        DetachHandler();
        options = opts;

        if (opts.EnableHotReload && opts.Source.SupportsChangeNotifications)
        {
            changeHandler = OnSourceChanged;
            opts.Source.ResourceChanged += changeHandler;
        }

        var normalized = NormalizeLanguage(initialLanguage);
        LoadLanguage(normalized);
    }

    public string NormalizeLanguage(string requested)
    {
        var opts = GetOptions();
        var available = EnumerateAvailableLanguages(opts);

        if (IsAvailable(opts, available, requested))
            return requested;

        foreach (var fb in opts.FallbackResolver(requested))
        {
            if (IsAvailable(opts, available, fb))
                return fb;
        }

        return opts.DefaultLanguage;
    }

    public void LoadLanguage(string language)
    {
        var opts = GetOptions();
        if (!opts.SupportedLanguages.ContainsKey(language))
            throw new ArgumentOutOfRangeException(nameof(language), $"Language {language} not in supported list");

        var snapshot = BuildSnapshot(opts, language);
        Interlocked.Exchange(ref currentSnapshot, snapshot);
        LanguageChanged?.Invoke();
    }

    public string Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var snap = Volatile.Read(ref currentSnapshot);
        return TryResolve(snap, key, out var fmt) ? fmt : LogMissing(snap, key);
    }

    public string Get(string key, params object[] args)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0) return Get(key);

        var snap = Volatile.Read(ref currentSnapshot);
        if (!TryResolve(snap, key, out var fmt))
            return LogMissing(snap, key);

        try
        {
            return string.Format(CultureInfo.CurrentCulture, fmt, args);
        }
        catch (FormatException)
        {
            return fmt;
        }
    }

    public void Dispose()
    {
        DetachHandler();
        options?.Source.Dispose();
        options = null;
        Interlocked.Exchange(ref currentSnapshot, LanguageSnapshot.Empty);
    }

    private void DetachHandler()
    {
        if (options != null && changeHandler != null)
            options.Source.ResourceChanged -= changeHandler;
        changeHandler = null;
    }

    private LocalizationOptions GetOptions() =>
        Volatile.Read(ref options) ?? throw new InvalidOperationException("LocalizationManager not configured");

    private static void ValidateOptions(LocalizationOptions o)
    {
        if (o.SupportedLanguages.Count == 0)
            throw new ArgumentException("SupportedLanguages must not be empty");
        if (!o.SupportedLanguages.ContainsKey(o.DefaultLanguage))
            throw new ArgumentException($"DefaultLanguage {o.DefaultLanguage} not in SupportedLanguages");
    }

    private void OnSourceChanged(object? sender, LocalizationSourceChangedEventArgs e)
    {
        try { LoadLanguage(CurrentLanguage); }
        catch { }
    }

    private LanguageSnapshot BuildSnapshot(LocalizationOptions opts, string language)
    {
        var chain = EnumerateResourceLanguages(opts, language);
        var resources = new List<FrozenDictionary<string, string>>(chain.Count);

        foreach (var lang in chain)
        {
            var res = LoadLanguageResource(opts, lang);
            if (res.Count > 0) resources.Add(res);
        }

        return new LanguageSnapshot(language, EnumerateAvailableLanguages(opts), resources.ToArray(), opts.LoggerTag);
    }

    private static List<string> EnumerateResourceLanguages(LocalizationOptions opts, string language)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();

        Add(language);
        foreach (var fb in opts.FallbackResolver(language)) Add(fb);
        Add(opts.DefaultLanguage);
        return ordered;

        void Add(string v)
        {
            if (!opts.SupportedLanguages.ContainsKey(v)) return;
            if (!seen.Add(v)) return;
            ordered.Add(v);
        }
    }

    private static bool IsAvailable(LocalizationOptions opts, FrozenDictionary<string, string> available, string lang) =>
        opts.SupportedLanguages.ContainsKey(lang) && available.ContainsKey(lang);

    private static bool TryResolve(LanguageSnapshot snap, string key, out string format)
    {
        foreach (var r in snap.Resources)
        {
            if (r.TryGetValue(key, out var v)) { format = v; return true; }
        }
        format = string.Empty;
        return false;
    }

    private static FrozenDictionary<string, string> LoadLanguageResource(LocalizationOptions opts, string language)
    {
        try
        {
            var name = opts.FileNameResolver(language);
            if (string.IsNullOrWhiteSpace(name)) return EmptyResource;

            using var stream = opts.Source.OpenRead(language, name);
            if (stream == null) return EmptyResource;

            var res = opts.Parser.Parse(stream);
            return res.Count == 0 ? EmptyResource : res;
        }
        catch (Exception ex)
        {
            Plugin.Log?.Error(ex, "[{0}] Failed to load language {1}", opts.LoggerTag, language);
            return EmptyResource;
        }
    }

    private static FrozenDictionary<string, string> EnumerateAvailableLanguages(LocalizationOptions opts)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in opts.SupportedLanguages)
        {
            var name = opts.FileNameResolver(kv.Key);
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!opts.Source.Exists(kv.Key, name)) continue;
            dict[kv.Key] = kv.Value;
        }
        return dict.Count == 0
            ? FrozenDictionary<string, string>.Empty
            : dict.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static string LogMissing(LanguageSnapshot snap, string key)
    {
        if (snap.MissingKeys.TryAdd(key, 0))
            Plugin.Log?.Warning("[{0}] Missing localization key: {1}", snap.LoggerTag, key);
        return key;
    }

    private sealed class LanguageSnapshot(
        string language,
        FrozenDictionary<string, string> availableLanguages,
        FrozenDictionary<string, string>[] resources,
        string loggerTag)
    {
        public static LanguageSnapshot Empty { get; } =
            new(string.Empty, FrozenDictionary<string, string>.Empty, [], "Localization");

        public string Language { get; } = language;
        public FrozenDictionary<string, string> AvailableLanguages { get; } = availableLanguages;
        public FrozenDictionary<string, string>[] Resources { get; } = resources;
        public string LoggerTag { get; } = loggerTag;
        public ConcurrentDictionary<string, byte> MissingKeys { get; } = new(StringComparer.Ordinal);
    }
}
