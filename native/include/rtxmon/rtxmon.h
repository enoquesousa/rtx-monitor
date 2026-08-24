#ifndef RTXMON_RTXMON_H
#define RTXMON_RTXMON_H

#include <stdint.h>

#if defined(_WIN32)
#if defined(RTXMON_BUILDING_NATIVE)
#define RTXMON_API __declspec(dllexport)
#else
#define RTXMON_API __declspec(dllimport)
#endif
#define RTXMON_CALL __cdecl
#else
#define RTXMON_API __attribute__((visibility("default")))
#define RTXMON_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define RTXMON_ABI_VERSION 1U
#define RTXMON_TEXT_CAPACITY 96U

typedef struct rtxmon_context rtxmon_context_t;

typedef enum rtxmon_status {
    RTXMON_STATUS_OK = 0,
    RTXMON_STATUS_INVALID_ARGUMENT = 1,
    RTXMON_STATUS_OUT_OF_MEMORY = 2,
    RTXMON_STATUS_BACKEND_NOT_FOUND = 3,
    RTXMON_STATUS_BACKEND_SYMBOL_MISSING = 4,
    RTXMON_STATUS_DRIVER_NOT_LOADED = 5,
    RTXMON_STATUS_NO_PERMISSION = 6,
    RTXMON_STATUS_GPU_NOT_FOUND = 7,
    RTXMON_STATUS_NOT_SUPPORTED = 8,
    RTXMON_STATUS_GPU_LOST = 9,
    RTXMON_STATUS_BACKEND_ERROR = 10,
    RTXMON_STATUS_ABI_MISMATCH = 11
} rtxmon_status_t;

typedef enum rtxmon_sensor_kind {
    RTXMON_SENSOR_GPU_DIE = 0
} rtxmon_sensor_kind_t;

typedef enum rtxmon_temperature_backend {
    RTXMON_BACKEND_NVML_TEMPERATURE_V1 = 1,
    RTXMON_BACKEND_NVML_TEMPERATURE_LEGACY = 2
} rtxmon_temperature_backend_t;

typedef struct rtxmon_gpu_info {
    uint32_t struct_size;
    uint32_t index;
    char name[RTXMON_TEXT_CAPACITY];
    char uuid[RTXMON_TEXT_CAPACITY];
    char driver_version[RTXMON_TEXT_CAPACITY];
    char nvml_version[RTXMON_TEXT_CAPACITY];
} rtxmon_gpu_info_t;

typedef struct rtxmon_temperature_sample {
    uint32_t struct_size;
    uint32_t gpu_index;
    int32_t temperature_c;
    uint32_t sensor_kind;
    uint32_t backend;
    uint32_t reserved;
    uint64_t timestamp_unix_ms;
} rtxmon_temperature_sample_t;

RTXMON_API uint32_t RTXMON_CALL rtxmon_abi_version(void);
RTXMON_API const char *RTXMON_CALL rtxmon_status_string(rtxmon_status_t status);
RTXMON_API const char *RTXMON_CALL rtxmon_temperature_backend_string(uint32_t backend);

/*
 * Returns a thread-local diagnostic for the most recent API call on the
 * current thread. The pointer remains valid until the next rtxmon call on
 * that thread.
 */
RTXMON_API const char *RTXMON_CALL rtxmon_last_error(void);

RTXMON_API rtxmon_status_t RTXMON_CALL
rtxmon_context_create(rtxmon_context_t **out_context);

RTXMON_API void RTXMON_CALL
rtxmon_context_destroy(rtxmon_context_t *context);

RTXMON_API rtxmon_status_t RTXMON_CALL
rtxmon_get_gpu_count(rtxmon_context_t *context, uint32_t *out_count);

/* Set out_info->struct_size to sizeof(rtxmon_gpu_info_t) before calling. */
RTXMON_API rtxmon_status_t RTXMON_CALL
rtxmon_get_gpu_info(
    rtxmon_context_t *context,
    uint32_t gpu_index,
    rtxmon_gpu_info_t *out_info);

/* Set out_sample->struct_size to sizeof(rtxmon_temperature_sample_t). */
RTXMON_API rtxmon_status_t RTXMON_CALL
rtxmon_read_gpu_die_temperature(
    rtxmon_context_t *context,
    uint32_t gpu_index,
    rtxmon_temperature_sample_t *out_sample);

#ifdef __cplusplus
}
#endif

#endif
