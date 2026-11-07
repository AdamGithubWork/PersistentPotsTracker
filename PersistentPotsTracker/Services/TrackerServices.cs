using Dalamud.Plugin.Services;
using PersistentPotsTracker.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PersistentPotsTracker.Services;

// Owns tracker data, refresh scheduling, and the API request lifecycle.
//
// Except for completedRequests, state belongs to the calling (Dalamud) thread.
// The HTTP continuation only enqueues a result; Update() applies it.
public sealed class TrackerService : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(45);

    // Follow-up fetch after entering the Horn or seeing new activity, in case
    // the first response was stale and didn't contain our instance yet.
    private static readonly TimeSpan MatchRetryDelay = TimeSpan.FromSeconds(5);

    // Floor between any two requests, however they were triggered.
    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromSeconds(5);

    // Short wait after a datacenter checkbox change, so ticking several boxes
    // in a row sends one request instead of one each.
    private static readonly TimeSpan SelectionDebounce = TimeSpan.FromMilliseconds(750);

    private static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RateLimitBackoff = TimeSpan.FromMinutes(5);

    private readonly Configuration configuration;
    private readonly IPluginLog pluginLog;
    private readonly PotPredictionService potPredictionService;
    private readonly TrackerApiClient apiClient = new();
    private readonly CancellationTokenSource apiCancellation = new();
    private readonly ConcurrentQueue<RefreshResult> completedRequests = new();

    private DateTime lastRefresh = DateTime.MinValue;
    private DateTime nextAllowedRequest = DateTime.MinValue;
    private DateTime? entryFollowupAt;
    private DateTime? activityRetryAt;

    // Bumped when the datacenter selection or territory changes, so a response
    // for an older one is dropped instead of overwriting newer state.
    private int selectionVersion;
    private int territoryVersion;
    private int consecutiveFailures;

    private bool refreshPending;
    private bool wasWindowOpen;
    private bool isRefreshing;
    private bool disposed;
    private Task? activeRequest;

    public List<TrackerData> Trackers { get; private set; } = new();
    public List<PotTableRow> PotRows { get; private set; } = new();
    public string? ApiError { get; private set; }

    public bool IsRefreshing => isRefreshing;
    public bool CanRefresh => !disposed && !isRefreshing && DateTime.UtcNow >= nextAllowedRequest;

    private sealed record RefreshResult(
        int SelectionVersion,
        int TerritoryVersion,
        List<TrackerData>? Trackers,
        string? Error,
        Exception? Exception,
        bool RateLimited,
        bool Cancelled);

    public TrackerService(Configuration configuration, IPluginLog pluginLog, PotPredictionService potPredictionService)
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
        ClearData();
        lastRefresh = DateTime.MinValue;
        refreshPending = true;

        // A new selection skips the normal request floor, but not a failure
        // backoff: after errors or a rate limit we still wait it out.
        if (consecutiveFailures == 0)
            nextAllowedRequest = DateTime.UtcNow + SelectionDebounce;
    }

    // Called by Plugin.OnFrameworkUpdate. Applies completed API results on the
    // Dalamud thread, then decides whether to start a new request.
    public void Update(DateTime now, bool insideHorn, bool matchFound, bool isWindowOpen)
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

        // Opening the window forces a fresh fetch.
        if (isWindowOpen && !wasWindowOpen)
            lastRefresh = DateTime.MinValue;

        wasWindowOpen = isWindowOpen;

        if (isWindowOpen && now - lastRefresh >= RefreshInterval)
            refreshPending = true;

        if (refreshPending && !isRefreshing && now >= nextAllowedRequest)
            _ = RefreshAsync();
    }

    // Call from the Dalamud/UI thread, like the other state-changing methods.
    // The returned task completes when the response is queued; Update applies
    // it on the next framework update.
    public Task RefreshAsync()
    {
        if (disposed)
            return Task.CompletedTask;

        if (isRefreshing)
        {
            refreshPending = true;
            return activeRequest ?? Task.CompletedTask;
        }

        var now = DateTime.UtcNow;

        if (now < nextAllowedRequest)
        {
            refreshPending = true;
            return Task.CompletedTask;
        }

        refreshPending = false;

        var datacenters = new List<int>(5);
        if (configuration.ShowAether) datacenters.Add(4);
        if (configuration.ShowPrimal) datacenters.Add(5);
        if (configuration.ShowCrystal) datacenters.Add(8);
        if (configuration.ShowDynamis) datacenters.Add(11);
        if (configuration.ShowMateria) datacenters.Add(9);

        if (datacenters.Count == 0)
        {
            ClearData();
            lastRefresh = now;
            return Task.CompletedTask;
        }

        isRefreshing = true;
        nextAllowedRequest = now + MinRequestInterval;
        activeRequest = FetchAsync(datacenters, selectionVersion, territoryVersion);

        return activeRequest;
    }

    private void ClearData()
    {
        Trackers = new();
        PotRows = new();
        ApiError = null;
    }

    // Runs off the Dalamud thread after the first await, so it must not touch
    // any state except completedRequests.
    private async Task FetchAsync(List<int> datacenters, int selection, int territory)
    {
        RefreshResult Result(
            List<TrackerData>? trackers = null,
            string? error = null,
            Exception? exception = null,
            bool rateLimited = false,
            bool cancelled = false) =>
            new(selection, territory, trackers, error, exception, rateLimited, cancelled);

        RefreshResult result;

        try
        {
            var trackers = await apiClient.GetTrackersAsync(datacenters, apiCancellation.Token).ConfigureAwait(false);
            result = Result(trackers);
        }
        catch (OperationCanceledException) when (apiCancellation.IsCancellationRequested)
        {
            result = Result(cancelled: true);
        }
        catch (OperationCanceledException ex)
        {
            result = Result(error: "Tracker API request timed out.", exception: ex);
        }
        catch (ApiRateLimitedException ex)
        {
            result = Result(error: "Tracker API rate limit reached. Retrying later.", exception: ex, rateLimited: true);
        }
        catch (JsonException ex)
        {
            result = Result(error: "Tracker API returned an unexpected response.", exception: ex);
        }
        catch (Exception ex)
        {
            result = Result(error: ex.Message, exception: ex);
        }

        completedRequests.Enqueue(result);
    }

    private void ApplyCompletedRequests(DateTime now)
    {
        while (completedRequests.TryDequeue(out var result))
        {
            var current = result.SelectionVersion == selectionVersion &&
                          result.TerritoryVersion == territoryVersion;

            // Cancellation is expected during shutdown, nothing to report.
            if (!result.Cancelled)
            {
                // An empty list counts as a failure: a healthy response always
                // has at least one instance in it.
                if (result.Trackers is { Count: > 0 } trackers)
                    ApplySuccess(trackers, current, now);
                else
                    ApplyFailure(result, current, now);
            }

            if (current)
                lastRefresh = now;
            else if (result.SelectionVersion != selectionVersion)
                refreshPending = true;

            // Mark complete only after results and timestamps are published.
            isRefreshing = false;
            activeRequest = null;
        }
    }

    private void ApplySuccess(List<TrackerData> trackers, bool current, DateTime now)
    {
        consecutiveFailures = 0;

        if (!current)
            return;

        try
        {
            var rows = potPredictionService.BuildPotRows(trackers);

            Trackers = trackers;
            PotRows = rows;
            ApiError = null;
        }
        catch (Exception ex)
        {
            RegisterFailure(now, rateLimited: false);
            ApiError = "Could not process tracker API data.";
            pluginLog.Error(ex, "Failed to build pot predictions.");
        }
    }

    private void ApplyFailure(RefreshResult result, bool current, DateTime now)
    {
        RegisterFailure(now, result.RateLimited);

        if (!current)
            return;

        ApiError = result.Error ?? "The tracker API returned no instances.";

        if (result.Exception != null)
            pluginLog.Warning(result.Exception, "Tracker API refresh failed.");
        else
            pluginLog.Warning(ApiError);
    }

    private void RegisterFailure(DateTime now, bool rateLimited)
    {
        consecutiveFailures++;

        // 30s, 60s, 120s... capped at MaxBackoff. The exponent is clamped so
        // Math.Pow can't run away on a long outage.
        var exponent = Math.Min(consecutiveFailures - 1, 10);
        var backoff = TimeSpan.FromSeconds(
            Math.Min(BaseBackoff.TotalSeconds * Math.Pow(2, exponent), MaxBackoff.TotalSeconds));

        var retryAt = now + (rateLimited ? RateLimitBackoff : backoff);

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

        // Don't dispose the CancellationTokenSource while an HTTP operation
        // is still using it.
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
