
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using PersistentPotsTracker.Models;
using System;
using System.Collections.Generic;

namespace PersistentPotsTracker.Services;

public sealed class PotMapService
{
    private const uint SouthHornTerritory = 1252;
    private const uint NorthHornTerritory = 1346;
    private const uint PhantomVillageTerritory = 1278;

    private static readonly TimeSpan TravelTimeout = TimeSpan.FromMinutes(10);

    // Give the zone time to finish loading before opening the map.
    private static readonly TimeSpan ArrivalDelay = TimeSpan.FromSeconds(10);

    private readonly record struct PotLocation(uint Territory, float X, float Y, string Name);

    private static readonly Dictionary<(string Zone, string Pot), PotLocation> Locations = new()
    {
        [("South Horn", "S")] = new(SouthHornTerritory, 11.9f, 32.0f, "Pleading Pots"),
        [("South Horn", "N")] = new(SouthHornTerritory, 25.6f, 17.1f, "Persistent Pots"),
        [("North Horn", "S")] = new(NorthHornTerritory, 11.0f, 25.8f, "In a Pot of Bother"),
        [("North Horn", "N")] = new(NorthHornTerritory, 26.2f, 11.6f, "Daylight Pottery")
    };

    private readonly IGameGui gameGui;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;

    private PotTableRow? pendingRow;
    private DateTime travelStarted;
    private DateTime? arrivalSince;
    private uint startingTerritory;
    private bool wasLifestreamBusy;

    public bool HasPendingTravel => pendingRow != null;

    public PotMapService(IGameGui gameGui, IDataManager dataManager, IPluginLog log)
    {
        this.gameGui = gameGui;
        this.dataManager = dataManager;
        this.log = log;
    }

    public bool ShowOnMap(PotTableRow row)
    {
        if (!Locations.TryGetValue((row.Zone, row.NextPot), out var location))
            return false;

        try
        {
            var territory = dataManager.GetExcelSheet<TerritoryType>()
                .GetRowOrDefault(location.Territory);

            if (territory == null)
                return false;

            var mapId = territory.Value.Map.RowId;

            if (mapId == 0)
                return false;

            var link = new MapLinkPayload(
                location.Territory, mapId, location.X, location.Y);

            bool opened = gameGui.OpenMapWithMapLink(link);

            if (opened)
                log.Information($"Opened map for {location.Name} at {location.X}, {location.Y}");

            return opened;
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"Couldn't open map for {row.Zone} / {row.NextPot}.");
            return false;
        }
    }

    public void BeginTravel(PotTableRow row, uint currentTerritory)
    {
        pendingRow = row;
        startingTerritory = currentTerritory;
        travelStarted = DateTime.UtcNow;
        arrivalSince = null;
        wasLifestreamBusy = false;
    }

    public void CancelPendingTravel()
    {
        pendingRow = null;
        arrivalSince = null;
        wasLifestreamBusy = false;
    }

    public void Update(
        DateTime now,
        uint currentTerritory,
        string currentDatacenter,
        bool playerLoaded,
        bool? lifestreamBusy)
    {
        if (pendingRow is not { } row)
            return;

        if (now - travelStarted > TravelTimeout)
        {
            log.Debug("Pending travel timed out.");
            CancelPendingTravel();
            return;
        }

        if (!playerLoaded || lifestreamBusy == null)
        {
            arrivalSince = null;
            return;
        }

        if (lifestreamBusy.Value)
        {
            wasLifestreamBusy = true;
            arrivalSince = null;
            return;
        }


        if (!string.Equals(GetRegion(currentDatacenter), GetRegion(row.Datacenter), StringComparison.OrdinalIgnoreCase))
        {
            arrivalSince = null;
            return;
        }


        if (!HasArrived(row, currentTerritory))
        {
            arrivalSince = null;
            return;
        }

        arrivalSince ??= now;

        if (now - arrivalSince.Value < ArrivalDelay)
            return;

        CancelPendingTravel();
        ShowOnMap(row);
    }

    private static string GetRegion(string datacenter) =>
        datacenter is "Aether" or "Primal" or "Crystal" or "Dynamis" or "North America"
            ? "North America"
            : datacenter;

    private bool HasArrived(PotTableRow row, uint currentTerritory)
    {
        if (Locations.TryGetValue((row.Zone, row.NextPot), out var location) &&
            currentTerritory == location.Territory)
        {
            return true;
        }

        // Don't count Phantom Village unless the player has actually traveled.
        return currentTerritory == PhantomVillageTerritory &&
               (wasLifestreamBusy || currentTerritory != startingTerritory);
    }
}
