#include <rtxmon/rtxmon.h>
#include "rtxmon_internal.h"

#include <stddef.h>
#include <stdio.h>
#include <string.h>

_Static_assert(sizeof(rtxmon_nvml_fan_speed_info_v1_t) == 12U, "NVML RPM structure size");
_Static_assert(offsetof(rtxmon_nvml_fan_speed_info_v1_t, fan) == 4U, "NVML RPM fan offset");
_Static_assert(offsetof(rtxmon_nvml_fan_speed_info_v1_t, speed) == 8U, "NVML RPM speed offset");
_Static_assert(RTXMON_NVML_FAN_SPEED_INFO_V1_VERSION == 0x0100000cU, "NVML RPM version");
_Static_assert(sizeof(rtxmon_public_field_value_t) == 64U, "public field layout unchanged");
_Static_assert(sizeof(rtxmon_public_telemetry_report_t) == 3096U, "public report layout unchanged");

static uint32_t fan_count;
static nvmlReturn_t count_result;
static nvmlReturn_t rpm_result[32];
static nvmlReturn_t percent_result[32];
static uint32_t rpm_value[32];
static uint32_t rpm_calls;
static uint32_t percent_calls;
static uint32_t legacy_calls;
static uint32_t count_calls;
static uint32_t wrong_version_index;
static uint32_t wrong_fan_index;
static int invalid_request;

static int check(int condition, const char *message)
{
    if (condition) return 0;
    (void)fprintf(stderr, "FAILED: %s\n", message);
    return 1;
}

static nvmlReturn_t RTXMON_NVML_CALL mock_gpu_count(uint32_t *count)
{
    *count = 1U;
    return NVML_SUCCESS;
}

static nvmlReturn_t RTXMON_NVML_CALL mock_device(uint32_t index, nvmlDevice_t *device)
{
    if (index != 0U) return NVML_ERROR_NOT_FOUND;
    *device = (nvmlDevice_t)(uintptr_t)1U;
    return NVML_SUCCESS;
}

static nvmlReturn_t RTXMON_NVML_CALL mock_fan_count(nvmlDevice_t device, uint32_t *count)
{
    (void)device;
    ++count_calls;
    *count = fan_count;
    return count_result;
}

static nvmlReturn_t RTXMON_NVML_CALL mock_rpm(nvmlDevice_t device, rtxmon_nvml_fan_speed_info_v1_t *info)
{
    uint32_t fan = info->fan;
    (void)device;
    ++rpm_calls;
    if (info->version != 0x0100000cU || info->speed != 0U || fan >= 32U) {
        invalid_request = 1;
        return NVML_ERROR_INVALID_ARGUMENT;
    }
    /* Failed calls deliberately write data, which must never become a published zero/value. */
    info->speed = rpm_result[fan] == NVML_SUCCESS ? rpm_value[fan] : 987654U;
    if (fan == wrong_version_index) info->version += 1U;
    if (fan == wrong_fan_index) info->fan += 1U;
    return rpm_result[fan];
}

static nvmlReturn_t RTXMON_NVML_CALL mock_percent(nvmlDevice_t device, uint32_t fan, uint32_t *percent)
{
    (void)device;
    ++percent_calls;
    if (fan >= 32U) {
        invalid_request = 1;
        return NVML_ERROR_INVALID_ARGUMENT;
    }
    *percent = percent_result[fan] == NVML_SUCCESS ? 30U + fan : 987654U;
    return percent_result[fan];
}

static nvmlReturn_t RTXMON_NVML_CALL mock_legacy(nvmlDevice_t device, uint32_t *percent)
{
    (void)device;
    ++legacy_calls;
    *percent = 27U;
    return NVML_SUCCESS;
}

static void reset(rtxmon_context_t *context)
{
    uint32_t index;
    (void)memset(context, 0, sizeof(*context));
    context->initialized = 1;
    context->nvml.device_get_count_v2 = mock_gpu_count;
    context->nvml.device_get_handle_by_index_v2 = mock_device;
    context->nvml.device_get_num_fans = mock_fan_count;
    context->nvml.device_get_fan_speed_v2 = mock_percent;
    context->nvml.device_get_fan_speed_rpm = mock_rpm;
    context->nvml.device_get_fan_speed = mock_legacy;
    fan_count = 2U;
    count_result = NVML_SUCCESS;
    rpm_calls = percent_calls = legacy_calls = count_calls = 0U;
    wrong_version_index = wrong_fan_index = UINT32_MAX;
    invalid_request = 0;
    for (index = 0U; index < 32U; ++index) {
        rpm_result[index] = percent_result[index] = NVML_SUCCESS;
        rpm_value[index] = 1090U + index * 10U;
    }
}

static int read_report(rtxmon_context_t *context, rtxmon_public_telemetry_report_t *report)
{
    rtxmon_status_t status;
    /* Seed a previous payload to prove every new unavailable result has been cleared. */
    (void)memset(report, 0xa5, sizeof(*report));
    report->struct_size = (uint32_t)sizeof(*report);
    status = rtxmon_read_public_telemetry(context, 0U, report);
    return check(status == RTXMON_STATUS_OK,
        "public telemetry succeeds independently of individual fan failures") +
        check(report->field_count <= RTXMON_MAX_PUBLIC_FIELDS && !invalid_request,
            "fan calls and output remain bounded with a valid documented request");
}

static const rtxmon_public_field_value_t *find(
    const rtxmon_public_telemetry_report_t *report, uint32_t field, uint32_t fan)
{
    uint32_t index;
    for (index = 0U; index < report->field_count; ++index) {
        if (report->fields[index].field == field && report->fields[index].provider_native_id == fan) {
            return &report->fields[index];
        }
    }
    return NULL;
}

static int available(const rtxmon_public_telemetry_report_t *report,
    uint32_t field, uint32_t fan, uint64_t expected, uint32_t provider, uint32_t unit)
{
    const rtxmon_public_field_value_t *value = find(report, field, fan);
    return check(value != NULL && value->state == RTXMON_CAPABILITY_AVAILABLE &&
        value->native_status == NVML_SUCCESS && value->value_type == RTXMON_VALUE_TYPE_UNSIGNED_INTEGER &&
        value->value_u64 == expected && value->provider == provider && value->unit == unit &&
        value->origin == RTXMON_ORIGIN_DRIVER_REPORTED,
        "successful fan value preserves provider, native fan index, intended unit, and unsigned value");
}

static int absent(const rtxmon_public_telemetry_report_t *report,
    uint32_t field, uint32_t fan, nvmlReturn_t native_result, uint32_t state)
{
    const rtxmon_public_field_value_t *value = find(report, field, fan);
    return check(value != NULL && value->native_status == native_result && value->state == state &&
        value->value_type == RTXMON_VALUE_TYPE_UNKNOWN && value->value_u64 == 0U &&
        value->value_i64 == 0 && value->value_f64 == 0.0,
        "unavailable fan result keeps explicit native status and no stale or fabricated value");
}

static int later_fields_present(const rtxmon_public_telemetry_report_t *report)
{
    static const uint32_t later[] = {
        RTXMON_PUBLIC_FIELD_PERFORMANCE_STATE,
        RTXMON_PUBLIC_FIELD_CLOCK_EVENT_REASONS_CURRENT,
        RTXMON_PUBLIC_FIELD_CLOCK_EVENT_REASONS_SUPPORTED,
        RTXMON_PUBLIC_FIELD_ENCODER_UTILIZATION_PERCENT,
        RTXMON_PUBLIC_FIELD_ENCODER_SAMPLING_PERIOD_US,
        RTXMON_PUBLIC_FIELD_DECODER_UTILIZATION_PERCENT,
        RTXMON_PUBLIC_FIELD_DECODER_SAMPLING_PERIOD_US,
        RTXMON_PUBLIC_FIELD_POWER_CONSUMPTION_DEFAULT_LIMIT_PERCENT,
        RTXMON_PUBLIC_FIELD_POWER_CONSUMPTION_CURRENT_LIMIT_PERCENT,
        RTXMON_PUBLIC_FIELD_TEMPERATURE_GPU_LIMIT_C,
    };
    size_t index;
    int failures = 0;
    for (index = 0U; index < sizeof(later) / sizeof(later[0]); ++index) {
        uint32_t field_index;
        int found = 0;
        for (field_index = 0U; field_index < report->field_count; ++field_index) {
            if (report->fields[field_index].field == later[index]) found = 1;
        }
        failures += check(found, "fan capacity reserves every following telemetry field");
    }
    return failures;
}

static int test_two_fans_and_zero(void)
{
    rtxmon_context_t context;
    rtxmon_public_telemetry_report_t report;
    int failures;
    reset(&context);
    failures = read_report(&context, &report);
    failures += check(report.field_count == 37U && rpm_calls == 2U && percent_calls == 2U &&
        count_calls == 1U && legacy_calls == 0U, "two fans add two percent and two intended RPM values");
    failures += available(&report, RTXMON_PUBLIC_FIELD_FAN_SPEED_INTENDED_RPM, 0U, 1090U,
        RTXMON_PUBLIC_PROVIDER_NVML_FAN_SPEED_RPM, RTXMON_UNIT_RPM);
    failures += available(&report, RTXMON_PUBLIC_FIELD_FAN_SPEED_INTENDED_RPM, 1U, 1100U,
        RTXMON_PUBLIC_PROVIDER_NVML_FAN_SPEED_RPM, RTXMON_UNIT_RPM);
    failures += available(&report, RTXMON_PUBLIC_FIELD_FAN_SPEED_PERCENT, 1U, 31U,
        RTXMON_PUBLIC_PROVIDER_NVML_FAN_SPEED_V2, RTXMON_UNIT_PERCENT);
    failures += later_fields_present(&report);
    rpm_value[0] = 0U;
    failures += read_report(&context, &report);
    failures += available(&report, RTXMON_PUBLIC_FIELD_FAN_SPEED_INTENDED_RPM, 0U, 0U,
        RTXMON_PUBLIC_PROVIDER_NVML_FAN_SPEED_RPM, RTXMON_UNIT_RPM);
    return failures;
}

static int test_unavailable_and_failed_payloads(void)
{
    static const nvmlReturn_t errors[] = {NVML_ERROR_NOT_SUPPORTED, NVML_ERROR_GPU_IS_LOST,
        NVML_ERROR_ARGUMENT_VERSION_MISMATCH};
    rtxmon_context_t context;
    rtxmon_public_telemetry_report_t report;
    int failures = 0;
    size_t index;
    for (index = 0U; index < sizeof(errors) / sizeof(errors[0]); ++index) {
        reset(&context);
        rpm_result[0] = errors[index];
        failures += read_report(&context, &report);
        failures += absent(&report, RTXMON_PUBLIC_FIELD_FAN_SPEED_INTENDED_RPM, 0U, errors[index],
            errors[index] == NVML_ERROR_NOT_SUPPORTED ? RTXMON_CAPABILITY_NOT_SUPPORTED : RTXMON_CAPABILITY_QUERY_FAILED);
        failures += available(&report, RTXMON_PUBLIC_FIELD_FAN_SPEED_INTENDED_RPM, 1U, 1100U,
            RTXMON_PUBLIC_PROVIDER_NVML_FAN_SPEED_RPM, RTXMON_UNIT_RPM);
        failures += available(&report, RTXMON_PUBLIC_FIELD_FAN_SPEED_PERCENT, 0U, 30U,
            RTXMON_PUBLIC_PROVIDER_NVML_FAN_SPEED_V2, RTXMON_UNIT_PERCENT);
    }
    reset(&context);
    context.nvml.device_get_fan_speed_rpm = NULL;
    failures += read_report(&context, &report);
    failures += absent(&report, RTXMON_PUBLIC_FIELD_FAN_SPEED_INTENDED_RPM, 1U,
        NVML_ERROR_FUNCTION_NOT_FOUND, RTXMON_CAPABILITY_PROVIDER_UNAVAILABLE);
    failures += check(rpm_calls == 0U && percent_calls == 2U, "a missing RPM export preserves indexed percent collection");
    reset(&context);
    wrong_version_index = 0U;
    wrong_fan_index = 1U;
    failures += read_report(&context, &report);
    failures += absent(&report, RTXMON_PUBLIC_FIELD_FAN_SPEED_INTENDED_RPM, 0U,
        NVML_ERROR_ARGUMENT_VERSION_MISMATCH, RTXMON_CAPABILITY_QUERY_FAILED);
    failures += absent(&report, RTXMON_PUBLIC_FIELD_FAN_SPEED_INTENDED_RPM, 1U,
        NVML_ERROR_UNKNOWN, RTXMON_CAPABILITY_QUERY_FAILED);
    reset(&context);
    percent_result[0] = NVML_ERROR_NOT_SUPPORTED;
    failures += read_report(&context, &report);
    failures += absent(&report, RTXMON_PUBLIC_FIELD_FAN_SPEED_PERCENT, 0U,
        NVML_ERROR_NOT_SUPPORTED, RTXMON_CAPABILITY_NOT_SUPPORTED);
    failures += available(&report, RTXMON_PUBLIC_FIELD_FAN_SPEED_INTENDED_RPM, 0U, 1090U,
        RTXMON_PUBLIC_PROVIDER_NVML_FAN_SPEED_RPM, RTXMON_UNIT_RPM);
    return failures;
}

static int test_count_and_legacy_fallbacks(void)
{
    rtxmon_context_t context;
    rtxmon_public_telemetry_report_t report;
    int failures = 0;
    reset(&context);
    fan_count = 0U;
    failures += read_report(&context, &report);
    failures += check(rpm_calls == 0U && percent_calls == 0U && legacy_calls == 1U,
        "zero fans do not cause an indexed query and retain the existing legacy percent fallback");
    failures += absent(&report, RTXMON_PUBLIC_FIELD_FAN_SPEED_INTENDED_RPM, 0U,
        NVML_ERROR_NOT_SUPPORTED, RTXMON_CAPABILITY_NOT_SUPPORTED);
    context.nvml.device_get_fan_speed = NULL;
    failures += read_report(&context, &report);
    failures += absent(&report, RTXMON_PUBLIC_FIELD_FAN_SPEED_PERCENT, 0U,
        NVML_ERROR_NOT_SUPPORTED, RTXMON_CAPABILITY_NOT_SUPPORTED);
    reset(&context);
    count_result = NVML_ERROR_GPU_IS_LOST;
    failures += read_report(&context, &report);
    failures += absent(&report, RTXMON_PUBLIC_FIELD_FAN_SPEED_INTENDED_RPM, 0U,
        NVML_ERROR_GPU_IS_LOST, RTXMON_CAPABILITY_QUERY_FAILED);
    failures += available(&report, RTXMON_PUBLIC_FIELD_FAN_SPEED_PERCENT, 0U, 27U,
        RTXMON_PUBLIC_PROVIDER_NVML_FAN_SPEED_LEGACY, RTXMON_UNIT_PERCENT);
    failures += check(rpm_calls == 0U && percent_calls == 0U, "failed enumeration cannot fabricate fan indices");
    reset(&context);
    context.nvml.device_get_num_fans = NULL;
    failures += read_report(&context, &report);
    failures += absent(&report, RTXMON_PUBLIC_FIELD_FAN_SPEED_INTENDED_RPM, 0U,
        NVML_ERROR_FUNCTION_NOT_FOUND, RTXMON_CAPABILITY_PROVIDER_UNAVAILABLE);
    failures += check(count_calls == 0U && rpm_calls == 0U && legacy_calls == 1U,
        "missing enumeration export preserves legacy percent without guessing RPM fan zero");
    reset(&context);
    context.nvml.device_get_fan_speed_v2 = NULL;
    failures += read_report(&context, &report);
    failures += check(report.field_count == 36U && rpm_calls == 2U && percent_calls == 0U && legacy_calls == 1U,
        "RPM collection does not require the indexed percent export");
    failures += available(&report, RTXMON_PUBLIC_FIELD_FAN_SPEED_PERCENT, 0U, 27U,
        RTXMON_PUBLIC_PROVIDER_NVML_FAN_SPEED_LEGACY, RTXMON_UNIT_PERCENT);
    context.nvml.device_get_fan_speed_rpm = NULL;
    failures += read_report(&context, &report);
    failures += available(&report, RTXMON_PUBLIC_FIELD_FAN_SPEED_PERCENT, 0U, 27U,
        RTXMON_PUBLIC_PROVIDER_NVML_FAN_SPEED_LEGACY, RTXMON_UNIT_PERCENT);
    context.nvml.device_get_fan_speed = NULL;
    failures += read_report(&context, &report);
    failures += absent(&report, RTXMON_PUBLIC_FIELD_FAN_SPEED_PERCENT, 0U,
        NVML_ERROR_FUNCTION_NOT_FOUND, RTXMON_CAPABILITY_PROVIDER_UNAVAILABLE);
    return failures;
}

static int test_capacity(void)
{
    static const uint32_t excessive_counts[] = {8U, 33U, UINT32_MAX};
    rtxmon_context_t context;
    rtxmon_public_telemetry_report_t report;
    int failures = 0;
    size_t index;
    reset(&context);
    fan_count = 7U;
    failures += read_report(&context, &report);
    failures += check(report.field_count == 47U && rpm_calls == 7U && percent_calls == 7U,
        "the largest representable paired fan set is fully collected");
    failures += later_fields_present(&report);
    reset(&context);
    fan_count = 14U;
    context.nvml.device_get_fan_speed_v2 = NULL;
    failures += read_report(&context, &report);
    failures += check(report.field_count == 48U && rpm_calls == 14U && legacy_calls == 1U,
        "RPM plus one legacy percent can exactly fill fan capacity without truncation");
    failures += later_fields_present(&report);
    for (index = 0U; index < sizeof(excessive_counts) / sizeof(excessive_counts[0]); ++index) {
        reset(&context);
        fan_count = excessive_counts[index];
        failures += read_report(&context, &report);
        failures += check(report.field_count == 35U && rpm_calls == 0U && percent_calls == 0U && legacy_calls == 1U,
            "unrepresentable fan counts cannot cause unbounded calls or silently truncated fan results");
        failures += absent(&report, RTXMON_PUBLIC_FIELD_FAN_SPEED_INTENDED_RPM, 0U,
            NVML_ERROR_INSUFFICIENT_SIZE, RTXMON_CAPABILITY_QUERY_FAILED);
        failures += later_fields_present(&report);
    }
    return failures;
}

int main(void)
{
    int failures = 0;
    failures += check(RTXMON_ABI_VERSION == 7U && RTXMON_PUBLIC_FIELD_FAN_SPEED_INTENDED_RPM == 35 &&
        RTXMON_PUBLIC_PROVIDER_NVML_FAN_SPEED_RPM == 18 && RTXMON_UNIT_RPM == 12,
        "fan identifiers are appended without changing ABI version");
    failures += check(strcmp(rtxmon_public_field_string(RTXMON_PUBLIC_FIELD_FAN_SPEED_INTENDED_RPM),
        "fan_speed_intended_rpm") == 0, "intended RPM field has an explicit stable name");
    failures += check(strcmp(rtxmon_public_provider_string(RTXMON_PUBLIC_PROVIDER_NVML_FAN_SPEED_RPM),
        "NVML nvmlDeviceGetFanSpeedRPM") == 0 && strcmp(rtxmon_unit_string(RTXMON_UNIT_RPM), "rpm") == 0,
        "documented RPM provider and unit names are stable");
    failures += test_two_fans_and_zero();
    failures += test_unavailable_and_failed_payloads();
    failures += test_count_and_legacy_fallbacks();
    failures += test_capacity();
    if (failures != 0) return 1;
    (void)puts("Public fan intended-RPM fake-backend tests passed.");
    return 0;
}
