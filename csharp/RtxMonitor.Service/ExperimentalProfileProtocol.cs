using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using RtxMonitor.ConsoleApp;
using RtxMonitor.Managed;

namespace RtxMonitor.Service;

internal static class ExperimentalProfileProtocol
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    internal static string Serialize(ExperimentalProfileObservation observation) => JsonSerializer.Serialize(
        new { protocol_version = 1, kind = "profile", observation = JsonSerializer.SerializeToElement(observation, Options) });

    internal static string SerializeError(Exception error) => JsonSerializer.Serialize(new
    {
        protocol_version = 1,
        kind = "error",
        status = error switch
        {
            RtxMonitorException native => native.Status.ToString(),
            DllNotFoundException or EntryPointNotFoundException or BadImageFormatException => "profile_module_unavailable",
            _ => "profile_query_failed",
        },
    });

    internal static ExperimentalProfileObservation Parse(string payload, string expectedUuid)
    {
        using JsonDocument document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 12 });
        JsonElement root = document.RootElement;
        RequireUniqueProperties(root);
        Require(root.ValueKind == JsonValueKind.Object && root.GetProperty("protocol_version").GetInt32() == 1);
        string? kind = root.GetProperty("kind").GetString();
        if (kind == "error")
        {
            RequireProperties(root, "protocol_version", "kind", "status");
            string status = root.GetProperty("status").GetString() ?? throw new InvalidDataException("Status ausente.");
            if (Enum.TryParse(status, out MonitoringStatus nativeStatus) && Enum.IsDefined(nativeStatus) && nativeStatus != MonitoringStatus.Ok)
                throw new RtxMonitorException(nativeStatus, "Diagnóstico experimental recusado pelo backend.");
            if (status is not ("profile_module_unavailable" or "profile_query_failed"))
                throw new InvalidDataException("Estado de diagnóstico experimental inválido.");
            throw new PrivateWorkerException(status, "Diagnóstico experimental indisponível.");
        }
        Require(kind == "profile");
        RequireProperties(root, "protocol_version", "kind", "observation");
        ExperimentalProfileObservation observation = root.GetProperty("observation")
            .Deserialize<ExperimentalProfileObservation>(Options) ?? throw new InvalidDataException("Diagnóstico ausente.");
        Require(observation.Gpu is not null);
        GpuInfo gpu = observation.Gpu!;
        ValidateUuid(gpu.Uuid);
        Require(gpu.Index < 64 && gpu.Uuid.Equals(expectedUuid, StringComparison.OrdinalIgnoreCase));
        RequireText(gpu.Name);
        RequireText(gpu.DriverVersion);
        RequireText(gpu.NvmlVersion);
        if (observation.Board is BoardIdentity board)
        {
            Require(board.GpuIndex == gpu.Index && ((uint)board.Flags & ~3U) == 0);
            RequireText(board.PciBusId, allowEmpty: true);
            RequireText(board.VbiosVersion, allowEmpty: true);
        }
        if (observation.Profile is PrivateProfileStatus profile)
        {
            Require(profile.GpuIndex == gpu.Index && Enum.IsDefined(profile.ProfileState) &&
                Enum.IsDefined(profile.ThermalState) && Enum.IsDefined(profile.VoltageState));
            RequireText(profile.ProfileId, allowEmpty: true);
            if (profile.RevocationReason is not null) RequireText(profile.RevocationReason, allowEmpty: true);
            Require(profile.IdentityChecks is not null && profile.IdentityChecks.Count <= 7);
            foreach (PrivateProfileIdentityCheck check in profile.IdentityChecks!)
            {
                Require(check is not null);
                RequireText(check!.Field);
                Require(check.State is "matched" or "mismatch" or "unavailable");
            }
            // Unknown/revoked policy remains representable; the collector decides eligibility.
            Require(profile.ThermalMinIntervalMilliseconds <= 60000 && profile.VoltageMinIntervalMilliseconds <= 60000 &&
                profile.ThermalTimeoutMilliseconds <= 5000 && profile.VoltageTimeoutMilliseconds <= 5000);
        }
        return observation;
    }

    internal static void ValidateUuid(string uuid)
    {
        Require(uuid is not null && uuid.StartsWith("GPU-", StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParseExact(uuid.AsSpan(4), "D", out Guid parsed) && parsed != Guid.Empty);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(type =>
        {
            if (type.Kind != JsonTypeInfoKind.Object) return;
            foreach (JsonPropertyInfo property in type.Properties)
                if (property.Set is not null) property.IsRequired = true;
        });
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            IgnoreReadOnlyProperties = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            TypeInfoResolver = resolver,
            MaxDepth = 12,
        };
    }

    private static void RequireUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                Require(names.Add(property.Name));
                RequireUniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in element.EnumerateArray()) RequireUniqueProperties(item);
    }

    private static void RequireProperties(JsonElement element, params string[] properties) =>
        Require(element.EnumerateObject().Select(property => property.Name).Order()
            .SequenceEqual(properties.Order()));

    private static void RequireText(string value, bool allowEmpty = false) =>
        Require(value is not null && value.Length <= 256 && (allowEmpty || !string.IsNullOrWhiteSpace(value)));

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidDataException("Protocolo do diagnóstico experimental inválido.");
    }
}
