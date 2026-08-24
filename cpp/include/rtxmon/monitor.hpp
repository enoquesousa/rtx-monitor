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

struct BoardIdentity {
    std::uint32_t gpu_index;
    std::uint32_t pci_vendor_id;
    std::uint32_t pci_device_id;
    std::uint32_t pci_subsystem_vendor_id;
    std::uint32_t pci_subsystem_device_id;
    std::uint32_t pci_domain;
    std::uint32_t pci_bus;
    std::uint32_t pci_device;
    std::uint32_t pci_function;
    std::uint32_t flags;
    std::string pci_bus_id;
    std::string vbios_version;
};

struct ThermalProviderResult {
    rtxmon_thermal_provider_t provider;
    rtxmon_capability_state_t state;
    std::int32_t native_status;
    std::uint32_t capability_count;
};

struct ThermalCapability {
    rtxmon_thermal_provider_t provider;
    rtxmon_thermal_target_t target;
    rtxmon_thermal_controller_t controller;
    rtxmon_capability_state_t state;
    rtxmon_sensor_confidence_t confidence;
    std::uint32_t value_flags;
    std::int32_t current_temperature_c;
    std::int32_t default_min_temperature_c;
    std::int32_t default_max_temperature_c;
    std::int32_t native_status;
    std::uint32_t provider_native_id;

    [[nodiscard]] bool has_current_temperature() const noexcept;
    [[nodiscard]] bool has_default_minimum() const noexcept;
    [[nodiscard]] bool has_default_maximum() const noexcept;
};

struct ThermalReport {
    std::uint32_t gpu_index;
    std::chrono::system_clock::time_point captured_at;
    std::uint64_t timestamp_unix_ms;
    std::vector<ThermalProviderResult> providers;
    std::vector<ThermalCapability> capabilities;
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
    [[nodiscard]] BoardIdentity board_identity(std::uint32_t index) const;
    [[nodiscard]] TemperatureSample read_gpu_die_temperature(std::uint32_t index) const;
    [[nodiscard]] ThermalReport scan_thermal_capabilities(std::uint32_t index) const;

private:
    rtxmon_context_t *context_{nullptr};
};

[[nodiscard]] const char *backend_name(rtxmon_temperature_backend_t backend) noexcept;
[[nodiscard]] const char *provider_name(rtxmon_thermal_provider_t provider) noexcept;
[[nodiscard]] const char *capability_state_name(rtxmon_capability_state_t state) noexcept;
[[nodiscard]] const char *thermal_target_name(rtxmon_thermal_target_t target) noexcept;
[[nodiscard]] const char *thermal_controller_name(rtxmon_thermal_controller_t controller) noexcept;
[[nodiscard]] const char *confidence_name(rtxmon_sensor_confidence_t confidence) noexcept;

} // namespace rtxmon

#endif
