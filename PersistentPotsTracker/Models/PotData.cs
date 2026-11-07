using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PersistentPotsTracker;

public class PotData
{
    private List<long> respawnTimes = new();

    [JsonPropertyName("fate_id")]
    public int FateId { get; set; }

    [JsonPropertyName("spawn_time")]
    public long SpawnTime { get; set; }

    [JsonPropertyName("death_time")]
    public long DeathTime { get; set; }

    [JsonPropertyName("last_seen")]
    public long LastSeen { get; set; }

    [JsonPropertyName("respawn_times")]
    public List<long> RespawnTimes
    {
        get => respawnTimes;
        set => respawnTimes = value ?? new List<long>();
    }

    [JsonPropertyName("killed_fates")]
    public int KilledFates { get; set; }

    [JsonPropertyName("killed_ces")]
    public int KilledCes { get; set; }

    [JsonPropertyName("state")]
    public int State { get; set; }
}
