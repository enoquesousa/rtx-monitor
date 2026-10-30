#include "private_profile_gate.h"
#include "private_profile.h"

#include <stdio.h>
#include <string.h>

#define RTXMON_PRIVATE_IDENTITY_ALL 127U

static void set_pending_states(rtxmon_private_profile_report_t *report, uint32_t state)
{
    if (report->thermal_state == RTXMON_PRIVATE_OPERATION_UNKNOWN) {
        report->thermal_state = state;
    }
    if (report->voltage_state == RTXMON_PRIVATE_OPERATION_UNKNOWN) {
        report->voltage_state = state;
    }
}

static uint32_t identity_query_state(nvmlReturn_t status)
{
    return status == NVML_ERROR_FUNCTION_NOT_FOUND || status == NVML_ERROR_NOT_SUPPORTED
        ? RTXMON_PRIVATE_OPERATION_IDENTITY_UNAVAILABLE
        : RTXMON_PRIVATE_OPERATION_QUERY_FAILED;
}

static void record_identity(
    rtxmon_private_profile_report_t *report, uint32_t flag, int matches)
{
    report->identity_checked_flags |= flag;
    if (matches != 0) {
        report->identity_match_flags |= flag;
    }
}

static uint32_t find_unique_gpu(
    rtxmon_context_t *context,
    const rtxmon_nvml_pci_info_t *pci,
    rtxmon_nvapi_gpu_handle_t *out_handle,
    const rtxmon_private_acquisition_t *acquisition)
{
    rtxmon_nvapi_gpu_handle_t handles[RTXMON_NVAPI_MAX_PHYSICAL_GPUS] = {0};
    rtxmon_nvapi_gpu_handle_t candidate = NULL;
    uint32_t count = 0U, matches = 0U, i;
    int query_failed = 0;
    rtxmon_nvapi_status_t result;
    *out_handle = NULL;
    if (context->nvapi.enum_physical_gpus == NULL || context->nvapi.gpu_get_bus_id == NULL ||
        context->nvapi.gpu_get_bus_slot_id == NULL || context->nvapi.gpu_get_pci_identifiers == NULL) {
        return RTXMON_PRIVATE_OPERATION_MODULE_UNAVAILABLE;
    }
    if (rtxmon_private_acquisition_check_internal(acquisition) != RTXMON_STATUS_OK) {
        return RTXMON_PRIVATE_OPERATION_TIMEOUT;
    }
    result = context->nvapi.enum_physical_gpus(handles, &count);
    if (rtxmon_private_acquisition_check_internal(acquisition) != RTXMON_STATUS_OK) {
        return RTXMON_PRIVATE_OPERATION_TIMEOUT;
    }
    if (result != RTXMON_NVAPI_OK || count > RTXMON_NVAPI_MAX_PHYSICAL_GPUS) {
        return RTXMON_PRIVATE_OPERATION_QUERY_FAILED;
    }
    for (i = 0U; i < count; ++i) {
        uint32_t bus = 0U, slot = 0U, device = 0U, subsystem = 0U, revision = 0U, extended = 0U;
        if (rtxmon_private_acquisition_check_internal(acquisition) != RTXMON_STATUS_OK) {
            return RTXMON_PRIVATE_OPERATION_TIMEOUT;
        }
        result = handles[i] != NULL
            ? context->nvapi.gpu_get_bus_id(handles[i], &bus) : RTXMON_NVAPI_INVALID_ARGUMENT;
        if (rtxmon_private_acquisition_check_internal(acquisition) != RTXMON_STATUS_OK) {
            return RTXMON_PRIVATE_OPERATION_TIMEOUT;
        }
        if (result == RTXMON_NVAPI_OK) {
            result = context->nvapi.gpu_get_bus_slot_id(handles[i], &slot);
            if (rtxmon_private_acquisition_check_internal(acquisition) != RTXMON_STATUS_OK) {
                return RTXMON_PRIVATE_OPERATION_TIMEOUT;
            }
        }
        if (result == RTXMON_NVAPI_OK) {
            result = context->nvapi.gpu_get_pci_identifiers(
                handles[i], &device, &subsystem, &revision, &extended);
            if (rtxmon_private_acquisition_check_internal(acquisition) != RTXMON_STATUS_OK) {
                return RTXMON_PRIVATE_OPERATION_TIMEOUT;
            }
        }
        if (result != RTXMON_NVAPI_OK) {
            query_failed = 1;
            continue;
        }
        if (bus == pci->bus && slot == pci->device && device == pci->pci_device_id &&
            subsystem == pci->pci_subsystem_id) {
            candidate = handles[i];
            ++matches;
        }
    }
    if (matches > 1U) {
        return RTXMON_PRIVATE_OPERATION_IDENTITY_AMBIGUOUS;
    }
    /* A failed lookup can conceal another matching GPU, so uniqueness is unproven. */
    if (query_failed != 0) {
        return RTXMON_PRIVATE_OPERATION_QUERY_FAILED;
    }
    if (matches == 0U) {
        return RTXMON_PRIVATE_OPERATION_GPU_NOT_FOUND;
    }
    *out_handle = candidate;
    return RTXMON_PRIVATE_OPERATION_COMPATIBLE;
}

rtxmon_status_t rtxmon_private_profile_evaluate_internal(
    rtxmon_context_t *context,
    uint32_t gpu_index,
    rtxmon_private_profile_report_t *report,
    rtxmon_nvapi_gpu_handle_t *out_handle,
    const rtxmon_private_acquisition_t *acquisition)
{
    const rtxmon_private_profile_catalog_t *profile = rtxmon_private_catalog_get();
    rtxmon_nvml_pci_info_t pci = {0};
    nvmlDevice_t device = NULL;
    nvmlReturn_t result;
    char uuid[RTXMON_TEXT_CAPACITY] = {0};
    char vbios[RTXMON_TEXT_CAPACITY] = {0};
    char driver[RTXMON_TEXT_CAPACITY] = {0};
    uint32_t identity_state = RTXMON_PRIVATE_OPERATION_IDENTITY_UNAVAILABLE;
    uint32_t association_state;

    (void)memset(report, 0, sizeof(*report));
    report->struct_size = (uint32_t)sizeof(*report);
    report->gpu_index = gpu_index;
    *out_handle = NULL;
    if (context == NULL || context->initialized == 0) {
        return RTXMON_STATUS_INVALID_ARGUMENT;
    }
    report->profile_revision = profile->revision;
    report->thermal_min_interval_ms = profile->thermal.min_interval_ms;
    report->thermal_timeout_ms = profile->thermal.timeout_ms;
    report->voltage_min_interval_ms = profile->voltage.min_interval_ms;
    report->voltage_timeout_ms = profile->voltage.timeout_ms;
    report->profile_state = profile->revoked != 0U
        ? RTXMON_PRIVATE_PROFILE_REVOKED : RTXMON_PRIVATE_PROFILE_ACTIVE;
    (void)snprintf(report->profile_id, sizeof(report->profile_id), "%s", profile->profile_id);
    if (profile->revoked != 0U) {
        (void)snprintf(report->revocation_reason, sizeof(report->revocation_reason),
            "%s", profile->revocation_reason);
        set_pending_states(report, RTXMON_PRIVATE_OPERATION_REVOKED);
        return RTXMON_STATUS_OK;
    }
    if (profile->thermal.revoked != 0U) {
        report->thermal_state = RTXMON_PRIVATE_OPERATION_REVOKED;
    }
    if (profile->voltage.revoked != 0U) {
        report->voltage_state = RTXMON_PRIVATE_OPERATION_REVOKED;
    }
    (void)snprintf(report->revocation_reason, sizeof(report->revocation_reason), "%s%s%s",
        profile->thermal.revocation_reason,
        profile->thermal.revoked != 0U && profile->voltage.revoked != 0U ? "; " : "",
        profile->voltage.revocation_reason);
    if (profile->thermal.revoked != 0U && profile->voltage.revoked != 0U) {
        return RTXMON_STATUS_OK;
    }
    if (rtxmon_private_acquisition_timed_out_internal()) {
        set_pending_states(report, RTXMON_PRIVATE_OPERATION_TIMEOUT);
        return RTXMON_STATUS_OK;
    }
#define RTXMON_GATE_CHECK() \
    do { \
        if (rtxmon_private_acquisition_check_internal(acquisition) != RTXMON_STATUS_OK) { \
            set_pending_states(report, RTXMON_PRIVATE_OPERATION_TIMEOUT); \
            return RTXMON_STATUS_TIMEOUT; \
        } \
    } while (0)
    RTXMON_GATE_CHECK();
    if (context->nvml.device_get_handle_by_index_v2 == NULL) {
        set_pending_states(report, RTXMON_PRIVATE_OPERATION_IDENTITY_UNAVAILABLE);
        return RTXMON_STATUS_OK;
    }
    result = context->nvml.device_get_handle_by_index_v2(gpu_index, &device);
    RTXMON_GATE_CHECK();
    if (result != NVML_SUCCESS || device == NULL) {
        set_pending_states(report, result == NVML_ERROR_NOT_FOUND || result == NVML_ERROR_GPU_NOT_FOUND
            ? RTXMON_PRIVATE_OPERATION_GPU_NOT_FOUND : RTXMON_PRIVATE_OPERATION_QUERY_FAILED);
        return RTXMON_STATUS_OK;
    }
    result = rtxmon_get_pci_info_internal(context, device, &pci);
    RTXMON_GATE_CHECK();
    if (result != NVML_SUCCESS) {
        set_pending_states(report, identity_query_state(result));
        return RTXMON_STATUS_OK;
    }
    record_identity(report, RTXMON_PRIVATE_IDENTITY_VENDOR,
        (pci.pci_device_id & 0xffffU) == profile->vendor_id);
    record_identity(report, RTXMON_PRIVATE_IDENTITY_DEVICE,
        (pci.pci_device_id >> 16U) == profile->device_id);
    record_identity(report, RTXMON_PRIVATE_IDENTITY_SUBSYSTEM_VENDOR,
        (pci.pci_subsystem_id & 0xffffU) == profile->subsystem_vendor_id);
    record_identity(report, RTXMON_PRIVATE_IDENTITY_SUBSYSTEM_DEVICE,
        (pci.pci_subsystem_id >> 16U) == profile->subsystem_device_id);

#define RTXMON_RECORD_TEXT_IDENTITY(flag, query, buffer, comparison) \
    do { \
        RTXMON_GATE_CHECK(); \
        result = (query); \
        RTXMON_GATE_CHECK(); \
        if (result == NVML_SUCCESS && memchr(buffer, '\0', sizeof(buffer)) != NULL && (buffer)[0] != '\0') { \
            record_identity(report, flag, comparison); \
        } else if (result != NVML_SUCCESS && identity_query_state(result) == RTXMON_PRIVATE_OPERATION_QUERY_FAILED) { \
            identity_state = RTXMON_PRIVATE_OPERATION_QUERY_FAILED; \
        } \
    } while (0)

    RTXMON_RECORD_TEXT_IDENTITY(RTXMON_PRIVATE_IDENTITY_UUID,
        context->nvml.device_get_uuid != NULL
            ? context->nvml.device_get_uuid(device, uuid, (uint32_t)sizeof(uuid)) : NVML_ERROR_FUNCTION_NOT_FOUND,
        uuid, strcmp(uuid, profile->uuid) == 0);
    RTXMON_RECORD_TEXT_IDENTITY(RTXMON_PRIVATE_IDENTITY_VBIOS,
        context->nvml.device_get_vbios_version != NULL
            ? context->nvml.device_get_vbios_version(device, vbios, (uint32_t)sizeof(vbios)) : NVML_ERROR_FUNCTION_NOT_FOUND,
        vbios, rtxmon_private_ascii_equal_ignore_case(vbios, profile->vbios));
    RTXMON_RECORD_TEXT_IDENTITY(RTXMON_PRIVATE_IDENTITY_DRIVER,
        context->nvml.system_get_driver_version != NULL
            ? context->nvml.system_get_driver_version(driver, (uint32_t)sizeof(driver)) : NVML_ERROR_FUNCTION_NOT_FOUND,
        driver, strcmp(driver, profile->driver) == 0);
#undef RTXMON_RECORD_TEXT_IDENTITY

    if (report->identity_checked_flags != RTXMON_PRIVATE_IDENTITY_ALL) {
        set_pending_states(report, identity_state);
        return RTXMON_STATUS_OK;
    }
    if (report->identity_match_flags != RTXMON_PRIVATE_IDENTITY_ALL) {
        set_pending_states(report, RTXMON_PRIVATE_OPERATION_IDENTITY_MISMATCH);
        return RTXMON_STATUS_OK;
    }
    /* These pointers are assigned only after loader hash/RVA validation. */
    if (context->nvapi_initialized == 0 || context->nvapi.gpu_therm_channel_get_status == NULL) {
        if (report->thermal_state == RTXMON_PRIVATE_OPERATION_UNKNOWN) {
            report->thermal_state = RTXMON_PRIVATE_OPERATION_MODULE_UNAVAILABLE;
        }
    }
    if (context->nvapi_initialized == 0 || context->nvapi.gpu_voltage_status == NULL) {
        if (report->voltage_state == RTXMON_PRIVATE_OPERATION_UNKNOWN) {
            report->voltage_state = RTXMON_PRIVATE_OPERATION_MODULE_UNAVAILABLE;
        }
    }
    if (report->thermal_state != RTXMON_PRIVATE_OPERATION_UNKNOWN &&
        report->voltage_state != RTXMON_PRIVATE_OPERATION_UNKNOWN) {
        return RTXMON_STATUS_OK;
    }
    RTXMON_GATE_CHECK();
    association_state = find_unique_gpu(context, &pci, out_handle, acquisition);
    set_pending_states(report, association_state);
    RTXMON_GATE_CHECK();
#undef RTXMON_GATE_CHECK
    return RTXMON_STATUS_OK;
}

rtxmon_status_t rtxmon_private_operation_status_internal(uint32_t state, int32_t *native_status)
{
    *native_status = RTXMON_NVAPI_NOT_SUPPORTED;
    if (state == RTXMON_PRIVATE_OPERATION_TIMEOUT) {
        *native_status = RTXMON_NVAPI_ERROR;
        return RTXMON_STATUS_TIMEOUT;
    }
    if (state == RTXMON_PRIVATE_OPERATION_RATE_LIMITED) {
        *native_status = RTXMON_NVAPI_ERROR;
        return RTXMON_STATUS_RATE_LIMITED;
    }
    if (state == RTXMON_PRIVATE_OPERATION_COMPATIBLE) {
        *native_status = RTXMON_NVAPI_OK;
        return RTXMON_STATUS_OK;
    }
    if (state == RTXMON_PRIVATE_OPERATION_GPU_NOT_FOUND) {
        *native_status = RTXMON_NVAPI_NVIDIA_DEVICE_NOT_FOUND;
        return RTXMON_STATUS_GPU_NOT_FOUND;
    }
    if (state == RTXMON_PRIVATE_OPERATION_QUERY_FAILED ||
        state == RTXMON_PRIVATE_OPERATION_IDENTITY_AMBIGUOUS) {
        *native_status = RTXMON_NVAPI_ERROR;
        return RTXMON_STATUS_BACKEND_ERROR;
    }
    return RTXMON_STATUS_NOT_SUPPORTED;
}

rtxmon_status_t RTXMON_CALL rtxmon_get_private_profile_status(
    rtxmon_context_t *context, uint32_t gpu_index, rtxmon_private_profile_report_t *out_report)
{
    rtxmon_nvapi_gpu_handle_t handle = NULL;
    rtxmon_status_t status;
    rtxmon_private_acquisition_t acquisition;
    if (out_report == NULL) {
        return RTXMON_STATUS_INVALID_ARGUMENT;
    }
    if (out_report->struct_size < sizeof(*out_report)) {
        return RTXMON_STATUS_ABI_MISMATCH;
    }
    if (context == NULL || context->initialized == 0) {
        return rtxmon_private_profile_evaluate_internal(context, gpu_index, out_report, &handle, NULL);
    }
    status = rtxmon_private_acquisition_begin_internal(&acquisition, &rtxmon_private_catalog_get()->thermal);
    if (status == RTXMON_STATUS_OK) {
        status = rtxmon_private_profile_evaluate_internal(context, gpu_index, out_report, &handle, &acquisition);
    }
    if (status == RTXMON_STATUS_TIMEOUT) {
        /* The latch makes this report-only path return before any backend call. */
        status = rtxmon_private_profile_evaluate_internal(context, gpu_index, out_report, &handle, NULL);
    }
    rtxmon_private_acquisition_end_internal(&acquisition);
    return status;
}
