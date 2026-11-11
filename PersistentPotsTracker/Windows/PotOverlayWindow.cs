
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
    private string lastRegion = "";
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

    public override bool DrawConditions() => plugin.Configuration.ShowPotOverlay;

    public override void Draw()
    {
        var region = GetRegion(plugin.GetCurrentDatacenter());
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        UpdateNextPot(region, now);

        if (ImGui.Button($"{BuildLabel(region, now)}###PotOverlayButton"))
            plugin.OpenTrackerWindow();

        if (ImGui.IsItemHovered())
            DrawTooltip(region, now);
    }

    private string BuildLabel(string region, long now)
    {
        if (region == "Unknown")
            return "Pots: Region unknown";

        if (cachedNextPot == null)
            return $"{region} Pots: --:--";

        var zone = cachedNextPot.Zone switch
        {
            "South Horn" => "South",
            "North Horn" => "North",
            _ => cachedNextPot.Zone
        };

        return $"{region} | {zone}: {FormatCountdown(cachedNextPot.NextSpawnTime - now)}";
    }

    private void UpdateNextPot(string region, long now)
    {
        var source = plugin.PotRows;

        if (ReferenceEquals(lastSource, source) &&
            lastRegion == region &&
            (cachedNextPot == null || cachedNextPot.NextSpawnTime > now))
        {
            return;
        }

        lastSource = source;
        lastRegion = region;
        cachedNextPot = null;

        foreach (var row in source)
        {
            if (GetRegion(row.Datacenter) != region || row.NextSpawnTime <= now)
                continue;

            if (cachedNextPot == null || row.NextSpawnTime < cachedNextPot.NextSpawnTime)
                cachedNextPot = row;
        }
    }

    private void DrawTooltip(string region, long now)
    {
        if (region == "Unknown")
        {
            ImGui.SetTooltip("Waiting for current region information");
            return;
        }

        if (cachedNextPot == null)
        {
            ImGui.SetTooltip("No upcoming Pot prediction available");
            return;
        }

        ImGui.SetTooltip(
            $"Next predicted Pot in {region}\n" +
            $"Zone: {cachedNextPot.Zone}\n" +
            $"Pot location: {cachedNextPot.NextPot}\n" +
            $"Countdown: {FormatCountdown(cachedNextPot.NextSpawnTime - now)}\n\n" +
            "Click to open Persistent Pots Tracker");
    }

    private static string GetRegion(string datacenter) =>
        datacenter is "Aether" or "Primal" or "Crystal" or "Dynamis" or "North America"
            ? "North America"
            : datacenter;

    private static string FormatCountdown(long seconds)
    {
        if (seconds <= 0)
            return "00:00";

        var t = TimeSpan.FromSeconds(seconds);

        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes:D2}:{t.Seconds:D2}";
    }
}
