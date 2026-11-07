
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using PersistentPotsTracker.Models;
using System;

namespace PersistentPotsTracker.Services;

public sealed class PotMapService
{
    private const uint SouthHornTerritory = 1252;
    private const uint NorthHornTerritory = 1346;
    private const uint PhantomVillageTerritory = 1278;

    private static readonly TimeSpan TravelTimeout =
        TimeSpan.FromMinutes(10);

    private static readonly TimeSpan ArrivalDelay =
        TimeSpan.FromSeconds(10);

    private readonly IGameGui gameGui;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;

    private PotTableRow? pendingRow;
    public bool HasPendingTravel => pendingRow != null;
    private DateTime travelStarted;
    private DateTime? idleSince;
    private uint startingTerritory;
    private bool observedBusy;

    public PotMapService(
        IGameGui gameGui,
        IDataManager dataManager,
        IPluginLog log)
    {
        this.gameGui = gameGui;
        this.dataManager = dataManager;
        this.log = log;
    }

    public bool ShowOnMap(PotTableRow row)
    {
        if (!TryGetLocation(
                row, out uint territoryId,
                out float x, out float y,
                out string potName))
        {
            return false;
        }

        try
        {
            var territory = dataManager
                .GetExcelSheet<TerritoryType>()
                .GetRowOrDefault(territoryId);

            if (territory == null)
                return false;

            uint mapId = territory.Value.Map.RowId;

            if (mapId == 0)
                return false;

            var link = new MapLinkPayload(
                territoryId, mapId, x, y);

            bool opened = gameGui.OpenMapWithMapLink(link);

            if (opened)
            {
                log.Information(
                    $"Opened map for {potName} at {x}, {y}");
            }

            return opened;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to open pot map.");
            return false;
        }
    }

    public void BeginTravel(
        PotTableRow row,
        uint currentTerritory)
    {
        pendingRow = row;
        startingTerritory = currentTerritory;
        travelStarted = DateTime.UtcNow;
        idleSince = null;
        observedBusy = false;
    }

    public void Update(
        DateTime now,
        uint currentTerritory,
        string currentDatacenter,
        bool playerLoaded,
        bool? lifestreamBusy)
    {
        if (pendingRow == null)
            return;

        if (now - travelStarted > TravelTimeout)
        {
            CancelPendingTravel();
            return;
        }

        if (!playerLoaded || lifestreamBusy == null)
        {
            idleSince = null;
            return;
        }

        if (lifestreamBusy.Value)
        {
            observedBusy = true;
            idleSince = null;
            return;
        }

        var row = pendingRow;

        if (!string.Equals(
                currentDatacenter,
                row.Datacenter,
                StringComparison.OrdinalIgnoreCase))
        {
            idleSince = null;
            return;
        }

        uint destinationTerritory = row.Zone switch
        {
            "South Horn" => SouthHornTerritory,
            "North Horn" => NorthHornTerritory,
            _ => 0
        };

        bool arrivedInHorn =
            destinationTerritory != 0 &&
            currentTerritory == destinationTerritory;

        bool completedTravelToVillage =
            currentTerritory == PhantomVillageTerritory &&
            (observedBusy ||
             currentTerritory != startingTerritory);

        if (!arrivedInHorn && !completedTravelToVillage)
        {
            idleSince = null;
            return;
        }

        idleSince ??= now;

        if (now - idleSince.Value < ArrivalDelay)
            return;

        CancelPendingTravel();
        ShowOnMap(row);
    }

    public void CancelPendingTravel()
    {
        pendingRow = null;
        idleSince = null;
        observedBusy = false;
    }

    private static bool TryGetLocation(
    PotTableRow row,
    out uint territoryId,
    out float x,
    out float y,
    out string potName)
    {
        territoryId = 0;
        x = 0;
        y = 0;
        potName = "";

        switch ((row.Zone, row.NextPot))
        {
            case ("South Horn", "S"):
                territoryId = SouthHornTerritory;
                x = 11.9f;
                y = 32.0f;
                potName = "Pleading Pots";
                break;

            case ("South Horn", "N"):
                territoryId = SouthHornTerritory;
                x = 25.6f;
                y = 17.1f;
                potName = "Persistent Pots";
                break;

            case ("North Horn", "S"):
                territoryId = NorthHornTerritory;
                x = 11.0f;
                y = 25.8f;
                potName = "In a Pot of Bother";
                break;

            case ("North Horn", "N"):
                territoryId = NorthHornTerritory;
                x = 26.2f;
                y = 11.6f;
                potName = "Daylight Pottery";
                break;

            default:
                return false;
        }

        return true;
    }
}
