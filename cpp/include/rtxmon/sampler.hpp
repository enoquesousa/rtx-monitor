#ifndef RTXMON_SAMPLER_HPP
#define RTXMON_SAMPLER_HPP

#include <cstddef>
#include <cstdint>
#include <functional>
#include <memory>
#include <optional>
#include <string>
#include <vector>

#include <rtxmon/monitor.hpp>

namespace rtxmon {

enum class TelemetryEventKind {
    sample,
    gap,
    recovered,
};

struct TelemetryEvent {
    std::uint64_t sequence{};
    TelemetryEventKind kind{TelemetryEventKind::gap};
    std::string target_gpu_uuid;
    std::optional<GpuInfo> gpu;
    std::optional<TemperatureSample> sample;
    std::uint64_t observed_at_unix_ms{};
    rtxmon_status_t status{RTXMON_STATUS_OK};
    std::string message;
    std::uint32_t consecutive_failures{};
    std::uint32_t retry_after_ms{};
};

struct SamplerOptions {
    std::size_t buffer_capacity{256U};
    std::uint32_t initial_backoff_ms{250U};
    std::uint32_t maximum_backoff_ms{5000U};
};

class MonitoringSession {
public:
    virtual ~MonitoringSession() = default;

    [[nodiscard]] virtual std::vector<GpuInfo> gpus() const = 0;
    [[nodiscard]] virtual TemperatureSample read_gpu_die_temperature(
        std::uint32_t index) const = 0;
};

using MonitoringSessionFactory =
    std::function<std::unique_ptr<MonitoringSession>()>;

class CircularEventBuffer final {
public:
    explicit CircularEventBuffer(std::size_t capacity);

    void push(TelemetryEvent event);

    [[nodiscard]] std::vector<TelemetryEvent> snapshot() const;
    [[nodiscard]] std::size_t size() const noexcept;
    [[nodiscard]] std::size_t capacity() const noexcept;

private:
    std::size_t capacity_;
    std::size_t start_{0U};
    std::vector<TelemetryEvent> events_;
};

class ResilientSampler final {
public:
    explicit ResilientSampler(
        std::string target_gpu_uuid,
        SamplerOptions options = {},
        MonitoringSessionFactory session_factory = {});

    ~ResilientSampler();

    ResilientSampler(const ResilientSampler &) = delete;
    ResilientSampler &operator=(const ResilientSampler &) = delete;
    ResilientSampler(ResilientSampler &&) noexcept;
    ResilientSampler &operator=(ResilientSampler &&) noexcept;

    [[nodiscard]] std::vector<TelemetryEvent> poll();
    [[nodiscard]] std::vector<TelemetryEvent> recent_events() const;
    [[nodiscard]] std::uint32_t next_delay_ms(
        std::uint32_t successful_sample_interval_ms) const noexcept;
    [[nodiscard]] const std::string &target_gpu_uuid() const noexcept;
    [[nodiscard]] std::uint32_t consecutive_failures() const noexcept;

private:
    void connect();
    void record(TelemetryEvent event, std::vector<TelemetryEvent> &emitted);
    [[nodiscard]] TelemetryEvent base_event(TelemetryEventKind kind);
    [[nodiscard]] std::uint32_t advance_backoff() noexcept;

    std::string target_gpu_uuid_;
    SamplerOptions options_;
    MonitoringSessionFactory session_factory_;
    std::unique_ptr<MonitoringSession> session_;
    std::optional<GpuInfo> current_gpu_;
    CircularEventBuffer events_;
    std::uint64_t next_sequence_{1U};
    std::uint32_t consecutive_failures_{0U};
    std::uint32_t next_backoff_ms_;
    std::uint32_t pending_retry_ms_{0U};
};

[[nodiscard]] bool is_recoverable_sampling_status(rtxmon_status_t status) noexcept;
[[nodiscard]] const char *telemetry_event_kind_name(TelemetryEventKind kind) noexcept;

} // namespace rtxmon

#endif
