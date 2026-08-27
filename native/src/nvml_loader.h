#ifndef RTXMON_NVML_LOADER_H
#define RTXMON_NVML_LOADER_H

#include <stddef.h>

#include "nvml_abi.h"

typedef enum rtxmon_nvml_loader_status {
    RTXMON_NVML_LOADER_OK = 0,
    RTXMON_NVML_LOADER_LIBRARY_NOT_FOUND = 1,
    RTXMON_NVML_LOADER_SYMBOL_MISSING = 2
} rtxmon_nvml_loader_status_t;

typedef struct rtxmon_nvml_api {
    void *library;
    rtxmon_nvml_init_v2_fn init_v2;
    rtxmon_nvml_shutdown_fn shutdown;
    rtxmon_nvml_device_get_count_v2_fn device_get_count_v2;
    rtxmon_nvml_device_get_handle_by_index_v2_fn device_get_handle_by_index_v2;
    rtxmon_nvml_device_get_name_fn device_get_name;
    rtxmon_nvml_device_get_uuid_fn device_get_uuid;
    rtxmon_nvml_device_get_pci_info_v3_fn device_get_pci_info_v3;
    rtxmon_nvml_device_get_vbios_version_fn device_get_vbios_version;
    rtxmon_nvml_system_get_driver_version_fn system_get_driver_version;
    rtxmon_nvml_system_get_nvml_version_fn system_get_nvml_version;
    rtxmon_nvml_device_get_temperature_v_fn device_get_temperature_v;
    rtxmon_nvml_device_get_temperature_fn device_get_temperature;
    rtxmon_nvml_device_get_temperature_threshold_fn device_get_temperature_threshold;
    rtxmon_nvml_device_get_thermal_settings_fn device_get_thermal_settings;
    rtxmon_nvml_device_get_field_values_fn device_get_field_values;
    rtxmon_nvml_device_get_clock_info_fn device_get_clock_info;
    rtxmon_nvml_device_get_utilization_rates_fn device_get_utilization_rates;
    rtxmon_nvml_device_get_memory_info_fn device_get_memory_info;
    rtxmon_nvml_device_get_num_fans_fn device_get_num_fans;
    rtxmon_nvml_device_get_fan_speed_v2_fn device_get_fan_speed_v2;
    rtxmon_nvml_device_get_fan_speed_fn device_get_fan_speed;
    rtxmon_nvml_device_get_performance_state_fn device_get_performance_state;
    rtxmon_nvml_device_get_clock_reasons_fn device_get_current_clocks_event_reasons;
    rtxmon_nvml_device_get_clock_reasons_fn device_get_current_clocks_throttle_reasons;
    rtxmon_nvml_device_get_clock_reasons_fn device_get_supported_clocks_event_reasons;
    rtxmon_nvml_device_get_clock_reasons_fn device_get_supported_clocks_throttle_reasons;
    rtxmon_nvml_device_get_engine_utilization_fn device_get_encoder_utilization;
    rtxmon_nvml_device_get_engine_utilization_fn device_get_decoder_utilization;
    rtxmon_nvml_error_string_fn error_string;
} rtxmon_nvml_api_t;

rtxmon_nvml_loader_status_t rtxmon_nvml_load(
    rtxmon_nvml_api_t *api,
    char *error,
    size_t error_capacity);

void rtxmon_nvml_unload(rtxmon_nvml_api_t *api);

#endif
