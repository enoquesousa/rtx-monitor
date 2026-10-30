#include <rtxmon/lab/vbios_json.hpp>

#include <cstddef>
#include <cstdint>
#include <ios>
#include <locale>
#include <ostream>
#include <optional>

namespace rtxmon::lab {
namespace {

class JsonStreamState final {
public:
    explicit JsonStreamState(std::ostream &output)
        : output_(output),
          flags_(output.flags()),
          precision_(output.precision()),
          width_(output.width()),
          fill_(output.fill()),
          locale_(output.getloc())
    {
        output_.imbue(std::locale::classic());
        output_.setf(std::ios_base::dec, std::ios_base::basefield);
        output_.unsetf(std::ios_base::showbase | std::ios_base::showpos);
        output_.width(0);
    }

    JsonStreamState(const JsonStreamState &) = delete;
    JsonStreamState &operator=(const JsonStreamState &) = delete;

    ~JsonStreamState()
    {
        try {
            output_.imbue(locale_);
            output_.flags(flags_);
            output_.precision(precision_);
            output_.width(width_);
            output_.fill(fill_);
        } catch (...) {
            // Formatting restoration cannot make already-emitted JSON safer.
        }
    }

private:
    std::ostream &output_;
    std::ios_base::fmtflags flags_;
    std::streamsize precision_;
    std::streamsize width_;
    char fill_;
    std::locale locale_;
};

void write_optional_u64(
    std::ostream &output,
    const std::optional<std::uint64_t> &value)
{
    if (value.has_value()) {
        output << *value;
    } else {
        output << "null";
    }
}

void write_optional_offset(
    std::ostream &output,
    const std::optional<std::size_t> &value)
{
    if (value.has_value()) {
        output << *value;
    } else {
        output << "null";
    }
}

template <typename Unsigned>
void write_optional_unsigned(
    std::ostream &output,
    const std::optional<Unsigned> &value)
{
    if (value.has_value()) {
        output << static_cast<std::uint64_t>(*value);
    } else {
        output << "null";
    }
}

} // namespace

void write_vbios_json(std::ostream &output, const VbiosParseResult &result)
{
    const JsonStreamState stream_state{output};
    output << "{\n"
           << "  \"schema_version\": 1,\n"
           << "  \"status\": \"" << vbios_parse_status_name(result.status) << "\",\n"
           << "  \"rom\": ";

    if (!result.rom_image.has_value()) {
        output << "null,\n";
    } else {
        const auto &rom = *result.rom_image;
        const auto &pcir = rom.pcir;
        output << "{\n"
               << "    \"offset\": " << rom.offset << ",\n"
               << "    \"declared_size\": " << rom.declared_size << ",\n"
               << "    \"legacy_length_units_512_bytes\": ";
        write_optional_unsigned(output, rom.legacy_length_units_512_bytes);
        output << ",\n"
               << "    \"efi_initialization_length_units_512_bytes\": ";
        write_optional_unsigned(output, rom.efi_initialization_length_units_512_bytes);
        output << ",\n"
               << "    \"pcir\": {\n"
               << "      \"offset\": " << pcir.offset << ",\n"
               << "      \"vendor_id\": " << pcir.vendor_id << ",\n"
               << "      \"device_id\": " << pcir.device_id << ",\n"
               << "      \"revision_specific_data\": "
               << pcir.revision_specific_data << ",\n"
               << "      \"structure_length\": " << pcir.structure_length << ",\n"
               << "      \"structure_revision\": "
               << static_cast<unsigned int>(pcir.structure_revision) << ",\n"
               << "      \"class_code\": " << pcir.class_code << ",\n"
               << "      \"image_length_units_512_bytes\": "
               << pcir.image_length_units_512_bytes << ",\n"
               << "      \"code_revision\": " << pcir.code_revision << ",\n"
               << "      \"code_type\": "
               << static_cast<unsigned int>(pcir.code_type) << ",\n"
               << "      \"indicator\": "
               << static_cast<unsigned int>(pcir.indicator) << "\n"
               << "    }\n"
               << "  },\n";
    }

    output << "  \"bit\": ";
    if (!result.bit.has_value()) {
        output << "null,\n";
    } else {
        const auto &bit = *result.bit;
        output << "{\n"
               << "    \"offset\": " << bit.offset << ",\n"
               << "    \"bcd_version\": " << bit.bcd_version << ",\n"
               << "    \"header_size\": "
               << static_cast<unsigned int>(bit.header_size) << ",\n"
               << "    \"token_size\": "
               << static_cast<unsigned int>(bit.token_size) << ",\n"
               << "    \"token_count\": "
               << static_cast<unsigned int>(bit.token_count) << ",\n"
               << "    \"header_checksum_valid\": "
               << (bit.header_checksum_valid ? "true" : "false") << ",\n"
               << "    \"version_supported\": "
               << (bit.version_supported ? "true" : "false") << ",\n"
               << "    \"tokens\": [";

        if (bit.tokens.empty()) {
            output << "]\n";
        } else {
            output << '\n';
            for (std::size_t index = 0U; index < bit.tokens.size(); ++index) {
                const auto &token = bit.tokens[index];
                output << "      {\n"
                       << "        \"offset\": " << token.offset << ",\n"
                       << "        \"id\": " << static_cast<unsigned int>(token.id) << ",\n"
                       << "        \"data_version\": "
                       << static_cast<unsigned int>(token.data_version) << ",\n"
                       << "        \"data_size\": " << token.data_size << ",\n"
                       << "        \"data_pointer\": " << token.data_pointer << ",\n"
                       << "        \"validated_data_offset\": ";
                write_optional_offset(output, token.validated_data_offset);
                output << "\n      }";
                if (index + 1U != bit.tokens.size()) {
                    output << ',';
                }
                output << '\n';
            }
            output << "    ]\n";
        }
        output << "  },\n";
    }

    output << "  \"diagnostics\": [";
    if (!result.diagnostics.empty()) {
        output << '\n';
    }
    for (std::size_t index = 0U; index < result.diagnostics.size(); ++index) {
        const auto &diagnostic = result.diagnostics[index];
        output << "    {\n"
               << "      \"severity\": \""
               << vbios_diagnostic_severity_name(diagnostic.severity) << "\",\n"
               << "      \"code\": \""
               << vbios_diagnostic_code_name(diagnostic.code) << "\",\n"
               << "      \"offset\": " << diagnostic.offset << ",\n"
               << "      \"expected\": ";
        write_optional_u64(output, diagnostic.expected);
        output << ",\n"
               << "      \"actual\": ";
        write_optional_u64(output, diagnostic.actual);
        output << "\n    }";
        if (index + 1U != result.diagnostics.size()) {
            output << ',';
        }
        output << '\n';
    }
    output << "  ]\n"
           << "}\n";
}

} // namespace rtxmon::lab
