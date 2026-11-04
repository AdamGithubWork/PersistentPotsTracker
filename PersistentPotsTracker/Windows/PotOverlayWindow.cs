using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using PersistentPotsTracker.Models;
using System;

namespace PersistentPotsTracker.Windows;

public class PotOverlayWindow : Window
{
    private readonly Plugin plugin;
    private System.Collections.Generic.List<PotTableRow>? lastSource;
    private string lastDatacenter = "";
    private PotTableRow? cachedNextPot;

    public PotOverlayWindow(Plugin plugin)
        : base("Pot Overlay###PersistentPotsOverlay")
    {
        this.plugin = plugin;
        Flags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoFocusOnAppearing;
        IsOpen = true;
        RespectCloseHotkey = false;
    }

    public override void Draw()
    {
        string datacenter = plugin.GetCurrentDatacenter();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Only search again if tracker data or datacenter changes, or the
        // previously selected prediction has passed its spawn time.
        if (!ReferenceEquals(lastSource, plugin.PotRows) ||
            lastDatacenter != datacenter ||
            (cachedNextPot != null && cachedNextPot.NextSpawnTime <= now))
        {
            lastSource = plugin.PotRows;
            lastDatacenter = datacenter;
            cachedNextPot = null;
            foreach (var row in plugin.PotRows)
            {
                if (row.Datacenter != datacenter || row.NextSpawnTime <= now)
                    continue;
                if (cachedNextPot == null || row.NextSpawnTime < cachedNextPot.NextSpawnTime)
                    cachedNextPot = row;
            }
        }

        string label;
        string tooltip;
        if (datacenter == "Unknown")
        {
            label = "Pots: Datacenter unknown";
            tooltip = "Waiting for current datacenter information";
        }
        else if (cachedNextPot == null)
        {
            label = $"{datacenter} Pots: --:--";
            tooltip = "No upcoming Pot prediction available";
        }
        else
        {
            long remaining = cachedNextPot.NextSpawnTime - now;
            var duration = TimeSpan.FromSeconds(remaining);
            string countdown = duration.TotalHours >= 1
                ? $"{(int)duration.TotalHours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}"
                : $"{duration.Minutes:D2}:{duration.Seconds:D2}";
            string zone = cachedNextPot.Zone switch
            {
                "South Horn" => "South",
                "North Horn" => "North",
                _ => "Unknown"
            };
            label = $"{datacenter} | {zone}: {countdown}";
            tooltip =
                $"Next predicted Pot on {datacenter}\n" +
                $"Zone: {cachedNextPot.Zone}\n" +
                $"Pot location: {cachedNextPot.NextPot}\n" +
                $"Countdown: {countdown}\n\n" +
                "Click to open Persistent Pots Tracker";
        }

        if (ImGui.Button(label))
            plugin.OpenTrackerWindow();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
    }
}
