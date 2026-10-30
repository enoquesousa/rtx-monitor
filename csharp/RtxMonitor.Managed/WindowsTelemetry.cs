namespace RtxMonitor.Managed;

public sealed record WindowsAdapterIdentity(
    long Luid,
    string Description,
    uint VendorId,
    uint DeviceId,
    uint SubsystemVendorId,
    uint SubsystemDeviceId);

public sealed record WindowsTelemetryMetric(
    string State,
    double? Value,
    string Unit,
    string? Error = null);

public sealed record WindowsEngineTelemetry(
    string EngineType,
    WindowsTelemetryMetric Utilization);

public sealed record WindowsTelemetrySnapshot(
    int SchemaVersion,
    DateTimeOffset CapturedAt,
    string State,
    string? Error,
    GpuInfo Gpu,
    WindowsAdapterIdentity? Adapter,
    WindowsTelemetryMetric LocalMemory,
    WindowsTelemetryMetric NonLocalMemory,
    IReadOnlyList<WindowsEngineTelemetry> Engines);
