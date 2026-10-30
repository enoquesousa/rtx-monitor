#ifndef RTXMON_LAB_RM_THERMAL_PROTOCOL_HPP
#define RTXMON_LAB_RM_THERMAL_PROTOCOL_HPP

#include <array>
#include <cstddef>
#include <cstdint>
#include <optional>

namespace rtxmon::lab {

// Minimal, transport-independent representation of the MIT-licensed NVIDIA
// THERMAL_SYSTEM_EXECUTE_V2 RM control ABI published in open-gpu-kernel-modules.
// This module only constructs and validates byte-compatible request objects. It
// does not open NVAPI, a driver, a device object, PCI, MMIO, I2C, or firmware.
inline constexpr std::uint32_t rm_thermal_api_version = 1U;
inline constexpr std::uint32_t rm_thermal_api_revision = 0U;
inline constexpr std::uint32_t rm_thermal_execute_v2_command = 0x20800513U;
inline constexpr std::uint32_t rm_thermal_execute_v2_physical_command = 0x20808513U;
inline constexpr std::size_t rm_thermal_maximum_instructions = 32U;

enum class RmThermalOpcode : std::uint32_t {
    targets_available = 0x00000100U,
    target_type = 0x00000101U,
    provider_type = 0x00000301U,
    sensors_available = 0x00000500U,
    sensor_provider = 0x00000510U,
    sensor_target = 0x00000520U,
    sensor_reading_range = 0x00000540U,
    sensor_reading = 0x00001500U,
};

enum class RmThermalTarget : std::uint32_t {
    none = 0x00000000U,
    gpu = 0x00000001U,
    memory = 0x00000002U,
    power_supply = 0x00000004U,
    board = 0x00000008U,
    unknown = 0xFFFFFFFFU,
};

struct RmThermalInstruction {
    std::uint32_t result{};
    std::uint32_t executed{};
    std::uint32_t opcode{};
    std::array<std::uint32_t, 8U> operands{};
};

struct RmThermalExecuteV2 {
    std::uint32_t client_api_version{};
    std::uint32_t client_api_revision{};
    std::uint32_t client_instruction_size{};
    std::uint32_t execute_flags{};
    std::uint32_t successful_instructions{};
    std::uint32_t instruction_list_size{};
    std::array<RmThermalInstruction, rm_thermal_maximum_instructions> instructions{};
};

static_assert(sizeof(RmThermalInstruction) == 44U);
static_assert(alignof(RmThermalInstruction) == alignof(std::uint32_t));
static_assert(offsetof(RmThermalInstruction, operands) == 12U);
static_assert(sizeof(RmThermalExecuteV2) == 1432U);
static_assert(offsetof(RmThermalExecuteV2, instructions) == 24U);

struct RmThermalSensorSnapshot {
    std::uint32_t sensor_index{};
    std::uint32_t provider_index{};
    std::uint32_t target_index{};
    std::int32_t minimum{};
    std::int32_t maximum{};
    std::int32_t reading{};
};

[[nodiscard]] RmThermalExecuteV2 make_rm_thermal_sensor_count_request() noexcept;

[[nodiscard]] std::optional<RmThermalExecuteV2> make_rm_thermal_sensor_snapshot_request(
    std::uint32_t sensor_index) noexcept;

[[nodiscard]] RmThermalExecuteV2 make_rm_thermal_provider_type_request(
    std::uint32_t provider_index) noexcept;

[[nodiscard]] RmThermalExecuteV2 make_rm_thermal_target_type_request(
    std::uint32_t target_index) noexcept;

[[nodiscard]] std::optional<std::uint32_t> decode_rm_thermal_sensor_count(
    const RmThermalExecuteV2 &response) noexcept;

[[nodiscard]] std::optional<RmThermalSensorSnapshot> decode_rm_thermal_sensor_snapshot(
    const RmThermalExecuteV2 &response,
    std::uint32_t expected_sensor_index) noexcept;

[[nodiscard]] std::optional<std::uint32_t> decode_rm_thermal_provider_type(
    const RmThermalExecuteV2 &response,
    std::uint32_t expected_provider_index) noexcept;

[[nodiscard]] std::optional<RmThermalTarget> decode_rm_thermal_target_type(
    const RmThermalExecuteV2 &response,
    std::uint32_t expected_target_index) noexcept;

} // namespace rtxmon::lab

#endif
