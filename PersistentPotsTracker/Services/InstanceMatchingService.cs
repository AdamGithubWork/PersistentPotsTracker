using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PersistentPotsTracker.Services;

public class InstanceMatchingService
{
    // FATEs older than this have usually ended or been replaced by the time
    // another player's tracker reports them.
    private const long RecentFateWindowSeconds = 90;

    private readonly IFateTable fateTable;
    private readonly IPluginLog pluginLog;

    private bool hasActivitySnapshot;
    private HashSet<int> previousFateIds = new();
    private HashSet<int> previousCEIds = new();

    public int? MatchedTrackerId { get; private set; }

    public InstanceMatchingService(IFateTable fateTable, IPluginLog pluginLog)
    {
        this.fateTable = fateTable;
        this.pluginLog = pluginLog;
    }

    public HashSet<int> GetLocalFateIds() => fateTable.Select(f => (int)f.FateId).ToHashSet();

    public unsafe HashSet<int> GetLocalCEIds()
    {
        var ceIds = new HashSet<int>();
        var container = DynamicEventContainer.GetInstance();

        if (container == null)
            return ceIds;

        foreach (ref var ce in container->Events)
        {
            if (ce.DynamicEventId != 0 && ce.IsActive())
                ceIds.Add(ce.DynamicEventId);
        }

        return ceIds;
    }

    // True if anything showed up that wasn't in the previous snapshot.
    // The first call only records a baseline, so it never reports new activity.
    public bool CheckForNewActivity(HashSet<int> currentFates, HashSet<int> currentCEs)
    {
        bool hasNewActivity = hasActivitySnapshot &&
            (currentFates.Any(id => !previousFateIds.Contains(id)) ||
             currentCEs.Any(id => !previousCEIds.Contains(id)));

        previousFateIds = currentFates;
        previousCEIds = currentCEs;
        hasActivitySnapshot = true;

        return hasNewActivity;
    }

    public bool TryMatch(IReadOnlyCollection<TrackerData> trackers, HashSet<int> localFates, HashSet<int> localCEs, string datacenter)
    {
        if (MatchedTrackerId.HasValue || trackers.Count == 0)
            return false;

        var match = FindMatch(trackers, localFates, localCEs, datacenter, out int candidateCount);

        if (match.HasValue)
        {
            MatchedTrackerId = match.Value;
            pluginLog.Information(
                $"Matched tracker {match.Value} on {datacenter} ({localFates.Count} fates, {localCEs.Count} CEs locally)");
            return true;
        }

        if (candidateCount > 1)
            pluginLog.Debug($"{candidateCount} trackers fit the local activity, waiting for more to go on.");

        return false;
    }

    public void Reset()
    {
        if (MatchedTrackerId.HasValue)
            pluginLog.Information($"Cleared match for tracker {MatchedTrackerId}.");

        MatchedTrackerId = null;
        hasActivitySnapshot = false;
        previousFateIds = new();
        previousCEIds = new();
    }

    private static int? FindMatch(
        IReadOnlyCollection<TrackerData> trackers,
        HashSet<int> localFates,
        HashSet<int> localCEs,
        string datacenter,
        out int candidateCount)
    {
        candidateCount = 0;

        if (localFates.Count == 0 && localCEs.Count == 0)
            return null;

        // Unknown datacenter (id 0) means don't filter by it.
        int datacenterId = datacenter switch
        {
            "Aether" => 4,
            "Primal" => 5,
            "Crystal" => 8,
            "Materia" => 9,
            "Dynamis" => 11,
            _ => 0
        };
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int? matchingTrackerId = null;

        foreach (var tracker in trackers)
        {
            if (datacenterId != 0 && tracker.Datacenter != datacenterId)
                continue;

            if (!TrackerFitsLocal(tracker, localFates, localCEs, now))
                continue;

            candidateCount++;
            matchingTrackerId = tracker.Id;
        }

        // Only commit on a unique candidate. A wrong match poisons the timers,
        // which costs more than waiting for the next poll.
        return candidateCount == 1 ? matchingTrackerId : null;
    }

    private static bool TrackerFitsLocal(TrackerData tracker, HashSet<int> localFates, HashSet<int> localCEs, long now)
    {
        bool hasActiveCE = false;
        bool hasMatchingCE = false;
        bool hasConflictingCE = false;

        foreach (var ce in tracker.GetEncounters())
        {
            if (ce.SpawnTime <= 0 || ce.SpawnTime > now || ce.DeathTime != 0)
                continue;

            hasActiveCE = true;

            if (localCEs.Contains(ce.FateId))
                hasMatchingCE = true;
            else
                hasConflictingCE = true;
        }

        // CEs are the stronger signal: if we're in one, the tracker has to be
        // running that one and nothing else.
        if (localCEs.Count > 0)
            return hasMatchingCE && !hasConflictingCE;

        // No local CE, so a tracker with a live CE is a different instance.
        if (hasActiveCE)
            return false;

        return tracker.GetFates().Any(fate =>
            fate.SpawnTime > 0 &&
            fate.SpawnTime <= now &&
            now - fate.SpawnTime <= RecentFateWindowSeconds &&
            localFates.Contains(fate.FateId));
    }
}
