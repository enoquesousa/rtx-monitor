#include "rtxmon_internal.h"

#include <limits.h>
#include <string.h>

#if defined(_MSC_VER)
#define RTXMON_SCAN_STATIC_ASSERT(condition, message) static_assert(condition, message)
#else
#define RTXMON_SCAN_STATIC_ASSERT(condition, message) _Static_assert(condition, message)
#endif

RTXMON_SCAN_STATIC_ASSERT(
    RTXMON_NVML_THERMAL_TARGET_NONE == 0 && RTXMON_NVML_THERMAL_TARGET_GPU == 1 &&
        RTXMON_NVML_THERMAL_TARGET_MEMORY == 2 &&
        RTXMON_NVML_THERMAL_TARGET_POWER_SUPPLY == 4 &&
        RTXMON_NVML_THERMAL_TARGET_BOARD == 8 &&
        RTXMON_NVML_THERMAL_TARGET_VCD_BOARD == 9 &&
        RTXMON_NVML_THERMAL_TARGET_VCD_INLET == 10 &&
        RTXMON_NVML_THERMAL_TARGET_VCD_OUTLET == 11,
    "NVML thermal target values changed");

RTXMON_SCAN_STATIC_ASSERT(
    RTXMON_NVAPI_THERMAL_TARGET_NONE == 0 && RTXMON_NVAPI_THERMAL_TARGET_GPU == 1 &&
        RTXMON_NVAPI_THERMAL_TARGET_MEMORY == 2 &&
        RTXMON_NVAPI_THERMAL_TARGET_POWER_SUPPLY == 4 &&
        RTXMON_NVAPI_THERMAL_TARGET_BOARD == 8 &&
        RTXMON_NVAPI_THERMAL_TARGET_VCD_BOARD == 9 &&
        RTXMON_NVAPI_THERMAL_TARGET_VCD_INLET == 10 &&
        RTXMON_NVAPI_THERMAL_TARGET_VCD_OUTLET == 11,
    "NVAPI thermal target values changed");

RTXMON_SCAN_STATIC_ASSERT(
    RTXMON_MAX_THERMAL_CAPABILITIES >=
        (RTXMON_NVML_MAX_THERMAL_SENSORS + 1U + RTXMON_NVAPI_MAX_THERMAL_SENSORS),
    "thermal report cannot hold every public provider result");

static uint32_t rtxmon_capability_state_from_nvml(nvmlReturn_t result)
{
    switch (result) {
    case NVML_SUCCESS:
        return RTXMON_CAPABILITY_AVAILABLE;
    case NVML_ERROR_NOT_SUPPORTED:
    case NVML_ERROR_DEPRECATED:
        return RTXMON_CAPABILITY_NOT_SUPPORTED;
    case NVML_ERROR_FUNCTION_NOT_FOUND:
        return RTXMON_CAPABILITY_PROVIDER_UNAVAILABLE;
    default:
        return RTXMON_CAPABILITY_QUERY_FAILED;
    }
}

static uint32_t rtxmon_capability_state_from_nvapi(rtxmon_nvapi_status_t result)
{
    switch (result) {
    case RTXMON_NVAPI_OK:
        return RTXMON_CAPABILITY_AVAILABLE;
    case RTXMON_NVAPI_NOT_SUPPORTED:
        return RTXMON_CAPABILITY_NOT_SUPPORTED;
    case RTXMON_NVAPI_LIBRARY_NOT_FOUND:
    case RTXMON_NVAPI_NO_IMPLEMENTATION:
        return RTXMON_CAPABILITY_PROVIDER_UNAVAILABLE;
    default:
        return RTXMON_CAPABILITY_QUERY_FAILED;
    }
}

static uint32_t rtxmon_map_thermal_target(int target)
{
    switch (target) {
    case RTXMON_NVML_THERMAL_TARGET_NONE:
        return RTXMON_THERMAL_TARGET_NONE;
    case RTXMON_NVML_THERMAL_TARGET_GPU:
        return RTXMON_THERMAL_TARGET_GPU;
    case RTXMON_NVML_THERMAL_TARGET_MEMORY:
        return RTXMON_THERMAL_TARGET_MEMORY;
    case RTXMON_NVML_THERMAL_TARGET_POWER_SUPPLY:
        return RTXMON_THERMAL_TARGET_POWER_SUPPLY;
    case RTXMON_NVML_THERMAL_TARGET_BOARD:
        return RTXMON_THERMAL_TARGET_BOARD;
    case RTXMON_NVML_THERMAL_TARGET_VCD_BOARD:
        return RTXMON_THERMAL_TARGET_VCD_BOARD;
    case RTXMON_NVML_THERMAL_TARGET_VCD_INLET:
        return RTXMON_THERMAL_TARGET_VCD_INLET;
    case RTXMON_NVML_THERMAL_TARGET_VCD_OUTLET:
        return RTXMON_THERMAL_TARGET_VCD_OUTLET;
    default:
        return RTXMON_THERMAL_TARGET_UNKNOWN;
    }
}

static uint32_t rtxmon_map_nvml_controller(int controller)
{
    if (controller >= RTXMON_NVML_THERMAL_CONTROLLER_NONE &&
        controller <= RTXMON_NVML_THERMAL_CONTROLLER_ADT7473S) {
        return (uint32_t)controller;
    }

    return RTXMON_THERMAL_CONTROLLER_UNKNOWN;
}

static uint32_t rtxmon_map_nvapi_controller(int controller)
{
    switch (controller) {
    case RTXMON_NVAPI_THERMAL_CONTROLLER_NONE:
        return RTXMON_THERMAL_CONTROLLER_NONE;
    case RTXMON_NVAPI_THERMAL_CONTROLLER_GPU_INTERNAL:
        return RTXMON_THERMAL_CONTROLLER_GPU_INTERNAL;
    case RTXMON_NVAPI_THERMAL_CONTROLLER_ADM1032:
        return RTXMON_THERMAL_CONTROLLER_ADM1032;
    case RTXMON_NVAPI_THERMAL_CONTROLLER_MAX6649:
        return RTXMON_THERMAL_CONTROLLER_MAX6649;
    case RTXMON_NVAPI_THERMAL_CONTROLLER_MAX1617:
        return RTXMON_THERMAL_CONTROLLER_MAX1617;
    case RTXMON_NVAPI_THERMAL_CONTROLLER_LM99:
        return RTXMON_THERMAL_CONTROLLER_LM99;
    case RTXMON_NVAPI_THERMAL_CONTROLLER_LM89:
        return RTXMON_THERMAL_CONTROLLER_LM89;
    case RTXMON_NVAPI_THERMAL_CONTROLLER_LM64:
        return RTXMON_THERMAL_CONTROLLER_LM64;
    case RTXMON_NVAPI_THERMAL_CONTROLLER_ADT7473:
        return RTXMON_THERMAL_CONTROLLER_ADT7473;
    case RTXMON_NVAPI_THERMAL_CONTROLLER_SBMAX6649:
        return RTXMON_THERMAL_CONTROLLER_SBMAX6649;
    case RTXMON_NVAPI_THERMAL_CONTROLLER_VBIOSEVT:
        return RTXMON_THERMAL_CONTROLLER_VBIOSEVT;
    case RTXMON_NVAPI_THERMAL_CONTROLLER_OS:
        return RTXMON_THERMAL_CONTROLLER_OS;
    default:
        return RTXMON_THERMAL_CONTROLLER_UNKNOWN;
    }
}

static void rtxmon_set_provider_result(
    rtxmon_thermal_report_t *report,
    uint32_t index,
    uint32_t provider,
    uint32_t state,
    int32_t native_status,
    uint32_t capability_count)
{
    rtxmon_thermal_provider_result_t *result = &report->providers[index];

    result->provider = provider;
    result->state = state;
    result->native_status = native_status;
    result->capability_count = capability_count;
}

static rtxmon_thermal_capability_t *rtxmon_append_capability(
    rtxmon_thermal_report_t *report)
{
    rtxmon_thermal_capability_t *capability;

    if (report->capability_count >= RTXMON_MAX_THERMAL_CAPABILITIES) {
        return NULL;
    }

    capability = &report->capabilities[report->capability_count++];
    (void)memset(capability, 0, sizeof(*capability));
    capability->target = RTXMON_THERMAL_TARGET_UNKNOWN;
    capability->controller = RTXMON_THERMAL_CONTROLLER_UNKNOWN;
    capability->confidence = RTXMON_CONFIDENCE_DRIVER_REPORTED;
    return capability;
}

static int rtxmon_nvml_field_temperature(
    const rtxmon_nvml_field_value_t *field,
    int32_t *out_temperature)
{
    switch (field->value_type) {
    case RTXMON_NVML_VALUE_TYPE_DOUBLE:
        if (field->value.double_value != field->value.double_value ||
            field->value.double_value < (double)INT32_MIN ||
            field->value.double_value > (double)INT32_MAX) {
            return 0;
        }
        *out_temperature = (int32_t)field->value.double_value;
        return (double)*out_temperature == field->value.double_value;
    case RTXMON_NVML_VALUE_TYPE_UNSIGNED_INT:
        if (field->value.unsigned_int_value > (uint32_t)INT32_MAX) {
            return 0;
        }
        *out_temperature = (int32_t)field->value.unsigned_int_value;
        return 1;
    case RTXMON_NVML_VALUE_TYPE_UNSIGNED_LONG:
        if (field->value.unsigned_long_value > (unsigned long)INT32_MAX) {
            return 0;
        }
        *out_temperature = (int32_t)field->value.unsigned_long_value;
        return 1;
    case RTXMON_NVML_VALUE_TYPE_SIGNED_INT:
        *out_temperature = field->value.signed_int_value;
        return 1;
    case RTXMON_NVML_VALUE_TYPE_UNSIGNED_LONG_LONG:
        if (field->value.unsigned_long_long_value > (uint64_t)INT32_MAX) {
            return 0;
        }
        *out_temperature = (int32_t)field->value.unsigned_long_long_value;
        return 1;
    case RTXMON_NVML_VALUE_TYPE_SIGNED_LONG_LONG:
        if (field->value.signed_long_long_value < (int64_t)INT32_MIN ||
            field->value.signed_long_long_value > (int64_t)INT32_MAX) {
            return 0;
        }
        *out_temperature = (int32_t)field->value.signed_long_long_value;
        return 1;
    case RTXMON_NVML_VALUE_TYPE_UNSIGNED_SHORT:
        *out_temperature = (int32_t)field->value.unsigned_short_value;
        return 1;
    default:
        return 0;
    }
}

static rtxmon_nvapi_status_t rtxmon_find_nvapi_gpu(
    rtxmon_context_t *context,
    const rtxmon_nvml_pci_info_t *pci,
    rtxmon_nvapi_gpu_handle_t *out_handle)
{
    rtxmon_nvapi_gpu_handle_t handles[RTXMON_NVAPI_MAX_PHYSICAL_GPUS];
    rtxmon_nvapi_gpu_handle_t matched_handle = NULL;
    rtxmon_nvapi_status_t result;
    uint32_t count = 0U;
    uint32_t match_count = 0U;
    uint32_t index;

    (void)memset(handles, 0, sizeof(handles));
    *out_handle = NULL;

    result = context->nvapi.enum_physical_gpus(handles, &count);
    if (result != RTXMON_NVAPI_OK) {
        return result;
    }

    if (count > RTXMON_NVAPI_MAX_PHYSICAL_GPUS) {
        return RTXMON_NVAPI_ERROR;
    }

    for (index = 0U; index < count; ++index) {
        uint32_t bus_id = 0U;
        uint32_t slot_id = 0U;
        uint32_t device_id = 0U;
        uint32_t subsystem_id = 0U;
        uint32_t revision_id = 0U;
        uint32_t extended_device_id = 0U;

        if (context->nvapi.gpu_get_bus_id(handles[index], &bus_id) != RTXMON_NVAPI_OK ||
            context->nvapi.gpu_get_bus_slot_id(handles[index], &slot_id) != RTXMON_NVAPI_OK ||
            context->nvapi.gpu_get_pci_identifiers(
                handles[index],
                &device_id,
                &subsystem_id,
                &revision_id,
                &extended_device_id) != RTXMON_NVAPI_OK) {
            continue;
        }

        (void)revision_id;
        (void)extended_device_id;
        if (bus_id == pci->bus && slot_id == pci->device &&
            device_id == pci->pci_device_id && subsystem_id == pci->pci_subsystem_id) {
            matched_handle = handles[index];
            ++match_count;
        }
    }

    if (match_count != 1U) {
        return RTXMON_NVAPI_NVIDIA_DEVICE_NOT_FOUND;
    }

    *out_handle = matched_handle;
    return RTXMON_NVAPI_OK;
}

static void rtxmon_collect_nvml_thermal_settings(
    rtxmon_context_t *context,
    nvmlDevice_t device,
    rtxmon_thermal_report_t *report)
{
    rtxmon_nvml_thermal_settings_t settings;
    nvmlReturn_t result;
    uint32_t index;

    if (context->nvml.device_get_thermal_settings == NULL) {
        rtxmon_set_provider_result(
            report,
            0U,
            RTXMON_PROVIDER_NVML_THERMAL_SETTINGS,
            RTXMON_CAPABILITY_PROVIDER_UNAVAILABLE,
            NVML_ERROR_FUNCTION_NOT_FOUND,
            0U);
        return;
    }

    (void)memset(&settings, 0, sizeof(settings));
    result = context->nvml.device_get_thermal_settings(
        device,
        RTXMON_NVML_THERMAL_TARGET_ALL,
        &settings);

    if (result == NVML_SUCCESS && settings.count <= RTXMON_NVML_MAX_THERMAL_SENSORS) {
        uint32_t appended = 0U;

        for (index = 0U; index < settings.count; ++index) {
            rtxmon_thermal_capability_t *capability = rtxmon_append_capability(report);
            if (capability == NULL) {
                break;
            }

            capability->provider = RTXMON_PROVIDER_NVML_THERMAL_SETTINGS;
            capability->target = rtxmon_map_thermal_target(settings.sensors[index].target);
            capability->controller = rtxmon_map_nvml_controller(settings.sensors[index].controller);
            capability->state = RTXMON_CAPABILITY_AVAILABLE;
            capability->value_flags =
                RTXMON_THERMAL_VALUE_CURRENT_VALID |
                RTXMON_THERMAL_VALUE_DEFAULT_MIN_VALID |
                RTXMON_THERMAL_VALUE_DEFAULT_MAX_VALID;
            capability->current_temperature_c = settings.sensors[index].current_temperature;
            capability->default_min_temperature_c =
                settings.sensors[index].default_min_temperature;
            capability->default_max_temperature_c =
                settings.sensors[index].default_max_temperature;
            capability->native_status = NVML_SUCCESS;
            capability->provider_native_id = index;
            ++appended;
        }

        rtxmon_set_provider_result(
            report,
            0U,
            RTXMON_PROVIDER_NVML_THERMAL_SETTINGS,
            appended == settings.count
                ? RTXMON_CAPABILITY_AVAILABLE
                : RTXMON_CAPABILITY_QUERY_FAILED,
            appended == settings.count ? NVML_SUCCESS : NVML_ERROR_INSUFFICIENT_SIZE,
            appended);
        return;
    }

    {
        const nvmlReturn_t report_result = result == NVML_SUCCESS
            ? NVML_ERROR_INVALID_ARGUMENT
            : result;
        rtxmon_set_provider_result(
            report,
            0U,
            RTXMON_PROVIDER_NVML_THERMAL_SETTINGS,
            rtxmon_capability_state_from_nvml(report_result),
            report_result,
            0U);
    }
}

static void rtxmon_collect_nvml_memory_field(
    rtxmon_context_t *context,
    nvmlDevice_t device,
    rtxmon_thermal_report_t *report)
{
    rtxmon_thermal_capability_t *capability = rtxmon_append_capability(report);

    if (capability != NULL) {
        capability->provider = RTXMON_PROVIDER_NVML_FIELD_VALUES;
        capability->target = RTXMON_THERMAL_TARGET_MEMORY;
        capability->controller = RTXMON_THERMAL_CONTROLLER_UNKNOWN;
        capability->provider_native_id = RTXMON_NVML_FI_DEV_MEMORY_TEMP;
    }

    if (context->nvml.device_get_field_values == NULL) {
        if (capability != NULL) {
            capability->state = RTXMON_CAPABILITY_PROVIDER_UNAVAILABLE;
            capability->native_status = NVML_ERROR_FUNCTION_NOT_FOUND;
        }
        rtxmon_set_provider_result(
            report,
            1U,
            RTXMON_PROVIDER_NVML_FIELD_VALUES,
            RTXMON_CAPABILITY_PROVIDER_UNAVAILABLE,
            NVML_ERROR_FUNCTION_NOT_FOUND,
            capability != NULL ? 1U : 0U);
        return;
    }

    {
        rtxmon_nvml_field_value_t field;
        nvmlReturn_t result;

        (void)memset(&field, 0, sizeof(field));
        field.field_id = RTXMON_NVML_FI_DEV_MEMORY_TEMP;
        result = context->nvml.device_get_field_values(device, 1, &field);

        if (capability != NULL) {
            capability->native_status = result == NVML_SUCCESS ? field.result : result;
            capability->state = rtxmon_capability_state_from_nvml(
                result == NVML_SUCCESS ? field.result : result);

            if (result == NVML_SUCCESS && field.result == NVML_SUCCESS) {
                if (rtxmon_nvml_field_temperature(
                        &field,
                        &capability->current_temperature_c) != 0) {
                    capability->value_flags = RTXMON_THERMAL_VALUE_CURRENT_VALID;
                } else {
                    capability->state = RTXMON_CAPABILITY_QUERY_FAILED;
                }
            }
        }

        rtxmon_set_provider_result(
            report,
            1U,
            RTXMON_PROVIDER_NVML_FIELD_VALUES,
            rtxmon_capability_state_from_nvml(result),
            result,
            capability != NULL ? 1U : 0U);
    }
}

static void rtxmon_collect_nvapi_thermal_settings(
    rtxmon_context_t *context,
    const rtxmon_nvml_pci_info_t *pci,
    nvmlReturn_t pci_result,
    rtxmon_thermal_report_t *report)
{
    rtxmon_nvapi_gpu_handle_t handle = NULL;
    rtxmon_nvapi_status_t nvapi_result;
    uint32_t index;

    if (context->nvapi_initialized == 0) {
        rtxmon_set_provider_result(
            report,
            2U,
            RTXMON_PROVIDER_NVAPI_THERMAL_SETTINGS,
            rtxmon_capability_state_from_nvapi(context->nvapi_initialize_status),
            context->nvapi_initialize_status,
            0U);
        return;
    }

    if (pci_result != NVML_SUCCESS) {
        rtxmon_set_provider_result(
            report,
            2U,
            RTXMON_PROVIDER_NVAPI_THERMAL_SETTINGS,
            RTXMON_CAPABILITY_QUERY_FAILED,
            RTXMON_NVAPI_INVALID_ARGUMENT,
            0U);
        return;
    }

    rtxmon_lock_nvapi_internal();
    nvapi_result = rtxmon_find_nvapi_gpu(context, pci, &handle);
    if (nvapi_result == RTXMON_NVAPI_OK) {
        rtxmon_nvapi_thermal_settings_v2_t settings;

        (void)memset(&settings, 0, sizeof(settings));
        settings.version = RTXMON_NVAPI_THERMAL_SETTINGS_V2_VERSION;
        nvapi_result = context->nvapi.gpu_get_thermal_settings(
            handle,
            RTXMON_NVAPI_THERMAL_TARGET_ALL,
            &settings);

        if (nvapi_result == RTXMON_NVAPI_OK &&
            settings.count <= RTXMON_NVAPI_MAX_THERMAL_SENSORS) {
            uint32_t appended = 0U;

            for (index = 0U; index < settings.count; ++index) {
                rtxmon_thermal_capability_t *capability = rtxmon_append_capability(report);
                if (capability == NULL) {
                    break;
                }

                capability->provider = RTXMON_PROVIDER_NVAPI_THERMAL_SETTINGS;
                capability->target = rtxmon_map_thermal_target(settings.sensors[index].target);
                capability->controller =
                    rtxmon_map_nvapi_controller(settings.sensors[index].controller);
                capability->state = RTXMON_CAPABILITY_AVAILABLE;
                capability->value_flags =
                    RTXMON_THERMAL_VALUE_CURRENT_VALID |
                    RTXMON_THERMAL_VALUE_DEFAULT_MIN_VALID |
                    RTXMON_THERMAL_VALUE_DEFAULT_MAX_VALID;
                capability->current_temperature_c = settings.sensors[index].current_temperature;
                capability->default_min_temperature_c =
                    settings.sensors[index].default_min_temperature;
                capability->default_max_temperature_c =
                    settings.sensors[index].default_max_temperature;
                capability->native_status = RTXMON_NVAPI_OK;
                capability->provider_native_id = index;
                ++appended;
            }

            rtxmon_set_provider_result(
                report,
                2U,
                RTXMON_PROVIDER_NVAPI_THERMAL_SETTINGS,
                appended == settings.count
                    ? RTXMON_CAPABILITY_AVAILABLE
                    : RTXMON_CAPABILITY_QUERY_FAILED,
                appended == settings.count ? RTXMON_NVAPI_OK : RTXMON_NVAPI_ERROR,
                appended);
        } else {
            if (nvapi_result == RTXMON_NVAPI_OK) {
                nvapi_result = RTXMON_NVAPI_INVALID_ARGUMENT;
            }
            rtxmon_set_provider_result(
                report,
                2U,
                RTXMON_PROVIDER_NVAPI_THERMAL_SETTINGS,
                rtxmon_capability_state_from_nvapi(nvapi_result),
                nvapi_result,
                0U);
        }
    } else {
        rtxmon_set_provider_result(
            report,
            2U,
            RTXMON_PROVIDER_NVAPI_THERMAL_SETTINGS,
            rtxmon_capability_state_from_nvapi(nvapi_result),
            nvapi_result,
            0U);
    }
    rtxmon_unlock_nvapi_internal();
}

void rtxmon_collect_thermal_capabilities(
    rtxmon_context_t *context,
    uint32_t gpu_index,
    nvmlDevice_t device,
    rtxmon_thermal_report_t *report)
{
    rtxmon_nvml_pci_info_t pci = {0};
    nvmlReturn_t pci_result;

    (void)memset(report, 0, sizeof(*report));
    report->struct_size = (uint32_t)sizeof(*report);
    report->gpu_index = gpu_index;
    report->provider_count = RTXMON_MAX_THERMAL_PROVIDERS;

    rtxmon_collect_nvml_thermal_settings(context, device, report);
    rtxmon_collect_nvml_memory_field(context, device, report);

    pci_result = rtxmon_get_pci_info_internal(context, device, &pci);
    rtxmon_collect_nvapi_thermal_settings(context, &pci, pci_result, report);

    report->timestamp_unix_ms = rtxmon_timestamp_unix_ms_internal();
}
