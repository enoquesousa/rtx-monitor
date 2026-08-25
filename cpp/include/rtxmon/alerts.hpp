#ifndef RTXMON_ALERTS_HPP
#define RTXMON_ALERTS_HPP

#include <cstdint>
#include <optional>

#include <rtxmon/sampler.hpp>

namespace rtxmon {

struct AlertOptions {
    std::int32_t threshold_c;
    std::int32_t hysteresis_c{0};
};

// Pure state machine: turns a stream of die temperatures into alert_raised /
// alert_cleared transitions. Holds no session, thread, or clock of its own,
// so it stays testable without a GPU and independent of the resilient
// sampler's reconnect/backoff policy.
class AlertEvaluator final {
public:
    explicit AlertEvaluator(AlertOptions options);

    [[nodiscard]] std::optional<TelemetryEventKind> observe(std::int32_t temperature_c) noexcept;
    [[nodiscard]] bool alarmed() const noexcept;
    [[nodiscard]] const AlertOptions &options() const noexcept;

private:
    AlertOptions options_;
    bool alarmed_{false};
};

} // namespace rtxmon

#endif
