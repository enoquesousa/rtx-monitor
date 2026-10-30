using System.Diagnostics;
using System.Text.Json;
using RtxMonitor.Managed;

namespace RtxMonitor.ConsoleApp;

internal static class PrivateSampleJson
{
    // Version 2 transports the acquired driver integers and the bounded native interval.
    // The legacy CLI projection remains version 1 with its original rounded values.
    internal static JsonElement ThermalRaw(GpuInfo gpu, PrivateThermalRawSample sample) => JsonSerializer.SerializeToElement(new
    {
        schema_version = 2,
        source_kind = PrivateThermalSample.SourceKind,
        gpu_index = sample.GpuIndex,
        gpu_uuid = gpu.Uuid,
        captured_at_utc = sample.CapturedAt.ToString("O"),
        captured_at_unix_ms = sample.TimestampUnixMilliseconds,
        monotonic_ns = MonotonicNanoseconds(),
        monotonic_frequency_hz = Stopwatch.Frequency,
        gpu_die_temperature_c = sample.GpuDieTemperatureC,
        gpu_hotspot_temperature_c = sample.GpuHotspotTemperatureC,
        delta_c = sample.DeltaC,
        gpu_die_temperature_fixed8 = sample.GpuDieTemperatureFixed8,
        gpu_hotspot_temperature_fixed8 = sample.GpuHotspotTemperatureFixed8,
        gpu_die_temperature_millic = sample.GpuDieTemperatureMillic,
        gpu_hotspot_temperature_millic = sample.GpuHotspotTemperatureMillic,
        acquisition_started_at_utc = sample.AcquisitionStartedAt.ToString("O"),
        acquisition_started_unix_ms = sample.AcquisitionStartedUnixMilliseconds,
        acquisition_started_monotonic_ms = sample.AcquisitionStartedMonotonicMilliseconds,
        acquisition_finished_monotonic_ms = sample.AcquisitionFinishedMonotonicMilliseconds,
        native_status = sample.NativeStatus,
        profile_evidence_stage = PrivateThermalSample.ProfileEvidenceStage,
        profile_name = PrivateThermalSample.Profile,
        interface_id = PrivateThermalSample.InterfaceId,
        structure_version = PrivateThermalSample.StructureVersion,
        nvapi_module_sha256 = PrivateThermalSample.NvapiModuleSha256,
        function_rva = PrivateThermalSample.FunctionRva,
    });

    internal static JsonElement Thermal(GpuInfo gpu, PrivateThermalSample sample) => JsonSerializer.SerializeToElement(new
    {
        schema_version = 1,
        source_kind = PrivateThermalSample.SourceKind,
        gpu_index = sample.GpuIndex,
        gpu_uuid = gpu.Uuid,
        captured_at_utc = sample.CapturedAt.ToString("O"),
        captured_at_unix_ms = sample.TimestampUnixMilliseconds,
        monotonic_ns = MonotonicNanoseconds(),
        monotonic_frequency_hz = Stopwatch.Frequency,
        gpu_die_temperature_c = sample.GpuDieTemperatureC,
        gpu_hotspot_temperature_c = sample.GpuHotspotTemperatureC,
        delta_c = Math.Round(sample.DeltaC, 3),
        native_status = sample.NativeStatus,
        profile_evidence_stage = PrivateThermalSample.ProfileEvidenceStage,
        profile_name = PrivateThermalSample.Profile,
        interface_id = PrivateThermalSample.InterfaceId,
        structure_version = PrivateThermalSample.StructureVersion,
        nvapi_module_sha256 = PrivateThermalSample.NvapiModuleSha256,
        function_rva = PrivateThermalSample.FunctionRva,
    });

    internal static JsonElement Voltage(GpuInfo gpu, PrivateVoltageSample sample) => JsonSerializer.SerializeToElement(new
    {
        schema_version = 1,
        source_kind = PrivateVoltageSample.SourceKind,
        gpu_index = sample.GpuIndex,
        gpu_uuid = gpu.Uuid,
        captured_at_utc = sample.CapturedAt.ToString("O"),
        captured_at_unix_ms = sample.TimestampUnixMilliseconds,
        monotonic_ns = MonotonicNanoseconds(),
        monotonic_frequency_hz = Stopwatch.Frequency,
        gpu_core_voltage_microvolts = sample.GpuCoreVoltageMicrovolts,
        gpu_core_voltage_v = sample.GpuCoreVoltageV,
        native_status = sample.NativeStatus,
        profile_evidence_stage = PrivateVoltageSample.ProfileEvidenceStage,
        profile_name = PrivateVoltageSample.Profile,
        interface_id = PrivateVoltageSample.InterfaceId,
        structure_version = PrivateVoltageSample.StructureVersion,
        nvapi_module_sha256 = PrivateVoltageSample.NvapiModuleSha256,
        function_rva = PrivateVoltageSample.FunctionRva,
    });

    private static long MonotonicNanoseconds() => checked(
        (long)(((Int128)Stopwatch.GetTimestamp() * 1_000_000_000L) / Stopwatch.Frequency));
}
