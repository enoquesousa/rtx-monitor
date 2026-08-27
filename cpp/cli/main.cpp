#include <rtxmon/alerts.hpp>
#include <rtxmon/metrics.hpp>
#include <rtxmon/monitor.hpp>
#include <rtxmon/sampler.hpp>

#include <algorithm>
#include <atomic>
#include <charconv>
#include <chrono>
#include <csignal>
#include <cstdint>
#include <cstdlib>
#include <iomanip>
#include <iostream>
#include <limits>
#include <optional>
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
    telemetry,
};

struct Options {
    Mode mode{Mode::once};
    std::uint32_t gpu_index{0};
    bool gpu_index_set{false};
    std::string gpu_uuid;
    std::uint32_t interval_ms{1000};
    std::uint64_t count{0};
    std::size_t buffer_capacity{256U};
    bool json{false};
    bool events{false};
    bool alert_enabled{false};
    bool alert_hysteresis_set{false};
    std::int32_t alert_threshold_c{0};
    std::int32_t alert_hysteresis_c{0};
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
        << "  rtxmon [--once] [--gpu INDEX | --gpu-uuid UUID] [--json]\n"
        << "  rtxmon --watch [--gpu INDEX | --gpu-uuid UUID] [--interval MS] [--count N] [--json]\n"
        << "  rtxmon --watch --events [--gpu INDEX | --gpu-uuid UUID] [--interval MS] [--count N]\n"
        << "  rtxmon --watch --alert-threshold C [--alert-hysteresis C] [--events]\n"
        << "  rtxmon --list [--json]\n"
        << "  rtxmon --capabilities [--gpu INDEX | --gpu-uuid UUID] [--json]\n\n"
        << "  rtxmon --telemetry [--gpu INDEX | --gpu-uuid UUID] [--json]\n\n"
        << "Options:\n"
        << "  --once          Read one sample (default)\n"
        << "  --watch         Read continuously until Ctrl+C\n"
        << "  --list          List NVIDIA GPUs\n"
        << "  --capabilities  Inventory public thermal capabilities and provider states\n"
        << "  --telemetry     Read the documented telemetry catalog and computed metrics\n"
        << "  --gpu INDEX     Select the zero-based GPU index\n"
        << "  --gpu-uuid UUID Select a stable GPU UUID; mutually exclusive with --gpu\n"
        << "  --interval MS   Poll interval, 100 to 60000 ms (default: 1000)\n"
        << "  --count N       Stop watch mode after N samples; 0 means unlimited\n"
        << "  --buffer N      Retain the most recent 1 to 65536 events (default: 256)\n"
        << "  --json          Emit JSON (sample schema v1 in watch mode)\n"
        << "  --events        Emit the full event stream (schema v4) as JSON Lines\n"
        << "  --alert-threshold C   Raise an alert while --watch when die temperature reaches C (0-500)\n"
        << "  --alert-hysteresis C  Clear at threshold-C; 0 clears only below threshold\n"
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
        if (argument == "--telemetry") {
            options.mode = Mode::telemetry;
            continue;
        }
        if (argument == "--json") {
            options.json = true;
            continue;
        }
        if (argument == "--events") {
            options.events = true;
            continue;
        }

        if (argument == "--gpu" || argument == "--gpu-uuid" ||
            argument == "--interval" || argument == "--count" ||
            argument == "--buffer" || argument == "--alert-threshold" ||
            argument == "--alert-hysteresis") {
            if (index + 1 >= argc) {
                usage_error(std::string{"missing value for "} + std::string{argument});
            }
            const std::string_view value{argv[++index]};
            if (argument == "--gpu") {
                options.gpu_index = parse_unsigned<std::uint32_t>(value, "--gpu");
                options.gpu_index_set = true;
            } else if (argument == "--gpu-uuid") {
                if (value.empty()) {
                    usage_error("--gpu-uuid must not be empty");
                }
                options.gpu_uuid = value;
            } else if (argument == "--interval") {
                options.interval_ms = parse_unsigned<std::uint32_t>(value, "--interval");
            } else if (argument == "--count") {
                options.count = parse_unsigned<std::uint64_t>(value, "--count");
            } else if (argument == "--buffer") {
                options.buffer_capacity = parse_unsigned<std::size_t>(value, "--buffer");
            } else if (argument == "--alert-threshold") {
                options.alert_threshold_c =
                    static_cast<std::int32_t>(parse_unsigned<std::uint32_t>(value, "--alert-threshold"));
                options.alert_enabled = true;
            } else {
                options.alert_hysteresis_c =
                    static_cast<std::int32_t>(parse_unsigned<std::uint32_t>(value, "--alert-hysteresis"));
                options.alert_hysteresis_set = true;
            }
            continue;
        }

        usage_error("unknown option: " + std::string{argument});
    }

    if (options.interval_ms < 100 || options.interval_ms > 60000) {
        usage_error("--interval must be between 100 and 60000 ms");
    }
    if (options.buffer_capacity == 0U || options.buffer_capacity > 65536U) {
        usage_error("--buffer must be between 1 and 65536 events");
    }
    if (options.gpu_index_set && !options.gpu_uuid.empty()) {
        usage_error("--gpu and --gpu-uuid are mutually exclusive");
    }
    if (options.events && options.mode != Mode::watch) {
        usage_error("--events requires --watch");
    }
    if (options.alert_enabled && options.mode != Mode::watch) {
        usage_error("--alert-threshold requires --watch");
    }
    if (!options.alert_enabled && options.alert_hysteresis_set) {
        usage_error("--alert-hysteresis requires --alert-threshold");
    }
    if (options.alert_threshold_c < 0 || options.alert_threshold_c > 500) {
        usage_error("--alert-threshold must be between 0 and 500 C");
    }
    if (options.alert_hysteresis_c < 0 || options.alert_hysteresis_c > options.alert_threshold_c) {
        usage_error("--alert-hysteresis must be between 0 and the threshold");
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

rtxmon::GpuInfo resolve_gpu(const rtxmon::Monitor &monitor, const Options &options)
{
    return options.gpu_uuid.empty()
        ? monitor.gpu(options.gpu_index)
        : monitor.gpu_by_uuid(options.gpu_uuid);
}

void print_nullable_int(std::optional<std::int32_t> value)
{
    if (value.has_value()) {
        std::cout << *value;
    } else {
        std::cout << "null";
    }
}

void print_event_public_telemetry_json(const rtxmon::PublicTelemetryReport &telemetry);
void print_event_computed_metrics_json(const rtxmon::ComputedMetricsReport &computed);

void print_event_json(const rtxmon::TelemetryEvent &event)
{
    std::cout
        << "{\"schema_version\":4"
        << ",\"event_type\":\"" << rtxmon::telemetry_event_kind_name(event.kind)
        << "\",\"sequence\":" << event.sequence
        << ",\"target_gpu_uuid\":\"" << json_escape(event.target_gpu_uuid)
        << "\",\"gpu_index\":";
    if (event.gpu.has_value()) {
        std::cout << event.gpu->index;
    } else {
        std::cout << "null";
    }
    std::cout << ",\"gpu_name\":";
    if (event.gpu.has_value()) {
        std::cout << '"' << json_escape(event.gpu->name) << '"';
    } else {
        std::cout << "null";
    }
    std::cout
        << ",\"observed_at_unix_ms\":" << event.observed_at_unix_ms
        << ",\"status\":\"" << json_escape(rtxmon_status_string(event.status))
        << "\",\"status_code\":" << static_cast<std::int32_t>(event.status)
        << ",\"message\":\"" << json_escape(event.message)
        << "\",\"consecutive_failures\":" << event.consecutive_failures
        << ",\"retry_after_ms\":" << event.retry_after_ms
        << ",\"sample\":";
    if (event.sample.has_value()) {
        std::cout
            << "{\"temperature_c\":" << event.sample->temperature_c
            << ",\"sensor\":\"gpu_die\""
            << ",\"backend\":\""
            << json_escape(rtxmon::backend_name(event.sample->backend))
            << "\",\"timestamp_unix_ms\":" << event.sample->timestamp_unix_ms
            << '}';
    } else {
        std::cout << "null";
    }
    std::cout << ",\"alert_threshold_c\":";
    print_nullable_int(event.alert_threshold_c);
    std::cout << ",\"alert_hysteresis_c\":";
    print_nullable_int(event.alert_hysteresis_c);
    std::cout << ",\"public_telemetry\":";
    if (event.public_telemetry.has_value()) {
        print_event_public_telemetry_json(*event.public_telemetry);
    } else {
        std::cout << "null";
    }
    std::cout << ",\"computed_metrics\":";
    if (event.computed_metrics.has_value()) {
        print_event_computed_metrics_json(*event.computed_metrics);
    } else {
        std::cout << "null";
    }
    std::cout << ",\"windows_telemetry\":null";
    std::cout << "}\n";
    std::cout.flush();
}

void print_event_text(const rtxmon::TelemetryEvent &event)
{
    if (event.kind == rtxmon::TelemetryEventKind::sample &&
        event.gpu.has_value() && event.sample.has_value()) {
        print_sample(*event.gpu, *event.sample, false);
        return;
    }

    if (event.kind == rtxmon::TelemetryEventKind::gap) {
        std::cerr << iso_utc(event.observed_at_unix_ms)
                  << " | GPU " << event.target_gpu_uuid
                  << " | gap | " << rtxmon_status_string(event.status)
                  << " | retry in " << event.retry_after_ms << " ms"
                  << " | " << event.message << '\n';
        return;
    }

    if (event.kind == rtxmon::TelemetryEventKind::recovered) {
        std::cerr << iso_utc(event.observed_at_unix_ms)
                  << " | GPU " << event.target_gpu_uuid
                  << " | monitoring recovered after " << event.consecutive_failures
                  << (event.consecutive_failures == 1U ? " failure" : " failures")
                  << '\n';
        return;
    }

    std::cerr << iso_utc(event.observed_at_unix_ms)
              << " | GPU " << event.target_gpu_uuid
              << " | " << rtxmon::telemetry_event_kind_name(event.kind)
              << " | " << event.message << '\n';
}

void print_watch_event(const rtxmon::TelemetryEvent &event, const Options &options)
{
    if (options.events) {
        print_event_json(event);
        return;
    }

    if (event.kind == rtxmon::TelemetryEventKind::sample &&
        event.gpu.has_value() && event.sample.has_value()) {
        print_sample(*event.gpu, *event.sample, options.json);
        return;
    }

    print_event_text(event);
}

void interruptible_sleep(std::uint32_t delay_ms)
{
    constexpr std::uint32_t slice_ms = 50U;
    std::uint32_t remaining = delay_ms;
    while (running.load() && remaining > 0U) {
        const auto current = std::min(remaining, slice_ms);
        std::this_thread::sleep_for(std::chrono::milliseconds{current});
        remaining -= current;
    }
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

void print_public_value_json(const rtxmon::PublicFieldValue &field)
{
    std::cout << "\"value_u64\":";
    if (field.unsigned_value.has_value()) {
        std::cout << *field.unsigned_value;
    } else {
        std::cout << "null";
    }
    std::cout << ",\"value_i64\":";
    if (field.signed_value.has_value()) {
        std::cout << *field.signed_value;
    } else {
        std::cout << "null";
    }
    std::cout << ",\"value_f64\":";
    if (field.double_value.has_value()) {
        std::cout << std::setprecision(12) << *field.double_value;
    } else {
        std::cout << "null";
    }
}

void print_performance_limit_reasons_json(const rtxmon::PublicTelemetryReport &telemetry)
{
    const auto current = std::find_if(
        telemetry.fields.begin(),
        telemetry.fields.end(),
        [](const rtxmon::PublicFieldValue &field) {
            return field.field == RTXMON_PUBLIC_FIELD_CLOCK_EVENT_REASONS_CURRENT;
        });
    if (current == telemetry.fields.end() ||
        current->state != RTXMON_CAPABILITY_AVAILABLE ||
        !current->unsigned_value.has_value()) {
        std::cout << "null";
        return;
    }

    struct Reason {
        std::uint64_t mask;
        const char *name;
        const char *primary;
    };
    static constexpr Reason reasons[] = {
        {1ULL << 0U, "gpu_idle", "idle"},
        {1ULL << 1U, "application_clocks", "application_clocks"},
        {1ULL << 2U, "software_power_cap", "power"},
        {1ULL << 3U, "hardware_slowdown", "hardware_slowdown"},
        {1ULL << 4U, "sync_boost", "sync_boost"},
        {1ULL << 5U, "software_thermal", "thermal"},
        {1ULL << 6U, "hardware_thermal", "thermal"},
        {1ULL << 7U, "hardware_power_brake", "power_brake"},
        {1ULL << 8U, "display_clock", "display_clock"},
    };

    const std::uint64_t raw = *current->unsigned_value;
    const char *primary = raw == 0U ? "none" : "unknown";
    bool first = true;
    std::cout << "{\"raw_bitmask\":" << raw << ",\"active_reasons\":[";
    for (const auto &reason : reasons) {
        if ((raw & reason.mask) == 0U) {
            continue;
        }
        if (first) {
            primary = reason.primary;
            first = false;
        } else {
            std::cout << ',';
        }
        std::cout << '"' << reason.name << '"';
    }
    std::cout << "],\"primary_reason\":\"" << primary << "\"}";
}

void print_event_public_telemetry_json(const rtxmon::PublicTelemetryReport &telemetry)
{
    std::size_t available = 0U;
    std::size_t not_supported = 0U;
    std::size_t provider_unavailable = 0U;
    std::size_t query_failed = 0U;
    for (const auto &field : telemetry.fields) {
        switch (field.state) {
        case RTXMON_CAPABILITY_AVAILABLE:
            ++available;
            break;
        case RTXMON_CAPABILITY_NOT_SUPPORTED:
            ++not_supported;
            break;
        case RTXMON_CAPABILITY_PROVIDER_UNAVAILABLE:
            ++provider_unavailable;
            break;
        case RTXMON_CAPABILITY_QUERY_FAILED:
            ++query_failed;
            break;
        default:
            break;
        }
    }

    std::cout
        << "{\"gpu_index\":" << telemetry.gpu_index
        << ",\"captured_at_unix_ms\":" << telemetry.timestamp_unix_ms
        << ",\"coverage\":{\"total\":" << telemetry.fields.size()
        << ",\"available\":" << available
        << ",\"not_supported\":" << not_supported
        << ",\"provider_unavailable\":" << provider_unavailable
        << ",\"query_failed\":" << query_failed << "}"
        << ",\"performance_limit_reasons\":";
    print_performance_limit_reasons_json(telemetry);
    std::cout << ",\"fields\":[";
    for (std::size_t index = 0U; index < telemetry.fields.size(); ++index) {
        const auto &field = telemetry.fields[index];
        if (index != 0U) {
            std::cout << ',';
        }
        std::cout
            << "{\"field\":\"" << json_escape(rtxmon::public_field_name(field.field))
            << "\",\"provider\":\"" << json_escape(rtxmon::public_provider_name(field.provider))
            << "\",\"provider_native_id\":" << field.provider_native_id
            << ",\"state\":\"" << json_escape(rtxmon::capability_state_name(field.state))
            << "\",\"origin\":\"" << json_escape(rtxmon::origin_name(field.origin))
            << "\",\"value_type\":\"" << json_escape(rtxmon::value_type_name(field.value_type))
            << "\",\"unit\":\"" << json_escape(rtxmon::unit_name(field.unit)) << "\",";
        print_public_value_json(field);
        std::cout
            << ",\"native_status\":" << field.native_status
            << ",\"timestamp_unix_ms\":" << field.timestamp_unix_ms << '}';
    }
    std::cout << "]}";
}

void print_event_computed_metrics_json(const rtxmon::ComputedMetricsReport &computed)
{
    std::cout
        << "{\"gpu_index\":" << computed.gpu_index
        << ",\"timestamp_unix_ms\":" << computed.timestamp_unix_ms
        << ",\"metrics\":[";
    for (std::size_t index = 0U; index < computed.metrics.size(); ++index) {
        const auto &metric = computed.metrics[index];
        if (index != 0U) {
            std::cout << ',';
        }
        std::cout
            << "{\"metric\":\"" << json_escape(rtxmon::computed_metric_name(metric.metric))
            << "\",\"state\":\"" << json_escape(rtxmon::metric_state_name(metric.state))
            << "\",\"origin\":\"" << json_escape(rtxmon::origin_name(metric.origin))
            << "\",\"unit\":\"" << json_escape(rtxmon::unit_name(metric.unit))
            << "\",\"formula\":\"" << json_escape(rtxmon::computed_metric_formula(metric.metric))
            << "\",\"value\":";
        if (metric.value.has_value()) {
            std::cout << std::setprecision(12) << *metric.value;
        } else {
            std::cout << "null";
        }
        std::cout
            << ",\"window_ms\":" << metric.window_ms
            << ",\"sample_count\":" << metric.sample_count
            << ",\"temperature_threshold_c\":";
        if (metric.temperature_threshold_c.has_value()) {
            std::cout << *metric.temperature_threshold_c;
        } else {
            std::cout << "null";
        }
        std::cout << ",\"inputs\":[";
        for (std::size_t input = 0U; input < metric.inputs.size(); ++input) {
            if (input != 0U) {
                std::cout << ',';
            }
            std::cout << '"' << json_escape(rtxmon::public_field_name(metric.inputs[input])) << '"';
        }
        std::cout << "]}";
    }
    std::cout << "]}";
}

void print_public_telemetry_json(
    const rtxmon::GpuInfo &gpu,
    const rtxmon::BoardIdentity &board,
    const rtxmon::PublicTelemetryReport &telemetry,
    const rtxmon::ComputedMetricsReport &computed)
{
    std::size_t available = 0U;
    std::size_t not_supported = 0U;
    std::size_t provider_unavailable = 0U;
    std::size_t query_failed = 0U;
    for (const auto &field : telemetry.fields) {
        switch (field.state) {
        case RTXMON_CAPABILITY_AVAILABLE:
            ++available;
            break;
        case RTXMON_CAPABILITY_NOT_SUPPORTED:
            ++not_supported;
            break;
        case RTXMON_CAPABILITY_PROVIDER_UNAVAILABLE:
            ++provider_unavailable;
            break;
        case RTXMON_CAPABILITY_QUERY_FAILED:
            ++query_failed;
            break;
        default:
            break;
        }
    }

    std::cout
        << "{\"schema_version\":2"
        << ",\"gpu\":{\"index\":" << gpu.index
        << ",\"name\":\"" << json_escape(gpu.name)
        << "\",\"uuid\":\"" << json_escape(gpu.uuid)
        << "\",\"driver_version\":\"" << json_escape(gpu.driver_version)
        << "\",\"nvml_version\":\"" << json_escape(gpu.nvml_version) << "\"}"
        << ",\"profile_key\":\"" << json_escape(board_profile_key(board)) << "\""
        << ",\"captured_at_unix_ms\":" << telemetry.timestamp_unix_ms
        << ",\"coverage\":{\"total\":" << telemetry.fields.size()
        << ",\"available\":" << available
        << ",\"not_supported\":" << not_supported
        << ",\"provider_unavailable\":" << provider_unavailable
        << ",\"query_failed\":" << query_failed << "}"
        << ",\"performance_limit_reasons\":";
    print_performance_limit_reasons_json(telemetry);
    std::cout << ",\"fields\":[";

    for (std::size_t index = 0U; index < telemetry.fields.size(); ++index) {
        const auto &field = telemetry.fields[index];
        if (index != 0U) {
            std::cout << ',';
        }
        std::cout
            << "{\"field\":\"" << json_escape(rtxmon::public_field_name(field.field))
            << "\",\"provider\":\"" << json_escape(rtxmon::public_provider_name(field.provider))
            << "\",\"provider_native_id\":" << field.provider_native_id
            << ",\"state\":\"" << json_escape(rtxmon::capability_state_name(field.state))
            << "\",\"origin\":\"" << json_escape(rtxmon::origin_name(field.origin))
            << "\",\"value_type\":\"" << json_escape(rtxmon::value_type_name(field.value_type))
            << "\",\"unit\":\"" << json_escape(rtxmon::unit_name(field.unit)) << "\",";
        print_public_value_json(field);
        std::cout
            << ",\"native_status\":" << field.native_status
            << ",\"timestamp_unix_ms\":" << field.timestamp_unix_ms << '}';
    }

    std::cout << "],\"computed_metrics\":[";
    for (std::size_t index = 0U; index < computed.metrics.size(); ++index) {
        const auto &metric = computed.metrics[index];
        if (index != 0U) {
            std::cout << ',';
        }
        std::cout
            << "{\"metric\":\"" << json_escape(rtxmon::computed_metric_name(metric.metric))
            << "\",\"state\":\"" << json_escape(rtxmon::metric_state_name(metric.state))
            << "\",\"origin\":\"" << json_escape(rtxmon::origin_name(metric.origin))
            << "\",\"unit\":\"" << json_escape(rtxmon::unit_name(metric.unit))
            << "\",\"formula\":\"" << json_escape(rtxmon::computed_metric_formula(metric.metric))
            << "\",\"value\":";
        if (metric.value.has_value()) {
            std::cout << std::setprecision(12) << *metric.value;
        } else {
            std::cout << "null";
        }
        std::cout
            << ",\"window_ms\":" << metric.window_ms
            << ",\"sample_count\":" << metric.sample_count
            << ",\"temperature_threshold_c\":";
        if (metric.temperature_threshold_c.has_value()) {
            std::cout << *metric.temperature_threshold_c;
        } else {
            std::cout << "null";
        }
        std::cout << ",\"inputs\":[";
        for (std::size_t input = 0U; input < metric.inputs.size(); ++input) {
            if (input != 0U) {
                std::cout << ',';
            }
            std::cout << '"' << json_escape(rtxmon::public_field_name(metric.inputs[input])) << '"';
        }
        std::cout << "]}";
    }
    std::cout << "]}\n";
}

void print_public_telemetry_text(
    const rtxmon::GpuInfo &gpu,
    const rtxmon::BoardIdentity &board,
    const rtxmon::PublicTelemetryReport &telemetry,
    const rtxmon::ComputedMetricsReport &computed)
{
    std::cout << "GPU " << gpu.index << "  " << gpu.name << '\n'
              << "Profile " << board_profile_key(board) << '\n'
              << "Captured " << iso_utc(telemetry.timestamp_unix_ms) << "\n\nDocumented fields:\n";

    for (const auto &field : telemetry.fields) {
        std::cout << "  " << rtxmon::public_field_name(field.field)
                  << " | " << rtxmon::capability_state_name(field.state)
                  << " | " << rtxmon::public_provider_name(field.provider)
                  << '[' << field.provider_native_id << ']';
        if (field.unsigned_value.has_value()) {
            std::cout << " | " << *field.unsigned_value;
        } else if (field.signed_value.has_value()) {
            std::cout << " | " << *field.signed_value;
        } else if (field.double_value.has_value()) {
            std::cout << " | " << std::setprecision(12) << *field.double_value;
        }
        std::cout << " " << rtxmon::unit_name(field.unit)
                  << " | native status " << field.native_status << '\n';
    }

    std::cout << "\nComputed metrics:\n";
    for (const auto &metric : computed.metrics) {
        std::cout << "  " << rtxmon::computed_metric_name(metric.metric)
                  << " | " << rtxmon::metric_state_name(metric.state);
        if (metric.value.has_value()) {
            std::cout << " | " << std::setprecision(12) << *metric.value
                      << ' ' << rtxmon::unit_name(metric.unit);
        }
        std::cout << " | window " << metric.window_ms << " ms"
                  << " | samples " << metric.sample_count
                  << " | " << rtxmon::computed_metric_formula(metric.metric) << '\n';
    }
}

std::string alert_message(
    rtxmon::TelemetryEventKind kind,
    std::int32_t temperature_c,
    const Options &options)
{
    std::ostringstream message;
    if (kind == rtxmon::TelemetryEventKind::alert_raised) {
        message << "die temperature " << temperature_c << " C reached alert threshold "
                 << options.alert_threshold_c << " C";
    } else {
        message << "die temperature " << temperature_c << " C cleared alert threshold "
                 << options.alert_threshold_c << " C (hysteresis " << options.alert_hysteresis_c
                 << " C)";
    }
    return message.str();
}

int run_watch(const Options &options)
{
    std::string target_uuid = options.gpu_uuid;
    if (target_uuid.empty()) {
        const rtxmon::Monitor monitor;
        target_uuid = resolve_gpu(monitor, options).uuid;
    }

    rtxmon::ResilientSampler sampler{
        target_uuid,
        rtxmon::SamplerOptions{options.buffer_capacity, 250U, 5000U}};

    std::optional<rtxmon::AlertEvaluator> alert_evaluator;
    if (options.alert_enabled) {
        alert_evaluator.emplace(
            rtxmon::AlertOptions{options.alert_threshold_c, options.alert_hysteresis_c});
    }
    std::uint64_t stream_sequence = 1U;

    running.store(true);
    std::signal(SIGINT, on_interrupt);
    std::uint64_t samples = 0U;
    while (running.load() && (options.count == 0U || samples < options.count)) {
        const auto events = sampler.poll();
        for (auto event : events) {
            event.sequence = stream_sequence++;
            print_watch_event(event, options);
            if (event.kind == rtxmon::TelemetryEventKind::sample) {
                ++samples;

                if (alert_evaluator.has_value() && event.sample.has_value()) {
                    const auto alert_kind = alert_evaluator->observe(event.sample->temperature_c);
                    if (alert_kind.has_value()) {
                        auto alert_event = event;
                        alert_event.sequence = stream_sequence++;
                        alert_event.kind = *alert_kind;
                        alert_event.alert_threshold_c = options.alert_threshold_c;
                        alert_event.alert_hysteresis_c = options.alert_hysteresis_c;
                        alert_event.public_telemetry.reset();
                        alert_event.computed_metrics.reset();
                        alert_event.message =
                            alert_message(*alert_kind, event.sample->temperature_c, options);
                        print_watch_event(alert_event, options);
                    }
                }
            }
        }

        if (running.load() && (options.count == 0U || samples < options.count)) {
            interruptible_sleep(sampler.next_delay_ms(options.interval_ms));
        }
    }

    return 0;
}

int run(const Options &options)
{
    if (options.mode == Mode::watch) {
        return run_watch(options);
    }

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

    const auto gpu = resolve_gpu(monitor, options);
    if (options.mode == Mode::capabilities) {
        const auto board = monitor.board_identity(gpu.index);
        const auto report = monitor.scan_thermal_capabilities(gpu.index);
        if (options.json) {
            print_capability_report_json(gpu, board, report);
        } else {
            print_capability_report_text(gpu, board, report);
        }
        return 0;
    }
    if (options.mode == Mode::telemetry) {
        const auto board = monitor.board_identity(gpu.index);
        const auto telemetry = monitor.read_public_telemetry(gpu.index);
        rtxmon::MetricsEngine metrics;
        const auto computed = metrics.observe(telemetry);
        if (options.json) {
            print_public_telemetry_json(gpu, board, telemetry, computed);
        } else {
            print_public_telemetry_text(gpu, board, telemetry, computed);
        }
        return 0;
    }
    if (options.mode == Mode::once) {
        print_sample(gpu, monitor.read_gpu_die_temperature(gpu.index), options.json);
        return 0;
    }

    throw std::logic_error("unhandled run mode");
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
