using Dalamud.Configuration;
using Dalamud.Plugin;
using System;

namespace PersistentPotsTracker;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool ShowAether { get; set; } = true;
    public bool ShowPrimal { get; set; } = true;
    public bool ShowCrystal { get; set; } = true;
    public bool ShowDynamis { get; set; } = true;
    public bool ShowMateria { get; set; } = true;
    public bool IsConfigWindowMovable { get; set; } = true;

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pluginInterface) => this.pluginInterface = pluginInterface;

    public void Save() => pluginInterface?.SavePluginConfig(this);
}
