using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using PersistentPotsTracker.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace PersistentPotsTracker.Windows;

public class MainWindow : Window
{
    private readonly Plugin plugin;
    private List<PotTableRow> sortedRows = new();
    private List<PotTableRow>? lastSource;
    private string lastSortKey = "";

    public MainWindow(Plugin plugin)
        : base("Persistent Pots Tracker###PersistentPotsTrackerMainWindow")
    {
        this.plugin = plugin;
        Size = new Vector2(950, 500);
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags = ImGuiWindowFlags.NoCollapse;
    }

    public override void Draw()
    {
        DrawDatacenterFilters();
        ImGui.Separator();
        DrawRefreshControls();
        ImGui.Separator();
        DrawPotTable();
    }

    private void DrawDatacenterFilters()
    {
        ImGui.Text("Datacenters");
        bool aether = plugin.Configuration.ShowAether;
        bool primal = plugin.Configuration.ShowPrimal;
        bool crystal = plugin.Configuration.ShowCrystal;
        bool dynamis = plugin.Configuration.ShowDynamis;
        bool changed = false;

        if (ImGui.Checkbox("Aether", ref aether))
        {
            plugin.Configuration.ShowAether = aether;
            changed = true;
        }
        ImGui.SameLine();
        if (ImGui.Checkbox("Primal", ref primal))
        {
            plugin.Configuration.ShowPrimal = primal;
            changed = true;
        }
        ImGui.SameLine();
        if (ImGui.Checkbox("Crystal", ref crystal))
        {
            plugin.Configuration.ShowCrystal = crystal;
            changed = true;
        }
        ImGui.SameLine();
        if (ImGui.Checkbox("Dynamis", ref dynamis))
        {
            plugin.Configuration.ShowDynamis = dynamis;
            changed = true;
        }
        if (changed)
        {
            plugin.Configuration.Save();
            plugin.DatacenterSelectionChanged();
        }
    }

    private void DrawRefreshControls()
    {
        if (ImGui.Button("Refresh Pots"))
            _ = plugin.RefreshTrackersAsync();

        ImGui.SameLine();
        if (plugin.IsRefreshing)
            ImGui.TextDisabled("Refreshing...");
        else
            ImGui.Text($"Instances found: {plugin.Trackers.Count}");

        if (plugin.MatchedTrackerId.HasValue)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.3f, 1f, 0.3f, 1f), "Current instance identified");
        }

        if (!string.IsNullOrEmpty(plugin.ApiError))
            ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), $"API Error: {plugin.ApiError}");
    }

    private void DrawPotTable()
    {
        var flags =
            ImGuiTableFlags.Borders |
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.Resizable |
            ImGuiTableFlags.ScrollY |
            ImGuiTableFlags.SizingStretchProp |
            ImGuiTableFlags.Sortable |
            ImGuiTableFlags.SortMulti;

        if (!ImGui.BeginTable("PotTimerTable", 6, flags))
            return;

        ImGui.TableSetupColumn("Datacenter");
        ImGui.TableSetupColumn("Zone");
        ImGui.TableSetupColumn("Last/Current CE");
        ImGui.TableSetupColumn("Last/Current FATE");
        ImGui.TableSetupColumn("Next Pot");
        ImGui.TableSetupColumn("Countdown", ImGuiTableColumnFlags.DefaultSort);
        ImGui.TableHeadersRow();

        var sortSpecs = ImGui.TableGetSortSpecs();
        var sortParts = new List<string>();
        for (int i = 0; i < sortSpecs.SpecsCount; i++)
        {
            var spec = sortSpecs.Specs[i];
            sortParts.Add($"{spec.SortOrder}:{spec.ColumnIndex}:{(int)spec.SortDirection}");
        }
        string sortKey = string.Join("|", sortParts);

        // Plugin replaces PotRows on refresh. Re-sort only when the source
        // changes or the user changes the table's sorting preferences.
        if (!ReferenceEquals(lastSource, plugin.PotRows) || sortKey != lastSortKey)
        {
            lastSource = plugin.PotRows;
            lastSortKey = sortKey;
            sortedRows = plugin.PotRows.ToList();

            // Keep the same ordering behavior as the original implementation.
            for (int i = sortSpecs.SpecsCount - 1; i >= 0; i--)
            {
                var spec = sortSpecs.Specs[i];
                bool ascending = spec.SortDirection == ImGuiSortDirection.Ascending;
                sortedRows = spec.ColumnIndex switch
                {
                    0 => ascending ? sortedRows.OrderBy(r => r.Datacenter).ToList() : sortedRows.OrderByDescending(r => r.Datacenter).ToList(),
                    1 => ascending ? sortedRows.OrderBy(r => r.Zone).ToList() : sortedRows.OrderByDescending(r => r.Zone).ToList(),
                    2 => ascending ? sortedRows.OrderBy(r => r.LastCurrentCE).ToList() : sortedRows.OrderByDescending(r => r.LastCurrentCE).ToList(),
                    3 => ascending ? sortedRows.OrderBy(r => r.LastCurrentFate).ToList() : sortedRows.OrderByDescending(r => r.LastCurrentFate).ToList(),
                    4 => ascending ? sortedRows.OrderBy(r => r.NextPot).ToList() : sortedRows.OrderByDescending(r => r.NextPot).ToList(),
                    5 => ascending
                        ? sortedRows.OrderBy(r => r.NextSpawnTime == 0 ? long.MaxValue : r.NextSpawnTime).ToList()
                        : sortedRows.OrderByDescending(r => r.NextSpawnTime).ToList(),
                    _ => sortedRows
                };
            }
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var row in sortedRows)
        {
            bool isCurrentInstance = plugin.MatchedTrackerId.HasValue &&
                                     row.TrackerId == plugin.MatchedTrackerId.Value;
            ImGui.TableNextRow();
            if (isCurrentInstance)
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0,
                    ImGui.ColorConvertFloat4ToU32(new Vector4(0.1f, 0.45f, 0.2f, 0.45f)));

            ImGui.TableSetColumnIndex(0);
            if (isCurrentInstance)
                ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), row.Datacenter);
            else
                ImGui.TextUnformatted(row.Datacenter);

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(row.Zone);
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(row.LastCurrentCE);
            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(row.LastCurrentFate);
            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted(row.NextPot);
            ImGui.TableSetColumnIndex(5);

            if (row.NextSpawnTime == 0)
            {
                ImGui.TextDisabled("Waiting for first Pot");
                continue;
            }

            long remaining = row.NextSpawnTime - now;
            if (remaining <= 0)
                ImGui.TextColored(new Vector4(0.3f, 1f, 0.3f, 1f), "Ready / Overdue");
            else if (remaining <= 300)
                ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), FormatCountdown(remaining));
            else
                ImGui.Text(FormatCountdown(remaining));
        }
        ImGui.EndTable();
    }

    private static string FormatCountdown(long seconds)
    {
        if (seconds <= 0)
            return "00:00";
        var duration = TimeSpan.FromSeconds(seconds);
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}";
        return $"{duration.Minutes:D2}:{duration.Seconds:D2}";
    }
}
