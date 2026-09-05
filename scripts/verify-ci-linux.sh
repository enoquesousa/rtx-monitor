#!/usr/bin/env bash
set -euo pipefail

# Portability and deterministic offline tests only. This does not acquire NVAPI
# channels or validate physical readings or profiles on another GPU.
configuration="${1:-Release}"
if (( $# > 1 )) || [[ "$configuration" != Release && "$configuration" != Debug ]]; then
    printf 'Usage: bash scripts/verify-ci-linux.sh [Release|Debug]\n' >&2
    exit 2
fi
if [[ "$(uname -s)" != Linux || "$(uname -m)" != x86_64 ]]; then
    printf 'This CI script requires Linux x86_64.\n' >&2
    exit 2
fi
for tool in cmake ninja dotnet python3 timeout; do
    if ! command -v "$tool" >/dev/null 2>&1; then
        printf 'Required tool is missing: %s\n' "$tool" >&2
        exit 2
    fi
done

project_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
build_directory="$project_root/build/linux-x64/${configuration,,}"
native_directory="$build_directory/bin"

# CMakeLists.txt applies -Wall -Wextra -Werror to every native target and test.
cmake -S "$project_root" -B "$build_directory" -G Ninja \
    -DCMAKE_BUILD_TYPE="$configuration" \
    -DRTXMON_BUILD_TESTS=ON \
    -DRTXMON_ENABLE_GPU_TESTS=OFF
cmake --build "$build_directory" --parallel "${CMAKE_BUILD_PARALLEL_LEVEL:-2}"
ctest --test-dir "$build_directory" --output-on-failure \
    --no-tests=error --timeout 60 -L no-gpu

"$native_directory/rtxmon_private_catalog_snapshot" > "$build_directory/private-catalog-snapshot.json"
python3 "$project_root/scripts/verify-private-profile.py" \
    --snapshot "$build_directory/private-catalog-snapshot.json"
python3 -m unittest discover -s "$project_root/scripts/tests" -p 'test_private_profile_audit.py'

# The existing project copy target is Windows-only. Linux P/Invoke and optional
# export probes resolve the freshly built shared library through the loader.
if [[ ! -f "$native_directory/librtxmon_native.so" ]]; then
    printf 'The native Linux library was not produced.\n' >&2
    exit 1
fi
export LD_LIBRARY_PATH="$native_directory${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

for suite in Managed Storage Console Lab; do
    project="$project_root/csharp/RtxMonitor.$suite.Tests/RtxMonitor.$suite.Tests.csproj"
    assembly="$project_root/csharp/RtxMonitor.$suite.Tests/bin/$configuration/net8.0/RtxMonitor.$suite.Tests.dll"
    dotnet build "$project" --configuration "$configuration" \
        --nologo --warnaserror -p:NativeLibraryDir="$native_directory"
    # Bound the suite itself as well as the watchdog's individual fake workers.
    timeout --kill-after=5s 180s dotnet "$assembly"
done

# Help paths must remain usable without initializing a GPU backend.
timeout --kill-after=5s 30s "$native_directory/rtxmon" --help >/dev/null
timeout --kill-after=5s 30s "$native_directory/rtxmon-vbios" --help >/dev/null
timeout --kill-after=5s 30s dotnet \
    "$project_root/csharp/RtxMonitor.Console/bin/$configuration/net8.0/RtxMonitor.Console.dll" \
    --help >/dev/null
timeout --kill-after=5s 30s dotnet \
    "$project_root/csharp/RtxMonitor.Lab/bin/$configuration/net8.0/rtxmon-lab.dll" \
    --help >/dev/null

printf 'Linux portability CI passed: native no-GPU tests, Managed, Storage, Console fake workers, and Lab unsupported-platform guard.\n'
