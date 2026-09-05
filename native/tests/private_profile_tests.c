#include "private_profile.h"
#include "nvapi_abi.h"

#include <stddef.h>
#include <stdio.h>

#if defined(_MSC_VER)
#define RTXMON_LAYOUT_ASSERT(condition, message) static_assert(condition, message)
#else
#define RTXMON_LAYOUT_ASSERT(condition, message) _Static_assert(condition, message)
#endif

/* Compile-time contracts for the two existing private payloads only. These
 * fixtures establish byte layout, not driver behavior or sensor support.
 */
RTXMON_LAYOUT_ASSERT(sizeof(rtxmon_nvapi_therm_channel_status_v2_t) == 168U, "thermal payload size");
RTXMON_LAYOUT_ASSERT(offsetof(rtxmon_nvapi_therm_channel_status_v2_t, version) == 0U, "thermal version offset");
RTXMON_LAYOUT_ASSERT(sizeof(((rtxmon_nvapi_therm_channel_status_v2_t *)0)->version) == 4U, "thermal version width");
RTXMON_LAYOUT_ASSERT(offsetof(rtxmon_nvapi_therm_channel_status_v2_t, channel_mask) == 4U, "thermal mask offset");
RTXMON_LAYOUT_ASSERT(sizeof(((rtxmon_nvapi_therm_channel_status_v2_t *)0)->channel_mask) == 4U, "thermal mask width");
RTXMON_LAYOUT_ASSERT(offsetof(rtxmon_nvapi_therm_channel_status_v2_t, words) == 8U, "thermal words offset");
RTXMON_LAYOUT_ASSERT(sizeof(((rtxmon_nvapi_therm_channel_status_v2_t *)0)->words) == 160U, "thermal words width");
RTXMON_LAYOUT_ASSERT(sizeof(((rtxmon_nvapi_therm_channel_status_v2_t *)0)->words[0]) == 4U, "thermal word width");
RTXMON_LAYOUT_ASSERT(offsetof(rtxmon_nvapi_therm_channel_status_v2_t, words) + 8U * sizeof(uint32_t) == 40U,
    "thermal die reviewed byte offset");
RTXMON_LAYOUT_ASSERT(offsetof(rtxmon_nvapi_therm_channel_status_v2_t, words) + 9U * sizeof(uint32_t) == 44U,
    "thermal hotspot reviewed byte offset");
RTXMON_LAYOUT_ASSERT(RTXMON_NVAPI_THERM_CHANNEL_STATUS_V2_VERSION == 0x000200a8U, "thermal exact version");
RTXMON_LAYOUT_ASSERT((RTXMON_NVAPI_THERM_CHANNEL_STATUS_V2_VERSION & 0xffffU) == sizeof(rtxmon_nvapi_therm_channel_status_v2_t),
    "thermal version encodes payload size");
RTXMON_LAYOUT_ASSERT((RTXMON_NVAPI_THERM_CHANNEL_STATUS_V2_VERSION >> 16U) == 2U, "thermal version number");
RTXMON_LAYOUT_ASSERT(sizeof(rtxmon_nvapi_voltage_status_v1_t) == 76U, "voltage payload size");
RTXMON_LAYOUT_ASSERT(offsetof(rtxmon_nvapi_voltage_status_v1_t, version) == 0U, "voltage version offset");
RTXMON_LAYOUT_ASSERT(sizeof(((rtxmon_nvapi_voltage_status_v1_t *)0)->version) == 4U, "voltage version width");
RTXMON_LAYOUT_ASSERT(offsetof(rtxmon_nvapi_voltage_status_v1_t, words) == 4U, "voltage words offset");
RTXMON_LAYOUT_ASSERT(sizeof(((rtxmon_nvapi_voltage_status_v1_t *)0)->words) == 72U, "voltage words width");
RTXMON_LAYOUT_ASSERT(sizeof(((rtxmon_nvapi_voltage_status_v1_t *)0)->words[0]) == 4U, "voltage word width");
RTXMON_LAYOUT_ASSERT(offsetof(rtxmon_nvapi_voltage_status_v1_t, words) + 9U * sizeof(uint32_t) == 40U,
    "voltage value reviewed byte offset");
RTXMON_LAYOUT_ASSERT(RTXMON_NVAPI_VOLTAGE_STATUS_V1_VERSION == 0x0001004cU, "voltage exact version");
RTXMON_LAYOUT_ASSERT((RTXMON_NVAPI_VOLTAGE_STATUS_V1_VERSION & 0xffffU) == sizeof(rtxmon_nvapi_voltage_status_v1_t),
    "voltage version encodes payload size");
RTXMON_LAYOUT_ASSERT((RTXMON_NVAPI_VOLTAGE_STATUS_V1_VERSION >> 16U) == 1U, "voltage version number");

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
