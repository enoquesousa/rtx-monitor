#ifndef RTXMON_PRIVATE_PROFILE_H
#define RTXMON_PRIVATE_PROFILE_H

#include <ctype.h>
#include <stdint.h>
#include <string.h>

#define RTXMON_PRIVATE_PROFILE_VENDOR_ID 0x10deU
#define RTXMON_PRIVATE_PROFILE_DEVICE_ID 0x2504U
#define RTXMON_PRIVATE_PROFILE_SUBSYSTEM_VENDOR_ID 0x10deU
#define RTXMON_PRIVATE_PROFILE_SUBSYSTEM_DEVICE_ID 0x1536U
#define RTXMON_PRIVATE_PROFILE_UUID "GPU-fca3647e-8390-15a8-f23b-d0f870c9accd"
#define RTXMON_PRIVATE_PROFILE_VBIOS "94.06.25.00.fc"
#define RTXMON_PRIVATE_PROFILE_DRIVER "610.88"
#define RTXMON_PRIVATE_THERMAL_MIN_RAW (-40 * 256)
#define RTXMON_PRIVATE_THERMAL_DIE_MAX_RAW (125 * 256)
#define RTXMON_PRIVATE_THERMAL_HOTSPOT_MAX_RAW (150 * 256)
#define RTXMON_PRIVATE_THERMAL_MAX_DELTA_RAW (80 * 256)
#define RTXMON_PRIVATE_VOLTAGE_MIN_MICROVOLTS 100000U
#define RTXMON_PRIVATE_VOLTAGE_MAX_MICROVOLTS 2000000U

static inline int rtxmon_private_ascii_equal_ignore_case(const char *left, const char *right)
{
    if (left == NULL || right == NULL) {
        return 0;
    }
    while (*left != '\0' && *right != '\0') {
        if (tolower((unsigned char)*left) != tolower((unsigned char)*right)) {
            return 0;
        }
        ++left;
        ++right;
    }
    return *left == '\0' && *right == '\0';
}

static inline int rtxmon_private_profile_matches(
    uint32_t vendor_id,
    uint32_t device_id,
    uint32_t subsystem_vendor_id,
    uint32_t subsystem_device_id,
    const char *uuid,
    const char *vbios,
    const char *driver)
{
    return vendor_id == RTXMON_PRIVATE_PROFILE_VENDOR_ID &&
        device_id == RTXMON_PRIVATE_PROFILE_DEVICE_ID &&
        subsystem_vendor_id == RTXMON_PRIVATE_PROFILE_SUBSYSTEM_VENDOR_ID &&
        subsystem_device_id == RTXMON_PRIVATE_PROFILE_SUBSYSTEM_DEVICE_ID &&
        uuid != NULL && strcmp(uuid, RTXMON_PRIVATE_PROFILE_UUID) == 0 &&
        driver != NULL && strcmp(driver, RTXMON_PRIVATE_PROFILE_DRIVER) == 0 &&
        rtxmon_private_ascii_equal_ignore_case(vbios, RTXMON_PRIVATE_PROFILE_VBIOS);
}

static inline int rtxmon_private_thermal_channel_result_valid(
    uint32_t channel,
    uint32_t channel_mask,
    int32_t raw)
{
    const int32_t maximum = channel == 0U
        ? RTXMON_PRIVATE_THERMAL_DIE_MAX_RAW
        : RTXMON_PRIVATE_THERMAL_HOTSPOT_MAX_RAW;
    return channel < 2U && channel_mask == (1U << channel) &&
        raw >= RTXMON_PRIVATE_THERMAL_MIN_RAW && raw <= maximum;
}

static inline int rtxmon_private_thermal_pair_valid(int32_t die_raw, int32_t hotspot_raw)
{
    const int32_t delta = hotspot_raw - die_raw;
    return delta >= 0 && delta <= RTXMON_PRIVATE_THERMAL_MAX_DELTA_RAW;
}

static inline int rtxmon_private_voltage_microvolts_valid(uint32_t value)
{
    return value >= RTXMON_PRIVATE_VOLTAGE_MIN_MICROVOLTS &&
        value <= RTXMON_PRIVATE_VOLTAGE_MAX_MICROVOLTS;
}

#endif
