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
        << "  rtxmon --list [--json]\n\n"
        << "Options:\n"
        << "  --once          Read one sample (default)\n"
        << "  --watch         Read continuously until Ctrl+C\n"
        << "  --list          List NVIDIA GPUs\n"
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
