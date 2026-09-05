#include "rtxmon_internal.h"
#include "private_profile.h"
#include "private_profile_gate.h"

#include <string.h>

#if defined(_MSC_VER)
#define RTXMON_PRIVATE_VOLTAGE_STATIC_ASSERT(condition, message) static_assert(condition, message)
#else
#define RTXMON_PRIVATE_VOLTAGE_STATIC_ASSERT(condition, message) _Static_assert(condition, message)
#endif

#define RTXMON_VOLTAGE_VALUE_WORD_INDEX 10U

RTXMON_PRIVATE_VOLTAGE_STATIC_ASSERT(
    sizeof(rtxmon_nvapi_voltage_status_v1_t) == 76U,
    "private NVAPI voltage status ABI changed");

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

rtxmon_status_t RTXMON_CALL rtxmon_read_private_voltage_status(
    rtxmon_context_t *context,
    uint32_t gpu_index,
    rtxmon_private_voltage_sample_t *out_sample)
{
    rtxmon_private_voltage_sample_t sample;
    rtxmon_nvapi_voltage_status_v1_t status;
    rtxmon_nvapi_gpu_handle_t handle = NULL;
    rtxmon_private_profile_report_t report = {0};
    rtxmon_private_acquisition_t acquisition;
    const rtxmon_private_profile_catalog_t *profile = rtxmon_private_catalog_get();
    const rtxmon_private_operation_policy_t *policy = &profile->voltage;
    rtxmon_status_t gate_status;
    rtxmon_nvapi_status_t result;
    uint32_t raw_microvolts;

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
        gate_status = rtxmon_private_operation_status_internal(report.voltage_state, &sample.native_status);
    }
    if (gate_status == RTXMON_STATUS_OK) {
        gate_status = rtxmon_private_acquisition_admit_internal(&acquisition, RTXMON_PRIVATE_VOLTAGE, policy);
    }
    if (gate_status != RTXMON_STATUS_OK) {
        goto finish;
    }
    result = RTXMON_NVAPI_OK;
    if (result == RTXMON_NVAPI_OK) {
        (void)memset(&status, 0, sizeof(status));
        status.version = policy->structure_version;
        gate_status = rtxmon_private_acquisition_check_internal(&acquisition);
        if (gate_status != RTXMON_STATUS_OK) {
            goto finish;
        }
        result = context->nvapi.gpu_voltage_status(handle, &status);
        gate_status = rtxmon_private_acquisition_check_internal(&acquisition);
        if (gate_status != RTXMON_STATUS_OK) {
            goto finish;
        }
        if (result == RTXMON_NVAPI_OK && status.version != RTXMON_NVAPI_VOLTAGE_STATUS_V1_VERSION) {
            result = RTXMON_NVAPI_INCOMPATIBLE_STRUCT_VERSION;
        }
        raw_microvolts = status.words[RTXMON_VOLTAGE_VALUE_WORD_INDEX - 1U];
        if (result == RTXMON_NVAPI_OK &&
            !rtxmon_private_voltage_microvolts_valid(raw_microvolts)) {
            result = RTXMON_NVAPI_ERROR;
        }
        if (result == RTXMON_NVAPI_OK) {
            sample.gpu_core_voltage_microvolts = raw_microvolts;
            sample.value_flags |= RTXMON_PRIVATE_VOLTAGE_CORE_VALID;
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
        sample.gpu_core_voltage_microvolts = 0U;
    }
    rtxmon_private_acquisition_end_internal(&acquisition);
    *out_sample = sample;
    return gate_status;
}
