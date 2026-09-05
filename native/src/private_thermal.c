#include "rtxmon_internal.h"
#include "private_profile.h"
#include "private_profile_gate.h"

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
    rtxmon_private_profile_report_t report = {0};
    rtxmon_private_acquisition_t acquisition;
    const rtxmon_private_profile_catalog_t *profile = rtxmon_private_catalog_get();
    const rtxmon_private_operation_policy_t *policy = &profile->thermal;
    rtxmon_status_t gate_status;
    rtxmon_nvapi_status_t result;
    int32_t raw_values[2] = {0, 0};
    int32_t millic_values[2] = {0, 0};
    uint32_t channel;

    if (out_sample == NULL) {
        return RTXMON_STATUS_INVALID_ARGUMENT;
    }
    if (out_sample->struct_size < sizeof(*out_sample)) {
        return RTXMON_STATUS_ABI_MISMATCH;
    }
    (void)memset(&sample, 0, sizeof(sample));
    sample.struct_size = (uint32_t)sizeof(sample);
    sample.gpu_index = gpu_index;
    sample.native_status = RTXMON_NVAPI_INVALID_ARGUMENT;
    *out_sample = sample;

    if (context == NULL || context->initialized == 0) {
        return RTXMON_STATUS_INVALID_ARGUMENT;
    }
    if (profile->revoked != 0U || policy->revoked != 0U) {
        sample.native_status = RTXMON_NVAPI_NOT_SUPPORTED;
        sample.timestamp_unix_ms = rtxmon_timestamp_unix_ms_internal();
        *out_sample = sample;
        return RTXMON_STATUS_NOT_SUPPORTED;
    }
    gate_status = rtxmon_private_acquisition_begin_internal(&acquisition, policy);
    if (gate_status == RTXMON_STATUS_OK) {
        gate_status = rtxmon_private_profile_evaluate_internal(context, gpu_index, &report, &handle, &acquisition);
    }
    if (gate_status == RTXMON_STATUS_OK) {
        gate_status = rtxmon_private_operation_status_internal(report.thermal_state, &sample.native_status);
    }
    if (gate_status == RTXMON_STATUS_OK) {
        gate_status = rtxmon_private_acquisition_admit_internal(&acquisition, RTXMON_PRIVATE_THERMAL, policy);
    }
    if (gate_status != RTXMON_STATUS_OK) {
        goto finish;
    }
    result = RTXMON_NVAPI_OK;
    if (result == RTXMON_NVAPI_OK) {
        for (channel = 0U; channel < 2U; ++channel) {
            rtxmon_nvapi_therm_channel_status_v2_t status;
            int32_t raw;
            (void)memset(&status, 0, sizeof(status));
            status.version = policy->structure_version;
            status.channel_mask = 1U << channel;
            gate_status = rtxmon_private_acquisition_check_internal(&acquisition);
            if (gate_status != RTXMON_STATUS_OK) {
                goto finish;
            }
            result = context->nvapi.gpu_therm_channel_get_status(handle, &status);
            gate_status = rtxmon_private_acquisition_check_internal(&acquisition);
            if (gate_status != RTXMON_STATUS_OK) {
                goto finish;
            }
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
    sample.native_status = result;
    gate_status = result == RTXMON_NVAPI_OK ? RTXMON_STATUS_OK : map_nvapi_status(result);
finish:
    sample.timestamp_unix_ms = rtxmon_timestamp_unix_ms_internal();
    if (rtxmon_private_acquisition_check_internal(&acquisition) != RTXMON_STATUS_OK) {
        gate_status = RTXMON_STATUS_TIMEOUT;
    }
    if (gate_status == RTXMON_STATUS_TIMEOUT || gate_status == RTXMON_STATUS_RATE_LIMITED) {
        sample.native_status = RTXMON_NVAPI_ERROR;
        sample.value_flags = 0U;
        sample.gpu_die_temperature_millic = 0;
        sample.gpu_hotspot_temperature_millic = 0;
    }
    rtxmon_private_acquisition_end_internal(&acquisition);
    *out_sample = sample;
    return gate_status;
}
