#ifndef RTXMON_NVAPI_ABI_H
#define RTXMON_NVAPI_ABI_H

#include <stdint.h>

#if defined(_WIN32)
#define RTXMON_NVAPI_CALL __cdecl
#else
#define RTXMON_NVAPI_CALL
#endif

#define RTXMON_NVAPI_MAX_PHYSICAL_GPUS 64U
#define RTXMON_NVAPI_MAX_THERMAL_SENSORS 3U

typedef int32_t rtxmon_nvapi_status_t;
typedef void *rtxmon_nvapi_gpu_handle_t;

enum {
    RTXMON_NVAPI_OK = 0,
    RTXMON_NVAPI_ERROR = -1,
    RTXMON_NVAPI_LIBRARY_NOT_FOUND = -2,
    RTXMON_NVAPI_NO_IMPLEMENTATION = -3,
    RTXMON_NVAPI_API_NOT_INITIALIZED = -4,
    RTXMON_NVAPI_INVALID_ARGUMENT = -5,
    RTXMON_NVAPI_NVIDIA_DEVICE_NOT_FOUND = -6,
    RTXMON_NVAPI_INCOMPATIBLE_STRUCT_VERSION = -9,
    RTXMON_NVAPI_NOT_SUPPORTED = -104
};

enum {
    RTXMON_NVAPI_THERMAL_TARGET_NONE = 0,
    RTXMON_NVAPI_THERMAL_TARGET_GPU = 1,
    RTXMON_NVAPI_THERMAL_TARGET_MEMORY = 2,
    RTXMON_NVAPI_THERMAL_TARGET_POWER_SUPPLY = 4,
    RTXMON_NVAPI_THERMAL_TARGET_BOARD = 8,
    RTXMON_NVAPI_THERMAL_TARGET_VCD_BOARD = 9,
    RTXMON_NVAPI_THERMAL_TARGET_VCD_INLET = 10,
    RTXMON_NVAPI_THERMAL_TARGET_VCD_OUTLET = 11,
    RTXMON_NVAPI_THERMAL_TARGET_ALL = 15,
    RTXMON_NVAPI_THERMAL_TARGET_UNKNOWN = -1
};

enum {
    RTXMON_NVAPI_THERMAL_CONTROLLER_NONE = 0,
    RTXMON_NVAPI_THERMAL_CONTROLLER_GPU_INTERNAL = 1,
    RTXMON_NVAPI_THERMAL_CONTROLLER_ADM1032 = 2,
    RTXMON_NVAPI_THERMAL_CONTROLLER_MAX6649 = 3,
    RTXMON_NVAPI_THERMAL_CONTROLLER_MAX1617 = 4,
    RTXMON_NVAPI_THERMAL_CONTROLLER_LM99 = 5,
    RTXMON_NVAPI_THERMAL_CONTROLLER_LM89 = 6,
    RTXMON_NVAPI_THERMAL_CONTROLLER_LM64 = 7,
    RTXMON_NVAPI_THERMAL_CONTROLLER_ADT7473 = 8,
    RTXMON_NVAPI_THERMAL_CONTROLLER_SBMAX6649 = 9,
    RTXMON_NVAPI_THERMAL_CONTROLLER_VBIOSEVT = 10,
    RTXMON_NVAPI_THERMAL_CONTROLLER_OS = 11,
    RTXMON_NVAPI_THERMAL_CONTROLLER_UNKNOWN = -1
};

enum {
    RTXMON_NVAPI_ID_INITIALIZE = 0x0150e828U,
    RTXMON_NVAPI_ID_UNLOAD = 0xd22bdd7eU,
    RTXMON_NVAPI_ID_ENUM_PHYSICAL_GPUS = 0xe5ac921fU,
    RTXMON_NVAPI_ID_GPU_GET_PCI_IDENTIFIERS = 0x2ddfb66eU,
    RTXMON_NVAPI_ID_GPU_GET_BUS_ID = 0x1be0b8e5U,
    RTXMON_NVAPI_ID_GPU_GET_BUS_SLOT_ID = 0x2a0a350fU,
    RTXMON_NVAPI_ID_GPU_GET_THERMAL_SETTINGS = 0xe3640a56U,
    RTXMON_NVAPI_ID_GPU_THERM_CHANNEL_GET_STATUS = 0x65fe3aadU,
    RTXMON_NVAPI_ID_GPU_VOLTAGE_STATUS = 0x465f9bcfU
};

typedef struct rtxmon_nvapi_therm_channel_status_v2 {
    uint32_t version;
    uint32_t channel_mask;
    uint32_t words[40];
} rtxmon_nvapi_therm_channel_status_v2_t;

#define RTXMON_NVAPI_THERM_CHANNEL_STATUS_V2_VERSION 0x000200a8U

typedef struct rtxmon_nvapi_voltage_status_v1 {
    uint32_t version;
    uint32_t words[18];
} rtxmon_nvapi_voltage_status_v1_t;

#define RTXMON_NVAPI_VOLTAGE_STATUS_V1_VERSION 0x0001004cU

typedef struct rtxmon_nvapi_thermal_sensor {
    int32_t controller;
    int32_t default_min_temperature;
    int32_t default_max_temperature;
    int32_t current_temperature;
    int32_t target;
} rtxmon_nvapi_thermal_sensor_t;

typedef struct rtxmon_nvapi_thermal_settings_v2 {
    uint32_t version;
    uint32_t count;
    rtxmon_nvapi_thermal_sensor_t sensors[RTXMON_NVAPI_MAX_THERMAL_SENSORS];
} rtxmon_nvapi_thermal_settings_v2_t;

#define RTXMON_NVAPI_MAKE_VERSION(type, version_number) \
    ((uint32_t)(sizeof(type) | ((uint32_t)(version_number) << 16U)))
#define RTXMON_NVAPI_THERMAL_SETTINGS_V2_VERSION \
    RTXMON_NVAPI_MAKE_VERSION(rtxmon_nvapi_thermal_settings_v2_t, 2U)

typedef void *(RTXMON_NVAPI_CALL *rtxmon_nvapi_query_interface_fn)(uint32_t interface_id);
typedef rtxmon_nvapi_status_t(RTXMON_NVAPI_CALL *rtxmon_nvapi_initialize_fn)(void);
typedef rtxmon_nvapi_status_t(RTXMON_NVAPI_CALL *rtxmon_nvapi_unload_fn)(void);
typedef rtxmon_nvapi_status_t(RTXMON_NVAPI_CALL *rtxmon_nvapi_enum_physical_gpus_fn)(
    rtxmon_nvapi_gpu_handle_t handles[RTXMON_NVAPI_MAX_PHYSICAL_GPUS],
    uint32_t *count);
typedef rtxmon_nvapi_status_t(RTXMON_NVAPI_CALL *rtxmon_nvapi_gpu_get_pci_identifiers_fn)(
    rtxmon_nvapi_gpu_handle_t handle,
    uint32_t *device_id,
    uint32_t *subsystem_id,
    uint32_t *revision_id,
    uint32_t *extended_device_id);
typedef rtxmon_nvapi_status_t(RTXMON_NVAPI_CALL *rtxmon_nvapi_gpu_get_bus_id_fn)(
    rtxmon_nvapi_gpu_handle_t handle,
    uint32_t *bus_id);
typedef rtxmon_nvapi_status_t(RTXMON_NVAPI_CALL *rtxmon_nvapi_gpu_get_bus_slot_id_fn)(
    rtxmon_nvapi_gpu_handle_t handle,
    uint32_t *slot_id);
typedef rtxmon_nvapi_status_t(RTXMON_NVAPI_CALL *rtxmon_nvapi_gpu_get_thermal_settings_fn)(
    rtxmon_nvapi_gpu_handle_t handle,
    uint32_t sensor_index,
    rtxmon_nvapi_thermal_settings_v2_t *settings);
typedef rtxmon_nvapi_status_t(RTXMON_NVAPI_CALL *rtxmon_nvapi_gpu_therm_channel_get_status_fn)(
    rtxmon_nvapi_gpu_handle_t handle,
    rtxmon_nvapi_therm_channel_status_v2_t *status);
typedef rtxmon_nvapi_status_t(RTXMON_NVAPI_CALL *rtxmon_nvapi_gpu_voltage_status_fn)(
    rtxmon_nvapi_gpu_handle_t handle,
    rtxmon_nvapi_voltage_status_v1_t *status);

#endif
