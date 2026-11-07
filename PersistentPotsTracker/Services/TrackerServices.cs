using Dalamud.Plugin.Services;
using PersistentPotsTracker.Models;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PersistentPotsTracker.Services;

// Owns tracker data, its refresh lifecycle, and the triggers for refreshing.
public sealed class TrackerService : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan MatchRetryDelay = TimeSpan.FromSeconds(5);

    private readonly Configuration configuration;
    private readonly IPluginLog pluginLog;
    private readonly PotPredictionService potPredictionService;
    private readonly TrackerApiClient apiClient = new();
    private readonly CancellationTokenSource apiCancellation = new();

    public List<TrackerData> Trackers { get; private set; } = new();
    public List<PotTableRow> PotRows { get; private set; } = new();
    public string? ApiError { get; private set; }

    // The refresh continuation can resume on a thread-pool thread (Dalamud's framework
    // thread has no synchronization context), so flags shared with Update() are volatile.
    private volatile bool isRefreshing;
    public bool IsRefreshing => isRefreshing;

    private DateTime lastRefresh = DateTime.MinValue;
    private bool wasWindowOpen;
    private volatile bool refreshPending;
    private int selectionVersion;
    private int territoryVersion;
    private DateTime? entryFollowupAt;
    private DateTime? activityRetryAt;
    private volatile bool disposed;

    public TrackerService(
        Configuration configuration,
        IPluginLog pluginLog,
        PotPredictionService potPredictionService)
    {
        this.configuration = configuration;
        this.pluginLog = pluginLog;
        this.potPredictionService = potPredictionService;
    }

    public void OnTerritoryChanged(bool enteringHorn)
    {
        territoryVersion++;
        refreshPending = enteringHorn;
        entryFollowupAt = enteringHorn ? DateTime.UtcNow + MatchRetryDelay : null;
        activityRetryAt = null;
    }

    public void OnNewActivity(DateTime now)
    {
        refreshPending = true;
        activityRetryAt = now + MatchRetryDelay;
    }

    public void OnMatchFound()
    {
        entryFollowupAt = null;
        activityRetryAt = null;
    }

    public void DatacenterSelectionChanged()
    {
        selectionVersion++;
        Trackers = new();
        PotRows = new();
        ApiError = null;
        lastRefresh = DateTime.MinValue;
        refreshPending = true;
    }

    // Called from the Dalamud framework update, without owning the framework event.
    public void Update(DateTime now, bool insideHorn, bool matchFound, bool isWindowOpen)
    {
        if (disposed)
            return;

        if (insideHorn && !matchFound)
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

        if (isWindowOpen && !wasWindowOpen)
            lastRefresh = DateTime.MinValue;
        wasWindowOpen = isWindowOpen;

        if (isWindowOpen && now - lastRefresh >= RefreshInterval)
            refreshPending = true;

        // Collapse multiple triggers into one outstanding request at a time.
        if (refreshPending && !isRefreshing)
        {
            refreshPending = false;
            _ = RefreshAsync();
        }
    }

    public async Task RefreshAsync()
    {
        if (disposed)
            return;

        if (isRefreshing)
        {
            refreshPending = true;
            return;
        }

        refreshPending = false;

        var datacenters = new List<int>(5);

        if (configuration.ShowAether)
            datacenters.Add(4);

        if (configuration.ShowPrimal)
            datacenters.Add(5);

        if (configuration.ShowCrystal)
            datacenters.Add(8);

        if (configuration.ShowDynamis)
            datacenters.Add(11);

        if (configuration.ShowMateria)
            datacenters.Add(9);

        if (datacenters.Count == 0)
        {
            Trackers = new();
            PotRows = new();
            ApiError = null;
            lastRefresh = DateTime.UtcNow;
            return;
        }

        isRefreshing = true;
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
            if (IsCurrent(requestVersion, requestTerritoryVersion))
            {
                ApiError = "Tracker API request timed out.";
                pluginLog.Warning(ApiError);
            }
        }
        catch (HttpRequestException ex)
        {
            if (IsCurrent(requestVersion, requestTerritoryVersion))
            {
                ApiError = ex.Message;
                pluginLog.Warning(ex, "Tracker API request failed.");
            }
        }
        catch (JsonException ex)
        {
            if (IsCurrent(requestVersion, requestTerritoryVersion))
            {
                ApiError = "Tracker API returned an unexpected response.";
                pluginLog.Warning(ex, "Tracker API response could not be parsed.");
            }
        }
        catch (Exception ex)
        {
            if (IsCurrent(requestVersion, requestTerritoryVersion))
            {
                ApiError = ex.Message;
                pluginLog.Error(ex, "Failed to refresh trackers.");
            }
        }
        finally
        {
            if (disposed)
            {
                isRefreshing = false;
                apiCancellation.Dispose();
            }
            else
            {
                if (requestVersion == selectionVersion &&
                    requestTerritoryVersion == territoryVersion)
                    lastRefresh = DateTime.UtcNow;
                else if (requestVersion != selectionVersion)
                    refreshPending = true;

                // Clear the flag last. Clearing it first let Update() see
                // "not refreshing" with a stale lastRefresh and start a second refresh.
                isRefreshing = false;
            }
        }
    }

    private bool IsCurrent(int requestVersion, int requestTerritoryVersion)
    {
        return !disposed &&
               requestVersion == selectionVersion &&
               requestTerritoryVersion == territoryVersion;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        apiCancellation.Cancel();
        apiClient.Dispose();

        // An in-flight request releases the token source in its finally block.
        if (!isRefreshing)
            apiCancellation.Dispose();
    }
}
