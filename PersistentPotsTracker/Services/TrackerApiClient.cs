
using PersistentPotsTracker.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PersistentPotsTracker.Services;

/// Thrown when the tracker API returns HTTP 429 Too Many Requests.

public sealed class ApiRateLimitedException : Exception
{
    public TimeSpan? RetryAfter { get; }

    public ApiRateLimitedException(TimeSpan? retryAfter)
        : base("The tracker API rate-limited this client.")
    {
        RetryAfter = retryAfter;
    }
}


/// Handles HTTP requests to the OccultTracker API.

public sealed class TrackerApiClient : IDisposable
{
    private const string BaseUrl =
        "https://infi.ovh/api/OccultTrackerV3";

    private const long MaxResponseBytes = 8 * 1024 * 1024;
    private const long CutoffRoundingSeconds = 60;

    private static readonly TimeSpan MaxDataAge =
        TimeSpan.FromMinutes(10);

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

        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "PersistentPotsTracker/0.1.0");
    }


    /// Fetches recently updated tracker data for the selected datacenters.
    public async Task<List<TrackerData>> GetTrackersAsync(
        IReadOnlyCollection<int> datacenters,
        CancellationToken cancellationToken = default)
    {
        if (datacenters.Count == 0)
            return new List<TrackerData>();

        // Fetch trackers updated within approximately the last 10 minutes.
        // Round the timestamp down to improve shared HTTP cache reuse.
        long cutoff = DateTimeOffset.UtcNow
            .Subtract(MaxDataAge)
            .ToUnixTimeSeconds();

        cutoff -= cutoff % CutoffRoundingSeconds;

        // Stable ordering keeps equivalent datacenter selections
        // producing identical URLs.
        string dcFilter = string.Join(
            ",",
            datacenters.Distinct().OrderBy(id => id));

        string url =
            $"{BaseUrl}?last_update=gte.{cutoff}" +
            $"&datacenter=in.({dcFilter})";

        using var response = await httpClient.GetAsync(
            url,
            cancellationToken).ConfigureAwait(false);

        // Respect server rate-limit responses.
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter;
            TimeSpan? wait = retryAfter?.Delta;

            if (wait == null &&
                retryAfter?.Date is DateTimeOffset retryDate)
            {
                wait = retryDate - DateTimeOffset.UtcNow;
            }

            throw new ApiRateLimitedException(wait);
        }

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<List<TrackerData>>(
            cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? new List<TrackerData>();
    }

    public void Dispose()
    {
        httpClient.Dispose();
    }
}
