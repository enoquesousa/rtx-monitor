using System.Text.Json;

namespace RtxMonitor.Service;

public sealed record ServiceHealthResponse(
    int SchemaVersion,
    string Status,
    bool Ready,
    string ServiceVersion,
    long StartedAtUnixMs,
    long UptimeMs,
    StorageHealthResponse Storage,
    DiscoveryHealthResponse Discovery,
    CollectorSummaryResponse Collectors,
    SseSummaryResponse Sse);

public sealed record StorageHealthResponse(
    string State,
    string DatabasePath,
    int? SchemaVersion,
    long ChangedAtUnixMs,
    string? LastError);

public sealed record DiscoveryHealthResponse(
    string State,
    long? LastAttemptAtUnixMs,
    long? LastSuccessAtUnixMs,
    string? LastError);

public sealed record CollectorSummaryResponse(int Active, int KnownGpus);

public sealed record SseSummaryResponse(
    int ConnectedClients,
    int MaximumClients,
    int QueueCapacity);

public sealed record GpuListResponse(
    int SchemaVersion,
    string DiscoveryState,
    int Count,
    IReadOnlyList<GpuRuntimeResponse> Gpus);

public sealed record GpuRuntimeResponse(
    uint Index,
    string Name,
    string Uuid,
    string DriverVersion,
    string NvmlVersion,
    bool Present,
    string CollectorState,
    string? RunId,
    string? ProfileKey,
    string BoardCaptureState,
    string? BoardCaptureError,
    string? LastEventType,
    long? LastEventAtUnixMs,
    int? LastSampleTemperatureC,
    long? LastSampleAtUnixMs,
    uint ConsecutiveFailures,
    string? LastError);

public sealed record CapabilitiesResponse(
    int SchemaVersion,
    long CapturedAtUnixMs,
    GpuIdentityResponse Gpu,
    BoardResponse? Board,
    string BoardCaptureState,
    string? BoardCaptureError,
    string? ThermalScanError,
    IReadOnlyList<ThermalProviderResponse> Providers,
    IReadOnlyList<ThermalCapabilityResponse> ThermalCapabilities);

public sealed record GpuIdentityResponse(
    uint Index,
    string Name,
    string Uuid,
    string DriverVersion,
    string NvmlVersion);

public sealed record BoardResponse(
    uint Flags,
    bool PciIdentityAvailable,
    string PciBusId,
    uint PciVendorId,
    uint PciDeviceId,
    uint PciSubsystemVendorId,
    uint PciSubsystemDeviceId,
    uint PciDomain,
    uint PciBus,
    uint PciDevice,
    uint PciFunction,
    bool VbiosAvailable,
    string? VbiosVersion,
    string? ProfileKey);

public sealed record ThermalProviderResponse(
    string Provider,
    string State,
    int NativeStatus,
    uint CapabilityCount);

public sealed record ThermalCapabilityResponse(
    string Provider,
    uint ProviderNativeId,
    string Target,
    string Controller,
    string State,
    string Confidence,
    int? CurrentTemperatureC,
    int? DefaultMinimumTemperatureC,
    int? DefaultMaximumTemperatureC,
    int NativeStatus);

public sealed record PublicTelemetryResponse(
    int SchemaVersion,
    long CapturedAtUnixMs,
    GpuIdentityResponse Gpu,
    BoardResponse? Board,
    string BoardCaptureState,
    string? BoardCaptureError,
    string CollectorState,
    PublicTelemetryCoverageResponse Coverage,
    PerformanceLimitReasonsResponse? PerformanceLimitReasons,
    IReadOnlyList<PublicTelemetryFieldResponse> Fields,
    ComputedMetricsResponse? ComputedMetrics);

public sealed record PublicTelemetryCoverageResponse(
    int Total,
    int Available,
    int NotSupported,
    int ProviderUnavailable,
    int QueryFailed);

public sealed record PerformanceLimitReasonsResponse(
    ulong RawBitmask,
    IReadOnlyList<string> ActiveReasons,
    string PrimaryReason);

public sealed record PublicTelemetryFieldResponse(
    string Field,
    string Provider,
    uint ProviderNativeId,
    string State,
    string Origin,
    string ValueType,
    string Unit,
    ulong? ValueU64,
    long? ValueI64,
    double? ValueF64,
    int NativeStatus,
    long TimestampUnixMs);

public sealed record ComputedMetricsResponse(
    long TimestampUnixMs,
    IReadOnlyList<ComputedMetricResponse> Metrics);

public sealed record ComputedMetricResponse(
    string Metric,
    string State,
    string Origin,
    string Unit,
    string Formula,
    double? Value,
    long WindowMs,
    uint SampleCount,
    int? TemperatureThresholdC,
    IReadOnlyList<string> Inputs);

public sealed record WindowsTelemetryResponse(
    int SchemaVersion,
    long CapturedAtUnixMs,
    string State,
    string? Error,
    GpuIdentityResponse Gpu,
    WindowsAdapterIdentityResponse? Adapter,
    WindowsMetricResponse LocalMemory,
    WindowsMetricResponse NonLocalMemory,
    IReadOnlyList<WindowsEngineResponse> Engines);

public sealed record WindowsAdapterIdentityResponse(
    string Luid,
    string Description,
    uint VendorId,
    uint DeviceId,
    uint SubsystemVendorId,
    uint SubsystemDeviceId);

public sealed record WindowsMetricResponse(string State, double? Value, string Unit, string? Error);

public sealed record WindowsEngineResponse(string EngineType, WindowsMetricResponse Utilization);

public sealed record HistoryResponse(
    int SchemaVersion,
    int Count,
    int Limit,
    string Order,
    long? LastEventId,
    IReadOnlyList<JsonElement> Items);

public sealed record StreamGapResponse(
    int SchemaVersion,
    long DroppedEvents,
    long? AfterEventId,
    long LatestDroppedEventId,
    string RecoveryEndpoint);
