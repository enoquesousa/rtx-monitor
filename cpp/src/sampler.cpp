#include <rtxmon/sampler.hpp>

#include <algorithm>
#include <chrono>
#include <cctype>
#include <limits>
#include <sstream>
#include <stdexcept>
#include <string_view>
#include <utility>

namespace rtxmon {
namespace {

class NativeMonitoringSession final : public MonitoringSession {
public:
    [[nodiscard]] std::vector<GpuInfo> gpus() const override
    {
        return monitor_.gpus();
    }

    [[nodiscard]] TemperatureSample read_gpu_die_temperature(
        std::uint32_t index) const override
    {
        return monitor_.read_gpu_die_temperature(index);
    }

private:
    Monitor monitor_;
};

[[nodiscard]] MonitoringSessionFactory native_session_factory()
{
    return [] {
        return std::make_unique<NativeMonitoringSession>();
    };
}

[[nodiscard]] bool ascii_case_insensitive_equal(
    std::string_view left,
    std::string_view right) noexcept
{
    if (left.size() != right.size()) {
        return false;
    }

    return std::equal(
        left.begin(),
        left.end(),
        right.begin(),
        [](char left_character, char right_character) {
            const auto left_value = static_cast<unsigned char>(left_character);
            const auto right_value = static_cast<unsigned char>(right_character);
            return std::tolower(left_value) == std::tolower(right_value);
        });
}

[[nodiscard]] std::uint64_t now_unix_ms()
{
    const auto now = std::chrono::system_clock::now();
    const auto milliseconds = std::chrono::duration_cast<std::chrono::milliseconds>(
        now.time_since_epoch());
    return static_cast<std::uint64_t>(milliseconds.count());
}

} // namespace

CircularEventBuffer::CircularEventBuffer(std::size_t capacity)
    : capacity_(capacity)
{
    if (capacity_ == 0U) {
        throw std::invalid_argument("event buffer capacity must be greater than zero");
    }

    events_.reserve(capacity_);
}

void CircularEventBuffer::push(TelemetryEvent event)
{
    if (events_.size() < capacity_) {
        events_.push_back(std::move(event));
        return;
    }

    events_[start_] = std::move(event);
    start_ = (start_ + 1U) % capacity_;
}

std::vector<TelemetryEvent> CircularEventBuffer::snapshot() const
{
    if (events_.size() < capacity_ || start_ == 0U) {
        return events_;
    }

    std::vector<TelemetryEvent> ordered;
    ordered.reserve(events_.size());
    for (std::size_t offset = 0U; offset < events_.size(); ++offset) {
        const auto index = (start_ + offset) % events_.size();
        ordered.push_back(events_[index]);
    }
    return ordered;
}

std::size_t CircularEventBuffer::size() const noexcept
{
    return events_.size();
}

std::size_t CircularEventBuffer::capacity() const noexcept
{
    return capacity_;
}

ResilientSampler::ResilientSampler(
    std::string target_gpu_uuid,
    SamplerOptions options,
    MonitoringSessionFactory session_factory)
    : target_gpu_uuid_(std::move(target_gpu_uuid)),
      options_(options),
      session_factory_(std::move(session_factory)),
      events_(options.buffer_capacity),
      next_backoff_ms_(options.initial_backoff_ms)
{
    if (target_gpu_uuid_.empty()) {
        throw std::invalid_argument("target GPU UUID must not be empty");
    }
    if (options_.initial_backoff_ms == 0U) {
        throw std::invalid_argument("initial backoff must be greater than zero");
    }
    if (options_.maximum_backoff_ms < options_.initial_backoff_ms) {
        throw std::invalid_argument(
            "maximum backoff must be greater than or equal to initial backoff");
    }
    if (!session_factory_) {
        session_factory_ = native_session_factory();
    }
}

ResilientSampler::~ResilientSampler() = default;
ResilientSampler::ResilientSampler(ResilientSampler &&) noexcept = default;
ResilientSampler &ResilientSampler::operator=(ResilientSampler &&) noexcept = default;

std::vector<TelemetryEvent> ResilientSampler::poll()
{
    std::vector<TelemetryEvent> emitted;
    emitted.reserve(2U);

    try {
        if (!session_) {
            connect();
        }

        auto sample = session_->read_gpu_die_temperature(current_gpu_->index);
        if (sample.gpu_index != current_gpu_->index) {
            throw MonitorError(
                RTXMON_STATUS_BACKEND_ERROR,
                "temperature sample belongs to a different GPU index");
        }

        if (consecutive_failures_ > 0U) {
            auto recovered = base_event(TelemetryEventKind::recovered);
            recovered.observed_at_unix_ms = sample.timestamp_unix_ms;
            recovered.consecutive_failures = consecutive_failures_;

            std::ostringstream message;
            message << "monitoring recovered after " << consecutive_failures_
                    << (consecutive_failures_ == 1U ? " failure" : " failures");
            recovered.message = message.str();
            record(std::move(recovered), emitted);
        }

        consecutive_failures_ = 0U;
        pending_retry_ms_ = 0U;
        next_backoff_ms_ = options_.initial_backoff_ms;

        auto sample_event = base_event(TelemetryEventKind::sample);
        sample_event.observed_at_unix_ms = sample.timestamp_unix_ms;
        sample_event.sample = std::move(sample);
        record(std::move(sample_event), emitted);
    } catch (const MonitorError &error) {
        if (!is_recoverable_sampling_status(error.status())) {
            throw;
        }

        if (consecutive_failures_ < std::numeric_limits<std::uint32_t>::max()) {
            ++consecutive_failures_;
        }

        auto gap = base_event(TelemetryEventKind::gap);
        gap.status = error.status();
        gap.message = error.what();
        gap.consecutive_failures = consecutive_failures_;
        gap.retry_after_ms = advance_backoff();
        pending_retry_ms_ = gap.retry_after_ms;
        record(std::move(gap), emitted);

        session_.reset();
        current_gpu_.reset();
    }

    return emitted;
}

std::vector<TelemetryEvent> ResilientSampler::recent_events() const
{
    return events_.snapshot();
}

std::uint32_t ResilientSampler::next_delay_ms(
    std::uint32_t successful_sample_interval_ms) const noexcept
{
    return consecutive_failures_ == 0U
        ? successful_sample_interval_ms
        : pending_retry_ms_;
}

const std::string &ResilientSampler::target_gpu_uuid() const noexcept
{
    return target_gpu_uuid_;
}

std::uint32_t ResilientSampler::consecutive_failures() const noexcept
{
    return consecutive_failures_;
}

void ResilientSampler::connect()
{
    auto candidate = session_factory_();
    if (!candidate) {
        throw MonitorError(
            RTXMON_STATUS_BACKEND_ERROR,
            "monitoring session factory returned no session");
    }

    const auto available_gpus = candidate->gpus();
    const auto match = std::find_if(
        available_gpus.begin(),
        available_gpus.end(),
        [this](const GpuInfo &gpu) {
            return ascii_case_insensitive_equal(gpu.uuid, target_gpu_uuid_);
        });

    if (match == available_gpus.end()) {
        throw MonitorError(
            RTXMON_STATUS_GPU_NOT_FOUND,
            "target GPU UUID is not currently available: " + target_gpu_uuid_);
    }

    current_gpu_ = *match;
    session_ = std::move(candidate);
}

void ResilientSampler::record(
    TelemetryEvent event,
    std::vector<TelemetryEvent> &emitted)
{
    events_.push(event);
    emitted.push_back(std::move(event));
}

TelemetryEvent ResilientSampler::base_event(TelemetryEventKind kind)
{
    TelemetryEvent event;
    event.sequence = next_sequence_++;
    event.kind = kind;
    event.target_gpu_uuid = target_gpu_uuid_;
    event.gpu = current_gpu_;
    event.observed_at_unix_ms = now_unix_ms();
    event.status = RTXMON_STATUS_OK;
    event.consecutive_failures = consecutive_failures_;
    return event;
}

std::uint32_t ResilientSampler::advance_backoff() noexcept
{
    const auto current = next_backoff_ms_;
    if (next_backoff_ms_ >= options_.maximum_backoff_ms ||
        next_backoff_ms_ > options_.maximum_backoff_ms / 2U) {
        next_backoff_ms_ = options_.maximum_backoff_ms;
    } else {
        next_backoff_ms_ *= 2U;
    }
    return current;
}

bool is_recoverable_sampling_status(rtxmon_status_t status) noexcept
{
    switch (status) {
    case RTXMON_STATUS_BACKEND_NOT_FOUND:
    case RTXMON_STATUS_BACKEND_SYMBOL_MISSING:
    case RTXMON_STATUS_DRIVER_NOT_LOADED:
    case RTXMON_STATUS_GPU_NOT_FOUND:
    case RTXMON_STATUS_GPU_LOST:
    case RTXMON_STATUS_BACKEND_ERROR:
        return true;
    case RTXMON_STATUS_OK:
    case RTXMON_STATUS_INVALID_ARGUMENT:
    case RTXMON_STATUS_OUT_OF_MEMORY:
    case RTXMON_STATUS_NO_PERMISSION:
    case RTXMON_STATUS_NOT_SUPPORTED:
    case RTXMON_STATUS_ABI_MISMATCH:
    default:
        return false;
    }
}

const char *telemetry_event_kind_name(TelemetryEventKind kind) noexcept
{
    switch (kind) {
    case TelemetryEventKind::sample:
        return "sample";
    case TelemetryEventKind::gap:
        return "gap";
    case TelemetryEventKind::recovered:
        return "recovered";
    case TelemetryEventKind::alert_raised:
        return "alert_raised";
    case TelemetryEventKind::alert_cleared:
        return "alert_cleared";
    default:
        return "unknown";
    }
}

} // namespace rtxmon
