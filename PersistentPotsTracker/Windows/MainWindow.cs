
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

    // Currently selected tracker for travel.
    private int? selectedTrackerId;

    private static readonly Vector4 MatchedRowColor =
        new(0.1f, 0.45f, 0.2f, 0.45f);

    private static readonly Vector4 SelectedRowColor =
        new(0.15f, 0.35f, 0.65f, 0.5f);

    private static readonly Vector4 HoverRowColor =
        new(0.3f, 0.3f, 0.3f, 0.2f);

    private static readonly Vector4 SuccessColor =
        new(0.3f, 1f, 0.3f, 1f);

    private static readonly Vector4 WarningColor =
        new(1f, 0.8f, 0.2f, 1f);

    private static readonly Vector4 ErrorColor =
        new(1f, 0.3f, 0.3f, 1f);

    private const long SoonThresholdSeconds = 300;

    private const float TravelPanelHeight = 80f;

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

        // Clear the selection if its tracker is no longer available.
        if (selectedTrackerId.HasValue &&
            !plugin.PotRows.Any(r =>
                r.TrackerId == selectedTrackerId.Value))
        {
            selectedTrackerId = null;
        }

        DrawPotTable();
        DrawTravelSelection();
    }

    private void DrawDatacenterFilters()
    {
        ImGui.Text("Datacenters");

        bool aether = plugin.Configuration.ShowAether;
        bool primal = plugin.Configuration.ShowPrimal;
        bool crystal = plugin.Configuration.ShowCrystal;
        bool dynamis = plugin.Configuration.ShowDynamis;
        bool materia = plugin.Configuration.ShowMateria;
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
        ImGui.SameLine();

        if (ImGui.Checkbox("Materia", ref materia))
        {
            plugin.Configuration.ShowMateria = materia;
            changed = true;
        }
        if (changed)
        {
            plugin.Configuration.Save();
            plugin.DatacenterSelectionChanged();

            selectedTrackerId = null;
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

        // Reserve room for the selection panel when needed.
        float availableHeight = ImGui.GetContentRegionAvail().Y;

        float tableHeight = Math.Max(
            100f,
            availableHeight - TravelPanelHeight);

        if (!ImGui.BeginTable(
                "PotTimerTable",
                6,
                flags,
                new Vector2(0, tableHeight)))
        {
            return;
        }

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

        // Apply sorting from lowest to highest priority.
        for (int i = sortSpecs.SpecsCount - 1; i >= 0; i--)
        {
            var spec = sortSpecs.Specs[i];

            bool ascending =
                spec.SortDirection ==
                ImGuiSortDirection.Ascending;

            sortedRows = spec.ColumnIndex switch
            {
                0 => ascending
                    ? sortedRows.OrderBy(
                        r => r.Datacenter).ToList()
                    : sortedRows.OrderByDescending(
                        r => r.Datacenter).ToList(),

                1 => ascending
                    ? sortedRows.OrderBy(
                        r => r.Zone).ToList()
                    : sortedRows.OrderByDescending(
                        r => r.Zone).ToList(),

                2 => ascending
                    ? sortedRows.OrderBy(
                        r => r.LastCurrentCE).ToList()
                    : sortedRows.OrderByDescending(
                        r => r.LastCurrentCE).ToList(),

                3 => ascending
                    ? sortedRows.OrderBy(
                        r => r.LastCurrentFate).ToList()
                    : sortedRows.OrderByDescending(
                        r => r.LastCurrentFate).ToList(),

                4 => ascending
                    ? sortedRows.OrderBy(
                        r => r.NextPot).ToList()
                    : sortedRows.OrderByDescending(
                        r => r.NextPot).ToList(),

                5 => SortByCountdown(
                    sortedRows, ascending),

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
            ? rows.OrderBy(
                    r => r.NextSpawnTime == 0)
                  .ThenBy(
                    r => r.NextSpawnTime)
                  .ToList()
            : rows.OrderBy(
                    r => r.NextSpawnTime == 0)
                  .ThenByDescending(
                    r => r.NextSpawnTime)
                  .ToList();
    }

    private void DrawPotRow(PotTableRow row, long now)
    {
        bool isCurrentInstance =
            plugin.MatchedTrackerId.HasValue &&
            row.TrackerId == plugin.MatchedTrackerId.Value;

        bool isSelected =
            selectedTrackerId == row.TrackerId;

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);

        // Capture row bounds without creating layout widgets.
        Vector2 rowStart = ImGui.GetCursorScreenPos();

        float rowLeft = rowStart.X;
        float rowTop = rowStart.Y;

        // Column 0: Datacenter
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

        // Column 1: Zone
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(row.Zone);

        // Column 2: Last/Current CE
        ImGui.TableSetColumnIndex(2);
        ImGui.TextUnformatted(row.LastCurrentCE);

        // Column 3: Last/Current FATE
        ImGui.TableSetColumnIndex(3);
        ImGui.TextUnformatted(row.LastCurrentFate);

        // Column 4: Next Pot
        ImGui.TableSetColumnIndex(4);
        ImGui.TextUnformatted(row.NextPot);

        // Column 5: Countdown
        ImGui.TableSetColumnIndex(5);

        // Capture the right side of the final column.
        float rowRight =
            ImGui.GetCursorScreenPos().X +
            ImGui.GetContentRegionAvail().X;

        DrawCountdown(row, now);

        float rowBottom = ImGui.GetItemRectMax().Y;

        HandleRowInteraction(
            row,
            rowLeft,
            rowRight,
            rowTop,
            rowBottom,
            isSelected,
            isCurrentInstance);
    }

    private void HandleRowInteraction(
        PotTableRow row,
        float left,
        float right,
        float top,
        float bottom,
        bool isSelected,
        bool isCurrentInstance)
    {
        bool canTravel =
            (row.Zone is "North Horn" or "South Horn") &&
            (row.Datacenter is
                "Aether" or "Primal" or "Crystal" or "Dynamis");

        Vector2 mouse = ImGui.GetMousePos();

        bool hovered =
            canTravel &&
            ImGui.IsWindowHovered(
                ImGuiHoveredFlags.ChildWindows) &&
            mouse.X >= left &&
            mouse.X < right &&
            mouse.Y >= top &&
            mouse.Y < bottom;

        // Selected destination takes priority over other highlights.
        if (isSelected)
        {
            ImGui.TableSetBgColor(
                ImGuiTableBgTarget.RowBg0,
                ImGui.ColorConvertFloat4ToU32(
                    SelectedRowColor));
        }
        else if (isCurrentInstance)
        {
            ImGui.TableSetBgColor(
                ImGuiTableBgTarget.RowBg0,
                ImGui.ColorConvertFloat4ToU32(
                    MatchedRowColor));
        }
        else if (hovered)
        {
            ImGui.TableSetBgColor(
                ImGuiTableBgTarget.RowBg0,
                ImGui.ColorConvertFloat4ToU32(
                    HoverRowColor));
        }

        if (!hovered)
            return;

        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        // Double right-click to travel immediately.
        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Right))
        {
            plugin.TravelToTracker(row);
            return;
        }

        // Single left-click to select or deselect.
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            selectedTrackerId = isSelected
                ? null
                : row.TrackerId;
        }
    }


    private void DrawTravelSelection()
    {
        if (!selectedTrackerId.HasValue)
        {
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.TextDisabled("Select a tracker to view travel options.");
            return;
        }

        PotTableRow? row = plugin.PotRows.FirstOrDefault(
            r => r.TrackerId == selectedTrackerId.Value);

        if (row == null)
        {
            selectedTrackerId = null;
            return;
        }

        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextDisabled("Selected destination");
        ImGui.Text($"{row.Datacenter} - {row.Zone}");

        bool canTravel =
            (row.Zone is "North Horn" or "South Horn") &&
            (row.Datacenter is
                "Aether" or "Primal" or "Crystal" or "Dynamis");

        bool hasPrediction =
            row.NextPot is "S" or "N";

        // Three buttons, aligned toward the right.
        const float clearWidth = 65f;
        const float mapWidth = 115f;
        const float travelWidth = 75f;

        float spacing = ImGui.GetStyle().ItemSpacing.X;

        float totalWidth =
            clearWidth + mapWidth + travelWidth +
            spacing * 2;

        float startX =
            ImGui.GetCursorPosX() +
            ImGui.GetContentRegionAvail().X -
            totalWidth;

        if (startX > ImGui.GetCursorPosX())
        {
            ImGui.SetCursorPosX(startX);
        }

        if (ImGui.Button(
                "Clear##TravelSelection",
                new Vector2(clearWidth, 0)))
        {
            selectedTrackerId = null;
            return;
        }

        ImGui.SameLine();

        if (!hasPrediction)
            ImGui.BeginDisabled();

        if (ImGui.Button(
                "Show on Map##SelectedTracker",
                new Vector2(mapWidth, 0)))
        {
            plugin.ShowPotOnMap(row);
        }

        if (!hasPrediction)
            ImGui.EndDisabled();

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Open the predicted pot location " +
                "on the FFXIV map.");
        }

        ImGui.SameLine();

        if (!canTravel)
            ImGui.BeginDisabled();

        if (ImGui.Button(
                "Travel##SelectedTracker",
                new Vector2(travelWidth, 0)))
        {
            plugin.TravelToTracker(row);
        }

        if (!canTravel)
            ImGui.EndDisabled();

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                $"Travel to {row.Datacenter} / {row.Zone}\n" +
                "Uses Lifestream and opens the pot map " +
                "after arrival.");
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

        return
            $"{duration.Minutes:D2}:{duration.Seconds:D2}";
    }
}
