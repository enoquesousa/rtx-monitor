using System.Text.Json;

namespace RtxMonitor.Managed;

public enum PrivateProfileState : uint
{
    Unknown = 0,
    Active = 1,
    Revoked = 2,
}

public enum PrivateOperationState : uint
{
    Unknown = 0,
    Compatible = 1,
    Revoked = 2,
    IdentityUnavailable = 3,
    IdentityMismatch = 4,
    ModuleUnavailable = 5,
    GpuNotFound = 6,
    IdentityAmbiguous = 7,
    QueryFailed = 8,
    RateLimited = 9,
    Timeout = 10,
}

public sealed record PrivateProfileIdentityCheck(string Field, string State);

/// <summary>Eligibility to attempt acquisition; no private sensor is read by this diagnostic.</summary>
public sealed record PrivateProfileStatus(
    uint GpuIndex,
    string ProfileId,
    uint ProfileRevision,
    PrivateProfileState ProfileState,
    string? RevocationReason,
    IReadOnlyList<PrivateProfileIdentityCheck> IdentityChecks,
    PrivateOperationState ThermalState,
    PrivateOperationState VoltageState,
    uint ThermalMinIntervalMilliseconds,
    uint ThermalTimeoutMilliseconds,
    uint VoltageMinIntervalMilliseconds,
    uint VoltageTimeoutMilliseconds)
{
    private static readonly string[] identityFields =
        ["pci_vendor_id", "pci_device_id", "pci_subsystem_vendor_id", "pci_subsystem_device_id", "gpu_uuid", "vbios_version", "driver_version"];

    internal static PrivateProfileStatus FromNative(NativePrivateProfileStatus native)
    {
        const uint allIdentityFlags = 127;
        if ((native.IdentityCheckedFlags & ~allIdentityFlags) != 0 ||
            (native.IdentityMatchFlags & ~native.IdentityCheckedFlags) != 0)
        {
            throw new InvalidOperationException("O diagnóstico nativo contém flags de identidade inconsistentes.");
        }

        PrivateProfileState profileState = Enum.IsDefined((PrivateProfileState)native.ProfileState)
            ? (PrivateProfileState)native.ProfileState : PrivateProfileState.Unknown;
        PrivateOperationState thermal = NormalizeOperation(native.ThermalState);
        PrivateOperationState voltage = NormalizeOperation(native.VoltageState);
        if ((thermal == PrivateOperationState.Compatible || voltage == PrivateOperationState.Compatible) &&
            (profileState != PrivateProfileState.Active || native.IdentityMatchFlags != allIdentityFlags))
        {
            throw new InvalidOperationException("O diagnóstico declarou compatibilidade sem um perfil ativo e identidade completa.");
        }

        PrivateProfileIdentityCheck[] checks = identityFields.Select((field, bit) =>
        {
            uint flag = 1U << bit;
            string state = (native.IdentityCheckedFlags & flag) == 0 ? "unavailable"
                : (native.IdentityMatchFlags & flag) != 0 ? "matched" : "mismatch";
            return new PrivateProfileIdentityCheck(field, state);
        }).ToArray();

        return new PrivateProfileStatus(
            native.GpuIndex, native.ProfileId, native.ProfileRevision, profileState,
            string.IsNullOrWhiteSpace(native.RevocationReason) ? null : native.RevocationReason,
            Array.AsReadOnly(checks), thermal, voltage,
            native.ThermalMinIntervalMilliseconds, native.ThermalTimeoutMilliseconds,
            native.VoltageMinIntervalMilliseconds, native.VoltageTimeoutMilliseconds);
    }

    public bool IsEligibleForAcquisition(PrivateOperationState state) =>
        ProfileState == PrivateProfileState.Active && state == PrivateOperationState.Compatible &&
        IdentityChecks.Count == identityFields.Length &&
        IdentityChecks.Select(check => check.Field).SequenceEqual(identityFields) &&
        IdentityChecks.All(check => check.State == "matched");

    public string ToJson(DateTimeOffset evaluatedAt) => JsonSerializer.Serialize(new
    {
        schema_version = 2,
        source_kind = "private_profile_status",
        gpu_index = GpuIndex,
        evaluated_at_utc = evaluatedAt.ToUniversalTime().ToString("O"),
        profile_id = ProfileId,
        profile_revision = ProfileRevision,
        profile_state = ProfileState switch
        {
            PrivateProfileState.Active => "active",
            PrivateProfileState.Revoked => "revoked",
            _ => "unknown",
        },
        revocation_reason = RevocationReason,
        acquisition_performed = false,
        returned_payload_state = "not_evaluated",
        gsp_state = "not_observed",
        identity_checks = IdentityChecks.Select(check => new { field = check.Field, state = check.State }),
        operations = new[]
        {
            new { operation = "thermal", state = StateName(ThermalState), eligible_for_acquisition = IsEligibleForAcquisition(ThermalState), minimum_interval_ms = ThermalMinIntervalMilliseconds, acquisition_timeout_ms = ThermalTimeoutMilliseconds },
            new { operation = "voltage", state = StateName(VoltageState), eligible_for_acquisition = IsEligibleForAcquisition(VoltageState), minimum_interval_ms = VoltageMinIntervalMilliseconds, acquisition_timeout_ms = VoltageTimeoutMilliseconds },
        },
        rate_limit_scope = "process_per_operation",
        timeout_behavior = "discard_late_result_and_block_process",
    });

    public static string StateName(PrivateOperationState state) => state switch
    {
        PrivateOperationState.Compatible => "compatible",
        PrivateOperationState.Revoked => "revoked",
        PrivateOperationState.IdentityUnavailable => "identity_unavailable",
        PrivateOperationState.IdentityMismatch => "identity_mismatch",
        PrivateOperationState.ModuleUnavailable => "module_unavailable",
        PrivateOperationState.GpuNotFound => "gpu_not_found",
        PrivateOperationState.IdentityAmbiguous => "identity_ambiguous",
        PrivateOperationState.QueryFailed => "query_failed",
        PrivateOperationState.RateLimited => "rate_limited",
        PrivateOperationState.Timeout => "timeout",
        _ => "unknown",
    };

    private static PrivateOperationState NormalizeOperation(uint state) =>
        Enum.IsDefined((PrivateOperationState)state) ? (PrivateOperationState)state : PrivateOperationState.Unknown;
}
