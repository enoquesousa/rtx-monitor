#include <rtxmon/rtxmon.h>

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <deque>
#include <new>

namespace {

struct TemperatureObservation {
    std::uint64_t timestamp_unix_ms{};
    double gpu_temperature_c{};
    bool has_memory_temperature{};
    double memory_temperature_c{};
};

bool read_numeric_field(
    const rtxmon_public_telemetry_report_t &telemetry,
    std::uint32_t field,
    double &value)
{
    const auto count = std::min(
        telemetry.field_count,
        static_cast<std::uint32_t>(RTXMON_MAX_PUBLIC_FIELDS));
    for (std::uint32_t index = 0U; index < count; ++index) {
        const auto &candidate = telemetry.fields[index];
        if (candidate.field != field || candidate.state != RTXMON_CAPABILITY_AVAILABLE) {
            continue;
        }

        switch (candidate.value_type) {
        case RTXMON_VALUE_TYPE_UNSIGNED_INTEGER:
        case RTXMON_VALUE_TYPE_BITMASK:
            value = static_cast<double>(candidate.value_u64);
            return true;
        case RTXMON_VALUE_TYPE_SIGNED_INTEGER:
            value = static_cast<double>(candidate.value_i64);
            return true;
        case RTXMON_VALUE_TYPE_DOUBLE:
            value = candidate.value_f64;
            return true;
        default:
            break;
        }
    }

    return false;
}

void initialize_metric(
    rtxmon_computed_metric_t &metric,
    std::uint32_t kind,
    std::uint32_t unit,
    std::uint64_t timestamp_unix_ms,
    std::uint32_t window_ms)
{
    metric = {};
    metric.metric = kind;
    metric.state = RTXMON_METRIC_STATE_INPUT_UNAVAILABLE;
    metric.origin = RTXMON_ORIGIN_COMPUTED;
    metric.unit = unit;
    metric.timestamp_unix_ms = timestamp_unix_ms;
    metric.window_ms = window_ms;
}

} // namespace

struct rtxmon_metrics_context {
    rtxmon_metrics_options_t options{};
    std::deque<TemperatureObservation> observations;
};

rtxmon_status_t RTXMON_CALL
rtxmon_metrics_context_create(
    const rtxmon_metrics_options_t *options,
    rtxmon_metrics_context_t **out_context)
{
    if (options == nullptr || out_context == nullptr) {
        return RTXMON_STATUS_INVALID_ARGUMENT;
    }
    *out_context = nullptr;

    if (options->struct_size < sizeof(rtxmon_metrics_options_t)) {
        return RTXMON_STATUS_ABI_MISMATCH;
    }
    if (options->window_ms < 100U || options->window_ms > 3'600'000U ||
        options->temperature_threshold_c < 0 || options->temperature_threshold_c > 500 ||
        options->maximum_samples < 2U || options->maximum_samples > 65'536U) {
        return RTXMON_STATUS_INVALID_ARGUMENT;
    }

    auto *context = new (std::nothrow) rtxmon_metrics_context{};
    if (context == nullptr) {
        return RTXMON_STATUS_OUT_OF_MEMORY;
    }

    context->options = *options;
    *out_context = context;
    return RTXMON_STATUS_OK;
}

void RTXMON_CALL rtxmon_metrics_context_destroy(rtxmon_metrics_context_t *context)
{
    delete context;
}

void RTXMON_CALL rtxmon_metrics_context_reset(rtxmon_metrics_context_t *context)
{
    if (context != nullptr) {
        context->observations.clear();
    }
}

rtxmon_status_t RTXMON_CALL
rtxmon_metrics_observe(
    rtxmon_metrics_context_t *context,
    const rtxmon_public_telemetry_report_t *telemetry,
    rtxmon_computed_metrics_report_t *out_report)
{
    if (context == nullptr || telemetry == nullptr || out_report == nullptr) {
        return RTXMON_STATUS_INVALID_ARGUMENT;
    }
    if (telemetry->struct_size < sizeof(rtxmon_public_telemetry_report_t) ||
        out_report->struct_size < sizeof(rtxmon_computed_metrics_report_t)) {
        return RTXMON_STATUS_ABI_MISMATCH;
    }
    if (telemetry->field_count > RTXMON_MAX_PUBLIC_FIELDS ||
        telemetry->timestamp_unix_ms == 0U) {
        return RTXMON_STATUS_INVALID_ARGUMENT;
    }

    try {
        const std::uint64_t now = telemetry->timestamp_unix_ms;
        const std::uint64_t window_start = now > context->options.window_ms
            ? now - context->options.window_ms
            : 0U;

        if (!context->observations.empty() &&
            now < context->observations.back().timestamp_unix_ms) {
            context->observations.clear();
        }
        if (!context->observations.empty() &&
            now == context->observations.back().timestamp_unix_ms) {
            context->observations.pop_back();
        }

        double gpu_temperature = 0.0;
        double memory_temperature = 0.0;
        const bool has_gpu_temperature = read_numeric_field(
            *telemetry,
            RTXMON_PUBLIC_FIELD_GPU_DIE_TEMPERATURE_C,
            gpu_temperature);
        const bool has_memory_temperature = read_numeric_field(
            *telemetry,
            RTXMON_PUBLIC_FIELD_MEMORY_TEMPERATURE_C,
            memory_temperature);

        if (has_gpu_temperature) {
            context->observations.push_back(TemperatureObservation{
                now,
                gpu_temperature,
                has_memory_temperature,
                memory_temperature,
            });
        }

        while (context->observations.size() > 1U &&
               context->observations[1].timestamp_unix_ms <= window_start) {
            context->observations.pop_front();
        }
        while (context->observations.size() > context->options.maximum_samples) {
            context->observations.pop_front();
        }

        rtxmon_computed_metrics_report_t report{};
        report.struct_size = static_cast<std::uint32_t>(sizeof(report));
        report.gpu_index = telemetry->gpu_index;
        report.metric_count = RTXMON_MAX_COMPUTED_METRICS;
        report.timestamp_unix_ms = now;

        initialize_metric(
            report.metrics[0],
            RTXMON_METRIC_GPU_TEMPERATURE_WINDOW_AVERAGE,
            RTXMON_UNIT_CELSIUS,
            now,
            context->options.window_ms);
        initialize_metric(
            report.metrics[1],
            RTXMON_METRIC_GPU_TEMPERATURE_SLOPE,
            RTXMON_UNIT_CELSIUS_PER_SECOND,
            now,
            context->options.window_ms);
        initialize_metric(
            report.metrics[2],
            RTXMON_METRIC_GPU_TEMPERATURE_TIME_ABOVE_THRESHOLD,
            RTXMON_UNIT_SECONDS,
            now,
            context->options.window_ms);
        initialize_metric(
            report.metrics[3],
            RTXMON_METRIC_GPU_MEMORY_TEMPERATURE_DELTA,
            RTXMON_UNIT_CELSIUS,
            now,
            0U);

        for (std::size_t index = 0U; index < 3U; ++index) {
            report.metrics[index].input_count = 1U;
            report.metrics[index].input_fields[0] =
                RTXMON_PUBLIC_FIELD_GPU_DIE_TEMPERATURE_C;
        }
        report.metrics[2].temperature_threshold_c =
            context->options.temperature_threshold_c;
        report.metrics[3].input_count = 2U;
        report.metrics[3].input_fields[0] =
            RTXMON_PUBLIC_FIELD_GPU_DIE_TEMPERATURE_C;
        report.metrics[3].input_fields[1] =
            RTXMON_PUBLIC_FIELD_MEMORY_TEMPERATURE_C;

        std::size_t in_window_count = 0U;
        double sum = 0.0;
        const TemperatureObservation *first = nullptr;
        const TemperatureObservation *last = nullptr;
        for (const auto &observation : context->observations) {
            if (observation.timestamp_unix_ms < window_start ||
                observation.timestamp_unix_ms > now) {
                continue;
            }
            if (first == nullptr) {
                first = &observation;
            }
            last = &observation;
            sum += observation.gpu_temperature_c;
            ++in_window_count;
        }

        const auto bounded_count = static_cast<std::uint32_t>(std::min<std::size_t>(
            in_window_count,
            static_cast<std::size_t>(UINT32_MAX)));
        for (std::size_t index = 0U; index < 3U; ++index) {
            report.metrics[index].sample_count = bounded_count;
        }

        if (has_gpu_temperature && in_window_count > 0U) {
            report.metrics[0].state = RTXMON_METRIC_STATE_AVAILABLE;
            report.metrics[0].value = sum / static_cast<double>(in_window_count);
        }

        if (has_gpu_temperature && first != nullptr && last != nullptr &&
            in_window_count >= 2U && last->timestamp_unix_ms > first->timestamp_unix_ms) {
            const double elapsed_seconds = static_cast<double>(
                last->timestamp_unix_ms - first->timestamp_unix_ms) / 1000.0;
            report.metrics[1].state = RTXMON_METRIC_STATE_AVAILABLE;
            report.metrics[1].value =
                (last->gpu_temperature_c - first->gpu_temperature_c) / elapsed_seconds;
        } else if (has_gpu_temperature) {
            report.metrics[1].state = RTXMON_METRIC_STATE_INSUFFICIENT_DATA;
        }

        if (has_gpu_temperature && in_window_count >= 2U) {
            double seconds_above = 0.0;
            for (std::size_t index = 1U; index < context->observations.size(); ++index) {
                const auto &previous = context->observations[index - 1U];
                const auto &current = context->observations[index];
                const std::uint64_t interval_start = std::max(
                    previous.timestamp_unix_ms,
                    window_start);
                const std::uint64_t interval_end = std::min(current.timestamp_unix_ms, now);
                if (interval_end > interval_start &&
                    previous.gpu_temperature_c >
                        static_cast<double>(context->options.temperature_threshold_c)) {
                    seconds_above += static_cast<double>(interval_end - interval_start) / 1000.0;
                }
            }
            report.metrics[2].state = RTXMON_METRIC_STATE_AVAILABLE;
            report.metrics[2].value = seconds_above;
        } else if (has_gpu_temperature) {
            report.metrics[2].state = RTXMON_METRIC_STATE_INSUFFICIENT_DATA;
        }

        report.metrics[3].sample_count = has_gpu_temperature && has_memory_temperature ? 1U : 0U;
        if (has_gpu_temperature && has_memory_temperature) {
            report.metrics[3].state = RTXMON_METRIC_STATE_AVAILABLE;
            report.metrics[3].value = gpu_temperature - memory_temperature;
        }

        *out_report = report;
        return RTXMON_STATUS_OK;
    } catch (const std::bad_alloc &) {
        return RTXMON_STATUS_OUT_OF_MEMORY;
    } catch (...) {
        return RTXMON_STATUS_BACKEND_ERROR;
    }
}
