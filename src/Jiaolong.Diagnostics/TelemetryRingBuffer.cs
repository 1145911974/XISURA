using Jiaolong.Contracts.Models;

namespace Jiaolong.Diagnostics;

public sealed class TelemetryRingBuffer
{
    private readonly int capacity;
    private readonly Queue<HardwareSnapshot> snapshots = new();
    private readonly object sync = new();

    public TelemetryRingBuffer(int capacity = 60)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
    }

    public void Add(HardwareSnapshot snapshot)
    {
        lock (sync)
        {
            snapshots.Enqueue(snapshot);
            while (snapshots.Count > capacity)
            {
                snapshots.Dequeue();
            }
        }
    }

    public IReadOnlyList<HardwareSnapshot> Snapshot()
    {
        lock (sync)
        {
            return snapshots.ToArray();
        }
    }
}
