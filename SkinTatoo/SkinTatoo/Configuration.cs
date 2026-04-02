using System;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace SkinTatoo;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public int HttpPort { get; set; } = 14780;
    public int TextureResolution { get; set; } = 1024;
    public Core.SkinTarget LastTarget { get; set; } = Core.SkinTarget.Body;

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi) => pluginInterface = pi;
    public void Save() => pluginInterface?.SavePluginConfig(this);
}
