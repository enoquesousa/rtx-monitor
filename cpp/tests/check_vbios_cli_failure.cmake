if(NOT DEFINED RTXMON_VBIOS_CLI OR NOT DEFINED RTXMON_VBIOS_INPUT)
    message(FATAL_ERROR "RTXMON_VBIOS_CLI and RTXMON_VBIOS_INPUT are required")
endif()

if(NOT DEFINED RTXMON_VBIOS_EXPECTED_DIAGNOSTIC)
    set(RTXMON_VBIOS_EXPECTED_DIAGNOSTIC "input_too_large")
endif()

execute_process(
    COMMAND "${RTXMON_VBIOS_CLI}" "${RTXMON_VBIOS_INPUT}"
    RESULT_VARIABLE cli_result
    OUTPUT_VARIABLE cli_output
    ERROR_VARIABLE cli_error
)

if(NOT cli_result EQUAL 3)
    message(FATAL_ERROR
        "Expected rtxmon-vbios exit 3, got ${cli_result}. stderr: ${cli_error}")
endif()

string(FIND
    "${cli_output}"
    "\"code\": \"${RTXMON_VBIOS_EXPECTED_DIAGNOSTIC}\""
    diagnostic_position)
if(diagnostic_position EQUAL -1)
    message(FATAL_ERROR
        "Expected ${RTXMON_VBIOS_EXPECTED_DIAGNOSTIC} JSON diagnostic. output: ${cli_output}")
endif()
