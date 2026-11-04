namespace PersistentPotsTracker.Models;

public class PotTableRow
{
    public int TrackerId { get; set; }
    public string Datacenter { get; set; } = "";
    public string Zone { get; set; } = "";
    public string LastCurrentCE { get; set; } = "";
    public string LastCurrentFate { get; set; } = "";
    public string NextPot { get; set; } = "";
    public long NextSpawnTime { get; set; }
}
