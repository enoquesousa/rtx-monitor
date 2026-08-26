#include <rtxmon/lab/vbios_parser.hpp>

#include <array>
#include <limits>
#include <optional>
#include <utility>
#include <vector>

namespace rtxmon::lab {
namespace {

constexpr std::size_t kRomAlignment = 512U;
constexpr std::size_t kPcirPointerOffset = 0x18U;
constexpr std::size_t kPcirRevision0MinimumSize = 0x18U;
constexpr std::size_t kPcirRevision3MinimumSize = 0x1CU;
constexpr std::size_t kBitMinimumHeaderSize = 12U;
constexpr std::size_t kBitMinimumTokenSize = 6U;
constexpr std::uint16_t kSupportedBitVersion = 0x0100U;
constexpr std::uint16_t kNvidiaVendorId = 0x10DEU;
constexpr std::uint8_t kLegacyCodeType = 0x00U;
constexpr std::uint8_t kEfiCodeType = 0x03U;
constexpr std::uint8_t kLastImageIndicator = 0x80U;

constexpr std::array<std::uint8_t, 2U> kRomSignature{0x55U, 0xAAU};
constexpr std::array<std::uint8_t, 4U> kPcirSignature{'P', 'C', 'I', 'R'};
constexpr std::array<std::uint8_t, 4U> kEfiSignature{0xF1U, 0x0EU, 0x00U, 0x00U};
constexpr std::array<std::uint8_t, 6U> kBitSignature{
    0xFFU,
    0xB8U,
    'B',
    'I',
    'T',
    0x00U,
};

class ByteReader final {
public:
    explicit ByteReader(std::span<const std::byte> bytes) noexcept
        : bytes_(bytes)
    {
    }

    [[nodiscard]] std::size_t size() const noexcept
    {
        return bytes_.size();
    }

    [[nodiscard]] bool contains(std::size_t offset, std::size_t length) const noexcept
    {
        return offset <= bytes_.size() && length <= bytes_.size() - offset;
    }

    [[nodiscard]] std::optional<std::uint8_t> u8(std::size_t offset) const noexcept
    {
        if (!contains(offset, 1U)) {
            return std::nullopt;
        }
        return std::to_integer<std::uint8_t>(bytes_[offset]);
    }

    [[nodiscard]] std::optional<std::uint16_t> le16(std::size_t offset) const noexcept
    {
        if (!contains(offset, 2U)) {
            return std::nullopt;
        }

        const auto low = static_cast<std::uint16_t>(
            std::to_integer<std::uint8_t>(bytes_[offset]));
        const auto high = static_cast<std::uint16_t>(
            std::to_integer<std::uint8_t>(bytes_[offset + 1U]));
        return static_cast<std::uint16_t>(low | static_cast<std::uint16_t>(high << 8U));
    }

    template <std::size_t Size>
    [[nodiscard]] bool matches(
        std::size_t offset,
        const std::array<std::uint8_t, Size> &signature) const noexcept
    {
        if (!contains(offset, Size)) {
            return false;
        }

        for (std::size_t index = 0U; index < Size; ++index) {
            if (std::to_integer<std::uint8_t>(bytes_[offset + index]) != signature[index]) {
                return false;
            }
        }
        return true;
    }

    [[nodiscard]] std::optional<std::uint8_t> checksum8(
        std::size_t offset,
        std::size_t length) const noexcept
    {
        if (!contains(offset, length)) {
            return std::nullopt;
        }

        std::uint8_t sum = 0U;
        for (std::size_t index = 0U; index < length; ++index) {
            sum = static_cast<std::uint8_t>(
                sum + std::to_integer<std::uint8_t>(bytes_[offset + index]));
        }
        return sum;
    }

private:
    std::span<const std::byte> bytes_;
};

[[nodiscard]] bool checked_add(
    std::size_t left,
    std::size_t right,
    std::size_t &result) noexcept
{
    if (right > std::numeric_limits<std::size_t>::max() - left) {
        return false;
    }
    result = left + right;
    return true;
}

[[nodiscard]] bool checked_multiply(
    std::size_t left,
    std::size_t right,
    std::size_t &result) noexcept
{
    if (left != 0U && right > std::numeric_limits<std::size_t>::max() / left) {
        return false;
    }
    result = left * right;
    return true;
}

[[nodiscard]] VbiosDiagnostic make_diagnostic(
    VbiosDiagnosticSeverity severity,
    VbiosDiagnosticCode code,
    std::size_t offset,
    std::optional<std::uint64_t> expected = std::nullopt,
    std::optional<std::uint64_t> actual = std::nullopt)
{
    return VbiosDiagnostic{severity, code, offset, expected, actual};
}

void remember_first(
    std::optional<VbiosDiagnostic> &destination,
    VbiosDiagnostic diagnostic)
{
    if (!destination.has_value()) {
        destination = std::move(diagnostic);
    }
}

enum class ImageFailureKind {
    pcir,
    image,
};

struct ValidatedImage {
    PciRomImageInfo info;
    std::vector<VbiosDiagnostic> warnings;
};

struct ImageValidation {
    std::optional<ValidatedImage> image;
    bool recognized_pcir{};
    ImageFailureKind failure_kind{ImageFailureKind::pcir};
    std::optional<VbiosDiagnostic> failure;
};

[[nodiscard]] ImageValidation validate_image_at(
    const ByteReader &reader,
    std::size_t rom_offset)
{
    ImageValidation validation{};

    if (!reader.matches(rom_offset, kRomSignature)) {
        validation.failure_kind = ImageFailureKind::image;
        validation.failure = make_diagnostic(
            VbiosDiagnosticSeverity::error,
            VbiosDiagnosticCode::rom_signature_not_found,
            rom_offset);
        return validation;
    }

    std::size_t pcir_pointer_field = 0U;
    if (!checked_add(rom_offset, kPcirPointerOffset, pcir_pointer_field) ||
        !reader.contains(pcir_pointer_field, 2U)) {
        validation.failure = make_diagnostic(
            VbiosDiagnosticSeverity::error,
            VbiosDiagnosticCode::pcir_pointer_out_of_bounds,
            pcir_pointer_field);
        return validation;
    }

    const auto relative_pcir_offset = reader.le16(pcir_pointer_field).value();
    std::size_t pcir_offset = 0U;
    if (!checked_add(
            rom_offset,
            static_cast<std::size_t>(relative_pcir_offset),
            pcir_offset) ||
        !reader.contains(pcir_offset, kPcirSignature.size())) {
        validation.failure = make_diagnostic(
            VbiosDiagnosticSeverity::error,
            VbiosDiagnosticCode::pcir_pointer_out_of_bounds,
            pcir_pointer_field,
            std::nullopt,
            relative_pcir_offset);
        return validation;
    }
    if (!reader.matches(pcir_offset, kPcirSignature)) {
        validation.failure = make_diagnostic(
            VbiosDiagnosticSeverity::error,
            VbiosDiagnosticCode::pcir_signature_invalid,
            pcir_offset);
        return validation;
    }

    // Once both the ROM and PCIR signatures agree, this is an anchored image.
    // Later 512-byte boundaries must not be promoted if this image is invalid.
    validation.recognized_pcir = true;

    if ((relative_pcir_offset % 4U) != 0U) {
        validation.failure = make_diagnostic(
            VbiosDiagnosticSeverity::error,
            VbiosDiagnosticCode::pcir_pointer_misaligned,
            pcir_pointer_field,
            4U,
            relative_pcir_offset % 4U);
        return validation;
    }
    if (!reader.contains(pcir_offset, kPcirRevision0MinimumSize)) {
        validation.failure = make_diagnostic(
            VbiosDiagnosticSeverity::error,
            VbiosDiagnosticCode::pcir_header_truncated,
            pcir_offset,
            kPcirRevision0MinimumSize,
            reader.size() - pcir_offset);
        return validation;
    }

    const auto structure_revision = reader.u8(pcir_offset + 0x0CU).value();
    std::size_t minimum_structure_size = 0U;
    if (structure_revision == 0U) {
        minimum_structure_size = kPcirRevision0MinimumSize;
    } else if (structure_revision == 3U) {
        minimum_structure_size = kPcirRevision3MinimumSize;
    } else {
        validation.failure_kind = ImageFailureKind::image;
        validation.failure = make_diagnostic(
            VbiosDiagnosticSeverity::error,
            VbiosDiagnosticCode::pcir_revision_unsupported,
            pcir_offset + 0x0CU,
            std::nullopt,
            structure_revision);
        return validation;
    }

    const auto structure_length = reader.le16(pcir_offset + 0x0AU).value();
    if (structure_length < minimum_structure_size) {
        validation.failure_kind = ImageFailureKind::image;
        validation.failure = make_diagnostic(
            VbiosDiagnosticSeverity::error,
            VbiosDiagnosticCode::pcir_structure_length_invalid,
            pcir_offset + 0x0AU,
            minimum_structure_size,
            structure_length);
        return validation;
    }
    if (!reader.contains(pcir_offset, static_cast<std::size_t>(structure_length))) {
        validation.failure_kind = ImageFailureKind::image;
        validation.failure = make_diagnostic(
            VbiosDiagnosticSeverity::error,
            VbiosDiagnosticCode::pcir_structure_out_of_bounds,
            pcir_offset,
            structure_length,
            reader.size() - pcir_offset);
        return validation;
    }

    const auto image_length_units = reader.le16(pcir_offset + 0x10U).value();
    if (image_length_units == 0U) {
        validation.failure_kind = ImageFailureKind::image;
        validation.failure = make_diagnostic(
            VbiosDiagnosticSeverity::error,
            VbiosDiagnosticCode::image_length_zero,
            pcir_offset + 0x10U,
            1U,
            0U);
        return validation;
    }

    std::size_t declared_size = 0U;
    const bool size_valid = checked_multiply(
        static_cast<std::size_t>(image_length_units),
        kRomAlignment,
        declared_size);
    if (!size_valid || !reader.contains(rom_offset, declared_size)) {
        validation.failure_kind = ImageFailureKind::image;
        validation.failure = make_diagnostic(
            VbiosDiagnosticSeverity::error,
            VbiosDiagnosticCode::image_truncated,
            rom_offset,
            size_valid ? std::optional<std::uint64_t>{declared_size} : std::nullopt,
            rom_offset <= reader.size()
                ? std::optional<std::uint64_t>{reader.size() - rom_offset}
                : std::optional<std::uint64_t>{0U});
        return validation;
    }

    const auto relative_pcir = pcir_offset - rom_offset;
    const auto pcir_size = static_cast<std::size_t>(structure_length);
    if (relative_pcir > declared_size || pcir_size > declared_size - relative_pcir) {
        validation.failure_kind = ImageFailureKind::image;
        validation.failure = make_diagnostic(
            VbiosDiagnosticSeverity::error,
            VbiosDiagnosticCode::pcir_outside_declared_image,
            pcir_offset,
            declared_size,
            relative_pcir + pcir_size);
        return validation;
    }

    const auto class_code = static_cast<std::uint32_t>(reader.u8(pcir_offset + 0x0DU).value()) |
        (static_cast<std::uint32_t>(reader.u8(pcir_offset + 0x0EU).value()) << 8U) |
        (static_cast<std::uint32_t>(reader.u8(pcir_offset + 0x0FU).value()) << 16U);
    const PcirInfo pcir{
        pcir_offset,
        reader.le16(pcir_offset + 0x04U).value(),
        reader.le16(pcir_offset + 0x06U).value(),
        reader.le16(pcir_offset + 0x08U).value(),
        structure_length,
        structure_revision,
        class_code,
        image_length_units,
        reader.le16(pcir_offset + 0x12U).value(),
        reader.u8(pcir_offset + 0x14U).value(),
        reader.u8(pcir_offset + 0x15U).value(),
    };

    std::optional<std::uint8_t> legacy_length;
    std::optional<std::uint16_t> efi_initialization_length;
    std::vector<VbiosDiagnostic> warnings;
    if (pcir.code_type == kLegacyCodeType) {
        legacy_length = reader.u8(rom_offset + 2U).value();
        std::size_t legacy_initialization_size = 0U;
        const bool legacy_size_valid = *legacy_length != 0U && checked_multiply(
            static_cast<std::size_t>(*legacy_length),
            kRomAlignment,
            legacy_initialization_size);
        if (*legacy_length != pcir.image_length_units_512_bytes) {
            warnings.push_back(make_diagnostic(
                VbiosDiagnosticSeverity::warning,
                VbiosDiagnosticCode::rom_header_length_mismatch,
                rom_offset + 2U,
                pcir.image_length_units_512_bytes,
                *legacy_length));
        }

        if (!legacy_size_valid || legacy_initialization_size > declared_size) {
            validation.failure_kind = ImageFailureKind::image;
            validation.failure = make_diagnostic(
                VbiosDiagnosticSeverity::error,
                VbiosDiagnosticCode::rom_header_length_mismatch,
                rom_offset + 2U,
                pcir.image_length_units_512_bytes,
                *legacy_length);
            return validation;
        }

        const auto checksum = reader.checksum8(
            rom_offset,
            legacy_initialization_size).value();
        if (checksum != 0U) {
            validation.failure_kind = ImageFailureKind::image;
            validation.failure = make_diagnostic(
                VbiosDiagnosticSeverity::error,
                VbiosDiagnosticCode::rom_checksum_invalid,
                rom_offset,
                0U,
                checksum);
            return validation;
        }
    } else if (pcir.code_type == kEfiCodeType) {
        efi_initialization_length = reader.le16(rom_offset + 2U).value();
        std::size_t efi_initialization_size = 0U;
        if (!reader.matches(rom_offset + 4U, kEfiSignature)) {
            validation.failure_kind = ImageFailureKind::image;
            validation.failure = make_diagnostic(
                VbiosDiagnosticSeverity::error,
                VbiosDiagnosticCode::efi_signature_invalid,
                rom_offset + 4U,
                0x0EF1U,
                reader.le16(rom_offset + 4U).value());
            return validation;
        }
        if (*efi_initialization_length == 0U ||
            !checked_multiply(
                static_cast<std::size_t>(*efi_initialization_length),
                kRomAlignment,
                efi_initialization_size) ||
            efi_initialization_size > declared_size) {
            validation.failure_kind = ImageFailureKind::image;
            validation.failure = make_diagnostic(
                VbiosDiagnosticSeverity::error,
                VbiosDiagnosticCode::rom_header_length_mismatch,
                rom_offset + 2U,
                pcir.image_length_units_512_bytes,
                *efi_initialization_length);
            return validation;
        }
        if (*efi_initialization_length != pcir.image_length_units_512_bytes) {
            warnings.push_back(make_diagnostic(
                VbiosDiagnosticSeverity::warning,
                VbiosDiagnosticCode::rom_header_length_mismatch,
                rom_offset + 2U,
                pcir.image_length_units_512_bytes,
                *efi_initialization_length));
        }
    }

    validation.image = ValidatedImage{
        PciRomImageInfo{
            rom_offset,
            declared_size,
            legacy_length,
            efi_initialization_length,
            pcir,
        },
        std::move(warnings),
    };
    return validation;
}

struct CandidateSearch {
    std::optional<ValidatedImage> candidate;
    bool saw_rom_signature{};
    VbiosParseStatus failure_status{VbiosParseStatus::invalid_pcir};
    std::optional<VbiosDiagnostic> failure;
};

[[nodiscard]] CandidateSearch find_first_image(const ByteReader &reader)
{
    CandidateSearch search{};

    for (std::size_t rom_offset = 0U; reader.contains(rom_offset, kRomSignature.size());) {
        if (reader.matches(rom_offset, kRomSignature)) {
            search.saw_rom_signature = true;
            auto validation = validate_image_at(reader, rom_offset);
            if (validation.image.has_value()) {
                search.candidate = std::move(*validation.image);
                return search;
            }

            if (validation.failure.has_value()) {
                remember_first(search.failure, *validation.failure);
            }
            if (validation.recognized_pcir) {
                search.failure_status = validation.failure_kind == ImageFailureKind::pcir
                    ? VbiosParseStatus::invalid_pcir
                    : VbiosParseStatus::invalid_image;
                search.failure = validation.failure;
                return search;
            }
        }

        if (reader.size() - rom_offset <= kRomAlignment) {
            break;
        }
        rom_offset += kRomAlignment;
    }

    return search;
}

struct ValidatedChain {
    std::vector<PciRomImageInfo> images;
    std::vector<VbiosDiagnostic> warnings;
};

struct ChainValidation {
    std::optional<ValidatedChain> chain;
    std::optional<VbiosDiagnostic> failure;
};

[[nodiscard]] ChainValidation validate_chain(
    const ByteReader &reader,
    ValidatedImage first)
{
    ValidatedChain chain{};
    chain.images.push_back(std::move(first.info));
    chain.warnings = std::move(first.warnings);

    while ((chain.images.back().pcir.indicator & kLastImageIndicator) == 0U) {
        const auto &previous = chain.images.back();
        std::size_t next_offset = 0U;
        if (!checked_add(previous.offset, previous.declared_size, next_offset) ||
            !reader.matches(next_offset, kRomSignature)) {
            std::optional<std::uint64_t> actual;
            if (reader.contains(next_offset, 2U)) {
                actual = reader.le16(next_offset).value();
            } else if (next_offset <= reader.size()) {
                actual = reader.size() - next_offset;
            }
            return ChainValidation{
                std::nullopt,
                make_diagnostic(
                    VbiosDiagnosticSeverity::error,
                    VbiosDiagnosticCode::image_chain_truncated,
                    next_offset,
                    0xAA55U,
                    actual),
            };
        }

        auto next = validate_image_at(reader, next_offset);
        if (!next.image.has_value()) {
            return ChainValidation{std::nullopt, next.failure};
        }
        if (next.image->info.pcir.code_type == kLegacyCodeType) {
            return ChainValidation{
                std::nullopt,
                make_diagnostic(
                    VbiosDiagnosticSeverity::error,
                    VbiosDiagnosticCode::legacy_image_not_first,
                    next.image->info.pcir.offset + 0x14U,
                    0U,
                    chain.images.size()),
            };
        }
        for (const auto &warning : next.image->warnings) {
            chain.warnings.push_back(warning);
        }
        chain.images.push_back(std::move(next.image->info));
    }

    return ChainValidation{std::move(chain), std::nullopt};
}

struct BitAddressContext {
    const PciRomImageInfo &legacy;
    bool tail_layout_supported;
    std::size_t inserted_efi_size;
};

[[nodiscard]] std::optional<std::size_t> resolve_token_data(
    const ByteReader &reader,
    const BitAddressContext &context,
    std::uint16_t data_pointer,
    std::uint16_t data_size)
{
    if (data_pointer == 0U) {
        return std::nullopt;
    }

    const auto relative_offset = static_cast<std::size_t>(data_pointer);
    const auto length = static_cast<std::size_t>(data_size);
    if (relative_offset <= context.legacy.declared_size &&
        length <= context.legacy.declared_size - relative_offset) {
        return context.legacy.offset + relative_offset;
    }

    // A range that starts in the legacy image but crosses its boundary is not
    // adjusted into the inserted EFI image.
    if (relative_offset <= context.legacy.declared_size ||
        !context.tail_layout_supported) {
        return std::nullopt;
    }

    std::size_t adjusted_relative_offset = 0U;
    std::size_t absolute_offset = 0U;
    if (!checked_add(relative_offset, context.inserted_efi_size, adjusted_relative_offset) ||
        !checked_add(context.legacy.offset, adjusted_relative_offset, absolute_offset) ||
        !reader.contains(absolute_offset, length)) {
        return std::nullopt;
    }
    return absolute_offset;
}

[[nodiscard]] std::optional<BitInfo> find_bit(
    const ByteReader &reader,
    const BitAddressContext &context,
    std::vector<VbiosDiagnostic> &diagnostics)
{
    const std::size_t search_begin = context.legacy.offset;
    const std::size_t search_end = context.legacy.offset + context.legacy.declared_size;
    bool saw_signature = false;
    std::optional<VbiosDiagnostic> first_failure;

    for (std::size_t bit_offset = search_begin;
         bit_offset <= search_end - kBitSignature.size();
         ++bit_offset) {
        if (!reader.matches(bit_offset, kBitSignature)) {
            continue;
        }

        saw_signature = true;
        if (!reader.contains(bit_offset, kBitMinimumHeaderSize) ||
            kBitMinimumHeaderSize > search_end - bit_offset) {
            remember_first(
                first_failure,
                make_diagnostic(
                    VbiosDiagnosticSeverity::error,
                    VbiosDiagnosticCode::bit_header_truncated,
                    bit_offset,
                    kBitMinimumHeaderSize,
                    search_end - bit_offset));
            continue;
        }

        const auto bcd_version = reader.le16(bit_offset + 6U).value();
        const auto header_size = reader.u8(bit_offset + 8U).value();
        const auto token_size = reader.u8(bit_offset + 9U).value();
        const auto token_count = reader.u8(bit_offset + 10U).value();

        if (header_size < kBitMinimumHeaderSize) {
            remember_first(
                first_failure,
                make_diagnostic(
                    VbiosDiagnosticSeverity::error,
                    VbiosDiagnosticCode::bit_header_size_invalid,
                    bit_offset + 8U,
                    kBitMinimumHeaderSize,
                    header_size));
            continue;
        }
        if (static_cast<std::size_t>(header_size) > search_end - bit_offset) {
            remember_first(
                first_failure,
                make_diagnostic(
                    VbiosDiagnosticSeverity::error,
                    VbiosDiagnosticCode::bit_header_truncated,
                    bit_offset,
                    header_size,
                    search_end - bit_offset));
            continue;
        }
        if (token_size < kBitMinimumTokenSize) {
            remember_first(
                first_failure,
                make_diagnostic(
                    VbiosDiagnosticSeverity::error,
                    VbiosDiagnosticCode::bit_token_size_invalid,
                    bit_offset + 9U,
                    kBitMinimumTokenSize,
                    token_size));
            continue;
        }
        if (reader.checksum8(bit_offset, header_size).value() != 0U) {
            remember_first(
                first_failure,
                make_diagnostic(
                    VbiosDiagnosticSeverity::error,
                    VbiosDiagnosticCode::bit_header_checksum_invalid,
                    bit_offset));
            continue;
        }
        if (bcd_version != kSupportedBitVersion) {
            diagnostics.push_back(make_diagnostic(
                VbiosDiagnosticSeverity::error,
                VbiosDiagnosticCode::bit_version_unsupported,
                bit_offset + 6U,
                kSupportedBitVersion,
                bcd_version));
            return BitInfo{
                bit_offset,
                bcd_version,
                header_size,
                token_size,
                token_count,
                true,
                false,
                {},
            };
        }

        std::size_t token_bytes = 0U;
        const bool token_bytes_valid = checked_multiply(
            static_cast<std::size_t>(token_size),
            static_cast<std::size_t>(token_count),
            token_bytes);
        std::size_t token_array_offset = 0U;
        const bool token_offset_valid = checked_add(bit_offset, header_size, token_array_offset);
        if (!token_bytes_valid || !token_offset_valid || token_array_offset > search_end ||
            token_bytes > search_end - token_array_offset) {
            remember_first(
                first_failure,
                make_diagnostic(
                    VbiosDiagnosticSeverity::error,
                    VbiosDiagnosticCode::bit_token_array_out_of_bounds,
                    token_offset_valid ? token_array_offset : bit_offset,
                    token_bytes_valid
                        ? std::optional<std::uint64_t>{token_bytes}
                        : std::nullopt,
                    token_offset_valid && token_array_offset <= search_end
                        ? std::optional<std::uint64_t>{search_end - token_array_offset}
                        : std::optional<std::uint64_t>{0U}));
            continue;
        }

        BitInfo bit{
            bit_offset,
            bcd_version,
            header_size,
            token_size,
            token_count,
            true,
            true,
            {},
        };
        bit.tokens.reserve(token_count);

        for (std::size_t index = 0U; index < token_count; ++index) {
            const std::size_t token_offset =
                token_array_offset + index * static_cast<std::size_t>(token_size);
            const auto data_size = reader.le16(token_offset + 2U).value();
            const auto data_pointer = reader.le16(token_offset + 4U).value();
            const auto validated_data_offset = resolve_token_data(
                reader,
                context,
                data_pointer,
                data_size);

            if (data_pointer != 0U && !validated_data_offset.has_value()) {
                std::size_t attempted_end = 0U;
                const bool attempted_end_valid = checked_add(
                    static_cast<std::size_t>(data_pointer),
                    static_cast<std::size_t>(data_size),
                    attempted_end);
                diagnostics.push_back(make_diagnostic(
                    VbiosDiagnosticSeverity::warning,
                    VbiosDiagnosticCode::bit_token_data_not_resolved,
                    token_offset,
                    reader.size(),
                    attempted_end_valid
                        ? std::optional<std::uint64_t>{attempted_end}
                        : std::nullopt));
            }

            bit.tokens.push_back(BitTokenInfo{
                token_offset,
                reader.u8(token_offset).value(),
                reader.u8(token_offset + 1U).value(),
                data_size,
                data_pointer,
                validated_data_offset,
            });
        }

        return bit;
    }

    if (saw_signature && first_failure.has_value()) {
        diagnostics.push_back(*first_failure);
    } else {
        diagnostics.push_back(make_diagnostic(
            VbiosDiagnosticSeverity::warning,
            VbiosDiagnosticCode::bit_not_found,
            search_begin));
    }
    return std::nullopt;
}

} // namespace

VbiosParseResult parse_vbios(std::span<const std::byte> bytes)
{
    VbiosParseResult result{};
    const ByteReader reader{bytes};
    auto search = find_first_image(reader);

    if (!search.candidate.has_value()) {
        if (!search.saw_rom_signature) {
            result.status = VbiosParseStatus::rom_not_found;
            result.diagnostics.push_back(make_diagnostic(
                VbiosDiagnosticSeverity::error,
                VbiosDiagnosticCode::rom_signature_not_found,
                0U));
        } else {
            result.status = search.failure_status;
            if (search.failure.has_value()) {
                result.diagnostics.push_back(*search.failure);
            }
        }
        return result;
    }

    auto chain_validation = validate_chain(reader, std::move(*search.candidate));
    if (!chain_validation.chain.has_value()) {
        result.status = VbiosParseStatus::invalid_image;
        if (chain_validation.failure.has_value()) {
            result.diagnostics.push_back(*chain_validation.failure);
        }
        return result;
    }

    auto &chain = *chain_validation.chain;
    result.rom_image = chain.images.front();
    result.diagnostics = std::move(chain.warnings);
    for (const auto &image : chain.images) {
        if (image.pcir.vendor_id != kNvidiaVendorId) {
            result.diagnostics.push_back(make_diagnostic(
                VbiosDiagnosticSeverity::warning,
                VbiosDiagnosticCode::unexpected_pci_vendor,
                image.pcir.offset + 0x04U,
                kNvidiaVendorId,
                image.pcir.vendor_id));
        }
    }

    // PCI firmware places a legacy image first when it is present. Searching
    // only that validated image avoids treating coincidental bytes in EFI
    // PE/COFF or trailing opaque data as a BIT header.
    const PciRomImageInfo *legacy = nullptr;
    if (!chain.images.empty() && chain.images.front().pcir.code_type == kLegacyCodeType) {
        legacy = &chain.images.front();
    }

    if (legacy == nullptr) {
        result.diagnostics.push_back(make_diagnostic(
            VbiosDiagnosticSeverity::warning,
            VbiosDiagnosticCode::bit_not_found,
            result.rom_image->offset));
        result.status = VbiosParseStatus::partial;
        return result;
    }

    bool tail_layout_supported = true;
    std::size_t inserted_efi_size = 0U;
    for (std::size_t index = 1U; index < chain.images.size(); ++index) {
        if (chain.images[index].pcir.code_type != kEfiCodeType ||
            !checked_add(
                inserted_efi_size,
                chain.images[index].declared_size,
                inserted_efi_size)) {
            tail_layout_supported = false;
            inserted_efi_size = 0U;
            break;
        }
    }

    result.bit = find_bit(
        reader,
        BitAddressContext{*legacy, tail_layout_supported, inserted_efi_size},
        result.diagnostics);
    result.status = result.bit.has_value() && result.bit->version_supported
        ? VbiosParseStatus::success
        : VbiosParseStatus::partial;
    return result;
}

std::string_view vbios_parse_status_name(VbiosParseStatus status) noexcept
{
    switch (status) {
    case VbiosParseStatus::success:
        return "success";
    case VbiosParseStatus::partial:
        return "partial";
    case VbiosParseStatus::rom_not_found:
        return "rom_not_found";
    case VbiosParseStatus::invalid_pcir:
        return "invalid_pcir";
    case VbiosParseStatus::invalid_image:
        return "invalid_image";
    }
    return "unknown";
}

std::string_view vbios_diagnostic_severity_name(
    VbiosDiagnosticSeverity severity) noexcept
{
    switch (severity) {
    case VbiosDiagnosticSeverity::information:
        return "information";
    case VbiosDiagnosticSeverity::warning:
        return "warning";
    case VbiosDiagnosticSeverity::error:
        return "error";
    }
    return "unknown";
}

std::string_view vbios_diagnostic_code_name(VbiosDiagnosticCode code) noexcept
{
    switch (code) {
    case VbiosDiagnosticCode::rom_signature_not_found:
        return "rom_signature_not_found";
    case VbiosDiagnosticCode::pcir_pointer_out_of_bounds:
        return "pcir_pointer_out_of_bounds";
    case VbiosDiagnosticCode::pcir_pointer_misaligned:
        return "pcir_pointer_misaligned";
    case VbiosDiagnosticCode::pcir_signature_invalid:
        return "pcir_signature_invalid";
    case VbiosDiagnosticCode::pcir_header_truncated:
        return "pcir_header_truncated";
    case VbiosDiagnosticCode::pcir_structure_length_invalid:
        return "pcir_structure_length_invalid";
    case VbiosDiagnosticCode::pcir_structure_out_of_bounds:
        return "pcir_structure_out_of_bounds";
    case VbiosDiagnosticCode::pcir_revision_unsupported:
        return "pcir_revision_unsupported";
    case VbiosDiagnosticCode::image_length_zero:
        return "image_length_zero";
    case VbiosDiagnosticCode::image_truncated:
        return "image_truncated";
    case VbiosDiagnosticCode::image_chain_truncated:
        return "image_chain_truncated";
    case VbiosDiagnosticCode::legacy_image_not_first:
        return "legacy_image_not_first";
    case VbiosDiagnosticCode::pcir_outside_declared_image:
        return "pcir_outside_declared_image";
    case VbiosDiagnosticCode::rom_header_length_mismatch:
        return "rom_header_length_mismatch";
    case VbiosDiagnosticCode::rom_checksum_invalid:
        return "rom_checksum_invalid";
    case VbiosDiagnosticCode::efi_signature_invalid:
        return "efi_signature_invalid";
    case VbiosDiagnosticCode::unexpected_pci_vendor:
        return "unexpected_pci_vendor";
    case VbiosDiagnosticCode::bit_not_found:
        return "bit_not_found";
    case VbiosDiagnosticCode::bit_header_truncated:
        return "bit_header_truncated";
    case VbiosDiagnosticCode::bit_header_size_invalid:
        return "bit_header_size_invalid";
    case VbiosDiagnosticCode::bit_token_size_invalid:
        return "bit_token_size_invalid";
    case VbiosDiagnosticCode::bit_header_checksum_invalid:
        return "bit_header_checksum_invalid";
    case VbiosDiagnosticCode::bit_version_unsupported:
        return "bit_version_unsupported";
    case VbiosDiagnosticCode::bit_token_array_out_of_bounds:
        return "bit_token_array_out_of_bounds";
    case VbiosDiagnosticCode::bit_token_data_not_resolved:
        return "bit_token_data_not_resolved";
    }
    return "unknown";
}

} // namespace rtxmon::lab
