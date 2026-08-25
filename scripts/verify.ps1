[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$configurationLower = $Configuration.ToLowerInvariant()
$nativeOutput = Join-Path $projectRoot "build\windows-x64\bin\$Configuration"
$cppExecutable = Join-Path $nativeOutput 'rtxmon.exe'
$cExecutable = Join-Path $nativeOutput 'rtxmon-c.exe'
$csharpExecutable = Join-Path $projectRoot "csharp\RtxMonitor.Console\bin\$Configuration\net8.0\RtxMonitor.Console.exe"
$capabilitySchemaPath = Join-Path $projectRoot 'docs\schema\capabilities-v2.schema.json'
$eventSchemaV1Path = Join-Path $projectRoot 'docs\schema\telemetry-event-v1.schema.json'
$eventSchemaPath = Join-Path $projectRoot 'docs\schema\telemetry-event-v2.schema.json'

function Assert-LastExitCode {
    param([Parameter(Mandatory)][string]$Description)

    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

function Assert-Temperature {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][int]$Temperature
    )

    if ($Temperature -lt -50 -or $Temperature -gt 150) {
        throw "$Source returned an implausible GPU temperature: $Temperature C."
    }
}

function Assert-EventStream {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][object[]]$Events,
        [Parameter(Mandatory)][string]$GpuUuid
    )

    if ($Events.Count -ne 2) {
        throw "$Source produced $($Events.Count) events instead of 2."
    }

    for ($index = 0; $index -lt $Events.Count; $index++) {
        $event = $Events[$index]
        if ($event.sequence -ne ($index + 1)) {
            throw "$Source did not emit one contiguous global sequence."
        }
        if ($event.schema_version -ne 2 -or $event.event_type -ne 'sample') {
            throw "$Source emitted an unexpected event envelope."
        }
        if ($event.target_gpu_uuid -ne $GpuUuid -or
            $event.sample.sensor -ne 'gpu_die' -or
            $null -eq $event.sample.temperature_c) {
            throw "$Source did not preserve the selected GPU and sensor reading."
        }
    }
}

function Assert-AlertStream {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][object[]]$Events,
        [Parameter(Mandatory)][string]$GpuUuid
    )

    if ($Events.Count -ne 2 -or
        $Events[0].event_type -ne 'sample' -or
        $Events[1].event_type -ne 'alert_raised') {
        throw "$Source did not emit exactly one sample followed by one alert."
    }
    for ($index = 0; $index -lt $Events.Count; $index++) {
        if ($Events[$index].sequence -ne ($index + 1)) {
            throw "$Source did not emit one contiguous global sequence."
        }
    }

    $alerts = @($Events | Where-Object { $_.event_type -eq 'alert_raised' })
    foreach ($alert in $alerts) {
        if ($alert.schema_version -ne 2 -or
            $alert.target_gpu_uuid -ne $GpuUuid -or
            $alert.alert_threshold_c -ne 0 -or
            $null -eq $alert.sample -or
            $null -eq $alert.sample.temperature_c) {
            throw "$Source emitted an unexpected alert_raised envelope."
        }
    }
}

Push-Location -LiteralPath $projectRoot
try {
    if (-not $SkipBuild) {
        & (Join-Path $PSScriptRoot 'verify-ci.ps1') -Configuration $Configuration
    }

    $capabilitySchema = Get-Content -Raw -LiteralPath $capabilitySchemaPath | ConvertFrom-Json
    if ($capabilitySchema.properties.schema_version.const -ne 2) {
        throw 'The capability JSON Schema is missing schema_version const 2.'
    }

    $eventSchemaV1 = Get-Content -Raw -LiteralPath $eventSchemaV1Path | ConvertFrom-Json
    if ($eventSchemaV1.properties.schema_version.const -ne 1) {
        throw 'The telemetry event JSON Schema v1 is missing or no longer declares schema_version const 1.'
    }

    $eventSchema = Get-Content -Raw -LiteralPath $eventSchemaPath | ConvertFrom-Json
    if ($eventSchema.properties.schema_version.const -ne 2) {
        throw 'The telemetry event JSON Schema is missing schema_version const 2.'
    }

    & ctest --preset "windows-x64-$configurationLower"
    Assert-LastExitCode -Description 'CTest'

    $cOutput = (& $cExecutable | Out-String)
    Assert-LastExitCode -Description 'C example'
    if ($cOutput -notmatch 'GPU die temperature:\s*(-?\d+) C') {
        throw 'The C example did not expose a GPU die temperature.'
    }
    $cTemperature = [int]$Matches[1]

    $cppOutput = (& $cppExecutable --once --json | Out-String).Trim()
    Assert-LastExitCode -Description 'C++ CLI'
    $cppSample = $cppOutput | ConvertFrom-Json

    $csharpOutput = (& $csharpExecutable --once --json | Out-String).Trim()
    Assert-LastExitCode -Description 'C# monitor'
    $csharpSample = $csharpOutput | ConvertFrom-Json

    $cppCapabilitiesOutput = (& $cppExecutable --capabilities --json | Out-String).Trim()
    Assert-LastExitCode -Description 'C++ capability inventory'
    $cppCapabilities = $cppCapabilitiesOutput | ConvertFrom-Json

    $csharpCapabilitiesOutput = (& $csharpExecutable --capabilities --json | Out-String).Trim()
    Assert-LastExitCode -Description 'C# capability inventory'
    $csharpCapabilities = $csharpCapabilitiesOutput | ConvertFrom-Json

    $smiOutput = (& nvidia-smi `
        --query-gpu=index,uuid,temperature.gpu `
        --format=csv,noheader,nounits | Select-Object -First 1)
    Assert-LastExitCode -Description 'nvidia-smi reference query'
    $smiFields = $smiOutput -split ',' | ForEach-Object { $_.Trim() }
    if ($smiFields.Count -ne 3) {
        throw "Unexpected nvidia-smi output: $smiOutput"
    }
    $smiTemperature = [int]$smiFields[2]

    Assert-Temperature -Source 'C' -Temperature $cTemperature
    Assert-Temperature -Source 'C++' -Temperature ([int]$cppSample.temperature_c)
    Assert-Temperature -Source 'C#' -Temperature ([int]$csharpSample.temperature_c)
    Assert-Temperature -Source 'nvidia-smi' -Temperature $smiTemperature

    if ($cppSample.gpu_uuid -ne $csharpSample.gpu_uuid -or $cppSample.gpu_uuid -ne $smiFields[1]) {
        throw 'C++, C#, and nvidia-smi did not address the same GPU UUID.'
    }

    if ($cppSample.sensor -ne 'gpu_die' -or $csharpSample.sensor -ne 'gpu_die') {
        throw 'A consumer did not identify the reading as the GPU die sensor.'
    }

    if ($cppCapabilities.schema_version -ne 2 -or $csharpCapabilities.schema_version -ne 2) {
        throw 'A capability consumer did not emit schema version 2.'
    }

    if ($cppCapabilities.gpu.uuid -ne $cppSample.gpu_uuid -or
        $csharpCapabilities.gpu.uuid -ne $cppSample.gpu_uuid) {
        throw 'A capability inventory addressed a different GPU UUID.'
    }

    foreach ($property in @('name', 'uuid', 'driver_version', 'nvml_version')) {
        if ($cppCapabilities.gpu.$property -ne $csharpCapabilities.gpu.$property) {
            throw "C++ and C# GPU metadata differ at '$property'."
        }
    }

    $boardProperties = @(
        'pci_bus_id',
        'pci_vendor_id',
        'pci_device_id',
        'pci_subsystem_vendor_id',
        'pci_subsystem_device_id',
        'vbios_version',
        'profile_key'
    )
    foreach ($property in $boardProperties) {
        if ($cppCapabilities.board.$property -ne $csharpCapabilities.board.$property) {
            throw "C++ and C# board identity differ at '$property'."
        }
    }

    if (-not $cppCapabilities.board.pci_identity_available -or
        -not $csharpCapabilities.board.pci_identity_available) {
        throw 'PCI board identity was not reported as available.'
    }

    if ($cppCapabilities.providers.Count -ne 3 -or
        $csharpCapabilities.providers.Count -ne 3) {
        throw 'The capability inventory must expose all three public providers.'
    }

    if ($cppCapabilities.thermal_capabilities.Count -ne
        $csharpCapabilities.thermal_capabilities.Count) {
        throw 'C++ and C# returned different thermal capability counts.'
    }

    for ($index = 0; $index -lt $cppCapabilities.providers.Count; $index++) {
        $cppProvider = $cppCapabilities.providers[$index]
        $csharpProvider = $csharpCapabilities.providers[$index]
        foreach ($property in @('provider', 'state', 'native_status', 'capability_count')) {
            if ($cppProvider.$property -ne $csharpProvider.$property) {
                throw "C++ and C# provider $index differ at '$property'."
            }
        }
    }

    for ($index = 0; $index -lt $cppCapabilities.thermal_capabilities.Count; $index++) {
        $cppSensor = $cppCapabilities.thermal_capabilities[$index]
        $csharpSensor = $csharpCapabilities.thermal_capabilities[$index]
        foreach ($property in @(
            'provider',
            'provider_native_id',
            'target',
            'controller',
            'state',
            'confidence',
            'native_status'
        )) {
            if ($cppSensor.$property -ne $csharpSensor.$property) {
                throw "C++ and C# thermal capability $index differ at '$property'."
            }
        }

        if ($null -ne $cppSensor.current_temperature_c -and
            $null -ne $csharpSensor.current_temperature_c -and
            [Math]::Abs(
                [int]$cppSensor.current_temperature_c -
                [int]$csharpSensor.current_temperature_c) -gt 5) {
            throw "C++ and C# thermal capability $index differ by more than 5 C."
        }
    }

    $memoryCapabilities = @(
        $cppCapabilities.thermal_capabilities | Where-Object { $_.target -eq 'memory' }
    )
    if ($memoryCapabilities.Count -ne 1) {
        throw 'The NVML memory-temperature field must have one explicit capability record.'
    }

    if ($memoryCapabilities[0].state -notin @(
        'available',
        'not_supported',
        'provider_unavailable',
        'query_failed'
    )) {
        throw "Unexpected memory-temperature state: $($memoryCapabilities[0].state)."
    }

    $temperatures = @(
        $cTemperature,
        [int]$cppSample.temperature_c,
        [int]$csharpSample.temperature_c,
        $smiTemperature
    )
    $minimum = ($temperatures | Measure-Object -Minimum).Minimum
    $maximum = ($temperatures | Measure-Object -Maximum).Maximum
    if (($maximum - $minimum) -gt 5) {
        throw "Sequential readers differed by more than 5 C: $($temperatures -join ', ')."
    }

    $cppWatch = @(& $cppExecutable --watch --count 2 --interval 100 --json)
    Assert-LastExitCode -Description 'C++ watch mode'
    if ($cppWatch.Count -ne 2) {
        throw "C++ watch mode produced $($cppWatch.Count) samples instead of 2."
    }

    $csharpWatch = @(& $csharpExecutable --watch --count 2 --interval 100 --json)
    Assert-LastExitCode -Description 'C# watch mode'
    if ($csharpWatch.Count -ne 2) {
        throw "C# watch mode produced $($csharpWatch.Count) samples instead of 2."
    }

    $cppUuidSample = (& $cppExecutable `
        --once `
        --gpu-uuid $cppSample.gpu_uuid `
        --json | Out-String).Trim() | ConvertFrom-Json
    Assert-LastExitCode -Description 'C++ UUID selection'

    $csharpUuidSample = (& $csharpExecutable `
        --once `
        --gpu-uuid ($cppSample.gpu_uuid.ToLowerInvariant()) `
        --json | Out-String).Trim() | ConvertFrom-Json
    Assert-LastExitCode -Description 'C# UUID selection'

    if ($cppUuidSample.gpu_uuid -ne $cppSample.gpu_uuid -or
        $csharpUuidSample.gpu_uuid -ne $cppSample.gpu_uuid) {
        throw 'UUID selection did not resolve the expected GPU.'
    }

    $cppEvents = @(& $cppExecutable --watch --count 2 --interval 100 --events) |
        ForEach-Object { $_ | ConvertFrom-Json }
    Assert-LastExitCode -Description 'C++ resilient event stream'

    $csharpEvents = @(& $csharpExecutable --watch --count 2 --interval 100 --events) |
        ForEach-Object { $_ | ConvertFrom-Json }
    Assert-LastExitCode -Description 'C# resilient event stream'

    Assert-EventStream -Source 'C++ resilient stream' -Events $cppEvents -GpuUuid $cppSample.gpu_uuid
    Assert-EventStream -Source 'C# resilient stream' -Events $csharpEvents -GpuUuid $cppSample.gpu_uuid

    $cppAlertEvents = @(& $cppExecutable --watch --count 1 --interval 100 --events --alert-threshold 0) |
        ForEach-Object { $_ | ConvertFrom-Json }
    Assert-LastExitCode -Description 'C++ alert stream'

    $csharpAlertEvents = @(& $csharpExecutable --watch --count 1 --interval 100 --events --alert-threshold 0) |
        ForEach-Object { $_ | ConvertFrom-Json }
    Assert-LastExitCode -Description 'C# alert stream'

    Assert-AlertStream -Source 'C++ alert stream' -Events $cppAlertEvents -GpuUuid $cppSample.gpu_uuid
    Assert-AlertStream -Source 'C# alert stream' -Events $csharpAlertEvents -GpuUuid $cppSample.gpu_uuid

    Write-Host 'Verification passed.'
    Write-Host "GPU: $($cppSample.gpu_name)"
    Write-Host "UUID: $($cppSample.gpu_uuid)"
    Write-Host "C / C++ / C# / nvidia-smi: $($temperatures -join ' / ') C"
    Write-Host "Backend: $($cppSample.backend)"
    Write-Host "Board profile: $($cppCapabilities.board.profile_key)"
    Write-Host "Thermal capability records: $($cppCapabilities.thermal_capabilities.Count)"
    Write-Host 'Resilient event streams: C++ and C# passed.'
    Write-Host 'Threshold alert streams: C++ and C# passed.'
}
finally {
    Pop-Location
}
