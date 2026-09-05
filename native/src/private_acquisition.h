#ifndef RTXMON_PRIVATE_ACQUISITION_H
#define RTXMON_PRIVATE_ACQUISITION_H

#include "rtxmon_internal.h"
#include "private_profile_catalog.h"

typedef enum rtxmon_private_operation {
    RTXMON_PRIVATE_THERMAL = 0,
    RTXMON_PRIVATE_VOLTAGE = 1
} rtxmon_private_operation_t;

typedef struct rtxmon_private_acquisition {
    uint64_t started_ms;
    uint32_t timeout_ms;
    int locked;
} rtxmon_private_acquisition_t;

/* A deadline rejects late results; it cannot cancel a synchronous driver call.
 * A timeout permanently disables both private readers in this process.
 * Only an isolated process supervisor can bound a driver call that never returns.
 */
RTXMON_INTERNAL rtxmon_status_t rtxmon_private_acquisition_begin_internal(
    rtxmon_private_acquisition_t *acquisition, const rtxmon_private_operation_policy_t *policy);
RTXMON_INTERNAL rtxmon_status_t rtxmon_private_acquisition_check_internal(
    const rtxmon_private_acquisition_t *acquisition);
/* Called with the NVAPI lock, after identity gates and immediately before reads.
 * The per-operation fence is shared by all contexts/GPUs in this process.
 */
RTXMON_INTERNAL rtxmon_status_t rtxmon_private_acquisition_admit_internal(
    const rtxmon_private_acquisition_t *acquisition, rtxmon_private_operation_t operation,
    const rtxmon_private_operation_policy_t *policy);
RTXMON_INTERNAL void rtxmon_private_acquisition_end_internal(rtxmon_private_acquisition_t *acquisition);
RTXMON_INTERNAL int rtxmon_private_acquisition_timed_out_internal(void);

#endif
