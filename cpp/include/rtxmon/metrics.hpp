#ifndef RTXMON_METRICS_HPP
#define RTXMON_METRICS_HPP

#include <cstdint>
#include <optional>
#include <vector>

#include <rtxmon/monitor.hpp>

namespace rtxmon {

struct MetricsOptions {
    std::uint32_t window_ms{5000U};
    std::int32_t temperature_threshold_c{80};
    std::uint32_t maximum_samples{1024U};
};

struct ComputedMetric {
    rtxmon_computed_metric_kind_t metric;
    rtxmon_metric_state_t state;
    rtxmon_data_origin_t origin;
    rtxmon_unit_t unit;
    std::optional<double> value;
    std::uint64_t timestamp_unix_ms;
    std::uint64_t window_ms;
    std::uint32_t sample_count;
    std::optional<std::int32_t> temperature_threshold_c;
    std::vector<rtxmon_public_field_t> inputs;
};

struct ComputedMetricsReport {
    std::uint32_t gpu_index;
    std::uint64_t timestamp_unix_ms;
    std::vector<ComputedMetric> metrics;
};

class MetricsEngine final {
public:
    explicit MetricsEngine(MetricsOptions options = {});
    ~MetricsEngine();

    MetricsEngine(const MetricsEngine &) = delete;
    MetricsEngine &operator=(const MetricsEngine &) = delete;

    MetricsEngine(MetricsEngine &&other) noexcept;
    MetricsEngine &operator=(MetricsEngine &&other) noexcept;

    [[nodiscard]] ComputedMetricsReport observe(const PublicTelemetryReport &telemetry);
    void reset() noexcept;

private:
    rtxmon_metrics_context_t *context_{nullptr};
};

[[nodiscard]] const char *computed_metric_name(
    rtxmon_computed_metric_kind_t metric) noexcept;
[[nodiscard]] const char *computed_metric_formula(
    rtxmon_computed_metric_kind_t metric) noexcept;
[[nodiscard]] const char *metric_state_name(rtxmon_metric_state_t state) noexcept;

} // namespace rtxmon

#endif
