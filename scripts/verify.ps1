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
$serviceExecutable = Join-Path $projectRoot "csharp\RtxMonitor.Service\bin\$Configuration\net8.0-windows\win-x64\RtxMonitor.Service.exe"
$capabilitySchemaPath = Join-Path $projectRoot 'docs\schema\capabilities-v2.schema.json'
$publicTelemetrySchemaPath = Join-Path $projectRoot 'docs\schema\public-telemetry-v1.schema.json'
$eventSchemaV1Path = Join-Path $projectRoot 'docs\schema\telemetry-event-v1.schema.json'
$eventSchemaV2Path = Join-Path $projectRoot 'docs\schema\telemetry-event-v2.schema.json'
$eventSchemaPath = Join-Path $projectRoot 'docs\schema\telemetry-event-v3.schema.json'
$evidenceTempRoot = $null
$serviceProcess = $null

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
        [Parameter(Mandatory)][string]$GpuUuid,
        [Parameter(Mandatory)][int]$SchemaVersion,
        [switch]$RequireEnrichedTelemetry
    )

    if ($Events.Count -ne 2) {
        throw "$Source produced $($Events.Count) events instead of 2."
    }

    for ($index = 0; $index -lt $Events.Count; $index++) {
        $event = $Events[$index]
        if ($event.sequence -ne ($index + 1)) {
            throw "$Source did not emit one contiguous global sequence."
        }
        if ($event.schema_version -ne $SchemaVersion -or $event.event_type -ne 'sample') {
            throw "$Source emitted an unexpected event envelope."
        }
        if ($event.target_gpu_uuid -ne $GpuUuid -or
            $event.sample.sensor -ne 'gpu_die' -or
            $null -eq $event.sample.temperature_c) {
            throw "$Source did not preserve the selected GPU and sensor reading."
        }
        if ($RequireEnrichedTelemetry -and
            ($null -eq $event.public_telemetry -or
             $null -eq $event.computed_metrics -or
             $event.public_telemetry.fields.Count -lt 31 -or
             $event.computed_metrics.metrics.Count -ne 4)) {
            throw "$Source did not preserve the enriched public telemetry reports."
        }
    }
}

function Assert-AlertStream {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][object[]]$Events,
        [Parameter(Mandatory)][string]$GpuUuid,
        [Parameter(Mandatory)][int]$SchemaVersion
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
        if ($alert.schema_version -ne $SchemaVersion -or
            $alert.target_gpu_uuid -ne $GpuUuid -or
            $alert.alert_threshold_c -ne 0 -or
            $null -eq $alert.sample -or
            $null -eq $alert.sample.temperature_c) {
            throw "$Source emitted an unexpected alert_raised envelope."
        }
        if ($SchemaVersion -ge 3 -and
            ($null -ne $alert.public_telemetry -or $null -ne $alert.computed_metrics)) {
            throw "$Source duplicated raw telemetry inside an alert transition."
        }
    }
}

function Get-PublicField {
    param(
        [Parameter(Mandatory)][object]$Report,
        [Parameter(Mandatory)][string]$Name,
        [int]$ProviderNativeId = -1
    )

    $matches = @($Report.fields | Where-Object {
        $_.field -eq $Name -and
        ($ProviderNativeId -lt 0 -or $_.provider_native_id -eq $ProviderNativeId)
    })
    if ($matches.Count -ne 1) {
        throw "Expected one '$Name' field, found $($matches.Count)."
    }
    return $matches[0]
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

    $publicTelemetrySchema = Get-Content -Raw -LiteralPath $publicTelemetrySchemaPath | ConvertFrom-Json
    if ($publicTelemetrySchema.properties.schema_version.const -ne 1 -or
        $publicTelemetrySchema.properties.fields.minItems -ne 31 -or
        $publicTelemetrySchema.properties.computed_metrics.minItems -ne 4) {
        throw 'The public telemetry JSON Schema is incomplete.'
    }

    $eventSchemaV1 = Get-Content -Raw -LiteralPath $eventSchemaV1Path | ConvertFrom-Json
    if ($eventSchemaV1.properties.schema_version.const -ne 1) {
        throw 'The telemetry event JSON Schema v1 is missing or no longer declares schema_version const 1.'
    }

    $eventSchemaV2 = Get-Content -Raw -LiteralPath $eventSchemaV2Path | ConvertFrom-Json
    if ($eventSchemaV2.properties.schema_version.const -ne 2) {
        throw 'The telemetry event JSON Schema v2 is missing or no longer declares schema_version const 2.'
    }

    $eventSchema = Get-Content -Raw -LiteralPath $eventSchemaPath | ConvertFrom-Json
    if ($eventSchema.properties.schema_version.const -ne 3) {
        throw 'The telemetry event JSON Schema is missing schema_version const 3.'
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

    $cppTelemetryOutput = (& $cppExecutable --telemetry --json | Out-String).Trim()
    Assert-LastExitCode -Description 'C++ public telemetry catalog'
    $cppTelemetry = $cppTelemetryOutput | ConvertFrom-Json

    $csharpTelemetryOutput = (& $csharpExecutable --telemetry --json | Out-String).Trim()
    Assert-LastExitCode -Description 'C# public telemetry catalog'
    $csharpTelemetry = $csharpTelemetryOutput | ConvertFrom-Json

    $smiOutput = (& nvidia-smi `
        --query-gpu=index,uuid,temperature.gpu `
        --format=csv,noheader,nounits | Select-Object -First 1)
    Assert-LastExitCode -Description 'nvidia-smi reference query'
    $smiFields = $smiOutput -split ',' | ForEach-Object { $_.Trim() }
    if ($smiFields.Count -ne 3) {
        throw "Unexpected nvidia-smi output: $smiOutput"
    }
    $smiTemperature = [int]$smiFields[2]

    $smiTelemetryOutput = (& nvidia-smi `
        --query-gpu=temperature.gpu,power.draw,clocks.current.graphics,clocks.current.memory,memory.total,memory.used,fan.speed,pstate `
        --format=csv,noheader,nounits | Select-Object -First 1)
    Assert-LastExitCode -Description 'nvidia-smi telemetry reference query'
    $smiTelemetry = $smiTelemetryOutput -split ',' | ForEach-Object { $_.Trim() }
    if ($smiTelemetry.Count -ne 8) {
        throw "Unexpected nvidia-smi telemetry output: $smiTelemetryOutput"
    }

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

    if ($cppTelemetry.schema_version -ne 1 -or $csharpTelemetry.schema_version -ne 1 -or
        $cppTelemetry.gpu.uuid -ne $cppSample.gpu_uuid -or
        $csharpTelemetry.gpu.uuid -ne $cppSample.gpu_uuid -or
        $cppTelemetry.profile_key -ne $cppCapabilities.board.profile_key -or
        $csharpTelemetry.profile_key -ne $cppCapabilities.board.profile_key) {
        throw 'A public telemetry report lost its schema, GPU, or board profile identity.'
    }
    if ($cppTelemetry.fields.Count -lt 31 -or
        $cppTelemetry.fields.Count -ne $csharpTelemetry.fields.Count -or
        $cppTelemetry.coverage.total -ne $cppTelemetry.fields.Count -or
        $csharpTelemetry.coverage.total -ne $csharpTelemetry.fields.Count) {
        throw 'Public telemetry coverage does not match the documented field catalog.'
    }
    for ($index = 0; $index -lt $cppTelemetry.fields.Count; $index++) {
        $cppField = $cppTelemetry.fields[$index]
        $csharpField = $csharpTelemetry.fields[$index]
        foreach ($property in @(
            'field',
            'provider',
            'provider_native_id',
            'state',
            'origin',
            'value_type',
            'unit',
            'native_status'
        )) {
            if ($cppField.$property -ne $csharpField.$property) {
                throw "C++ and C# public telemetry field $index differ at '$property'."
            }
        }
        if ($cppField.state -ne 'available' -and
            ($null -ne $cppField.value_u64 -or
             $null -ne $cppField.value_i64 -or
             $null -ne $cppField.value_f64 -or
             $null -ne $csharpField.value_u64 -or
             $null -ne $csharpField.value_i64 -or
             $null -ne $csharpField.value_f64)) {
            throw "Unavailable field '$($cppField.field)' was represented as a numeric value."
        }
    }

    if ($cppTelemetry.computed_metrics.Count -ne 4 -or
        $csharpTelemetry.computed_metrics.Count -ne 4) {
        throw 'Both consumers must expose exactly four computed metrics.'
    }
    for ($index = 0; $index -lt 4; $index++) {
        $cppMetric = $cppTelemetry.computed_metrics[$index]
        $csharpMetric = $csharpTelemetry.computed_metrics[$index]
        foreach ($property in @('metric', 'state', 'origin', 'unit', 'formula', 'window_ms', 'sample_count')) {
            if ($cppMetric.$property -ne $csharpMetric.$property) {
                throw "C++ and C# computed metric $index differ at '$property'."
            }
        }
        if (($cppMetric.inputs -join ',') -ne ($csharpMetric.inputs -join ',')) {
            throw "C++ and C# computed metric $index use different inputs."
        }
    }

    $cppGpuTemperature = Get-PublicField -Report $cppTelemetry -Name 'gpu_die_temperature_c'
    $csharpGpuTemperature = Get-PublicField -Report $csharpTelemetry -Name 'gpu_die_temperature_c'
    if ([Math]::Abs([int]$cppGpuTemperature.value_i64 - [int]$smiTelemetry[0]) -gt 5 -or
        [Math]::Abs([int]$csharpGpuTemperature.value_i64 - [int]$smiTelemetry[0]) -gt 5) {
        throw 'Public GPU temperature differs from nvidia-smi by more than 5 C.'
    }
    $memoryTemperature = Get-PublicField -Report $cppTelemetry -Name 'memory_temperature_c'
    if ($memoryTemperature.state -ne 'not_supported' -or
        $null -ne $memoryTemperature.value_i64) {
        throw 'This RTX 3060 must keep unavailable memory temperature explicit instead of inventing zero.'
    }
    $memoryTotal = Get-PublicField -Report $cppTelemetry -Name 'memory_total_bytes'
    $smiMemoryTotalBytes = [uint64]([double]::Parse(
        $smiTelemetry[4],
        [Globalization.CultureInfo]::InvariantCulture) * 1MB)
    if ([uint64]$memoryTotal.value_u64 -ne $smiMemoryTotalBytes) {
        throw 'Public memory total differs from nvidia-smi.'
    }
    $power = Get-PublicField -Report $cppTelemetry -Name 'power_instant_mw'
    $smiPowerMilliwatts = [double]::Parse(
        $smiTelemetry[1],
        [Globalization.CultureInfo]::InvariantCulture) * 1000
    if ([Math]::Abs([double]$power.value_u64 - $smiPowerMilliwatts) -gt 50000) {
        throw 'Public instantaneous power differs from nvidia-smi by more than 50 W.'
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

    Assert-EventStream `
        -Source 'C++ resilient stream' `
        -Events $cppEvents `
        -GpuUuid $cppSample.gpu_uuid `
        -SchemaVersion 3 `
        -RequireEnrichedTelemetry
    Assert-EventStream `
        -Source 'C# resilient stream' `
        -Events $csharpEvents `
        -GpuUuid $cppSample.gpu_uuid `
        -SchemaVersion 3 `
        -RequireEnrichedTelemetry

    $cppAlertEvents = @(& $cppExecutable --watch --count 1 --interval 100 --events --alert-threshold 0) |
        ForEach-Object { $_ | ConvertFrom-Json }
    Assert-LastExitCode -Description 'C++ alert stream'

    $csharpAlertEvents = @(& $csharpExecutable --watch --count 1 --interval 100 --events --alert-threshold 0) |
        ForEach-Object { $_ | ConvertFrom-Json }
    Assert-LastExitCode -Description 'C# alert stream'

    Assert-AlertStream `
        -Source 'C++ alert stream' `
        -Events $cppAlertEvents `
        -GpuUuid $cppSample.gpu_uuid `
        -SchemaVersion 3
    Assert-AlertStream `
        -Source 'C# alert stream' `
        -Events $csharpAlertEvents `
        -GpuUuid $cppSample.gpu_uuid `
        -SchemaVersion 3

    $evidenceTempRoot = Join-Path `
        ([System.IO.Path]::GetTempPath()) `
        ("rtxmon-verify-{0}" -f [Guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $evidenceTempRoot
    $evidenceDatabase = Join-Path $evidenceTempRoot 'telemetry.db'

    $storedEvents = @(& $csharpExecutable `
        --watch `
        --count 2 `
        --interval 100 `
        --events `
        --database $evidenceDatabase) | ForEach-Object { $_ | ConvertFrom-Json }
    Assert-LastExitCode -Description 'C# persisted event stream'
    Assert-EventStream `
        -Source 'C# persisted event stream' `
        -Events $storedEvents `
        -GpuUuid $cppSample.gpu_uuid `
        -SchemaVersion 3 `
        -RequireEnrichedTelemetry

    $history = @(& $csharpExecutable `
        --history `
        --database $evidenceDatabase `
        --limit 10 `
        --json) | ForEach-Object { $_ | ConvertFrom-Json }
    Assert-LastExitCode -Description 'C# evidence history'
    if ($history.Count -ne 2) {
        throw "C# evidence history returned $($history.Count) records instead of 2."
    }

    $historyRunId = [string]$history[0].run.run_id
    foreach ($record in $history) {
        if ($record.evidence_schema_version -ne 1 -or
            $record.store_schema_version -ne 1 -or
            $record.event.schema_version -ne 3 -or
            $record.run.run_id -ne $historyRunId -or
            $record.run.application_version -ne '0.7.0' -or
            $record.device_snapshot.gpu.uuid -ne $cppSample.gpu_uuid -or
            $record.device_snapshot.board.profile_key -ne $cppCapabilities.board.profile_key -or
            $null -eq $record.event.public_telemetry -or
            $null -eq $record.event.computed_metrics) {
            throw 'A persisted evidence record did not preserve schema, run, version, GPU, or board provenance.'
        }
    }

    $continuedHistory = @(& $csharpExecutable `
        --history `
        --database $evidenceDatabase `
        --run-id $historyRunId `
        --after-sequence 1 `
        --limit 10 `
        --json) | ForEach-Object { $_ | ConvertFrom-Json }
    Assert-LastExitCode -Description 'C# evidence sequence query'
    if ($continuedHistory.Count -ne 1 -or $continuedHistory[0].event.sequence -ne 2) {
        throw 'C# evidence sequence query did not resume after sequence 1.'
    }

    $exportedHistory = @(& $csharpExecutable `
        --export `
        --database $evidenceDatabase) | ForEach-Object { $_ | ConvertFrom-Json }
    Assert-LastExitCode -Description 'C# evidence export'
    if ($exportedHistory.Count -ne 2 -or
        $exportedHistory[0].event.sequence -ne 1 -or
        $exportedHistory[1].event.sequence -ne 2) {
        throw 'C# evidence export did not preserve the complete ordered stream.'
    }

    if (-not (Test-Path -LiteralPath $serviceExecutable -PathType Leaf)) {
        throw "Service executable is missing: $serviceExecutable"
    }

    $portProbe = [System.Net.Sockets.TcpListener]::new(
        [System.Net.IPAddress]::Loopback,
        0)
    $portProbe.Start()
    $servicePort = ([System.Net.IPEndPoint]$portProbe.LocalEndpoint).Port
    $portProbe.Stop()
    $serviceDatabase = Join-Path $evidenceTempRoot 'service-telemetry.db'
    $serviceStdout = Join-Path $evidenceTempRoot 'service.stdout.log'
    $serviceStderr = Join-Path $evidenceTempRoot 'service.stderr.log'
    $serviceProcess = Start-Process `
        -FilePath $serviceExecutable `
        -ArgumentList @(
            "--RtxMonitor:Port=$servicePort",
            "--RtxMonitor:DatabasePath=$serviceDatabase",
            '--RtxMonitor:IntervalMilliseconds=100',
            '--RtxMonitor:DiscoveryIntervalSeconds=1'
        ) `
        -RedirectStandardOutput $serviceStdout `
        -RedirectStandardError $serviceStderr `
        -WindowStyle Hidden `
        -PassThru

    $serviceBaseUri = "http://127.0.0.1:$servicePort"
    $serviceDeadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    $serviceHealth = $null
    while ([DateTimeOffset]::UtcNow -lt $serviceDeadline) {
        if ($serviceProcess.HasExited) {
            $serviceLog = if (Test-Path -LiteralPath $serviceStderr) {
                Get-Content -LiteralPath $serviceStderr -Raw
            }
            else {
                ''
            }
            throw "Service exited before becoming ready. $serviceLog"
        }

        try {
            $serviceHealth = Invoke-RestMethod -Uri "$serviceBaseUri/health" -TimeoutSec 2
            if ($serviceHealth.ready) {
                break
            }
        }
        catch {
            $serviceHealth = $null
        }
        Start-Sleep -Milliseconds 200
    }
    if ($null -eq $serviceHealth -or
        -not $serviceHealth.ready -or
        $serviceHealth.service_version -ne '0.7.0' -or
        $serviceHealth.storage.state -ne 'available') {
        throw 'Local service did not become ready with SQLite and version 0.7.0.'
    }

    $serviceDeadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    $serviceGpus = $null
    $serviceHistory = $null
    $encodedServiceUuid = [Uri]::EscapeDataString([string]$cppSample.gpu_uuid)
    while ([DateTimeOffset]::UtcNow -lt $serviceDeadline) {
        $serviceGpus = Invoke-RestMethod -Uri "$serviceBaseUri/api/v1/gpus" -TimeoutSec 2
        $serviceHistory = Invoke-RestMethod `
            -Uri "$serviceBaseUri/api/v1/history?order=asc&limit=10&gpu_uuid=$encodedServiceUuid" `
            -TimeoutSec 2
        if ($serviceGpus.count -ge 1 -and $serviceHistory.count -ge 2) {
            break
        }
        Start-Sleep -Milliseconds 200
    }
    $matchingServiceGpus = @(
        $serviceGpus.gpus | Where-Object { $_.uuid -eq $cppSample.gpu_uuid }
    )
    if ($matchingServiceGpus.Count -ne 1 -or
        $null -eq $matchingServiceGpus[0].last_sample_temperature_c) {
        throw 'Local service did not expose the expected physical GPU and last sample.'
    }
    $serviceTemperature = [int]$matchingServiceGpus[0].last_sample_temperature_c
    Assert-Temperature -Source 'local service' -Temperature $serviceTemperature
    $allTemperatures = @($temperatures) + $serviceTemperature
    $allMinimum = ($allTemperatures | Measure-Object -Minimum).Minimum
    $allMaximum = ($allTemperatures | Measure-Object -Maximum).Maximum
    if (($allMaximum - $allMinimum) -gt 5) {
        throw "The local service differed by more than 5 C: $($allTemperatures -join ', ')."
    }
    if ($serviceHistory.count -lt 2 -or
        $serviceHistory.items[0].run.application_version -ne '0.7.0' -or
        $serviceHistory.items[0].event.schema_version -ne 3 -or
        $null -eq $serviceHistory.items[0].event.public_telemetry -or
        $serviceHistory.items[0].device_snapshot.gpu.uuid -ne $cppSample.gpu_uuid) {
        throw 'Local service history did not preserve version and GPU provenance.'
    }

    $serviceCapabilities = Invoke-RestMethod `
        -Uri "$serviceBaseUri/api/v1/gpus/$encodedServiceUuid/capabilities" `
        -TimeoutSec 5
    if ($serviceCapabilities.schema_version -ne 1 -or
        $serviceCapabilities.gpu.uuid -ne $cppSample.gpu_uuid -or
        $serviceCapabilities.board.profile_key -ne $cppCapabilities.board.profile_key) {
        throw 'Local service capabilities did not preserve GPU and board identity.'
    }

    $serviceTelemetry = Invoke-RestMethod `
        -Uri "$serviceBaseUri/api/v1/gpus/$encodedServiceUuid/telemetry" `
        -TimeoutSec 5
    if ($serviceTelemetry.schema_version -ne 1 -or
        $serviceTelemetry.gpu.uuid -ne $cppSample.gpu_uuid -or
        $serviceTelemetry.board.profile_key -ne $cppCapabilities.board.profile_key -or
        $serviceTelemetry.coverage.total -lt 31 -or
        $serviceTelemetry.fields.Count -ne $serviceTelemetry.coverage.total -or
        $serviceTelemetry.computed_metrics.metrics.Count -ne 4) {
        throw 'Local service telemetry did not preserve public fields, coverage, metrics, and board identity.'
    }

    Stop-Process -Id $serviceProcess.Id
    $null = $serviceProcess.WaitForExit(10000)
    $serviceProcess = $null

    Write-Host 'Verification passed.'
    Write-Host "GPU: $($cppSample.gpu_name)"
    Write-Host "UUID: $($cppSample.gpu_uuid)"
    Write-Host "C / C++ / C# / nvidia-smi: $($temperatures -join ' / ') C"
    Write-Host "Local service: $serviceTemperature C"
    Write-Host "Backend: $($cppSample.backend)"
    Write-Host "Board profile: $($cppCapabilities.board.profile_key)"
    Write-Host "Thermal capability records: $($cppCapabilities.thermal_capabilities.Count)"
    Write-Host "Public telemetry coverage: $($cppTelemetry.coverage.available)/$($cppTelemetry.coverage.total) available."
    Write-Host 'Resilient event streams: C++ and C# passed.'
    Write-Host 'Threshold alert streams: C++ and C# passed.'
    Write-Host 'SQLite evidence history and export: passed.'
    Write-Host "Local service HTTP, discovery, history, capabilities, and telemetry: passed on 127.0.0.1:$servicePort."
}
finally {
    try {
        if ($null -ne $serviceProcess -and -not $serviceProcess.HasExited) {
            Stop-Process -Id $serviceProcess.Id -Force
            $null = $serviceProcess.WaitForExit(10000)
        }
        if ($null -ne $evidenceTempRoot -and (Test-Path -LiteralPath $evidenceTempRoot)) {
            $resolvedEvidenceRoot = (Resolve-Path -LiteralPath $evidenceTempRoot).Path
            $resolvedSystemTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
            if (-not $resolvedEvidenceRoot.StartsWith(
                    $resolvedSystemTemp,
                    [StringComparison]::OrdinalIgnoreCase) -or
                -not ([System.IO.Path]::GetFileName($resolvedEvidenceRoot)).StartsWith(
                    'rtxmon-verify-',
                    [StringComparison]::Ordinal)) {
                throw "Refusing to remove unexpected verification directory: $resolvedEvidenceRoot"
            }

            Remove-Item -LiteralPath $resolvedEvidenceRoot -Recurse -Force
        }
    }
    finally {
        Pop-Location
    }
}
