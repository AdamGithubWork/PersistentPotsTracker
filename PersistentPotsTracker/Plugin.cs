
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using PersistentPotsTracker.Models;
using PersistentPotsTracker.Services;
using PersistentPotsTracker.Windows;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PersistentPotsTracker;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/pots";

    private static readonly TimeSpan MatchInterval =
        TimeSpan.FromSeconds(2);
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IFramework framework;
    private readonly IDataManager dataManager;
    private readonly IPlayerState playerState;
    public IClientState ClientState { get; }
    public Configuration Configuration { get; }

    private readonly WindowSystem windowSystem =
        new("PersistentPotsTracker");

    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;
    private readonly PotOverlayWindow potOverlayWindow;
    private readonly PotMapService potMapService;
    private readonly TrackerService trackerService;
    private readonly InstanceMatchingService instanceMatchingService;
    private readonly LifestreamService lifestreamService;

    public List<TrackerData> Trackers => trackerService.Trackers;
    public List<PotTableRow> PotRows => trackerService.PotRows;
    public bool IsRefreshing => trackerService.IsRefreshing;
    public string? ApiError => trackerService.ApiError;

    public int? MatchedTrackerId =>
        instanceMatchingService.MatchedTrackerId;

    public string? LastTravelMessage { get; private set; }

    private DateTime lastMatchCheck = DateTime.MinValue;
    private bool disposed;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IFramework framework,
        IClientState clientState,
        IDataManager dataManager,
        IFateTable fateTable,
        IPluginLog pluginLog,
        IPlayerState playerState,
        IGameGui gameGui)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.framework = framework;
        this.dataManager = dataManager;
        this.playerState = playerState;

        ClientState = clientState;

        Configuration =
            pluginInterface.GetPluginConfig() as Configuration
            ?? new Configuration();

        Configuration.Initialize(pluginInterface);

        instanceMatchingService =
            new InstanceMatchingService(fateTable, pluginLog);

        trackerService = new TrackerService(
            Configuration,
            pluginLog,
            new PotPredictionService(dataManager));

        lifestreamService = new LifestreamService(
            pluginInterface,
            pluginLog);

        mainWindow = new MainWindow(this);
        configWindow = new ConfigWindow(this);
        potOverlayWindow = new PotOverlayWindow(this);
        potMapService = new PotMapService(
    gameGui,
    dataManager,
    pluginLog);
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(configWindow);
        windowSystem.AddWindow(potOverlayWindow);

        commandManager.AddHandler(
            CommandName,
            new CommandInfo(OnCommand)
            {
                HelpMessage = "Open Persistent Pots Tracker."
            });

        pluginInterface.UiBuilder.Draw += DrawUI;
        pluginInterface.UiBuilder.OpenMainUi += OpenMainUI;
        pluginInterface.UiBuilder.OpenConfigUi += OpenConfigUI;

        framework.Update += OnFrameworkUpdate;
        ClientState.TerritoryChanged += OnTerritoryChanged;

        // Handle loading while already inside a Horn.
        if (IsHornTerritory(ClientState.TerritoryType))
        {
            trackerService.OnTerritoryChanged(true);
        }
    }

    private void OnCommand(string command, string args)
    {
        mainWindow.Toggle();
    }

    private void DrawUI()
    {
        windowSystem.Draw();
    }

    private void OpenMainUI()
    {
        mainWindow.IsOpen = true;
    }

    private void OpenConfigUI()
    {
        configWindow.IsOpen = true;
    }

    public void OpenTrackerWindow()
    {
        mainWindow.IsOpen = true;
    }

    public string GetCurrentDatacenter()
    {
        if (!playerState.IsLoaded)
            return "Unknown";

        var world = playerState.CurrentWorld;

        if (!world.IsValid)
            return "Unknown";

        uint datacenterId = world.Value.DataCenter.RowId;

        return datacenterId switch
        {
            4 => "Aether",
            5 => "Primal",
            8 => "Crystal",
            9 => "Materia",
            11 => "Dynamis",
            _ => "Unknown"
        };
    }
    public string GetHomeWorld()
    {
        if (!playerState.IsLoaded || !playerState.HomeWorld.IsValid)
            return "";

        return playerState.HomeWorld.Value.Name.ToString();
    }

    public string GetHomeDatacenter()
    {
        if (!playerState.IsLoaded || !playerState.HomeWorld.IsValid)
            return "Unknown";

        uint id = playerState.HomeWorld.Value.DataCenter.RowId;

        return id switch
        {
            4 => "Aether",
            5 => "Primal",
            8 => "Crystal",
            11 => "Dynamis",
            9 => "Materia",
            _ => "Unknown"
        };
    }
    private bool IsHornTerritory(uint territoryId)
    {
        var sheet = dataManager.GetExcelSheet<TerritoryType>();
        var territory = sheet.GetRowOrDefault(territoryId);

        if (territory == null)
            return false;

        string name =
            territory.Value.PlaceName.Value.Name.ToString();

        return name.Contains(
                   "South Horn",
                   StringComparison.OrdinalIgnoreCase)
               ||
               name.Contains(
                   "North Horn",
                   StringComparison.OrdinalIgnoreCase);
    }

    private bool IsInsideHorn()
    {
        return IsHornTerritory(ClientState.TerritoryType);
    }

    private void OnTerritoryChanged(uint territoryId)
    {
        instanceMatchingService.Reset();

        trackerService.OnTerritoryChanged(
            IsHornTerritory(territoryId));
    }

    public void DatacenterSelectionChanged()
    {
        trackerService.DatacenterSelectionChanged();
    }

    public Task RefreshTrackersAsync()
    {
        return trackerService.RefreshAsync();
    }

    public void TravelToTracker(PotTableRow row)
    {
        bool requested = lifestreamService.TryTravel(
            row,
            GetCurrentDatacenter(),
            GetHomeDatacenter(),
            GetHomeWorld(),
            out var message);

        LastTravelMessage = message;

        if (requested)
        {
            potMapService.BeginTravel(
                row,
                ClientState.TerritoryType);
        }
    }
    public void ShowPotOnMap(PotTableRow row)
    {
        bool opened = potMapService.ShowOnMap(row);

        LastTravelMessage = opened
            ? "Opened the predicted pot location on the map."
            : "Could not open the predicted pot location.";
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (disposed)
            return;

        DateTime now = DateTime.UtcNow;

        // Only check Lifestream while waiting for travel to finish.
        if (potMapService.HasPendingTravel)
        {
            bool? lifestreamBusy =
                lifestreamService.TryGetBusy(out bool busy)
                    ? busy
                    : null;

            potMapService.Update(
                now,
                ClientState.TerritoryType,
                GetCurrentDatacenter(),
                playerState.IsLoaded,
                lifestreamBusy);
        }

        bool insideHorn = IsInsideHorn();

        // Check local activity and match instances every 2 seconds.
        if (insideHorn &&
            now - lastMatchCheck >= MatchInterval)
        {
            lastMatchCheck = now;

            var (localFates, localCEs) =
                instanceMatchingService.GetLocalActivity();

            bool newActivity =
                instanceMatchingService.CheckForNewActivity(
                    localFates,
                    localCEs);

            if (newActivity && !MatchedTrackerId.HasValue)
            {
                trackerService.OnNewActivity(now);
            }

            bool foundMatch =
                instanceMatchingService.TryMatch(
                    Trackers,
                    localFates,
                    localCEs,
                    GetCurrentDatacenter());

            if (foundMatch)
            {
                trackerService.OnMatchFound();
            }
        }

        // Lightweight refresh scheduling.
        trackerService.Update(
            now,
            insideHorn,
            MatchedTrackerId.HasValue,
            mainWindow.IsOpen);
    }


    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;

        framework.Update -= OnFrameworkUpdate;
        ClientState.TerritoryChanged -= OnTerritoryChanged;

        commandManager.RemoveHandler(CommandName);

        pluginInterface.UiBuilder.Draw -= DrawUI;
        pluginInterface.UiBuilder.OpenMainUi -= OpenMainUI;
        pluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUI;

        windowSystem.RemoveAllWindows();

        trackerService.Dispose();
    }
}
