using RtxMonitor.Managed;

namespace RtxMonitor.Service;

public sealed class WindowsTelemetryState : IWindowsTelemetrySnapshotSource
{
    private readonly object gate = new();
    private readonly Dictionary<string, WindowsTelemetrySnapshot> snapshots =
        new(StringComparer.OrdinalIgnoreCase);

    public WindowsTelemetrySnapshot? GetSnapshot(string gpuUuid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gpuUuid);
        lock (gate)
        {
            return snapshots.GetValueOrDefault(gpuUuid);
        }
    }

    internal void Record(WindowsTelemetrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (gate)
        {
            snapshots[snapshot.Gpu.Uuid] = snapshot;
        }
    }
}
