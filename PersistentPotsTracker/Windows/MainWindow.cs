
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using PersistentPotsTracker.Models;

namespace PersistentPotsTracker.Windows;

public class MainWindow : Window
{
    private readonly Plugin plugin;

    private List<PotTableRow> sortedRows = new();
    private List<PotTableRow>? lastSource;
    private string lastSortKey = "";

    private static readonly Vector4 MatchedRowColor =
        new(0.1f, 0.45f, 0.2f, 0.45f);

    private static readonly Vector4 SuccessColor =
        new(0.3f, 1f, 0.3f, 1f);

    private static readonly Vector4 WarningColor =
        new(1f, 0.8f, 0.2f, 1f);

    private static readonly Vector4 ErrorColor =
        new(1f, 0.3f, 0.3f, 1f);

    private const long SoonThresholdSeconds = 300;

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

        if (!string.IsNullOrEmpty(plugin.LastTravelMessage))
        {
            ImGui.TextWrapped(plugin.LastTravelMessage);
            ImGui.Separator();
        }

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
        if (plugin.IsRefreshing)
            ImGui.BeginDisabled();

        if (ImGui.Button("Refresh Pots"))
        {
            _ = plugin.RefreshTrackersAsync();
        }

        if (plugin.IsRefreshing)
            ImGui.EndDisabled();

        ImGui.SameLine();

        if (plugin.IsRefreshing)
        {
            ImGui.TextDisabled("Refreshing...");
        }
        else
        {
            ImGui.Text(
                $"Instances found: {plugin.Trackers.Count}");
        }

        if (plugin.MatchedTrackerId.HasValue)
        {
            ImGui.SameLine();

            ImGui.TextColored(
                SuccessColor,
                "Current instance identified");
        }

        if (!string.IsNullOrEmpty(plugin.ApiError))
        {
            ImGui.TextColored(
                ErrorColor,
                $"API Error: {plugin.ApiError}");
        }
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

        if (!ImGui.BeginTable("PotTimerTable", 7, flags))
            return;

        ImGui.TableSetupColumn(
            "Travel",
            ImGuiTableColumnFlags.NoSort,
            0.85f);

        ImGui.TableSetupColumn("Datacenter");
        ImGui.TableSetupColumn("Zone");
        ImGui.TableSetupColumn("Last/Current CE");
        ImGui.TableSetupColumn("Last/Current FATE");
        ImGui.TableSetupColumn("Next Pot");

        ImGui.TableSetupColumn(
            "Countdown",
            ImGuiTableColumnFlags.DefaultSort);

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        UpdateSortedRows();

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var row in sortedRows)
        {
            DrawPotRow(row, now);
        }

        ImGui.EndTable();
    }

    private void UpdateSortedRows()
    {
        var sortSpecs = ImGui.TableGetSortSpecs();

        var sortParts = new List<string>();

        for (int i = 0; i < sortSpecs.SpecsCount; i++)
        {
            var spec = sortSpecs.Specs[i];

            sortParts.Add(
                $"{spec.SortOrder}:{spec.ColumnIndex}:" +
                $"{(int)spec.SortDirection}");
        }

        string sortKey = string.Join("|", sortParts);

        if (ReferenceEquals(lastSource, plugin.PotRows) &&
            sortKey == lastSortKey)
        {
            return;
        }

        lastSource = plugin.PotRows;
        lastSortKey = sortKey;
        sortedRows = plugin.PotRows.ToList();

        // Apply multi-column sorting from lowest to
        // highest priority using stable ordering.
        for (int i = sortSpecs.SpecsCount - 1; i >= 0; i--)
        {
            var spec = sortSpecs.Specs[i];

            bool ascending =
                spec.SortDirection == ImGuiSortDirection.Ascending;

            sortedRows = spec.ColumnIndex switch
            {
                1 => ascending
                    ? sortedRows.OrderBy(
                        r => r.Datacenter).ToList()
                    : sortedRows.OrderByDescending(
                        r => r.Datacenter).ToList(),

                2 => ascending
                    ? sortedRows.OrderBy(
                        r => r.Zone).ToList()
                    : sortedRows.OrderByDescending(
                        r => r.Zone).ToList(),

                3 => ascending
                    ? sortedRows.OrderBy(
                        r => r.LastCurrentCE).ToList()
                    : sortedRows.OrderByDescending(
                        r => r.LastCurrentCE).ToList(),

                4 => ascending
                    ? sortedRows.OrderBy(
                        r => r.LastCurrentFate).ToList()
                    : sortedRows.OrderByDescending(
                        r => r.LastCurrentFate).ToList(),

                5 => ascending
                    ? sortedRows.OrderBy(
                        r => r.NextPot).ToList()
                    : sortedRows.OrderByDescending(
                        r => r.NextPot).ToList(),

                6 => SortByCountdown(sortedRows, ascending),

                _ => sortedRows
            };
        }
    }

    private static List<PotTableRow> SortByCountdown(
        List<PotTableRow> rows,
        bool ascending)
    {
        // Unknown spawn times always appear last.
        return ascending
            ? rows.OrderBy(r => r.NextSpawnTime == 0)
                  .ThenBy(r => r.NextSpawnTime)
                  .ToList()
            : rows.OrderBy(r => r.NextSpawnTime == 0)
                  .ThenByDescending(r => r.NextSpawnTime)
                  .ToList();
    }

    private void DrawPotRow(PotTableRow row, long now)
    {
        bool isCurrentInstance =
            plugin.MatchedTrackerId.HasValue &&
            row.TrackerId == plugin.MatchedTrackerId.Value;

        ImGui.TableNextRow();

        if (isCurrentInstance)
        {
            ImGui.TableSetBgColor(
                ImGuiTableBgTarget.RowBg0,
                ImGui.ColorConvertFloat4ToU32(MatchedRowColor));
        }

        // Column 0: Travel
        ImGui.TableSetColumnIndex(0);

        DrawTravelButton(row);

        // Column 1: Datacenter
        ImGui.TableSetColumnIndex(1);

        if (isCurrentInstance)
        {
            ImGui.TextColored(
                SuccessColor,
                row.Datacenter);
        }
        else
        {
            ImGui.TextUnformatted(row.Datacenter);
        }

        // Column 2: Zone
        ImGui.TableSetColumnIndex(2);
        ImGui.TextUnformatted(row.Zone);

        // Column 3: Last/Current CE
        ImGui.TableSetColumnIndex(3);
        ImGui.TextUnformatted(row.LastCurrentCE);

        // Column 4: Last/Current FATE
        ImGui.TableSetColumnIndex(4);
        ImGui.TextUnformatted(row.LastCurrentFate);

        // Column 5: Next Pot
        ImGui.TableSetColumnIndex(5);
        ImGui.TextUnformatted(row.NextPot);

        // Column 6: Countdown
        ImGui.TableSetColumnIndex(6);

        DrawCountdown(row, now);
    }

    private void DrawTravelButton(PotTableRow row)
    {
        if (row.Zone is not ("North Horn" or "South Horn"))
        {
            ImGui.TextDisabled("N/A");
            return;
        }

        if (ImGui.SmallButton($"Go##Travel{row.TrackerId}"))
        {
            plugin.TravelToTracker(row);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                $"Ask Lifestream to travel to " +
                $"{row.Datacenter} / {row.Zone}.\n" +
                "Does not select the exact instance.");
        }
    }

    private static void DrawCountdown(
        PotTableRow row,
        long now)
    {
        if (row.NextSpawnTime == 0)
        {
            ImGui.TextDisabled("Waiting for first Pot");
            return;
        }

        long remaining = row.NextSpawnTime - now;

        if (remaining <= 0)
        {
            ImGui.TextColored(
                SuccessColor,
                "Ready / Overdue");
        }
        else if (remaining <= SoonThresholdSeconds)
        {
            ImGui.TextColored(
                WarningColor,
                FormatCountdown(remaining));
        }
        else
        {
            ImGui.TextUnformatted(
                FormatCountdown(remaining));
        }
    }

    private static string FormatCountdown(long seconds)
    {
        if (seconds <= 0)
            return "00:00";

        var duration = TimeSpan.FromSeconds(seconds);

        if (duration.TotalHours >= 1)
        {
            return
                $"{(int)duration.TotalHours:D2}:" +
                $"{duration.Minutes:D2}:" +
                $"{duration.Seconds:D2}";
        }

        return $"{duration.Minutes:D2}:{duration.Seconds:D2}";
    }
}
