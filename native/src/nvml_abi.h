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

#define RTXMON_NVML_MAX_THERMAL_SENSORS 3U

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

enum {
    RTXMON_NVML_TEMPERATURE_THRESHOLD_GPU_MAX = 3
};

enum {
    RTXMON_NVML_CLOCK_GRAPHICS = 0,
    RTXMON_NVML_CLOCK_SM = 1,
    RTXMON_NVML_CLOCK_MEMORY = 2,
    RTXMON_NVML_CLOCK_VIDEO = 3
};

enum {
    RTXMON_NVML_THERMAL_TARGET_NONE = 0,
    RTXMON_NVML_THERMAL_TARGET_GPU = 1,
    RTXMON_NVML_THERMAL_TARGET_MEMORY = 2,
    RTXMON_NVML_THERMAL_TARGET_POWER_SUPPLY = 4,
    RTXMON_NVML_THERMAL_TARGET_BOARD = 8,
    RTXMON_NVML_THERMAL_TARGET_VCD_BOARD = 9,
    RTXMON_NVML_THERMAL_TARGET_VCD_INLET = 10,
    RTXMON_NVML_THERMAL_TARGET_VCD_OUTLET = 11,
    RTXMON_NVML_THERMAL_TARGET_ALL = 15,
    RTXMON_NVML_THERMAL_TARGET_UNKNOWN = -1
};

enum {
    RTXMON_NVML_THERMAL_CONTROLLER_NONE = 0,
    RTXMON_NVML_THERMAL_CONTROLLER_GPU_INTERNAL = 1,
    RTXMON_NVML_THERMAL_CONTROLLER_ADM1032 = 2,
    RTXMON_NVML_THERMAL_CONTROLLER_ADT7461 = 3,
    RTXMON_NVML_THERMAL_CONTROLLER_MAX6649 = 4,
    RTXMON_NVML_THERMAL_CONTROLLER_MAX1617 = 5,
    RTXMON_NVML_THERMAL_CONTROLLER_LM99 = 6,
    RTXMON_NVML_THERMAL_CONTROLLER_LM89 = 7,
    RTXMON_NVML_THERMAL_CONTROLLER_LM64 = 8,
    RTXMON_NVML_THERMAL_CONTROLLER_G781 = 9,
    RTXMON_NVML_THERMAL_CONTROLLER_ADT7473 = 10,
    RTXMON_NVML_THERMAL_CONTROLLER_SBMAX6649 = 11,
    RTXMON_NVML_THERMAL_CONTROLLER_VBIOSEVT = 12,
    RTXMON_NVML_THERMAL_CONTROLLER_OS = 13,
    RTXMON_NVML_THERMAL_CONTROLLER_NVSYSCON_CANOAS = 14,
    RTXMON_NVML_THERMAL_CONTROLLER_NVSYSCON_E551 = 15,
    RTXMON_NVML_THERMAL_CONTROLLER_MAX6649R = 16,
    RTXMON_NVML_THERMAL_CONTROLLER_ADT7473S = 17,
    RTXMON_NVML_THERMAL_CONTROLLER_UNKNOWN = -1
};

enum {
    RTXMON_NVML_VALUE_TYPE_DOUBLE = 0,
    RTXMON_NVML_VALUE_TYPE_UNSIGNED_INT = 1,
    RTXMON_NVML_VALUE_TYPE_UNSIGNED_LONG = 2,
    RTXMON_NVML_VALUE_TYPE_UNSIGNED_LONG_LONG = 3,
    RTXMON_NVML_VALUE_TYPE_SIGNED_LONG_LONG = 4,
    RTXMON_NVML_VALUE_TYPE_SIGNED_INT = 5,
    RTXMON_NVML_VALUE_TYPE_UNSIGNED_SHORT = 6
};

enum {
    RTXMON_NVML_FI_DEV_MEMORY_TEMP = 82,
    RTXMON_NVML_FI_DEV_TOTAL_ENERGY_CONSUMPTION = 83,
    RTXMON_NVML_FI_DEV_POWER_AVERAGE = 185,
    RTXMON_NVML_FI_DEV_POWER_INSTANT = 186,
    RTXMON_NVML_FI_DEV_POWER_MIN_LIMIT = 187,
    RTXMON_NVML_FI_DEV_POWER_MAX_LIMIT = 188,
    RTXMON_NVML_FI_DEV_POWER_DEFAULT_LIMIT = 189,
    RTXMON_NVML_FI_DEV_POWER_CURRENT_LIMIT = 190,
    RTXMON_NVML_FI_DEV_POWER_REQUESTED_LIMIT = 192,
    RTXMON_NVML_FI_DEV_TEMPERATURE_SHUTDOWN_TLIMIT = 193,
    RTXMON_NVML_FI_DEV_TEMPERATURE_SLOWDOWN_TLIMIT = 194,
    RTXMON_NVML_FI_DEV_TEMPERATURE_MEM_MAX_TLIMIT = 195,
    RTXMON_NVML_FI_DEV_TEMPERATURE_GPU_MAX_TLIMIT = 196
};

typedef struct rtxmon_nvml_temperature_v1 {
    uint32_t version;
    int sensor_type;
    int temperature;
} rtxmon_nvml_temperature_v1_t;

typedef struct rtxmon_nvml_pci_info {
    char bus_id_legacy[16];
    uint32_t domain;
    uint32_t bus;
    uint32_t device;
    uint32_t pci_device_id;
    uint32_t pci_subsystem_id;
    char bus_id[32];
} rtxmon_nvml_pci_info_t;

typedef struct rtxmon_nvml_thermal_sensor {
    int controller;
    int default_min_temperature;
    int default_max_temperature;
    int current_temperature;
    int target;
} rtxmon_nvml_thermal_sensor_t;

typedef struct rtxmon_nvml_thermal_settings {
    uint32_t count;
    rtxmon_nvml_thermal_sensor_t sensors[RTXMON_NVML_MAX_THERMAL_SENSORS];
} rtxmon_nvml_thermal_settings_t;

typedef struct rtxmon_nvml_utilization {
    uint32_t gpu;
    uint32_t memory;
} rtxmon_nvml_utilization_t;

typedef struct rtxmon_nvml_memory {
    uint64_t total;
    uint64_t free;
    uint64_t used;
} rtxmon_nvml_memory_t;

typedef union rtxmon_nvml_value {
    double double_value;
    int32_t signed_int_value;
    uint32_t unsigned_int_value;
    unsigned long unsigned_long_value;
    uint64_t unsigned_long_long_value;
    int64_t signed_long_long_value;
    uint16_t unsigned_short_value;
} rtxmon_nvml_value_t;

typedef struct rtxmon_nvml_field_value {
    uint32_t field_id;
    uint32_t scope_id;
    int64_t timestamp;
    int64_t latency_usec;
    int value_type;
    nvmlReturn_t result;
    rtxmon_nvml_value_t value;
} rtxmon_nvml_field_value_t;

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
typedef nvmlReturn_t(RTXMON_NVML_CALL *rtxmon_nvml_device_get_pci_info_v3_fn)(
    nvmlDevice_t device,
    rtxmon_nvml_pci_info_t *pci_info);
typedef nvmlReturn_t(RTXMON_NVML_CALL *rtxmon_nvml_device_get_vbios_version_fn)(
    nvmlDevice_t device,
    char *version,
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
typedef nvmlReturn_t(RTXMON_NVML_CALL *rtxmon_nvml_device_get_temperature_threshold_fn)(
    nvmlDevice_t device,
    int threshold_type,
    uint32_t *temperature);
typedef nvmlReturn_t(RTXMON_NVML_CALL *rtxmon_nvml_device_get_thermal_settings_fn)(
    nvmlDevice_t device,
    uint32_t sensor_index,
    rtxmon_nvml_thermal_settings_t *settings);
typedef nvmlReturn_t(RTXMON_NVML_CALL *rtxmon_nvml_device_get_field_values_fn)(
    nvmlDevice_t device,
    int value_count,
    rtxmon_nvml_field_value_t *values);
typedef nvmlReturn_t(RTXMON_NVML_CALL *rtxmon_nvml_device_get_clock_info_fn)(
    nvmlDevice_t device,
    int clock_type,
    uint32_t *clock_mhz);
typedef nvmlReturn_t(RTXMON_NVML_CALL *rtxmon_nvml_device_get_utilization_rates_fn)(
    nvmlDevice_t device,
    rtxmon_nvml_utilization_t *utilization);
typedef nvmlReturn_t(RTXMON_NVML_CALL *rtxmon_nvml_device_get_memory_info_fn)(
    nvmlDevice_t device,
    rtxmon_nvml_memory_t *memory);
typedef nvmlReturn_t(RTXMON_NVML_CALL *rtxmon_nvml_device_get_num_fans_fn)(
    nvmlDevice_t device,
    uint32_t *fan_count);
typedef nvmlReturn_t(RTXMON_NVML_CALL *rtxmon_nvml_device_get_fan_speed_v2_fn)(
    nvmlDevice_t device,
    uint32_t fan_index,
    uint32_t *speed_percent);
typedef nvmlReturn_t(RTXMON_NVML_CALL *rtxmon_nvml_device_get_fan_speed_fn)(
    nvmlDevice_t device,
    uint32_t *speed_percent);
typedef nvmlReturn_t(RTXMON_NVML_CALL *rtxmon_nvml_device_get_performance_state_fn)(
    nvmlDevice_t device,
    int *performance_state);
typedef nvmlReturn_t(RTXMON_NVML_CALL *rtxmon_nvml_device_get_clock_reasons_fn)(
    nvmlDevice_t device,
    uint64_t *reasons);
typedef nvmlReturn_t(RTXMON_NVML_CALL *rtxmon_nvml_device_get_engine_utilization_fn)(
    nvmlDevice_t device,
    uint32_t *utilization,
    uint32_t *sampling_period_us);
typedef const char *(RTXMON_NVML_CALL *rtxmon_nvml_error_string_fn)(nvmlReturn_t result);

#endif
