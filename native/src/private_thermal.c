#include "rtxmon_internal.h"
#include "private_profile.h"

#include <limits.h>
#include <string.h>

#if defined(_MSC_VER)
#define RTXMON_PRIVATE_THERM_STATIC_ASSERT(condition, message) static_assert(condition, message)
#else
#define RTXMON_PRIVATE_THERM_STATIC_ASSERT(condition, message) _Static_assert(condition, message)
#endif

RTXMON_PRIVATE_THERM_STATIC_ASSERT(
    sizeof(rtxmon_nvapi_therm_channel_status_v2_t) == 168U,
    "private NVAPI thermal channel ABI changed");

static int profile_matches(
    rtxmon_context_t *context,
    nvmlDevice_t device,
    const rtxmon_nvml_pci_info_t *pci)
{
    char driver[RTXMON_TEXT_CAPACITY] = {0};
    char uuid[RTXMON_TEXT_CAPACITY] = {0};
    char vbios[RTXMON_TEXT_CAPACITY] = {0};
    nvmlReturn_t result;
    const uint32_t vendor_id = pci->pci_device_id & 0xffffU;
    const uint32_t device_id = (pci->pci_device_id >> 16U) & 0xffffU;
    const uint32_t subsystem_vendor_id = pci->pci_subsystem_id & 0xffffU;
    const uint32_t subsystem_device_id = (pci->pci_subsystem_id >> 16U) & 0xffffU;

    if (context->nvml.device_get_uuid == NULL ||
        context->nvml.system_get_driver_version == NULL ||
        context->nvml.device_get_vbios_version == NULL) {
        return 0;
    }
    result = context->nvml.device_get_uuid(device, uuid, (uint32_t)sizeof(uuid));
    if (result != NVML_SUCCESS) {
        return 0;
    }
    result = context->nvml.system_get_driver_version(driver, (uint32_t)sizeof(driver));
    if (result != NVML_SUCCESS) {
        return 0;
    }
    result = context->nvml.device_get_vbios_version(device, vbios, (uint32_t)sizeof(vbios));
    if (result != NVML_SUCCESS) {
        return 0;
    }
    uuid[sizeof(uuid) - 1U] = '\0';
    driver[sizeof(driver) - 1U] = '\0';
    vbios[sizeof(vbios) - 1U] = '\0';
    return rtxmon_private_profile_matches(
        vendor_id,
        device_id,
        subsystem_vendor_id,
        subsystem_device_id,
        uuid,
        vbios,
        driver);
}

static rtxmon_status_t map_nvapi_status(rtxmon_nvapi_status_t status)
{
    if (status == RTXMON_NVAPI_NOT_SUPPORTED ||
        status == RTXMON_NVAPI_NO_IMPLEMENTATION ||
        status == RTXMON_NVAPI_INCOMPATIBLE_STRUCT_VERSION) {
        return RTXMON_STATUS_NOT_SUPPORTED;
    }
    if (status == RTXMON_NVAPI_NVIDIA_DEVICE_NOT_FOUND) {
        return RTXMON_STATUS_GPU_NOT_FOUND;
    }
    return RTXMON_STATUS_BACKEND_ERROR;
}

static rtxmon_nvapi_status_t find_gpu(
    rtxmon_context_t *context,
    const rtxmon_nvml_pci_info_t *pci,
    rtxmon_nvapi_gpu_handle_t *out_handle)
{
    rtxmon_nvapi_gpu_handle_t handles[RTXMON_NVAPI_MAX_PHYSICAL_GPUS] = {0};
    uint32_t count = 0U;
    uint32_t matches = 0U;
    uint32_t i;
    rtxmon_nvapi_status_t result = context->nvapi.enum_physical_gpus(handles, &count);

    *out_handle = NULL;
    if (result != RTXMON_NVAPI_OK || count > RTXMON_NVAPI_MAX_PHYSICAL_GPUS) {
        return result != RTXMON_NVAPI_OK ? result : RTXMON_NVAPI_ERROR;
    }
    for (i = 0U; i < count; ++i) {
        uint32_t bus = 0U, slot = 0U, device = 0U, subsystem = 0U, revision = 0U, extended = 0U;
        if (context->nvapi.gpu_get_bus_id(handles[i], &bus) != RTXMON_NVAPI_OK ||
            context->nvapi.gpu_get_bus_slot_id(handles[i], &slot) != RTXMON_NVAPI_OK ||
            context->nvapi.gpu_get_pci_identifiers(
                handles[i], &device, &subsystem, &revision, &extended) != RTXMON_NVAPI_OK) {
            continue;
        }
        if (bus == pci->bus && slot == pci->device && device == pci->pci_device_id &&
            subsystem == pci->pci_subsystem_id) {
            *out_handle = handles[i];
            ++matches;
        }
    }
    return matches == 1U ? RTXMON_NVAPI_OK : RTXMON_NVAPI_NVIDIA_DEVICE_NOT_FOUND;
}

static int32_t fixed8_to_millic(int32_t raw)
{
    int64_t scaled = (int64_t)raw * 1000;
    scaled += scaled >= 0 ? 128 : -128;
    return (int32_t)(scaled / 256);
}

rtxmon_status_t RTXMON_CALL rtxmon_read_private_thermal_channels(
    rtxmon_context_t *context,
    uint32_t gpu_index,
    rtxmon_private_thermal_sample_t *out_sample)
{
    rtxmon_private_thermal_sample_t sample;
    rtxmon_nvapi_gpu_handle_t handle = NULL;
    rtxmon_nvml_pci_info_t pci = {0};
    nvmlDevice_t device = NULL;
    nvmlReturn_t nvml_result;
    rtxmon_nvapi_status_t result;
    int32_t raw_values[2] = {0, 0};
    int32_t millic_values[2] = {0, 0};
    uint32_t channel;

    if (context == NULL || out_sample == NULL) {
        return RTXMON_STATUS_INVALID_ARGUMENT;
    }
    if (out_sample->struct_size < sizeof(*out_sample)) {
        return RTXMON_STATUS_ABI_MISMATCH;
    }
    if (context->nvapi_initialized == 0 || context->nvapi.gpu_therm_channel_get_status == NULL) {
        return RTXMON_STATUS_NOT_SUPPORTED;
    }
    nvml_result = context->nvml.device_get_handle_by_index_v2(gpu_index, &device);
    if (nvml_result != NVML_SUCCESS) {
        return nvml_result == NVML_ERROR_NOT_FOUND ? RTXMON_STATUS_GPU_NOT_FOUND : RTXMON_STATUS_BACKEND_ERROR;
    }
    nvml_result = rtxmon_get_pci_info_internal(context, device, &pci);
    if (nvml_result != NVML_SUCCESS) {
        return RTXMON_STATUS_BACKEND_ERROR;
    }

    (void)memset(&sample, 0, sizeof(sample));
    sample.struct_size = (uint32_t)sizeof(sample);
    sample.gpu_index = gpu_index;
    if (!profile_matches(context, device, &pci)) {
        sample.native_status = RTXMON_NVAPI_NOT_SUPPORTED;
        sample.timestamp_unix_ms = rtxmon_timestamp_unix_ms_internal();
        *out_sample = sample;
        return RTXMON_STATUS_NOT_SUPPORTED;
    }

    rtxmon_lock_nvapi_internal();
    result = find_gpu(context, &pci, &handle);
    if (result == RTXMON_NVAPI_OK) {
        for (channel = 0U; channel < 2U; ++channel) {
            rtxmon_nvapi_therm_channel_status_v2_t status;
            int32_t raw;
            (void)memset(&status, 0, sizeof(status));
            status.version = RTXMON_NVAPI_THERM_CHANNEL_STATUS_V2_VERSION;
            status.channel_mask = 1U << channel;
            result = context->nvapi.gpu_therm_channel_get_status(handle, &status);
            if (result == RTXMON_NVAPI_OK &&
                status.version != RTXMON_NVAPI_THERM_CHANNEL_STATUS_V2_VERSION) {
                result = RTXMON_NVAPI_INCOMPATIBLE_STRUCT_VERSION;
            }
            if (result != RTXMON_NVAPI_OK) {
                break;
            }
            raw = (int32_t)status.words[8U + channel]; /* absolute words 10 and 11 */
            if (!rtxmon_private_thermal_channel_result_valid(
                    channel,
                    status.channel_mask,
                    raw)) {
                result = RTXMON_NVAPI_ERROR;
                break;
            }
            raw_values[channel] = raw;
        }
        if (result == RTXMON_NVAPI_OK &&
            !rtxmon_private_thermal_pair_valid(raw_values[0], raw_values[1])) {
            result = RTXMON_NVAPI_ERROR;
        }
        if (result == RTXMON_NVAPI_OK) {
            millic_values[0] = fixed8_to_millic(raw_values[0]);
            millic_values[1] = fixed8_to_millic(raw_values[1]);
            if (millic_values[1] < millic_values[0] ||
                millic_values[1] - millic_values[0] > 80000) {
                result = RTXMON_NVAPI_ERROR;
            }
        }
        if (result == RTXMON_NVAPI_OK) {
            sample.gpu_die_temperature_millic = millic_values[0];
            sample.gpu_hotspot_temperature_millic = millic_values[1];
            sample.value_flags = RTXMON_PRIVATE_THERMAL_DIE_VALID |
                RTXMON_PRIVATE_THERMAL_HOTSPOT_VALID;
        }
    }
    rtxmon_unlock_nvapi_internal();

    sample.native_status = result;
    sample.timestamp_unix_ms = rtxmon_timestamp_unix_ms_internal();
    *out_sample = sample;
    return result == RTXMON_NVAPI_OK ? RTXMON_STATUS_OK : map_nvapi_status(result);
}
