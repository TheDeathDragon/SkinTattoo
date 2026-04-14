using System.Collections.Frozen;
using System.IO;

namespace SkinTattoo.Services.Localization;

public interface ILocalizationParser
{
    FrozenDictionary<string, string> Parse(Stream stream);
}
