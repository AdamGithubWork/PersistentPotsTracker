using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using System.Numerics;

namespace PersistentPotsTracker.Windows;

public class ConfigWindow : Window
{
    private readonly Plugin plugin;

    public ConfigWindow(Plugin plugin)
        : base("Persistent Pots Tracker Settings###PersistentPotsTrackerConfigWindow")
    {
        this.plugin = plugin;

        Size = new Vector2(400, 200);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        ImGui.Text("Persistent Pots Tracker Settings");
        ImGui.Separator();

        var showOverlay = plugin.Configuration.ShowPotOverlay;

        if (ImGui.Checkbox("Show timer overlay", ref showOverlay))
        {
            plugin.Configuration.ShowPotOverlay = showOverlay;
            plugin.Configuration.Save();
        }

        var useLifestream = plugin.Configuration.UseLifestream;

        if (ImGui.Checkbox("Use Lifestream", ref useLifestream))
        {
            plugin.Configuration.UseLifestream = useLifestream;
            plugin.Configuration.Save();
        }

        var colorCode = plugin.Configuration.ColorCodeDatacenters;

        if (ImGui.Checkbox("Color-code datacenters", ref colorCode))
        {
            plugin.Configuration.ColorCodeDatacenters = colorCode;
            plugin.Configuration.Save();
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Tracker data automatically refreshes every 45 seconds.");
    }
}
