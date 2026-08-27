#include "nvml_loader.h"

#include <stdio.h>
#include <string.h>

#if defined(_WIN32)
#include <windows.h>
#include <wchar.h>
#else
#include <dlfcn.h>
#endif

static void rtxmon_loader_error(char *error, size_t capacity, const char *message)
{
    if (error == NULL || capacity == 0U) {
        return;
    }

    (void)snprintf(error, capacity, "%s", message);
    error[capacity - 1U] = '\0';
}

#if defined(_WIN32)
static HMODULE rtxmon_load_windows_nvml(void)
{
    wchar_t path[32768];
    UINT system_length = GetSystemDirectoryW(path, (UINT)(sizeof(path) / sizeof(path[0])));

    if (system_length > 0U && system_length < (UINT)(sizeof(path) / sizeof(path[0]))) {
        if (wcscat_s(path, sizeof(path) / sizeof(path[0]), L"\\nvml.dll") == 0) {
            HMODULE module = LoadLibraryW(path);
            if (module != NULL) {
                return module;
            }
        }
    }

    {
        DWORD program_files_length = GetEnvironmentVariableW(
            L"ProgramW6432",
            path,
            (DWORD)(sizeof(path) / sizeof(path[0])));

        if (program_files_length > 0U &&
            program_files_length < (DWORD)(sizeof(path) / sizeof(path[0])) &&
            wcscat_s(
                path,
                sizeof(path) / sizeof(path[0]),
                L"\\NVIDIA Corporation\\NVSMI\\nvml.dll") == 0) {
            return LoadLibraryW(path);
        }
    }

    return NULL;
}

static void *rtxmon_get_symbol(void *library, const char *name)
{
    return (void *)GetProcAddress((HMODULE)library, name);
}

static void rtxmon_close_library(void *library)
{
    if (library != NULL) {
        (void)FreeLibrary((HMODULE)library);
    }
}
#else
static void *rtxmon_load_linux_nvml(void)
{
    void *library = dlopen("libnvidia-ml.so.1", RTLD_NOW | RTLD_LOCAL);
    if (library == NULL) {
        library = dlopen("libnvidia-ml.so", RTLD_NOW | RTLD_LOCAL);
    }
    return library;
}

static void *rtxmon_get_symbol(void *library, const char *name)
{
    return dlsym(library, name);
}

static void rtxmon_close_library(void *library)
{
    if (library != NULL) {
        (void)dlclose(library);
    }
}
#endif

#define RTXMON_RESOLVE_REQUIRED(api, member, type, symbol_name)                  \
    do {                                                                         \
        (api)->member = (type)rtxmon_get_symbol((api)->library, (symbol_name));  \
        if ((api)->member == NULL) {                                              \
            (void)snprintf(                                                       \
                error,                                                            \
                error_capacity,                                                   \
                "NVML symbol is missing: %s",                                    \
                (symbol_name));                                                   \
            if (error != NULL && error_capacity > 0U) {                          \
                error[error_capacity - 1U] = '\0';                               \
            }                                                                     \
            rtxmon_nvml_unload(api);                                              \
            return RTXMON_NVML_LOADER_SYMBOL_MISSING;                             \
        }                                                                         \
    } while (0)

#define RTXMON_RESOLVE_OPTIONAL(api, member, type, symbol_name)                 \
    do {                                                                         \
        (api)->member = (type)rtxmon_get_symbol((api)->library, (symbol_name));  \
    } while (0)

rtxmon_nvml_loader_status_t rtxmon_nvml_load(
    rtxmon_nvml_api_t *api,
    char *error,
    size_t error_capacity)
{
    if (api == NULL) {
        rtxmon_loader_error(error, error_capacity, "NVML loader received a null API table");
        return RTXMON_NVML_LOADER_SYMBOL_MISSING;
    }

    (void)memset(api, 0, sizeof(*api));

#if defined(_WIN32)
    api->library = (void *)rtxmon_load_windows_nvml();
#else
    api->library = rtxmon_load_linux_nvml();
#endif

    if (api->library == NULL) {
        rtxmon_loader_error(
            error,
            error_capacity,
            "NVML library was not found in the NVIDIA driver installation");
        return RTXMON_NVML_LOADER_LIBRARY_NOT_FOUND;
    }

    RTXMON_RESOLVE_REQUIRED(api, init_v2, rtxmon_nvml_init_v2_fn, "nvmlInit_v2");
    RTXMON_RESOLVE_REQUIRED(api, shutdown, rtxmon_nvml_shutdown_fn, "nvmlShutdown");
    RTXMON_RESOLVE_REQUIRED(
        api,
        device_get_count_v2,
        rtxmon_nvml_device_get_count_v2_fn,
        "nvmlDeviceGetCount_v2");
    RTXMON_RESOLVE_REQUIRED(
        api,
        device_get_handle_by_index_v2,
        rtxmon_nvml_device_get_handle_by_index_v2_fn,
        "nvmlDeviceGetHandleByIndex_v2");
    RTXMON_RESOLVE_REQUIRED(
        api,
        device_get_name,
        rtxmon_nvml_device_get_name_fn,
        "nvmlDeviceGetName");
    RTXMON_RESOLVE_REQUIRED(
        api,
        device_get_uuid,
        rtxmon_nvml_device_get_uuid_fn,
        "nvmlDeviceGetUUID");
    RTXMON_RESOLVE_REQUIRED(
        api,
        error_string,
        rtxmon_nvml_error_string_fn,
        "nvmlErrorString");

    RTXMON_RESOLVE_OPTIONAL(
        api,
        device_get_pci_info_v3,
        rtxmon_nvml_device_get_pci_info_v3_fn,
        "nvmlDeviceGetPciInfo_v3");
    RTXMON_RESOLVE_OPTIONAL(
        api,
        device_get_vbios_version,
        rtxmon_nvml_device_get_vbios_version_fn,
        "nvmlDeviceGetVbiosVersion");
    RTXMON_RESOLVE_OPTIONAL(
        api,
        system_get_driver_version,
        rtxmon_nvml_system_get_driver_version_fn,
        "nvmlSystemGetDriverVersion");
    RTXMON_RESOLVE_OPTIONAL(
        api,
        system_get_nvml_version,
        rtxmon_nvml_system_get_nvml_version_fn,
        "nvmlSystemGetNVMLVersion");
    RTXMON_RESOLVE_OPTIONAL(
        api,
        device_get_temperature_v,
        rtxmon_nvml_device_get_temperature_v_fn,
        "nvmlDeviceGetTemperatureV");
    RTXMON_RESOLVE_OPTIONAL(
        api,
        device_get_temperature,
        rtxmon_nvml_device_get_temperature_fn,
        "nvmlDeviceGetTemperature");
    RTXMON_RESOLVE_OPTIONAL(
        api,
        device_get_temperature_threshold,
        rtxmon_nvml_device_get_temperature_threshold_fn,
        "nvmlDeviceGetTemperatureThreshold");
    RTXMON_RESOLVE_OPTIONAL(
        api,
        device_get_thermal_settings,
        rtxmon_nvml_device_get_thermal_settings_fn,
        "nvmlDeviceGetThermalSettings");
    RTXMON_RESOLVE_OPTIONAL(
        api,
        device_get_field_values,
        rtxmon_nvml_device_get_field_values_fn,
        "nvmlDeviceGetFieldValues");
    RTXMON_RESOLVE_OPTIONAL(
        api,
        device_get_clock_info,
        rtxmon_nvml_device_get_clock_info_fn,
        "nvmlDeviceGetClockInfo");
    RTXMON_RESOLVE_OPTIONAL(
        api,
        device_get_utilization_rates,
        rtxmon_nvml_device_get_utilization_rates_fn,
        "nvmlDeviceGetUtilizationRates");
    RTXMON_RESOLVE_OPTIONAL(
        api,
        device_get_memory_info,
        rtxmon_nvml_device_get_memory_info_fn,
        "nvmlDeviceGetMemoryInfo");
    RTXMON_RESOLVE_OPTIONAL(
        api,
        device_get_num_fans,
        rtxmon_nvml_device_get_num_fans_fn,
        "nvmlDeviceGetNumFans");
    RTXMON_RESOLVE_OPTIONAL(
        api,
        device_get_fan_speed_v2,
        rtxmon_nvml_device_get_fan_speed_v2_fn,
        "nvmlDeviceGetFanSpeed_v2");
    RTXMON_RESOLVE_OPTIONAL(
        api,
        device_get_fan_speed,
        rtxmon_nvml_device_get_fan_speed_fn,
        "nvmlDeviceGetFanSpeed");
    RTXMON_RESOLVE_OPTIONAL(
        api,
        device_get_performance_state,
        rtxmon_nvml_device_get_performance_state_fn,
        "nvmlDeviceGetPerformanceState");
    RTXMON_RESOLVE_OPTIONAL(
        api,
        device_get_current_clocks_event_reasons,
        rtxmon_nvml_device_get_clock_reasons_fn,
        "nvmlDeviceGetCurrentClocksEventReasons");
    RTXMON_RESOLVE_OPTIONAL(
        api,
        device_get_current_clocks_throttle_reasons,
        rtxmon_nvml_device_get_clock_reasons_fn,
        "nvmlDeviceGetCurrentClocksThrottleReasons");
    RTXMON_RESOLVE_OPTIONAL(
        api,
        device_get_supported_clocks_event_reasons,
        rtxmon_nvml_device_get_clock_reasons_fn,
        "nvmlDeviceGetSupportedClocksEventReasons");
    RTXMON_RESOLVE_OPTIONAL(
        api,
        device_get_supported_clocks_throttle_reasons,
        rtxmon_nvml_device_get_clock_reasons_fn,
        "nvmlDeviceGetSupportedClocksThrottleReasons");
    RTXMON_RESOLVE_OPTIONAL(
        api,
        device_get_encoder_utilization,
        rtxmon_nvml_device_get_engine_utilization_fn,
        "nvmlDeviceGetEncoderUtilization");
    RTXMON_RESOLVE_OPTIONAL(
        api,
        device_get_decoder_utilization,
        rtxmon_nvml_device_get_engine_utilization_fn,
        "nvmlDeviceGetDecoderUtilization");

    if (api->device_get_temperature_v == NULL && api->device_get_temperature == NULL) {
        rtxmon_loader_error(
            error,
            error_capacity,
            "NVML exposes neither nvmlDeviceGetTemperatureV nor its legacy fallback");
        rtxmon_nvml_unload(api);
        return RTXMON_NVML_LOADER_SYMBOL_MISSING;
    }

    rtxmon_loader_error(error, error_capacity, "");
    return RTXMON_NVML_LOADER_OK;
}

void rtxmon_nvml_unload(rtxmon_nvml_api_t *api)
{
    if (api == NULL) {
        return;
    }

    rtxmon_close_library(api->library);
    (void)memset(api, 0, sizeof(*api));
}
