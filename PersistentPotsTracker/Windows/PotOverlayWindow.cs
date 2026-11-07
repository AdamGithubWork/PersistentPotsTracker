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

    // The window stays "open" either way; this just skips drawing it when the
    // setting is off.
    public override bool DrawConditions() => plugin.Configuration.ShowPotOverlay;

    public override void Draw()
    {
        var datacenter = plugin.GetCurrentDatacenter();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        UpdateNextPot(datacenter, now);

        // The ### keeps the button ID constant while the label text changes.
        if (ImGui.Button($"{BuildLabel(datacenter, now)}###PotOverlayButton"))
            plugin.OpenTrackerWindow();

        if (ImGui.IsItemHovered())
            DrawTooltip(datacenter, now);
    }

    private string BuildLabel(string datacenter, long now)
    {
        if (datacenter == "Unknown")
            return "Pots: Datacenter unknown";

        if (cachedNextPot == null)
            return $"{datacenter} Pots: --:--";

        var zone = cachedNextPot.Zone switch
        {
            "South Horn" => "South",
            "North Horn" => "North",
            _ => cachedNextPot.Zone
        };

        return $"{datacenter} | {zone}: {FormatCountdown(cachedNextPot.NextSpawnTime - now)}";
    }

    // The soonest upcoming pot on our datacenter. Cached so we don't scan every
    // frame; recomputed when the data or datacenter changes, or once the cached
    // pot has spawned and a later one should take over.
    private void UpdateNextPot(string datacenter, long now)
    {
        var source = plugin.PotRows;

        if (ReferenceEquals(lastSource, source) &&
            lastDatacenter == datacenter &&
            (cachedNextPot == null || cachedNextPot.NextSpawnTime > now))
        {
            return;
        }

        lastSource = source;
        lastDatacenter = datacenter;
        cachedNextPot = null;

        foreach (var row in source)
        {
            if (row.Datacenter != datacenter || row.NextSpawnTime <= now)
                continue;

            if (cachedNextPot == null || row.NextSpawnTime < cachedNextPot.NextSpawnTime)
                cachedNextPot = row;
        }
    }

    private void DrawTooltip(string datacenter, long now)
    {
        if (datacenter == "Unknown")
        {
            ImGui.SetTooltip("Waiting for current datacenter information");
            return;
        }

        if (cachedNextPot == null)
        {
            ImGui.SetTooltip("No upcoming Pot prediction available");
            return;
        }

        ImGui.SetTooltip(
            $"Next predicted Pot on {datacenter}\n" +
            $"Zone: {cachedNextPot.Zone}\n" +
            $"Pot location: {cachedNextPot.NextPot}\n" +
            $"Countdown: {FormatCountdown(cachedNextPot.NextSpawnTime - now)}\n\n" +
            "Click to open Persistent Pots Tracker");
    }

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
