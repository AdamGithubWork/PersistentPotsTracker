namespace PersistentPotsTracker.Models;

public sealed class PotTableRow
{
    public int TrackerId { get; init; }
    public string Datacenter { get; init; } = "";
    public string Zone { get; init; } = "";
    public string LastCurrentCE { get; init; } = "";
    public string LastCurrentFate { get; init; } = "";
    public string NextPot { get; init; } = "";
    public long NextSpawnTime { get; init; }
}
