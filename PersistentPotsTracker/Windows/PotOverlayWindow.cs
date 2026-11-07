
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using PersistentPotsTracker.Models;
using System;
using System.Collections.Generic;

namespace PersistentPotsTracker.Windows;

public class PotOverlayWindow : Window
{
    private readonly Plugin plugin;

    private List<PotTableRow>? lastSource;
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
        var datacenter = plugin.GetCurrentDatacenter();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        UpdateNextPot(datacenter, now);

        string label;

        if (datacenter == "Unknown")
        {
            label = "Pots: Datacenter unknown";
        }
        else if (cachedNextPot == null)
        {
            label = $"{datacenter} Pots: --:--";
        }
        else
        {
            var remaining = cachedNextPot.NextSpawnTime - now;
            var countdown = FormatCountdown(remaining);

            var zone = cachedNextPot.Zone switch
            {
                "South Horn" => "South",
                "North Horn" => "North",
                _ => cachedNextPot.Zone
            };

            label = $"{datacenter} | {zone}: {countdown}";
        }

        // Keep the button ID constant even as the countdown changes.
        if (ImGui.Button($"{label}###PotOverlayButton"))
        {
            plugin.OpenTrackerWindow();
        }

        if (ImGui.IsItemHovered())
        {
            DrawTooltip(datacenter, now);
        }
    }

    private void UpdateNextPot(string datacenter, long now)
    {
        if (ReferenceEquals(lastSource, plugin.PotRows) &&
            lastDatacenter == datacenter &&
            (cachedNextPot == null || cachedNextPot.NextSpawnTime > now))
        {
            return;
        }

        lastSource = plugin.PotRows;
        lastDatacenter = datacenter;
        cachedNextPot = null;

        foreach (var row in plugin.PotRows)
        {
            if (row.Datacenter != datacenter ||
                row.NextSpawnTime <= now)
            {
                continue;
            }

            if (cachedNextPot == null ||
                row.NextSpawnTime < cachedNextPot.NextSpawnTime)
            {
                cachedNextPot = row;
            }
        }
    }

    private void DrawTooltip(string datacenter, long now)
    {
        if (datacenter == "Unknown")
        {
            ImGui.SetTooltip(
                "Waiting for current datacenter information");
            return;
        }

        if (cachedNextPot == null)
        {
            ImGui.SetTooltip(
                "No upcoming Pot prediction available");
            return;
        }

        var remaining = cachedNextPot.NextSpawnTime - now;
        var countdown = FormatCountdown(remaining);

        var tooltip =
            $"Next predicted Pot on {datacenter}\n" +
            $"Zone: {cachedNextPot.Zone}\n" +
            $"Pot location: {cachedNextPot.NextPot}\n" +
            $"Countdown: {countdown}\n\n" +
            "Click to open Persistent Pots Tracker";

        ImGui.SetTooltip(tooltip);
    }

    private static string FormatCountdown(long seconds)
    {
        if (seconds <= 0)
            return "00:00";

        var duration = TimeSpan.FromSeconds(seconds);

        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours:D2}:" +
                   $"{duration.Minutes:D2}:" +
                   $"{duration.Seconds:D2}";
        }

        return $"{duration.Minutes:D2}:{duration.Seconds:D2}";
    }
}
