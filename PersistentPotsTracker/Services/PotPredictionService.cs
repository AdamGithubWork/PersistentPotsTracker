
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using PersistentPotsTracker.Models;
using System;
using System.Collections.Generic;

namespace PersistentPotsTracker.Services;

public class PotPredictionService
{
    private readonly IDataManager dataManager;

    private readonly Dictionary<int, string> fateNameCache = new();
    private readonly Dictionary<int, string> ceNameCache = new();

    public PotPredictionService(IDataManager dataManager)
    {
        this.dataManager = dataManager;
    }

    public List<PotTableRow> BuildPotRows(List<TrackerData> trackers)
    {
        var rows = new List<PotTableRow>(trackers.Count);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var tracker in trackers)
        {
            rows.Add(BuildPotRow(tracker, now));
        }

        return rows;
    }

    private PotTableRow BuildPotRow(TrackerData tracker, long now)
    {
        PotData? lastPot = null;
        bool hasSouth = false;
        bool hasNorth = false;

        foreach (var pot in tracker.GetPots())
        {
            switch (pot.FateId)
            {
                case 1976:
                case 1977:
                    hasSouth = true;
                    break;

                case 2072:
                case 2073:
                    hasNorth = true;
                    break;

                default:
                    continue;
            }

            if (pot.SpawnTime <= 0 || pot.SpawnTime > now)
                continue;

            if (lastPot == null || pot.SpawnTime > lastPot.SpawnTime)
            {
                lastPot = pot;
            }
        }

        string zone = hasSouth
            ? "South Horn"
            : hasNorth ? "North Horn" : "Unknown";

        string nextPot = "Unknown";
        long nextSpawnTime = 0;

        if (lastPot != null)
        {
            nextPot = lastPot.FateId switch
            {
                1976 or 2072 => "S",
                1977 or 2073 => "N",
                _ => "Unknown"
            };

            nextSpawnTime = lastPot.SpawnTime + 1800;
        }

        string datacenter = tracker.Datacenter switch
        {
            4 => "Aether",
            5 => "Primal",
            8 => "Crystal",
            11 => "Dynamis",
            _ => $"Unknown ({tracker.Datacenter})"
        };

        return new PotTableRow
        {
            TrackerId = tracker.Id,
            Datacenter = datacenter,
            Zone = zone,
            LastCurrentCE = GetLastCurrentEncounter(
                tracker.GetEncounters(), false, now),
            LastCurrentFate = GetLastCurrentEncounter(
                tracker.GetFates(), true, now),
            NextPot = nextPot,
            NextSpawnTime = nextSpawnTime
        };
    }

    private string GetLastCurrentEncounter(
        List<PotData> history,
        bool isFate,
        long now)
    {
        PotData? latestActive = null;
        PotData? latest = null;

        foreach (var encounter in history)
        {
            if (encounter.SpawnTime <= 0 ||
                encounter.SpawnTime > now)
            {
                continue;
            }

            if (latest == null ||
                encounter.SpawnTime > latest.SpawnTime)
            {
                latest = encounter;
            }

            if (encounter.DeathTime == 0 &&
                (latestActive == null ||
                 encounter.SpawnTime > latestActive.SpawnTime))
            {
                latestActive = encounter;
            }
        }

        var selected = latestActive ?? latest;

        if (selected == null)
            return "Unknown";

        string name = isFate
            ? GetFateName(selected.FateId)
            : GetCEName(selected.FateId);

        return latestActive != null
            ? $"{name} (Active)"
            : name;
    }

    private string GetFateName(int fateId)
    {
        if (fateNameCache.TryGetValue(fateId, out var cached))
            return cached;

        var sheet = dataManager.GetExcelSheet<Fate>();
        var fate = sheet.GetRowOrDefault((uint)fateId);

        string name = fate?.Name.ToString() ?? "";

        if (string.IsNullOrWhiteSpace(name))
            name = $"FATE {fateId}";

        fateNameCache[fateId] = name;
        return name;
    }

    private string GetCEName(int ceId)
    {
        if (ceNameCache.TryGetValue(ceId, out var cached))
            return cached;

        var sheet = dataManager.GetExcelSheet<DynamicEvent>();
        var ce = sheet.GetRowOrDefault((uint)ceId);

        string name = ce?.Name.ToString() ?? "";

        if (string.IsNullOrWhiteSpace(name))
            name = $"CE {ceId}";

        ceNameCache[ceId] = name;
        return name;
    }
}
