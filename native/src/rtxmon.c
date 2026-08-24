#include <rtxmon/rtxmon.h>

#include "nvml_loader.h"

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

struct rtxmon_context {
    rtxmon_nvml_api_t nvml;
    int initialized;
};

static RTXMON_THREAD_LOCAL char rtxmon_error[RTXMON_ERROR_CAPACITY];

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

static uint64_t rtxmon_timestamp_unix_ms(void)
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
        return "NVML backend not found";
    case RTXMON_STATUS_BACKEND_SYMBOL_MISSING:
        return "required NVML symbol missing";
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
        return "NVML backend error";
    case RTXMON_STATUS_ABI_MISMATCH:
        return "NVML ABI version mismatch";
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

const char *RTXMON_CALL rtxmon_last_error(void)
{
    return rtxmon_error;
}

rtxmon_status_t RTXMON_CALL rtxmon_context_create(rtxmon_context_t **out_context)
{
    rtxmon_context_t *context;
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
            sample.timestamp_unix_ms = rtxmon_timestamp_unix_ms();
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
            sample.timestamp_unix_ms = rtxmon_timestamp_unix_ms();
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
