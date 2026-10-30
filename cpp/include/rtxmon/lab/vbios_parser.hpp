#ifndef RTXMON_LAB_VBIOS_PARSER_HPP
#define RTXMON_LAB_VBIOS_PARSER_HPP

#include <cstddef>
#include <cstdint>
#include <optional>
#include <span>
#include <string_view>
#include <vector>

namespace rtxmon::lab {

enum class VbiosParseStatus {
    success,
    partial,
    rom_not_found,
    invalid_pcir,
    invalid_image,
};

enum class VbiosDiagnosticSeverity {
    information,
    warning,
    error,
};

enum class VbiosDiagnosticCode {
    rom_signature_not_found,
    pcir_pointer_out_of_bounds,
    pcir_pointer_misaligned,
    pcir_signature_invalid,
    pcir_header_truncated,
    pcir_structure_length_invalid,
    pcir_structure_out_of_bounds,
    pcir_revision_unsupported,
    image_length_zero,
    image_truncated,
    image_chain_truncated,
    legacy_image_not_first,
    pcir_outside_declared_image,
    rom_header_length_mismatch,
    rom_checksum_invalid,
    efi_signature_invalid,
    unexpected_pci_vendor,
    bit_not_found,
    bit_header_truncated,
    bit_header_size_invalid,
    bit_token_size_invalid,
    bit_header_checksum_invalid,
    bit_version_unsupported,
    bit_token_array_out_of_bounds,
    bit_token_data_not_resolved,
};

struct VbiosDiagnostic {
    VbiosDiagnosticSeverity severity;
    VbiosDiagnosticCode code;
    std::size_t offset;
    std::optional<std::uint64_t> expected;
    std::optional<std::uint64_t> actual;
};

struct PcirInfo {
    std::size_t offset;
    std::uint16_t vendor_id;
    std::uint16_t device_id;
    // Revision-dependent PCIR word at offset +0x08. PCI 3.0 defines it as
    // DeviceListOffset; older structures do not share that meaning.
    std::uint16_t revision_specific_data;
    std::uint16_t structure_length;
    std::uint8_t structure_revision;
    std::uint32_t class_code;
    std::uint16_t image_length_units_512_bytes;
    std::uint16_t code_revision;
    std::uint8_t code_type;
    std::uint8_t indicator;
};

struct PciRomImageInfo {
    std::size_t offset;
    std::size_t declared_size;
    std::optional<std::uint8_t> legacy_length_units_512_bytes;
    std::optional<std::uint16_t> efi_initialization_length_units_512_bytes;
    PcirInfo pcir;
};

struct BitTokenInfo {
    std::size_t offset;
    std::uint8_t id;
    std::uint8_t data_version;
    std::uint16_t data_size;
    std::uint16_t data_pointer;

    // Set only when the NVIDIA pointer adjustment has been applied where
    // required and the complete opaque range is inside the input artifact.
    // The parser never dereferences or interprets this range.
    std::optional<std::size_t> validated_data_offset;
};

struct BitInfo {
    std::size_t offset;
    std::uint16_t bcd_version;
    std::uint8_t header_size;
    std::uint8_t token_size;
    std::uint8_t token_count;
    bool header_checksum_valid;
    bool version_supported;
    std::vector<BitTokenInfo> tokens;
};

struct VbiosParseResult {
    VbiosParseStatus status{VbiosParseStatus::rom_not_found};
    std::optional<PciRomImageInfo> rom_image;
    std::optional<BitInfo> bit;
    std::vector<VbiosDiagnostic> diagnostics;

    [[nodiscard]] bool has_valid_rom() const noexcept
    {
        return rom_image.has_value();
    }
};

// Parses an immutable, already-acquired firmware byte sequence. This function
// performs no file, driver, PCI, MMIO, I2C, or other hardware access.
[[nodiscard]] VbiosParseResult parse_vbios(std::span<const std::byte> bytes);

[[nodiscard]] std::string_view vbios_parse_status_name(VbiosParseStatus status) noexcept;
[[nodiscard]] std::string_view vbios_diagnostic_severity_name(
    VbiosDiagnosticSeverity severity) noexcept;
[[nodiscard]] std::string_view vbios_diagnostic_code_name(
    VbiosDiagnosticCode code) noexcept;

} // namespace rtxmon::lab

#endif
