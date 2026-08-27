namespace RtxMonitor.Managed;

public sealed record GpuInfo(
    uint Index,
    string Name,
    string Uuid,
    string DriverVersion,
    string NvmlVersion);

public enum TemperatureBackend : uint
{
    NvmlTemperatureV1 = 1,
    NvmlTemperatureLegacy = 2,
}

public readonly record struct TemperatureSample(
    uint GpuIndex,
    int TemperatureC,
    TemperatureBackend Backend,
    string BackendName,
    DateTimeOffset CapturedAt,
    ulong TimestampUnixMilliseconds);

[Flags]
public enum BoardIdentityFlags : uint
{
    None = 0,
    PciValid = 1U << 0,
    VbiosValid = 1U << 1,
}

public sealed record BoardIdentity(
    uint GpuIndex,
    uint PciVendorId,
    uint PciDeviceId,
    uint PciSubsystemVendorId,
    uint PciSubsystemDeviceId,
    uint PciDomain,
    uint PciBus,
    uint PciDevice,
    uint PciFunction,
    BoardIdentityFlags Flags,
    string PciBusId,
    string VbiosVersion)
{
    public bool HasPciIdentity => (Flags & BoardIdentityFlags.PciValid) != 0;

    public bool HasVbiosVersion => (Flags & BoardIdentityFlags.VbiosValid) != 0;
}

public enum ThermalProvider : uint
{
    NvmlThermalSettings = 1,
    NvmlFieldValues = 2,
    NvapiThermalSettings = 3,
}

public enum CapabilityState : uint
{
    Unknown = 0,
    Available = 1,
    NotSupported = 2,
    ProviderUnavailable = 3,
    QueryFailed = 4,
}

public enum ThermalTarget : uint
{
    None = 0,
    Gpu = 1,
    Memory = 2,
    PowerSupply = 4,
    Board = 8,
    VcdBoard = 9,
    VcdInlet = 10,
    VcdOutlet = 11,
    Unknown = 255,
}

public enum ThermalController : uint
{
    None = 0,
    GpuInternal = 1,
    Adm1032 = 2,
    Adt7461 = 3,
    Max6649 = 4,
    Max1617 = 5,
    Lm99 = 6,
    Lm89 = 7,
    Lm64 = 8,
    G781 = 9,
    Adt7473 = 10,
    SbMax6649 = 11,
    VbiosEvent = 12,
    OperatingSystem = 13,
    NvSysconCanoas = 14,
    NvSysconE551 = 15,
    Max6649R = 16,
    Adt7473S = 17,
    Unknown = 255,
}

public enum SensorConfidence : uint
{
    Unknown = 0,
    DriverReported = 1,
    Experimental = 2,
}

public sealed record ThermalProviderResult(
    ThermalProvider Provider,
    string ProviderName,
    CapabilityState State,
    string StateName,
    int NativeStatus,
    uint CapabilityCount);

public sealed record ThermalCapability(
    ThermalProvider Provider,
    string ProviderName,
    ThermalTarget Target,
    string TargetName,
    ThermalController Controller,
    string ControllerName,
    CapabilityState State,
    string StateName,
    SensorConfidence Confidence,
    string ConfidenceName,
    int? CurrentTemperatureC,
    int? DefaultMinimumTemperatureC,
    int? DefaultMaximumTemperatureC,
    int NativeStatus,
    uint ProviderNativeId);

public sealed record ThermalReport(
    uint GpuIndex,
    DateTimeOffset CapturedAt,
    ulong TimestampUnixMilliseconds,
    IReadOnlyList<ThermalProviderResult> Providers,
    IReadOnlyList<ThermalCapability> Capabilities);

public sealed record PrivateThermalSample(
    uint GpuIndex,
    double GpuDieTemperatureC,
    double GpuHotspotTemperatureC,
    double DeltaC,
    int NativeStatus,
    DateTimeOffset CapturedAt,
    ulong TimestampUnixMilliseconds)
{
    public const string Source = "nvapi_thermal_channel";
}

public enum DataOrigin : uint
{
    Unknown = 0,
    DriverReported = 1,
    Computed = 2,
    Experimental = 3,
}

public enum PublicTelemetryField : uint
{
    GpuDieTemperatureC = 1,
    MemoryTemperatureC = 2,
    TotalEnergyMj = 3,
    PowerAverageMw = 4,
    PowerInstantMw = 5,
    PowerLimitMinMw = 6,
    PowerLimitMaxMw = 7,
    PowerLimitDefaultMw = 8,
    PowerLimitCurrentMw = 9,
    PowerLimitRequestedMw = 10,
    TemperatureShutdownC = 11,
    TemperatureSlowdownC = 12,
    TemperatureMemoryMaxC = 13,
    TemperatureGpuMaxC = 14,
    ClockGraphicsMhz = 15,
    ClockSmMhz = 16,
    ClockMemoryMhz = 17,
    ClockVideoMhz = 18,
    UtilizationGpuPercent = 19,
    UtilizationMemoryPercent = 20,
    MemoryTotalBytes = 21,
    MemoryFreeBytes = 22,
    MemoryUsedBytes = 23,
    FanSpeedPercent = 24,
    PerformanceState = 25,
    ClockEventReasonsCurrent = 26,
    ClockEventReasonsSupported = 27,
    EncoderUtilizationPercent = 28,
    EncoderSamplingPeriodUs = 29,
    DecoderUtilizationPercent = 30,
    DecoderSamplingPeriodUs = 31,
    PowerConsumptionDefaultLimitPercent = 32,
    PowerConsumptionCurrentLimitPercent = 33,
    TemperatureGpuLimitC = 34,
}

public enum PublicTelemetryProvider : uint
{
    NvmlTemperatureV1 = 1,
    NvmlTemperatureLegacy = 2,
    NvmlFieldValues = 3,
    NvmlClockInfo = 4,
    NvmlUtilizationRates = 5,
    NvmlMemoryInfo = 6,
    NvmlFanSpeedV2 = 7,
    NvmlFanSpeedLegacy = 8,
    NvmlPerformanceState = 9,
    NvmlClockEventReasons = 10,
    NvmlClockThrottleReasonsLegacy = 11,
    NvmlEncoderUtilization = 12,
    NvmlDecoderUtilization = 13,
    NvmlSupportedClockEventReasons = 14,
    NvmlSupportedClockThrottleReasonsLegacy = 15,
    ComputedPowerRatio = 16,
    NvmlTemperatureThreshold = 17,
}

public enum TelemetryValueType : uint
{
    Unknown = 0,
    UnsignedInteger = 1,
    SignedInteger = 2,
    Double = 3,
    Bitmask = 4,
}

public enum TelemetryUnit : uint
{
    Unknown = 0,
    Celsius = 1,
    Milliwatt = 2,
    Millijoule = 3,
    Megahertz = 4,
    Percent = 5,
    Bytes = 6,
    PState = 7,
    Bitmask = 8,
    Microseconds = 9,
    CelsiusPerSecond = 10,
    Seconds = 11,
}

public sealed record PublicTelemetryValue(
    PublicTelemetryField Field,
    string FieldName,
    PublicTelemetryProvider Provider,
    string ProviderName,
    CapabilityState State,
    string StateName,
    DataOrigin Origin,
    string OriginName,
    TelemetryValueType ValueType,
    string ValueTypeName,
    TelemetryUnit Unit,
    string UnitName,
    int NativeStatus,
    uint ProviderNativeId,
    ulong? UnsignedValue,
    long? SignedValue,
    double? DoubleValue,
    ulong TimestampUnixMilliseconds)
{
    public double? NumericValue => ValueType switch
    {
        TelemetryValueType.UnsignedInteger or TelemetryValueType.Bitmask => UnsignedValue,
        TelemetryValueType.SignedInteger => SignedValue,
        TelemetryValueType.Double => DoubleValue,
        _ => null,
    };
}

public sealed record PublicTelemetryCoverage(
    int Total,
    int Available,
    int NotSupported,
    int ProviderUnavailable,
    int QueryFailed);

public sealed record PublicTelemetryReport(
    uint GpuIndex,
    DateTimeOffset CapturedAt,
    ulong TimestampUnixMilliseconds,
    IReadOnlyList<PublicTelemetryValue> Fields)
{
    public PublicTelemetryCoverage Coverage => new(
        Fields.Count,
        Fields.Count(field => field.State == CapabilityState.Available),
        Fields.Count(field => field.State == CapabilityState.NotSupported),
        Fields.Count(field => field.State == CapabilityState.ProviderUnavailable),
        Fields.Count(field => field.State == CapabilityState.QueryFailed));

    public PublicTelemetryValue? Find(PublicTelemetryField field) =>
        Fields.FirstOrDefault(candidate => candidate.Field == field);
}

public enum ComputedMetricKind : uint
{
    GpuTemperatureWindowAverage = 1,
    GpuTemperatureSlope = 2,
    GpuTemperatureTimeAboveThreshold = 3,
    GpuMemoryTemperatureDelta = 4,
}

public enum ComputedMetricState : uint
{
    Unknown = 0,
    Available = 1,
    InsufficientData = 2,
    InputUnavailable = 3,
}

public sealed record ComputedMetricOptions(
    uint WindowMilliseconds = 5000,
    int TemperatureThresholdC = 80,
    uint MaximumSamples = 1024);

public sealed record ComputedMetric(
    ComputedMetricKind Kind,
    string KindName,
    ComputedMetricState State,
    string StateName,
    DataOrigin Origin,
    string OriginName,
    TelemetryUnit Unit,
    string UnitName,
    string Formula,
    double? Value,
    ulong TimestampUnixMilliseconds,
    ulong WindowMilliseconds,
    uint SampleCount,
    int? TemperatureThresholdC,
    IReadOnlyList<PublicTelemetryField> Inputs,
    IReadOnlyList<string> InputNames);

public sealed record ComputedMetricsReport(
    uint GpuIndex,
    ulong TimestampUnixMilliseconds,
    IReadOnlyList<ComputedMetric> Metrics);
