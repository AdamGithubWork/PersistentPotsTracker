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
    private const long SoonThresholdSeconds = 300;
    private const float TravelPanelHeight = 80f;

    private static readonly Vector4 MatchedRowColor = new(0.1f, 0.45f, 0.2f, 0.45f);
    private static readonly Vector4 SelectedRowColor = new(0.15f, 0.35f, 0.65f, 0.5f);
    private static readonly Vector4 HoverRowColor = new(0.3f, 0.3f, 0.3f, 0.2f);

    private static readonly Vector4 SuccessColor = new(0.3f, 1f, 0.3f, 1f);
    private static readonly Vector4 WarningColor = new(1f, 0.8f, 0.2f, 1f);
    private static readonly Vector4 ErrorColor = new(1f, 0.3f, 0.3f, 1f);

    private static readonly Dictionary<string, Vector4> RegionRowColors = new()
    {
        ["North America"] = new(0.92f, 0.49f, 0.14f, 0.40f),
        ["Materia"] = new(0.90f, 0.78f, 0.14f, 0.40f),
    };

    // Sort keys for the text columns, in column order. Countdown comes after.
    private static readonly Func<PotTableRow, string>[] TextColumns =
    {
        r => GetRegionName(r.Datacenter),
        r => r.Zone,
        r => r.LastCurrentCE,
        r => r.LastCurrentFate,
        r => r.NextPot,
    };

    private static readonly int CountdownColumn = TextColumns.Length;

    private readonly Plugin plugin;

    private List<PotTableRow> sortedRows = new();
    private List<PotTableRow>? lastSource;
    private string lastSortKey = "";

    // Currently selected tracker for travel.
    private int? selectedTrackerId;

    // Checked once per frame. Selecting rows and "Show on Map" don't need
    // Lifestream, so only the travel actions look at these.
    private bool lifestreamEnabled;
    private bool lifestreamAvailable;

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
        lifestreamEnabled = plugin.Configuration.UseLifestream;
        lifestreamAvailable = plugin.IsLifestreamAvailable;

        DrawRegionFilters();
        ImGui.Separator();
        DrawRefreshControls();
        ImGui.Separator();

        // Looked up before the table so a click this frame only takes effect next frame.
        var selectedRow = FindSelectedRow();

        DrawPotTable();
        DrawTravelSelection(selectedRow);
    }

    // Also clears the selection if its tracker is no longer in the list.
    private PotTableRow? FindSelectedRow()
    {
        if (selectedTrackerId is not { } id)
            return null;

        var row = plugin.PotRows.FirstOrDefault(r => r.TrackerId == id);
        if (row == null)
            selectedTrackerId = null;

        return row;
    }

    private void DrawRegionFilters()
    {
        var config = plugin.Configuration;

        ImGui.Text("Regions");

        var changed = false;
        changed |= DrawFilter("North America", config.ShowNorthAmerica, v => config.ShowNorthAmerica = v);
        ImGui.SameLine();
        changed |= DrawFilter("Materia", config.ShowMateria, v => config.ShowMateria = v);

        if (!changed)
            return;

        config.Save();
        plugin.DatacenterSelectionChanged();
        selectedTrackerId = null;
    }

    private static bool DrawFilter(string label, bool value, Action<bool> set)
    {
        if (!ImGui.Checkbox(label, ref value))
            return false;

        set(value);
        return true;
    }

    private void DrawRefreshControls()
    {
        ImGui.BeginDisabled(!plugin.CanRefresh);

        if (ImGui.Button("Refresh"))
            _ = plugin.RefreshTrackersAsync();

        ImGui.EndDisabled();
        ImGui.SameLine();

        if (plugin.IsRefreshing)
            ImGui.TextDisabled("Refreshing...");
        else
            ImGui.Text($"Instances found: {plugin.Trackers.Count}");

        if (plugin.MatchedTrackerId.HasValue)
        {
            ImGui.SameLine();
            ImGui.TextColored(SuccessColor, "Current instance identified");
        }

        if (lifestreamEnabled && !lifestreamAvailable)
        {
            ImGui.SameLine();
            ImGui.TextColored(WarningColor, "Lifestream not found: travel is disabled");
        }

        if (!string.IsNullOrEmpty(plugin.ApiError))
        {
            // The message can contain exception text, so draw it unformatted.
            ImGui.PushStyleColor(ImGuiCol.Text, ErrorColor);
            ImGui.TextUnformatted($"API Error: {plugin.ApiError}");
            ImGui.PopStyleColor();
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

        // Always reserve space for the travel panel so the table doesn't jump on selection.
        var tableHeight = Math.Max(100f, ImGui.GetContentRegionAvail().Y - TravelPanelHeight);

        if (!ImGui.BeginTable("PotTimerTable", 6, flags, new Vector2(0, tableHeight)))
            return;

        ImGui.TableSetupColumn("Region");
        ImGui.TableSetupColumn("Zone");
        ImGui.TableSetupColumn("Last/Current CE");
        ImGui.TableSetupColumn("Last/Current FATE");
        ImGui.TableSetupColumn("Next Pot");
        ImGui.TableSetupColumn("Countdown", ImGuiTableColumnFlags.DefaultSort);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        UpdateSortedRows();

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var row in sortedRows)
            DrawPotRow(row, now);

        ImGui.EndTable();
    }

    private void UpdateSortedRows()
    {
        // Read the list once: the plugin swaps it wholesale on refresh.
        var source = plugin.PotRows;
        var sortSpecs = ImGui.TableGetSortSpecs();

        var sortParts = new List<string>();
        for (var i = 0; i < sortSpecs.SpecsCount; i++)
        {
            var spec = sortSpecs.Specs[i];
            sortParts.Add($"{spec.SortOrder}:{spec.ColumnIndex}:{(int)spec.SortDirection}");
        }

        // Only re-sort when the data or the sort specs actually changed.
        var sortKey = string.Join("|", sortParts);
        if (ReferenceEquals(lastSource, source) && sortKey == lastSortKey)
            return;

        lastSource = source;
        lastSortKey = sortKey;
        sortedRows = source.ToList();

        // OrderBy is stable, so applying the specs from lowest to highest
        // priority leaves the highest-priority column as the primary sort.
        for (var i = sortSpecs.SpecsCount - 1; i >= 0; i--)
        {
            var spec = sortSpecs.Specs[i];
            var ascending = spec.SortDirection == ImGuiSortDirection.Ascending;
            sortedRows = SortBy(sortedRows, spec.ColumnIndex, ascending);
        }
    }

    private static List<PotTableRow> SortBy(List<PotTableRow> rows, int column, bool ascending)
    {
        if (column == CountdownColumn)
        {
            // Unknown spawn times always go last, whichever direction.
            var knownFirst = rows.OrderBy(r => r.NextSpawnTime == 0);

            return (ascending
                ? knownFirst.ThenBy(r => r.NextSpawnTime)
                : knownFirst.ThenByDescending(r => r.NextSpawnTime)).ToList();
        }

        if (column < 0 || column >= TextColumns.Length)
            return rows;

        var key = TextColumns[column];
        return (ascending ? rows.OrderBy(key) : rows.OrderByDescending(key)).ToList();
    }

    private void DrawPotRow(PotTableRow row, long now)
    {
        var region = GetRegionName(row.Datacenter);
        var isCurrentInstance = plugin.MatchedTrackerId == row.TrackerId;
        var isSelected = selectedTrackerId == row.TrackerId;

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);

        if (plugin.Configuration.ColorCodeDatacenters && !isSelected && !isCurrentInstance)
        {
            var color = RegionRowColors.GetValueOrDefault(region);
            ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, ImGui.ColorConvertFloat4ToU32(color));
        }

        // Capture row bounds without creating layout widgets.
        var rowMin = ImGui.GetCursorScreenPos();

        if (isCurrentInstance)
            ImGui.TextColored(SuccessColor, region);
        else
            ImGui.TextUnformatted(region);

        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(row.Zone);

        ImGui.TableSetColumnIndex(2);
        ImGui.TextUnformatted(row.LastCurrentCE);

        ImGui.TableSetColumnIndex(3);
        ImGui.TextUnformatted(row.LastCurrentFate);

        ImGui.TableSetColumnIndex(4);
        ImGui.TextUnformatted(row.NextPot);

        ImGui.TableSetColumnIndex(5);
        var rowRight = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        DrawCountdown(row, now);
        var rowBottom = ImGui.GetItemRectMax().Y;

        HandleRowInteraction(row, rowMin, new Vector2(rowRight, rowBottom), isSelected, isCurrentInstance);
    }

    private void HandleRowInteraction(
        PotTableRow row,
        Vector2 rowMin,
        Vector2 rowMax,
        bool isSelected,
        bool isCurrentInstance)
    {
        var mouse = ImGui.GetMousePos();
        var hovered =
            CanTravel(row) &&
            ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows) &&
            mouse.X >= rowMin.X && mouse.X < rowMax.X &&
            mouse.Y >= rowMin.Y && mouse.Y < rowMax.Y;

        // Selected beats matched, which beats hover.
        if (isSelected)
            SetRowBackground(SelectedRowColor);
        else if (isCurrentInstance)
            SetRowBackground(MatchedRowColor);
        else if (hovered)
            SetRowBackground(HoverRowColor);

        if (!hovered)
            return;

        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        // Double right-click travels straight away, same or different datacenter.
        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Right))
        {
            if (lifestreamEnabled && lifestreamAvailable)
                plugin.TravelToTracker(row);

            return;
        }

        // Left-click toggles the selection.
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            selectedTrackerId = isSelected ? null : row.TrackerId;
    }

    private static void SetRowBackground(Vector4 color) =>
        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.ColorConvertFloat4ToU32(color));

    private static string GetRegionName(string datacenter) =>
        datacenter is "Aether" or "Primal" or "Crystal" or "Dynamis"
            ? "North America"
            : datacenter;

    // Whether the row has a known destination. Used by the row hover logic and
    // the travel panel.
    private static bool CanTravel(PotTableRow row) =>
        row.Zone is "North Horn" or "South Horn" &&
        row.Datacenter is "Aether" or "Primal" or "Crystal" or "Dynamis" or "Materia";

    private void DrawTravelSelection(PotTableRow? row)
    {
        ImGui.Separator();
        ImGui.Spacing();

        if (row == null)
        {
            ImGui.TextDisabled("Select a tracker to view travel options.");
            return;
        }

        ImGui.TextDisabled("Selected destination");
        ImGui.TextUnformatted($"{GetRegionName(row.Datacenter)} - {row.Zone}");

        var knownDestination = CanTravel(row);
        var canTravel = lifestreamEnabled && lifestreamAvailable && knownDestination;
        var hasPrediction = row.NextPot is "S" or "N";

        // Three buttons, aligned toward the right.
        const float clearWidth = 65f;
        const float mapWidth = 115f;
        const float travelWidth = 75f;

        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var totalWidth = clearWidth + mapWidth + travelWidth + spacing * 2;
        var startX = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - totalWidth;

        if (startX > ImGui.GetCursorPosX())
            ImGui.SetCursorPosX(startX);

        if (ImGui.Button("Clear##TravelSelection", new Vector2(clearWidth, 0)))
        {
            selectedTrackerId = null;
            return;
        }

        ImGui.SameLine();

        ImGui.BeginDisabled(!hasPrediction);
        if (ImGui.Button("Show on Map##SelectedTracker", new Vector2(mapWidth, 0)))
            plugin.ShowPotOnMap(row);
        ImGui.EndDisabled();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open the predicted pot location on the FFXIV map.");

        ImGui.SameLine();

        ImGui.BeginDisabled(!canTravel);
        if (ImGui.Button("Travel##SelectedTracker", new Vector2(travelWidth, 0)))
            plugin.TravelToTracker(row);
        ImGui.EndDisabled();

        // Disabled items only report hover with AllowWhenDisabled.
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            if (!lifestreamEnabled)
            {
                ImGui.SetTooltip("Travel is turned off. Tick Use Lifestream in settings to enable it.");
            }
            else if (!lifestreamAvailable)
            {
                ImGui.SetTooltip("Travel needs the Lifestream plugin. Install and enable it to use this.");
            }
            else if (knownDestination)
            {
                ImGui.SetTooltip(
                    $"Travel to {GetRegionName(row.Datacenter)} / {row.Zone}\n" +
                    "Uses Lifestream and opens the pot map after arrival.");
            }
        }
    }

    private static void DrawCountdown(PotTableRow row, long now)
    {
        if (row.NextSpawnTime == 0)
        {
            ImGui.TextDisabled("Waiting for first Pot");
            return;
        }

        var remaining = row.NextSpawnTime - now;

        if (remaining <= 0)
            ImGui.TextColored(SuccessColor, "Ready / Overdue");
        else if (remaining <= SoonThresholdSeconds)
            ImGui.TextColored(WarningColor, FormatCountdown(remaining));
        else
            ImGui.TextUnformatted(FormatCountdown(remaining));
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
