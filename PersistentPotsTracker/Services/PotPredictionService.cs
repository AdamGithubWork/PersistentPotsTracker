using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using PersistentPotsTracker.Models;
using System;
using System.Collections.Generic;

namespace PersistentPotsTracker.Services;

public class PotPredictionService
{
    private const string SouthHorn = "South Horn";
    private const string NorthHorn = "North Horn";
    private const string Unknown = "Unknown";

    // Seconds from a pot spawning to the next one.
    private const long PotSpawnIntervalSeconds = 1800;

    // Pot FATE ID -> (zone, label shown in "Next Pot").
    private static readonly Dictionary<int, (string Zone, string Label)> PotFates = new()
    {
        [1976] = (SouthHorn, "S"),
        [1977] = (SouthHorn, "N"),
        [2072] = (NorthHorn, "S"),
        [2073] = (NorthHorn, "N"),
    };

    private readonly IDataManager dataManager;
    private readonly Dictionary<int, string> fateNameCache = new();
    private readonly Dictionary<int, string> ceNameCache = new();

    public PotPredictionService(IDataManager dataManager)
    {
        this.dataManager = dataManager;
    }

    public List<PotTableRow> BuildPotRows(IReadOnlyList<TrackerData> trackers)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var rows = new List<PotTableRow>(trackers.Count);

        foreach (var tracker in trackers)
            rows.Add(BuildPotRow(tracker, now));

        return rows;
    }

    private PotTableRow BuildPotRow(TrackerData tracker, long now)
    {
        var (zone, lastPot) = ReadPots(tracker, now);

        var nextPot = Unknown;
        long nextSpawnTime = 0;

        if (lastPot != null)
        {
            nextPot = PotFates[lastPot.FateId].Label;
            nextSpawnTime = lastPot.SpawnTime + PotSpawnIntervalSeconds;
        }

        var datacenter = tracker.Datacenter switch
        {
            4 => "Aether",
            5 => "Primal",
            8 => "Crystal",
            9 => "Materia",
            11 => "Dynamis",
            _ => $"Unknown ({tracker.Datacenter})"
        };

        return new PotTableRow
        {
            TrackerId = tracker.Id,
            Datacenter = datacenter,
            Zone = zone,
            LastCurrentCE = GetLastCurrentEncounter(tracker.GetEncounters(), GetCEName, now),
            LastCurrentFate = GetLastCurrentEncounter(tracker.GetFates(), GetFateName, now),
            NextPot = nextPot,
            NextSpawnTime = nextSpawnTime
        };
    }

    // Zone comes from any pot the tracker has ever reported, spawned yet or not.
    // The last pot only counts if it has actually spawned.
    private static (string Zone, PotData? LastPot) ReadPots(TrackerData tracker, long now)
    {
        PotData? lastPot = null;
        var hasSouth = false;
        var hasNorth = false;

        foreach (var pot in tracker.GetPots())
        {
            if (!PotFates.TryGetValue(pot.FateId, out var info))
                continue;

            if (info.Zone == SouthHorn)
                hasSouth = true;
            else
                hasNorth = true;

            if (pot.SpawnTime <= 0 || pot.SpawnTime > now)
                continue;

            if (lastPot == null || pot.SpawnTime > lastPot.SpawnTime)
                lastPot = pot;
        }

        var zone = hasSouth ? SouthHorn : hasNorth ? NorthHorn : Unknown;
        return (zone, lastPot);
    }

    // Prefers the newest encounter that hasn't died yet; otherwise the newest overall.
    private static string GetLastCurrentEncounter(IReadOnlyList<PotData> history, Func<int, string> getName, long now)
    {
        PotData? latest = null;
        PotData? latestActive = null;

        foreach (var encounter in history)
        {
            if (encounter.SpawnTime <= 0 || encounter.SpawnTime > now)
                continue;

            if (latest == null || encounter.SpawnTime > latest.SpawnTime)
                latest = encounter;

            if (encounter.DeathTime == 0 && (latestActive == null || encounter.SpawnTime > latestActive.SpawnTime))
                latestActive = encounter;
        }

        var selected = latestActive ?? latest;
        if (selected == null)
            return Unknown;

        var name = getName(selected.FateId);
        return latestActive != null ? $"{name} (Active)" : name;
    }

    private string GetFateName(int fateId) =>
        LookupName(fateNameCache, fateId, "FATE",
            id => dataManager.GetExcelSheet<Fate>().GetRowOrDefault((uint)id)?.Name.ToString());

    private string GetCEName(int ceId) =>
        LookupName(ceNameCache, ceId, "CE",
            id => dataManager.GetExcelSheet<DynamicEvent>().GetRowOrDefault((uint)id)?.Name.ToString());

    private static string LookupName(Dictionary<int, string> cache, int id, string fallbackPrefix, Func<int, string?> lookup)
    {
        if (cache.TryGetValue(id, out var cached))
            return cached;

        var name = lookup(id);
        if (string.IsNullOrWhiteSpace(name))
            name = $"{fallbackPrefix} {id}";

        cache[id] = name;
        return name;
    }
}
