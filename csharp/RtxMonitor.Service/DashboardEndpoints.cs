using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RtxMonitor.Managed;

namespace RtxMonitor.Service;

/// <summary>Downloadable dashboard reports assembled only from published caches.</summary>
public static class DashboardEndpoints
{
    public static void Map(WebApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        application.MapGet(
            "/api/v1/snapshot",
            (HttpContext context, IMonitoringSnapshotSource monitoring,
                TelemetryEventHub hub, IWindowsTelemetrySnapshotSource windows,
                IExperimentalTelemetryPublicationSource profiles, ResearchTelemetryState research,
                IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions) =>
            {
                if (!context.Request.Query.TryGetValue("gpu_uuid", out var values) || values.Count != 1 ||
                    string.IsNullOrWhiteSpace(values[0]) || values[0]!.Length > 256)
                    return Results.Problem(statusCode: 400, title: "UUID inválido",
                        detail: "Informe exatamente um gpu_uuid com 1 a 256 caracteres.");

                string gpuUuid = values[0]!;
                MonitoringRuntimeSnapshot runtime = monitoring.GetSnapshot();
                GpuRuntimeSnapshot? gpu = runtime.Gpus.FirstOrDefault(item =>
                    string.Equals(item.Gpu.Uuid, gpuUuid, StringComparison.OrdinalIgnoreCase));
                if (gpu is null)
                    return Results.Problem(statusCode: 404, title: "GPU desconhecida",
                        detail: "A GPU selecionada não consta no inventário do serviço.");

                // These sources apply the same identity, publication and expiry
                // gates used by their existing GET endpoints. None acquires data.
                WindowsTelemetrySnapshot? windowsSnapshot = windows.GetSnapshot(gpu.Gpu.Uuid);
                ExperimentalTelemetryReport? profile = profiles.GetPublishedSnapshot(gpu.Gpu.Uuid);
                (JsonElement researchFull, JsonElement researchSummary) = research.GetPublication();
                (researchFull, researchSummary) = ForSelectedGpu(researchFull, researchSummary, gpu,
                    runtime.Discovery.State == "available");
                DateTimeOffset exportedAt = DateTimeOffset.UtcNow;
                var report = new
                {
                    schema = "rtx-monitor-dashboard-snapshot-v1",
                    exported_at = exportedAt,
                    cached_only = true,
                    simultaneous_acquisitions = false,
                    scope = "Cached service reports retain their own acquisition times and identities. " +
                        "Unavailable readings remain null. Unknown physical sites are not inferred. " +
                        "Research includes the complete raw publication; integer JSON tokens retain their full precision.",
                    gpu = ServiceEndpoints.BuildGpu(gpu),
                    health = ServiceEndpoints.BuildHealth(runtime, hub),
                    public_telemetry = gpu.PublicTelemetry is null ? null : ServiceEndpoints.BuildTelemetry(gpu),
                    windows_telemetry = windowsSnapshot is null ? null : ServiceEndpoints.BuildWindowsTelemetry(windowsSnapshot),
                    profile_telemetry = profile is null ? (JsonElement?)null : ExperimentalTelemetryJson.ToElement(profile),
                    research_telemetry = researchFull,
                    research_summary = researchSummary,
                    capabilities = gpu.Capabilities is null ? null : ServiceEndpoints.BuildCapabilities(gpu.Capabilities),
                    availability = new
                    {
                        public_telemetry = gpu.PublicTelemetry is null ? "not_captured" : "captured",
                        windows_telemetry = windowsSnapshot?.State ?? "not_captured",
                        profile_telemetry = profile is null ? "not_captured" : "captured",
                        research_telemetry = researchFull.TryGetProperty("state", out JsonElement state) && state.ValueKind == JsonValueKind.String ? state.GetString() : "unavailable",
                        capabilities = gpu.Capabilities is null ? "not_captured" : "captured",
                    },
                };
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report, jsonOptions.Value.SerializerOptions);
                string filename = "rtx3060-snapshot-" + exportedAt.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture) + ".json";
                context.Response.Headers.CacheControl = "no-store";
                return Results.File(bytes, "application/json", filename);
            })
            .WithName("dashboard-snapshot");
    }

    private static (JsonElement Full, JsonElement Summary) ForSelectedGpu(
        JsonElement full, JsonElement summary, GpuRuntimeSnapshot gpu, bool identityAvailable)
    {
        // Existing unavailable publications already contain no measurements.
        if (full.TryGetProperty("collection", out JsonElement collection) && collection.ValueKind == JsonValueKind.Null)
            return (full, summary);
        BoardIdentity? board = gpu.Capabilities?.Evidence.Board;
        bool complete = gpu.Present && identityAvailable && board is { HasPciIdentity: true, HasVbiosVersion: true } &&
            full.TryGetProperty("identity", out JsonElement identityProbe) && identityProbe.ValueKind == JsonValueKind.Object &&
            HasString(identityProbe, "driver_version", out _) && HasString(identityProbe, "vbios_version", out _) &&
            HasString(identityProbe, "pci_bus_id", out _) && HasString(identityProbe, "device_id", out _) &&
            HasString(identityProbe, "subsystem_id", out _);
        bool matches = false;
        if (complete && board is not null)
        {
            JsonElement identity = full.GetProperty("identity");
            matches = HasString(identity, "driver_version", out string driver) &&
                string.Equals(driver, gpu.Gpu.DriverVersion, StringComparison.Ordinal) &&
                HasString(identity, "vbios_version", out string vbios) &&
                string.Equals(vbios, board.VbiosVersion, StringComparison.OrdinalIgnoreCase) &&
                HasString(identity, "pci_bus_id", out string busId) &&
                TryPciAddress(busId, out var researchPci) && TryPciAddress(board.PciBusId, out var selectedPci) &&
                researchPci == selectedPci && selectedPci == (board.PciDomain, board.PciBus, board.PciDevice, board.PciFunction) &&
                board.PciVendorId <= ushort.MaxValue && board.PciDeviceId <= ushort.MaxValue &&
                board.PciSubsystemVendorId <= ushort.MaxValue && board.PciSubsystemDeviceId <= ushort.MaxValue &&
                HasString(identity, "device_id", out string device) && TryHex(device, out uint deviceId) &&
                deviceId == ((board.PciDeviceId << 16) | board.PciVendorId) &&
                HasString(identity, "subsystem_id", out string subsystem) && TryHex(subsystem, out uint subsystemId) &&
                subsystemId == ((board.PciSubsystemDeviceId << 16) | board.PciSubsystemVendorId);
            if (identity.TryGetProperty("gpu_uuid", out JsonElement uuid))
                matches &= uuid.ValueKind == JsonValueKind.String && string.Equals(uuid.GetString(), gpu.Gpu.Uuid, StringComparison.OrdinalIgnoreCase);
        }
        if (matches) return (full, summary);
        JsonElement unavailable = JsonSerializer.SerializeToElement(new
        {
            schema_version = 1,
            state = complete ? "identity_mismatch" : "identity_unavailable",
            observed_at = (DateTimeOffset?)null,
            updated_at = DateTimeOffset.UtcNow,
            identity = (object?)null,
            collection = (object?)null,
            settings_writes_requested = false,
            error = "A identidade do cache privado não corresponde à GPU selecionada ou não pôde ser confirmada.",
        });
        return (unavailable, unavailable);
    }

    private static bool HasString(JsonElement identity, string name, out string value)
    {
        value = string.Empty;
        if (!identity.TryGetProperty(name, out JsonElement property) || property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString() ?? string.Empty;
        return value.Length != 0;
    }

    private static bool TryHex(string text, out uint value) => uint.TryParse(
        text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text.AsSpan(2) : text.AsSpan(),
        NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);

    private static bool TryPciAddress(string text, out (uint Domain, uint Bus, uint Device, uint Function) address)
    {
        address = default;
        string[] parts = text.Split(':');
        if (parts.Length != 3) return false;
        string[] slot = parts[2].Split('.');
        if (slot.Length != 2 || !TryHex(parts[0], out uint domain) || !TryHex(parts[1], out uint bus) ||
            !TryHex(slot[0], out uint device) || !TryHex(slot[1], out uint function) || bus > 255 || device > 31 || function > 7)
            return false;
        address = (domain, bus, device, function);
        return true;
    }
}
