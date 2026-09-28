namespace ApsMonitor.Services;

public sealed class InfluxOptions
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "http://localhost:8086";
    public string Organization { get; set; } = "";
    public string Bucket { get; set; } = "";
    public string Token { get; set; } = "";
    public int FlushIntervalSeconds { get; set; } = 1;
    public int BatchSize { get; set; } = 5000;
    public int QueueCapacity { get; set; } = 100000;
}
