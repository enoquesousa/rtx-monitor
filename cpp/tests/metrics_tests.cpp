#include <rtxmon/metrics.hpp>

#include <cstdint>
#include <iostream>
#include <stdexcept>

namespace {

void require(bool condition, const char *message)
{
    if (!condition) {
        throw std::runtime_error(message);
    }
}

rtxmon::PublicFieldValue temperature_field(
    rtxmon_public_field_t field,
    std::int64_t temperature_c,
    std::uint64_t timestamp_unix_ms)
{
    return rtxmon::PublicFieldValue{
        field,
        field == RTXMON_PUBLIC_FIELD_GPU_DIE_TEMPERATURE_C
            ? RTXMON_PUBLIC_PROVIDER_NVML_TEMPERATURE_V1
            : RTXMON_PUBLIC_PROVIDER_NVML_FIELD_VALUES,
        RTXMON_CAPABILITY_AVAILABLE,
        RTXMON_ORIGIN_DRIVER_REPORTED,
        RTXMON_VALUE_TYPE_SIGNED_INTEGER,
        RTXMON_UNIT_CELSIUS,
        0,
        field == RTXMON_PUBLIC_FIELD_MEMORY_TEMPERATURE_C ? 82U : 0U,
        std::nullopt,
        temperature_c,
        std::nullopt,
        timestamp_unix_ms,
    };
}

rtxmon::PublicTelemetryReport report(
    std::uint64_t timestamp_unix_ms,
    std::int64_t gpu_temperature_c,
    std::optional<std::int64_t> memory_temperature_c)
{
    rtxmon::PublicTelemetryReport value{
        3U,
        std::chrono::system_clock::time_point{
            std::chrono::milliseconds{timestamp_unix_ms}},
        timestamp_unix_ms,
        {},
    };
    value.fields.push_back(temperature_field(
        RTXMON_PUBLIC_FIELD_GPU_DIE_TEMPERATURE_C,
        gpu_temperature_c,
        timestamp_unix_ms));
    if (memory_temperature_c.has_value()) {
        value.fields.push_back(temperature_field(
            RTXMON_PUBLIC_FIELD_MEMORY_TEMPERATURE_C,
            *memory_temperature_c,
            timestamp_unix_ms));
    }
    return value;
}

void test_metrics_window()
{
    rtxmon::MetricsEngine engine(rtxmon::MetricsOptions{5000U, 45, 16U});

    const auto first = engine.observe(report(1000U, 40, 35));
    require(first.metrics.size() == 4U, "four computed metrics must always be emitted");
    require(first.metrics[0].value == 40.0, "first average must equal the only sample");
    require(
        first.metrics[1].state == RTXMON_METRIC_STATE_INSUFFICIENT_DATA,
        "slope must require two samples");
    require(first.metrics[3].value == 5.0, "same-snapshot thermal delta");

    const auto second = engine.observe(report(2000U, 50, std::nullopt));
    require(second.metrics[0].value == 45.0, "two-sample rolling average");
    require(second.metrics[1].value == 10.0, "endpoint slope in Celsius per second");
    require(second.metrics[2].value == 0.0, "zero duration remains an available value");
    require(
        second.metrics[3].state == RTXMON_METRIC_STATE_INPUT_UNAVAILABLE,
        "missing memory temperature must not become zero");

    const auto third = engine.observe(report(3000U, 60, 37));
    require(third.metrics[0].value == 50.0, "three-sample rolling average");
    require(third.metrics[1].value == 10.0, "three-sample endpoint slope");
    require(third.metrics[2].value == 1.0, "left-continuous threshold dwell time");
    require(
        std::string_view{rtxmon::computed_metric_formula(third.metrics[2].metric)}.find(
            "threshold_c") != std::string_view::npos,
        "formula must describe its threshold input");

    engine.reset();
    const auto after_reset = engine.observe(report(4000U, 55, 38));
    require(
        after_reset.metrics[1].state == RTXMON_METRIC_STATE_INSUFFICIENT_DATA,
        "reset must clear historical slope inputs");
}

} // namespace

int main()
{
    try {
        test_metrics_window();
        std::cout << "rtxmon metrics tests passed\n";
        return 0;
    } catch (const std::exception &error) {
        std::cerr << "FAILED: " << error.what() << '\n';
        return 1;
    }
}
