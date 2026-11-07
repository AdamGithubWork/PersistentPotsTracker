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

    // Pot FATE IDs: which zone each belongs to and the label shown in "Next Pot".
    // One table instead of two parallel switches, so the IDs live in one place.
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
        var rows = new List<PotTableRow>(trackers.Count);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var tracker in trackers)
        {
            rows.Add(BuildPotRow(tracker, now));
        }

        return rows;
    }

    private PotTableRow BuildPotRow(TrackerData tracker, long now)
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
            {
                lastPot = pot;
            }
        }

        var zone = hasSouth
            ? SouthHorn
            : hasNorth ? NorthHorn : Unknown;

        var nextPot = Unknown;
        long nextSpawnTime = 0;

        if (lastPot != null)
        {
            if (PotFates.TryGetValue(lastPot.FateId, out var lastInfo))
                nextPot = lastInfo.Label;

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
            LastCurrentCE = GetLastCurrentEncounter(
                tracker.GetEncounters(), false, now),
            LastCurrentFate = GetLastCurrentEncounter(
                tracker.GetFates(), true, now),
            NextPot = nextPot,
            NextSpawnTime = nextSpawnTime
        };
    }

    private string GetLastCurrentEncounter(
        IReadOnlyList<PotData> history,
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
            return Unknown;

        var name = isFate
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

        var name = fate?.Name.ToString() ?? "";

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

        var name = ce?.Name.ToString() ?? "";

        if (string.IsNullOrWhiteSpace(name))
            name = $"CE {ceId}";

        ceNameCache[ceId] = name;
        return name;
    }
}
