using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PersistentPotsTracker.Services;

public class TrackerApiClient : IDisposable
{
    private const string BaseUrl = "https://infi.ovh/api/OccultTrackerV3";

    private static readonly TimeSpan MaxDataAge = TimeSpan.FromMinutes(10);

    private readonly HttpClient httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    public TrackerApiClient()
    {
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "PersistentPotsTracker/0.1.0");
    }

    public async Task<List<TrackerData>> GetTrackersAsync(
        IReadOnlyCollection<int> datacenters,
        CancellationToken cancellationToken = default)
    {
        if (datacenters.Count == 0)
            return new List<TrackerData>();

        long cutoff = DateTimeOffset.UtcNow
            .Subtract(MaxDataAge)
            .ToUnixTimeSeconds();

        string dcFilter = string.Join(",", datacenters);

        string url =
            $"{BaseUrl}?last_update=gte.{cutoff}" +
            $"&datacenter=in.({dcFilter})";

        return await httpClient.GetFromJsonAsync<List<TrackerData>>(
            url,
            cancellationToken) ?? new List<TrackerData>();
    }

    public void Dispose()
    {
        httpClient.Dispose();
    }
}
