#include <rtxmon/alerts.hpp>

#include <stdexcept>

namespace rtxmon {

AlertEvaluator::AlertEvaluator(AlertOptions options)
    : options_(options)
{
    if (options_.hysteresis_c < 0) {
        throw std::invalid_argument("alert hysteresis must not be negative");
    }
    if (options_.hysteresis_c > options_.threshold_c) {
        throw std::invalid_argument("alert hysteresis must not exceed the threshold");
    }
}

std::optional<TelemetryEventKind> AlertEvaluator::observe(std::int32_t temperature_c) noexcept
{
    if (!alarmed_ && temperature_c >= options_.threshold_c) {
        alarmed_ = true;
        return TelemetryEventKind::alert_raised;
    }

    const auto clear_temperature = options_.threshold_c - options_.hysteresis_c;
    const bool dropped_below_threshold = options_.hysteresis_c == 0
        ? temperature_c < clear_temperature
        : temperature_c <= clear_temperature;
    if (alarmed_ && dropped_below_threshold) {
        alarmed_ = false;
        return TelemetryEventKind::alert_cleared;
    }

    return std::nullopt;
}

bool AlertEvaluator::alarmed() const noexcept
{
    return alarmed_;
}

const AlertOptions &AlertEvaluator::options() const noexcept
{
    return options_;
}

} // namespace rtxmon
