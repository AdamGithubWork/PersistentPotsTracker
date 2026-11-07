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

    // The local FATE/CE scan and match attempt run on this interval rather
    // than every frame.
    private static readonly TimeSpan MatchInterval = TimeSpan.FromSeconds(2);

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IFramework framework;
    private readonly IDataManager dataManager;
    private readonly IPlayerState playerState;

    private readonly TrackerService trackerService;
    private readonly InstanceMatchingService instanceMatchingService;
    private readonly LifestreamService lifestreamService;
    private readonly PotMapService potMapService;

    private readonly WindowSystem windowSystem = new("PersistentPotsTracker");
    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;
    private readonly PotOverlayWindow potOverlayWindow;

    private DateTime lastMatchCheck = DateTime.MinValue;
    private bool disposed;

    public IClientState ClientState { get; }
    public Configuration Configuration { get; }

    public bool InsideHorn { get; private set; }
    public string? LastTravelMessage { get; private set; }

    // Tracker state, passed through for the windows.
    public List<TrackerData> Trackers => trackerService.Trackers;
    public List<PotTableRow> PotRows => trackerService.PotRows;
    public bool IsRefreshing => trackerService.IsRefreshing;
    public bool CanRefresh => trackerService.CanRefresh;
    public string? ApiError => trackerService.ApiError;
    public int? MatchedTrackerId => instanceMatchingService.MatchedTrackerId;

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

        Configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(pluginInterface);

        instanceMatchingService = new InstanceMatchingService(fateTable, pluginLog);
        trackerService = new TrackerService(Configuration, pluginLog, new PotPredictionService(dataManager));
        lifestreamService = new LifestreamService(pluginInterface, pluginLog);
        potMapService = new PotMapService(gameGui, dataManager, pluginLog);

        mainWindow = new MainWindow(this);
        configWindow = new ConfigWindow(this);
        potOverlayWindow = new PotOverlayWindow(this);

        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(configWindow);
        windowSystem.AddWindow(potOverlayWindow);

        commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Persistent Pots Tracker."
        });

        pluginInterface.UiBuilder.Draw += windowSystem.Draw;
        pluginInterface.UiBuilder.OpenMainUi += OpenTrackerWindow;
        pluginInterface.UiBuilder.OpenConfigUi += OpenConfigUI;

        framework.Update += OnFrameworkUpdate;
        ClientState.TerritoryChanged += OnTerritoryChanged;

        // The plugin can load while the player is already inside a Horn.
        InsideHorn = IsHornTerritory(ClientState.TerritoryType);
        if (InsideHorn)
            trackerService.OnTerritoryChanged(true);
    }

    public void OpenTrackerWindow() => mainWindow.IsOpen = true;

    public void DatacenterSelectionChanged() => trackerService.DatacenterSelectionChanged();

    public Task RefreshTrackersAsync() => trackerService.RefreshAsync();

    public string GetCurrentDatacenter()
    {
        if (!playerState.IsLoaded || !playerState.CurrentWorld.IsValid)
            return "Unknown";

        return GetDatacenterName(playerState.CurrentWorld.Value.DataCenter.RowId);
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

        return GetDatacenterName(playerState.HomeWorld.Value.DataCenter.RowId);
    }

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
            potMapService.BeginTravel(row, ClientState.TerritoryType);
    }

    public void ShowPotOnMap(PotTableRow row)
    {
        var opened = potMapService.ShowOnMap(row);

        LastTravelMessage = opened
            ? "Opened the predicted pot location on the map."
            : "Could not open the predicted pot location.";
    }

    private void OnCommand(string command, string args) => mainWindow.Toggle();

    private void OpenConfigUI() => configWindow.IsOpen = true;

    private static string GetDatacenterName(uint id) => id switch
    {
        4 => "Aether",
        5 => "Primal",
        8 => "Crystal",
        9 => "Materia",
        11 => "Dynamis",
        _ => "Unknown"
    };

    private bool IsHornTerritory(uint territoryId)
    {
        var territory = dataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(territoryId);
        if (territory == null)
            return false;

        var name = territory.Value.PlaceName.Value.Name.ToString();

        return name.Contains("South Horn", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("North Horn", StringComparison.OrdinalIgnoreCase);
    }

    private void OnTerritoryChanged(uint territoryId)
    {
        InsideHorn = IsHornTerritory(territoryId);
        instanceMatchingService.Reset();
        trackerService.OnTerritoryChanged(InsideHorn);
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (disposed)
            return;

        var now = DateTime.UtcNow;

        // Lifestream is only polled while a travel is pending.
        if (potMapService.HasPendingTravel)
            UpdatePendingTravel(now);

        if (InsideHorn && now - lastMatchCheck >= MatchInterval)
        {
            lastMatchCheck = now;
            UpdateInstanceMatch(now);
        }

        trackerService.Update(now, InsideHorn, MatchedTrackerId.HasValue, mainWindow.IsOpen);
    }

    private void UpdatePendingTravel(DateTime now)
    {
        bool? lifestreamBusy = lifestreamService.TryGetBusy(out var busy) ? busy : null;

        potMapService.Update(
            now,
            ClientState.TerritoryType,
            GetCurrentDatacenter(),
            playerState.IsLoaded,
            lifestreamBusy);
    }

    private void UpdateInstanceMatch(DateTime now)
    {
        var localFates = instanceMatchingService.GetLocalFateIds();
        var localCEs = instanceMatchingService.GetLocalCEIds();

        // Once we've matched, new activity is expected and shouldn't trigger refetches.
        if (instanceMatchingService.CheckForNewActivity(localFates, localCEs) && !MatchedTrackerId.HasValue)
            trackerService.OnNewActivity(now);

        if (instanceMatchingService.TryMatch(Trackers, localFates, localCEs, GetCurrentDatacenter()))
            trackerService.OnMatchFound();
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;

        framework.Update -= OnFrameworkUpdate;
        ClientState.TerritoryChanged -= OnTerritoryChanged;

        commandManager.RemoveHandler(CommandName);

        pluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        pluginInterface.UiBuilder.OpenMainUi -= OpenTrackerWindow;
        pluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUI;

        windowSystem.RemoveAllWindows();

        trackerService.Dispose();
    }
}
