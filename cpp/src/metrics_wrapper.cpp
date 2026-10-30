#include <rtxmon/metrics.hpp>

#include <sstream>
#include <utility>

namespace rtxmon {
namespace {

[[noreturn]] void throw_metrics_status(rtxmon_status_t status, const char *operation)
{
    std::ostringstream message;
    message << operation << ": " << rtxmon_status_string(status);
    const char *diagnostic = rtxmon_last_error();
    if (diagnostic != nullptr && diagnostic[0] != '\0') {
        message << " (" << diagnostic << ')';
    }
    throw MonitorError(status, message.str());
}

rtxmon_public_telemetry_report_t to_native(const PublicTelemetryReport &telemetry)
{
    if (telemetry.fields.size() > RTXMON_MAX_PUBLIC_FIELDS) {
        throw MonitorError(
            RTXMON_STATUS_ABI_MISMATCH,
            "Public telemetry field count exceeds the ABI limit");
    }

    rtxmon_public_telemetry_report_t native{};
    native.struct_size = sizeof(native);
    native.gpu_index = telemetry.gpu_index;
    native.field_count = static_cast<std::uint32_t>(telemetry.fields.size());
    native.timestamp_unix_ms = telemetry.timestamp_unix_ms;

    for (std::size_t index = 0U; index < telemetry.fields.size(); ++index) {
        const auto &source = telemetry.fields[index];
        auto &destination = native.fields[index];
        destination.field = static_cast<std::uint32_t>(source.field);
        destination.provider = static_cast<std::uint32_t>(source.provider);
        destination.state = static_cast<std::uint32_t>(source.state);
        destination.origin = static_cast<std::uint32_t>(source.origin);
        destination.value_type = static_cast<std::uint32_t>(source.value_type);
        destination.unit = static_cast<std::uint32_t>(source.unit);
        destination.native_status = source.native_status;
        destination.provider_native_id = source.provider_native_id;
        destination.value_u64 = source.unsigned_value.value_or(0U);
        destination.value_i64 = source.signed_value.value_or(0);
        destination.value_f64 = source.double_value.value_or(0.0);
        destination.timestamp_unix_ms = source.timestamp_unix_ms;
    }

    return native;
}

} // namespace

MetricsEngine::MetricsEngine(MetricsOptions options)
{
    rtxmon_metrics_options_t native_options{};
    native_options.struct_size = sizeof(native_options);
    native_options.window_ms = options.window_ms;
    native_options.temperature_threshold_c = options.temperature_threshold_c;
    native_options.maximum_samples = options.maximum_samples;

    const auto status = rtxmon_metrics_context_create(&native_options, &context_);
    if (status != RTXMON_STATUS_OK) {
        throw_metrics_status(status, "Could not create the computed metrics window");
    }
}

MetricsEngine::~MetricsEngine()
{
    rtxmon_metrics_context_destroy(context_);
}

MetricsEngine::MetricsEngine(MetricsEngine &&other) noexcept
    : context_(std::exchange(other.context_, nullptr))
{
}

MetricsEngine &MetricsEngine::operator=(MetricsEngine &&other) noexcept
{
    if (this != &other) {
        rtxmon_metrics_context_destroy(context_);
        context_ = std::exchange(other.context_, nullptr);
    }
    return *this;
}

ComputedMetricsReport MetricsEngine::observe(const PublicTelemetryReport &telemetry)
{
    const auto native_telemetry = to_native(telemetry);
    rtxmon_computed_metrics_report_t native_report{};
    native_report.struct_size = sizeof(native_report);

    const auto status = rtxmon_metrics_observe(context_, &native_telemetry, &native_report);
    if (status != RTXMON_STATUS_OK) {
        throw_metrics_status(status, "Could not calculate telemetry metrics");
    }
    if (native_report.metric_count > RTXMON_MAX_COMPUTED_METRICS) {
        throw MonitorError(
            RTXMON_STATUS_ABI_MISMATCH,
            "Computed metric count exceeds the ABI limit");
    }

    ComputedMetricsReport report{
        native_report.gpu_index,
        native_report.timestamp_unix_ms,
        {},
    };
    report.metrics.reserve(native_report.metric_count);
    for (std::uint32_t index = 0U; index < native_report.metric_count; ++index) {
        const auto &source = native_report.metrics[index];
        if (source.input_count > RTXMON_MAX_METRIC_INPUTS) {
            throw MonitorError(
                RTXMON_STATUS_ABI_MISMATCH,
                "Computed metric input count exceeds the ABI limit");
        }

        std::vector<rtxmon_public_field_t> inputs;
        inputs.reserve(source.input_count);
        for (std::uint32_t input = 0U; input < source.input_count; ++input) {
            inputs.push_back(static_cast<rtxmon_public_field_t>(source.input_fields[input]));
        }

        report.metrics.push_back(ComputedMetric{
            static_cast<rtxmon_computed_metric_kind_t>(source.metric),
            static_cast<rtxmon_metric_state_t>(source.state),
            static_cast<rtxmon_data_origin_t>(source.origin),
            static_cast<rtxmon_unit_t>(source.unit),
            source.state == RTXMON_METRIC_STATE_AVAILABLE
                ? std::optional<double>{source.value}
                : std::nullopt,
            source.timestamp_unix_ms,
            source.window_ms,
            source.sample_count,
            source.metric == RTXMON_METRIC_GPU_TEMPERATURE_TIME_ABOVE_THRESHOLD
                ? std::optional<std::int32_t>{source.temperature_threshold_c}
                : std::nullopt,
            std::move(inputs),
        });
    }

    return report;
}

void MetricsEngine::reset() noexcept
{
    rtxmon_metrics_context_reset(context_);
}

const char *computed_metric_name(rtxmon_computed_metric_kind_t metric) noexcept
{
    return rtxmon_computed_metric_string(static_cast<std::uint32_t>(metric));
}

const char *computed_metric_formula(rtxmon_computed_metric_kind_t metric) noexcept
{
    return rtxmon_computed_metric_formula(static_cast<std::uint32_t>(metric));
}

const char *metric_state_name(rtxmon_metric_state_t state) noexcept
{
    return rtxmon_metric_state_string(static_cast<std::uint32_t>(state));
}

} // namespace rtxmon
