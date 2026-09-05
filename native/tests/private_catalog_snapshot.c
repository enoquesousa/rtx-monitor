#include <rtxmon/rtxmon.h>

#include "nvapi_abi.h"
#include "private_profile.h"
#include "private_profile_catalog.h"

#include <stddef.h>
#include <stdio.h>

/* Offline audit producer. Link only private_profile_catalog.c, never a loader,
 * monitoring context, driver library or GPU-facing implementation. Layouts come
 * from the same declarations as acquisition; the output is platform-independent
 * for the supported 64-bit builds and contains no live measurements.
 */
static void print_json_string(const char *value)
{
    const unsigned char *cursor = (const unsigned char *)value;
    (void)putchar('"');
    while (*cursor != 0U) {
        if (*cursor == '"' || *cursor == '\\') {
            (void)putchar('\\');
            (void)putchar(*cursor);
        } else if (*cursor < 32U) {
            (void)printf("\\u%04x", (unsigned int)*cursor);
        } else {
            (void)putchar(*cursor);
        }
        ++cursor;
    }
    (void)putchar('"');
}

static void print_operation(
    const char *name,
    const rtxmon_private_operation_policy_t *policy,
    size_t size,
    size_t version_offset,
    const size_t *value_offsets,
    size_t value_count,
    size_t value_width,
    int signed_values,
    uint32_t scale_divisor)
{
    size_t index;
    (void)printf("{\"operation\":");
    print_json_string(name);
    (void)printf(",\"revoked\":%s,\"revocation_reason\":", policy->revoked ? "true" : "false");
    print_json_string(policy->revocation_reason);
    (void)printf(",\"interface_id\":\"0x%08x\",\"function_rva\":\"0x%08x\","
        "\"structure_version\":\"0x%08x\",\"structure_size_bytes\":%zu,"
        "\"version_offset_bytes\":%zu,\"value_offsets_bytes\":[",
        (unsigned int)policy->interface_id, (unsigned int)policy->function_rva,
        (unsigned int)policy->structure_version, size, version_offset);
    for (index = 0U; index < value_count; ++index) {
        (void)printf("%s%zu", index == 0U ? "" : ",", value_offsets[index]);
    }
    (void)printf("],\"value_width_bytes\":%zu,\"signed_values\":%s,"
        "\"scale_divisor\":%u,\"minimum_interval_ms\":%u,\"timeout_ms\":%u}",
        value_width, signed_values ? "true" : "false", (unsigned int)scale_divisor,
        (unsigned int)policy->min_interval_ms, (unsigned int)policy->timeout_ms);
}

int main(void)
{
    const rtxmon_private_profile_catalog_t *profile = rtxmon_private_catalog_get();
    const size_t thermal_offsets[] = {
        offsetof(rtxmon_nvapi_therm_channel_status_v2_t, words) + 8U * sizeof(uint32_t),
        offsetof(rtxmon_nvapi_therm_channel_status_v2_t, words) + 9U * sizeof(uint32_t)
    };
    const size_t voltage_offsets[] = {
        offsetof(rtxmon_nvapi_voltage_status_v1_t, words) + 9U * sizeof(uint32_t)
    };
    (void)printf("{\"schema_version\":1,\"snapshot_kind\":\"compiled_private_catalog\","
        "\"profile_count\":1,\"abi_version\":%u,\"pointer_size_bytes\":%zu,"
        "\"acquisition_platform\":\"windows_x64\",\"profile_id\":",
        (unsigned int)RTXMON_ABI_VERSION, sizeof(void *));
    print_json_string(profile->profile_id);
    (void)printf(",\"profile_revision\":%u,\"revoked\":%s,\"revocation_reason\":",
        (unsigned int)profile->revision, profile->revoked ? "true" : "false");
    print_json_string(profile->revocation_reason);
    (void)printf(",\"identity\":{\"pci_vendor_id\":\"0x%04x\","
        "\"pci_device_id\":\"0x%04x\",\"pci_subsystem_vendor_id\":\"0x%04x\","
        "\"pci_subsystem_device_id\":\"0x%04x\",\"gpu_uuid\":",
        (unsigned int)profile->vendor_id, (unsigned int)profile->device_id,
        (unsigned int)profile->subsystem_vendor_id, (unsigned int)profile->subsystem_device_id);
    print_json_string(profile->uuid);
    (void)printf(",\"vbios_version\":");
    print_json_string(profile->vbios);
    (void)printf(",\"driver_version\":");
    print_json_string(profile->driver);
    (void)printf("},\"module_sha256\":");
    print_json_string(profile->module_sha256);
    (void)printf(",\"gsp\":{\"state\":\"not_observed\",\"version\":null},"
        "\"thermal_channel_mask_offset_bytes\":%zu,\"thermal_channel_masks\":[1,2],"
        "\"bounds\":{\"thermal_min_raw\":%d,\"thermal_die_max_raw\":%d,"
        "\"thermal_hotspot_max_raw\":%d,\"thermal_max_delta_raw\":%d,"
        "\"voltage_min_microvolts\":%u,\"voltage_max_microvolts\":%u},\"operations\":[",
        offsetof(rtxmon_nvapi_therm_channel_status_v2_t, channel_mask),
        RTXMON_PRIVATE_THERMAL_MIN_RAW, RTXMON_PRIVATE_THERMAL_DIE_MAX_RAW,
        RTXMON_PRIVATE_THERMAL_HOTSPOT_MAX_RAW, RTXMON_PRIVATE_THERMAL_MAX_DELTA_RAW,
        RTXMON_PRIVATE_VOLTAGE_MIN_MICROVOLTS, RTXMON_PRIVATE_VOLTAGE_MAX_MICROVOLTS);
    print_operation("thermal", &profile->thermal, sizeof(rtxmon_nvapi_therm_channel_status_v2_t),
        offsetof(rtxmon_nvapi_therm_channel_status_v2_t, version), thermal_offsets, 2U,
        sizeof(((rtxmon_nvapi_therm_channel_status_v2_t *)0)->words[0]), 1, 256U);
    (void)putchar(',');
    print_operation("voltage", &profile->voltage, sizeof(rtxmon_nvapi_voltage_status_v1_t),
        offsetof(rtxmon_nvapi_voltage_status_v1_t, version), voltage_offsets, 1U,
        sizeof(((rtxmon_nvapi_voltage_status_v1_t *)0)->words[0]), 0, 1000000U);
    (void)printf("]}\n");
    return ferror(stdout) ? 1 : 0;
}
