
using Dalamud.Plugin.Services;
using PersistentPotsTracker.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PersistentPotsTracker.Services;

// Owns tracker data, refresh scheduling, and the API request lifecycle.
public sealed class TrackerService : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan MatchRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RateLimitBackoff = TimeSpan.FromMinutes(5);

    private readonly Configuration configuration;
    private readonly IPluginLog pluginLog;
    private readonly PotPredictionService potPredictionService;
    private readonly TrackerApiClient apiClient = new();
    private readonly CancellationTokenSource apiCancellation = new();
    private readonly ConcurrentQueue<RefreshResult> completedRequests = new();

    public List<TrackerData> Trackers { get; private set; } = new();
    public List<PotTableRow> PotRows { get; private set; } = new();
    public string? ApiError { get; private set; }
    public bool IsRefreshing => isRefreshing;

    private DateTime lastRefresh = DateTime.MinValue;
    private DateTime nextAllowedRequest = DateTime.MinValue;
    private DateTime? entryFollowupAt;
    private DateTime? activityRetryAt;
    private int consecutiveFailures;
    private int selectionVersion;
    private int territoryVersion;
    private bool refreshPending;
    private bool wasWindowOpen;
    private bool isRefreshing;
    private bool disposed;
    private Task? activeRequest;

    // Except for completedRequests, state belongs to the calling (Dalamud) thread.
    // The HTTP continuation only enqueues a result; Update() applies it.
    private sealed record RefreshResult(
        int SelectionVersion,
        int TerritoryVersion,
        List<TrackerData>? Trackers,
        string? Error,
        Exception? Exception,
        bool RateLimited,
        bool Cancelled);

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

    // Called by Plugin.OnFrameworkUpdate.
    // Applies completed API results on the Dalamud thread.
    public void Update(
        DateTime now,
        bool insideHorn,
        bool matchFound,
        bool isWindowOpen)
    {
        if (disposed)
            return;

        ApplyCompletedRequests(now);

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

        if (refreshPending && !isRefreshing && now >= nextAllowedRequest)
            _ = RefreshAsync();
    }

    // Call from the Dalamud/UI thread, like the other public
    // state-changing methods.
    // The returned task completes when the response is queued.
    // Update applies it on the next framework update.
    public Task RefreshAsync()
    {
        if (disposed)
            return Task.CompletedTask;

        if (isRefreshing)
        {
            refreshPending = true;
            return activeRequest ?? Task.CompletedTask;
        }

        DateTime now = DateTime.UtcNow;

        if (now < nextAllowedRequest)
        {
            refreshPending = true;
            return Task.CompletedTask;
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
            lastRefresh = now;
            return Task.CompletedTask;
        }

        isRefreshing = true;
        nextAllowedRequest = now + MinRequestInterval;

        activeRequest = FetchAsync(
            datacenters,
            selectionVersion,
            territoryVersion);

        return activeRequest;
    }

    // No UI-facing state is modified after the await.
    private async Task FetchAsync(
        List<int> datacenters,
        int selection,
        int territory)
    {
        RefreshResult result;

        try
        {
            var trackers = await apiClient.GetTrackersAsync(
                datacenters,
                apiCancellation.Token).ConfigureAwait(false);

            result = new(
                selection,
                territory,
                trackers,
                null,
                null,
                false,
                false);
        }
        catch (OperationCanceledException)
            when (apiCancellation.IsCancellationRequested)
        {
            result = new(
                selection,
                territory,
                null,
                null,
                null,
                false,
                true);
        }
        catch (OperationCanceledException ex)
        {
            result = new(
                selection,
                territory,
                null,
                "Tracker API request timed out.",
                ex,
                false,
                false);
        }
        catch (HttpRequestException ex)
            when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            result = new(
                selection,
                territory,
                null,
                "Tracker API rate limit reached. Retrying later.",
                ex,
                true,
                false);
        }
        catch (HttpRequestException ex)
        {
            result = new(
                selection,
                territory,
                null,
                ex.Message,
                ex,
                false,
                false);
        }
        catch (JsonException ex)
        {
            result = new(
                selection,
                territory,
                null,
                "Tracker API returned an unexpected response.",
                ex,
                false,
                false);
        }
        catch (Exception ex)
        {
            result = new(
                selection,
                territory,
                null,
                ex.Message,
                ex,
                false,
                false);
        }

        completedRequests.Enqueue(result);
    }

    private void ApplyCompletedRequests(DateTime now)
    {
        while (completedRequests.TryDequeue(out var result))
        {
            bool current =
                result.SelectionVersion == selectionVersion &&
                result.TerritoryVersion == territoryVersion;

            bool success = result.Trackers is { Count: > 0 };

            if (result.Cancelled)
            {
                // Cancellation is expected during shutdown.
            }
            else if (success)
            {
                consecutiveFailures = 0;

                if (current)
                {
                    try
                    {
                        var rows =
                            potPredictionService.BuildPotRows(
                                result.Trackers!);

                        Trackers = result.Trackers!;
                        PotRows = rows;
                        ApiError = null;
                    }
                    catch (Exception ex)
                    {
                        RegisterFailure(now, false);
                        ApiError = "Could not process tracker API data.";

                        pluginLog.Error(
                            ex,
                            "Failed to build pot predictions.");
                    }
                }
            }
            else
            {
                RegisterFailure(now, result.RateLimited);

                if (current)
                {
                    ApiError = result.Error ??
                        "The tracker API returned no instances.";

                    if (result.Exception != null)
                    {
                        pluginLog.Warning(
                            result.Exception,
                            "Tracker API refresh failed.");
                    }
                    else
                    {
                        pluginLog.Warning(ApiError);
                    }
                }
            }

            if (current)
            {
                lastRefresh = now;
            }
            else if (result.SelectionVersion != selectionVersion)
            {
                refreshPending = true;
            }

            // Mark complete after publishing results and timestamps.
            isRefreshing = false;
            activeRequest = null;
        }
    }

    private void RegisterFailure(DateTime now, bool rateLimited)
    {
        consecutiveFailures++;

        double seconds = Math.Min(
            BaseBackoff.TotalSeconds *
                Math.Pow(2, Math.Min(consecutiveFailures - 1, 10)),
            MaxBackoff.TotalSeconds);

        var delay = rateLimited
            ? RateLimitBackoff
            : TimeSpan.FromSeconds(seconds);

        DateTime retryAt = now + delay;

        if (retryAt > nextAllowedRequest)
            nextAllowedRequest = retryAt;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;

        apiCancellation.Cancel();
        apiClient.Dispose();

        // Do not dispose a CancellationTokenSource while
        // an HTTP operation is using it.
        if (activeRequest is { IsCompleted: false } request)
        {
            _ = request.ContinueWith(
                _ => apiCancellation.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        else
        {
            apiCancellation.Dispose();
        }
    }
}
