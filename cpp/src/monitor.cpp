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

const char *backend_name(rtxmon_temperature_backend_t backend) noexcept
{
    return rtxmon_temperature_backend_string(static_cast<std::uint32_t>(backend));
}

} // namespace rtxmon
