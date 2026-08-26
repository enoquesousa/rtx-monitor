#include <rtxmon/lab/vbios_json.hpp>
#include <rtxmon/lab/vbios_parser.hpp>

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <iostream>
#include <limits>
#include <new>
#include <optional>
#include <span>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#endif

namespace {

enum class ReadStatus {
    success,
    open_failed,
    size_failed,
    input_too_large,
    read_failed,
    path_not_local_regular,
    unsupported_platform,
};

struct ReadResult {
    ReadStatus status{ReadStatus::open_failed};
    std::vector<std::byte> bytes;
};

#ifdef _WIN32

// Hard resource boundary for untrusted offline artifacts. PCI option ROMs are
// normally much smaller; 16 MiB leaves ample room for prefixed/multi-image
// captures while preventing an accidental or hostile whole-file allocation.
constexpr std::uintmax_t kMaximumInputBytes = 16U * 1024U * 1024U;

class WindowsHandle final {
public:
    explicit WindowsHandle(HANDLE value) noexcept
        : value_(value)
    {
    }

    WindowsHandle(const WindowsHandle &) = delete;
    WindowsHandle &operator=(const WindowsHandle &) = delete;

    ~WindowsHandle()
    {
        if (value_ != INVALID_HANDLE_VALUE) {
            CloseHandle(value_);
        }
    }

    [[nodiscard]] HANDLE get() const noexcept
    {
        return value_;
    }

private:
    HANDLE value_;
};

[[nodiscard]] bool starts_with_two_separators(std::wstring_view path) noexcept
{
    return path.size() >= 2U &&
        (path[0] == L'\\' || path[0] == L'/') &&
        (path[1] == L'\\' || path[1] == L'/');
}

[[nodiscard]] bool uses_remote_drive(std::wstring_view path) noexcept
{
    if (path.size() < 3U || path[1] != L':' ||
        (path[2] != L'\\' && path[2] != L'/')) {
        return true;
    }

    const wchar_t root[] = {path[0], L':', L'\\', L'\0'};
    const auto type = GetDriveTypeW(root);
    return type == DRIVE_UNKNOWN || type == DRIVE_NO_ROOT_DIR || type == DRIVE_REMOTE;
}

[[nodiscard]] bool contains_alternate_stream(std::wstring_view path) noexcept
{
    return path.find(L':', 2U) != std::wstring_view::npos;
}

[[nodiscard]] bool has_reparse_component(const std::wstring &path) noexcept
{
    if (path.size() < 3U) {
        return true;
    }

    for (std::size_t index = 3U; index <= path.size(); ++index) {
        if (index != path.size() && path[index] != L'\\' && path[index] != L'/') {
            continue;
        }
        if (index == 3U) {
            continue;
        }

        const auto component_path = path.substr(0U, index);
        const auto attributes = GetFileAttributesW(component_path.c_str());
        if (attributes != INVALID_FILE_ATTRIBUTES &&
            (attributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0U) {
            return true;
        }
    }
    return false;
}

[[nodiscard]] bool handle_resolves_to_remote_path(HANDLE handle)
{
    constexpr std::wstring_view unc_prefix{L"\\\\?\\UNC\\"};
    constexpr std::wstring_view extended_prefix{L"\\\\?\\"};

    const auto required = GetFinalPathNameByHandleW(
        handle,
        nullptr,
        0U,
        FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
    if (required == 0U) {
        return true;
    }
    std::vector<wchar_t> buffer(static_cast<std::size_t>(required) + 1U);
    const auto copied = GetFinalPathNameByHandleW(
        handle,
        buffer.data(),
        static_cast<DWORD>(buffer.size()),
        FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
    if (copied == 0U || copied >= buffer.size()) {
        return true;
    }

    const std::wstring_view resolved{buffer.data(), copied};
    if (resolved.starts_with(unc_prefix)) {
        return true;
    }
    if (resolved.starts_with(extended_prefix) && resolved.size() >= 7U &&
        resolved[5U] == L':') {
        return uses_remote_drive(resolved.substr(4U));
    }
    return false;
}

[[nodiscard]] std::optional<std::wstring> absolute_windows_path(const char *path)
{
    std::wstring supplied;
    try {
        supplied = std::filesystem::path{path}.wstring();
    } catch (const std::filesystem::filesystem_error &) {
        return std::nullopt;
    }
    if (supplied.empty() || starts_with_two_separators(supplied)) {
        return std::nullopt;
    }

    const auto required = GetFullPathNameW(supplied.c_str(), 0U, nullptr, nullptr);
    if (required == 0U) {
        return std::nullopt;
    }
    std::vector<wchar_t> buffer(static_cast<std::size_t>(required));
    const auto copied = GetFullPathNameW(
        supplied.c_str(),
        required,
        buffer.data(),
        nullptr);
    if (copied == 0U || copied >= required) {
        return std::nullopt;
    }
    return std::wstring{buffer.data(), copied};
}

ReadResult read_binary_file(const char *path)
{
    const auto absolute_path = absolute_windows_path(path);
    if (!absolute_path.has_value() || starts_with_two_separators(*absolute_path) ||
        uses_remote_drive(*absolute_path) || contains_alternate_stream(*absolute_path) ||
        has_reparse_component(*absolute_path)) {
        return ReadResult{ReadStatus::path_not_local_regular, {}};
    }

    const WindowsHandle input{CreateFileW(
        absolute_path->c_str(),
        GENERIC_READ,
        FILE_SHARE_READ,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_SEQUENTIAL_SCAN,
        nullptr)};
    if (input.get() == INVALID_HANDLE_VALUE) {
        return ReadResult{ReadStatus::open_failed, {}};
    }

    FILE_ATTRIBUTE_TAG_INFO attributes{};
    FILE_STANDARD_INFO standard{};
    if (GetFileType(input.get()) != FILE_TYPE_DISK ||
        !GetFileInformationByHandleEx(
            input.get(), FileAttributeTagInfo, &attributes, sizeof(attributes)) ||
        !GetFileInformationByHandleEx(
            input.get(), FileStandardInfo, &standard, sizeof(standard)) ||
        standard.Directory != FALSE ||
        (attributes.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0U ||
        handle_resolves_to_remote_path(input.get())) {
        return ReadResult{ReadStatus::path_not_local_regular, {}};
    }
    if (standard.EndOfFile.QuadPart < 0) {
        return ReadResult{ReadStatus::size_failed, {}};
    }

    const auto unsigned_size = static_cast<std::uintmax_t>(standard.EndOfFile.QuadPart);
    if (unsigned_size > kMaximumInputBytes ||
        unsigned_size > std::numeric_limits<std::size_t>::max()) {
        return ReadResult{ReadStatus::input_too_large, {}};
    }

    const auto size = static_cast<std::size_t>(unsigned_size);
    std::vector<std::byte> bytes(size);
    std::size_t total_read = 0U;
    while (total_read < size) {
        const auto remaining = size - total_read;
        const auto request = static_cast<DWORD>((std::min)(
            remaining,
            static_cast<std::size_t>(std::numeric_limits<DWORD>::max())));
        DWORD bytes_read = 0U;
        if (!ReadFile(
                input.get(),
                bytes.data() + total_read,
                request,
                &bytes_read,
                nullptr) ||
            bytes_read == 0U) {
            return ReadResult{ReadStatus::read_failed, {}};
        }
        total_read += static_cast<std::size_t>(bytes_read);
    }

    FILE_STANDARD_INFO final_standard{};
    if (!GetFileInformationByHandleEx(
            input.get(), FileStandardInfo, &final_standard, sizeof(final_standard)) ||
        final_standard.EndOfFile.QuadPart != standard.EndOfFile.QuadPart) {
        return ReadResult{ReadStatus::read_failed, {}};
    }
    return ReadResult{ReadStatus::success, std::move(bytes)};
}

#else

ReadResult read_binary_file(const char *path)
{
    // v0.8 deliberately has no Unix path-ingestion backend. In particular,
    // never probe an operator-controlled path that could name sysfs or a
    // device; the pure byte-span parser remains portable and separately usable.
    (void)path;
    return ReadResult{ReadStatus::unsupported_platform, {}};
}

#endif

std::string_view read_status_name(ReadStatus status) noexcept
{
    switch (status) {
    case ReadStatus::success:
        return "success";
    case ReadStatus::open_failed:
        return "open_failed";
    case ReadStatus::size_failed:
        return "size_failed";
    case ReadStatus::input_too_large:
        return "input_too_large";
    case ReadStatus::read_failed:
        return "read_failed";
    case ReadStatus::path_not_local_regular:
        return "path_not_local_regular";
    case ReadStatus::unsupported_platform:
        return "unsupported_platform";
    }
    return "unknown";
}

void write_read_error_json(ReadStatus status)
{
    std::cout << "{\n"
              << "  \"schema_version\": 1,\n"
              << "  \"status\": \"io_error\",\n"
              << "  \"rom\": null,\n"
              << "  \"bit\": null,\n"
              << "  \"diagnostics\": [\n"
              << "    {\n"
              << "      \"severity\": \"error\",\n"
              << "      \"code\": \"" << read_status_name(status) << "\",\n"
              << "      \"offset\": 0,\n"
              << "      \"expected\": null,\n"
              << "      \"actual\": null\n"
              << "    }\n"
              << "  ]\n"
              << "}\n";
}

void print_help()
{
    std::cout
        << "Usage: rtxmon-vbios <firmware.rom>\n"
        << "\n"
        << "On Windows, parse an operator-supplied PCI expansion ROM path and emit JSON.\n"
        << "Remote, device, alternate-stream, and reparse paths are rejected.\n"
        << "On other platforms, v0.8 returns unsupported_platform before path access.\n"
        << "This command performs no GPU, driver, PCI, MMIO, or I2C access.\n"
        << "\n"
        << "Options:\n"
        << "  -h, --help  Show this help text.\n";
}

} // namespace

int main(int argc, char **argv)
{
    if (argc == 2) {
        const std::string_view argument{argv[1]};
        if (argument == "--help" || argument == "-h") {
            print_help();
            std::cout.flush();
            return std::cout ? 0 : 3;
        }
    }
    if (argc != 2) {
        std::cerr << "Expected exactly one firmware path. Use --help for usage.\n";
        return 2;
    }

    try {
        auto input = read_binary_file(argv[1]);
        if (input.status != ReadStatus::success) {
            write_read_error_json(input.status);
            std::cout.flush();
            return 3;
        }

        const auto result = rtxmon::lab::parse_vbios(
            std::span<const std::byte>{input.bytes.data(), input.bytes.size()});
        rtxmon::lab::write_vbios_json(std::cout, result);
        std::cout.flush();
        if (!std::cout) {
            std::cerr << "Failed to write the complete VBIOS JSON result.\n";
            return 3;
        }
        return result.has_valid_rom() ? 0 : 1;
    } catch (const std::bad_alloc &) {
        write_read_error_json(ReadStatus::input_too_large);
        return 3;
    }
}
