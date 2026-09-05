cmake_minimum_required(VERSION 3.25)

project(
    rtx-monitor
    VERSION 0.9.0
    DESCRIPTION "Read-only NVIDIA RTX thermal monitor and capability inventory"
    LANGUAGES C CXX
)

option(RTXMON_BUILD_TESTS "Build tests" ON)
option(RTXMON_ENABLE_GPU_TESTS "Enable tests that require an NVIDIA GPU and driver" OFF)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_C_EXTENSIONS OFF)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

set(CMAKE_RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib")

add_library(rtxmon_native SHARED
    cpp/src/metrics.cpp
    native/src/nvapi_loader.c
    native/src/nvml_loader.c
    native/src/private_module_profile.c
    native/src/private_profile_catalog.c
    native/src/private_profile_gate.c
    native/src/private_acquisition.c
    native/src/public_telemetry.c
    native/src/private_thermal.c
    native/src/private_voltage.c
    native/src/rtxmon.c
    native/src/thermal_scan.c
)

target_include_directories(rtxmon_native
    PUBLIC
        $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/native/include>
        $<INSTALL_INTERFACE:include>
    PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}/native/src
)

target_compile_definitions(rtxmon_native
    PRIVATE
        RTXMON_BUILDING_NATIVE
        $<$<PLATFORM_ID:Windows>:WIN32_LEAN_AND_MEAN;NOMINMAX;_WIN32_WINNT=0x0A00>
)

if(WIN32)
    target_link_libraries(rtxmon_native PRIVATE bcrypt)
elseif(UNIX)
    target_link_libraries(rtxmon_native PRIVATE ${CMAKE_DL_LIBS})
else()
    message(FATAL_ERROR "rtx-monitor currently supports Windows and Linux")
endif()

add_library(rtxmon_core STATIC
    cpp/src/alerts.cpp
    cpp/src/metrics_wrapper.cpp
    cpp/src/monitor.cpp
    cpp/src/sampler.cpp
)

target_include_directories(rtxmon_core
    PUBLIC
        ${CMAKE_CURRENT_SOURCE_DIR}/cpp/include
)

target_link_libraries(rtxmon_core PUBLIC rtxmon_native)

add_library(rtxmon_lab STATIC
    cpp/src/lab/rm_thermal_protocol.cpp
    cpp/src/lab/vbios_json.cpp
    cpp/src/lab/vbios_parser.cpp
)

target_include_directories(rtxmon_lab
    PUBLIC
        $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/cpp/include>
        $<INSTALL_INTERFACE:include>
)

add_executable(rtxmon_cli
    cpp/cli/main.cpp
)
set_target_properties(rtxmon_cli PROPERTIES OUTPUT_NAME rtxmon)
target_link_libraries(rtxmon_cli PRIVATE rtxmon_core)

add_executable(rtxmon_c_example
    examples/c/temperature_once.c
)
set_target_properties(rtxmon_c_example PROPERTIES OUTPUT_NAME rtxmon-c)
target_link_libraries(rtxmon_c_example PRIVATE rtxmon_native)

add_executable(rtxmon_vbios_cli
    cpp/lab/main.cpp
)
set_target_properties(rtxmon_vbios_cli PROPERTIES OUTPUT_NAME rtxmon-vbios)
target_link_libraries(rtxmon_vbios_cli PRIVATE rtxmon_lab)

if(MSVC)
    foreach(target rtxmon_native rtxmon_core rtxmon_lab rtxmon_cli rtxmon_c_example rtxmon_vbios_cli)
        target_compile_options(${target} PRIVATE /W4 /WX /utf-8)
    endforeach()
    target_compile_options(rtxmon_core PRIVATE /permissive-)
    target_compile_options(rtxmon_lab PRIVATE /permissive-)
    target_compile_options(rtxmon_cli PRIVATE /permissive-)
    target_compile_options(rtxmon_vbios_cli PRIVATE /permissive-)
else()
    foreach(target rtxmon_native rtxmon_core rtxmon_lab rtxmon_cli rtxmon_c_example rtxmon_vbios_cli)
        target_compile_options(${target} PRIVATE -Wall -Wextra -Werror)
    endforeach()
endif()

if(RTXMON_BUILD_TESTS)
    include(CTest)

    add_executable(rtxmon_abi_tests
        native/tests/abi_tests.c
    )
    target_link_libraries(rtxmon_abi_tests PRIVATE rtxmon_native)

    if(MSVC)
        target_compile_options(rtxmon_abi_tests PRIVATE /W4 /WX /utf-8)
    else()
        target_compile_options(rtxmon_abi_tests PRIVATE -Wall -Wextra -Werror)
    endif()

    add_test(NAME rtxmon_abi COMMAND rtxmon_abi_tests)
    set_tests_properties(rtxmon_abi PROPERTIES LABELS "unit;abi;no-gpu")

    add_executable(rtxmon_private_profile_tests
        native/src/private_profile_catalog.c
        native/tests/private_profile_tests.c
    )
    target_include_directories(rtxmon_private_profile_tests PRIVATE native/src)
    if(MSVC)
        target_compile_options(rtxmon_private_profile_tests PRIVATE /W4 /WX /utf-8)
    else()
        target_compile_options(rtxmon_private_profile_tests PRIVATE -Wall -Wextra -Werror)
    endif()
    add_test(NAME rtxmon_private_profile COMMAND rtxmon_private_profile_tests)
    set_tests_properties(rtxmon_private_profile PROPERTIES LABELS "unit;profile;no-gpu")

    # Offline snapshot of the compiled allowlist; never links a GPU backend.
    add_executable(rtxmon_private_catalog_snapshot
        native/src/private_profile_catalog.c
        native/tests/private_catalog_snapshot.c
    )
    target_include_directories(rtxmon_private_catalog_snapshot PRIVATE native/src native/include)
    if(MSVC)
        target_compile_options(rtxmon_private_catalog_snapshot PRIVATE /W4 /WX /utf-8)
    else()
        target_compile_options(rtxmon_private_catalog_snapshot PRIVATE -Wall -Wextra -Werror)
    endif()

    foreach(policy_variant active profile_revoked thermal_revoked voltage_revoked)
        set(policy_target rtxmon_private_policy_${policy_variant}_tests)
        add_executable(${policy_target}
            native/src/private_profile_catalog.c
            native/src/private_profile_gate.c
            native/src/private_acquisition.c
            native/src/private_thermal.c
            native/src/private_voltage.c
            native/tests/private_policy_tests.c
        )
        target_include_directories(${policy_target} PRIVATE native/include native/src)
        target_compile_definitions(${policy_target} PRIVATE
            RTXMON_BUILDING_NATIVE RTXMON_PRIVATE_PROFILE_TESTING
            RTXMON_TEST_PROFILE_REVOKED=$<IF:$<STREQUAL:${policy_variant},profile_revoked>,1,0>
            RTXMON_TEST_THERMAL_REVOKED=$<IF:$<STREQUAL:${policy_variant},thermal_revoked>,1,0>
            RTXMON_TEST_VOLTAGE_REVOKED=$<IF:$<STREQUAL:${policy_variant},voltage_revoked>,1,0>
        )
        if(MSVC)
            target_compile_options(${policy_target} PRIVATE /W4 /WX /utf-8)
        else()
            target_compile_options(${policy_target} PRIVATE -Wall -Wextra -Werror)
        endif()
        add_test(NAME rtxmon_private_policy_${policy_variant} COMMAND ${policy_target})
        set_tests_properties(rtxmon_private_policy_${policy_variant}
            PROPERTIES LABELS "unit;profile;security;no-gpu")
    endforeach()

    foreach(acquisition_case rate_fence identity_timeout association_timeout thermal_first_timeout
        thermal_second_timeout voltage_timeout total_timeout lock_timeout lock_acquired_late
        publication_timeout clock_unavailable admission_clock_unavailable admission_clock_regression
        admission_clock_deadline)
        add_test(NAME rtxmon_private_acquisition_${acquisition_case}
            COMMAND rtxmon_private_policy_active_tests ${acquisition_case})
        set_tests_properties(rtxmon_private_acquisition_${acquisition_case}
            PROPERTIES LABELS "unit;profile;acquisition;no-gpu")
    endforeach()

    add_executable(rtxmon_private_module_profile_tests
        native/src/private_module_profile.c
        native/tests/private_module_profile_tests.c
    )
    target_include_directories(rtxmon_private_module_profile_tests PRIVATE native/src)
    if(MSVC)
        target_compile_options(rtxmon_private_module_profile_tests PRIVATE /W4 /WX /utf-8)
        target_link_libraries(rtxmon_private_module_profile_tests PRIVATE bcrypt)
    else()
        target_compile_options(rtxmon_private_module_profile_tests PRIVATE -Wall -Wextra -Werror)
    endif()
    add_test(NAME rtxmon_private_module_profile COMMAND rtxmon_private_module_profile_tests)
    set_tests_properties(rtxmon_private_module_profile PROPERTIES LABELS "unit;profile;security;no-gpu")

    add_executable(rtxmon_sampler_tests
        cpp/tests/sampler_tests.cpp
    )
    target_link_libraries(rtxmon_sampler_tests PRIVATE rtxmon_core)

    if(MSVC)
        target_compile_options(rtxmon_sampler_tests PRIVATE /W4 /WX /permissive- /utf-8)
    else()
        target_compile_options(rtxmon_sampler_tests PRIVATE -Wall -Wextra -Werror)
    endif()

    add_test(NAME rtxmon_sampler COMMAND rtxmon_sampler_tests)
    set_tests_properties(rtxmon_sampler PROPERTIES LABELS "unit;sampler;no-gpu")

    add_executable(rtxmon_alerts_tests
        cpp/tests/alerts_tests.cpp
    )
    target_link_libraries(rtxmon_alerts_tests PRIVATE rtxmon_core)

    if(MSVC)
        target_compile_options(rtxmon_alerts_tests PRIVATE /W4 /WX /permissive- /utf-8)
    else()
        target_compile_options(rtxmon_alerts_tests PRIVATE -Wall -Wextra -Werror)
    endif()

    add_test(NAME rtxmon_alerts COMMAND rtxmon_alerts_tests)
    set_tests_properties(rtxmon_alerts PROPERTIES LABELS "unit;alerts;no-gpu")

    add_executable(rtxmon_metrics_tests
        cpp/tests/metrics_tests.cpp
    )
    target_link_libraries(rtxmon_metrics_tests PRIVATE rtxmon_core)

    if(MSVC)
        target_compile_options(rtxmon_metrics_tests PRIVATE /W4 /WX /permissive- /utf-8)
    else()
        target_compile_options(rtxmon_metrics_tests PRIVATE -Wall -Wextra -Werror)
    endif()

    add_test(NAME rtxmon_metrics COMMAND rtxmon_metrics_tests)
    set_tests_properties(rtxmon_metrics PROPERTIES LABELS "unit;metrics;no-gpu")

    add_executable(rtxmon_vbios_parser_tests
        cpp/tests/vbios_parser_tests.cpp
    )
    target_link_libraries(rtxmon_vbios_parser_tests PRIVATE rtxmon_lab)

    if(MSVC)
        target_compile_options(rtxmon_vbios_parser_tests PRIVATE /W4 /WX /permissive- /utf-8)
    else()
        target_compile_options(rtxmon_vbios_parser_tests PRIVATE -Wall -Wextra -Werror)
    endif()

    add_test(NAME rtxmon_vbios_parser COMMAND rtxmon_vbios_parser_tests)
    set_tests_properties(rtxmon_vbios_parser PROPERTIES LABELS "unit;vbios;lab;no-gpu")

    add_executable(rtxmon_rm_thermal_protocol_tests
        cpp/tests/rm_thermal_protocol_tests.cpp
    )
    target_link_libraries(rtxmon_rm_thermal_protocol_tests PRIVATE rtxmon_lab)

    if(MSVC)
        target_compile_options(rtxmon_rm_thermal_protocol_tests PRIVATE /W4 /WX /permissive- /utf-8)
    else()
        target_compile_options(rtxmon_rm_thermal_protocol_tests PRIVATE -Wall -Wextra -Werror)
    endif()

    add_test(NAME rtxmon_rm_thermal_protocol COMMAND rtxmon_rm_thermal_protocol_tests)
    set_tests_properties(
        rtxmon_rm_thermal_protocol
        PROPERTIES LABELS "unit;rm-thermal;lab;no-gpu"
    )

    if(WIN32)
        set(RTXMON_VBIOS_TEST_FIXTURE
            "${CMAKE_CURRENT_BINARY_DIR}/$<CONFIG>/synthetic-vbios-test.rom")
        add_test(
            NAME rtxmon_vbios_fixture_setup
            COMMAND rtxmon_vbios_parser_tests --write-fixture "${RTXMON_VBIOS_TEST_FIXTURE}"
        )
        set_tests_properties(rtxmon_vbios_fixture_setup PROPERTIES
            FIXTURES_SETUP rtxmon_vbios_fixture
            LABELS "fixture;vbios;lab;no-gpu"
        )

        add_test(NAME rtxmon_vbios_cli_parse COMMAND rtxmon_vbios_cli "${RTXMON_VBIOS_TEST_FIXTURE}")
        set_tests_properties(rtxmon_vbios_cli_parse PROPERTIES
            FIXTURES_REQUIRED rtxmon_vbios_fixture
            LABELS "integration;vbios;lab;no-gpu"
            PASS_REGULAR_EXPRESSION "\"status\": \"success\""
        )

        set(RTXMON_VBIOS_OVERSIZED_FIXTURE
            "${CMAKE_CURRENT_BINARY_DIR}/$<CONFIG>/synthetic-vbios-oversized-test.rom")
        add_test(
            NAME rtxmon_vbios_oversized_fixture_setup
            COMMAND rtxmon_vbios_parser_tests
                --write-oversized-fixture "${RTXMON_VBIOS_OVERSIZED_FIXTURE}"
        )
        set_tests_properties(rtxmon_vbios_oversized_fixture_setup PROPERTIES
            FIXTURES_SETUP rtxmon_vbios_oversized_fixture
            LABELS "fixture;security;vbios;lab;no-gpu"
        )

        add_test(
            NAME rtxmon_vbios_cli_rejects_oversized
            COMMAND ${CMAKE_COMMAND}
                "-DRTXMON_VBIOS_CLI=$<TARGET_FILE:rtxmon_vbios_cli>"
                "-DRTXMON_VBIOS_INPUT=${RTXMON_VBIOS_OVERSIZED_FIXTURE}"
                -P "${CMAKE_CURRENT_SOURCE_DIR}/cpp/tests/check_vbios_cli_failure.cmake"
        )
        set_tests_properties(rtxmon_vbios_cli_rejects_oversized PROPERTIES
            FIXTURES_REQUIRED rtxmon_vbios_oversized_fixture
            LABELS "integration;security;vbios;lab;no-gpu"
        )

        add_test(
            NAME rtxmon_vbios_cli_rejects_device_path
            COMMAND ${CMAKE_COMMAND}
                "-DRTXMON_VBIOS_CLI=$<TARGET_FILE:rtxmon_vbios_cli>"
                "-DRTXMON_VBIOS_INPUT=NUL"
                "-DRTXMON_VBIOS_EXPECTED_DIAGNOSTIC=path_not_local_regular"
                -P "${CMAKE_CURRENT_SOURCE_DIR}/cpp/tests/check_vbios_cli_failure.cmake"
        )
        set_tests_properties(rtxmon_vbios_cli_rejects_device_path PROPERTIES
            LABELS "integration;security;vbios;lab;no-gpu"
        )
    else()
        add_test(
            NAME rtxmon_vbios_cli_rejects_unsupported_platform
            COMMAND ${CMAKE_COMMAND}
                "-DRTXMON_VBIOS_CLI=$<TARGET_FILE:rtxmon_vbios_cli>"
                "-DRTXMON_VBIOS_INPUT=rtxmon-vbios-path-must-not-be-inspected"
                "-DRTXMON_VBIOS_EXPECTED_DIAGNOSTIC=unsupported_platform"
                -P "${CMAKE_CURRENT_SOURCE_DIR}/cpp/tests/check_vbios_cli_failure.cmake"
        )
        set_tests_properties(rtxmon_vbios_cli_rejects_unsupported_platform PROPERTIES
            LABELS "integration;security;vbios;lab;no-gpu"
        )
    endif()

    add_test(NAME rtxmon_vbios_cli_help COMMAND rtxmon_vbios_cli --help)
    set_tests_properties(rtxmon_vbios_cli_help PROPERTIES LABELS "smoke;vbios;lab;no-gpu")

    if(RTXMON_ENABLE_GPU_TESTS)
        add_test(NAME rtxmon_gpu_smoke COMMAND rtxmon_cli --once --json)
        set_tests_properties(rtxmon_gpu_smoke PROPERTIES LABELS "gpu;integration")
        add_test(NAME rtxmon_capability_smoke COMMAND rtxmon_cli --capabilities --json)
        set_tests_properties(rtxmon_capability_smoke PROPERTIES LABELS "gpu;integration;capabilities")
    endif()
endif()

include(GNUInstallDirs)
install(TARGETS rtxmon_native rtxmon_core rtxmon_lab rtxmon_cli rtxmon_c_example rtxmon_vbios_cli
    RUNTIME DESTINATION ${CMAKE_INSTALL_BINDIR}
    LIBRARY DESTINATION ${CMAKE_INSTALL_LIBDIR}
    ARCHIVE DESTINATION ${CMAKE_INSTALL_LIBDIR}
)
install(DIRECTORY native/include/ DESTINATION ${CMAKE_INSTALL_INCLUDEDIR})
install(DIRECTORY cpp/include/ DESTINATION ${CMAKE_INSTALL_INCLUDEDIR})
