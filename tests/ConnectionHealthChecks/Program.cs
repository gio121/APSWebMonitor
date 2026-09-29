using ApsMonitor.Services;

var clock = new ManualClock();
var health = new DeviceConnectionHealth(clock);
int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
}
byte[] Frame(byte node)
{
    byte[] frame = [0xAA, 9, 0, 1, node, 0x12, 0, 0, 0];
    frame[^1] = (byte)frame.Sum(b => (int)b);
    return frame;
}
Check(health.Snapshot() == (false, false), "Initially disconnected");
health.Observe(Frame(2));
Check(health.Snapshot() == (false, true), "Control response does not claim communications");
clock.Advance(2);
health.Observe(Frame(3));
Check(health.Snapshot() == (true, true), "Independent responses");
clock.Advance(1);
Check(health.Snapshot() == (true, false), "Control expires independently at 3 seconds");
clock.Advance(2);
Check(health.Snapshot() == (false, false), "Communications expires without new traffic");
health.Observe([0xAA]);
var bad = Frame(2); bad[^1] ^= 0x80; health.Observe(bad);
Check(health.Snapshot() == (false, false), "Invalid and truncated packets rejected");
var wrongDestination = Frame(2); wrongDestination[3] = 4; wrongDestination[^1] += 3;
health.Observe(wrongDestination);
health.Observe(Frame(1));
Check(health.Snapshot() == (false, false), "Other nodes and destinations ignored");
var wrongLength = Frame(2); wrongLength[1]++; wrongLength[^1]++;
health.Observe(wrongLength);
Check(health.Snapshot() == (false, false), "Invalid length ignored");
var compatibility = Frame(3); compatibility[^1] += 2; health.Observe(compatibility);
Check(health.Snapshot() == (true, false), "Existing checksum variant supported");
health.Observe(Frame(2));
health.Reset();
Check(health.Snapshot() == (false, false), "Disconnect or target change clears both states");
Console.WriteLine($"PASS: {checks} device connection checks.");

sealed class ManualClock : TimeProvider
{
    private long _timestamp;
    public override long TimestampFrequency => 1000;
    public override long GetTimestamp() => _timestamp;
    public void Advance(int seconds) => _timestamp += seconds * 1000;
}
