using System.Text.Json;

namespace RtxMonitor.Managed;

public static class TelemetryJson
{
    public const int SchemaVersion = 2;

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
        };

        return JsonSerializer.Serialize(payload);
    }
}
