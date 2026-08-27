#include <rtxmon/rtxmon.h>

#include "rtxmon_internal.h"

#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#if defined(_WIN32)
#include <windows.h>
#else
#include <time.h>
#endif

#if defined(_MSC_VER)
#define RTXMON_THREAD_LOCAL __declspec(thread)
#else
#define RTXMON_THREAD_LOCAL _Thread_local
#endif

#define RTXMON_ERROR_CAPACITY 512U

#if defined(_MSC_VER)
#define RTXMON_PRIVATE_STATIC_ASSERT(condition, message) static_assert(condition, message)
#else
#define RTXMON_PRIVATE_STATIC_ASSERT(condition, message) _Static_assert(condition, message)
#endif

RTXMON_PRIVATE_STATIC_ASSERT(
    sizeof(rtxmon_nvml_temperature_v1_t) == 12U,
    "NVML temperature ABI changed");
RTXMON_PRIVATE_STATIC_ASSERT(sizeof(rtxmon_nvml_pci_info_t) == 68U, "NVML PCI ABI changed");
RTXMON_PRIVATE_STATIC_ASSERT(
    sizeof(rtxmon_nvml_thermal_settings_t) == 64U,
    "NVML thermal ABI changed");
RTXMON_PRIVATE_STATIC_ASSERT(
    sizeof(rtxmon_nvml_field_value_t) == 40U,
    "NVML field value ABI changed");
RTXMON_PRIVATE_STATIC_ASSERT(
    sizeof(rtxmon_nvml_utilization_t) == 8U,
    "NVML utilization ABI changed");
RTXMON_PRIVATE_STATIC_ASSERT(
    sizeof(rtxmon_nvml_memory_t) == 24U,
    "NVML memory ABI changed");
RTXMON_PRIVATE_STATIC_ASSERT(
    sizeof(rtxmon_nvapi_thermal_settings_v2_t) == 68U,
    "NVAPI thermal ABI changed");

static RTXMON_THREAD_LOCAL char rtxmon_error[RTXMON_ERROR_CAPACITY];

#if defined(_WIN32)
static SRWLOCK rtxmon_nvapi_global_lock = SRWLOCK_INIT;
#endif

static void rtxmon_clear_error(void)
{
    rtxmon_error[0] = '\0';
}

static void rtxmon_set_error(const char *format, ...)
{
    va_list arguments;

    va_start(arguments, format);
    (void)vsnprintf(rtxmon_error, sizeof(rtxmon_error), format, arguments);
    va_end(arguments);
    rtxmon_error[sizeof(rtxmon_error) - 1U] = '\0';
}

static const char *rtxmon_nvml_error_text(
    const rtxmon_context_t *context,
    nvmlReturn_t result)
{
    const char *text;

    if (context == NULL || context->nvml.error_string == NULL) {
        return "unknown NVML error";
    }

    text = context->nvml.error_string(result);
    return text != NULL ? text : "unknown NVML error";
}

static rtxmon_status_t rtxmon_map_nvml_status(nvmlReturn_t result)
{
    switch (result) {
    case NVML_SUCCESS:
        return RTXMON_STATUS_OK;
    case NVML_ERROR_INVALID_ARGUMENT:
        return RTXMON_STATUS_INVALID_ARGUMENT;
    case NVML_ERROR_NOT_SUPPORTED:
    case NVML_ERROR_DEPRECATED:
        return RTXMON_STATUS_NOT_SUPPORTED;
    case NVML_ERROR_NO_PERMISSION:
        return RTXMON_STATUS_NO_PERMISSION;
    case NVML_ERROR_DRIVER_NOT_LOADED:
        return RTXMON_STATUS_DRIVER_NOT_LOADED;
    case NVML_ERROR_NOT_FOUND:
    case NVML_ERROR_GPU_NOT_FOUND:
        return RTXMON_STATUS_GPU_NOT_FOUND;
    case NVML_ERROR_GPU_IS_LOST:
        return RTXMON_STATUS_GPU_LOST;
    case NVML_ERROR_ARGUMENT_VERSION_MISMATCH:
        return RTXMON_STATUS_ABI_MISMATCH;
    default:
        return RTXMON_STATUS_BACKEND_ERROR;
    }
}

void rtxmon_lock_nvapi_internal(void)
{
#if defined(_WIN32)
    AcquireSRWLockExclusive(&rtxmon_nvapi_global_lock);
#endif
}

void rtxmon_unlock_nvapi_internal(void)
{
#if defined(_WIN32)
    ReleaseSRWLockExclusive(&rtxmon_nvapi_global_lock);
#endif
}

static rtxmon_status_t rtxmon_validate_context(const rtxmon_context_t *context)
{
    if (context == NULL || context->initialized == 0) {
        rtxmon_set_error("rtxmon context is null or not initialized");
        return RTXMON_STATUS_INVALID_ARGUMENT;
    }

    return RTXMON_STATUS_OK;
}

static rtxmon_status_t rtxmon_get_device(
    rtxmon_context_t *context,
    uint32_t gpu_index,
    nvmlDevice_t *out_device)
{
    uint32_t count = 0U;
    nvmlReturn_t result;

    result = context->nvml.device_get_count_v2(&count);
    if (result != NVML_SUCCESS) {
        rtxmon_set_error(
            "nvmlDeviceGetCount_v2 failed: %s (%d)",
            rtxmon_nvml_error_text(context, result),
            result);
        return rtxmon_map_nvml_status(result);
    }

    if (gpu_index >= count) {
        rtxmon_set_error("GPU index %u is out of range; detected GPU count is %u", gpu_index, count);
        return RTXMON_STATUS_GPU_NOT_FOUND;
    }

    result = context->nvml.device_get_handle_by_index_v2(gpu_index, out_device);
    if (result != NVML_SUCCESS) {
        rtxmon_set_error(
            "nvmlDeviceGetHandleByIndex_v2(%u) failed: %s (%d)",
            gpu_index,
            rtxmon_nvml_error_text(context, result),
            result);
        return rtxmon_map_nvml_status(result);
    }

    return RTXMON_STATUS_OK;
}

nvmlReturn_t rtxmon_get_pci_info_internal(
    rtxmon_context_t *context,
    nvmlDevice_t device,
    rtxmon_nvml_pci_info_t *out_pci)
{
    nvmlReturn_t result;

    if (context->nvml.device_get_pci_info_v3 == NULL) {
        return NVML_ERROR_FUNCTION_NOT_FOUND;
    }

    (void)memset(out_pci, 0, sizeof(*out_pci));
    result = context->nvml.device_get_pci_info_v3(device, out_pci);
    if (result == NVML_SUCCESS) {
        out_pci->bus_id_legacy[sizeof(out_pci->bus_id_legacy) - 1U] = '\0';
        out_pci->bus_id[sizeof(out_pci->bus_id) - 1U] = '\0';
    }

    return result;
}

uint64_t rtxmon_timestamp_unix_ms_internal(void)
{
#if defined(_WIN32)
    FILETIME file_time;
    ULARGE_INTEGER ticks;
    const uint64_t windows_to_unix_epoch_100ns = 116444736000000000ULL;

    GetSystemTimePreciseAsFileTime(&file_time);
    ticks.LowPart = file_time.dwLowDateTime;
    ticks.HighPart = file_time.dwHighDateTime;

    return (ticks.QuadPart - windows_to_unix_epoch_100ns) / 10000ULL;
#else
    struct timespec now;

    if (clock_gettime(CLOCK_REALTIME, &now) != 0) {
        return 0U;
    }

    return ((uint64_t)now.tv_sec * 1000ULL) + ((uint64_t)now.tv_nsec / 1000000ULL);
#endif
}

uint32_t RTXMON_CALL rtxmon_abi_version(void)
{
    rtxmon_clear_error();
    return RTXMON_ABI_VERSION;
}

const char *RTXMON_CALL rtxmon_status_string(rtxmon_status_t status)
{
    switch (status) {
    case RTXMON_STATUS_OK:
        return "ok";
    case RTXMON_STATUS_INVALID_ARGUMENT:
        return "invalid argument";
    case RTXMON_STATUS_OUT_OF_MEMORY:
        return "out of memory";
    case RTXMON_STATUS_BACKEND_NOT_FOUND:
        return "NVIDIA monitoring backend not found";
    case RTXMON_STATUS_BACKEND_SYMBOL_MISSING:
        return "required monitoring backend symbol missing";
    case RTXMON_STATUS_DRIVER_NOT_LOADED:
        return "NVIDIA driver not loaded";
    case RTXMON_STATUS_NO_PERMISSION:
        return "permission denied by NVIDIA driver";
    case RTXMON_STATUS_GPU_NOT_FOUND:
        return "GPU not found";
    case RTXMON_STATUS_NOT_SUPPORTED:
        return "operation not supported by GPU or driver";
    case RTXMON_STATUS_GPU_LOST:
        return "GPU is inaccessible or lost";
    case RTXMON_STATUS_BACKEND_ERROR:
        return "monitoring backend error";
    case RTXMON_STATUS_ABI_MISMATCH:
        return "backend ABI version mismatch";
    default:
        return "unknown rtxmon status";
    }
}

const char *RTXMON_CALL rtxmon_temperature_backend_string(uint32_t backend)
{
    switch (backend) {
    case RTXMON_BACKEND_NVML_TEMPERATURE_V1:
        return "NVML nvmlDeviceGetTemperatureV";
    case RTXMON_BACKEND_NVML_TEMPERATURE_LEGACY:
        return "NVML nvmlDeviceGetTemperature (legacy fallback)";
    default:
        return "unknown temperature backend";
    }
}

const char *RTXMON_CALL rtxmon_thermal_provider_string(uint32_t provider)
{
    switch (provider) {
    case RTXMON_PROVIDER_NVML_THERMAL_SETTINGS:
        return "NVML nvmlDeviceGetThermalSettings";
    case RTXMON_PROVIDER_NVML_FIELD_VALUES:
        return "NVML nvmlDeviceGetFieldValues";
    case RTXMON_PROVIDER_NVAPI_THERMAL_SETTINGS:
        return "NVAPI NvAPI_GPU_GetThermalSettings";
    default:
        return "unknown thermal provider";
    }
}

const char *RTXMON_CALL rtxmon_capability_state_string(uint32_t state)
{
    switch (state) {
    case RTXMON_CAPABILITY_UNKNOWN:
        return "unknown";
    case RTXMON_CAPABILITY_AVAILABLE:
        return "available";
    case RTXMON_CAPABILITY_NOT_SUPPORTED:
        return "not_supported";
    case RTXMON_CAPABILITY_PROVIDER_UNAVAILABLE:
        return "provider_unavailable";
    case RTXMON_CAPABILITY_QUERY_FAILED:
        return "query_failed";
    default:
        return "invalid capability state";
    }
}

const char *RTXMON_CALL rtxmon_thermal_target_string(uint32_t target)
{
    switch (target) {
    case RTXMON_THERMAL_TARGET_NONE:
        return "none";
    case RTXMON_THERMAL_TARGET_GPU:
        return "gpu";
    case RTXMON_THERMAL_TARGET_MEMORY:
        return "memory";
    case RTXMON_THERMAL_TARGET_POWER_SUPPLY:
        return "power_supply";
    case RTXMON_THERMAL_TARGET_BOARD:
        return "board";
    case RTXMON_THERMAL_TARGET_VCD_BOARD:
        return "vcd_board";
    case RTXMON_THERMAL_TARGET_VCD_INLET:
        return "vcd_inlet";
    case RTXMON_THERMAL_TARGET_VCD_OUTLET:
        return "vcd_outlet";
    case RTXMON_THERMAL_TARGET_UNKNOWN:
        return "unknown";
    default:
        return "unrecognized";
    }
}

const char *RTXMON_CALL rtxmon_thermal_controller_string(uint32_t controller)
{
    switch (controller) {
    case RTXMON_THERMAL_CONTROLLER_NONE:
        return "none";
    case RTXMON_THERMAL_CONTROLLER_GPU_INTERNAL:
        return "gpu_internal";
    case RTXMON_THERMAL_CONTROLLER_ADM1032:
        return "adm1032";
    case RTXMON_THERMAL_CONTROLLER_ADT7461:
        return "adt7461";
    case RTXMON_THERMAL_CONTROLLER_MAX6649:
        return "max6649";
    case RTXMON_THERMAL_CONTROLLER_MAX1617:
        return "max1617";
    case RTXMON_THERMAL_CONTROLLER_LM99:
        return "lm99";
    case RTXMON_THERMAL_CONTROLLER_LM89:
        return "lm89";
    case RTXMON_THERMAL_CONTROLLER_LM64:
        return "lm64";
    case RTXMON_THERMAL_CONTROLLER_G781:
        return "g781";
    case RTXMON_THERMAL_CONTROLLER_ADT7473:
        return "adt7473";
    case RTXMON_THERMAL_CONTROLLER_SBMAX6649:
        return "sbmax6649";
    case RTXMON_THERMAL_CONTROLLER_VBIOSEVT:
        return "vbios_event";
    case RTXMON_THERMAL_CONTROLLER_OS:
        return "os";
    case RTXMON_THERMAL_CONTROLLER_NVSYSCON_CANOAS:
        return "nvsyscon_canoas";
    case RTXMON_THERMAL_CONTROLLER_NVSYSCON_E551:
        return "nvsyscon_e551";
    case RTXMON_THERMAL_CONTROLLER_MAX6649R:
        return "max6649r";
    case RTXMON_THERMAL_CONTROLLER_ADT7473S:
        return "adt7473s";
    case RTXMON_THERMAL_CONTROLLER_UNKNOWN:
        return "unknown";
    default:
        return "unrecognized";
    }
}

const char *RTXMON_CALL rtxmon_sensor_confidence_string(uint32_t confidence)
{
    switch (confidence) {
    case RTXMON_CONFIDENCE_UNKNOWN:
        return "unknown";
    case RTXMON_CONFIDENCE_DRIVER_REPORTED:
        return "driver_reported";
    case RTXMON_CONFIDENCE_EXPERIMENTAL:
        return "experimental";
    default:
        return "invalid confidence";
    }
}

const char *RTXMON_CALL rtxmon_data_origin_string(uint32_t origin)
{
    switch (origin) {
    case RTXMON_ORIGIN_DRIVER_REPORTED:
        return "driver_reported";
    case RTXMON_ORIGIN_COMPUTED:
        return "computed";
    case RTXMON_ORIGIN_EXPERIMENTAL:
        return "experimental";
    case RTXMON_ORIGIN_UNKNOWN:
    default:
        return "unknown";
    }
}

const char *RTXMON_CALL rtxmon_public_field_string(uint32_t field)
{
    switch (field) {
    case RTXMON_PUBLIC_FIELD_GPU_DIE_TEMPERATURE_C:
        return "gpu_die_temperature_c";
    case RTXMON_PUBLIC_FIELD_MEMORY_TEMPERATURE_C:
        return "memory_temperature_c";
    case RTXMON_PUBLIC_FIELD_TOTAL_ENERGY_MJ:
        return "total_energy_mj";
    case RTXMON_PUBLIC_FIELD_POWER_AVERAGE_MW:
        return "power_average_mw";
    case RTXMON_PUBLIC_FIELD_POWER_INSTANT_MW:
        return "power_instant_mw";
    case RTXMON_PUBLIC_FIELD_POWER_LIMIT_MIN_MW:
        return "power_limit_min_mw";
    case RTXMON_PUBLIC_FIELD_POWER_LIMIT_MAX_MW:
        return "power_limit_max_mw";
    case RTXMON_PUBLIC_FIELD_POWER_LIMIT_DEFAULT_MW:
        return "power_limit_default_mw";
    case RTXMON_PUBLIC_FIELD_POWER_LIMIT_CURRENT_MW:
        return "power_limit_current_mw";
    case RTXMON_PUBLIC_FIELD_POWER_LIMIT_REQUESTED_MW:
        return "power_limit_requested_mw";
    case RTXMON_PUBLIC_FIELD_TEMPERATURE_SHUTDOWN_C:
        return "temperature_shutdown_c";
    case RTXMON_PUBLIC_FIELD_TEMPERATURE_SLOWDOWN_C:
        return "temperature_slowdown_c";
    case RTXMON_PUBLIC_FIELD_TEMPERATURE_MEMORY_MAX_C:
        return "temperature_memory_max_c";
    case RTXMON_PUBLIC_FIELD_TEMPERATURE_GPU_MAX_C:
        return "temperature_gpu_max_c";
    case RTXMON_PUBLIC_FIELD_CLOCK_GRAPHICS_MHZ:
        return "clock_graphics_mhz";
    case RTXMON_PUBLIC_FIELD_CLOCK_SM_MHZ:
        return "clock_sm_mhz";
    case RTXMON_PUBLIC_FIELD_CLOCK_MEMORY_MHZ:
        return "clock_memory_mhz";
    case RTXMON_PUBLIC_FIELD_CLOCK_VIDEO_MHZ:
        return "clock_video_mhz";
    case RTXMON_PUBLIC_FIELD_UTILIZATION_GPU_PERCENT:
        return "utilization_gpu_percent";
    case RTXMON_PUBLIC_FIELD_UTILIZATION_MEMORY_PERCENT:
        return "utilization_memory_percent";
    case RTXMON_PUBLIC_FIELD_MEMORY_TOTAL_BYTES:
        return "memory_total_bytes";
    case RTXMON_PUBLIC_FIELD_MEMORY_FREE_BYTES:
        return "memory_free_bytes";
    case RTXMON_PUBLIC_FIELD_MEMORY_USED_BYTES:
        return "memory_used_bytes";
    case RTXMON_PUBLIC_FIELD_FAN_SPEED_PERCENT:
        return "fan_speed_percent";
    case RTXMON_PUBLIC_FIELD_PERFORMANCE_STATE:
        return "performance_state";
    case RTXMON_PUBLIC_FIELD_CLOCK_EVENT_REASONS_CURRENT:
        return "clock_event_reasons_current";
    case RTXMON_PUBLIC_FIELD_CLOCK_EVENT_REASONS_SUPPORTED:
        return "clock_event_reasons_supported";
    case RTXMON_PUBLIC_FIELD_ENCODER_UTILIZATION_PERCENT:
        return "encoder_utilization_percent";
    case RTXMON_PUBLIC_FIELD_ENCODER_SAMPLING_PERIOD_US:
        return "encoder_sampling_period_us";
    case RTXMON_PUBLIC_FIELD_DECODER_UTILIZATION_PERCENT:
        return "decoder_utilization_percent";
    case RTXMON_PUBLIC_FIELD_DECODER_SAMPLING_PERIOD_US:
        return "decoder_sampling_period_us";
    case RTXMON_PUBLIC_FIELD_POWER_CONSUMPTION_DEFAULT_LIMIT_PERCENT:
        return "power_consumption_default_limit_percent";
    case RTXMON_PUBLIC_FIELD_POWER_CONSUMPTION_CURRENT_LIMIT_PERCENT:
        return "power_consumption_current_limit_percent";
    case RTXMON_PUBLIC_FIELD_TEMPERATURE_GPU_LIMIT_C:
        return "temperature_gpu_limit_c";
    default:
        return "unknown_public_field";
    }
}

const char *RTXMON_CALL rtxmon_public_provider_string(uint32_t provider)
{
    switch (provider) {
    case RTXMON_PUBLIC_PROVIDER_NVML_TEMPERATURE_V1:
        return "NVML nvmlDeviceGetTemperatureV";
    case RTXMON_PUBLIC_PROVIDER_NVML_TEMPERATURE_LEGACY:
        return "NVML nvmlDeviceGetTemperature";
    case RTXMON_PUBLIC_PROVIDER_NVML_FIELD_VALUES:
        return "NVML nvmlDeviceGetFieldValues";
    case RTXMON_PUBLIC_PROVIDER_NVML_CLOCK_INFO:
        return "NVML nvmlDeviceGetClockInfo";
    case RTXMON_PUBLIC_PROVIDER_NVML_UTILIZATION_RATES:
        return "NVML nvmlDeviceGetUtilizationRates";
    case RTXMON_PUBLIC_PROVIDER_NVML_MEMORY_INFO:
        return "NVML nvmlDeviceGetMemoryInfo";
    case RTXMON_PUBLIC_PROVIDER_NVML_FAN_SPEED_V2:
        return "NVML nvmlDeviceGetFanSpeed_v2";
    case RTXMON_PUBLIC_PROVIDER_NVML_FAN_SPEED_LEGACY:
        return "NVML nvmlDeviceGetFanSpeed";
    case RTXMON_PUBLIC_PROVIDER_NVML_PERFORMANCE_STATE:
        return "NVML nvmlDeviceGetPerformanceState";
    case RTXMON_PUBLIC_PROVIDER_NVML_CLOCK_EVENT_REASONS:
        return "NVML nvmlDeviceGetCurrentClocksEventReasons";
    case RTXMON_PUBLIC_PROVIDER_NVML_CLOCK_THROTTLE_REASONS_LEGACY:
        return "NVML nvmlDeviceGetCurrentClocksThrottleReasons";
    case RTXMON_PUBLIC_PROVIDER_NVML_ENCODER_UTILIZATION:
        return "NVML nvmlDeviceGetEncoderUtilization";
    case RTXMON_PUBLIC_PROVIDER_NVML_DECODER_UTILIZATION:
        return "NVML nvmlDeviceGetDecoderUtilization";
    case RTXMON_PUBLIC_PROVIDER_NVML_SUPPORTED_CLOCK_EVENT_REASONS:
        return "NVML nvmlDeviceGetSupportedClocksEventReasons";
    case RTXMON_PUBLIC_PROVIDER_NVML_SUPPORTED_CLOCK_THROTTLE_REASONS_LEGACY:
        return "NVML nvmlDeviceGetSupportedClocksThrottleReasons";
    case RTXMON_PUBLIC_PROVIDER_COMPUTED_POWER_RATIO:
        return "RTX Monitor computed power ratio";
    case RTXMON_PUBLIC_PROVIDER_NVML_TEMPERATURE_THRESHOLD:
        return "NVML nvmlDeviceGetTemperatureThreshold";
    default:
        return "unknown_public_provider";
    }
}

const char *RTXMON_CALL rtxmon_value_type_string(uint32_t value_type)
{
    switch (value_type) {
    case RTXMON_VALUE_TYPE_UNSIGNED_INTEGER:
        return "unsigned_integer";
    case RTXMON_VALUE_TYPE_SIGNED_INTEGER:
        return "signed_integer";
    case RTXMON_VALUE_TYPE_DOUBLE:
        return "double";
    case RTXMON_VALUE_TYPE_BITMASK:
        return "bitmask";
    case RTXMON_VALUE_TYPE_UNKNOWN:
    default:
        return "unknown";
    }
}

const char *RTXMON_CALL rtxmon_unit_string(uint32_t unit)
{
    switch (unit) {
    case RTXMON_UNIT_CELSIUS:
        return "celsius";
    case RTXMON_UNIT_MILLIWATT:
        return "milliwatt";
    case RTXMON_UNIT_MILLIJOULE:
        return "millijoule";
    case RTXMON_UNIT_MEGAHERTZ:
        return "megahertz";
    case RTXMON_UNIT_PERCENT:
        return "percent";
    case RTXMON_UNIT_BYTES:
        return "bytes";
    case RTXMON_UNIT_PSTATE:
        return "pstate";
    case RTXMON_UNIT_BITMASK:
        return "bitmask";
    case RTXMON_UNIT_MICROSECONDS:
        return "microseconds";
    case RTXMON_UNIT_CELSIUS_PER_SECOND:
        return "celsius_per_second";
    case RTXMON_UNIT_SECONDS:
        return "seconds";
    case RTXMON_UNIT_UNKNOWN:
    default:
        return "unknown";
    }
}

const char *RTXMON_CALL rtxmon_computed_metric_string(uint32_t metric)
{
    switch (metric) {
    case RTXMON_METRIC_GPU_TEMPERATURE_WINDOW_AVERAGE:
        return "gpu_temperature_window_average";
    case RTXMON_METRIC_GPU_TEMPERATURE_SLOPE:
        return "gpu_temperature_slope";
    case RTXMON_METRIC_GPU_TEMPERATURE_TIME_ABOVE_THRESHOLD:
        return "gpu_temperature_time_above_threshold";
    case RTXMON_METRIC_GPU_MEMORY_TEMPERATURE_DELTA:
        return "gpu_memory_temperature_delta";
    default:
        return "unknown_computed_metric";
    }
}

const char *RTXMON_CALL rtxmon_computed_metric_formula(uint32_t metric)
{
    switch (metric) {
    case RTXMON_METRIC_GPU_TEMPERATURE_WINDOW_AVERAGE:
        return "mean(gpu_die_temperature_c within window)";
    case RTXMON_METRIC_GPU_TEMPERATURE_SLOPE:
        return "(last(gpu_die_temperature_c)-first(gpu_die_temperature_c))/elapsed_seconds";
    case RTXMON_METRIC_GPU_TEMPERATURE_TIME_ABOVE_THRESHOLD:
        return "sum(clipped_interval_seconds where prior(gpu_die_temperature_c)>threshold_c)";
    case RTXMON_METRIC_GPU_MEMORY_TEMPERATURE_DELTA:
        return "gpu_die_temperature_c-memory_temperature_c at the same snapshot";
    default:
        return "unknown";
    }
}

const char *RTXMON_CALL rtxmon_metric_state_string(uint32_t state)
{
    switch (state) {
    case RTXMON_METRIC_STATE_AVAILABLE:
        return "available";
    case RTXMON_METRIC_STATE_INSUFFICIENT_DATA:
        return "insufficient_data";
    case RTXMON_METRIC_STATE_INPUT_UNAVAILABLE:
        return "input_unavailable";
    case RTXMON_METRIC_STATE_UNKNOWN:
    default:
        return "unknown";
    }
}

const char *RTXMON_CALL rtxmon_last_error(void)
{
    return rtxmon_error;
}

rtxmon_status_t RTXMON_CALL rtxmon_context_create(rtxmon_context_t **out_context)
{
    char nvapi_error[256];
    rtxmon_context_t *context;
    rtxmon_nvapi_loader_status_t nvapi_loader_status;
    rtxmon_nvml_loader_status_t loader_status;
    nvmlReturn_t result;

    rtxmon_clear_error();

    if (out_context == NULL) {
        rtxmon_set_error("out_context is null");
        return RTXMON_STATUS_INVALID_ARGUMENT;
    }

    *out_context = NULL;
    context = (rtxmon_context_t *)calloc(1U, sizeof(*context));
    if (context == NULL) {
        rtxmon_set_error("could not allocate rtxmon context");
        return RTXMON_STATUS_OUT_OF_MEMORY;
    }

    context->nvapi_initialize_status = RTXMON_NVAPI_LIBRARY_NOT_FOUND;

    loader_status = rtxmon_nvml_load(
        &context->nvml,
        rtxmon_error,
        sizeof(rtxmon_error));

    if (loader_status != RTXMON_NVML_LOADER_OK) {
        free(context);
        return loader_status == RTXMON_NVML_LOADER_LIBRARY_NOT_FOUND
            ? RTXMON_STATUS_BACKEND_NOT_FOUND
            : RTXMON_STATUS_BACKEND_SYMBOL_MISSING;
    }

    result = context->nvml.init_v2();
    if (result != NVML_SUCCESS) {
        rtxmon_set_error(
            "nvmlInit_v2 failed: %s (%d)",
            rtxmon_nvml_error_text(context, result),
            result);
        rtxmon_nvml_unload(&context->nvml);
        free(context);
        return rtxmon_map_nvml_status(result);
    }

    nvapi_loader_status = rtxmon_nvapi_load(
        &context->nvapi,
        nvapi_error,
        sizeof(nvapi_error));
    if (nvapi_loader_status == RTXMON_NVAPI_LOADER_OK) {
        rtxmon_lock_nvapi_internal();
        context->nvapi_initialize_status = context->nvapi.initialize();
        rtxmon_unlock_nvapi_internal();
        if (context->nvapi_initialize_status == RTXMON_NVAPI_OK) {
            context->nvapi_initialized = 1;
        }
    } else if (nvapi_loader_status == RTXMON_NVAPI_LOADER_PLATFORM_UNAVAILABLE ||
               nvapi_loader_status == RTXMON_NVAPI_LOADER_INTERFACE_MISSING ||
               nvapi_loader_status == RTXMON_NVAPI_LOADER_QUERY_INTERFACE_MISSING) {
        context->nvapi_initialize_status = RTXMON_NVAPI_NO_IMPLEMENTATION;
    }

    context->initialized = 1;
    *out_context = context;
    rtxmon_clear_error();
    return RTXMON_STATUS_OK;
}

void RTXMON_CALL rtxmon_context_destroy(rtxmon_context_t *context)
{
    if (context == NULL) {
        return;
    }

    rtxmon_lock_nvapi_internal();
    rtxmon_nvapi_unload(&context->nvapi, context->nvapi_initialized);
    rtxmon_unlock_nvapi_internal();
    context->nvapi_initialized = 0;

    if (context->initialized != 0 && context->nvml.shutdown != NULL) {
        (void)context->nvml.shutdown();
    }

    context->initialized = 0;
    rtxmon_nvml_unload(&context->nvml);
    free(context);
}

rtxmon_status_t RTXMON_CALL
rtxmon_get_gpu_count(rtxmon_context_t *context, uint32_t *out_count)
{
    rtxmon_status_t status;
    nvmlReturn_t result;

    rtxmon_clear_error();
    status = rtxmon_validate_context(context);
    if (status != RTXMON_STATUS_OK) {
        return status;
    }

    if (out_count == NULL) {
        rtxmon_set_error("out_count is null");
        return RTXMON_STATUS_INVALID_ARGUMENT;
    }

    result = context->nvml.device_get_count_v2(out_count);
    if (result != NVML_SUCCESS) {
        rtxmon_set_error(
            "nvmlDeviceGetCount_v2 failed: %s (%d)",
            rtxmon_nvml_error_text(context, result),
            result);
        return rtxmon_map_nvml_status(result);
    }

    return RTXMON_STATUS_OK;
}

rtxmon_status_t RTXMON_CALL
rtxmon_get_gpu_info(
    rtxmon_context_t *context,
    uint32_t gpu_index,
    rtxmon_gpu_info_t *out_info)
{
    rtxmon_gpu_info_t info;
    rtxmon_status_t status;
    nvmlDevice_t device = NULL;
    nvmlReturn_t result;

    rtxmon_clear_error();
    status = rtxmon_validate_context(context);
    if (status != RTXMON_STATUS_OK) {
        return status;
    }

    if (out_info == NULL) {
        rtxmon_set_error("out_info is null");
        return RTXMON_STATUS_INVALID_ARGUMENT;
    }

    if (out_info->struct_size < sizeof(rtxmon_gpu_info_t)) {
        rtxmon_set_error(
            "rtxmon_gpu_info_t size mismatch: caller=%u, required=%u",
            out_info->struct_size,
            (uint32_t)sizeof(rtxmon_gpu_info_t));
        return RTXMON_STATUS_ABI_MISMATCH;
    }

    status = rtxmon_get_device(context, gpu_index, &device);
    if (status != RTXMON_STATUS_OK) {
        return status;
    }

    (void)memset(&info, 0, sizeof(info));
    info.struct_size = (uint32_t)sizeof(info);
    info.index = gpu_index;

    result = context->nvml.device_get_name(device, info.name, (uint32_t)sizeof(info.name));
    if (result != NVML_SUCCESS) {
        rtxmon_set_error(
            "nvmlDeviceGetName failed: %s (%d)",
            rtxmon_nvml_error_text(context, result),
            result);
        return rtxmon_map_nvml_status(result);
    }

    result = context->nvml.device_get_uuid(device, info.uuid, (uint32_t)sizeof(info.uuid));
    if (result != NVML_SUCCESS) {
        rtxmon_set_error(
            "nvmlDeviceGetUUID failed: %s (%d)",
            rtxmon_nvml_error_text(context, result),
            result);
        return rtxmon_map_nvml_status(result);
    }

    if (context->nvml.system_get_driver_version != NULL) {
        result = context->nvml.system_get_driver_version(
            info.driver_version,
            (uint32_t)sizeof(info.driver_version));
        if (result != NVML_SUCCESS) {
            (void)snprintf(info.driver_version, sizeof(info.driver_version), "unavailable");
        }
    } else {
        (void)snprintf(info.driver_version, sizeof(info.driver_version), "unavailable");
    }

    if (context->nvml.system_get_nvml_version != NULL) {
        result = context->nvml.system_get_nvml_version(
            info.nvml_version,
            (uint32_t)sizeof(info.nvml_version));
        if (result != NVML_SUCCESS) {
            (void)snprintf(info.nvml_version, sizeof(info.nvml_version), "unavailable");
        }
    } else {
        (void)snprintf(info.nvml_version, sizeof(info.nvml_version), "unavailable");
    }

    info.name[sizeof(info.name) - 1U] = '\0';
    info.uuid[sizeof(info.uuid) - 1U] = '\0';
    info.driver_version[sizeof(info.driver_version) - 1U] = '\0';
    info.nvml_version[sizeof(info.nvml_version) - 1U] = '\0';
    (void)memcpy(out_info, &info, sizeof(info));
    return RTXMON_STATUS_OK;
}

rtxmon_status_t RTXMON_CALL
rtxmon_get_board_identity(
    rtxmon_context_t *context,
    uint32_t gpu_index,
    rtxmon_board_identity_t *out_identity)
{
    const char *function_separator;
    char *parse_end = NULL;
    rtxmon_board_identity_t identity;
    rtxmon_nvml_pci_info_t pci;
    rtxmon_status_t status;
    nvmlDevice_t device = NULL;
    nvmlReturn_t result;

    rtxmon_clear_error();
    status = rtxmon_validate_context(context);
    if (status != RTXMON_STATUS_OK) {
        return status;
    }

    if (out_identity == NULL) {
        rtxmon_set_error("out_identity is null");
        return RTXMON_STATUS_INVALID_ARGUMENT;
    }

    if (out_identity->struct_size < sizeof(rtxmon_board_identity_t)) {
        rtxmon_set_error(
            "rtxmon_board_identity_t size mismatch: caller=%u, required=%u",
            out_identity->struct_size,
            (uint32_t)sizeof(rtxmon_board_identity_t));
        return RTXMON_STATUS_ABI_MISMATCH;
    }

    status = rtxmon_get_device(context, gpu_index, &device);
    if (status != RTXMON_STATUS_OK) {
        return status;
    }

    result = rtxmon_get_pci_info_internal(context, device, &pci);
    if (result != NVML_SUCCESS) {
        rtxmon_set_error(
            "nvmlDeviceGetPciInfo_v3 failed: %s (%d)",
            rtxmon_nvml_error_text(context, result),
            result);
        return result == NVML_ERROR_FUNCTION_NOT_FOUND
            ? RTXMON_STATUS_NOT_SUPPORTED
            : rtxmon_map_nvml_status(result);
    }

    (void)memset(&identity, 0, sizeof(identity));
    identity.struct_size = (uint32_t)sizeof(identity);
    identity.gpu_index = gpu_index;
    identity.pci_vendor_id = pci.pci_device_id & 0xffffU;
    identity.pci_device_id = (pci.pci_device_id >> 16U) & 0xffffU;
    identity.pci_subsystem_vendor_id = pci.pci_subsystem_id & 0xffffU;
    identity.pci_subsystem_device_id = (pci.pci_subsystem_id >> 16U) & 0xffffU;
    identity.pci_domain = pci.domain;
    identity.pci_bus = pci.bus;
    identity.pci_device = pci.device;
    identity.flags = RTXMON_BOARD_IDENTITY_PCI_VALID;
    (void)snprintf(identity.pci_bus_id, sizeof(identity.pci_bus_id), "%s", pci.bus_id);

    function_separator = strrchr(identity.pci_bus_id, '.');
    if (function_separator != NULL && function_separator[1] != '\0') {
        const unsigned long parsed = strtoul(function_separator + 1, &parse_end, 16);
        if (parse_end != function_separator + 1 && *parse_end == '\0' && parsed <= 7UL) {
            identity.pci_function = (uint32_t)parsed;
        }
    }

    if (context->nvml.device_get_vbios_version != NULL) {
        result = context->nvml.device_get_vbios_version(
            device,
            identity.vbios_version,
            (uint32_t)sizeof(identity.vbios_version));
        if (result == NVML_SUCCESS) {
            identity.flags |= RTXMON_BOARD_IDENTITY_VBIOS_VALID;
        } else {
            identity.vbios_version[0] = '\0';
        }
    }

    identity.pci_bus_id[sizeof(identity.pci_bus_id) - 1U] = '\0';
    identity.vbios_version[sizeof(identity.vbios_version) - 1U] = '\0';
    (void)memcpy(out_identity, &identity, sizeof(identity));
    return RTXMON_STATUS_OK;
}

rtxmon_status_t RTXMON_CALL
rtxmon_read_gpu_die_temperature(
    rtxmon_context_t *context,
    uint32_t gpu_index,
    rtxmon_temperature_sample_t *out_sample)
{
    rtxmon_temperature_sample_t sample;
    rtxmon_nvml_temperature_v1_t versioned_temperature;
    rtxmon_status_t status;
    nvmlDevice_t device = NULL;
    nvmlReturn_t versioned_result = NVML_ERROR_FUNCTION_NOT_FOUND;
    nvmlReturn_t legacy_result = NVML_ERROR_FUNCTION_NOT_FOUND;
    uint32_t legacy_temperature = 0U;

    rtxmon_clear_error();
    status = rtxmon_validate_context(context);
    if (status != RTXMON_STATUS_OK) {
        return status;
    }

    if (out_sample == NULL) {
        rtxmon_set_error("out_sample is null");
        return RTXMON_STATUS_INVALID_ARGUMENT;
    }

    if (out_sample->struct_size < sizeof(rtxmon_temperature_sample_t)) {
        rtxmon_set_error(
            "rtxmon_temperature_sample_t size mismatch: caller=%u, required=%u",
            out_sample->struct_size,
            (uint32_t)sizeof(rtxmon_temperature_sample_t));
        return RTXMON_STATUS_ABI_MISMATCH;
    }

    status = rtxmon_get_device(context, gpu_index, &device);
    if (status != RTXMON_STATUS_OK) {
        return status;
    }

    (void)memset(&sample, 0, sizeof(sample));
    sample.struct_size = (uint32_t)sizeof(sample);
    sample.gpu_index = gpu_index;
    sample.sensor_kind = RTXMON_SENSOR_GPU_DIE;

    if (context->nvml.device_get_temperature_v != NULL) {
        (void)memset(&versioned_temperature, 0, sizeof(versioned_temperature));
        versioned_temperature.version = RTXMON_NVML_TEMPERATURE_V1_VERSION;
        versioned_temperature.sensor_type = NVML_TEMPERATURE_GPU;

        versioned_result = context->nvml.device_get_temperature_v(
            device,
            &versioned_temperature);

        if (versioned_result == NVML_SUCCESS) {
            sample.temperature_c = versioned_temperature.temperature;
            sample.backend = RTXMON_BACKEND_NVML_TEMPERATURE_V1;
            sample.timestamp_unix_ms = rtxmon_timestamp_unix_ms_internal();
            (void)memcpy(out_sample, &sample, sizeof(sample));
            return RTXMON_STATUS_OK;
        }
    }

    if (context->nvml.device_get_temperature != NULL) {
        legacy_result = context->nvml.device_get_temperature(
            device,
            NVML_TEMPERATURE_GPU,
            &legacy_temperature);

        if (legacy_result == NVML_SUCCESS) {
            sample.temperature_c = (int32_t)legacy_temperature;
            sample.backend = RTXMON_BACKEND_NVML_TEMPERATURE_LEGACY;
            sample.timestamp_unix_ms = rtxmon_timestamp_unix_ms_internal();
            (void)memcpy(out_sample, &sample, sizeof(sample));
            return RTXMON_STATUS_OK;
        }
    }

    rtxmon_set_error(
        "NVML die temperature query failed: versioned=%s (%d), legacy=%s (%d)",
        rtxmon_nvml_error_text(context, versioned_result),
        versioned_result,
        rtxmon_nvml_error_text(context, legacy_result),
        legacy_result);

    if (legacy_result != NVML_ERROR_FUNCTION_NOT_FOUND) {
        return rtxmon_map_nvml_status(legacy_result);
    }

    return rtxmon_map_nvml_status(versioned_result);
}

rtxmon_status_t RTXMON_CALL
rtxmon_scan_thermal_capabilities(
    rtxmon_context_t *context,
    uint32_t gpu_index,
    rtxmon_thermal_report_t *out_report)
{
    rtxmon_thermal_report_t report;
    rtxmon_status_t status;
    nvmlDevice_t device = NULL;

    rtxmon_clear_error();
    status = rtxmon_validate_context(context);
    if (status != RTXMON_STATUS_OK) {
        return status;
    }

    if (out_report == NULL) {
        rtxmon_set_error("out_report is null");
        return RTXMON_STATUS_INVALID_ARGUMENT;
    }

    if (out_report->struct_size < sizeof(rtxmon_thermal_report_t)) {
        rtxmon_set_error(
            "rtxmon_thermal_report_t size mismatch: caller=%u, required=%u",
            out_report->struct_size,
            (uint32_t)sizeof(rtxmon_thermal_report_t));
        return RTXMON_STATUS_ABI_MISMATCH;
    }

    status = rtxmon_get_device(context, gpu_index, &device);
    if (status != RTXMON_STATUS_OK) {
        return status;
    }

    rtxmon_collect_thermal_capabilities(context, gpu_index, device, &report);
    (void)memcpy(out_report, &report, sizeof(report));
    return RTXMON_STATUS_OK;
}
