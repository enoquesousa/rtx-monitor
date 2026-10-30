using System.Text.Json;
using System.Text.Json.Nodes;
using RtxMonitor.Managed;

namespace RtxMonitor.Managed.Tests;

internal static class ExperimentalTelemetryTests
{
    internal static int Run()
    {
        int failures = 0;
        void Check(bool condition, string message)
        {
            if (!condition) { Console.Error.WriteLine($"FAILED: experimental {message}"); failures++; }
        }

        GpuInfo gpu = new(0, "NVIDIA GeForce RTX 3060", ExperimentalTelemetryReport.SupportedGpuUuid, "610.88", "13.610.88");
        BoardIdentity board = new(0, 0x10de, 0x2504, 0x10de, 0x1536, 0, 1, 0, 0,
            BoardIdentityFlags.PciValid | BoardIdentityFlags.VbiosValid, "00000000:01:00.0", "94.06.25.00.fc");
        string[] identityFields = ["pci_vendor_id", "pci_device_id", "pci_subsystem_vendor_id", "pci_subsystem_device_id", "gpu_uuid", "vbios_version", "driver_version"];
        PrivateProfileStatus profile = new(0, ExperimentalTelemetryReport.SupportedProfileId, 2, PrivateProfileState.Active, null,
            Array.AsReadOnly(identityFields.Select(field => new PrivateProfileIdentityCheck(field, "matched")).ToArray()),
            PrivateOperationState.Compatible, PrivateOperationState.Compatible, 100, 2000, 100, 2000);

        JsonElement thermal = Fixture("rtx3060-thermal-synthetic-v2.json");
        JsonElement voltage = Fixture("rtx3060-voltage-recorded-v1.json");
        DateTimeOffset thermalTime = thermal.GetProperty("captured_at_utc").GetDateTimeOffset();
        DateTimeOffset voltageTime = voltage.GetProperty("captured_at_utc").GetDateTimeOffset();
        ExperimentalTelemetryReport thermalReport = ExperimentalTelemetryReport.Create(gpu, board, profile, thermal, null, evaluatedAt: thermalTime);
        ExperimentalTelemetryReport voltageReport = ExperimentalTelemetryReport.Create(gpu, board, profile, null, voltage, evaluatedAt: voltageTime);
        Check(thermalReport.Fields[0].State == ExperimentalTelemetryState.Available &&
            thermalReport.Fields[0].RawValue == 12168 && thermalReport.Fields[0].Value == 47.53125 &&
            thermalReport.Fields[0].WireRawValue == 12168 && thermalReport.Fields[0].RawEncoding == "signed_fixed8" &&
            thermalReport.Fields[0].Acquisition?.StartedMonotonicMilliseconds == 123456,
            "synthetic raw native vector preserves fixed8, exact Celsius and acquisition interval");
        Check(voltageReport.Fields[1].State == ExperimentalTelemetryState.Available &&
            voltageReport.Fields[1].RawValue == voltage.GetProperty("gpu_core_voltage_microvolts").GetInt64() &&
            voltageReport.Fields[1].Value == voltage.GetProperty("gpu_core_voltage_v").GetDouble() &&
            voltageReport.Fields[1].WireRawValue == voltageReport.Fields[1].RawValue,
            "recorded x64 voltage preserves integer microvolts and exact conversion");
        Check(thermalReport.Fields[1].State == ExperimentalTelemetryState.NotRequested && thermalReport.Fields[1].Value is null,
            "unsampled operation cannot borrow the other operation's value");
        Check(thermalReport.Fields[0].Tolerances.Single().Value == 0.051 &&
            voltageReport.Fields[1].Tolerances.Select(tolerance => tolerance.Value).SequenceEqual(new[] { 0.001, 0.002 }),
            "historical comparison tolerances remain reference-specific");
        ExperimentalTelemetryReport uppercaseIdentity = ExperimentalTelemetryReport.Create(gpu with { Uuid = gpu.Uuid.ToUpperInvariant() },
            board with { VbiosVersion = board.VbiosVersion.ToUpperInvariant() }, profile, thermal, null, evaluatedAt: thermalTime);
        Check(uppercaseIdentity.Fields[0].State == ExperimentalTelemetryState.Available &&
            uppercaseIdentity.Identity.GpuUuid == gpu.Uuid.ToUpperInvariant() &&
            uppercaseIdentity.Identity.VbiosVersion == board.VbiosVersion.ToUpperInvariant(),
            "case-equivalent UUID/VBIOS remain valid and preserve observed formatting");

        ExperimentalTelemetryReport[] gates =
        [
            ExperimentalTelemetryReport.Create(gpu, board, null, thermal, null, evaluatedAt: thermalTime),
            ExperimentalTelemetryReport.Create(gpu, board, profile with { ProfileId = "unreviewed-profile" }, thermal, null, evaluatedAt: thermalTime),
            ExperimentalTelemetryReport.Create(gpu, board, profile with { ProfileRevision = 3 }, thermal, null, evaluatedAt: thermalTime),
            ExperimentalTelemetryReport.Create(gpu, board, profile with { ProfileState = PrivateProfileState.Revoked, RevocationReason = "fixture_revoked" }, thermal, null, evaluatedAt: thermalTime),
            ExperimentalTelemetryReport.Create(gpu with { DriverVersion = "611.00" }, board, profile, thermal, null, evaluatedAt: thermalTime),
            ExperimentalTelemetryReport.Create(gpu, board with { VbiosVersion = "94.06.25.00.fd" }, profile, thermal, null, evaluatedAt: thermalTime),
            ExperimentalTelemetryReport.Create(gpu, board with { PciSubsystemDeviceId = 0x1537 }, profile, thermal, null, evaluatedAt: thermalTime),
            ExperimentalTelemetryReport.Create(gpu, null, profile, thermal, null, evaluatedAt: thermalTime),
        ];
        ExperimentalTelemetryState[] expectedStates = [ExperimentalTelemetryState.ProfileUnknown, ExperimentalTelemetryState.ProfileUnknown,
            ExperimentalTelemetryState.ProfileUnknown, ExperimentalTelemetryState.Revoked, ExperimentalTelemetryState.IdentityMismatch,
            ExperimentalTelemetryState.IdentityMismatch, ExperimentalTelemetryState.IdentityMismatch, ExperimentalTelemetryState.IdentityUnavailable];
        for (int i = 0; i < gates.Length; i++)
            Check(gates[i].Fields.All(field => field.State == expectedStates[i] && field.Value is null && field.RawValue is null),
                $"profile/identity gate {i} rejects even a known successful payload");

        foreach (PrivateOperationState operation in Enum.GetValues<PrivateOperationState>().Where(state => state != PrivateOperationState.Compatible))
        {
            ExperimentalTelemetryReport denied = ExperimentalTelemetryReport.Create(gpu, board, profile with { ThermalState = operation }, thermal, null, evaluatedAt: thermalTime);
            Check(denied.Fields[0].State != ExperimentalTelemetryState.Available && denied.Fields[0].Value is null &&
                denied.Fields[1].State == ExperimentalTelemetryState.NotRequested, $"operation gate {operation} remains independent");
        }
        ExperimentalTelemetryReport disabled = ExperimentalTelemetryReport.Create(gpu, null, null, thermal, voltage,
            ExperimentalTelemetryState.Disabled, ExperimentalTelemetryState.Disabled, thermalTime);
        Check(disabled.Fields.All(field => field.State == ExperimentalTelemetryState.Disabled && field.Value is null),
            "disabled does not need or imply a profile diagnostic");

        foreach (ExperimentalTelemetryState state in Enum.GetValues<ExperimentalTelemetryState>().Where(state => state != ExperimentalTelemetryState.Available))
        {
            ExperimentalTelemetryReport expired = thermalReport.WithFailure(state, thermalTime.AddMinutes(1));
            JsonElement serialized = ExperimentalTelemetryJson.ToElement(expired);
            Check(expired.Fields.All(field => field.State == state && field.Value is null && field.RawValue is null && field.CapturedAt is null && field.NativeStatus is null) &&
                serialized.GetProperty("fields").EnumerateArray().All(field => field.GetProperty("value").ValueKind == JsonValueKind.Null &&
                    field.GetProperty("raw_value").ValueKind == JsonValueKind.Null && field.GetProperty("provenance_state").GetString() == "profile_definition_only"),
                $"{state} clears previous samples while retaining definition provenance");
        }
        ExperimentalTelemetryReport contradictory = ExperimentalTelemetryReport.Create(gpu, board, profile, thermal, null,
            thermalFailure: ExperimentalTelemetryState.Timeout, evaluatedAt: thermalTime);
        Check(contradictory.Fields[0].State == ExperimentalTelemetryState.Timeout && contradictory.Fields[0].Value is null,
            "explicit failure rejects a simultaneously supplied payload");
        ExperimentalTelemetryReport changedIdentity = thermalReport.WithFailure(ExperimentalTelemetryState.IdentityMismatch, thermalTime.AddSeconds(1),
            gpu with { Index = 1, DriverVersion = "611.00" }, board with { GpuIndex = 1, VbiosVersion = "changed" });
        Check(changedIdentity.GpuIndex == 1 && changedIdentity.Identity.DriverVersion == "611.00" &&
            changedIdentity.Identity.VbiosVersion == "changed" && changedIdentity.ProfileId == profile.ProfileId,
            "invalidation records current observed identity separately from approved profile");

        (string name, object value)[] mutations =
        [
            ("gpu_uuid", "GPU-00000000-0000-0000-0000-000000000001"), ("gpu_index", 1),
            ("profile_name", "unknown"), ("schema_version", 1), ("profile_evidence_stage", "raw_unknown"),
            ("interface_id", "0x00000000"), ("structure_version", "0x000100a8"),
            ("function_rva", "0x00000000"), ("nvapi_module_sha256", new string('0', 64)),
            ("source_kind", "nvapi_cooler_status"), ("native_status", -101),
            ("gpu_hotspot_temperature_c", 151), ("gpu_die_temperature_c", 46),
            ("delta_c", 0), ("gpu_hotspot_temperature_c", 45.28125),
            ("monotonic_ns", -1), ("monotonic_frequency_hz", 0),
            ("gpu_hotspot_temperature_fixed8", 12169), ("gpu_die_temperature_fixed8", 9489),
            ("gpu_hotspot_temperature_millic", 47530), ("gpu_die_temperature_millic", 37062),
            ("acquisition_started_unix_ms", 1), ("acquisition_started_monotonic_ms", 123457),
            ("acquisition_finished_monotonic_ms", 125457),
            ("acquisition_finished_monotonic_ms", 125456),
            ("acquisition_started_at_utc", thermalTime.AddSeconds(1).ToString("O")),
            ("captured_at_unix_ms", thermalTime.AddSeconds(1).ToUnixTimeMilliseconds()),
        ];
        foreach ((string name, object value) in mutations)
        {
            JsonElement altered = Change(thermal, name, value);
            ExperimentalTelemetryReport rejected = ExperimentalTelemetryReport.Create(gpu, board, profile, altered, null, evaluatedAt: thermalTime);
            Check(rejected.Fields[0].State == ExperimentalTelemetryState.PayloadInvalid && rejected.Fields[0].Value is null && rejected.Fields[0].RawValue is null,
                $"altered {name} fails closed");
        }
        foreach ((string name, object value) in new[] { ("gpu_core_voltage_microvolts", (object)99999), ("gpu_core_voltage_v", (object)99.0) })
            Check(ExperimentalTelemetryReport.Create(gpu, board, profile, null, Change(voltage, name, value), evaluatedAt: voltageTime).Fields[1].State == ExperimentalTelemetryState.PayloadInvalid,
                $"voltage {name} conversion/bounds failure");
        Check(ExperimentalTelemetryReport.Create(gpu, board, profile, thermal, null, evaluatedAt: thermalTime.AddSeconds(11)).Fields[0].Value is null &&
            ExperimentalTelemetryReport.Create(gpu, board, profile, thermal, null, evaluatedAt: thermalTime.AddSeconds(-2)).Fields[0].Value is null,
            "stale and future payloads cannot be published as a current sample");
        using (JsonDocument duplicate = JsonDocument.Parse(thermal.GetRawText().Insert(1, "\"native_status\":0,")))
            Check(ExperimentalTelemetryReport.Create(gpu, board, profile, duplicate.RootElement, null, evaluatedAt: thermalTime).Fields[0].State == ExperimentalTelemetryState.PayloadInvalid,
                "duplicate payload keys are rejected");
        JsonObject missingProperty = JsonNode.Parse(thermal.GetRawText())!.AsObject();
        missingProperty.Remove("structure_version");
        Check(ExperimentalTelemetryReport.Create(gpu, board, profile, JsonSerializer.SerializeToElement(missingProperty), null, evaluatedAt: thermalTime).Fields[0].State == ExperimentalTelemetryState.PayloadInvalid,
            "truncated provenance fails closed");

        JsonElement output = ExperimentalTelemetryJson.ToElement(thermalReport);
        JsonElement outputField = output.GetProperty("fields")[0];
        JsonElement tolerance = outputField.GetProperty("tolerances")[0];
        Check(output.GetProperty("source_kind").GetString() == "profile_telemetry" &&
            output.GetProperty("runtime_source").GetString() == "direct_nvidia_driver" &&
            output.GetProperty("support_scope").GetString() == "exact_gpu_driver_module_profile" &&
            tolerance.GetProperty("scope").GetString() == "same_acquisition_reference_rounding" &&
            tolerance.GetProperty("asynchronous_comparison").GetString() == "diagnostic_only" &&
            !tolerance.GetProperty("physical_accuracy_guarantee").GetBoolean(),
            "perfil estável deve distinguir fidelidade à fonte de precisão física e comparação assíncrona");
        Check(output.GetProperty("origin").GetString() == "private_profile" && output.GetProperty("profile_revision").GetUInt32() == 2 &&
            output.GetProperty("identity").GetProperty("driver_version").GetString() == "610.88" &&
            output.GetProperty("expected_identity").GetProperty("vbios_version").GetString() == "94.06.25.00.fc" &&
            output.GetProperty("gsp").GetProperty("state").GetString() == "not_observed" &&
            outputField.GetProperty("wire_raw_state").GetString() == "available" &&
            outputField.GetProperty("provenance_state").GetString() == "validated_worker_payload" &&
            outputField.GetProperty("evidence_references").GetArrayLength() >= 3 &&
            output.GetProperty("fields").GetArrayLength() == 2,
            "structured provenance excludes private cooler and unobserved GSP claims");
        Check(output.GetProperty("schema_version").GetInt32() == 1 &&
            outputField.GetProperty("raw_value").GetInt32() == 12168 &&
            outputField.GetProperty("acquisition").GetProperty("started_monotonic_ms").GetInt64() == 123456,
            "raw v2 projection retains the native integer and pair timing");
        JsonElement legacyThermal = Fixture("rtx3060-thermal-recorded-v1.json");
        Check(ExperimentalTelemetryReport.Create(gpu, board, profile, legacyThermal, null, evaluatedAt: thermalTime)
            .Fields[0].State == ExperimentalTelemetryState.PayloadInvalid,
            "legacy thermal recording cannot masquerade as an acquired raw v2 payload");
        Check(ExperimentalTelemetryJson.Serialize(thermalReport) == output.GetRawText(), "serialization uses one canonical projection");
        Check(thermalReport.Fields is not ExperimentalTelemetryField[] && thermalReport.Fields[0].EvidenceReferences is not string[],
            "published collections cannot be mutated as arrays");
        return failures;
    }

    private static JsonElement Fixture(string filename)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string path = Path.Combine(directory.FullName, "csharp", "RtxMonitor.Managed.Tests", "Fixtures", filename);
            if (File.Exists(path)) { using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path)); return document.RootElement.Clone(); }
            directory = directory.Parent;
        }
        throw new FileNotFoundException($"Recorded experimental fixture not found: {filename}");
    }

    private static JsonElement Change(JsonElement source, string name, object value)
    {
        JsonObject node = JsonNode.Parse(source.GetRawText())!.AsObject();
        node[name] = JsonSerializer.SerializeToNode(value);
        return JsonSerializer.SerializeToElement(node);
    }
}
