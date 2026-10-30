using System.Text;
using System.Text.Json;

namespace RtxMonitor.Managed;

public static class TelemetryJson
{
    public const int SchemaVersion = 5;
    public const int ExperimentalSchemaVersion = 8;

    public static int GetSchemaVersion(TelemetryEvent telemetryEvent)
    {
        ArgumentNullException.ThrowIfNull(telemetryEvent);

        if (telemetryEvent.ExperimentalTelemetry is not ExperimentalTelemetryReport report)
        {
            return SchemaVersion;
        }
        if (telemetryEvent.Kind is not (TelemetryEventKind.Sample or TelemetryEventKind.Gap))
        {
            throw new ArgumentException(
                "A telemetria experimental só pode acompanhar eventos sample ou gap.",
                nameof(telemetryEvent));
        }
        if (!string.Equals(report.GpuUuid, telemetryEvent.TargetGpuUuid, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "O relatório experimental não corresponde ao UUID alvo do evento.",
                nameof(telemetryEvent));
        }
        if (report.Fields.Any(field => field.State == ExperimentalTelemetryState.Available) &&
            telemetryEvent.Gpu is GpuInfo gpu &&
            (!string.Equals(report.GpuUuid, gpu.Uuid, StringComparison.OrdinalIgnoreCase) ||
                report.GpuIndex != gpu.Index ||
                !string.Equals(report.Identity.DriverVersion, gpu.DriverVersion, StringComparison.Ordinal) ||
                !string.Equals(report.Identity.NvmlVersion, gpu.NvmlVersion, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "A leitura experimental disponível não corresponde à identidade atual da GPU do evento.",
                nameof(telemetryEvent));
        }
        return ExperimentalSchemaVersion;
    }

    public static string Serialize(TelemetryEvent telemetryEvent)
    {
        ArgumentNullException.ThrowIfNull(telemetryEvent);

        object? sample = telemetryEvent.Sample is TemperatureSample current
            ? new
            {
                temperature_c = current.TemperatureC,
                sensor = "gpu_die",
                backend = current.BackendName,
                timestamp_unix_ms = current.TimestampUnixMilliseconds,
            }
            : null;

        object? publicTelemetry = telemetryEvent.PublicTelemetry is PublicTelemetryReport report
            ? new
            {
                gpu_index = report.GpuIndex,
                captured_at_unix_ms = report.TimestampUnixMilliseconds,
                coverage = new
                {
                    total = report.Coverage.Total,
                    available = report.Coverage.Available,
                    not_supported = report.Coverage.NotSupported,
                    provider_unavailable = report.Coverage.ProviderUnavailable,
                    query_failed = report.Coverage.QueryFailed,
                },
                performance_limit_reasons = PerformanceLimitReasons.From(report) is { } reasons
                    ? new
                    {
                        raw_bitmask = reasons.RawBitmask,
                        active_reasons = reasons.ActiveReasons,
                        primary_reason = reasons.PrimaryReason,
                    }
                    : null,
                fields = report.Fields.Select(field => new
                {
                    field = field.FieldName,
                    provider = field.ProviderName,
                    provider_native_id = field.ProviderNativeId,
                    state = field.StateName,
                    origin = field.OriginName,
                    value_type = field.ValueTypeName,
                    unit = field.UnitName,
                    value_u64 = field.UnsignedValue,
                    value_i64 = field.SignedValue,
                    value_f64 = field.DoubleValue,
                    native_status = field.NativeStatus,
                    timestamp_unix_ms = field.TimestampUnixMilliseconds,
                }),
            }
            : null;

        object? computedMetrics = telemetryEvent.ComputedMetrics is ComputedMetricsReport computed
            ? new
            {
                gpu_index = computed.GpuIndex,
                timestamp_unix_ms = computed.TimestampUnixMilliseconds,
                metrics = computed.Metrics.Select(metric => new
                {
                    metric = metric.KindName,
                    state = metric.StateName,
                    origin = metric.OriginName,
                    unit = metric.UnitName,
                    formula = metric.Formula,
                    value = metric.Value,
                    window_ms = metric.WindowMilliseconds,
                    sample_count = metric.SampleCount,
                    temperature_threshold_c = metric.TemperatureThresholdC,
                    inputs = metric.InputNames,
                }),
            }
            : null;

        object? windowsTelemetry = telemetryEvent.WindowsTelemetry is WindowsTelemetrySnapshot windows
            ? new
            {
                schema_version = windows.SchemaVersion,
                captured_at_unix_ms = windows.CapturedAt.ToUnixTimeMilliseconds(),
                state = windows.State,
                error = windows.Error,
                gpu = new
                {
                    index = windows.Gpu.Index,
                    name = windows.Gpu.Name,
                    uuid = windows.Gpu.Uuid,
                    driver_version = windows.Gpu.DriverVersion,
                    nvml_version = windows.Gpu.NvmlVersion,
                },
                adapter = windows.Adapter is null ? null : new
                {
                    luid = $"0x{unchecked((ulong)windows.Adapter.Luid):x16}",
                    description = windows.Adapter.Description,
                    vendor_id = windows.Adapter.VendorId,
                    device_id = windows.Adapter.DeviceId,
                    subsystem_vendor_id = windows.Adapter.SubsystemVendorId,
                    subsystem_device_id = windows.Adapter.SubsystemDeviceId,
                },
                local_memory = WindowsMetric(windows.LocalMemory),
                non_local_memory = WindowsMetric(windows.NonLocalMemory),
                engines = windows.Engines.Select(engine => new
                {
                    engine_type = engine.EngineType,
                    utilization = WindowsMetric(engine.Utilization),
                }),
            }
            : null;

        var payload = new
        {
            schema_version = GetSchemaVersion(telemetryEvent),
            event_type = telemetryEvent.KindName,
            sequence = telemetryEvent.Sequence,
            target_gpu_uuid = telemetryEvent.TargetGpuUuid,
            gpu_index = telemetryEvent.Gpu?.Index,
            gpu_name = telemetryEvent.Gpu?.Name,
            observed_at_unix_ms = telemetryEvent.ObservedAtUnixMilliseconds,
            status = telemetryEvent.StatusName,
            status_code = (int)telemetryEvent.Status,
            message = telemetryEvent.Message,
            consecutive_failures = telemetryEvent.ConsecutiveFailures,
            retry_after_ms = telemetryEvent.RetryAfterMilliseconds,
            sample,
            alert_threshold_c = telemetryEvent.AlertThresholdC,
            alert_hysteresis_c = telemetryEvent.AlertHysteresisC,
            public_telemetry = publicTelemetry,
            computed_metrics = computedMetrics,
            windows_telemetry = windowsTelemetry,
        };

        if (telemetryEvent.ExperimentalTelemetry is null)
        {
            return JsonSerializer.Serialize(payload);
        }

        // Embed the same contract used by the experimental endpoint so its raw
        // values and provenance survive events, SQLite, history, and export.
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in JsonSerializer.SerializeToElement(payload).EnumerateObject())
            {
                property.WriteTo(writer);
            }
            writer.WritePropertyName("profile_telemetry");
            ExperimentalTelemetryJson.ToElement(telemetryEvent.ExperimentalTelemetry).WriteTo(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static object WindowsMetric(WindowsTelemetryMetric metric) => new
    {
        state = metric.State,
        value = metric.Value,
        unit = metric.Unit,
        error = metric.Error,
    };
}
