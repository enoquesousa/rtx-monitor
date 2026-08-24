#ifndef RTXMON_MONITOR_HPP
#define RTXMON_MONITOR_HPP

#include <chrono>
#include <cstdint>
#include <stdexcept>
#include <string>
#include <vector>

#include <rtxmon/rtxmon.h>

namespace rtxmon {

class MonitorError final : public std::runtime_error {
public:
    MonitorError(rtxmon_status_t status, const std::string &message);

    [[nodiscard]] rtxmon_status_t status() const noexcept;

private:
    rtxmon_status_t status_;
};

struct GpuInfo {
    std::uint32_t index;
    std::string name;
    std::string uuid;
    std::string driver_version;
    std::string nvml_version;
};

struct TemperatureSample {
    std::uint32_t gpu_index;
    std::int32_t temperature_c;
    rtxmon_sensor_kind_t sensor_kind;
    rtxmon_temperature_backend_t backend;
    std::chrono::system_clock::time_point captured_at;
    std::uint64_t timestamp_unix_ms;
};

class Monitor final {
public:
    Monitor();
    ~Monitor();

    Monitor(const Monitor &) = delete;
    Monitor &operator=(const Monitor &) = delete;

    Monitor(Monitor &&other) noexcept;
    Monitor &operator=(Monitor &&other) noexcept;

    [[nodiscard]] std::vector<GpuInfo> gpus() const;
    [[nodiscard]] GpuInfo gpu(std::uint32_t index) const;
    [[nodiscard]] TemperatureSample read_gpu_die_temperature(std::uint32_t index) const;

private:
    rtxmon_context_t *context_{nullptr};
};

[[nodiscard]] const char *backend_name(rtxmon_temperature_backend_t backend) noexcept;

} // namespace rtxmon

#endif
