using System.Text;
using System.Text.Json;
using RtxMonitor.Managed;

namespace RtxMonitor.Storage;

public static class EvidenceJson
{
    public const int SchemaVersion = 1;

    public static string Serialize(StoredTelemetryEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        using JsonDocument eventDocument = JsonDocument.Parse(evidence.EventJson);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("evidence_schema_version", SchemaVersion);
            writer.WriteNumber("store_schema_version", evidence.StoreSchemaVersion);
            writer.WriteNumber("event_id", evidence.EventId);
            writer.WriteNumber("stored_at_unix_ms", evidence.StoredAt.ToUnixTimeMilliseconds());

            WriteRun(writer, evidence.Run);
            WriteDeviceSnapshot(writer, evidence.DeviceSnapshot);

            writer.WritePropertyName("event");
            eventDocument.RootElement.WriteTo(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteRun(Utf8JsonWriter writer, MonitoringRunEvidence run)
    {
        writer.WritePropertyName("run");
        writer.WriteStartObject();
        writer.WriteString("run_id", run.RunId);
        writer.WriteNumber("event_schema_version", run.EventSchemaVersion);
        writer.WriteNumber("started_at_unix_ms", run.StartedAt.ToUnixTimeMilliseconds());
        WriteNullableNumber(
            writer,
            "completed_at_unix_ms",
            run.CompletedAt?.ToUnixTimeMilliseconds());
        writer.WriteString("completion_reason", run.CompletionReason);
        writer.WriteString("target_gpu_uuid", run.TargetGpuUuid);
        writer.WriteNumber("interval_ms", run.IntervalMilliseconds);
        writer.WriteNumber("buffer_capacity", run.BufferCapacity);
        WriteNullableNumber(writer, "alert_threshold_c", run.AlertThresholdC);
        writer.WriteNumber("alert_hysteresis_c", run.AlertHysteresisC);
        writer.WriteNumber("retention_days", run.RetentionDays);
        writer.WriteString("application_version", run.ApplicationVersion);
        writer.WriteString("os_description", run.OsDescription);
        writer.WriteString("os_architecture", run.OsArchitecture);
        writer.WriteString("process_architecture", run.ProcessArchitecture);
        writer.WriteEndObject();
    }

    private static void WriteDeviceSnapshot(
        Utf8JsonWriter writer,
        StoredGpuEvidenceSnapshot? snapshot)
    {
        writer.WritePropertyName("device_snapshot");
        if (snapshot is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteNumber("snapshot_id", snapshot.SnapshotId);
        writer.WriteNumber("observed_at_unix_ms", snapshot.ObservedAt.ToUnixTimeMilliseconds());
        writer.WriteString("board_capture_state", BoardStateName(snapshot.BoardState));
        writer.WriteString("board_capture_error", snapshot.BoardError);

        writer.WritePropertyName("gpu");
        writer.WriteStartObject();
        writer.WriteNumber("index", snapshot.Gpu.Index);
        writer.WriteString("name", snapshot.Gpu.Name);
        writer.WriteString("uuid", snapshot.Gpu.Uuid);
        writer.WriteString("driver_version", snapshot.Gpu.DriverVersion);
        writer.WriteString("nvml_version", snapshot.Gpu.NvmlVersion);
        writer.WriteEndObject();

        writer.WritePropertyName("board");
        if (snapshot.Board is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            BoardIdentity board = snapshot.Board;
            writer.WriteStartObject();
            writer.WriteNumber("flags", (uint)board.Flags);
            writer.WriteBoolean("pci_identity_available", board.HasPciIdentity);
            writer.WriteString("pci_bus_id", board.PciBusId);
            writer.WriteNumber("pci_vendor_id", board.PciVendorId);
            writer.WriteNumber("pci_device_id", board.PciDeviceId);
            writer.WriteNumber("pci_subsystem_vendor_id", board.PciSubsystemVendorId);
            writer.WriteNumber("pci_subsystem_device_id", board.PciSubsystemDeviceId);
            writer.WriteNumber("pci_domain", board.PciDomain);
            writer.WriteNumber("pci_bus", board.PciBus);
            writer.WriteNumber("pci_device", board.PciDevice);
            writer.WriteNumber("pci_function", board.PciFunction);
            writer.WriteBoolean("vbios_available", board.HasVbiosVersion);
            writer.WriteString(
                "vbios_version",
                board.HasVbiosVersion ? board.VbiosVersion : null);
            writer.WriteString("profile_key", snapshot.ProfileKey);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static string BoardStateName(BoardEvidenceState state) => state switch
    {
        BoardEvidenceState.NotAttempted => "not_attempted",
        BoardEvidenceState.Available => "available",
        BoardEvidenceState.QueryFailed => "query_failed",
        _ => "unknown",
    };

    private static void WriteNullableNumber(
        Utf8JsonWriter writer,
        string propertyName,
        long? value)
    {
        if (value is long number)
        {
            writer.WriteNumber(propertyName, number);
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }
}
