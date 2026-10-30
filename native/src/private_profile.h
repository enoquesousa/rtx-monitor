#ifndef RTXMON_PRIVATE_PROFILE_H
#define RTXMON_PRIVATE_PROFILE_H

#include <ctype.h>
#include <stdint.h>
#include <string.h>
#include "private_profile_catalog.h"
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
    const rtxmon_private_profile_catalog_t *profile = rtxmon_private_catalog_get();
    return vendor_id == profile->vendor_id && device_id == profile->device_id &&
        subsystem_vendor_id == profile->subsystem_vendor_id &&
        subsystem_device_id == profile->subsystem_device_id &&
        uuid != NULL && strcmp(uuid, profile->uuid) == 0 &&
        driver != NULL && strcmp(driver, profile->driver) == 0 &&
        rtxmon_private_ascii_equal_ignore_case(vbios, profile->vbios);
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
