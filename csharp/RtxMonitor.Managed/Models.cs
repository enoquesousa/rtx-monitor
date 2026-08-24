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
