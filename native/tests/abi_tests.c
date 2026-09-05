#if !defined(_WIN32) && !defined(_POSIX_C_SOURCE)
#define _POSIX_C_SOURCE 200809L
#endif
#include <rtxmon/rtxmon.h>
#include "../src/rtxmon_internal.h"
#include "../src/private_profile.h"

_Static_assert(sizeof(rtxmon_private_thermal_sample_t) == 40U, "private thermal ABI changed");
_Static_assert(sizeof(rtxmon_private_voltage_sample_t) == 32U, "private voltage ABI changed");

#include <stdio.h>
#include <stddef.h>
#include <string.h>
#if defined(_WIN32)
#include <windows.h>
#else
#include <time.h>
#endif

#if defined(_MSC_VER)
#define RTXMON_STATIC_ASSERT(condition, message) static_assert(condition, message)
#else
#define RTXMON_STATIC_ASSERT(condition, message) _Static_assert(condition, message)
#endif

RTXMON_STATIC_ASSERT(sizeof(rtxmon_temperature_sample_t) == 32U, "sample ABI changed");
RTXMON_STATIC_ASSERT(sizeof(rtxmon_private_profile_report_t) == 304U, "private profile ABI changed");
RTXMON_STATIC_ASSERT(offsetof(rtxmon_private_profile_report_t, profile_id) == 32U, "profile id offset changed");
RTXMON_STATIC_ASSERT(offsetof(rtxmon_private_profile_report_t, revocation_reason) == 160U, "revocation reason offset changed");
RTXMON_STATIC_ASSERT(offsetof(rtxmon_private_profile_report_t, thermal_min_interval_ms) == 288U, "thermal interval offset changed");
RTXMON_STATIC_ASSERT(offsetof(rtxmon_private_profile_report_t, thermal_timeout_ms) == 292U, "thermal timeout offset changed");
RTXMON_STATIC_ASSERT(offsetof(rtxmon_private_profile_report_t, voltage_min_interval_ms) == 296U, "voltage interval offset changed");
RTXMON_STATIC_ASSERT(offsetof(rtxmon_private_profile_report_t, voltage_timeout_ms) == 300U, "voltage timeout offset changed");
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

static const char *mock_uuid = "GPU-fca3647e-8390-15a8-f23b-d0f870c9accd";
/* These ABI integration cases use the real library clock. Boundary/timeout
 * behavior is tested separately using link-time fake clocks, without sleeping.
 */
static void wait_private_interval(void)
{
#if defined(_WIN32)
    Sleep(100U);
#else
    struct timespec delay = {0, 100000000L};
    while (nanosleep(&delay, &delay) != 0) { }
#endif
}
static uint32_t mock_thermal_call_count;
static uint32_t mock_voltage_call_count;
static int mock_fail_second_thermal_call;
static int mock_return_wrong_second_mask;
static int32_t mock_thermal_channel0_raw = 40 * 256;
static int32_t mock_thermal_channel1_raw = 50 * 256;

static nvmlReturn_t RTXMON_NVML_CALL mock_device_get_handle_by_index(
    uint32_t index,
    nvmlDevice_t *device)
{
    if (index != 0U || device == NULL) {
        return NVML_ERROR_NOT_FOUND;
    }
    *device = (nvmlDevice_t)(uintptr_t)1U;
    return NVML_SUCCESS;
}

static nvmlReturn_t RTXMON_NVML_CALL mock_device_get_uuid(
    nvmlDevice_t device,
    char *uuid,
    uint32_t length)
{
    (void)device;
    if (uuid == NULL || length == 0U) {
        return NVML_ERROR_INVALID_ARGUMENT;
    }
    (void)snprintf(uuid, length, "%s", mock_uuid);
    uuid[length - 1U] = '\0';
    return NVML_SUCCESS;
}

static nvmlReturn_t RTXMON_NVML_CALL mock_device_get_pci_info(
    nvmlDevice_t device,
    rtxmon_nvml_pci_info_t *pci)
{
    (void)device;
    if (pci == NULL) {
        return NVML_ERROR_INVALID_ARGUMENT;
    }
    (void)memset(pci, 0, sizeof(*pci));
    pci->bus = 1U;
    pci->device = 0U;
    pci->pci_device_id = (0x2504U << 16U) | 0x10deU;
    pci->pci_subsystem_id = (0x1536U << 16U) | 0x10deU;
    return NVML_SUCCESS;
}

static nvmlReturn_t RTXMON_NVML_CALL mock_device_get_vbios_version(
    nvmlDevice_t device,
    char *version,
    uint32_t length)
{
    (void)device;
    if (version == NULL || length == 0U) {
        return NVML_ERROR_INVALID_ARGUMENT;
    }
    (void)snprintf(version, length, "%s", "94.06.25.00.fc");
    version[length - 1U] = '\0';
    return NVML_SUCCESS;
}

static nvmlReturn_t RTXMON_NVML_CALL mock_system_get_driver_version(
    char *version,
    uint32_t length)
{
    if (version == NULL || length == 0U) {
        return NVML_ERROR_INVALID_ARGUMENT;
    }
    (void)snprintf(version, length, "%s", "610.88");
    version[length - 1U] = '\0';
    return NVML_SUCCESS;
}

static rtxmon_nvapi_status_t RTXMON_NVAPI_CALL mock_enum_physical_gpus(
    rtxmon_nvapi_gpu_handle_t handles[RTXMON_NVAPI_MAX_PHYSICAL_GPUS],
    uint32_t *count)
{
    handles[0] = (rtxmon_nvapi_gpu_handle_t)(uintptr_t)2U;
    *count = 1U;
    return RTXMON_NVAPI_OK;
}

static rtxmon_nvapi_status_t RTXMON_NVAPI_CALL mock_gpu_get_bus_id(
    rtxmon_nvapi_gpu_handle_t handle,
    uint32_t *bus_id)
{
    (void)handle;
    *bus_id = 1U;
    return RTXMON_NVAPI_OK;
}

static rtxmon_nvapi_status_t RTXMON_NVAPI_CALL mock_gpu_get_bus_slot_id(
    rtxmon_nvapi_gpu_handle_t handle,
    uint32_t *slot_id)
{
    (void)handle;
    *slot_id = 0U;
    return RTXMON_NVAPI_OK;
}

static rtxmon_nvapi_status_t RTXMON_NVAPI_CALL mock_gpu_get_pci_identifiers(
    rtxmon_nvapi_gpu_handle_t handle,
    uint32_t *device_id,
    uint32_t *subsystem_id,
    uint32_t *revision_id,
    uint32_t *extended_device_id)
{
    (void)handle;
    *device_id = (0x2504U << 16U) | 0x10deU;
    *subsystem_id = (0x1536U << 16U) | 0x10deU;
    *revision_id = 0U;
    *extended_device_id = 0U;
    return RTXMON_NVAPI_OK;
}

static rtxmon_nvapi_status_t RTXMON_NVAPI_CALL mock_gpu_therm_channel_get_status(
    rtxmon_nvapi_gpu_handle_t handle,
    rtxmon_nvapi_therm_channel_status_v2_t *status)
{
    const uint32_t requested_mask = status->channel_mask;
    (void)handle;
    ++mock_thermal_call_count;
    if (mock_fail_second_thermal_call != 0 && mock_thermal_call_count == 2U) {
        return RTXMON_NVAPI_ERROR;
    }
    status->version = RTXMON_NVAPI_THERM_CHANNEL_STATUS_V2_VERSION;
    if (requested_mask == 1U) {
        status->words[8] = (uint32_t)mock_thermal_channel0_raw;
    } else if (requested_mask == 2U) {
        status->words[9] = (uint32_t)mock_thermal_channel1_raw;
    }
    if (mock_return_wrong_second_mask != 0 && mock_thermal_call_count == 2U) {
        status->channel_mask = 1U;
    }
    return RTXMON_NVAPI_OK;
}

static rtxmon_nvapi_status_t RTXMON_NVAPI_CALL mock_gpu_voltage_status(
    rtxmon_nvapi_gpu_handle_t handle,
    rtxmon_nvapi_voltage_status_v1_t *status)
{
    (void)handle;
    ++mock_voltage_call_count;
    status->version = RTXMON_NVAPI_VOLTAGE_STATUS_V1_VERSION;
    status->words[9] = 956250U;
    return RTXMON_NVAPI_OK;
}

static void initialize_private_context(rtxmon_context_t *context)
{
    (void)memset(context, 0, sizeof(*context));
    context->nvml.device_get_handle_by_index_v2 = mock_device_get_handle_by_index;
    context->nvml.device_get_uuid = mock_device_get_uuid;
    context->nvml.device_get_pci_info_v3 = mock_device_get_pci_info;
    context->nvml.device_get_vbios_version = mock_device_get_vbios_version;
    context->nvml.system_get_driver_version = mock_system_get_driver_version;
    context->nvapi.enum_physical_gpus = mock_enum_physical_gpus;
    context->nvapi.gpu_get_bus_id = mock_gpu_get_bus_id;
    context->nvapi.gpu_get_bus_slot_id = mock_gpu_get_bus_slot_id;
    context->nvapi.gpu_get_pci_identifiers = mock_gpu_get_pci_identifiers;
    context->nvapi.gpu_therm_channel_get_status = mock_gpu_therm_channel_get_status;
    context->nvapi.gpu_voltage_status = mock_gpu_voltage_status;
    context->nvapi_initialized = 1;
    context->initialized = 1;
}

static int test_private_thermal_fail_closed(void)
{
    int failures = 0;
    rtxmon_context_t context;
    rtxmon_private_thermal_sample_t sample;
    rtxmon_status_t status;

    initialize_private_context(&context);
    mock_uuid = "GPU-fca3647e-8390-15a8-f23b-d0f870c9accd";
    mock_thermal_call_count = 0U;
    mock_fail_second_thermal_call = 0;
    mock_return_wrong_second_mask = 0;
    mock_thermal_channel0_raw = 40 * 256;
    mock_thermal_channel1_raw = 50 * 256;
    (void)memset(&sample, 0, sizeof(sample));
    sample.struct_size = (uint32_t)sizeof(sample);
    wait_private_interval();
    status = rtxmon_read_private_thermal_channels(&context, 0U, &sample);
    failures += check(
        status == RTXMON_STATUS_OK &&
            sample.value_flags == (RTXMON_PRIVATE_THERMAL_DIE_VALID |
                RTXMON_PRIVATE_THERMAL_HOTSPOT_VALID) &&
            sample.gpu_die_temperature_millic == 40000 &&
            sample.gpu_hotspot_temperature_millic == 50000,
        "private thermal publishes only a complete valid pair");

    mock_uuid = "GPU-00000000-0000-0000-0000-000000000000";
    mock_thermal_call_count = 0U;
    (void)memset(&sample, 0xff, sizeof(sample));
    sample.struct_size = (uint32_t)sizeof(sample);
    wait_private_interval();
    status = rtxmon_read_private_thermal_channels(&context, 0U, &sample);
    failures += check(
        status == RTXMON_STATUS_NOT_SUPPORTED && mock_thermal_call_count == 0U &&
            sample.value_flags == 0U && sample.gpu_die_temperature_millic == 0 &&
            sample.gpu_hotspot_temperature_millic == 0,
        "private thermal UUID mismatch fails before NVAPI and clears values");

    mock_uuid = "GPU-fca3647e-8390-15a8-f23b-d0f870c9accd";
    mock_thermal_call_count = 0U;
    mock_fail_second_thermal_call = 1;
    (void)memset(&sample, 0xff, sizeof(sample));
    sample.struct_size = (uint32_t)sizeof(sample);
    wait_private_interval();
    status = rtxmon_read_private_thermal_channels(&context, 0U, &sample);
    failures += check(
        status == RTXMON_STATUS_BACKEND_ERROR && mock_thermal_call_count == 2U &&
            sample.value_flags == 0U && sample.gpu_die_temperature_millic == 0 &&
            sample.gpu_hotspot_temperature_millic == 0,
        "private thermal second-channel failure never exposes a partial sample");

    mock_thermal_call_count = 0U;
    mock_fail_second_thermal_call = 0;
    mock_return_wrong_second_mask = 1;
    (void)memset(&sample, 0xff, sizeof(sample));
    sample.struct_size = (uint32_t)sizeof(sample);
    wait_private_interval();
    status = rtxmon_read_private_thermal_channels(&context, 0U, &sample);
    failures += check(
        status == RTXMON_STATUS_BACKEND_ERROR && mock_thermal_call_count == 2U &&
            sample.value_flags == 0U && sample.gpu_die_temperature_millic == 0 &&
            sample.gpu_hotspot_temperature_millic == 0,
        "private thermal returned channel-mask drift fails closed atomically");

    mock_thermal_call_count = 0U;
    mock_return_wrong_second_mask = 0;
    mock_thermal_channel0_raw = -10224;
    mock_thermal_channel1_raw = 10256;
    (void)memset(&sample, 0xff, sizeof(sample));
    sample.struct_size = (uint32_t)sizeof(sample);
    wait_private_interval();
    status = rtxmon_read_private_thermal_channels(&context, 0U, &sample);
    failures += check(
        status == RTXMON_STATUS_BACKEND_ERROR && mock_thermal_call_count == 2U &&
            sample.value_flags == 0U && sample.gpu_die_temperature_millic == 0 &&
            sample.gpu_hotspot_temperature_millic == 0,
        "private thermal rejects a fixed8 pair whose rounded millidegree delta exceeds 80 C");

    return failures;
}

static int test_private_voltage_identity_gate(void)
{
    int failures = 0;
    rtxmon_context_t context;
    rtxmon_private_voltage_sample_t sample;
    rtxmon_status_t status;

    initialize_private_context(&context);
    mock_uuid = "GPU-fca3647e-8390-15a8-f23b-d0f870c9accd";
    mock_voltage_call_count = 0U;
    (void)memset(&sample, 0, sizeof(sample));
    sample.struct_size = (uint32_t)sizeof(sample);
    wait_private_interval();
    status = rtxmon_read_private_voltage_status(&context, 0U, &sample);
    failures += check(
        status == RTXMON_STATUS_OK && mock_voltage_call_count == 1U &&
            sample.value_flags == RTXMON_PRIVATE_VOLTAGE_CORE_VALID &&
            sample.gpu_core_voltage_microvolts == 956250U,
        "private voltage accepts only the exact physical profile");

    mock_uuid = "GPU-00000000-0000-0000-0000-000000000000";
    mock_voltage_call_count = 0U;
    (void)memset(&sample, 0xff, sizeof(sample));
    sample.struct_size = (uint32_t)sizeof(sample);
    wait_private_interval();
    status = rtxmon_read_private_voltage_status(&context, 0U, &sample);
    failures += check(
        status == RTXMON_STATUS_NOT_SUPPORTED && mock_voltage_call_count == 0U &&
            sample.value_flags == 0U && sample.gpu_core_voltage_microvolts == 0U,
        "private voltage UUID mismatch fails before NVAPI and clears values");

    return failures;
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
    failures += check(RTXMON_ABI_VERSION == 7U && RTXMON_STATUS_RATE_LIMITED == 12 &&
        RTXMON_STATUS_TIMEOUT == 13 && RTXMON_PRIVATE_OPERATION_TIMEOUT == 10,
        "acquisition ABI status numbers are stable");
    failures += check(strcmp(rtxmon_status_string(RTXMON_STATUS_RATE_LIMITED), "private operation rate limited") == 0 &&
        strcmp(rtxmon_status_string(RTXMON_STATUS_TIMEOUT), "private acquisition timed out; restart process required") == 0,
        "acquisition statuses have actionable text");
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
        strcmp(
            rtxmon_public_field_string(
                RTXMON_PUBLIC_FIELD_POWER_CONSUMPTION_DEFAULT_LIMIT_PERCENT),
            "power_consumption_default_limit_percent") == 0,
        "computed power field string");
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
    failures += test_private_thermal_fail_closed();
    failures += test_private_voltage_identity_gate();

    status = rtxmon_context_create(NULL);
    failures += check(status == RTXMON_STATUS_INVALID_ARGUMENT, "null create argument");
    failures += check(strlen(rtxmon_last_error()) > 0U, "diagnostic after invalid argument");

    if (failures == 0) {
        (void)puts("rtxmon ABI tests passed");
    }

    return failures == 0 ? 0 : 1;
}
