#ifndef RTXMON_NVAPI_LOADER_H
#define RTXMON_NVAPI_LOADER_H

#include <stddef.h>

#include "nvapi_abi.h"

typedef enum rtxmon_nvapi_loader_status {
    RTXMON_NVAPI_LOADER_OK = 0,
    RTXMON_NVAPI_LOADER_PLATFORM_UNAVAILABLE = 1,
    RTXMON_NVAPI_LOADER_LIBRARY_NOT_FOUND = 2,
    RTXMON_NVAPI_LOADER_QUERY_INTERFACE_MISSING = 3,
    RTXMON_NVAPI_LOADER_INTERFACE_MISSING = 4
} rtxmon_nvapi_loader_status_t;

typedef struct rtxmon_nvapi_api {
    void *library;
    rtxmon_nvapi_query_interface_fn query_interface;
    rtxmon_nvapi_initialize_fn initialize;
    rtxmon_nvapi_unload_fn unload;
    rtxmon_nvapi_enum_physical_gpus_fn enum_physical_gpus;
    rtxmon_nvapi_gpu_get_pci_identifiers_fn gpu_get_pci_identifiers;
    rtxmon_nvapi_gpu_get_bus_id_fn gpu_get_bus_id;
    rtxmon_nvapi_gpu_get_bus_slot_id_fn gpu_get_bus_slot_id;
    rtxmon_nvapi_gpu_get_thermal_settings_fn gpu_get_thermal_settings;
} rtxmon_nvapi_api_t;

rtxmon_nvapi_loader_status_t rtxmon_nvapi_load(
    rtxmon_nvapi_api_t *api,
    char *error,
    size_t error_capacity);

void rtxmon_nvapi_unload(rtxmon_nvapi_api_t *api, int initialized);

#endif
