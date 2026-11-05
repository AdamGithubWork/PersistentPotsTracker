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
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PersistentPotsTracker;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/pots";

    private static readonly TimeSpan RefreshInterval =
        TimeSpan.FromSeconds(45);

    private static readonly TimeSpan MatchRetryDelay =
        TimeSpan.FromSeconds(5);

    private static readonly TimeSpan MatchInterval =
        TimeSpan.FromSeconds(2);

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IFramework framework;
    private readonly IDataManager dataManager;
    private readonly IPluginLog pluginLog;
    private readonly IPlayerState playerState;

    public IClientState ClientState { get; }
    public Configuration Configuration { get; }

    private readonly WindowSystem windowSystem =
        new("PersistentPotsTracker");

    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;
    private readonly PotOverlayWindow potOverlayWindow;

    private readonly TrackerApiClient apiClient = new();
    private readonly CancellationTokenSource apiCancellation = new();

    private readonly PotPredictionService potPredictionService;
    private readonly InstanceMatchingService instanceMatchingService;

    public List<TrackerData> Trackers { get; private set; } = new();
    public List<PotTableRow> PotRows { get; private set; } = new();

    public bool IsRefreshing { get; private set; }
    public string? ApiError { get; private set; }

    public int? MatchedTrackerId => instanceMatchingService.MatchedTrackerId;

    private DateTime lastRefresh = DateTime.MinValue;
    private DateTime lastMatchCheck = DateTime.MinValue;

    private int selectionVersion;
    private bool disposed;
    private bool wasWindowOpen;
    private bool refreshPending;
    private DateTime? entryFollowupAt;
    private DateTime? activityRetryAt;
    private int territoryVersion;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IFramework framework,
        IClientState clientState,
        IDataManager dataManager,
        IFateTable fateTable,
        IPluginLog pluginLog,
        IPlayerState playerState)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.framework = framework;
        this.dataManager = dataManager;
        potPredictionService = new PotPredictionService(dataManager);
        instanceMatchingService = new InstanceMatchingService(fateTable, pluginLog);
        this.pluginLog = pluginLog;
        this.playerState = playerState;

        ClientState = clientState;

        Configuration =
            pluginInterface.GetPluginConfig() as Configuration
            ?? new Configuration();

        Configuration.Initialize(pluginInterface);

        mainWindow = new MainWindow(this);
        configWindow = new ConfigWindow(this);
        potOverlayWindow = new PotOverlayWindow(this);

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

        // Handle loading the plugin while already inside a Horn.
        if (IsHornTerritory(ClientState.TerritoryType))
        {
            refreshPending = true;
            entryFollowupAt = DateTime.UtcNow + MatchRetryDelay;
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
            11 => "Dynamis",
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
        territoryVersion++;
        instanceMatchingService.Reset();

        bool enteringHorn = IsHornTerritory(territoryId);
        refreshPending = enteringHorn;
        entryFollowupAt = enteringHorn
            ? DateTime.UtcNow + MatchRetryDelay
            : null;
        activityRetryAt = null;
    }

    public void DatacenterSelectionChanged()
    {
        selectionVersion++;
        Trackers = new();
        PotRows = new();
        // Keep the locked instance until territory changes.
        ApiError = null;
        lastRefresh = DateTime.MinValue;
        refreshPending = true;
    }

    public async Task RefreshTrackersAsync()
    {
        if (disposed)
            return;

        if (IsRefreshing)
        {
            refreshPending = true;
            return;
        }

        refreshPending = false;

        var datacenters = new List<int>(4);

        if (Configuration.ShowAether)
            datacenters.Add(4);

        if (Configuration.ShowPrimal)
            datacenters.Add(5);

        if (Configuration.ShowCrystal)
            datacenters.Add(8);

        if (Configuration.ShowDynamis)
            datacenters.Add(11);

        if (datacenters.Count == 0)
        {
            Trackers = new();
            PotRows = new();
            ApiError = null;
            lastRefresh = DateTime.UtcNow;
            return;
        }

        IsRefreshing = true;
        int requestVersion = selectionVersion;
        int requestTerritoryVersion = territoryVersion;

        try
        {
            var results =
                await apiClient.GetTrackersAsync(
                    datacenters, apiCancellation.Token);

            if (disposed || requestVersion != selectionVersion ||
                requestTerritoryVersion != territoryVersion)
                return;

            // Preserve usable data if the API returns an empty result.
            if (results.Count > 0)
            {
                Trackers = results;
                PotRows = potPredictionService.BuildPotRows(results);
                ApiError = null;
            }
            else
            {
                ApiError = "The tracker API returned no instances.";
                pluginLog.Warning(ApiError);
            }
        }
        catch (OperationCanceledException)
            when (apiCancellation.IsCancellationRequested)
        {
            // Normal cancellation during plugin shutdown.
        }
        catch (OperationCanceledException)
        {
            if (!disposed && requestVersion == selectionVersion &&
                requestTerritoryVersion == territoryVersion)
            {
                ApiError = "Tracker API request timed out.";
                pluginLog.Warning(ApiError);
            }
        }
        catch (HttpRequestException ex)
        {
            if (!disposed && requestVersion == selectionVersion &&
                requestTerritoryVersion == territoryVersion)
            {
                ApiError = ex.Message;
                pluginLog.Warning(ex, "Tracker API request failed.");
            }
        }
        catch (Exception ex)
        {
            if (!disposed && requestVersion == selectionVersion &&
                requestTerritoryVersion == territoryVersion)
            {
                ApiError = ex.Message;
                pluginLog.Error(ex, "Failed to refresh trackers.");
            }
        }
        finally
        {
            IsRefreshing = false;
            if (disposed)
            {
                apiCancellation.Dispose();
            }
            else
            {
                if (requestVersion == selectionVersion &&
                    requestTerritoryVersion == territoryVersion)
                    lastRefresh = DateTime.UtcNow;
                else if (requestVersion != selectionVersion)
                    refreshPending = true;
            }
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (disposed)
            return;

        DateTime now = DateTime.UtcNow;
        bool insideHorn = IsInsideHorn();

        if (insideHorn && now - lastMatchCheck >= MatchInterval)
        {
            lastMatchCheck = now;
            var (localFates, localCEs) = instanceMatchingService.GetLocalActivity();
            bool newActivity = instanceMatchingService.CheckForNewActivity(
                localFates, localCEs);

            if (newActivity && !MatchedTrackerId.HasValue)
            {
                refreshPending = true;
                activityRetryAt = now + MatchRetryDelay;
            }

            if (instanceMatchingService.TryMatch(
                    Trackers, localFates, localCEs, GetCurrentDatacenter()))
            {
                entryFollowupAt = null;
                activityRetryAt = null;
            }
        }

        if (insideHorn && !MatchedTrackerId.HasValue)
        {
            if (entryFollowupAt.HasValue && now >= entryFollowupAt.Value)
            {
                entryFollowupAt = null;
                refreshPending = true;
            }

            if (activityRetryAt.HasValue && now >= activityRetryAt.Value)
            {
                activityRetryAt = null;
                refreshPending = true;
            }
        }

        bool isWindowOpen = mainWindow.IsOpen;
        if (isWindowOpen && !wasWindowOpen)
            lastRefresh = DateTime.MinValue;
        wasWindowOpen = isWindowOpen;

        if (isWindowOpen && now - lastRefresh >= RefreshInterval)
            refreshPending = true;

        // One outstanding request maximum. All triggers collapse into one
        // pending refresh while a request is running.
        if (refreshPending && !IsRefreshing)
        {
            refreshPending = false;
            _ = RefreshTrackersAsync();
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        apiCancellation.Cancel();

        framework.Update -= OnFrameworkUpdate;
        ClientState.TerritoryChanged -= OnTerritoryChanged;

        commandManager.RemoveHandler(CommandName);

        pluginInterface.UiBuilder.Draw -= DrawUI;
        pluginInterface.UiBuilder.OpenMainUi -= OpenMainUI;
        pluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUI;

        windowSystem.RemoveAllWindows();

        apiClient.Dispose();

        // An in-flight request releases the token source in its finally block.
        if (!IsRefreshing)
            apiCancellation.Dispose();
    }
}
