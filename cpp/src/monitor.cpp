#include <rtxmon/monitor.hpp>

#include <cstring>
#include <sstream>
#include <utility>

namespace rtxmon {
namespace {

[[noreturn]] void throw_for_status(rtxmon_status_t status, const char *operation)
{
    std::ostringstream message;
    const char *diagnostic = rtxmon_last_error();

    message << operation << ": " << rtxmon_status_string(status);
    if (diagnostic != nullptr && diagnostic[0] != '\0') {
        message << " (" << diagnostic << ')';
    }

    throw MonitorError(status, message.str());
}

} // namespace

MonitorError::MonitorError(rtxmon_status_t status, const std::string &message)
    : std::runtime_error(message), status_(status)
{
}

rtxmon_status_t MonitorError::status() const noexcept
{
    return status_;
}

Monitor::Monitor()
{
    const auto native_abi = rtxmon_abi_version();
    if (native_abi != RTXMON_ABI_VERSION) {
        std::ostringstream message;
        message << "Native ABI mismatch: C++ expects " << RTXMON_ABI_VERSION
                << ", library exposes " << native_abi;
        throw MonitorError(RTXMON_STATUS_ABI_MISMATCH, message.str());
    }

    const auto status = rtxmon_context_create(&context_);
    if (status != RTXMON_STATUS_OK) {
        throw_for_status(status, "Could not initialize the NVIDIA monitor");
    }
}

Monitor::~Monitor()
{
    rtxmon_context_destroy(context_);
}

Monitor::Monitor(Monitor &&other) noexcept
    : context_(std::exchange(other.context_, nullptr))
{
}

Monitor &Monitor::operator=(Monitor &&other) noexcept
{
    if (this != &other) {
        rtxmon_context_destroy(context_);
        context_ = std::exchange(other.context_, nullptr);
    }

    return *this;
}

std::vector<GpuInfo> Monitor::gpus() const
{
    std::uint32_t count = 0;
    const auto status = rtxmon_get_gpu_count(context_, &count);
    if (status != RTXMON_STATUS_OK) {
        throw_for_status(status, "Could not enumerate NVIDIA GPUs");
    }

    std::vector<GpuInfo> result;
    result.reserve(count);
    for (std::uint32_t index = 0; index < count; ++index) {
        result.push_back(gpu(index));
    }
    return result;
}

GpuInfo Monitor::gpu(std::uint32_t index) const
{
    rtxmon_gpu_info_t native_info{};
    native_info.struct_size = sizeof(native_info);

    const auto status = rtxmon_get_gpu_info(context_, index, &native_info);
    if (status != RTXMON_STATUS_OK) {
        throw_for_status(status, "Could not read NVIDIA GPU information");
    }

    return GpuInfo{
        native_info.index,
        native_info.name,
        native_info.uuid,
        native_info.driver_version,
        native_info.nvml_version,
    };
}

TemperatureSample Monitor::read_gpu_die_temperature(std::uint32_t index) const
{
    rtxmon_temperature_sample_t native_sample{};
    native_sample.struct_size = sizeof(native_sample);

    const auto status = rtxmon_read_gpu_die_temperature(context_, index, &native_sample);
    if (status != RTXMON_STATUS_OK) {
        throw_for_status(status, "Could not read the NVIDIA GPU die sensor");
    }

    const auto captured_at = std::chrono::system_clock::time_point{
        std::chrono::milliseconds{native_sample.timestamp_unix_ms}};

    return TemperatureSample{
        native_sample.gpu_index,
        native_sample.temperature_c,
        static_cast<rtxmon_sensor_kind_t>(native_sample.sensor_kind),
        static_cast<rtxmon_temperature_backend_t>(native_sample.backend),
        captured_at,
        native_sample.timestamp_unix_ms,
    };
}

BoardIdentity Monitor::board_identity(std::uint32_t index) const
{
    rtxmon_board_identity_t native_identity{};
    native_identity.struct_size = sizeof(native_identity);

    const auto status = rtxmon_get_board_identity(context_, index, &native_identity);
    if (status != RTXMON_STATUS_OK) {
        throw_for_status(status, "Could not read NVIDIA board identity");
    }

    return BoardIdentity{
        native_identity.gpu_index,
        native_identity.pci_vendor_id,
        native_identity.pci_device_id,
        native_identity.pci_subsystem_vendor_id,
        native_identity.pci_subsystem_device_id,
        native_identity.pci_domain,
        native_identity.pci_bus,
        native_identity.pci_device,
        native_identity.pci_function,
        native_identity.flags,
        native_identity.pci_bus_id,
        native_identity.vbios_version,
    };
}

bool ThermalCapability::has_current_temperature() const noexcept
{
    return (value_flags & RTXMON_THERMAL_VALUE_CURRENT_VALID) != 0U;
}

bool ThermalCapability::has_default_minimum() const noexcept
{
    return (value_flags & RTXMON_THERMAL_VALUE_DEFAULT_MIN_VALID) != 0U;
}

bool ThermalCapability::has_default_maximum() const noexcept
{
    return (value_flags & RTXMON_THERMAL_VALUE_DEFAULT_MAX_VALID) != 0U;
}

ThermalReport Monitor::scan_thermal_capabilities(std::uint32_t index) const
{
    rtxmon_thermal_report_t native_report{};
    native_report.struct_size = sizeof(native_report);

    const auto status = rtxmon_scan_thermal_capabilities(context_, index, &native_report);
    if (status != RTXMON_STATUS_OK) {
        throw_for_status(status, "Could not scan NVIDIA thermal capabilities");
    }

    if (native_report.provider_count > RTXMON_MAX_THERMAL_PROVIDERS ||
        native_report.capability_count > RTXMON_MAX_THERMAL_CAPABILITIES) {
        throw MonitorError(
            RTXMON_STATUS_ABI_MISMATCH,
            "Thermal report count exceeds the ABI limits");
    }

    ThermalReport report{
        native_report.gpu_index,
        std::chrono::system_clock::time_point{
            std::chrono::milliseconds{native_report.timestamp_unix_ms}},
        native_report.timestamp_unix_ms,
        {},
        {},
    };
    report.providers.reserve(native_report.provider_count);
    report.capabilities.reserve(native_report.capability_count);

    for (std::uint32_t provider_index = 0;
         provider_index < native_report.provider_count;
         ++provider_index) {
        const auto &provider = native_report.providers[provider_index];
        report.providers.push_back(ThermalProviderResult{
            static_cast<rtxmon_thermal_provider_t>(provider.provider),
            static_cast<rtxmon_capability_state_t>(provider.state),
            provider.native_status,
            provider.capability_count,
        });
    }

    for (std::uint32_t capability_index = 0;
         capability_index < native_report.capability_count;
         ++capability_index) {
        const auto &capability = native_report.capabilities[capability_index];
        report.capabilities.push_back(ThermalCapability{
            static_cast<rtxmon_thermal_provider_t>(capability.provider),
            static_cast<rtxmon_thermal_target_t>(capability.target),
            static_cast<rtxmon_thermal_controller_t>(capability.controller),
            static_cast<rtxmon_capability_state_t>(capability.state),
            static_cast<rtxmon_sensor_confidence_t>(capability.confidence),
            capability.value_flags,
            capability.current_temperature_c,
            capability.default_min_temperature_c,
            capability.default_max_temperature_c,
            capability.native_status,
            capability.provider_native_id,
        });
    }

    return report;
}

const char *backend_name(rtxmon_temperature_backend_t backend) noexcept
{
    return rtxmon_temperature_backend_string(static_cast<std::uint32_t>(backend));
}

const char *provider_name(rtxmon_thermal_provider_t provider) noexcept
{
    return rtxmon_thermal_provider_string(static_cast<std::uint32_t>(provider));
}

const char *capability_state_name(rtxmon_capability_state_t state) noexcept
{
    return rtxmon_capability_state_string(static_cast<std::uint32_t>(state));
}

const char *thermal_target_name(rtxmon_thermal_target_t target) noexcept
{
    return rtxmon_thermal_target_string(static_cast<std::uint32_t>(target));
}

const char *thermal_controller_name(rtxmon_thermal_controller_t controller) noexcept
{
    return rtxmon_thermal_controller_string(static_cast<std::uint32_t>(controller));
}

const char *confidence_name(rtxmon_sensor_confidence_t confidence) noexcept
{
    return rtxmon_sensor_confidence_string(static_cast<std::uint32_t>(confidence));
}

} // namespace rtxmon
