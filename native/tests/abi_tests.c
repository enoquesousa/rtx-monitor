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
RTXMON_STATIC_ASSERT(
    sizeof(rtxmon_public_field_value_t) == 64U,
    "public field ABI changed");
RTXMON_STATIC_ASSERT(
    sizeof(rtxmon_public_telemetry_report_t) == 3096U,
    "public telemetry report ABI changed");
RTXMON_STATIC_ASSERT(sizeof(rtxmon_metrics_options_t) == 16U, "metric options ABI changed");
RTXMON_STATIC_ASSERT(
    sizeof(rtxmon_computed_metric_t) == 64U,
    "computed metric ABI changed");
RTXMON_STATIC_ASSERT(
    sizeof(rtxmon_computed_metrics_report_t) == 280U,
    "computed metrics report ABI changed");

static int check(int condition, const char *message)
{
    if (condition) {
        return 0;
    }

    (void)fprintf(stderr, "FAILED: %s\n", message);
    return 1;
}

static void set_temperature_report(
    rtxmon_public_telemetry_report_t *report,
    uint64_t timestamp_unix_ms,
    int64_t gpu_temperature_c,
    int64_t memory_temperature_c)
{
    (void)memset(report, 0, sizeof(*report));
    report->struct_size = (uint32_t)sizeof(*report);
    report->gpu_index = 7U;
    report->field_count = 2U;
    report->timestamp_unix_ms = timestamp_unix_ms;

    report->fields[0].field = RTXMON_PUBLIC_FIELD_GPU_DIE_TEMPERATURE_C;
    report->fields[0].provider = RTXMON_PUBLIC_PROVIDER_NVML_TEMPERATURE_V1;
    report->fields[0].state = RTXMON_CAPABILITY_AVAILABLE;
    report->fields[0].origin = RTXMON_ORIGIN_DRIVER_REPORTED;
    report->fields[0].value_type = RTXMON_VALUE_TYPE_SIGNED_INTEGER;
    report->fields[0].unit = RTXMON_UNIT_CELSIUS;
    report->fields[0].value_i64 = gpu_temperature_c;
    report->fields[0].timestamp_unix_ms = timestamp_unix_ms;

    report->fields[1].field = RTXMON_PUBLIC_FIELD_MEMORY_TEMPERATURE_C;
    report->fields[1].provider = RTXMON_PUBLIC_PROVIDER_NVML_FIELD_VALUES;
    report->fields[1].state = RTXMON_CAPABILITY_AVAILABLE;
    report->fields[1].origin = RTXMON_ORIGIN_DRIVER_REPORTED;
    report->fields[1].value_type = RTXMON_VALUE_TYPE_SIGNED_INTEGER;
    report->fields[1].unit = RTXMON_UNIT_CELSIUS;
    report->fields[1].provider_native_id = 82U;
    report->fields[1].value_i64 = memory_temperature_c;
    report->fields[1].timestamp_unix_ms = timestamp_unix_ms;
}

static int test_computed_metrics(void)
{
    int failures = 0;
    rtxmon_metrics_context_t *context = NULL;
    rtxmon_metrics_options_t options;
    rtxmon_public_telemetry_report_t telemetry;
    rtxmon_computed_metrics_report_t metrics;
    rtxmon_status_t status;

    (void)memset(&options, 0, sizeof(options));
    options.struct_size = (uint32_t)sizeof(options);
    options.window_ms = 5000U;
    options.temperature_threshold_c = 45;
    options.maximum_samples = 16U;

    status = rtxmon_metrics_context_create(&options, &context);
    failures += check(status == RTXMON_STATUS_OK && context != NULL, "metric context create");
    if (context == NULL) {
        return failures;
    }

    set_temperature_report(&telemetry, 1000U, 40, 35);
    (void)memset(&metrics, 0, sizeof(metrics));
    metrics.struct_size = (uint32_t)sizeof(metrics);
    status = rtxmon_metrics_observe(context, &telemetry, &metrics);
    failures += check(status == RTXMON_STATUS_OK, "first metric observation");
    failures += check(metrics.metric_count == 4U, "metric count");
    failures += check(
        metrics.metrics[0].state == RTXMON_METRIC_STATE_AVAILABLE &&
            metrics.metrics[0].value == 40.0,
        "window average after first sample");
    failures += check(
        metrics.metrics[1].state == RTXMON_METRIC_STATE_INSUFFICIENT_DATA,
        "slope requires two samples");
    failures += check(
        metrics.metrics[3].state == RTXMON_METRIC_STATE_AVAILABLE &&
            metrics.metrics[3].value == 5.0,
        "thermal channel delta");

    set_temperature_report(&telemetry, 2000U, 50, 36);
    (void)memset(&metrics, 0, sizeof(metrics));
    metrics.struct_size = (uint32_t)sizeof(metrics);
    status = rtxmon_metrics_observe(context, &telemetry, &metrics);
    failures += check(status == RTXMON_STATUS_OK, "second metric observation");
    failures += check(
        metrics.metrics[0].value == 45.0 && metrics.metrics[1].value == 10.0,
        "average and slope are reproducible");
    failures += check(
        metrics.metrics[2].state == RTXMON_METRIC_STATE_AVAILABLE &&
            metrics.metrics[2].value == 0.0,
        "time above threshold preserves a legitimate zero");

    set_temperature_report(&telemetry, 3000U, 60, 37);
    (void)memset(&metrics, 0, sizeof(metrics));
    metrics.struct_size = (uint32_t)sizeof(metrics);
    status = rtxmon_metrics_observe(context, &telemetry, &metrics);
    failures += check(status == RTXMON_STATUS_OK, "third metric observation");
    failures += check(
        metrics.metrics[0].value == 50.0 &&
            metrics.metrics[1].value == 10.0 &&
            metrics.metrics[2].value == 1.0,
        "three-sample metric formulas");

    rtxmon_metrics_context_reset(context);
    rtxmon_metrics_context_destroy(context);
    return failures;
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
    failures += check(
        strcmp(rtxmon_data_origin_string(RTXMON_ORIGIN_COMPUTED), "computed") == 0,
        "data origin string");
    failures += check(
        strcmp(
            rtxmon_public_field_string(RTXMON_PUBLIC_FIELD_POWER_INSTANT_MW),
            "power_instant_mw") == 0,
        "public field string");
    failures += check(
        strstr(
            rtxmon_public_provider_string(RTXMON_PUBLIC_PROVIDER_NVML_FIELD_VALUES),
            "FieldValues") != NULL,
        "public provider string");
    failures += check(
        strcmp(rtxmon_unit_string(RTXMON_UNIT_CELSIUS_PER_SECOND), "celsius_per_second") == 0,
        "unit string");
    failures += check(
        strcmp(
            rtxmon_metric_state_string(RTXMON_METRIC_STATE_INSUFFICIENT_DATA),
            "insufficient_data") == 0,
        "metric state string");
    failures += check(
        strstr(
            rtxmon_computed_metric_formula(RTXMON_METRIC_GPU_TEMPERATURE_SLOPE),
            "elapsed_seconds") != NULL,
        "metric formula string");

    failures += test_computed_metrics();

    status = rtxmon_context_create(NULL);
    failures += check(status == RTXMON_STATUS_INVALID_ARGUMENT, "null create argument");
    failures += check(strlen(rtxmon_last_error()) > 0U, "diagnostic after invalid argument");

    if (failures == 0) {
        (void)puts("rtxmon ABI tests passed");
    }

    return failures == 0 ? 0 : 1;
}
