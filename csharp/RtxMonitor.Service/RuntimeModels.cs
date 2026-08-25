using RtxMonitor.Managed;
using RtxMonitor.Storage;

namespace RtxMonitor.Service;

public sealed record DiscoveredGpu(
    GpuInfo Gpu,
    GpuEvidenceSnapshot Evidence,
    ThermalReport? ThermalReport,
    string? ThermalError,
    DateTimeOffset CapturedAt);

public sealed record GpuRuntimeSnapshot(
    GpuInfo Gpu,
    bool Present,
    string CollectorState,
    string? RunId,
    string? ProfileKey,
    string BoardCaptureState,
    string? BoardCaptureError,
    string? LastEventType,
    DateTimeOffset? LastEventAt,
    int? TemperatureC,
    DateTimeOffset? TemperatureCapturedAt,
    uint ConsecutiveFailures,
    string? LastError,
    DiscoveredGpu? Capabilities,
    PublicTelemetryReport? PublicTelemetry = null,
    ComputedMetricsReport? ComputedMetrics = null);

public sealed record StorageRuntimeSnapshot(
    string State,
    string DatabasePath,
    int? SchemaVersion,
    DateTimeOffset ChangedAt,
    string? LastError);

public sealed record DiscoveryRuntimeSnapshot(
    string State,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessAt,
    string? LastError);

public sealed record MonitoringRuntimeSnapshot(
    DateTimeOffset StartedAt,
    string Status,
    bool Ready,
    StorageRuntimeSnapshot Storage,
    DiscoveryRuntimeSnapshot Discovery,
    IReadOnlyList<GpuRuntimeSnapshot> Gpus)
{
    public int ActiveCollectors => Gpus.Count(
        gpu => gpu.CollectorState is "starting" or "running" or "degraded");
}

public interface IMonitoringSnapshotSource
{
    MonitoringRuntimeSnapshot GetSnapshot();
}

public interface IHistorySource
{
    IReadOnlyList<StoredTelemetryEvidence> Query(TelemetryEventQuery query);
}

public sealed class ServiceDependencyUnavailableException : Exception
{
    public ServiceDependencyUnavailableException(string message)
        : base(message)
    {
    }

    public ServiceDependencyUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
