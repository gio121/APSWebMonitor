using System.Buffers.Binary;

namespace ApsMonitor.Services;

/// <summary>Live SEPSA responses only; session playback never updates this state.</summary>
public sealed class DeviceConnectionHealth(TimeProvider? clock = null)
{
    public static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(3);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private long? _control, _communications;

    public (bool Communications, bool Control) Snapshot()
    {
        lock (_gate)
        {
            long now = _clock.GetTimestamp();
            bool Recent(long? timestamp) => timestamp.HasValue && _clock.GetElapsedTime(timestamp.Value, now) < ResponseTimeout;
            return (Recent(_communications), Recent(_control));
        }
    }

    public void Reset()
    {
        lock (_gate) { _control = null; _communications = null; }
    }

    public void Observe(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 7 || frame[0] != 0xAA || frame[3] != 1 || frame[4] is not (2 or 3) ||
            BinaryPrimitives.ReadUInt16LittleEndian(frame[1..]) != frame.Length) return;
        int sum = 0;
        foreach (byte value in frame[..^1]) sum += value;
        // Support both standard SEPSA and the existing monitor client's +2 variant.
        if (frame[^1] != (byte)sum && frame[^1] != (byte)(sum + 2)) return;
        lock (_gate)
        {
            if (frame[4] == 2) _control = _clock.GetTimestamp();
            else _communications = _clock.GetTimestamp();
        }
    }
}
