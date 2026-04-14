using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace SkinTattoo.Services.Localization;

public sealed class LocalizationOptions
{
    public required FrozenDictionary<string, string> SupportedLanguages { get; init; }
    public required string DefaultLanguage { get; init; }
    public required Func<string, string> FileNameResolver { get; init; }
    public required ILocalizationSource Source { get; init; }
    public required ILocalizationParser Parser { get; init; }
    public Func<string, IEnumerable<string>> FallbackResolver { get; init; } = _ => [];
    public bool EnableHotReload { get; init; } = false;
    public string LoggerTag { get; init; } = "Localization";
}
