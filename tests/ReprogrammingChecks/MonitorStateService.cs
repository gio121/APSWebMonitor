namespace ApsMonitor.Services;

// Isolate network/protocol checks from the application's DB and Influx services.
public sealed class MonitorStateService
{
    private int _busy;
    public bool TryBeginReprogramming() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0;
    public void EndReprogramming() => Interlocked.Exchange(ref _busy, 0);
    public Task WaitForMonitoringStoppedAsync() => Task.CompletedTask;
}
