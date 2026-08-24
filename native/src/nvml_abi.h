#ifndef RTXMON_NVML_ABI_H
#define RTXMON_NVML_ABI_H

#include <stdint.h>

#if defined(_WIN32)
#define RTXMON_NVML_CALL __cdecl
#else
#define RTXMON_NVML_CALL
#endif

typedef int nvmlReturn_t;
typedef struct nvmlDevice_st *nvmlDevice_t;

enum {
    NVML_SUCCESS = 0,
    NVML_ERROR_UNINITIALIZED = 1,
    NVML_ERROR_INVALID_ARGUMENT = 2,
    NVML_ERROR_NOT_SUPPORTED = 3,
    NVML_ERROR_NO_PERMISSION = 4,
    NVML_ERROR_ALREADY_INITIALIZED = 5,
    NVML_ERROR_NOT_FOUND = 6,
    NVML_ERROR_INSUFFICIENT_SIZE = 7,
    NVML_ERROR_INSUFFICIENT_POWER = 8,
    NVML_ERROR_DRIVER_NOT_LOADED = 9,
    NVML_ERROR_TIMEOUT = 10,
    NVML_ERROR_LIBRARY_NOT_FOUND = 12,
    NVML_ERROR_FUNCTION_NOT_FOUND = 13,
    NVML_ERROR_GPU_IS_LOST = 15,
    NVML_ERROR_LIB_RM_VERSION_MISMATCH = 18,
    NVML_ERROR_ARGUMENT_VERSION_MISMATCH = 25,
    NVML_ERROR_DEPRECATED = 26,
    NVML_ERROR_GPU_NOT_FOUND = 28,
    NVML_ERROR_UNKNOWN = 999
};

enum {
    NVML_TEMPERATURE_GPU = 0
};

typedef struct rtxmon_nvml_temperature_v1 {
    uint32_t version;
    int sensor_type;
    int temperature;
} rtxmon_nvml_temperature_v1_t;

#define RTXMON_NVML_TEMPERATURE_V1_VERSION \
    ((uint32_t)(sizeof(rtxmon_nvml_temperature_v1_t) | (1U << 24U)))

typedef nvmlReturn_t(RTXMON_NVML_CALL *rtxmon_nvml_init_v2_fn)(void);
typedef nvmlReturn_t(RTXMON_NVML_CALL *rtxmon_nvml_shutdown_fn)(void);
typedef nvmlReturn_t(RTXMON_NVML_CALL *rtxmon_nvml_device_get_count_v2_fn)(uint32_t *count);
typedef nvmlReturn_t(RTXMON_NVML_CALL *rtxmon_nvml_device_get_handle_by_index_v2_fn)(
    uint32_t index,
    nvmlDevice_t *device);
typedef nvmlReturn_t(RTXMON_NVML_CALL *rtxmon_nvml_device_get_name_fn)(
    nvmlDevice_t device,
    char *name,
    uint32_t length);
typedef nvmlReturn_t(RTXMON_NVML_CALL *rtxmon_nvml_device_get_uuid_fn)(
    nvmlDevice_t device,
    char *uuid,
    uint32_t length);
typedef nvmlReturn_t(RTXMON_NVML_CALL *rtxmon_nvml_system_get_driver_version_fn)(
    char *version,
    uint32_t length);
typedef nvmlReturn_t(RTXMON_NVML_CALL *rtxmon_nvml_system_get_nvml_version_fn)(
    char *version,
    uint32_t length);
typedef nvmlReturn_t(RTXMON_NVML_CALL *rtxmon_nvml_device_get_temperature_v_fn)(
    nvmlDevice_t device,
    rtxmon_nvml_temperature_v1_t *temperature);
typedef nvmlReturn_t(RTXMON_NVML_CALL *rtxmon_nvml_device_get_temperature_fn)(
    nvmlDevice_t device,
    int sensor_type,
    uint32_t *temperature);
typedef const char *(RTXMON_NVML_CALL *rtxmon_nvml_error_string_fn)(nvmlReturn_t result);

#endif
