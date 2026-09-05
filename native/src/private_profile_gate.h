#ifndef RTXMON_PRIVATE_PROFILE_GATE_H
#define RTXMON_PRIVATE_PROFILE_GATE_H

#include "rtxmon_internal.h"
#include "private_profile_catalog.h"
#include "private_acquisition.h"

/* Caller holds the NVAPI lock throughout evaluation and any later acquisition. */
RTXMON_INTERNAL rtxmon_status_t rtxmon_private_profile_evaluate_internal(
    rtxmon_context_t *context,
    uint32_t gpu_index,
    rtxmon_private_profile_report_t *report,
    rtxmon_nvapi_gpu_handle_t *out_handle,
    const rtxmon_private_acquisition_t *acquisition);

RTXMON_INTERNAL rtxmon_status_t rtxmon_private_operation_status_internal(
    uint32_t operation_state,
    int32_t *native_status);

#endif
