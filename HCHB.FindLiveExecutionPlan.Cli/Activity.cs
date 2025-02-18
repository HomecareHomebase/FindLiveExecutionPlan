using System.Text.Json.Serialization;

namespace HCHB.FindLiveExecutionPlan.Cli;

public class Activity : ActivityBase
{
    // TODO: Remove actual unhelpful properties? They might be useful tough.
    public int SessionId { get; set; }
    public string Duration { get; set; }
    public string LoginName { get; set; }
    public string WaitInfo { get; set; }
    public string Cpu { get; set; }
    public string TempdbAllocations { get; set; }
    public string TempdbCurrent { get; set; }
    public int BlockingSessionId { get; set; }
    public string Reads { get; set; }
    public string Writes { get; set; }
    public string PhysicalReads { get; set; }
    [JsonIgnore] public string QueryPlan { get; set; }
    public string UsedMemory { get; set; }
    public string Status { get; set; }
    public string OpenTranCount { get; set; }
    public string PercentComplete { get; set; }
    public string HostName { get; set; }
    public string DatabaseName { get; set; }
    public string ProgramName { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime LoginTime { get; set; }
    public int RequestId { get; set; }
    public DateTime CollectionTime { get; set; }
}
// TODO: Either trim or convert to int the numeric strings so they report nicely. See example json.