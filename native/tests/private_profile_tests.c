#include "private_profile.h"

#include <stdio.h>

static int check(int condition, const char *message)
{
    if (condition) {
        return 0;
    }
    (void)fprintf(stderr, "FAILED: %s\n", message);
    return 1;
}

int main(void)
{
    int failures = 0;
    failures += check(
        rtxmon_private_profile_matches(
            0x10deU, 0x2504U, 0x10deU, 0x1536U,
            "GPU-fca3647e-8390-15a8-f23b-d0f870c9accd",
            "94.06.25.00.FC", "610.88"),
        "exact profile is accepted");
    failures += check(
        !rtxmon_private_profile_matches(
            0x10deU, 0x2504U, 0x10deU, 0x1537U,
            "GPU-fca3647e-8390-15a8-f23b-d0f870c9accd",
            "94.06.25.00.FC", "610.88"),
        "subsystem mismatch fails closed");
    failures += check(
        !rtxmon_private_profile_matches(
            0x10deU, 0x2504U, 0x10deU, 0x1536U,
            "GPU-00000000-0000-0000-0000-000000000000",
            "94.06.25.00.FC", "610.88"),
        "physical GPU UUID mismatch fails closed");
    failures += check(
        !rtxmon_private_profile_matches(
            0x10deU, 0x2504U, 0x10deU, 0x1536U,
            "GPU-FCA3647E-8390-15A8-F23B-D0F870C9ACCD",
            "94.06.25.00.FC", "610.88"),
        "physical GPU UUID must use the exact canonical representation");
    failures += check(
        !rtxmon_private_profile_matches(
            0x10deU, 0x2504U, 0x10deU, 0x1536U,
            "GPU-fca3647e-8390-15a8-f23b-d0f870c9accd",
            "94.06.25.00.FD", "610.88"),
        "VBIOS mismatch fails closed");
    failures += check(
        !rtxmon_private_profile_matches(
            0x10deU, 0x2504U, 0x10deU, 0x1536U,
            "GPU-fca3647e-8390-15a8-f23b-d0f870c9accd",
            "94.06.25.00.FC", "611.00"),
        "driver mismatch fails closed");
    failures += check(
        !rtxmon_private_profile_matches(
            0x10deU, 0x2504U, 0x10deU, 0x1536U,
            NULL, "94.06.25.00.FC", "610.88"),
        "missing identity fails closed");
    failures += check(
        rtxmon_private_thermal_channel_result_valid(0U, 0x00000001U, 40 * 256) &&
            rtxmon_private_thermal_channel_result_valid(1U, 0x00000002U, 50 * 256),
        "thermal channel mask and range accept the reviewed pair");
    failures += check(
        !rtxmon_private_thermal_channel_result_valid(0U, 0x00000002U, 40 * 256) &&
            !rtxmon_private_thermal_channel_result_valid(1U, 0x00000001U, 50 * 256) &&
            !rtxmon_private_thermal_channel_result_valid(2U, 0x00000004U, 50 * 256),
        "thermal channel mask mismatch fails closed");
    failures += check(
        !rtxmon_private_thermal_channel_result_valid(
            0U, 0x00000001U, RTXMON_PRIVATE_THERMAL_MIN_RAW - 1) &&
            !rtxmon_private_thermal_channel_result_valid(
                0U, 0x00000001U, RTXMON_PRIVATE_THERMAL_DIE_MAX_RAW + 1) &&
            !rtxmon_private_thermal_channel_result_valid(
                1U, 0x00000002U, RTXMON_PRIVATE_THERMAL_HOTSPOT_MAX_RAW + 1),
        "thermal channel range mismatch fails closed");
    failures += check(
        rtxmon_private_thermal_pair_valid(40 * 256, 50 * 256) &&
            rtxmon_private_thermal_pair_valid(40 * 256, 40 * 256) &&
            rtxmon_private_thermal_pair_valid(40 * 256, 120 * 256),
        "thermal pair accepts the schema-compatible delta range");
    failures += check(
        !rtxmon_private_thermal_pair_valid(50 * 256, 40 * 256) &&
            !rtxmon_private_thermal_pair_valid(40 * 256, (120 * 256) + 1),
        "thermal pair rejects negative or excessive hotspot delta");
    failures += check(
        rtxmon_private_voltage_microvolts_valid(100000U) &&
            rtxmon_private_voltage_microvolts_valid(2000000U),
        "voltage range includes reviewed boundaries");
    failures += check(
        !rtxmon_private_voltage_microvolts_valid(0U) &&
            !rtxmon_private_voltage_microvolts_valid(99999U) &&
            !rtxmon_private_voltage_microvolts_valid(2000001U),
        "voltage range rejects zero and out-of-profile values");
    return failures == 0 ? 0 : 1;
}
