#ifndef RTXMON_INTERNAL_H
#define RTXMON_INTERNAL_H

#include <rtxmon/rtxmon.h>

#include "nvapi_loader.h"
#include "nvml_loader.h"

#if defined(_WIN32)
#define RTXMON_INTERNAL
#elif defined(__GNUC__) || defined(__clang__)
#define RTXMON_INTERNAL __attribute__((visibility("hidden")))
#else
#define RTXMON_INTERNAL
#endif

struct rtxmon_context {
    rtxmon_nvml_api_t nvml;
    rtxmon_nvapi_api_t nvapi;
    rtxmon_nvapi_status_t nvapi_initialize_status;
    int nvapi_initialized;
    int initialized;
};

RTXMON_INTERNAL void rtxmon_lock_nvapi_internal(void);
RTXMON_INTERNAL int rtxmon_try_lock_nvapi_internal(void);
RTXMON_INTERNAL void rtxmon_unlock_nvapi_internal(void);
RTXMON_INTERNAL void rtxmon_pause_lock_wait_internal(void);
RTXMON_INTERNAL uint64_t rtxmon_monotonic_ms_internal(void);

RTXMON_INTERNAL nvmlReturn_t rtxmon_get_pci_info_internal(
    rtxmon_context_t *context,
    nvmlDevice_t device,
    rtxmon_nvml_pci_info_t *out_pci);

RTXMON_INTERNAL uint64_t rtxmon_timestamp_unix_ms_internal(void);

RTXMON_INTERNAL void rtxmon_collect_thermal_capabilities(
    rtxmon_context_t *context,
    uint32_t gpu_index,
    nvmlDevice_t device,
    rtxmon_thermal_report_t *report);

#endif
