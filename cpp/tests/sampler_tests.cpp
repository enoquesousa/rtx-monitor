#include <rtxmon/sampler.hpp>

#include <chrono>
#include <cstdint>
#include <deque>
#include <functional>
#include <iostream>
#include <memory>
#include <string>
#include <utility>
#include <vector>

namespace {

struct ReadOutcome {
    rtxmon_status_t status{RTXMON_STATUS_OK};
    std::uint32_t gpu_index{};
    std::int32_t temperature_c{};
    std::uint64_t timestamp_unix_ms{};
};

struct SessionScript {
    std::vector<rtxmon::GpuInfo> gpus;
    std::deque<ReadOutcome> reads;
};

class FakeSession final : public rtxmon::MonitoringSession {
public:
    explicit FakeSession(SessionScript script)
        : script_(std::move(script))
    {
    }

    [[nodiscard]] std::vector<rtxmon::GpuInfo> gpus() const override
    {
        return script_.gpus;
    }

    [[nodiscard]] rtxmon::TemperatureSample read_gpu_die_temperature(
        std::uint32_t index) const override
    {
        if (script_.reads.empty()) {
            throw rtxmon::MonitorError(
                RTXMON_STATUS_BACKEND_ERROR,
                "fake session has no scripted read");
        }

        const auto outcome = script_.reads.front();
        script_.reads.pop_front();
        if (outcome.status != RTXMON_STATUS_OK) {
            throw rtxmon::MonitorError(outcome.status, "scripted read failure");
        }
        if (outcome.gpu_index != index) {
            throw rtxmon::MonitorError(
                RTXMON_STATUS_BACKEND_ERROR,
                "fake session received an unexpected GPU index");
        }

        return rtxmon::TemperatureSample{
            outcome.gpu_index,
            outcome.temperature_c,
            RTXMON_SENSOR_GPU_DIE,
            RTXMON_BACKEND_NVML_TEMPERATURE_V1,
            std::chrono::system_clock::time_point{
                std::chrono::milliseconds{outcome.timestamp_unix_ms}},
            outcome.timestamp_unix_ms,
        };
    }

private:
    mutable SessionScript script_;
};

[[nodiscard]] rtxmon::GpuInfo gpu(std::uint32_t index, std::string uuid)
{
    return rtxmon::GpuInfo{
        index,
        "Fake NVIDIA RTX",
        std::move(uuid),
        "test-driver",
        "test-nvml",
    };
}

[[nodiscard]] rtxmon::MonitoringSessionFactory scripted_factory(
    std::deque<SessionScript> scripts)
{
    auto shared_scripts =
        std::make_shared<std::deque<SessionScript>>(std::move(scripts));

    return [shared_scripts]() -> std::unique_ptr<rtxmon::MonitoringSession> {
        if (shared_scripts->empty()) {
            throw rtxmon::MonitorError(
                RTXMON_STATUS_BACKEND_ERROR,
                "no scripted session remains");
        }

        auto script = std::move(shared_scripts->front());
        shared_scripts->pop_front();
        return std::make_unique<FakeSession>(std::move(script));
    };
}

int check(bool condition, const std::string &message)
{
    if (condition) {
        return 0;
    }

    std::cerr << "FAILED: " << message << '\n';
    return 1;
}

int test_circular_buffer()
{
    int failures = 0;
    rtxmon::CircularEventBuffer buffer{2U};

    for (std::uint64_t sequence = 1U; sequence <= 3U; ++sequence) {
        rtxmon::TelemetryEvent event;
        event.sequence = sequence;
        buffer.push(std::move(event));
    }

    const auto events = buffer.snapshot();
    failures += check(buffer.capacity() == 2U, "buffer capacity");
    failures += check(buffer.size() == 2U, "buffer bounded size");
    failures += check(events.size() == 2U, "buffer snapshot size");
    failures += check(events[0].sequence == 2U, "buffer oldest retained sequence");
    failures += check(events[1].sequence == 3U, "buffer newest retained sequence");
    return failures;
}

int test_sample_and_case_insensitive_uuid()
{
    constexpr std::uint64_t timestamp = 1'700'000'000'123ULL;
    auto factory = scripted_factory({
        SessionScript{
            {gpu(2U, "GPU-ABC")},
            {{RTXMON_STATUS_OK, 2U, 41, timestamp}},
        },
    });

    rtxmon::ResilientSampler sampler{
        "gpu-abc",
        rtxmon::SamplerOptions{4U, 100U, 400U},
        std::move(factory)};

    const auto events = sampler.poll();
    int failures = 0;
    failures += check(events.size() == 1U, "successful poll event count");
    failures += check(
        events[0].kind == rtxmon::TelemetryEventKind::sample,
        "successful poll event kind");
    failures += check(events[0].sample.has_value(), "sample payload present");
    if (events[0].sample.has_value()) {
        failures += check(
            events[0].sample->temperature_c == 41,
            "sample temperature preserved");
        failures += check(
            events[0].sample->timestamp_unix_ms == timestamp,
            "sample timestamp preserved");
    }
    failures += check(events[0].gpu.has_value(), "resolved GPU present");
    if (events[0].gpu.has_value()) {
        failures += check(events[0].gpu->index == 2U, "resolved GPU index");
    }
    failures += check(sampler.consecutive_failures() == 0U, "failure count reset");
    failures += check(sampler.next_delay_ms(1000U) == 1000U, "sample delay");
    return failures;
}

int test_gap_recovery_and_index_change()
{
    auto factory = scripted_factory({
        SessionScript{
            {gpu(1U, "GPU-STABLE")},
            {{RTXMON_STATUS_GPU_LOST, 1U, 0, 0U}},
        },
        SessionScript{
            {gpu(4U, "GPU-STABLE")},
            {{RTXMON_STATUS_OK, 4U, 44, 1'700'000'001'000ULL}},
        },
    });

    rtxmon::ResilientSampler sampler{
        "GPU-STABLE",
        rtxmon::SamplerOptions{3U, 125U, 500U},
        std::move(factory)};

    const auto failed = sampler.poll();
    const auto recovered = sampler.poll();
    const auto history = sampler.recent_events();

    int failures = 0;
    failures += check(failed.size() == 1U, "gap poll event count");
    failures += check(
        failed[0].kind == rtxmon::TelemetryEventKind::gap,
        "gap event kind");
    failures += check(
        failed[0].status == RTXMON_STATUS_GPU_LOST,
        "gap status preserved");
    failures += check(failed[0].retry_after_ms == 125U, "initial backoff");
    failures += check(recovered.size() == 2U, "recovery emits two events");
    failures += check(
        recovered[0].kind == rtxmon::TelemetryEventKind::recovered,
        "recovered event kind");
    failures += check(
        recovered[0].consecutive_failures == 1U,
        "recovered failure count");
    failures += check(
        recovered[1].kind == rtxmon::TelemetryEventKind::sample,
        "sample follows recovery");
    failures += check(recovered[1].gpu.has_value(), "recovered GPU present");
    if (recovered[1].gpu.has_value()) {
        failures += check(
            recovered[1].gpu->index == 4U,
            "UUID resolved after index change");
    }
    failures += check(
        recovered[0].observed_at_unix_ms <= recovered[1].observed_at_unix_ms,
        "recovery timestamps do not move backward");
    failures += check(history.size() == 3U, "recovery history size");
    failures += check(history[0].sequence == 1U, "gap sequence");
    failures += check(history[1].sequence == 2U, "recovered sequence");
    failures += check(history[2].sequence == 3U, "sample sequence");
    return failures;
}

int test_backoff_cap()
{
    auto factory = []() -> std::unique_ptr<rtxmon::MonitoringSession> {
        throw rtxmon::MonitorError(
            RTXMON_STATUS_DRIVER_NOT_LOADED,
            "scripted driver outage");
    };

    rtxmon::ResilientSampler sampler{
        "GPU-OFFLINE",
        rtxmon::SamplerOptions{8U, 100U, 250U},
        std::move(factory)};

    const auto first = sampler.poll();
    const auto second = sampler.poll();
    const auto third = sampler.poll();
    const auto fourth = sampler.poll();

    int failures = 0;
    failures += check(first[0].retry_after_ms == 100U, "first backoff");
    failures += check(second[0].retry_after_ms == 200U, "second backoff");
    failures += check(third[0].retry_after_ms == 250U, "capped backoff");
    failures += check(fourth[0].retry_after_ms == 250U, "stable capped backoff");
    failures += check(
        sampler.consecutive_failures() == 4U,
        "consecutive failure count");
    return failures;
}

int test_nonrecoverable_status()
{
    auto factory = []() -> std::unique_ptr<rtxmon::MonitoringSession> {
        throw rtxmon::MonitorError(
            RTXMON_STATUS_NO_PERMISSION,
            "scripted permission failure");
    };

    rtxmon::ResilientSampler sampler{
        "GPU-DENIED",
        rtxmon::SamplerOptions{},
        std::move(factory)};

    try {
        (void)sampler.poll();
    } catch (const rtxmon::MonitorError &error) {
        return check(
            error.status() == RTXMON_STATUS_NO_PERMISSION,
            "nonrecoverable status preserved");
    }

    return check(false, "nonrecoverable status must escape poll");
}

} // namespace

int main()
{
    int failures = 0;
    failures += test_circular_buffer();
    failures += test_sample_and_case_insensitive_uuid();
    failures += test_gap_recovery_and_index_change();
    failures += test_backoff_cap();
    failures += test_nonrecoverable_status();

    if (failures == 0) {
        std::cout << "rtxmon sampler tests passed\n";
    }
    return failures == 0 ? 0 : 1;
}
