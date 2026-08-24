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
