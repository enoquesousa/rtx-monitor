#include <rtxmon/lab/vbios_json.hpp>
#include <rtxmon/lab/vbios_parser.hpp>

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <locale>
#include <sstream>
#include <span>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

namespace {

constexpr std::size_t kFixtureSize = 1024U;
constexpr std::size_t kPcirOffset = 0x40U;
constexpr std::size_t kBitOffset = 0x80U;
constexpr std::size_t kLegacyImageSize = 0x200U;
constexpr std::size_t kEfiImageOffset = 0x200U;
constexpr std::size_t kTailOffset = 0x400U;
constexpr std::size_t kAdjustedTokenDataOffset = 0x500U;
constexpr std::size_t kCombinedFixtureSize = 0x600U;
constexpr std::uintmax_t kCliMaximumInputBytes = 16U * 1024U * 1024U;

void require(bool condition, const char *message)
{
    if (!condition) {
        throw std::runtime_error(message);
    }
}

void write_u8(
    std::vector<std::byte> &bytes,
    std::size_t offset,
    std::uint8_t value)
{
    bytes.at(offset) = static_cast<std::byte>(value);
}

void write_le16(
    std::vector<std::byte> &bytes,
    std::size_t offset,
    std::uint16_t value)
{
    write_u8(bytes, offset, static_cast<std::uint8_t>(value & 0xFFU));
    write_u8(bytes, offset + 1U, static_cast<std::uint8_t>(value >> 8U));
}

void write_ascii(
    std::vector<std::byte> &bytes,
    std::size_t offset,
    std::string_view value)
{
    for (std::size_t index = 0U; index < value.size(); ++index) {
        write_u8(
            bytes,
            offset + index,
            static_cast<std::uint8_t>(static_cast<unsigned char>(value[index])));
    }
}

void update_bit_checksum(std::vector<std::byte> &bytes, std::size_t bit_offset)
{
    const auto header_size = std::to_integer<std::uint8_t>(bytes.at(bit_offset + 8U));
    write_u8(bytes, bit_offset + 11U, 0U);

    std::uint8_t sum = 0U;
    for (std::size_t index = 0U; index < header_size; ++index) {
        sum = static_cast<std::uint8_t>(
            sum + std::to_integer<std::uint8_t>(bytes.at(bit_offset + index)));
    }
    write_u8(
        bytes,
        bit_offset + 11U,
        static_cast<std::uint8_t>(0U - static_cast<unsigned int>(sum)));
}

void update_legacy_checksum(
    std::vector<std::byte> &bytes,
    std::size_t rom_offset,
    std::size_t image_size)
{
    const auto checksum_offset = rom_offset + image_size - 1U;
    write_u8(bytes, checksum_offset, 0U);

    std::uint8_t sum = 0U;
    for (std::size_t index = 0U; index < image_size; ++index) {
        sum = static_cast<std::uint8_t>(
            sum + std::to_integer<std::uint8_t>(bytes.at(rom_offset + index)));
    }
    write_u8(
        bytes,
        checksum_offset,
        static_cast<std::uint8_t>(0U - static_cast<unsigned int>(sum)));
}

std::vector<std::byte> make_fixture(std::size_t rom_offset = 0U)
{
    std::vector<std::byte> bytes(rom_offset + kFixtureSize, std::byte{0xFFU});

    write_u8(bytes, rom_offset, 0x55U);
    write_u8(bytes, rom_offset + 1U, 0xAAU);
    write_u8(bytes, rom_offset + 2U, 2U);
    write_le16(bytes, rom_offset + 0x18U, static_cast<std::uint16_t>(kPcirOffset));

    const auto pcir = rom_offset + kPcirOffset;
    write_ascii(bytes, pcir, "PCIR");
    write_le16(bytes, pcir + 0x04U, 0x10DEU);
    write_le16(bytes, pcir + 0x06U, 0x2503U);
    write_le16(bytes, pcir + 0x08U, 0U);
    write_le16(bytes, pcir + 0x0AU, 0x18U);
    write_u8(bytes, pcir + 0x0CU, 0U);
    write_u8(bytes, pcir + 0x0DU, 0U);
    write_u8(bytes, pcir + 0x0EU, 0U);
    write_u8(bytes, pcir + 0x0FU, 3U);
    write_le16(bytes, pcir + 0x10U, 2U);
    write_le16(bytes, pcir + 0x12U, 1U);
    write_u8(bytes, pcir + 0x14U, 0U);
    write_u8(bytes, pcir + 0x15U, 0x80U);
    write_le16(bytes, pcir + 0x16U, 0U);

    const auto bit = rom_offset + kBitOffset;
    write_u8(bytes, bit, 0xFFU);
    write_u8(bytes, bit + 1U, 0xB8U);
    write_ascii(bytes, bit + 2U, std::string_view{"BIT\0", 4U});
    write_le16(bytes, bit + 6U, 0x0100U);
    write_u8(bytes, bit + 8U, 12U);
    write_u8(bytes, bit + 9U, 6U);
    write_u8(bytes, bit + 10U, 2U);

    const auto first_token = bit + 12U;
    write_u8(bytes, first_token, 0x32U);
    write_u8(bytes, first_token + 1U, 1U);
    write_le16(bytes, first_token + 2U, 4U);
    write_le16(bytes, first_token + 4U, 0x0200U);

    const auto second_token = first_token + 6U;
    write_u8(bytes, second_token, 0x69U);
    write_u8(bytes, second_token + 1U, 2U);
    write_le16(bytes, second_token + 2U, 0U);
    write_le16(bytes, second_token + 4U, 0U);

    write_u8(bytes, rom_offset + 0x0200U, 0x11U);
    write_u8(bytes, rom_offset + 0x0201U, 0x22U);
    write_u8(bytes, rom_offset + 0x0202U, 0x33U);
    write_u8(bytes, rom_offset + 0x0203U, 0x44U);
    update_bit_checksum(bytes, bit);
    update_legacy_checksum(bytes, rom_offset, kFixtureSize);
    return bytes;
}

std::vector<std::byte> make_legacy_efi_tail_fixture()
{
    std::vector<std::byte> bytes(kCombinedFixtureSize, std::byte{0xFFU});

    write_u8(bytes, 0U, 0x55U);
    write_u8(bytes, 1U, 0xAAU);
    write_u8(bytes, 2U, 1U);
    write_le16(bytes, 0x18U, static_cast<std::uint16_t>(kPcirOffset));

    write_ascii(bytes, kPcirOffset, "PCIR");
    write_le16(bytes, kPcirOffset + 0x04U, 0x10DEU);
    write_le16(bytes, kPcirOffset + 0x06U, 0x2503U);
    write_le16(bytes, kPcirOffset + 0x08U, 0x1234U);
    write_le16(bytes, kPcirOffset + 0x0AU, 0x18U);
    write_u8(bytes, kPcirOffset + 0x0CU, 0U);
    write_u8(bytes, kPcirOffset + 0x0DU, 0U);
    write_u8(bytes, kPcirOffset + 0x0EU, 0U);
    write_u8(bytes, kPcirOffset + 0x0FU, 3U);
    write_le16(bytes, kPcirOffset + 0x10U, 1U);
    write_le16(bytes, kPcirOffset + 0x12U, 1U);
    write_u8(bytes, kPcirOffset + 0x14U, 0U);
    write_u8(bytes, kPcirOffset + 0x15U, 0U);
    write_le16(bytes, kPcirOffset + 0x16U, 0U);

    write_u8(bytes, kBitOffset, 0xFFU);
    write_u8(bytes, kBitOffset + 1U, 0xB8U);
    write_ascii(bytes, kBitOffset + 2U, std::string_view{"BIT\0", 4U});
    write_le16(bytes, kBitOffset + 6U, 0x0100U);
    write_u8(bytes, kBitOffset + 8U, 12U);
    write_u8(bytes, kBitOffset + 9U, 6U);
    write_u8(bytes, kBitOffset + 10U, 1U);
    const auto token = kBitOffset + 12U;
    write_u8(bytes, token, 0x32U);
    write_u8(bytes, token + 1U, 1U);
    write_le16(bytes, token + 2U, 4U);
    write_le16(bytes, token + 4U, 0x0300U);
    update_bit_checksum(bytes, kBitOffset);
    update_legacy_checksum(bytes, 0U, kLegacyImageSize);

    const auto efi = kEfiImageOffset;
    write_u8(bytes, efi, 0x55U);
    write_u8(bytes, efi + 1U, 0xAAU);
    write_le16(bytes, efi + 2U, 1U);
    write_le16(bytes, efi + 4U, 0x0EF1U);
    write_le16(bytes, efi + 6U, 0U);
    write_le16(bytes, efi + 8U, 0x000BU);
    write_le16(bytes, efi + 0x0AU, 0x8664U);
    write_le16(bytes, efi + 0x0CU, 0U);
    for (std::size_t index = 0x0EU; index < 0x18U; ++index) {
        write_u8(bytes, efi + index, 0U);
    }
    write_le16(bytes, efi + 0x18U, static_cast<std::uint16_t>(kPcirOffset));

    const auto efi_pcir = efi + kPcirOffset;
    write_ascii(bytes, efi_pcir, "PCIR");
    write_le16(bytes, efi_pcir + 0x04U, 0x10DEU);
    write_le16(bytes, efi_pcir + 0x06U, 0x2503U);
    write_le16(bytes, efi_pcir + 0x08U, 0U);
    write_le16(bytes, efi_pcir + 0x0AU, 0x1CU);
    write_u8(bytes, efi_pcir + 0x0CU, 3U);
    write_u8(bytes, efi_pcir + 0x0DU, 0U);
    write_u8(bytes, efi_pcir + 0x0EU, 0U);
    write_u8(bytes, efi_pcir + 0x0FU, 3U);
    write_le16(bytes, efi_pcir + 0x10U, 1U);
    write_le16(bytes, efi_pcir + 0x12U, 1U);
    write_u8(bytes, efi_pcir + 0x14U, 3U);
    write_u8(bytes, efi_pcir + 0x15U, 0x80U);
    write_le16(bytes, efi_pcir + 0x16U, 0U);
    write_le16(bytes, efi_pcir + 0x18U, 0U);
    write_le16(bytes, efi_pcir + 0x1AU, 0U);

    write_u8(bytes, kAdjustedTokenDataOffset, 0xDEU);
    write_u8(bytes, kAdjustedTokenDataOffset + 1U, 0xADU);
    write_u8(bytes, kAdjustedTokenDataOffset + 2U, 0xBEU);
    write_u8(bytes, kAdjustedTokenDataOffset + 3U, 0xEFU);
    return bytes;
}

std::vector<std::byte> make_efi_only_fixture()
{
    const auto combined = make_legacy_efi_tail_fixture();
    return std::vector<std::byte>{
        combined.begin() + static_cast<std::ptrdiff_t>(kEfiImageOffset),
        combined.begin() + static_cast<std::ptrdiff_t>(kTailOffset)};
}

void write_fixture_file(const char *path, bool oversized)
{
    const std::filesystem::path fixture_path{path};
    std::error_code directory_error;
    if (fixture_path.has_parent_path()) {
        std::filesystem::create_directories(
            fixture_path.parent_path(),
            directory_error);
    }
    require(!directory_error, "fixture output directory must exist");

    std::ofstream output(fixture_path, std::ios::binary | std::ios::trunc);
    require(static_cast<bool>(output), "fixture output file must open");

    if (oversized) {
        // Create a sparse file one byte above the CLI limit. No 16 MiB buffer is
        // allocated by either this generator or the CLI under test.
        output.seekp(static_cast<std::streamoff>(kCliMaximumInputBytes));
        output.put('\0');
    } else {
        const auto bytes = make_fixture();
        output.write(
            reinterpret_cast<const char *>(bytes.data()),
            static_cast<std::streamsize>(bytes.size()));
    }
    require(static_cast<bool>(output), "fixture output file must be written");
}

rtxmon::lab::VbiosParseResult parse(const std::vector<std::byte> &bytes)
{
    return rtxmon::lab::parse_vbios(
        std::span<const std::byte>{bytes.data(), bytes.size()});
}

bool has_diagnostic(
    const rtxmon::lab::VbiosParseResult &result,
    rtxmon::lab::VbiosDiagnosticCode code)
{
    for (const auto &diagnostic : result.diagnostics) {
        if (diagnostic.code == code) {
            return true;
        }
    }
    return false;
}

void test_valid_synthetic_vbios()
{
    const auto result = parse(make_fixture());

    require(
        result.status == rtxmon::lab::VbiosParseStatus::success,
        "valid fixture must parse completely");
    require(result.has_valid_rom(), "valid fixture must expose its ROM image");
    require(result.rom_image->offset == 0U, "ROM must begin at offset zero");
    require(result.rom_image->declared_size == kFixtureSize, "declared image size");
    require(result.rom_image->pcir.offset == kPcirOffset, "PCIR location");
    require(result.rom_image->pcir.vendor_id == 0x10DEU, "NVIDIA vendor ID");
    require(result.rom_image->pcir.device_id == 0x2503U, "synthetic device ID");
    require(
        result.rom_image->pcir.revision_specific_data == 0U,
        "raw revision-dependent PCIR word");
    require(result.rom_image->pcir.class_code == 0x030000U, "display class code");
    require(result.rom_image->pcir.code_type == 0U, "legacy code type");
    require(result.rom_image->pcir.indicator == 0x80U, "last-image indicator");
    require(
        result.rom_image->legacy_length_units_512_bytes == 2U,
        "legacy one-byte initialization length");
    require(
        !result.rom_image->efi_initialization_length_units_512_bytes.has_value(),
        "legacy header must not be decoded as EFI");

    require(result.bit.has_value(), "valid fixture must expose BIT metadata");
    require(result.bit->offset == kBitOffset, "BIT location");
    require(result.bit->bcd_version == 0x0100U, "BIT BCD revision");
    require(result.bit->header_size == 12U, "BIT header size");
    require(result.bit->token_size == 6U, "BIT token size");
    require(result.bit->token_count == 2U, "BIT token count");
    require(result.bit->header_checksum_valid, "BIT checksum");
    require(result.bit->version_supported, "BIT version support flag");
    require(result.bit->tokens.size() == 2U, "two token records");
    require(result.bit->tokens[0].id == 0x32U, "I2C-pointer token ID metadata");
    require(result.bit->tokens[0].data_version == 1U, "token data revision");
    require(result.bit->tokens[0].data_size == 4U, "token data size");
    require(result.bit->tokens[0].data_pointer == 0x0200U, "token raw pointer");
    require(
        result.bit->tokens[0].validated_data_offset == 0x0200U,
        "bounded token range location");
    require(
        !result.bit->tokens[1].validated_data_offset.has_value(),
        "null token pointer must stay unresolved");
    require(result.diagnostics.empty(), "valid fixture should need no diagnostics");
}

void test_firmware_prefix_scan()
{
    const auto result = parse(make_fixture(512U));

    require(
        result.status == rtxmon::lab::VbiosParseStatus::success,
        "parser must find a ROM on a later 512-byte boundary");
    require(result.rom_image->offset == 512U, "prefixed ROM location");
    require(result.rom_image->pcir.offset == 512U + kPcirOffset, "relative PCIR pointer");
    require(result.bit->offset == 512U + kBitOffset, "prefixed BIT location");
    require(
        result.bit->tokens[0].validated_data_offset == 512U + 0x0200U,
        "token pointer must be relative to its ROM image");
}

void test_container_failures()
{
    const std::vector<std::byte> no_rom(64U, std::byte{0U});
    const auto missing = parse(no_rom);
    require(
        missing.status == rtxmon::lab::VbiosParseStatus::rom_not_found,
        "missing ROM signature status");
    require(
        has_diagnostic(missing, rtxmon::lab::VbiosDiagnosticCode::rom_signature_not_found),
        "missing ROM diagnostic");

    auto invalid_pointer_bytes = make_fixture();
    write_le16(invalid_pointer_bytes, 0x18U, 0xFFF0U);
    const auto invalid_pointer = parse(invalid_pointer_bytes);
    require(
        invalid_pointer.status == rtxmon::lab::VbiosParseStatus::invalid_pcir,
        "out-of-range PCIR pointer status");
    require(
        has_diagnostic(
            invalid_pointer,
            rtxmon::lab::VbiosDiagnosticCode::pcir_pointer_out_of_bounds),
        "out-of-range PCIR pointer diagnostic");

    auto invalid_signature_bytes = make_fixture();
    write_u8(invalid_signature_bytes, kPcirOffset, 0U);
    const auto invalid_signature = parse(invalid_signature_bytes);
    require(
        invalid_signature.status == rtxmon::lab::VbiosParseStatus::invalid_pcir,
        "invalid PCIR signature status");
    require(
        has_diagnostic(
            invalid_signature,
            rtxmon::lab::VbiosDiagnosticCode::pcir_signature_invalid),
        "invalid PCIR signature diagnostic");

    auto truncated_bytes = make_fixture();
    truncated_bytes.resize(700U);
    const auto truncated = parse(truncated_bytes);
    require(
        truncated.status == rtxmon::lab::VbiosParseStatus::invalid_image,
        "truncated declared image status");
    require(
        has_diagnostic(truncated, rtxmon::lab::VbiosDiagnosticCode::image_truncated),
        "truncated declared image diagnostic");
}

void test_malformed_bit_is_partial()
{
    auto checksum_bytes = make_fixture();
    write_u8(checksum_bytes, kBitOffset + 11U, 0U);
    update_legacy_checksum(checksum_bytes, 0U, kFixtureSize);
    const auto checksum = parse(checksum_bytes);
    require(
        checksum.status == rtxmon::lab::VbiosParseStatus::partial,
        "bad BIT must preserve validated ROM metadata as a partial result");
    require(!checksum.bit.has_value(), "bad BIT must not be accepted");
    require(
        has_diagnostic(
            checksum,
            rtxmon::lab::VbiosDiagnosticCode::bit_header_checksum_invalid),
        "bad BIT checksum diagnostic");

    auto token_array_bytes = make_fixture();
    write_u8(token_array_bytes, kBitOffset + 10U, 0xFFU);
    update_bit_checksum(token_array_bytes, kBitOffset);
    update_legacy_checksum(token_array_bytes, 0U, kFixtureSize);
    const auto token_array = parse(token_array_bytes);
    require(
        token_array.status == rtxmon::lab::VbiosParseStatus::partial,
        "out-of-range BIT token array must be partial");
    require(
        has_diagnostic(
            token_array,
            rtxmon::lab::VbiosDiagnosticCode::bit_token_array_out_of_bounds),
        "out-of-range BIT token array diagnostic");
}

void test_token_payload_is_never_followed_out_of_bounds()
{
    auto bytes = make_fixture();
    write_le16(bytes, kBitOffset + 12U + 4U, 0x03FFU);
    update_legacy_checksum(bytes, 0U, kFixtureSize);
    const auto result = parse(bytes);

    require(
        result.status == rtxmon::lab::VbiosParseStatus::success,
        "opaque token metadata remains parseable");
    require(
        !result.bit->tokens[0].validated_data_offset.has_value(),
        "out-of-range token payload must not receive a validated offset");
    require(
        has_diagnostic(
            result,
            rtxmon::lab::VbiosDiagnosticCode::bit_token_data_not_resolved),
        "unresolved token payload diagnostic");
}

void test_legacy_checksum_is_required()
{
    auto bytes = make_fixture();
    write_u8(bytes, 0x0300U, 0x5AU);
    const auto result = parse(bytes);

    require(
        result.status == rtxmon::lab::VbiosParseStatus::invalid_image,
        "legacy image checksum failure must invalidate the ROM");
    require(
        has_diagnostic(result, rtxmon::lab::VbiosDiagnosticCode::rom_checksum_invalid),
        "legacy checksum diagnostic");
    require(!result.has_valid_rom(), "invalid checksum must not expose a valid ROM");
}

void test_legacy_checksum_uses_initialization_length()
{
    auto shorter_initialization = make_fixture();
    write_u8(shorter_initialization, 2U, 1U);
    write_u8(shorter_initialization, 0x0300U, 0x5AU);
    update_legacy_checksum(shorter_initialization, 0U, kLegacyImageSize);
    const auto shorter = parse(shorter_initialization);
    require(
        shorter.status == rtxmon::lab::VbiosParseStatus::success,
        "checksum must cover Size512 initialization bytes, not all PCIR bytes");
    require(
        has_diagnostic(
            shorter,
            rtxmon::lab::VbiosDiagnosticCode::rom_header_length_mismatch),
        "smaller initialization range mismatch remains auditable as a warning");

    auto zero_length = make_fixture();
    write_u8(zero_length, 2U, 0U);
    const auto zero = parse(zero_length);
    require(
        zero.status == rtxmon::lab::VbiosParseStatus::invalid_image,
        "zero legacy initialization length must fail closed");
    require(
        has_diagnostic(
            zero,
            rtxmon::lab::VbiosDiagnosticCode::rom_header_length_mismatch),
        "zero legacy initialization length diagnostic");
}

void test_pcir_alignment_and_revision_are_required()
{
    auto misaligned_bytes = make_fixture();
    std::vector<std::byte> pcir_copy(
        misaligned_bytes.begin() + static_cast<std::ptrdiff_t>(kPcirOffset),
        misaligned_bytes.begin() + static_cast<std::ptrdiff_t>(kPcirOffset + 0x18U));
    for (std::size_t index = 0U; index < pcir_copy.size(); ++index) {
        misaligned_bytes[kPcirOffset + 1U + index] = pcir_copy[index];
    }
    write_le16(misaligned_bytes, 0x18U, static_cast<std::uint16_t>(kPcirOffset + 1U));
    update_legacy_checksum(misaligned_bytes, 0U, kFixtureSize);
    const auto misaligned = parse(misaligned_bytes);
    require(
        misaligned.status == rtxmon::lab::VbiosParseStatus::invalid_pcir,
        "misaligned PCIR must be rejected");
    require(
        has_diagnostic(
            misaligned,
            rtxmon::lab::VbiosDiagnosticCode::pcir_pointer_misaligned),
        "misaligned PCIR diagnostic");

    auto revision_bytes = make_fixture();
    write_u8(revision_bytes, kPcirOffset + 0x0CU, 1U);
    update_legacy_checksum(revision_bytes, 0U, kFixtureSize);
    const auto revision = parse(revision_bytes);
    require(
        revision.status == rtxmon::lab::VbiosParseStatus::invalid_image,
        "unsupported PCIR revision must fail closed");
    require(
        has_diagnostic(
            revision,
            rtxmon::lab::VbiosDiagnosticCode::pcir_revision_unsupported),
        "unsupported PCIR revision diagnostic");

    auto short_revision3_bytes = make_legacy_efi_tail_fixture();
    write_le16(
        short_revision3_bytes,
        kEfiImageOffset + kPcirOffset + 0x0AU,
        0x18U);
    const auto short_revision3 = parse(short_revision3_bytes);
    require(
        short_revision3.status == rtxmon::lab::VbiosParseStatus::invalid_image,
        "PCI revision 3 requires its complete 0x1c-byte structure");
    require(
        has_diagnostic(
            short_revision3,
            rtxmon::lab::VbiosDiagnosticCode::pcir_structure_length_invalid),
        "short PCI revision 3 diagnostic");

    auto invalid_efi_signature_bytes = make_legacy_efi_tail_fixture();
    write_u8(invalid_efi_signature_bytes, kEfiImageOffset + 4U, 0U);
    const auto invalid_efi_signature = parse(invalid_efi_signature_bytes);
    require(
        invalid_efi_signature.status == rtxmon::lab::VbiosParseStatus::invalid_image,
        "Code Type 03 requires the EFI expansion ROM signature");
    require(
        has_diagnostic(
            invalid_efi_signature,
            rtxmon::lab::VbiosDiagnosticCode::efi_signature_invalid),
        "invalid EFI expansion ROM signature diagnostic");
}

void test_unsupported_bit_version_is_partial()
{
    auto bytes = make_fixture();
    write_le16(bytes, kBitOffset + 6U, 0x0200U);
    update_bit_checksum(bytes, kBitOffset);
    update_legacy_checksum(bytes, 0U, kFixtureSize);
    const auto result = parse(bytes);

    require(
        result.status == rtxmon::lab::VbiosParseStatus::partial,
        "unsupported BIT version must preserve only validated ROM metadata");
    require(result.bit.has_value(), "validated unsupported BIT header must be preserved");
    require(!result.bit->version_supported, "unsupported BIT must be marked explicitly");
    require(result.bit->tokens.empty(), "unsupported BIT tokens must not be decoded");
    require(
        has_diagnostic(
            result,
            rtxmon::lab::VbiosDiagnosticCode::bit_version_unsupported),
        "unsupported BIT version diagnostic");

    std::ostringstream json;
    rtxmon::lab::write_vbios_json(json, result);
    require(
        json.str().find("\"status\": \"partial\"") != std::string::npos,
        "unsupported BIT JSON status");
    require(
        json.str().find("\"version_supported\": false") != std::string::npos,
        "unsupported BIT JSON support flag");
    require(
        json.str().find("\"tokens\": []") != std::string::npos,
        "unsupported BIT JSON must not expose token records");
}

void test_legacy_efi_chain_and_tail_pointer()
{
    const auto result = parse(make_legacy_efi_tail_fixture());

    require(
        result.status == rtxmon::lab::VbiosParseStatus::success,
        "legacy plus EFI chain must validate completely");
    require(result.bit.has_value(), "legacy BIT must be found");
    require(result.bit->tokens.size() == 1U, "combined fixture token count");
    require(result.bit->tokens[0].data_pointer == 0x0300U, "raw pointer is preserved");
    require(
        result.bit->tokens[0].validated_data_offset == kAdjustedTokenDataOffset,
        "NVIDIA pointer adjustment must skip the inserted EFI image");
    require(
        result.rom_image->pcir.revision_specific_data == 0x1234U,
        "revision-dependent PCIR word must remain raw");

    auto truncated_chain = make_legacy_efi_tail_fixture();
    truncated_chain.resize(kLegacyImageSize);
    const auto truncated = parse(truncated_chain);
    require(
        truncated.status == rtxmon::lab::VbiosParseStatus::invalid_image,
        "an announced but absent second image must invalidate the container");
    require(
        has_diagnostic(
            truncated,
            rtxmon::lab::VbiosDiagnosticCode::image_chain_truncated),
        "truncated chain diagnostic");
    require(!truncated.has_valid_rom(), "broken chain must not expose a valid container");

    auto cut_tail = make_legacy_efi_tail_fixture();
    cut_tail.resize(kAdjustedTokenDataOffset + 3U);
    const auto cut = parse(cut_tail);
    require(
        cut.status == rtxmon::lab::VbiosParseStatus::success,
        "opaque tail truncation remains a warning after the PCI chain validates");
    require(
        !cut.bit->tokens[0].validated_data_offset.has_value(),
        "partially present opaque payload must not receive a validated offset");
    require(
        has_diagnostic(
            cut,
            rtxmon::lab::VbiosDiagnosticCode::bit_token_data_not_resolved),
        "cut opaque payload diagnostic");
}

void test_legacy_image_must_be_first()
{
    const auto combined = make_legacy_efi_tail_fixture();
    std::vector<std::byte> reversed(kTailOffset, std::byte{0xFFU});
    std::copy(
        combined.begin() + static_cast<std::ptrdiff_t>(kEfiImageOffset),
        combined.begin() + static_cast<std::ptrdiff_t>(kTailOffset),
        reversed.begin());
    std::copy(
        combined.begin(),
        combined.begin() + static_cast<std::ptrdiff_t>(kLegacyImageSize),
        reversed.begin() + static_cast<std::ptrdiff_t>(kEfiImageOffset));
    write_u8(reversed, kPcirOffset + 0x15U, 0U);
    write_u8(
        reversed,
        kEfiImageOffset + kPcirOffset + 0x15U,
        0x80U);
    update_legacy_checksum(reversed, kEfiImageOffset, kLegacyImageSize);

    const auto result = parse(reversed);
    require(
        result.status == rtxmon::lab::VbiosParseStatus::invalid_image,
        "a legacy image after EFI violates the PCI image order");
    require(
        has_diagnostic(
            result,
            rtxmon::lab::VbiosDiagnosticCode::legacy_image_not_first),
        "legacy image order diagnostic");
}

void test_bit_search_is_limited_to_legacy_image()
{
    auto bytes = make_legacy_efi_tail_fixture();
    for (std::size_t index = 0U; index < 18U; ++index) {
        write_u8(bytes, kBitOffset + index, 0U);
    }
    const auto false_bit = kEfiImageOffset + kBitOffset;
    write_u8(bytes, false_bit, 0xFFU);
    write_u8(bytes, false_bit + 1U, 0xB8U);
    write_ascii(bytes, false_bit + 2U, std::string_view{"BIT\0", 4U});
    write_le16(bytes, false_bit + 6U, 0x0100U);
    write_u8(bytes, false_bit + 8U, 12U);
    write_u8(bytes, false_bit + 9U, 6U);
    write_u8(bytes, false_bit + 10U, 0U);
    update_bit_checksum(bytes, false_bit);
    update_legacy_checksum(bytes, 0U, kLegacyImageSize);
    const auto result = parse(bytes);

    require(
        result.status == rtxmon::lab::VbiosParseStatus::partial,
        "BIT-like bytes in EFI must not be promoted");
    require(!result.bit.has_value(), "BIT search must remain inside legacy");
    require(
        has_diagnostic(result, rtxmon::lab::VbiosDiagnosticCode::bit_not_found),
        "legacy-only BIT search diagnostic");
}

void test_efi_header_uses_16_bit_initialization_size()
{
    const auto result = parse(make_efi_only_fixture());

    require(
        result.status == rtxmon::lab::VbiosParseStatus::partial,
        "EFI-only option ROM is valid but has no legacy BIT");
    require(result.has_valid_rom(), "EFI-only image must retain validated metadata");
    require(
        !result.rom_image->legacy_length_units_512_bytes.has_value(),
        "EFI header must not expose a legacy one-byte size");
    require(
        result.rom_image->efi_initialization_length_units_512_bytes == 1U,
        "EFI InitializationSize is a 16-bit field");
    require(result.rom_image->pcir.structure_revision == 3U, "EFI PCIR revision 3");
}

void test_every_truncation_is_safe()
{
    const auto complete = make_fixture();
    for (std::size_t size = 0U; size < complete.size(); ++size) {
        const auto result = rtxmon::lab::parse_vbios(
            std::span<const std::byte>{complete.data(), size});
        require(
            result.status != rtxmon::lab::VbiosParseStatus::success,
            "a truncated fixture must never be reported as complete");
    }
}

void test_enum_names_are_stable()
{
    require(
        rtxmon::lab::vbios_parse_status_name(rtxmon::lab::VbiosParseStatus::success) ==
            "success",
        "parse status name");
    require(
        rtxmon::lab::vbios_diagnostic_severity_name(
            rtxmon::lab::VbiosDiagnosticSeverity::warning) == "warning",
        "diagnostic severity name");
    require(
        rtxmon::lab::vbios_diagnostic_code_name(
            rtxmon::lab::VbiosDiagnosticCode::bit_not_found) == "bit_not_found",
        "diagnostic code name");
}

class GroupingFacet final : public std::numpunct<char> {
protected:
    [[nodiscard]] char do_thousands_sep() const override
    {
        return '_';
    }

    [[nodiscard]] std::string do_grouping() const override
    {
        return "\3";
    }
};

void test_json_is_deterministic()
{
    const auto result = parse(make_fixture());
    std::ostringstream first;
    std::ostringstream second;
    rtxmon::lab::write_vbios_json(first, result);
    rtxmon::lab::write_vbios_json(second, result);

    const auto json = first.str();
    require(json == second.str(), "identical results must produce byte-identical JSON");
    require(json.starts_with("{\n  \"schema_version\": 1,"), "JSON schema prefix");
    require(json.find("\"status\": \"success\"") != std::string::npos, "JSON status");
    require(json.find("\"rom\": {") != std::string::npos, "JSON ROM object");
    require(json.find("\"pcir\": {") != std::string::npos, "JSON PCIR object");
    require(
        json.find("\"revision_specific_data\": 0") != std::string::npos,
        "JSON revision-dependent PCIR word");
    require(json.find("\"bit\": {") != std::string::npos, "JSON BIT object");
    require(json.find("\"tokens\": [") != std::string::npos, "JSON token metadata");
    require(json.ends_with("}\n"), "JSON must end with one object and newline");

    std::ostringstream hostile;
    hostile.imbue(std::locale{std::locale::classic(), new GroupingFacet});
    hostile << std::hex << std::showbase << std::setw(20);
    const auto original_flags = hostile.flags();
    const auto original_locale = hostile.getloc();
    rtxmon::lab::write_vbios_json(hostile, result);
    const auto hostile_json = hostile.str();
    require(hostile_json.starts_with("{\n"), "caller width must not prefix JSON");
    require(
        hostile_json.find("\"vendor_id\": 4318") != std::string::npos,
        "JSON integers must be classic-locale decimal");
    require(hostile.flags() == original_flags, "JSON writer must restore stream flags");
    require(hostile.getloc() == original_locale, "JSON writer must restore stream locale");
}

} // namespace

int main(int argc, char **argv)
{
    try {
        if (argc == 3) {
            const std::string_view operation{argv[1]};
            if (operation == "--write-fixture") {
                write_fixture_file(argv[2], false);
                std::cout << "synthetic VBIOS fixture written\n";
                return 0;
            }
            if (operation == "--write-oversized-fixture") {
                write_fixture_file(argv[2], true);
                std::cout << "synthetic oversized fixture written\n";
                return 0;
            }
        }
        require(argc == 1, "unexpected test arguments");

        test_valid_synthetic_vbios();
        test_firmware_prefix_scan();
        test_container_failures();
        test_malformed_bit_is_partial();
        test_token_payload_is_never_followed_out_of_bounds();
        test_legacy_checksum_is_required();
        test_legacy_checksum_uses_initialization_length();
        test_pcir_alignment_and_revision_are_required();
        test_unsupported_bit_version_is_partial();
        test_legacy_efi_chain_and_tail_pointer();
        test_legacy_image_must_be_first();
        test_bit_search_is_limited_to_legacy_image();
        test_efi_header_uses_16_bit_initialization_size();
        test_every_truncation_is_safe();
        test_enum_names_are_stable();
        test_json_is_deterministic();
        std::cout << "rtxmon VBIOS parser tests passed\n";
        return 0;
    } catch (const std::exception &error) {
        std::cerr << "FAILED: " << error.what() << '\n';
        return 1;
    }
}
