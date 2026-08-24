#include <rtxmon/rtxmon.h>

#include <stdio.h>
#include <string.h>

#if defined(_MSC_VER)
#define RTXMON_STATIC_ASSERT(condition, message) static_assert(condition, message)
#else
#define RTXMON_STATIC_ASSERT(condition, message) _Static_assert(condition, message)
#endif

RTXMON_STATIC_ASSERT(sizeof(rtxmon_temperature_sample_t) == 32U, "sample ABI changed");
RTXMON_STATIC_ASSERT(sizeof(rtxmon_gpu_info_t) == 392U, "GPU info ABI changed");

static int check(int condition, const char *message)
{
    if (condition) {
        return 0;
    }

    (void)fprintf(stderr, "FAILED: %s\n", message);
    return 1;
}

int main(void)
{
    int failures = 0;
    rtxmon_status_t status;

    failures += check(rtxmon_abi_version() == RTXMON_ABI_VERSION, "ABI version");
    failures += check(
        strcmp(rtxmon_status_string(RTXMON_STATUS_OK), "ok") == 0,
        "status string");
    failures += check(
        strstr(
            rtxmon_temperature_backend_string(RTXMON_BACKEND_NVML_TEMPERATURE_V1),
            "TemperatureV") != NULL,
        "versioned backend string");

    status = rtxmon_context_create(NULL);
    failures += check(status == RTXMON_STATUS_INVALID_ARGUMENT, "null create argument");
    failures += check(strlen(rtxmon_last_error()) > 0U, "diagnostic after invalid argument");

    if (failures == 0) {
        (void)puts("rtxmon ABI tests passed");
    }

    return failures == 0 ? 0 : 1;
}
