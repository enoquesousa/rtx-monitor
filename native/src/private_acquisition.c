#include "private_acquisition.h"

#if defined(_WIN32)
#include <windows.h>
static volatile LONG timeout_latched;
#else
#include <stdatomic.h>
static atomic_int timeout_latched;
#endif

/* All fence accesses are protected by the shared NVAPI lock. The latch is
 * atomic because a waiter may time out while another thread holds that lock.
 */
static uint64_t last_admitted_ms[2];
static int has_admitted[2];

int rtxmon_private_acquisition_timed_out_internal(void)
{
#if defined(_WIN32)
    return InterlockedCompareExchange(&timeout_latched, 0, 0) != 0;
#else
    return atomic_load(&timeout_latched) != 0;
#endif
}

static rtxmon_status_t latch_timeout(void)
{
#if defined(_WIN32)
    (void)InterlockedExchange(&timeout_latched, 1);
#else
    atomic_store(&timeout_latched, 1);
#endif
    return RTXMON_STATUS_TIMEOUT;
}

static rtxmon_status_t validate_clock_value(
    const rtxmon_private_acquisition_t *acquisition, uint64_t now)
{
    if (rtxmon_private_acquisition_timed_out_internal()) {
        return RTXMON_STATUS_TIMEOUT;
    }
    if (now == UINT64_MAX || acquisition->started_ms == UINT64_MAX ||
        now < acquisition->started_ms || now - acquisition->started_ms >= acquisition->timeout_ms) {
        return latch_timeout();
    }
    return RTXMON_STATUS_OK;
}

rtxmon_status_t rtxmon_private_acquisition_check_internal(
    const rtxmon_private_acquisition_t *acquisition)
{
    if (acquisition == NULL) {
        return RTXMON_STATUS_OK; /* Diagnostic compatibility queries have no acquisition. */
    }
    return validate_clock_value(acquisition, rtxmon_monotonic_ms_internal());
}

rtxmon_status_t rtxmon_private_acquisition_begin_internal(
    rtxmon_private_acquisition_t *acquisition, const rtxmon_private_operation_policy_t *policy)
{
    acquisition->started_ms = rtxmon_monotonic_ms_internal();
    acquisition->timeout_ms = policy->timeout_ms;
    acquisition->locked = 0;
    for (;;) {
        if (rtxmon_private_acquisition_check_internal(acquisition) != RTXMON_STATUS_OK) {
            return RTXMON_STATUS_TIMEOUT;
        }
        if (rtxmon_try_lock_nvapi_internal()) {
            acquisition->locked = 1;
            return rtxmon_private_acquisition_check_internal(acquisition);
        }
        rtxmon_pause_lock_wait_internal();
    }
}

rtxmon_status_t rtxmon_private_acquisition_admit_internal(
    const rtxmon_private_acquisition_t *acquisition, rtxmon_private_operation_t operation,
    const rtxmon_private_operation_policy_t *policy)
{
    uint64_t now;
    rtxmon_status_t status = rtxmon_private_acquisition_check_internal(acquisition);
    if (status != RTXMON_STATUS_OK) {
        return status;
    }
    now = rtxmon_monotonic_ms_internal();
    /* Validate this exact reading before it can update the fence. A transient
     * sentinel, regression or expired deadline must latch even if the next
     * clock read recovers, rather than publishing or poisoning rate state.
     */
    status = validate_clock_value(acquisition, now);
    if (status != RTXMON_STATUS_OK) {
        return status;
    }
    if (has_admitted[operation] && (now < last_admitted_ms[operation] ||
        now - last_admitted_ms[operation] < policy->min_interval_ms)) {
        return RTXMON_STATUS_RATE_LIMITED;
    }
    last_admitted_ms[operation] = now;
    has_admitted[operation] = 1;
    return rtxmon_private_acquisition_check_internal(acquisition);
}

void rtxmon_private_acquisition_end_internal(rtxmon_private_acquisition_t *acquisition)
{
    if (acquisition->locked) {
        acquisition->locked = 0;
        rtxmon_unlock_nvapi_internal();
    }
}
