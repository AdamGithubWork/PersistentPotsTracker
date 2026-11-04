
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using PersistentPotsTracker.Models;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace PersistentPotsTracker.Windows;

public class MainWindow : Window
{
    private const long WarningThresholdSeconds = 300;

    private static readonly Vector4 ReadyColor = new(0.3f, 1f, 0.3f, 1f);
    private static readonly Vector4 WarningColor = new(1f, 0.8f, 0.2f, 1f);
    private static readonly Vector4 CurrentInstanceColor = new(0.4f, 1f, 0.4f, 1f);
    private static readonly Vector4 CurrentInstanceBackground = new(0.1f, 0.45f, 0.2f, 0.45f);
    private static readonly Vector4 ErrorColor = new(1f, 0.3f, 0.3f, 1f);

    private readonly Plugin plugin;
    private List<PotTableRow> sortedRows = new();
    private List<PotTableRow>? lastSource;

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
        ImGui.BeginDisabled(plugin.IsRefreshing);

        if (ImGui.Button("Refresh Pots"))
            _ = plugin.RefreshTrackersAsync();

        ImGui.EndDisabled();

        ImGui.SameLine();

        if (plugin.IsRefreshing)
        {
            ImGui.TextDisabled("Refreshing...");
        }
        else
        {
            ImGui.Text($"Instances found: {plugin.Trackers.Count}");
        }

        if (plugin.MatchedTrackerId.HasValue)
        {
            ImGui.SameLine();

            ImGui.TextColored(
                ReadyColor,
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

        if (!ImGui.BeginTable("PotTimerTable", 6, flags))
            return;

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

        var sortSpecs = ImGui.TableGetSortSpecs();

        if (sortSpecs.SpecsDirty ||
            !ReferenceEquals(lastSource, plugin.PotRows))
        {
            lastSource = plugin.PotRows;
            sortedRows = new List<PotTableRow>(plugin.PotRows);

            var columns =
                new (int Index, int Order, bool Descending)[sortSpecs.SpecsCount];

            for (int i = 0; i < sortSpecs.SpecsCount; i++)
            {
                var spec = sortSpecs.Specs[i];

                columns[i] = (
                    spec.ColumnIndex,
                    spec.SortOrder,
                    spec.SortDirection == ImGuiSortDirection.Descending
                );
            }

            Array.Sort(columns, (a, b) =>
                a.Order.CompareTo(b.Order));

            sortedRows.Sort((a, b) =>
                CompareRows(a, b, columns));

            sortSpecs.SpecsDirty = false;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var row in sortedRows)
        {
            DrawPotRow(row, now);
        }

        ImGui.EndTable();
    }

    private static int CompareRows(
        PotTableRow a,
        PotTableRow b,
        (int Index, int Order, bool Descending)[] columns)
    {
        foreach (var column in columns)
        {
            int result = column.Index switch
            {
                0 => string.Compare(
                    a.Datacenter,
                    b.Datacenter,
                    StringComparison.CurrentCultureIgnoreCase),

                1 => string.Compare(
                    a.Zone,
                    b.Zone,
                    StringComparison.CurrentCultureIgnoreCase),

                2 => string.Compare(
                    a.LastCurrentCE,
                    b.LastCurrentCE,
                    StringComparison.CurrentCultureIgnoreCase),

                3 => string.Compare(
                    a.LastCurrentFate,
                    b.LastCurrentFate,
                    StringComparison.CurrentCultureIgnoreCase),

                4 => string.Compare(
                    a.NextPot,
                    b.NextPot,
                    StringComparison.CurrentCultureIgnoreCase),

                5 => CompareSpawnTimes(
                    a.NextSpawnTime,
                    b.NextSpawnTime,
                    column.Descending),

                _ => 0
            };

            if (result == 0)
                continue;

            return column.Index == 5
                ? result
                : column.Descending ? -result : result;
        }

        return a.TrackerId.CompareTo(b.TrackerId);
    }

    private static int CompareSpawnTimes(
        long a,
        long b,
        bool descending)
    {
        if (a == 0 && b == 0)
            return 0;

        if (a == 0)
            return 1;

        if (b == 0)
            return -1;

        return descending
            ? b.CompareTo(a)
            : a.CompareTo(b);
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
                ImGui.ColorConvertFloat4ToU32(
                    CurrentInstanceBackground));
        }

        ImGui.TableSetColumnIndex(0);

        if (isCurrentInstance)
        {
            ImGui.TextColored(
                CurrentInstanceColor,
                row.Datacenter);
        }
        else
        {
            ImGui.TextUnformatted(row.Datacenter);
        }

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
            return;
        }

        long remaining = row.NextSpawnTime - now;

        if (remaining <= 0)
        {
            ImGui.TextColored(
                ReadyColor,
                "Ready / Overdue");
        }
        else if (remaining <= WarningThresholdSeconds)
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
            return $"{(int)duration.TotalHours:D2}:" +
                   $"{duration.Minutes:D2}:" +
                   $"{duration.Seconds:D2}";
        }

        return $"{duration.Minutes:D2}:{duration.Seconds:D2}";
    }
}
