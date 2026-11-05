using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using PersistentPotsTracker.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PersistentPotsTracker.Services;

public class InstanceMatchingService
{
    private const long RecentFateWindowSeconds = 90;
    private readonly IFateTable fateTable;
    private readonly IPluginLog pluginLog;
    private bool hasActivitySnapshot;
    private HashSet<int> previousFateIds = new();
    private HashSet<int> previousCeIds = new();

    public int? MatchedTrackerId { get; private set; }

    public InstanceMatchingService(IFateTable fateTable, IPluginLog pluginLog)
    {
        this.fateTable = fateTable;
        this.pluginLog = pluginLog;
    }

    public (HashSet<int> Fates, HashSet<int> CEs) GetLocalActivity()
    {
        return (GetLocalFateIds(), GetLocalCEIds());
    }

    // A fresh snapshot establishes the baseline; only subsequent additions count.
    public bool CheckForNewActivity(HashSet<int> currentFates, HashSet<int> currentCEs)
    {
        if (!hasActivitySnapshot)
        {
            previousFateIds = currentFates;
            previousCeIds = currentCEs;
            hasActivitySnapshot = true;
            return false;
        }

        bool newActivity = currentFates.Except(previousFateIds).Any() ||
                           currentCEs.Except(previousCeIds).Any();

        previousFateIds = currentFates;
        previousCeIds = currentCEs;
        return newActivity;
    }

    // Returns true only when a unique match was established in this call.
    public bool TryMatch(
        IReadOnlyCollection<TrackerData> trackers,
        HashSet<int> localFates,
        HashSet<int> localCEs,
        string datacenter)
    {
        if (MatchedTrackerId.HasValue || trackers.Count == 0)
            return false;

        int? match = FindMatch(
            trackers, localFates, localCEs, datacenter,
            out int candidateCount);

        if (match.HasValue)
        {
            MatchedTrackerId = match.Value;
            pluginLog.Information($"INSTANCE MATCH: {match.Value}");
            return true;
        }

        if (candidateCount > 1)
        {
            pluginLog.Debug(
                $"INSTANCE MATCH: Ambiguous ({candidateCount} candidates); waiting.");
        }

        return false;
    }

    // Reset when the game changes territory, including the activity baseline.
    public void Reset()
    {
        if (MatchedTrackerId.HasValue)
            pluginLog.Information("INSTANCE MATCH: NONE");

        MatchedTrackerId = null;
        hasActivitySnapshot = false;
        previousFateIds.Clear();
        previousCeIds.Clear();
    }

    private HashSet<int> GetLocalFateIds()
    {
        var ids = new HashSet<int>();

        foreach (var fate in fateTable)
        {
            if (fate == null)
                continue;

            ids.Add((int)fate.FateId);
        }

        return ids;
    }

    private unsafe HashSet<int> GetLocalCEIds()
    {
        var ids = new HashSet<int>();
        var container = DynamicEventContainer.GetInstance();

        if (container == null)
            return ids;

        foreach (ref var ce in container->Events)
        {
            if (ce.DynamicEventId != 0 && ce.IsActive())
                ids.Add(ce.DynamicEventId);
        }

        return ids;
    }

    // A null result means no unique match was found.
    public int? FindMatch(
        IReadOnlyCollection<TrackerData> trackers,
        HashSet<int> localFates,
        HashSet<int> localCEs,
        string datacenter,
        out int candidateCount)
    {
        candidateCount = 0;

        if (trackers.Count == 0 || (localFates.Count == 0 && localCEs.Count == 0))
            return null;

        int currentDcId = datacenter switch
        {
            "Aether" => 4,
            "Primal" => 5,
            "Crystal" => 8,
            "Dynamis" => 11,
            _ => 0
        };

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int? candidateId = null;

        foreach (var tracker in trackers)
        {
            if (currentDcId != 0 && tracker.Datacenter != currentDcId)
                continue;

            var activeCes = tracker.GetEncounters()
                .Where(ce => ce.SpawnTime > 0 && ce.SpawnTime <= now && ce.DeathTime == 0)
                .ToList();

            var recentFates = tracker.GetFates()
                .Where(f => f.SpawnTime > 0 && f.SpawnTime <= now &&
                            now - f.SpawnTime <= RecentFateWindowSeconds)
                .ToList();

            bool ceMatch = activeCes.Any(ce => localCEs.Contains(ce.FateId));
            bool ceConflict = activeCes.Any(ce => !localCEs.Contains(ce.FateId));
            bool fateMatch = recentFates.Any(f => localFates.Contains(f.FateId));

            if (localCEs.Count > 0)
            {
                if (!ceMatch || ceConflict)
                    continue;
            }
            else
            {
                if (activeCes.Count > 0 || !fateMatch)
                    continue;
            }

            candidateCount++;
            candidateId = tracker.Id;
        }

        return candidateCount == 1 ? candidateId : null;
    }
}
