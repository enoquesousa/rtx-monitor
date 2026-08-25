using System.Text.Json;

namespace RtxMonitor.Managed;

public static class TelemetryJson
{
    public const int SchemaVersion = 3;

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

        var payload = new
        {
            schema_version = SchemaVersion,
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
        };

        return JsonSerializer.Serialize(payload);
    }
}
