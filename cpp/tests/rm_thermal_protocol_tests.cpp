#include <rtxmon/lab/rm_thermal_protocol.hpp>

#include <bit>
#include <cstdint>
#include <iostream>
#include <stdexcept>

namespace {

using rtxmon::lab::RmThermalExecuteV2;
using rtxmon::lab::RmThermalOpcode;

void require(bool condition, const char *message)
{
    if (!condition) {
        throw std::runtime_error(message);
    }
}

void mark_success(RmThermalExecuteV2 &response)
{
    response.successful_instructions = response.instruction_list_size;
    for (std::uint32_t index = 0U; index < response.instruction_list_size; ++index) {
        response.instructions[index].executed = 1U;
        response.instructions[index].result = 0U;
    }
}

void test_layout_and_constants()
{
    using namespace rtxmon::lab;
    require(rm_thermal_api_version == 1U, "thermal API version drifted");
    require(rm_thermal_api_revision == 0U, "thermal API revision drifted");
    require(
        rm_thermal_execute_v2_command == 0x20800513U,
        "logical RM command drifted");
    require(
        rm_thermal_execute_v2_physical_command == 0x20808513U,
        "physical RM command drifted");
    require(sizeof(RmThermalExecuteV2) == 1432U, "execute request ABI size drifted");
}

void test_sensor_count_request_and_response()
{
    using namespace rtxmon::lab;
    auto response = make_rm_thermal_sensor_count_request();
    require(response.client_api_version == 1U, "request must set API version");
    require(response.client_api_revision == 0U, "request must set API revision");
    require(response.client_instruction_size == 44U, "request must set instruction size");
    require(response.instruction_list_size == 1U, "count request must have one instruction");
    require(
        response.instructions[0].opcode ==
            static_cast<std::uint32_t>(RmThermalOpcode::sensors_available),
        "count request must use the documented opcode");

    mark_success(response);
    response.instructions[0].operands[0] = 4U;
    require(
        decode_rm_thermal_sensor_count(response) == 4U,
        "successful count response must decode");

    response.instructions[0].operands[0] = 33U;
    require(
        !decode_rm_thermal_sensor_count(response).has_value(),
        "count above the bounded instruction domain must fail closed");
}

void test_sensor_snapshot_round_trip()
{
    using namespace rtxmon::lab;
    auto request = make_rm_thermal_sensor_snapshot_request(3U);
    require(request.has_value(), "bounded sensor index must build a request");
    require(request->instruction_list_size == 4U, "snapshot must use four instructions");
    require(
        request->instructions[0].opcode ==
            static_cast<std::uint32_t>(RmThermalOpcode::sensor_provider),
        "snapshot must query provider first");
    require(
        request->instructions[3].opcode ==
            static_cast<std::uint32_t>(RmThermalOpcode::sensor_reading),
        "snapshot must query reading last");
    for (std::uint32_t index = 0U; index < 4U; ++index) {
        require(request->instructions[index].operands[0] == 3U, "sensor index must be pinned");
    }
    require(
        !make_rm_thermal_sensor_snapshot_request(32U).has_value(),
        "unbounded sensor index must fail closed");

    mark_success(*request);
    request->instructions[0].operands[1] = 7U;
    request->instructions[1].operands[1] = 2U;
    request->instructions[2].operands[1] = std::bit_cast<std::uint32_t>(-40);
    request->instructions[2].operands[2] = std::bit_cast<std::uint32_t>(150);
    request->instructions[3].operands[1] = std::bit_cast<std::uint32_t>(44);

    const auto snapshot = decode_rm_thermal_sensor_snapshot(*request, 3U);
    require(snapshot.has_value(), "complete snapshot response must decode");
    require(snapshot->provider_index == 7U, "provider index must decode");
    require(snapshot->target_index == 2U, "target index must decode");
    require(snapshot->minimum == -40, "signed minimum must decode");
    require(snapshot->maximum == 150, "signed maximum must decode");
    require(snapshot->reading == 44, "signed reading must decode");

    request->instructions[3].executed = 0U;
    require(
        !decode_rm_thermal_sensor_snapshot(*request, 3U).has_value(),
        "partial execution must fail closed");
}

void test_type_queries()
{
    using namespace rtxmon::lab;
    auto provider = make_rm_thermal_provider_type_request(5U);
    mark_success(provider);
    provider.instructions[0].operands[1] = 9U;
    require(
        decode_rm_thermal_provider_type(provider, 5U) == 9U,
        "provider type must decode for the requested index");
    require(
        !decode_rm_thermal_provider_type(provider, 4U).has_value(),
        "provider index mismatch must fail closed");

    auto target = make_rm_thermal_target_type_request(2U);
    mark_success(target);
    target.instructions[0].operands[1] =
        static_cast<std::uint32_t>(RmThermalTarget::gpu);
    require(
        decode_rm_thermal_target_type(target, 2U) == RmThermalTarget::gpu,
        "known target type must decode");
    target.instructions[0].operands[1] = 0x12345678U;
    require(
        !decode_rm_thermal_target_type(target, 2U).has_value(),
        "unknown target type must fail closed");
}

} // namespace

int main()
{
    try {
        test_layout_and_constants();
        test_sensor_count_request_and_response();
        test_sensor_snapshot_round_trip();
        test_type_queries();
        std::cout << "RM thermal protocol tests passed\n";
        return 0;
    } catch (const std::exception &error) {
        std::cerr << "RM thermal protocol tests failed: " << error.what() << '\n';
        return 1;
    }
}
