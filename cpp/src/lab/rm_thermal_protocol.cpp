#include <rtxmon/lab/rm_thermal_protocol.hpp>

#include <bit>
#include <initializer_list>

namespace rtxmon::lab {
namespace {

constexpr std::uint32_t nv_ok = 0U;
constexpr std::uint32_t executed = 1U;
constexpr std::uint32_t execute_flags_default = 0U;

RmThermalInstruction make_instruction(RmThermalOpcode opcode) noexcept
{
    RmThermalInstruction instruction{};
    instruction.opcode = static_cast<std::uint32_t>(opcode);
    return instruction;
}

RmThermalExecuteV2 make_request(
    std::initializer_list<RmThermalInstruction> instructions) noexcept
{
    RmThermalExecuteV2 request{};
    request.client_api_version = rm_thermal_api_version;
    request.client_api_revision = rm_thermal_api_revision;
    request.client_instruction_size = sizeof(RmThermalInstruction);
    request.execute_flags = execute_flags_default;
    request.instruction_list_size = static_cast<std::uint32_t>(instructions.size());

    std::size_t index = 0U;
    for (const auto &instruction : instructions) {
        request.instructions[index++] = instruction;
    }
    return request;
}

bool is_valid_response_header(
    const RmThermalExecuteV2 &response,
    std::uint32_t expected_instruction_count) noexcept
{
    return response.client_api_version == rm_thermal_api_version &&
        response.client_api_revision == rm_thermal_api_revision &&
        response.client_instruction_size == sizeof(RmThermalInstruction) &&
        response.instruction_list_size == expected_instruction_count &&
        response.successful_instructions == expected_instruction_count;
}

bool is_successful_instruction(
    const RmThermalInstruction &instruction,
    RmThermalOpcode expected_opcode) noexcept
{
    return instruction.executed == executed && instruction.result == nv_ok &&
        instruction.opcode == static_cast<std::uint32_t>(expected_opcode);
}

std::int32_t decode_signed(std::uint32_t value) noexcept
{
    return std::bit_cast<std::int32_t>(value);
}

} // namespace

RmThermalExecuteV2 make_rm_thermal_sensor_count_request() noexcept
{
    return make_request({make_instruction(RmThermalOpcode::sensors_available)});
}

std::optional<RmThermalExecuteV2> make_rm_thermal_sensor_snapshot_request(
    std::uint32_t sensor_index) noexcept
{
    if (sensor_index >= rm_thermal_maximum_instructions) {
        return std::nullopt;
    }

    auto provider = make_instruction(RmThermalOpcode::sensor_provider);
    provider.operands[0] = sensor_index;
    auto target = make_instruction(RmThermalOpcode::sensor_target);
    target.operands[0] = sensor_index;
    auto range = make_instruction(RmThermalOpcode::sensor_reading_range);
    range.operands[0] = sensor_index;
    auto reading = make_instruction(RmThermalOpcode::sensor_reading);
    reading.operands[0] = sensor_index;
    return make_request({provider, target, range, reading});
}

RmThermalExecuteV2 make_rm_thermal_provider_type_request(
    std::uint32_t provider_index) noexcept
{
    auto instruction = make_instruction(RmThermalOpcode::provider_type);
    instruction.operands[0] = provider_index;
    return make_request({instruction});
}

RmThermalExecuteV2 make_rm_thermal_target_type_request(
    std::uint32_t target_index) noexcept
{
    auto instruction = make_instruction(RmThermalOpcode::target_type);
    instruction.operands[0] = target_index;
    return make_request({instruction});
}

std::optional<std::uint32_t> decode_rm_thermal_sensor_count(
    const RmThermalExecuteV2 &response) noexcept
{
    if (!is_valid_response_header(response, 1U) ||
        !is_successful_instruction(
            response.instructions[0],
            RmThermalOpcode::sensors_available)) {
        return std::nullopt;
    }

    const auto count = response.instructions[0].operands[0];
    return count <= rm_thermal_maximum_instructions
        ? std::optional<std::uint32_t>{count}
        : std::nullopt;
}

std::optional<RmThermalSensorSnapshot> decode_rm_thermal_sensor_snapshot(
    const RmThermalExecuteV2 &response,
    std::uint32_t expected_sensor_index) noexcept
{
    if (!is_valid_response_header(response, 4U)) {
        return std::nullopt;
    }

    const auto &provider = response.instructions[0];
    const auto &target = response.instructions[1];
    const auto &range = response.instructions[2];
    const auto &reading = response.instructions[3];
    if (!is_successful_instruction(provider, RmThermalOpcode::sensor_provider) ||
        !is_successful_instruction(target, RmThermalOpcode::sensor_target) ||
        !is_successful_instruction(range, RmThermalOpcode::sensor_reading_range) ||
        !is_successful_instruction(reading, RmThermalOpcode::sensor_reading) ||
        provider.operands[0] != expected_sensor_index ||
        target.operands[0] != expected_sensor_index ||
        range.operands[0] != expected_sensor_index ||
        reading.operands[0] != expected_sensor_index) {
        return std::nullopt;
    }

    return RmThermalSensorSnapshot{
        expected_sensor_index,
        provider.operands[1],
        target.operands[1],
        decode_signed(range.operands[1]),
        decode_signed(range.operands[2]),
        decode_signed(reading.operands[1]),
    };
}

std::optional<std::uint32_t> decode_rm_thermal_provider_type(
    const RmThermalExecuteV2 &response,
    std::uint32_t expected_provider_index) noexcept
{
    if (!is_valid_response_header(response, 1U)) {
        return std::nullopt;
    }

    const auto &instruction = response.instructions[0];
    if (!is_successful_instruction(instruction, RmThermalOpcode::provider_type) ||
        instruction.operands[0] != expected_provider_index) {
        return std::nullopt;
    }
    return instruction.operands[1];
}

std::optional<RmThermalTarget> decode_rm_thermal_target_type(
    const RmThermalExecuteV2 &response,
    std::uint32_t expected_target_index) noexcept
{
    if (!is_valid_response_header(response, 1U)) {
        return std::nullopt;
    }

    const auto &instruction = response.instructions[0];
    if (!is_successful_instruction(instruction, RmThermalOpcode::target_type) ||
        instruction.operands[0] != expected_target_index) {
        return std::nullopt;
    }

    const auto type = instruction.operands[1];
    switch (type) {
    case static_cast<std::uint32_t>(RmThermalTarget::none):
    case static_cast<std::uint32_t>(RmThermalTarget::gpu):
    case static_cast<std::uint32_t>(RmThermalTarget::memory):
    case static_cast<std::uint32_t>(RmThermalTarget::power_supply):
    case static_cast<std::uint32_t>(RmThermalTarget::board):
    case static_cast<std::uint32_t>(RmThermalTarget::unknown):
        return static_cast<RmThermalTarget>(type);
    default:
        return std::nullopt;
    }
}

} // namespace rtxmon::lab
