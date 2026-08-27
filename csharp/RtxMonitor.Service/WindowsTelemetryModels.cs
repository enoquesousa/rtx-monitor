namespace RtxMonitor.Service;

using RtxMonitor.Managed;

public interface IWindowsTelemetrySnapshotSource
{
    WindowsTelemetrySnapshot? GetSnapshot(string gpuUuid);
}

public interface IWindowsGpuReader
{
    WindowsTelemetrySnapshot Read(DiscoveredGpu gpu, CancellationToken cancellationToken);
}
