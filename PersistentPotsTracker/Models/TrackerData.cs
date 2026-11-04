
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PersistentPotsTracker;

public class TrackerData
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("tracker_id")]
    public string TrackerId { get; set; } = "";

    [JsonPropertyName("datacenter")]
    public int Datacenter { get; set; }

    [JsonPropertyName("server")]
    public int Server { get; set; }

    [JsonPropertyName("territory")]
    public int Territory { get; set; }

    [JsonPropertyName("last_update")]
    public long LastUpdate { get; set; }

    [JsonPropertyName("pot_history")]
    public string PotHistoryJson { get; set; } = "[]";

    [JsonPropertyName("fate_history")]
    public string FateHistoryJson { get; set; } = "[]";

    [JsonPropertyName("encounter_history")]
    public string EncounterHistoryJson { get; set; } = "[]";

    private List<PotData>? cachedPots;
    private List<PotData>? cachedFates;
    private List<PotData>? cachedEncounters;

    public List<PotData> GetPots()
    {
        return cachedPots ??= ParseHistory(PotHistoryJson);
    }

    public List<PotData> GetFates()
    {
        return cachedFates ??= ParseHistory(FateHistoryJson);
    }

    public List<PotData> GetEncounters()
    {
        return cachedEncounters ??= ParseHistory(EncounterHistoryJson);
    }

    private static List<PotData> ParseHistory(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new();

        try
        {
            return JsonSerializer.Deserialize<List<PotData>>(json)
                   ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }
}
