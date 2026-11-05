using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Lumina.Excel.Sheets;
using PersistentPotsTracker.Models;
using PersistentPotsTracker.Services;
using PersistentPotsTracker.Windows;
using System;
using System.Collections.Generic;
using System.Linq;
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
    private readonly IFateTable fateTable;
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

    public List<TrackerData> Trackers { get; private set; } = new();
    public List<PotTableRow> PotRows { get; private set; } = new();

    public bool IsRefreshing { get; private set; }
    public string? ApiError { get; private set; }

    public int? MatchedTrackerId { get; private set; }

    private DateTime lastRefresh = DateTime.MinValue;
    private DateTime lastMatchCheck = DateTime.MinValue;

    private int selectionVersion;
    private bool disposed;
    private bool wasWindowOpen;
    private bool refreshPending;
    private DateTime? entryFollowupAt;
    private DateTime? activityRetryAt;
    private int territoryVersion;
    private bool hasActivitySnapshot;
    private HashSet<int> previousFateIds = new();
    private HashSet<int> previousCeIds = new();

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
        this.fateTable = fateTable;
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
        SetMatchedTracker(null);

        bool enteringHorn = IsHornTerritory(territoryId);
        refreshPending = enteringHorn;
        entryFollowupAt = enteringHorn
            ? DateTime.UtcNow + MatchRetryDelay
            : null;
        activityRetryAt = null;
        hasActivitySnapshot = false;
        previousFateIds.Clear();
        previousCeIds.Clear();
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
            CheckLocalActivity();
            TryMatchCurrentInstance();
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

    private void CheckLocalActivity()
    {
        var currentFates = GetLocalFateIds();
        var currentCes = GetLocalCEIds();

        if (!hasActivitySnapshot)
        {
            previousFateIds = currentFates;
            previousCeIds = currentCes;
            hasActivitySnapshot = true;
            return;
        }

        bool newActivity = currentFates.Except(previousFateIds).Any() ||
                           currentCes.Except(previousCeIds).Any();

        previousFateIds = currentFates;
        previousCeIds = currentCes;

        if (!newActivity || MatchedTrackerId.HasValue)
            return;

        refreshPending = true;
        activityRetryAt = DateTime.UtcNow + MatchRetryDelay;
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

    private void TryMatchCurrentInstance()
    {
        if (MatchedTrackerId.HasValue || Trackers.Count == 0)
            return;

        var localFates = GetLocalFateIds();
        var localCEs = GetLocalCEIds();

        if (localFates.Count == 0 && localCEs.Count == 0)
            return;

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string currentDatacenter = GetCurrentDatacenter();

        int currentDcId = currentDatacenter switch
        {
            "Aether" => 4,
            "Primal" => 5,
            "Crystal" => 8,
            "Dynamis" => 11,
            _ => 0
        };

        const long recentWindowSeconds = 90;

        var candidates = new List<int>();

        foreach (var tracker in Trackers)
        {
            if (currentDcId != 0 && tracker.Datacenter != currentDcId)
                continue;

            var activeCes = tracker.GetEncounters()
                .Where(ce =>
                    ce.SpawnTime > 0 &&
                    ce.SpawnTime <= now &&
                    ce.DeathTime == 0)
                .ToList();

            var recentFates = tracker.GetFates()
                .Where(f =>
                    f.SpawnTime > 0 &&
                    f.SpawnTime <= now &&
                    now - f.SpawnTime <= recentWindowSeconds)
                .ToList();

            bool ceMatch = activeCes.Any(ce =>
                localCEs.Contains(ce.FateId));

            bool ceConflict = activeCes.Any(ce =>
                !localCEs.Contains(ce.FateId));

            bool fateMatch = recentFates.Any(f =>
                localFates.Contains(f.FateId));

            if (localCEs.Count > 0)
            {
                // Require a matching CE, and reject conflicting active CEs.
                if (!ceMatch || ceConflict)
                    continue;
            }
            else
            {
                // Don't match a tracker showing a CE we can't see.
                if (activeCes.Count > 0)
                    continue;

                if (!fateMatch)
                    continue;
            }

            candidates.Add(tracker.Id);
        }

        if (candidates.Count == 1)
        {
            SetMatchedTracker(candidates[0]);
        }
        else if (candidates.Count > 1)
        {
            pluginLog.Debug(
                $"INSTANCE MATCH: Ambiguous ({candidates.Count} candidates); waiting.");
        }
    }

    private void SetMatchedTracker(int? trackerId)
    {
        if (MatchedTrackerId == trackerId)
            return;

        MatchedTrackerId = trackerId;

        if (trackerId.HasValue)
        {
            entryFollowupAt = null;
            activityRetryAt = null;
        }

        pluginLog.Information(
            $"INSTANCE MATCH: {trackerId?.ToString() ?? "NONE"}");
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
