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

    // Flags have to be set before the window begins, so a change to the
    // checkbox applies on the same frame.
    public override void PreDraw()
    {
        Flags = plugin.Configuration.IsConfigWindowMovable
            ? ImGuiWindowFlags.None
            : ImGuiWindowFlags.NoMove;
    }

    public override void Draw()
    {
        ImGui.Text("Persistent Pots Tracker Settings");
        ImGui.Separator();

        var movable = plugin.Configuration.IsConfigWindowMovable;

        if (ImGui.Checkbox("Allow settings window movement", ref movable))
        {
            plugin.Configuration.IsConfigWindowMovable = movable;
            plugin.Configuration.Save();
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Tracker data automatically refreshes every 45 seconds.");
    }
}
