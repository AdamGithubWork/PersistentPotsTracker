using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PersistentPotsTracker.Services;

// RetryAfter is null when the server doesn't say how long to wait.
public sealed class ApiRateLimitedException : Exception
{
    public TimeSpan? RetryAfter { get; }

    public ApiRateLimitedException(TimeSpan? retryAfter)
        : base("The tracker API rate-limited this client.")
    {
        RetryAfter = retryAfter;
    }
}

public sealed class TrackerApiClient : IDisposable
{
    private const string BaseUrl = "https://infi.ovh/api/OccultTrackerV3";

    private const long MaxResponseBytes = 8 * 1024 * 1024;

    // The cutoff gets rounded down to this, so requests made close together
    // share a URL and can be served from the HTTP cache.
    private const long CutoffRoundingSeconds = 60;

    // Only ask for trackers updated within this window.
    private static readonly TimeSpan MaxDataAge = TimeSpan.FromMinutes(10);

    private readonly HttpClient httpClient;

    public TrackerApiClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxAutomaticRedirections = 3
        };

        httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10),
            MaxResponseContentBufferSize = MaxResponseBytes
        };

        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("PersistentPotsTracker/0.1.0");
    }

    public async Task<List<TrackerData>> GetTrackersAsync(
        IReadOnlyCollection<int> datacenters,
        CancellationToken cancellationToken = default)
    {
        if (datacenters.Count == 0)
            return new();

        var cutoff = DateTimeOffset.UtcNow.Subtract(MaxDataAge).ToUnixTimeSeconds();
        cutoff -= cutoff % CutoffRoundingSeconds;

        // Sorted and deduped so the same selection always builds the same URL
        // (same cache reasoning as the cutoff).
        var dcFilter = string.Join(",", datacenters.Distinct().OrderBy(id => id));
        var url = $"{BaseUrl}?last_update=gte.{cutoff}&datacenter=in.({dcFilter})";

        using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new ApiRateLimitedException(GetRetryDelay(response));

        response.EnsureSuccessStatusCode();

        return await response.Content
            .ReadFromJsonAsync<List<TrackerData>>(cancellationToken: cancellationToken)
            .ConfigureAwait(false) ?? new();
    }

    // Retry-After can be a number of seconds or an HTTP date.
    private static TimeSpan? GetRetryDelay(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;

        if (retryAfter?.Delta is { } delta)
            return delta;

        if (retryAfter?.Date is { } date)
            return date - DateTimeOffset.UtcNow;

        return null;
    }

    public void Dispose() => httpClient.Dispose();
}
