cmake_minimum_required(VERSION 3.25)

project(
    rtx-monitor
    VERSION 0.1.0
    DESCRIPTION "Read-only NVIDIA RTX die temperature monitor"
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
    native/src/nvml_loader.c
    native/src/rtxmon.c
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
    # The loader uses an absolute System32/NVSMI path and needs no nvml.lib.
elseif(UNIX)
    target_link_libraries(rtxmon_native PRIVATE ${CMAKE_DL_LIBS})
else()
    message(FATAL_ERROR "rtx-monitor currently supports Windows and Linux")
endif()

add_library(rtxmon_core STATIC
    cpp/src/monitor.cpp
)

target_include_directories(rtxmon_core
    PUBLIC
        ${CMAKE_CURRENT_SOURCE_DIR}/cpp/include
)

target_link_libraries(rtxmon_core PUBLIC rtxmon_native)

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

if(MSVC)
    foreach(target rtxmon_native rtxmon_core rtxmon_cli rtxmon_c_example)
        target_compile_options(${target} PRIVATE /W4 /WX /utf-8)
    endforeach()
    target_compile_options(rtxmon_core PRIVATE /permissive-)
    target_compile_options(rtxmon_cli PRIVATE /permissive-)
else()
    foreach(target rtxmon_native rtxmon_core rtxmon_cli rtxmon_c_example)
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

    if(RTXMON_ENABLE_GPU_TESTS)
        add_test(NAME rtxmon_gpu_smoke COMMAND rtxmon_cli --once --json)
        set_tests_properties(rtxmon_gpu_smoke PROPERTIES LABELS "gpu;integration")
    endif()
endif()

include(GNUInstallDirs)
install(TARGETS rtxmon_native rtxmon_core rtxmon_cli rtxmon_c_example
    RUNTIME DESTINATION ${CMAKE_INSTALL_BINDIR}
    LIBRARY DESTINATION ${CMAKE_INSTALL_LIBDIR}
    ARCHIVE DESTINATION ${CMAKE_INSTALL_LIBDIR}
)
install(DIRECTORY native/include/ DESTINATION ${CMAKE_INSTALL_INCLUDEDIR})
install(DIRECTORY cpp/include/ DESTINATION ${CMAKE_INSTALL_INCLUDEDIR})
