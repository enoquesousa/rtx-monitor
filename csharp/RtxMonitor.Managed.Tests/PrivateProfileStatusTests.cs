using System.Runtime.InteropServices;
using System.Text.Json;
using RtxMonitor.Managed;

namespace RtxMonitor.Managed.Tests;

internal static class PrivateProfileStatusTests
{
    internal static int Run()
    {
        int failures = 0;
        void Check(bool condition, string message)
        {
            if (!condition)
            {
                Console.Error.WriteLine($"FAILED: {message}");
                failures++;
            }
        }

        Check(Marshal.SizeOf<NativePrivateProfileStatus>() == 304, "private profile ABI7 size");
        Check(Marshal.OffsetOf<NativePrivateProfileStatus>(nameof(NativePrivateProfileStatus.ProfileId)).ToInt32() == 32 &&
            Marshal.OffsetOf<NativePrivateProfileStatus>(nameof(NativePrivateProfileStatus.RevocationReason)).ToInt32() == 160,
            "private profile ABI7 text offsets");
        Check(Marshal.OffsetOf<NativePrivateProfileStatus>(nameof(NativePrivateProfileStatus.ThermalMinIntervalMilliseconds)).ToInt32() == 288 &&
            Marshal.OffsetOf<NativePrivateProfileStatus>(nameof(NativePrivateProfileStatus.VoltageTimeoutMilliseconds)).ToInt32() == 300,
            "private profile policy field offsets");
        NativePrivateProfileStatus native = NativePrivateProfileStatus.Create();
        native.ProfileId = PrivateThermalSample.Profile;
        native.ProfileRevision = 2;
        native.ThermalMinIntervalMilliseconds = native.VoltageMinIntervalMilliseconds = 100;
        native.ThermalTimeoutMilliseconds = native.VoltageTimeoutMilliseconds = 2000;
        native.ProfileState = 1;
        native.IdentityCheckedFlags = native.IdentityMatchFlags = 127;
        native.ThermalState = native.VoltageState = 1;
        PrivateProfileStatus report = PrivateProfileStatus.FromNative(native);
        using (JsonDocument json = JsonDocument.Parse(report.ToJson(DateTimeOffset.UnixEpoch)))
        {
            JsonElement root = json.RootElement;
            Check(!root.GetProperty("acquisition_performed").GetBoolean() &&
                root.GetProperty("returned_payload_state").GetString() == "not_evaluated" &&
                root.GetProperty("gsp_state").GetString() == "not_observed" &&
                root.GetProperty("operations")[0].GetProperty("eligible_for_acquisition").GetBoolean() &&
                root.GetProperty("identity_checks").GetArrayLength() == 7 &&
                root.GetProperty("schema_version").GetInt32() == 2 &&
                root.GetProperty("operations")[0].GetProperty("minimum_interval_ms").GetUInt32() == 100 &&
                root.GetProperty("operations")[1].GetProperty("acquisition_timeout_ms").GetUInt32() == 2000 &&
                root.GetProperty("timeout_behavior").GetString() == "discard_late_result_and_block_process",
                "compatibility declares eligibility only, without sensor acquisition or inferred GSP");
        }

        for (int bit = 0; bit < 7; bit++)
        {
            NativePrivateProfileStatus mismatch = native;
            mismatch.IdentityMatchFlags &= ~(1U << bit);
            mismatch.ThermalState = mismatch.VoltageState = 4;
            PrivateProfileStatus rejected = PrivateProfileStatus.FromNative(mismatch);
            Check(rejected.IdentityChecks[bit].State == "mismatch" &&
                !rejected.IsEligibleForAcquisition(rejected.ThermalState), $"identity mismatch {bit}");
            mismatch.IdentityCheckedFlags &= ~(1U << bit);
            mismatch.ThermalState = mismatch.VoltageState = 3;
            rejected = PrivateProfileStatus.FromNative(mismatch);
            Check(rejected.IdentityChecks[bit].State == "unavailable", $"identity unavailable {bit}");
        }

        foreach (uint state in new uint[] { 0, 2, 3, 4, 5, 6, 7, 8, 9, 10, uint.MaxValue })
        {
            NativePrivateProfileStatus unavailable = native;
            unavailable.ThermalState = state;
            PrivateProfileStatus rejected = PrivateProfileStatus.FromNative(unavailable);
            using JsonDocument json = JsonDocument.Parse(rejected.ToJson(DateTimeOffset.UnixEpoch));
            Check(!json.RootElement.GetProperty("operations")[0].GetProperty("eligible_for_acquisition").GetBoolean() &&
                json.RootElement.GetProperty("operations")[1].GetProperty("eligible_for_acquisition").GetBoolean(),
                $"thermal state {state} blocks only the affected operation");
        }

        NativePrivateProfileStatus revoked = native;
        revoked.ProfileState = 2;
        revoked.RevocationReason = "fixture_revoked";
        revoked.ThermalState = revoked.VoltageState = 2;
        report = PrivateProfileStatus.FromNative(revoked);
        Check(report.RevocationReason == "fixture_revoked" &&
            !report.IsEligibleForAcquisition(PrivateOperationState.Compatible), "profile revocation blocks eligibility");
        revoked.ProfileState = uint.MaxValue;
        report = PrivateProfileStatus.FromNative(revoked);
        Check(report.ProfileState == PrivateProfileState.Unknown &&
            !report.IsEligibleForAcquisition(PrivateOperationState.Compatible), "future profile state fails closed");

        NativePrivateProfileStatus[] contradictions = [native, native, native, native];
        contradictions[0].IdentityCheckedFlags = 0;
        contradictions[1].IdentityCheckedFlags = 255;
        contradictions[2].ProfileState = 2;
        contradictions[3].IdentityMatchFlags = 126;
        foreach (NativePrivateProfileStatus contradiction in contradictions)
        {
            bool rejected = false;
            try
            {
                _ = PrivateProfileStatus.FromNative(contradiction);
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }
            Check(rejected, "contradictory native eligibility is rejected before serialization");
        }
        return failures;
    }
}
