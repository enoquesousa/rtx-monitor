using System.Globalization;
using System.Text.Json;

namespace RtxMonitor.Managed;

public enum ExperimentalTelemetryState
{
    Available, Disabled, NotRequested, ProfileUnknown, Revoked, IdentityUnavailable,
    IdentityMismatch, ModuleUnavailable, GpuNotFound, IdentityAmbiguous, QueryFailed,
    RateLimited, Timeout, PayloadInvalid, WorkerUnavailable, Cancelled, PublicationPending,
}

public sealed record ExperimentalTelemetryIdentity(
    string GpuUuid, string DriverVersion, string NvmlVersion,
    uint? PciVendorId, uint? PciDeviceId, uint? PciSubsystemVendorId,
    uint? PciSubsystemDeviceId, string? VbiosVersion);

public sealed record ExperimentalTelemetryTolerance(string Reference, double Value, string Unit);

public sealed record ExperimentalAcquisitionInterval(
    DateTimeOffset StartedAt, long StartedMonotonicMilliseconds, long FinishedMonotonicMilliseconds);

/// <summary>A decoded value and its provenance. Failure never retains an earlier numeric payload.</summary>
public sealed class ExperimentalTelemetryField
{
    internal ExperimentalTelemetryField(bool thermal, ExperimentalTelemetryState state,
        double? value = null, long? rawValue = null, DateTimeOffset? capturedAt = null, int? nativeStatus = null,
        ExperimentalAcquisitionInterval? acquisition = null)
    {
        IsThermal = thermal;
        State = state;
        Value = state == ExperimentalTelemetryState.Available ? value : null;
        RawValue = state == ExperimentalTelemetryState.Available ? rawValue : null;
        CapturedAt = state == ExperimentalTelemetryState.Available ? capturedAt?.ToUniversalTime() : null;
        NativeStatus = state == ExperimentalTelemetryState.Available ? nativeStatus : null;
        Acquisition = state == ExperimentalTelemetryState.Available ? acquisition : null;
    }

    internal bool IsThermal { get; }
    public string Field => IsThermal ? "gpu_hotspot_temperature_c" : "gpu_core_voltage_v";
    public ExperimentalTelemetryState State { get; }
    public string StateName => ExperimentalTelemetryJson.StateName(State);
    public string Origin => "private_profile";
    public double? Value { get; }
    public long? RawValue { get; }
    public DateTimeOffset? CapturedAt { get; }
    public int? NativeStatus { get; }
    public ExperimentalAcquisitionInterval? Acquisition { get; }
    public string Unit => IsThermal ? "celsius" : "volt";
    public string RawEncoding => IsThermal ? "signed_fixed8" : "unsigned_microvolts";
    public string Formula => IsThermal ? "raw_value / 256" : "raw_value / 1000000";
    public string WireEncoding => IsThermal ? "signed_fixed8" : "unsigned_microvolts";
    public string WireFormula => IsThermal ? "wire_raw_value / 256" : "wire_raw_value / 1000000";
    public long? WireRawValue => RawValue;
    public string SourceKind => IsThermal ? PrivateThermalSample.SourceKind : PrivateVoltageSample.SourceKind;
    public string InterfaceId => IsThermal ? PrivateThermalSample.InterfaceId : PrivateVoltageSample.InterfaceId;
    public string StructureVersion => IsThermal ? PrivateThermalSample.StructureVersion : PrivateVoltageSample.StructureVersion;
    public string FunctionRva => IsThermal ? PrivateThermalSample.FunctionRva : PrivateVoltageSample.FunctionRva;
    public string ModuleSha256 => PrivateThermalSample.NvapiModuleSha256;
    public string ProfileEvidenceStage => "matched_external_reference";
    public IReadOnlyList<ExperimentalTelemetryTolerance> Tolerances => IsThermal ? ThermalTolerances : VoltageTolerances;
    public IReadOnlyList<string> EvidenceReferences => IsThermal ? ThermalEvidence : VoltageEvidence;

    private static readonly IReadOnlyList<ExperimentalTelemetryTolerance> ThermalTolerances = Array.AsReadOnly(
        new[] { new ExperimentalTelemetryTolerance("GPU-Z", 0.051, "celsius") });
    private static readonly IReadOnlyList<ExperimentalTelemetryTolerance> VoltageTolerances = Array.AsReadOnly(
        new[] { new ExperimentalTelemetryTolerance("GPU-Z", 0.001, "volt"), new ExperimentalTelemetryTolerance("HWiNFO", 0.002, "volt") });
    private static readonly IReadOnlyList<string> ThermalEvidence = Array.AsReadOnly(new[]
    {
        "docs/adr/0010-fixed-profile-private-nvapi-acquisition.md",
        "docs/profiles/rtx3060-galax-12gb.json",
        "docs/profiles/rtx3060-runtime-fixture-provenance.json",
        "docs/research/2026-09-06-v011-stabilization.md",
    });
    private static readonly IReadOnlyList<string> VoltageEvidence = Array.AsReadOnly(new[]
    {
        "docs/adr/0010-fixed-profile-private-nvapi-acquisition.md",
        "docs/research/2026-08-26-rtx3060-nvapi-voltage-status-v1.md",
        "docs/profiles/rtx3060-galax-12gb.json",
        "docs/profiles/rtx3060-runtime-fixture-provenance.json",
        "csharp/RtxMonitor.Lab/VoltageStatusCorrelationV2.cs",
    });
}

/// <summary>Immutable opt-in exact-profile snapshot. Internal type name retained for source compatibility. This factory never acquires hardware or grants eligibility.</summary>
public sealed class ExperimentalTelemetryReport
{
    public const string SupportedProfileId = PrivateThermalSample.Profile;
    public const uint SupportedProfileRevision = 2;
    public const string SupportedGpuUuid = "GPU-11111111-2222-3333-4444-555555555555";

    private ExperimentalTelemetryReport(uint gpuIndex, ExperimentalTelemetryIdentity identity,
        string? profileId, uint? profileRevision, string profileState, string? revocationReason,
        DateTimeOffset capturedAt, ExperimentalTelemetryField thermal, ExperimentalTelemetryField voltage)
    {
        GpuIndex = gpuIndex;
        Identity = identity;
        ProfileId = profileId;
        ProfileRevision = profileRevision;
        ProfileState = profileState;
        RevocationReason = revocationReason;
        CapturedAt = capturedAt.ToUniversalTime();
        Fields = Array.AsReadOnly(new[] { thermal, voltage });
    }

    public uint GpuIndex { get; }
    public string GpuUuid => Identity.GpuUuid;
    public DateTimeOffset CapturedAt { get; }
    public string? ProfileId { get; }
    public uint? ProfileRevision { get; }
    public string ProfileState { get; }
    public string? RevocationReason { get; }
    public ExperimentalTelemetryIdentity Identity { get; }
    public IReadOnlyList<ExperimentalTelemetryField> Fields { get; }

    public static ExperimentalTelemetryReport Create(GpuInfo gpu, BoardIdentity? board,
        PrivateProfileStatus? profile, JsonElement? thermal, JsonElement? voltage,
        ExperimentalTelemetryState thermalFailure = ExperimentalTelemetryState.NotRequested,
        ExperimentalTelemetryState voltageFailure = ExperimentalTelemetryState.NotRequested,
        DateTimeOffset? evaluatedAt = null)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        DateTimeOffset now = (evaluatedAt ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var identity = ObservedIdentity(gpu, board);
        return new ExperimentalTelemetryReport(gpu.Index, identity, profile?.ProfileId, profile?.ProfileRevision,
            profile?.ProfileState switch { PrivateProfileState.Active => "active", PrivateProfileState.Revoked => "revoked", _ => "unknown" },
            profile?.RevocationReason, now,
            BuildField(true, gpu, board, profile, thermal, thermalFailure, now),
            BuildField(false, gpu, board, profile, voltage, voltageFailure, now));
    }

    private static ExperimentalTelemetryIdentity ObservedIdentity(GpuInfo gpu, BoardIdentity? board) =>
        new(gpu.Uuid, gpu.DriverVersion, gpu.NvmlVersion,
            board?.HasPciIdentity == true ? board.PciVendorId : null,
            board?.HasPciIdentity == true ? board.PciDeviceId : null,
            board?.HasPciIdentity == true ? board.PciSubsystemVendorId : null,
            board?.HasPciIdentity == true ? board.PciSubsystemDeviceId : null,
            board?.HasVbiosVersion == true ? board.VbiosVersion : null);

    public ExperimentalTelemetryReport WithFailure(ExperimentalTelemetryState state, DateTimeOffset evaluatedAt,
        GpuInfo? observedGpu = null, BoardIdentity? observedBoard = null)
    {
        if (!Enum.IsDefined(state) || state == ExperimentalTelemetryState.Available)
            throw new ArgumentOutOfRangeException(nameof(state));
        return new ExperimentalTelemetryReport(observedGpu?.Index ?? GpuIndex,
            observedGpu is null ? Identity : ObservedIdentity(observedGpu, observedBoard), ProfileId, ProfileRevision, ProfileState,
            RevocationReason, evaluatedAt, new(true, state), new(false, state));
    }

    private static ExperimentalTelemetryField BuildField(bool thermal, GpuInfo gpu, BoardIdentity? board,
        PrivateProfileStatus? profile, JsonElement? payload, ExperimentalTelemetryState failure, DateTimeOffset now)
    {
        if (failure == ExperimentalTelemetryState.Disabled) return new(thermal, failure);
        ExperimentalTelemetryState gate = EvaluateGate(gpu, board, profile, thermal);
        if (gate != ExperimentalTelemetryState.NotRequested) return new(thermal, gate);
        if (!Enum.IsDefined(failure)) return new(thermal, ExperimentalTelemetryState.PayloadInvalid);
        if (failure is not (ExperimentalTelemetryState.NotRequested or ExperimentalTelemetryState.Available))
            return new(thermal, failure);
        if (payload is null)
            return new(thermal, Enum.IsDefined(failure) && failure != ExperimentalTelemetryState.Available
                ? failure : ExperimentalTelemetryState.PayloadInvalid);
        try
        {
            JsonElement root = payload.Value;
            Require(root.ValueKind == JsonValueKind.Object);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in root.EnumerateObject()) Require(names.Add(property.Name));
            Require(root.GetProperty("schema_version").GetInt32() == (thermal ? 2 : 1));
            Require(root.GetProperty("gpu_index").GetUInt32() == gpu.Index);
            Require(Text(root, "gpu_uuid").Equals(gpu.Uuid, StringComparison.OrdinalIgnoreCase));
            Require(Text(root, "profile_name") == SupportedProfileId);
            Require(Text(root, "profile_evidence_stage") == "matched_external_reference");
            Require(Text(root, "source_kind") == (thermal ? PrivateThermalSample.SourceKind : PrivateVoltageSample.SourceKind));
            Require(Text(root, "interface_id") == (thermal ? PrivateThermalSample.InterfaceId : PrivateVoltageSample.InterfaceId));
            Require(Text(root, "structure_version") == (thermal ? PrivateThermalSample.StructureVersion : PrivateVoltageSample.StructureVersion));
            Require(Text(root, "function_rva") == (thermal ? PrivateThermalSample.FunctionRva : PrivateVoltageSample.FunctionRva));
            Require(Text(root, "nvapi_module_sha256") == PrivateThermalSample.NvapiModuleSha256);
            Require(root.GetProperty("native_status").GetInt32() == 0);
            Require(root.GetProperty("monotonic_ns").GetInt64() >= 0);
            Require(root.GetProperty("monotonic_frequency_hz").GetInt64() > 0);
            DateTimeOffset captured = DateTimeOffset.ParseExact(Text(root, "captured_at_utc"), "O", CultureInfo.InvariantCulture).ToUniversalTime();
            Require(root.GetProperty("captured_at_unix_ms").GetInt64() == captured.ToUnixTimeMilliseconds());
            Require(captured >= now.AddSeconds(-10) && captured <= now.AddSeconds(1));
            if (thermal)
            {
                double die = Number(root, "gpu_die_temperature_c");
                double hotspot = Number(root, "gpu_hotspot_temperature_c");
                double delta = Number(root, "delta_c");
                Require(die is >= -40 and <= 125 && hotspot is >= -40 and <= 150 &&
                    hotspot >= die && hotspot - die <= 80 && delta == hotspot - die);
                int rawDie = root.GetProperty("gpu_die_temperature_fixed8").GetInt32();
                int rawHotspot = root.GetProperty("gpu_hotspot_temperature_fixed8").GetInt32();
                Require(die == rawDie / 256.0 && hotspot == rawHotspot / 256.0);
                Require(root.GetProperty("gpu_die_temperature_millic").GetInt32() == RoundedMillicelsius(rawDie) &&
                    root.GetProperty("gpu_hotspot_temperature_millic").GetInt32() == RoundedMillicelsius(rawHotspot));
                DateTimeOffset started = DateTimeOffset.ParseExact(Text(root, "acquisition_started_at_utc"), "O", CultureInfo.InvariantCulture).ToUniversalTime();
                long begin = root.GetProperty("acquisition_started_monotonic_ms").GetInt64();
                long end = root.GetProperty("acquisition_finished_monotonic_ms").GetInt64();
                Require(root.GetProperty("acquisition_started_unix_ms").GetInt64() == started.ToUnixTimeMilliseconds() &&
                    started <= captured && started >= captured.AddMilliseconds(-profile!.ThermalTimeoutMilliseconds) &&
                    begin >= 0 && end >= begin && end - begin < profile.ThermalTimeoutMilliseconds);
                return new(thermal, ExperimentalTelemetryState.Available, hotspot, rawHotspot, captured, 0,
                    new ExperimentalAcquisitionInterval(started, begin, end));
            }
            long microvolts = root.GetProperty("gpu_core_voltage_microvolts").GetInt64();
            double volts = Number(root, "gpu_core_voltage_v");
            Require(microvolts is >= 100000 and <= 2000000 && Math.Abs(volts - microvolts / 1000000.0) < 1e-12);
            return new(thermal, ExperimentalTelemetryState.Available, microvolts / 1000000.0, microvolts, captured, 0);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or
            FormatException or KeyNotFoundException or OverflowException or ArgumentOutOfRangeException)
        {
            return new(thermal, ExperimentalTelemetryState.PayloadInvalid);
        }
    }

    private static ExperimentalTelemetryState EvaluateGate(GpuInfo gpu, BoardIdentity? board, PrivateProfileStatus? profile, bool thermal)
    {
        if (profile is null || profile.ProfileId != SupportedProfileId || profile.ProfileRevision != SupportedProfileRevision ||
            profile.ProfileState == PrivateProfileState.Unknown) return ExperimentalTelemetryState.ProfileUnknown;
        if (profile.ProfileState == PrivateProfileState.Revoked) return ExperimentalTelemetryState.Revoked;
        PrivateOperationState operation = thermal ? profile.ThermalState : profile.VoltageState;
        if (operation != PrivateOperationState.Compatible) return operation switch
        {
            PrivateOperationState.Revoked => ExperimentalTelemetryState.Revoked,
            PrivateOperationState.IdentityUnavailable => ExperimentalTelemetryState.IdentityUnavailable,
            PrivateOperationState.IdentityMismatch => ExperimentalTelemetryState.IdentityMismatch,
            PrivateOperationState.ModuleUnavailable => ExperimentalTelemetryState.ModuleUnavailable,
            PrivateOperationState.GpuNotFound => ExperimentalTelemetryState.GpuNotFound,
            PrivateOperationState.IdentityAmbiguous => ExperimentalTelemetryState.IdentityAmbiguous,
            PrivateOperationState.QueryFailed => ExperimentalTelemetryState.QueryFailed,
            PrivateOperationState.RateLimited => ExperimentalTelemetryState.RateLimited,
            PrivateOperationState.Timeout => ExperimentalTelemetryState.Timeout,
            _ => ExperimentalTelemetryState.ProfileUnknown,
        };
        if (!profile.IsEligibleForAcquisition(operation)) return ExperimentalTelemetryState.IdentityUnavailable;
        if (board is null || !board.HasPciIdentity || !board.HasVbiosVersion ||
            string.IsNullOrWhiteSpace(gpu.Uuid) || string.IsNullOrWhiteSpace(gpu.DriverVersion))
            return ExperimentalTelemetryState.IdentityUnavailable;
        if (profile.GpuIndex != gpu.Index || board.GpuIndex != gpu.Index ||
            !gpu.Uuid.Equals(SupportedGpuUuid, StringComparison.OrdinalIgnoreCase) || gpu.DriverVersion != "610.88" ||
            board.PciVendorId != 0x10de || board.PciDeviceId != 0x2504 || board.PciSubsystemVendorId != 0x10de ||
            board.PciSubsystemDeviceId != 0x1536 || !board.VbiosVersion.Equals("94.06.25.00.fc", StringComparison.OrdinalIgnoreCase))
            return ExperimentalTelemetryState.IdentityMismatch;
        return ExperimentalTelemetryState.NotRequested;
    }

    private static long RoundedMillicelsius(int fixed8) =>
        ((long)fixed8 * 1000 + (fixed8 >= 0 ? 128 : -128)) / 256;
    private static string Text(JsonElement root, string name) => root.GetProperty(name).GetString() ?? throw new FormatException();
    private static double Number(JsonElement root, string name)
    {
        double value = root.GetProperty(name).GetDouble();
        Require(double.IsFinite(value));
        return value;
    }
    private static void Require(bool condition) { if (!condition) throw new FormatException("Invalid experimental payload."); }
}

public static class ExperimentalTelemetryJson
{
    public const int SchemaVersion = 1;
    public static string Serialize(ExperimentalTelemetryReport report) => ToElement(report).GetRawText();

    public static JsonElement ToElement(ExperimentalTelemetryReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.SerializeToElement(new
        {
            schema_version = SchemaVersion,
            source_kind = "profile_telemetry",
            origin = "private_profile",
            support_scope = "exact_gpu_driver_module_profile",
            runtime_source = "direct_nvidia_driver",
            validation_revision = "v011-source-fidelity-1",
            gpu_index = report.GpuIndex,
            gpu_uuid = report.GpuUuid,
            captured_at_utc = report.CapturedAt.ToString("O"),
            captured_at_unix_ms = report.CapturedAt.ToUnixTimeMilliseconds(),
            profile_id = report.ProfileId,
            profile_revision = report.ProfileRevision,
            profile_state = report.ProfileState,
            revocation_reason = report.RevocationReason,
            identity = new
            {
                gpu_uuid = report.Identity.GpuUuid,
                driver_version = report.Identity.DriverVersion,
                nvml_version = report.Identity.NvmlVersion,
                pci_vendor_id = Hex(report.Identity.PciVendorId),
                pci_device_id = Hex(report.Identity.PciDeviceId),
                pci_subsystem_vendor_id = Hex(report.Identity.PciSubsystemVendorId),
                pci_subsystem_device_id = Hex(report.Identity.PciSubsystemDeviceId),
                vbios_version = report.Identity.VbiosVersion,
            },
            expected_identity = new
            {
                gpu_uuid = ExperimentalTelemetryReport.SupportedGpuUuid,
                driver_version = "610.88",
                vbios_version = "94.06.25.00.fc",
                pci_vendor_id = "0x10de",
                pci_device_id = "0x2504",
                pci_subsystem_vendor_id = "0x10de",
                pci_subsystem_device_id = "0x1536",
            },
            gsp = new { state = "not_observed", version = (string?)null },
            fields = report.Fields.Select(field => new
            {
                field = field.Field,
                state = field.StateName,
                origin = field.Origin,
                value = field.Value,
                raw_value = field.RawValue,
                raw_encoding = field.RawEncoding,
                raw_source = "native_abi",
                formula = field.Formula,
                wire_encoding = field.WireEncoding,
                wire_formula = field.WireFormula,
                wire_raw_value = field.WireRawValue,
                wire_raw_state = field.Value.HasValue ? "available" : "unavailable",
                unit = field.Unit,
                captured_at_utc = field.CapturedAt?.ToString("O"),
                captured_at_unix_ms = field.CapturedAt?.ToUnixTimeMilliseconds(),
                native_status = field.NativeStatus,
                acquisition = field.Acquisition is null ? null : new
                {
                    started_at_utc = field.Acquisition.StartedAt.ToString("O"),
                    started_unix_ms = field.Acquisition.StartedAt.ToUnixTimeMilliseconds(),
                    started_monotonic_ms = field.Acquisition.StartedMonotonicMilliseconds,
                    finished_monotonic_ms = field.Acquisition.FinishedMonotonicMilliseconds,
                    clock = "native_monotonic_milliseconds",
                    scope = "thermal_pair",
                },
                source_kind = field.SourceKind,
                interface_id = field.InterfaceId,
                structure_version = field.StructureVersion,
                nvapi_module_sha256 = field.ModuleSha256,
                function_rva = field.FunctionRva,
                profile_evidence_stage = field.ProfileEvidenceStage,
                provenance_state = field.Value.HasValue ? "validated_worker_payload" : "profile_definition_only",
                tolerances = field.Tolerances.Select(tolerance => new
                {
                    reference = tolerance.Reference,
                    value = tolerance.Value,
                    unit = tolerance.Unit,
                    scope = field.IsThermal ? "same_acquisition_reference_rounding" : "recorded_reference_comparison",
                    asynchronous_comparison = "diagnostic_only",
                    physical_accuracy_guarantee = false,
                }),
                evidence_references = field.EvidenceReferences,
            }),
        });
    }

    public static string StateName(ExperimentalTelemetryState state) => state switch
    {
        ExperimentalTelemetryState.Available => "available",
        ExperimentalTelemetryState.Disabled => "disabled",
        ExperimentalTelemetryState.NotRequested => "not_requested",
        ExperimentalTelemetryState.ProfileUnknown => "profile_unknown",
        ExperimentalTelemetryState.Revoked => "revoked",
        ExperimentalTelemetryState.IdentityUnavailable => "identity_unavailable",
        ExperimentalTelemetryState.IdentityMismatch => "identity_mismatch",
        ExperimentalTelemetryState.ModuleUnavailable => "module_unavailable",
        ExperimentalTelemetryState.GpuNotFound => "gpu_not_found",
        ExperimentalTelemetryState.IdentityAmbiguous => "identity_ambiguous",
        ExperimentalTelemetryState.QueryFailed => "query_failed",
        ExperimentalTelemetryState.RateLimited => "rate_limited",
        ExperimentalTelemetryState.Timeout => "timeout",
        ExperimentalTelemetryState.PayloadInvalid => "payload_invalid",
        ExperimentalTelemetryState.WorkerUnavailable => "worker_unavailable",
        ExperimentalTelemetryState.Cancelled => "cancelled",
        ExperimentalTelemetryState.PublicationPending => "publication_pending",
        _ => "payload_invalid",
    };
    private static string? Hex(uint? value) => value.HasValue ? $"0x{value.Value:x4}" : null;
}
