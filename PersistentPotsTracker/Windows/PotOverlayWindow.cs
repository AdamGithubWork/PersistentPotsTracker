
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using PersistentPotsTracker.Models;
using System;
using System.Collections.Generic;

namespace PersistentPotsTracker.Windows;

public class PotOverlayWindow : Window
{
    private const long SpawnNoticeSeconds = 60;

    private readonly Plugin plugin;

    private List<PotTableRow>? lastSource;
    private string lastRegion = "";
    private PotTableRow? cachedNextPot;
    private PotTableRow? spawnedPot;

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

        if (spawnedPot != null)
        {
            var elapsed = now - spawnedPot.NextSpawnTime;

            return $"{region} | Pots ready / overdue ({elapsed / 60}:{elapsed % 60:D2})\n" +
                   $"Next pots: {GetNextPotLabel(now)}";
        }

        if (cachedNextPot == null)
            return $"{region} Pots: --:--";

        return $"{region} | {GetZoneName(cachedNextPot.Zone)}: " +
               FormatCountdown(cachedNextPot.NextSpawnTime - now);
    }

    private string GetNextPotLabel(long now)
    {
        if (cachedNextPot == null)
            return "--:--";

        return $"{GetZoneName(cachedNextPot.Zone)} " +
               FormatCountdown(cachedNextPot.NextSpawnTime - now);
    }

    private void UpdateNextPot(string region, long now)
    {
        var source = plugin.PotRows;

        if (lastRegion != region)
        {
            lastRegion = region;
            lastSource = null;
            cachedNextPot = null;
            spawnedPot = null;
        }

        var expired = cachedNextPot != null &&
                      now >= cachedNextPot.NextSpawnTime;

        if (expired)
        {
            if (now - cachedNextPot!.NextSpawnTime < SpawnNoticeSeconds)
                spawnedPot = cachedNextPot;

            cachedNextPot = null;
        }

        if (spawnedPot != null &&
            now - spawnedPot.NextSpawnTime >= SpawnNoticeSeconds)
        {
            spawnedPot = null;
        }

        if (!expired && ReferenceEquals(lastSource, source))
            return;

        lastSource = source;
        cachedNextPot = null;

        foreach (var row in source)
        {
            if (GetRegion(row.Datacenter) != region ||
                row.NextSpawnTime <= now)
                continue;

            if (cachedNextPot == null ||
                row.NextSpawnTime < cachedNextPot.NextSpawnTime)
            {
                cachedNextPot = row;
            }
        }
    }

    private void DrawTooltip(string region, long now)
    {
        if (region == "Unknown")
        {
            ImGui.SetTooltip("Waiting for current region information");
            return;
        }

        if (spawnedPot != null)
        {
            var elapsed = now - spawnedPot.NextSpawnTime;

            ImGui.SetTooltip(
                $"Pot predicted to have spawned in {spawnedPot.Zone}\n" +
                $"Pot location: {spawnedPot.NextPot}\n" +
                $"Time since prediction: {elapsed / 60}:{elapsed % 60:D2}\n" +
                $"Next pots: {GetNextPotLabel(now)}\n\n" +
                "Click to open Persistent Pots Tracker");
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

    private static string GetZoneName(string zone) => zone switch
    {
        "South Horn" => "South",
        "North Horn" => "North",
        _ => zone
    };

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
