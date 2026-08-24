#include <rtxmon/monitor.hpp>

#include <atomic>
#include <charconv>
#include <chrono>
#include <csignal>
#include <cstdint>
#include <cstdlib>
#include <iomanip>
#include <iostream>
#include <limits>
#include <sstream>
#include <stdexcept>
#include <string>
#include <string_view>
#include <thread>

namespace {

std::atomic_bool running{true};

enum class Mode {
    once,
    watch,
    list,
    capabilities,
};

struct Options {
    Mode mode{Mode::once};
    std::uint32_t gpu_index{0};
    std::uint32_t interval_ms{1000};
    std::uint64_t count{0};
    bool json{false};
};

void on_interrupt(int)
{
    running.store(false);
}

[[noreturn]] void usage_error(const std::string &message)
{
    throw std::invalid_argument(message + "; use --help for usage");
}

template <typename T>
T parse_unsigned(std::string_view text, const char *option)
{
    std::uint64_t parsed = 0;
    const auto result = std::from_chars(text.data(), text.data() + text.size(), parsed);
    if (result.ec != std::errc{} || result.ptr != text.data() + text.size() ||
        parsed > static_cast<std::uint64_t>(std::numeric_limits<T>::max())) {
        usage_error(std::string{"invalid value for "} + option + ": " + std::string{text});
    }

    return static_cast<T>(parsed);
}

void print_help()
{
    std::cout
        << "rtxmon - NVIDIA GPU die temperature monitor\n\n"
        << "Usage:\n"
        << "  rtxmon [--once] [--gpu INDEX] [--json]\n"
        << "  rtxmon --watch [--gpu INDEX] [--interval MS] [--count N] [--json]\n"
        << "  rtxmon --list [--json]\n"
        << "  rtxmon --capabilities [--gpu INDEX] [--json]\n\n"
        << "Options:\n"
        << "  --once          Read one sample (default)\n"
        << "  --watch         Read continuously until Ctrl+C\n"
        << "  --list          List NVIDIA GPUs\n"
        << "  --capabilities  Inventory public thermal capabilities and provider states\n"
        << "  --gpu INDEX     Select the zero-based GPU index\n"
        << "  --interval MS   Poll interval, 100 to 60000 ms (default: 1000)\n"
        << "  --count N       Stop watch mode after N samples; 0 means unlimited\n"
        << "  --json          Emit JSON (JSON Lines in watch mode)\n"
        << "  --help          Show this help\n";
}

Options parse_options(int argc, char **argv)
{
    Options options;

    for (int index = 1; index < argc; ++index) {
        const std::string_view argument{argv[index]};

        if (argument == "--help" || argument == "-h") {
            print_help();
            std::exit(0);
        }
        if (argument == "--once") {
            options.mode = Mode::once;
            continue;
        }
        if (argument == "--watch") {
            options.mode = Mode::watch;
            continue;
        }
        if (argument == "--list") {
            options.mode = Mode::list;
            continue;
        }
        if (argument == "--capabilities") {
            options.mode = Mode::capabilities;
            continue;
        }
        if (argument == "--json") {
            options.json = true;
            continue;
        }

        if (argument == "--gpu" || argument == "--interval" || argument == "--count") {
            if (index + 1 >= argc) {
                usage_error(std::string{"missing value for "} + std::string{argument});
            }
            const std::string_view value{argv[++index]};
            if (argument == "--gpu") {
                options.gpu_index = parse_unsigned<std::uint32_t>(value, "--gpu");
            } else if (argument == "--interval") {
                options.interval_ms = parse_unsigned<std::uint32_t>(value, "--interval");
            } else {
                options.count = parse_unsigned<std::uint64_t>(value, "--count");
            }
            continue;
        }

        usage_error("unknown option: " + std::string{argument});
    }

    if (options.interval_ms < 100 || options.interval_ms > 60000) {
        usage_error("--interval must be between 100 and 60000 ms");
    }

    return options;
}

std::string json_escape(std::string_view value)
{
    std::string escaped;
    escaped.reserve(value.size() + 8);

    for (const unsigned char character : value) {
        switch (character) {
        case '"':
            escaped += "\\\"";
            break;
        case '\\':
            escaped += "\\\\";
            break;
        case '\b':
            escaped += "\\b";
            break;
        case '\f':
            escaped += "\\f";
            break;
        case '\n':
            escaped += "\\n";
            break;
        case '\r':
            escaped += "\\r";
            break;
        case '\t':
            escaped += "\\t";
            break;
        default:
            if (character < 0x20U) {
                constexpr char hex[] = "0123456789abcdef";
                escaped += "\\u00";
                escaped += hex[(character >> 4U) & 0x0FU];
                escaped += hex[character & 0x0FU];
            } else {
                escaped += static_cast<char>(character);
            }
        }
    }

    return escaped;
}

std::string iso_utc(std::uint64_t timestamp_ms)
{
    const std::time_t seconds = static_cast<std::time_t>(timestamp_ms / 1000U);
    std::tm utc{};

#if defined(_WIN32)
    if (gmtime_s(&utc, &seconds) != 0) {
        return "invalid-time";
    }
#else
    if (gmtime_r(&seconds, &utc) == nullptr) {
        return "invalid-time";
    }
#endif

    std::ostringstream output;
    output << std::put_time(&utc, "%Y-%m-%dT%H:%M:%S")
           << '.' << std::setw(3) << std::setfill('0') << (timestamp_ms % 1000U) << 'Z';
    return output.str();
}

void print_gpu(const rtxmon::GpuInfo &gpu, bool json, bool last)
{
    if (json) {
        std::cout
            << "  {\"index\":" << gpu.index
            << ",\"name\":\"" << json_escape(gpu.name)
            << "\",\"uuid\":\"" << json_escape(gpu.uuid)
            << "\",\"driver_version\":\"" << json_escape(gpu.driver_version)
            << "\",\"nvml_version\":\"" << json_escape(gpu.nvml_version) << "\"}"
            << (last ? "\n" : ",\n");
        return;
    }

    std::cout << '[' << gpu.index << "] " << gpu.name << " | " << gpu.uuid
              << " | driver " << gpu.driver_version << " | NVML " << gpu.nvml_version << '\n';
}

void print_sample(
    const rtxmon::GpuInfo &gpu,
    const rtxmon::TemperatureSample &sample,
    bool json)
{
    if (json) {
        std::cout
            << "{\"schema_version\":1"
            << ",\"gpu_index\":" << sample.gpu_index
            << ",\"gpu_name\":\"" << json_escape(gpu.name)
            << "\",\"gpu_uuid\":\"" << json_escape(gpu.uuid)
            << "\",\"temperature_c\":" << sample.temperature_c
            << ",\"sensor\":\"gpu_die\""
            << ",\"backend\":\"" << json_escape(rtxmon::backend_name(sample.backend))
            << "\",\"timestamp_unix_ms\":" << sample.timestamp_unix_ms
            << "}\n";
    } else {
        std::cout << iso_utc(sample.timestamp_unix_ms) << " | GPU " << sample.gpu_index
                  << " " << gpu.name << " | die " << sample.temperature_c << " C | "
                  << rtxmon::backend_name(sample.backend) << '\n';
    }

    std::cout.flush();
}

std::string hex_id(std::uint32_t value)
{
    std::ostringstream output;
    output << "0x" << std::hex << std::setw(4) << std::setfill('0') << (value & 0xffffU);
    return output.str();
}

std::string board_profile_key(const rtxmon::BoardIdentity &board)
{
    const bool vbios_valid = (board.flags & RTXMON_BOARD_IDENTITY_VBIOS_VALID) != 0U;
    std::ostringstream output;
    output << std::hex << std::setw(4) << std::setfill('0') << (board.pci_vendor_id & 0xffffU)
           << ':' << std::setw(4) << (board.pci_device_id & 0xffffU)
           << '/' << std::setw(4) << (board.pci_subsystem_vendor_id & 0xffffU)
           << ':' << std::setw(4) << (board.pci_subsystem_device_id & 0xffffU)
           << '@' << (vbios_valid ? board.vbios_version : "unknown");
    return output.str();
}

void print_nullable_temperature(bool valid, std::int32_t value)
{
    if (valid) {
        std::cout << value;
    } else {
        std::cout << "null";
    }
}

void print_capability_report_json(
    const rtxmon::GpuInfo &gpu,
    const rtxmon::BoardIdentity &board,
    const rtxmon::ThermalReport &report)
{
    const bool pci_valid = (board.flags & RTXMON_BOARD_IDENTITY_PCI_VALID) != 0U;
    const bool vbios_valid = (board.flags & RTXMON_BOARD_IDENTITY_VBIOS_VALID) != 0U;

    std::cout
        << "{\"schema_version\":2"
        << ",\"gpu\":{\"index\":" << gpu.index
        << ",\"name\":\"" << json_escape(gpu.name)
        << "\",\"uuid\":\"" << json_escape(gpu.uuid)
        << "\",\"driver_version\":\"" << json_escape(gpu.driver_version)
        << "\",\"nvml_version\":\"" << json_escape(gpu.nvml_version) << "\"}"
        << ",\"board\":{\"pci_identity_available\":" << (pci_valid ? "true" : "false")
        << ",\"pci_bus_id\":\"" << json_escape(board.pci_bus_id)
        << "\",\"pci_vendor_id\":\"" << hex_id(board.pci_vendor_id)
        << "\",\"pci_device_id\":\"" << hex_id(board.pci_device_id)
        << "\",\"pci_subsystem_vendor_id\":\"" << hex_id(board.pci_subsystem_vendor_id)
        << "\",\"pci_subsystem_device_id\":\"" << hex_id(board.pci_subsystem_device_id)
        << "\",\"vbios_available\":" << (vbios_valid ? "true" : "false")
        << ",\"vbios_version\":";
    if (vbios_valid) {
        std::cout << '"' << json_escape(board.vbios_version) << '"';
    } else {
        std::cout << "null";
    }
    std::cout << ",\"profile_key\":\"" << json_escape(board_profile_key(board)) << "\"}"
              << ",\"captured_at_unix_ms\":" << report.timestamp_unix_ms
              << ",\"providers\":[";

    for (std::size_t index = 0; index < report.providers.size(); ++index) {
        const auto &provider = report.providers[index];
        if (index != 0U) {
            std::cout << ',';
        }
        std::cout
            << "{\"provider\":\"" << json_escape(rtxmon::provider_name(provider.provider))
            << "\",\"state\":\"" << json_escape(rtxmon::capability_state_name(provider.state))
            << "\",\"native_status\":" << provider.native_status
            << ",\"capability_count\":" << provider.capability_count << '}';
    }

    std::cout << "],\"thermal_capabilities\":[";
    for (std::size_t index = 0; index < report.capabilities.size(); ++index) {
        const auto &sensor = report.capabilities[index];
        if (index != 0U) {
            std::cout << ',';
        }
        std::cout
            << "{\"provider\":\"" << json_escape(rtxmon::provider_name(sensor.provider))
            << "\",\"provider_native_id\":" << sensor.provider_native_id
            << ",\"target\":\"" << json_escape(rtxmon::thermal_target_name(sensor.target))
            << "\",\"controller\":\"" << json_escape(rtxmon::thermal_controller_name(sensor.controller))
            << "\",\"state\":\"" << json_escape(rtxmon::capability_state_name(sensor.state))
            << "\",\"confidence\":\"" << json_escape(rtxmon::confidence_name(sensor.confidence))
            << "\",\"current_temperature_c\":";
        print_nullable_temperature(sensor.has_current_temperature(), sensor.current_temperature_c);
        std::cout << ",\"default_min_temperature_c\":";
        print_nullable_temperature(sensor.has_default_minimum(), sensor.default_min_temperature_c);
        std::cout << ",\"default_max_temperature_c\":";
        print_nullable_temperature(sensor.has_default_maximum(), sensor.default_max_temperature_c);
        std::cout << ",\"native_status\":" << sensor.native_status << '}';
    }

    std::cout << "]}\n";
}

void print_capability_report_text(
    const rtxmon::GpuInfo &gpu,
    const rtxmon::BoardIdentity &board,
    const rtxmon::ThermalReport &report)
{
    std::cout << "GPU " << gpu.index << "  " << gpu.name << '\n'
              << "Driver " << gpu.driver_version << "  NVML " << gpu.nvml_version << '\n'
              << "PCI " << board.pci_bus_id << "  "
              << hex_id(board.pci_vendor_id) << ':' << hex_id(board.pci_device_id).substr(2)
              << "  subsystem " << hex_id(board.pci_subsystem_vendor_id) << ':'
              << hex_id(board.pci_subsystem_device_id).substr(2) << '\n'
              << "VBIOS "
              << (((board.flags & RTXMON_BOARD_IDENTITY_VBIOS_VALID) != 0U)
                      ? board.vbios_version
                      : "unavailable")
              << "\n\nProviders:\n";

    for (const auto &provider : report.providers) {
        std::cout << "  " << rtxmon::provider_name(provider.provider)
                  << " | " << rtxmon::capability_state_name(provider.state)
                  << " | capabilities " << provider.capability_count
                  << " | native status " << provider.native_status << '\n';
    }

    std::cout << "\nThermal capabilities:\n";
    for (const auto &sensor : report.capabilities) {
        std::cout << "  " << rtxmon::provider_name(sensor.provider)
                  << '[' << sensor.provider_native_id << ']'
                  << " | target " << rtxmon::thermal_target_name(sensor.target)
                  << " | controller " << rtxmon::thermal_controller_name(sensor.controller)
                  << " | " << rtxmon::capability_state_name(sensor.state)
                  << " | " << rtxmon::confidence_name(sensor.confidence);
        if (sensor.has_current_temperature()) {
            std::cout << " | current " << sensor.current_temperature_c << " C";
        }
        if (sensor.has_default_minimum() && sensor.has_default_maximum()) {
            std::cout << " | driver defaults " << sensor.default_min_temperature_c
                      << ".." << sensor.default_max_temperature_c << " C";
        }
        std::cout << " | native status " << sensor.native_status << '\n';
    }

    std::cout
        << "\nOnly public driver-reported channels are listed; unavailable hotspot, memory, or VRM readings are not inferred.\n";
}

int run(const Options &options)
{
    rtxmon::Monitor monitor;

    if (options.mode == Mode::list) {
        const auto gpus = monitor.gpus();
        if (options.json) {
            std::cout << "[\n";
        }
        for (std::size_t index = 0; index < gpus.size(); ++index) {
            print_gpu(gpus[index], options.json, index + 1U == gpus.size());
        }
        if (options.json) {
            std::cout << "]\n";
        }
        return 0;
    }

    const auto gpu = monitor.gpu(options.gpu_index);
    if (options.mode == Mode::capabilities) {
        const auto board = monitor.board_identity(options.gpu_index);
        const auto report = monitor.scan_thermal_capabilities(options.gpu_index);
        if (options.json) {
            print_capability_report_json(gpu, board, report);
        } else {
            print_capability_report_text(gpu, board, report);
        }
        return 0;
    }
    if (options.mode == Mode::once) {
        print_sample(gpu, monitor.read_gpu_die_temperature(options.gpu_index), options.json);
        return 0;
    }

    std::signal(SIGINT, on_interrupt);
    std::uint64_t samples = 0;
    while (running.load() && (options.count == 0 || samples < options.count)) {
        print_sample(gpu, monitor.read_gpu_die_temperature(options.gpu_index), options.json);
        ++samples;
        if (running.load() && (options.count == 0 || samples < options.count)) {
            std::this_thread::sleep_for(std::chrono::milliseconds{options.interval_ms});
        }
    }

    return 0;
}

} // namespace

int main(int argc, char **argv)
{
    try {
        return run(parse_options(argc, argv));
    } catch (const rtxmon::MonitorError &error) {
        std::cerr << "rtxmon: " << error.what() << '\n';
        return 1;
    } catch (const std::exception &error) {
        std::cerr << "rtxmon: " << error.what() << '\n';
        return 2;
    }
}
