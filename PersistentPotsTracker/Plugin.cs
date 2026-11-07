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

    // Dalamud services
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IFramework framework;
    private readonly IDataManager dataManager;
    private readonly IPlayerState playerState;

    public IClientState ClientState { get; }
    public Configuration Configuration { get; }

    // Plugin services
    private readonly TrackerService trackerService;
    private readonly InstanceMatchingService instanceMatchingService;
    private readonly LifestreamService lifestreamService;
    private readonly PotMapService potMapService;

    // Windows
    private readonly WindowSystem windowSystem =
        new("PersistentPotsTracker");

    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;
    private readonly PotOverlayWindow potOverlayWindow;

    // Tracker data exposed to windows
    public List<TrackerData> Trackers => trackerService.Trackers;
    public List<PotTableRow> PotRows => trackerService.PotRows;
    public bool IsRefreshing => trackerService.IsRefreshing;
    public bool CanRefresh => trackerService.CanRefresh;
    public string? ApiError => trackerService.ApiError;

    public int? MatchedTrackerId =>
        instanceMatchingService.MatchedTrackerId;

    public string? LastTravelMessage { get; private set; }

    // State
    private bool insideHorn;
    public bool InsideHorn => insideHorn;

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

        // Configuration
        Configuration =
            pluginInterface.GetPluginConfig() as Configuration
            ?? new Configuration();

        Configuration.Initialize(pluginInterface);

        // Services
        instanceMatchingService =
            new InstanceMatchingService(fateTable, pluginLog);

        trackerService = new TrackerService(
            Configuration,
            pluginLog,
            new PotPredictionService(dataManager));

        lifestreamService = new LifestreamService(
            pluginInterface,
            pluginLog);

        potMapService = new PotMapService(
            gameGui,
            dataManager,
            pluginLog);

        // Windows
        mainWindow = new MainWindow(this);
        configWindow = new ConfigWindow(this);
        potOverlayWindow = new PotOverlayWindow(this);

        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(configWindow);
        windowSystem.AddWindow(potOverlayWindow);

        // Command
        commandManager.AddHandler(
            CommandName,
            new CommandInfo(OnCommand)
            {
                HelpMessage = "Open Persistent Pots Tracker."
            });

        // UI events
        pluginInterface.UiBuilder.Draw += DrawUI;
        pluginInterface.UiBuilder.OpenMainUi += OpenMainUI;
        pluginInterface.UiBuilder.OpenConfigUi += OpenConfigUI;

        // Game events
        framework.Update += OnFrameworkUpdate;
        ClientState.TerritoryChanged += OnTerritoryChanged;

        // Handle loading while already inside a Horn.
        insideHorn = IsHornTerritory(ClientState.TerritoryType);

        if (insideHorn)
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

    // Datacenter information
    private static string GetDatacenterName(uint id)
    {
        return id switch
        {
            4 => "Aether",
            5 => "Primal",
            8 => "Crystal",
            9 => "Materia",
            11 => "Dynamis",
            _ => "Unknown"
        };
    }

    public string GetCurrentDatacenter()
    {
        if (!playerState.IsLoaded)
            return "Unknown";

        var world = playerState.CurrentWorld;

        if (!world.IsValid)
            return "Unknown";

        return GetDatacenterName(
            world.Value.DataCenter.RowId);
    }

    public string GetHomeWorld()
    {
        if (!playerState.IsLoaded ||
            !playerState.HomeWorld.IsValid)
        {
            return "";
        }

        return playerState.HomeWorld.Value.Name.ToString();
    }

    public string GetHomeDatacenter()
    {
        if (!playerState.IsLoaded ||
            !playerState.HomeWorld.IsValid)
        {
            return "Unknown";
        }

        return GetDatacenterName(
            playerState.HomeWorld.Value.DataCenter.RowId);
    }

    // Territory detection
    private bool IsHornTerritory(uint territoryId)
    {
        var sheet = dataManager.GetExcelSheet<TerritoryType>();
        var territory = sheet.GetRowOrDefault(territoryId);

        if (territory == null)
            return false;

        var name =
            territory.Value.PlaceName.Value.Name.ToString();

        return name.Contains(
                   "South Horn",
                   StringComparison.OrdinalIgnoreCase)
               ||
               name.Contains(
                   "North Horn",
                   StringComparison.OrdinalIgnoreCase);
    }

    private void OnTerritoryChanged(uint territoryId)
    {
        insideHorn = IsHornTerritory(territoryId);

        instanceMatchingService.Reset();

        trackerService.OnTerritoryChanged(insideHorn);
    }

    // Tracker management
    public void DatacenterSelectionChanged()
    {
        trackerService.DatacenterSelectionChanged();
    }

    public Task RefreshTrackersAsync()
    {
        return trackerService.RefreshAsync();
    }

    // Travel and map integration
    public void TravelToTracker(PotTableRow row)
    {
        var requested = lifestreamService.TryTravel(
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
        var opened = potMapService.ShowOnMap(row);

        LastTravelMessage = opened
            ? "Opened the predicted pot location on the map."
            : "Could not open the predicted pot location.";
    }

    // Framework updates
    private void OnFrameworkUpdate(IFramework framework)
    {
        if (disposed)
            return;

        var now = DateTime.UtcNow;

        // Only check Lifestream while travel is pending.
        if (potMapService.HasPendingTravel)
        {
            bool? lifestreamBusy =
                lifestreamService.TryGetBusy(out var busy)
                    ? busy
                    : null;

            potMapService.Update(
                now,
                ClientState.TerritoryType,
                GetCurrentDatacenter(),
                playerState.IsLoaded,
                lifestreamBusy);
        }

        // Check local activity and instance matching
        // approximately every two seconds.
        if (insideHorn &&
            now - lastMatchCheck >= MatchInterval)
        {
            lastMatchCheck = now;

            var (localFates, localCEs) =
                instanceMatchingService.GetLocalActivity();

            var newActivity =
                instanceMatchingService.CheckForNewActivity(
                    localFates,
                    localCEs);

            if (newActivity && !MatchedTrackerId.HasValue)
            {
                trackerService.OnNewActivity(now);
            }

            var foundMatch =
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

        // Lightweight API refresh scheduling.
        trackerService.Update(
            now,
            insideHorn,
            MatchedTrackerId.HasValue,
            mainWindow.IsOpen);
    }

    // Cleanup
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
