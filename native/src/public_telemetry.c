#include <rtxmon/rtxmon.h>

#include "rtxmon_internal.h"

#include <stddef.h>
#include <string.h>

typedef struct rtxmon_field_descriptor {
    uint32_t native_id;
    uint32_t field;
    uint32_t unit;
} rtxmon_field_descriptor_t;

typedef struct rtxmon_clock_descriptor {
    int native_id;
    uint32_t field;
} rtxmon_clock_descriptor_t;

enum {
    RTXMON_PUBLIC_FIELDS_AFTER_FANS = 7U
};

static const rtxmon_field_descriptor_t rtxmon_nvml_fields[] = {
    {RTXMON_NVML_FI_DEV_MEMORY_TEMP,
     RTXMON_PUBLIC_FIELD_MEMORY_TEMPERATURE_C,
     RTXMON_UNIT_CELSIUS},
    {RTXMON_NVML_FI_DEV_TOTAL_ENERGY_CONSUMPTION,
     RTXMON_PUBLIC_FIELD_TOTAL_ENERGY_MJ,
     RTXMON_UNIT_MILLIJOULE},
    {RTXMON_NVML_FI_DEV_POWER_AVERAGE,
     RTXMON_PUBLIC_FIELD_POWER_AVERAGE_MW,
     RTXMON_UNIT_MILLIWATT},
    {RTXMON_NVML_FI_DEV_POWER_INSTANT,
     RTXMON_PUBLIC_FIELD_POWER_INSTANT_MW,
     RTXMON_UNIT_MILLIWATT},
    {RTXMON_NVML_FI_DEV_POWER_MIN_LIMIT,
     RTXMON_PUBLIC_FIELD_POWER_LIMIT_MIN_MW,
     RTXMON_UNIT_MILLIWATT},
    {RTXMON_NVML_FI_DEV_POWER_MAX_LIMIT,
     RTXMON_PUBLIC_FIELD_POWER_LIMIT_MAX_MW,
     RTXMON_UNIT_MILLIWATT},
    {RTXMON_NVML_FI_DEV_POWER_DEFAULT_LIMIT,
     RTXMON_PUBLIC_FIELD_POWER_LIMIT_DEFAULT_MW,
     RTXMON_UNIT_MILLIWATT},
    {RTXMON_NVML_FI_DEV_POWER_CURRENT_LIMIT,
     RTXMON_PUBLIC_FIELD_POWER_LIMIT_CURRENT_MW,
     RTXMON_UNIT_MILLIWATT},
    {RTXMON_NVML_FI_DEV_POWER_REQUESTED_LIMIT,
     RTXMON_PUBLIC_FIELD_POWER_LIMIT_REQUESTED_MW,
     RTXMON_UNIT_MILLIWATT},
    {RTXMON_NVML_FI_DEV_TEMPERATURE_SHUTDOWN_TLIMIT,
     RTXMON_PUBLIC_FIELD_TEMPERATURE_SHUTDOWN_C,
     RTXMON_UNIT_CELSIUS},
    {RTXMON_NVML_FI_DEV_TEMPERATURE_SLOWDOWN_TLIMIT,
     RTXMON_PUBLIC_FIELD_TEMPERATURE_SLOWDOWN_C,
     RTXMON_UNIT_CELSIUS},
    {RTXMON_NVML_FI_DEV_TEMPERATURE_MEM_MAX_TLIMIT,
     RTXMON_PUBLIC_FIELD_TEMPERATURE_MEMORY_MAX_C,
     RTXMON_UNIT_CELSIUS},
    {RTXMON_NVML_FI_DEV_TEMPERATURE_GPU_MAX_TLIMIT,
     RTXMON_PUBLIC_FIELD_TEMPERATURE_GPU_MAX_C,
     RTXMON_UNIT_CELSIUS},
};

static const rtxmon_clock_descriptor_t rtxmon_clocks[] = {
    {RTXMON_NVML_CLOCK_GRAPHICS, RTXMON_PUBLIC_FIELD_CLOCK_GRAPHICS_MHZ},
    {RTXMON_NVML_CLOCK_SM, RTXMON_PUBLIC_FIELD_CLOCK_SM_MHZ},
    {RTXMON_NVML_CLOCK_MEMORY, RTXMON_PUBLIC_FIELD_CLOCK_MEMORY_MHZ},
    {RTXMON_NVML_CLOCK_VIDEO, RTXMON_PUBLIC_FIELD_CLOCK_VIDEO_MHZ},
};

static rtxmon_status_t rtxmon_public_map_status(nvmlReturn_t result)
{
    switch (result) {
    case NVML_SUCCESS:
        return RTXMON_STATUS_OK;
    case NVML_ERROR_INVALID_ARGUMENT:
        return RTXMON_STATUS_INVALID_ARGUMENT;
    case NVML_ERROR_NOT_SUPPORTED:
    case NVML_ERROR_DEPRECATED:
        return RTXMON_STATUS_NOT_SUPPORTED;
    case NVML_ERROR_NO_PERMISSION:
        return RTXMON_STATUS_NO_PERMISSION;
    case NVML_ERROR_DRIVER_NOT_LOADED:
        return RTXMON_STATUS_DRIVER_NOT_LOADED;
    case NVML_ERROR_NOT_FOUND:
    case NVML_ERROR_GPU_NOT_FOUND:
        return RTXMON_STATUS_GPU_NOT_FOUND;
    case NVML_ERROR_GPU_IS_LOST:
        return RTXMON_STATUS_GPU_LOST;
    case NVML_ERROR_ARGUMENT_VERSION_MISMATCH:
        return RTXMON_STATUS_ABI_MISMATCH;
    default:
        return RTXMON_STATUS_BACKEND_ERROR;
    }
}

static uint32_t rtxmon_public_state(nvmlReturn_t result)
{
    switch (result) {
    case NVML_SUCCESS:
        return RTXMON_CAPABILITY_AVAILABLE;
    case NVML_ERROR_FUNCTION_NOT_FOUND:
        return RTXMON_CAPABILITY_PROVIDER_UNAVAILABLE;
    case NVML_ERROR_NOT_SUPPORTED:
    case NVML_ERROR_DEPRECATED:
        return RTXMON_CAPABILITY_NOT_SUPPORTED;
    default:
        return RTXMON_CAPABILITY_QUERY_FAILED;
    }
}

static rtxmon_public_field_value_t *rtxmon_public_add(
    rtxmon_public_telemetry_report_t *report,
    uint32_t field,
    uint32_t provider,
    uint32_t unit,
    uint32_t provider_native_id,
    nvmlReturn_t result,
    uint64_t timestamp_unix_ms)
{
    rtxmon_public_field_value_t *value;

    if (report->field_count >= RTXMON_MAX_PUBLIC_FIELDS) {
        return NULL;
    }

    value = &report->fields[report->field_count++];
    (void)memset(value, 0, sizeof(*value));
    value->field = field;
    value->provider = provider;
    value->state = rtxmon_public_state(result);
    value->origin = RTXMON_ORIGIN_DRIVER_REPORTED;
    value->unit = unit;
    value->native_status = result;
    value->provider_native_id = provider_native_id;
    value->timestamp_unix_ms = timestamp_unix_ms;
    return value;
}

static void rtxmon_public_set_u64(rtxmon_public_field_value_t *value, uint64_t number)
{
    if (value == NULL || value->state != RTXMON_CAPABILITY_AVAILABLE) {
        return;
    }

    value->value_type = RTXMON_VALUE_TYPE_UNSIGNED_INTEGER;
    value->value_u64 = number;
}

static void rtxmon_public_set_i64(rtxmon_public_field_value_t *value, int64_t number)
{
    if (value == NULL || value->state != RTXMON_CAPABILITY_AVAILABLE) {
        return;
    }

    value->value_type = RTXMON_VALUE_TYPE_SIGNED_INTEGER;
    value->value_i64 = number;
}

static void rtxmon_public_set_bitmask(rtxmon_public_field_value_t *value, uint64_t number)
{
    if (value == NULL || value->state != RTXMON_CAPABILITY_AVAILABLE) {
        return;
    }

    value->value_type = RTXMON_VALUE_TYPE_BITMASK;
    value->value_u64 = number;
}

static void rtxmon_public_set_nvml_value(
    rtxmon_public_field_value_t *output,
    const rtxmon_nvml_field_value_t *input)
{
    if (output == NULL || input == NULL || output->state != RTXMON_CAPABILITY_AVAILABLE) {
        return;
    }

    switch (input->value_type) {
    case RTXMON_NVML_VALUE_TYPE_DOUBLE:
        output->value_type = RTXMON_VALUE_TYPE_DOUBLE;
        output->value_f64 = input->value.double_value;
        break;
    case RTXMON_NVML_VALUE_TYPE_UNSIGNED_INT:
        rtxmon_public_set_u64(output, input->value.unsigned_int_value);
        break;
    case RTXMON_NVML_VALUE_TYPE_UNSIGNED_LONG:
        rtxmon_public_set_u64(output, (uint64_t)input->value.unsigned_long_value);
        break;
    case RTXMON_NVML_VALUE_TYPE_UNSIGNED_LONG_LONG:
        rtxmon_public_set_u64(output, input->value.unsigned_long_long_value);
        break;
    case RTXMON_NVML_VALUE_TYPE_SIGNED_LONG_LONG:
        rtxmon_public_set_i64(output, input->value.signed_long_long_value);
        break;
    case RTXMON_NVML_VALUE_TYPE_SIGNED_INT:
        rtxmon_public_set_i64(output, input->value.signed_int_value);
        break;
    case RTXMON_NVML_VALUE_TYPE_UNSIGNED_SHORT:
        rtxmon_public_set_u64(output, input->value.unsigned_short_value);
        break;
    default:
        output->state = RTXMON_CAPABILITY_QUERY_FAILED;
        output->value_type = RTXMON_VALUE_TYPE_UNKNOWN;
        output->native_status = NVML_ERROR_UNKNOWN;
        break;
    }
}

static void rtxmon_collect_temperature(
    rtxmon_context_t *context,
    nvmlDevice_t device,
    rtxmon_public_telemetry_report_t *report)
{
    rtxmon_nvml_temperature_v1_t versioned;
    nvmlReturn_t result = NVML_ERROR_FUNCTION_NOT_FOUND;
    uint32_t provider = RTXMON_PUBLIC_PROVIDER_NVML_TEMPERATURE_V1;
    uint32_t legacy_temperature = 0U;
    int64_t temperature = 0;

    if (context->nvml.device_get_temperature_v != NULL) {
        (void)memset(&versioned, 0, sizeof(versioned));
        versioned.version = RTXMON_NVML_TEMPERATURE_V1_VERSION;
        versioned.sensor_type = NVML_TEMPERATURE_GPU;
        result = context->nvml.device_get_temperature_v(device, &versioned);
        temperature = versioned.temperature;
    }

    if (result != NVML_SUCCESS && context->nvml.device_get_temperature != NULL) {
        provider = RTXMON_PUBLIC_PROVIDER_NVML_TEMPERATURE_LEGACY;
        result = context->nvml.device_get_temperature(
            device,
            NVML_TEMPERATURE_GPU,
            &legacy_temperature);
        temperature = (int64_t)legacy_temperature;
    }

    rtxmon_public_set_i64(
        rtxmon_public_add(
            report,
            RTXMON_PUBLIC_FIELD_GPU_DIE_TEMPERATURE_C,
            provider,
            RTXMON_UNIT_CELSIUS,
            NVML_TEMPERATURE_GPU,
            result,
            report->timestamp_unix_ms),
        temperature);
}

static void rtxmon_collect_field_values(
    rtxmon_context_t *context,
    nvmlDevice_t device,
    rtxmon_public_telemetry_report_t *report)
{
    rtxmon_nvml_field_value_t values[
        sizeof(rtxmon_nvml_fields) / sizeof(rtxmon_nvml_fields[0])];
    nvmlReturn_t result = NVML_ERROR_FUNCTION_NOT_FOUND;
    size_t index;

    (void)memset(values, 0, sizeof(values));
    for (index = 0U; index < sizeof(rtxmon_nvml_fields) / sizeof(rtxmon_nvml_fields[0]); ++index) {
        values[index].field_id = rtxmon_nvml_fields[index].native_id;
        values[index].scope_id = 0U;
        values[index].result = NVML_ERROR_FUNCTION_NOT_FOUND;
    }

    if (context->nvml.device_get_field_values != NULL) {
        result = context->nvml.device_get_field_values(
            device,
            (int)(sizeof(values) / sizeof(values[0])),
            values);
    }

    for (index = 0U; index < sizeof(rtxmon_nvml_fields) / sizeof(rtxmon_nvml_fields[0]); ++index) {
        const nvmlReturn_t field_result = result == NVML_SUCCESS
            ? values[index].result
            : result;
        const uint64_t timestamp = result == NVML_SUCCESS && values[index].timestamp > 0
            ? (uint64_t)values[index].timestamp / 1000ULL
            : report->timestamp_unix_ms;
        rtxmon_public_field_value_t *output = rtxmon_public_add(
            report,
            rtxmon_nvml_fields[index].field,
            RTXMON_PUBLIC_PROVIDER_NVML_FIELD_VALUES,
            rtxmon_nvml_fields[index].unit,
            rtxmon_nvml_fields[index].native_id,
            field_result,
            timestamp);

        if (field_result == NVML_SUCCESS) {
            rtxmon_public_set_nvml_value(output, &values[index]);
        }
    }
}

static void rtxmon_collect_clocks(
    rtxmon_context_t *context,
    nvmlDevice_t device,
    rtxmon_public_telemetry_report_t *report)
{
    size_t index;

    for (index = 0U; index < sizeof(rtxmon_clocks) / sizeof(rtxmon_clocks[0]); ++index) {
        uint32_t clock_mhz = 0U;
        const nvmlReturn_t result = context->nvml.device_get_clock_info != NULL
            ? context->nvml.device_get_clock_info(
                  device,
                  rtxmon_clocks[index].native_id,
                  &clock_mhz)
            : NVML_ERROR_FUNCTION_NOT_FOUND;
        rtxmon_public_set_u64(
            rtxmon_public_add(
                report,
                rtxmon_clocks[index].field,
                RTXMON_PUBLIC_PROVIDER_NVML_CLOCK_INFO,
                RTXMON_UNIT_MEGAHERTZ,
                (uint32_t)rtxmon_clocks[index].native_id,
                result,
                report->timestamp_unix_ms),
            clock_mhz);
    }
}

static void rtxmon_collect_utilization(
    rtxmon_context_t *context,
    nvmlDevice_t device,
    rtxmon_public_telemetry_report_t *report)
{
    rtxmon_nvml_utilization_t utilization;
    nvmlReturn_t result = NVML_ERROR_FUNCTION_NOT_FOUND;

    (void)memset(&utilization, 0, sizeof(utilization));
    if (context->nvml.device_get_utilization_rates != NULL) {
        result = context->nvml.device_get_utilization_rates(device, &utilization);
    }

    rtxmon_public_set_u64(
        rtxmon_public_add(
            report,
            RTXMON_PUBLIC_FIELD_UTILIZATION_GPU_PERCENT,
            RTXMON_PUBLIC_PROVIDER_NVML_UTILIZATION_RATES,
            RTXMON_UNIT_PERCENT,
            0U,
            result,
            report->timestamp_unix_ms),
        utilization.gpu);
    rtxmon_public_set_u64(
        rtxmon_public_add(
            report,
            RTXMON_PUBLIC_FIELD_UTILIZATION_MEMORY_PERCENT,
            RTXMON_PUBLIC_PROVIDER_NVML_UTILIZATION_RATES,
            RTXMON_UNIT_PERCENT,
            1U,
            result,
            report->timestamp_unix_ms),
        utilization.memory);
}

static void rtxmon_collect_memory(
    rtxmon_context_t *context,
    nvmlDevice_t device,
    rtxmon_public_telemetry_report_t *report)
{
    rtxmon_nvml_memory_t memory;
    nvmlReturn_t result = NVML_ERROR_FUNCTION_NOT_FOUND;

    (void)memset(&memory, 0, sizeof(memory));
    if (context->nvml.device_get_memory_info != NULL) {
        result = context->nvml.device_get_memory_info(device, &memory);
    }

    rtxmon_public_set_u64(
        rtxmon_public_add(
            report,
            RTXMON_PUBLIC_FIELD_MEMORY_TOTAL_BYTES,
            RTXMON_PUBLIC_PROVIDER_NVML_MEMORY_INFO,
            RTXMON_UNIT_BYTES,
            0U,
            result,
            report->timestamp_unix_ms),
        memory.total);
    rtxmon_public_set_u64(
        rtxmon_public_add(
            report,
            RTXMON_PUBLIC_FIELD_MEMORY_FREE_BYTES,
            RTXMON_PUBLIC_PROVIDER_NVML_MEMORY_INFO,
            RTXMON_UNIT_BYTES,
            1U,
            result,
            report->timestamp_unix_ms),
        memory.free);
    rtxmon_public_set_u64(
        rtxmon_public_add(
            report,
            RTXMON_PUBLIC_FIELD_MEMORY_USED_BYTES,
            RTXMON_PUBLIC_PROVIDER_NVML_MEMORY_INFO,
            RTXMON_UNIT_BYTES,
            2U,
            result,
            report->timestamp_unix_ms),
        memory.used);
}

static void rtxmon_collect_fans(
    rtxmon_context_t *context,
    nvmlDevice_t device,
    rtxmon_public_telemetry_report_t *report)
{
    uint32_t fan_count = 0U;
    nvmlReturn_t count_result = NVML_ERROR_FUNCTION_NOT_FOUND;

    if (context->nvml.device_get_num_fans != NULL &&
        context->nvml.device_get_fan_speed_v2 != NULL) {
        uint32_t fan_index;
        count_result = context->nvml.device_get_num_fans(device, &fan_count);
        if (count_result == NVML_SUCCESS && fan_count > 0U) {
            for (fan_index = 0U;
                 fan_index < fan_count &&
                 report->field_count + RTXMON_PUBLIC_FIELDS_AFTER_FANS <
                     RTXMON_MAX_PUBLIC_FIELDS;
                 ++fan_index) {
                uint32_t speed = 0U;
                const nvmlReturn_t result = context->nvml.device_get_fan_speed_v2(
                    device,
                    fan_index,
                    &speed);
                rtxmon_public_set_u64(
                    rtxmon_public_add(
                        report,
                        RTXMON_PUBLIC_FIELD_FAN_SPEED_PERCENT,
                        RTXMON_PUBLIC_PROVIDER_NVML_FAN_SPEED_V2,
                        RTXMON_UNIT_PERCENT,
                        fan_index,
                        result,
                        report->timestamp_unix_ms),
                    speed);
            }
            return;
        }
    }

    if (context->nvml.device_get_fan_speed != NULL) {
        uint32_t speed = 0U;
        const nvmlReturn_t result = context->nvml.device_get_fan_speed(device, &speed);
        rtxmon_public_set_u64(
            rtxmon_public_add(
                report,
                RTXMON_PUBLIC_FIELD_FAN_SPEED_PERCENT,
                RTXMON_PUBLIC_PROVIDER_NVML_FAN_SPEED_LEGACY,
                RTXMON_UNIT_PERCENT,
                0U,
                result,
                report->timestamp_unix_ms),
            speed);
        return;
    }

    (void)rtxmon_public_add(
        report,
        RTXMON_PUBLIC_FIELD_FAN_SPEED_PERCENT,
        RTXMON_PUBLIC_PROVIDER_NVML_FAN_SPEED_V2,
        RTXMON_UNIT_PERCENT,
        0U,
        count_result,
        report->timestamp_unix_ms);
}

static void rtxmon_collect_performance_state(
    rtxmon_context_t *context,
    nvmlDevice_t device,
    rtxmon_public_telemetry_report_t *report)
{
    int performance_state = 0;
    const nvmlReturn_t result = context->nvml.device_get_performance_state != NULL
        ? context->nvml.device_get_performance_state(device, &performance_state)
        : NVML_ERROR_FUNCTION_NOT_FOUND;

    rtxmon_public_set_i64(
        rtxmon_public_add(
            report,
            RTXMON_PUBLIC_FIELD_PERFORMANCE_STATE,
            RTXMON_PUBLIC_PROVIDER_NVML_PERFORMANCE_STATE,
            RTXMON_UNIT_PSTATE,
            0U,
            result,
            report->timestamp_unix_ms),
        performance_state);
}

static void rtxmon_collect_reason(
    rtxmon_context_t *context,
    nvmlDevice_t device,
    rtxmon_public_telemetry_report_t *report,
    uint32_t field,
    uint32_t modern_provider,
    uint32_t legacy_provider,
    rtxmon_nvml_device_get_clock_reasons_fn modern,
    rtxmon_nvml_device_get_clock_reasons_fn legacy)
{
    uint64_t reasons = 0U;
    uint32_t provider = modern_provider;
    nvmlReturn_t result = NVML_ERROR_FUNCTION_NOT_FOUND;

    (void)context;
    if (modern != NULL) {
        result = modern(device, &reasons);
    }
    if (result != NVML_SUCCESS && legacy != NULL) {
        provider = legacy_provider;
        result = legacy(device, &reasons);
    }

    rtxmon_public_set_bitmask(
        rtxmon_public_add(
            report,
            field,
            provider,
            RTXMON_UNIT_BITMASK,
            0U,
            result,
            report->timestamp_unix_ms),
        reasons);
}

static void rtxmon_collect_engine_utilization(
    rtxmon_public_telemetry_report_t *report,
    nvmlDevice_t device,
    rtxmon_nvml_device_get_engine_utilization_fn function,
    uint32_t provider,
    uint32_t utilization_field,
    uint32_t period_field)
{
    uint32_t utilization = 0U;
    uint32_t sampling_period = 0U;
    const nvmlReturn_t result = function != NULL
        ? function(device, &utilization, &sampling_period)
        : NVML_ERROR_FUNCTION_NOT_FOUND;

    rtxmon_public_set_u64(
        rtxmon_public_add(
            report,
            utilization_field,
            provider,
            RTXMON_UNIT_PERCENT,
            0U,
            result,
            report->timestamp_unix_ms),
        utilization);
    rtxmon_public_set_u64(
        rtxmon_public_add(
            report,
            period_field,
            provider,
            RTXMON_UNIT_MICROSECONDS,
            1U,
            result,
            report->timestamp_unix_ms),
        sampling_period);
}

rtxmon_status_t RTXMON_CALL
rtxmon_read_public_telemetry(
    rtxmon_context_t *context,
    uint32_t gpu_index,
    rtxmon_public_telemetry_report_t *out_report)
{
    uint32_t gpu_count = 0U;
    rtxmon_public_telemetry_report_t report;
    rtxmon_status_t status;
    nvmlDevice_t device = NULL;
    nvmlReturn_t result;

    if (out_report == NULL) {
        return RTXMON_STATUS_INVALID_ARGUMENT;
    }
    if (out_report->struct_size < sizeof(rtxmon_public_telemetry_report_t)) {
        return RTXMON_STATUS_ABI_MISMATCH;
    }

    status = rtxmon_get_gpu_count(context, &gpu_count);
    if (status != RTXMON_STATUS_OK) {
        return status;
    }
    if (gpu_index >= gpu_count) {
        return RTXMON_STATUS_GPU_NOT_FOUND;
    }

    result = context->nvml.device_get_handle_by_index_v2(gpu_index, &device);
    if (result != NVML_SUCCESS) {
        return rtxmon_public_map_status(result);
    }

    (void)memset(&report, 0, sizeof(report));
    report.struct_size = (uint32_t)sizeof(report);
    report.gpu_index = gpu_index;
    report.timestamp_unix_ms = rtxmon_timestamp_unix_ms_internal();

    rtxmon_collect_temperature(context, device, &report);
    rtxmon_collect_field_values(context, device, &report);
    rtxmon_collect_clocks(context, device, &report);
    rtxmon_collect_utilization(context, device, &report);
    rtxmon_collect_memory(context, device, &report);
    rtxmon_collect_fans(context, device, &report);
    rtxmon_collect_performance_state(context, device, &report);
    rtxmon_collect_reason(
        context,
        device,
        &report,
        RTXMON_PUBLIC_FIELD_CLOCK_EVENT_REASONS_CURRENT,
        RTXMON_PUBLIC_PROVIDER_NVML_CLOCK_EVENT_REASONS,
        RTXMON_PUBLIC_PROVIDER_NVML_CLOCK_THROTTLE_REASONS_LEGACY,
        context->nvml.device_get_current_clocks_event_reasons,
        context->nvml.device_get_current_clocks_throttle_reasons);
    rtxmon_collect_reason(
        context,
        device,
        &report,
        RTXMON_PUBLIC_FIELD_CLOCK_EVENT_REASONS_SUPPORTED,
        RTXMON_PUBLIC_PROVIDER_NVML_SUPPORTED_CLOCK_EVENT_REASONS,
        RTXMON_PUBLIC_PROVIDER_NVML_SUPPORTED_CLOCK_THROTTLE_REASONS_LEGACY,
        context->nvml.device_get_supported_clocks_event_reasons,
        context->nvml.device_get_supported_clocks_throttle_reasons);
    rtxmon_collect_engine_utilization(
        &report,
        device,
        context->nvml.device_get_encoder_utilization,
        RTXMON_PUBLIC_PROVIDER_NVML_ENCODER_UTILIZATION,
        RTXMON_PUBLIC_FIELD_ENCODER_UTILIZATION_PERCENT,
        RTXMON_PUBLIC_FIELD_ENCODER_SAMPLING_PERIOD_US);
    rtxmon_collect_engine_utilization(
        &report,
        device,
        context->nvml.device_get_decoder_utilization,
        RTXMON_PUBLIC_PROVIDER_NVML_DECODER_UTILIZATION,
        RTXMON_PUBLIC_FIELD_DECODER_UTILIZATION_PERCENT,
        RTXMON_PUBLIC_FIELD_DECODER_SAMPLING_PERIOD_US);

    (void)memcpy(out_report, &report, sizeof(report));
    return RTXMON_STATUS_OK;
}
