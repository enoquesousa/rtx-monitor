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
RTXMON_STATIC_ASSERT(sizeof(rtxmon_board_identity_t) == 240U, "board identity ABI changed");
RTXMON_STATIC_ASSERT(
    sizeof(rtxmon_thermal_provider_result_t) == 16U,
    "provider result ABI changed");
RTXMON_STATIC_ASSERT(
    sizeof(rtxmon_thermal_capability_t) == 48U,
    "thermal capability ABI changed");
RTXMON_STATIC_ASSERT(sizeof(rtxmon_thermal_report_t) == 456U, "thermal report ABI changed");

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
    failures += check(
        strstr(
            rtxmon_thermal_provider_string(RTXMON_PROVIDER_NVAPI_THERMAL_SETTINGS),
            "NVAPI") != NULL,
        "NVAPI provider string");
    failures += check(
        strcmp(
            rtxmon_capability_state_string(RTXMON_CAPABILITY_NOT_SUPPORTED),
            "not_supported") == 0,
        "capability state string");
    failures += check(
        strcmp(rtxmon_thermal_target_string(RTXMON_THERMAL_TARGET_MEMORY), "memory") == 0,
        "thermal target string");
    failures += check(
        strcmp(rtxmon_thermal_target_string(RTXMON_THERMAL_TARGET_VCD_INLET), "vcd_inlet") == 0,
        "VCD thermal target string");
    failures += check(
        strcmp(
            rtxmon_thermal_controller_string(RTXMON_THERMAL_CONTROLLER_GPU_INTERNAL),
            "gpu_internal") == 0,
        "thermal controller string");
    failures += check(
        strcmp(
            rtxmon_sensor_confidence_string(RTXMON_CONFIDENCE_DRIVER_REPORTED),
            "driver_reported") == 0,
        "confidence string");

    status = rtxmon_context_create(NULL);
    failures += check(status == RTXMON_STATUS_INVALID_ARGUMENT, "null create argument");
    failures += check(strlen(rtxmon_last_error()) > 0U, "diagnostic after invalid argument");

    if (failures == 0) {
        (void)puts("rtxmon ABI tests passed");
    }

    return failures == 0 ? 0 : 1;
}
