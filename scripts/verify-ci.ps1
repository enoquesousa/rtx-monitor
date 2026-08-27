[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$configurationLower = $Configuration.ToLowerInvariant()
$nativeOutput = Join-Path $projectRoot "build\windows-x64\bin\$Configuration"
$cppExecutable = Join-Path $nativeOutput 'rtxmon.exe'
$vbiosExecutable = Join-Path $nativeOutput 'rtxmon-vbios.exe'
$csharpExecutable = Join-Path $projectRoot "csharp\RtxMonitor.Console\bin\$Configuration\net8.0\RtxMonitor.Console.exe"
$serviceExecutable = Join-Path $projectRoot "csharp\RtxMonitor.Service\bin\$Configuration\net8.0-windows\win-x64\RtxMonitor.Service.exe"
$labExecutable = Join-Path $projectRoot "csharp\RtxMonitor.Lab\bin\$Configuration\net8.0\rtxmon-lab.exe"
$managedTestProject = Join-Path $projectRoot 'csharp\RtxMonitor.Managed.Tests\RtxMonitor.Managed.Tests.csproj'
$storageTestProject = Join-Path $projectRoot 'csharp\RtxMonitor.Storage.Tests\RtxMonitor.Storage.Tests.csproj'
$serviceTestProject = Join-Path $projectRoot 'csharp\RtxMonitor.Service.Tests\RtxMonitor.Service.Tests.csproj'
$labTestProject = Join-Path $projectRoot 'csharp\RtxMonitor.Lab.Tests\RtxMonitor.Lab.Tests.csproj'
$managedProject = Join-Path $projectRoot 'csharp\RtxMonitor.Managed\RtxMonitor.Managed.csproj'
$storageProject = Join-Path $projectRoot 'csharp\RtxMonitor.Storage\RtxMonitor.Storage.csproj'
$consoleProject = Join-Path $projectRoot 'csharp\RtxMonitor.Console\RtxMonitor.Console.csproj'
$serviceProject = Join-Path $projectRoot 'csharp\RtxMonitor.Service\RtxMonitor.Service.csproj'
$labProject = Join-Path $projectRoot 'csharp\RtxMonitor.Lab\RtxMonitor.Lab.csproj'
$managedAssembly = Join-Path $projectRoot "csharp\RtxMonitor.Managed\bin\$Configuration\net8.0\RtxMonitor.Managed.dll"
$storageAssembly = Join-Path $projectRoot "csharp\RtxMonitor.Storage\bin\$Configuration\net8.0\RtxMonitor.Storage.dll"
$serviceAssembly = Join-Path $projectRoot "csharp\RtxMonitor.Service\bin\$Configuration\net8.0-windows\win-x64\RtxMonitor.Service.dll"
$labAssembly = Join-Path $projectRoot "csharp\RtxMonitor.Lab\bin\$Configuration\net8.0\rtxmon-lab.dll"
$capabilitySchemaPath = Join-Path $projectRoot 'docs\schema\capabilities-v2.schema.json'
$publicTelemetrySchemaPath = Join-Path $projectRoot 'docs\schema\public-telemetry-v2.schema.json'
$eventSchemaV1Path = Join-Path $projectRoot 'docs\schema\telemetry-event-v1.schema.json'
$eventSchemaV2Path = Join-Path $projectRoot 'docs\schema\telemetry-event-v2.schema.json'
$eventSchemaPath = Join-Path $projectRoot 'docs\schema\telemetry-event-v4.schema.json'
$evidenceSchemaPath = Join-Path $projectRoot 'docs\schema\evidence-record-v1.schema.json'
$liveSchemaPath = Join-Path $projectRoot 'docs\schema\live-telemetry-v1.schema.json'
$streamGapSchemaPath = Join-Path $projectRoot 'docs\schema\stream-gap-v1.schema.json'
$windowsTelemetrySchemaPath = Join-Path $projectRoot 'docs\schema\windows-telemetry-v1.schema.json'
$openApiPath = Join-Path $projectRoot 'docs\openapi\service-v1.openapi.json'
$artifactManifestSchemaPath = Join-Path $projectRoot 'docs\schema\artifact-package-manifest-v1.schema.json'
$rawArtifactSchemaPath = Join-Path $projectRoot 'docs\schema\raw-artifact-v1.schema.json'
$evidencePackageSchemaPath = Join-Path $projectRoot 'docs\schema\evidence-package-v1.schema.json'
$labCommandErrorSchemaPath = Join-Path $projectRoot 'docs\schema\lab-command-error-v1.schema.json'
$experimentManifestSchemaPath = Join-Path $projectRoot 'docs\schema\experiment-manifest-v1.schema.json'
$analysisReportSchemaPath = Join-Path $projectRoot 'docs\schema\analysis-report-v1.schema.json'
$vbiosAnalysisSchemaPath = Join-Path $projectRoot 'docs\schema\vbios-analysis-v1.schema.json'
$gpuzReferenceSchemaPath = Join-Path $projectRoot 'docs\schema\gpuz-reference-analysis-v1.schema.json'
$experimentMarkerSchemaPath = Join-Path $projectRoot 'docs\schema\experiment-marker-v1.schema.json'
$gpuzCorrelationSchemaPath = Join-Path $projectRoot 'docs\schema\gpuz-correlation-v1.schema.json'
$nvapiObservationSchemaPath = Join-Path $projectRoot 'docs\schema\nvapi-query-observation-v1.schema.json'
$nvapiClassificationSchemaPath = Join-Path $projectRoot 'docs\schema\nvapi-interface-classification-v1.schema.json'
$nvapiResolutionSchemaPath = Join-Path $projectRoot 'docs\schema\nvapi-interface-resolution-v1.schema.json'
$nvapiCallSchemaPath = Join-Path $projectRoot 'docs\schema\nvapi-call-observation-v1.schema.json'
$nvapiCandidateSchemaPath = Join-Path $projectRoot 'docs\schema\nvapi-candidate-inventory-v1.schema.json'
$nvapiCandidateCallSchemaPath = Join-Path $projectRoot 'docs\schema\nvapi-candidate-call-observation-v1.schema.json'
$nvapiThermChannelSchemaPath = Join-Path $projectRoot 'docs\schema\nvapi-therm-channel-v2-observation-v1.schema.json'
$nvapiThermCorrelationSchemaPath = Join-Path $projectRoot 'docs\schema\nvapi-therm-channel-correlation-v1.schema.json'
$nvapiVoltageObservationSchemaPath = Join-Path $projectRoot 'docs\schema\nvapi-voltage-status-v1-observation-v1.schema.json'
$nvapiVoltageCorrelationSchemaPath = Join-Path $projectRoot 'docs\schema\nvapi-voltage-status-correlation-v1.schema.json'
$gpuzDeviceIoSchemaPath = Join-Path $projectRoot 'docs\schema\gpuz-device-io-control-observation-v1.schema.json'
$gpuzDeviceInputSchemaPath = Join-Path $projectRoot 'docs\schema\gpuz-device-io-control-input-v1.schema.json'
$windowsHandleSchemaPath = Join-Path $projectRoot 'docs\schema\windows-handle-identity-v1.schema.json'
$gpuzFixturePath = Join-Path $projectRoot 'csharp\RtxMonitor.Lab.Tests\Fixtures\gpuz-sensor-log.csv'
$nvapiObservationFixturePath = Join-Path $projectRoot 'csharp\RtxMonitor.Lab.Tests\Fixtures\nvapi-query-observation.json'
$nvapiInterfaceTableFixturePath = Join-Path $projectRoot 'csharp\RtxMonitor.Lab.Tests\Fixtures\nvapi-interface-table.h'
$nvapiClassificationFixturePath = Join-Path $projectRoot 'csharp\RtxMonitor.Lab.Tests\Fixtures\nvapi-interface-classification.json'
$nvapiResolutionFixturePath = Join-Path $projectRoot 'csharp\RtxMonitor.Lab.Tests\Fixtures\nvapi-interface-resolution.json'
$nvapiCallFixturePath = Join-Path $projectRoot 'csharp\RtxMonitor.Lab.Tests\Fixtures\nvapi-call-observation.json'
$nvapiCandidateCallFixturePath = Join-Path $projectRoot 'csharp\RtxMonitor.Lab.Tests\Fixtures\nvapi-candidate-call-observation.json'
$nvapiThermChannelFixturePath = Join-Path $projectRoot 'csharp\RtxMonitor.Lab.Tests\Fixtures\nvapi-therm-channel-v2-observation.json'
$gpuzThermReferenceFixturePath = Join-Path $projectRoot 'csharp\RtxMonitor.Lab.Tests\Fixtures\gpuz-therm-channel-reference.csv'
$nvapiVoltageObservationFixturePath = Join-Path $projectRoot 'csharp\RtxMonitor.Lab.Tests\Fixtures\nvapi-voltage-status-v1-observation.json'
$gpuzVoltageReferenceFixturePath = Join-Path $projectRoot 'csharp\RtxMonitor.Lab.Tests\Fixtures\gpuz-voltage-reference.csv'
$vbiosFixturePath = Join-Path `
    $projectRoot `
    "build\windows-x64\$Configuration\synthetic-vbios-test.rom"
$labCiRoot = $null
$labCiPackage = $null

function Invoke-Checked {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Command,

        [Parameter(Mandatory)]
        [string]$Description
    )

    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

function Remove-LabCiPackageSafely {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [string]$Package
    )

    try {
        $resolvedTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
        if (-not $resolvedTemp.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
            $resolvedTemp += [System.IO.Path]::DirectorySeparatorChar
        }
        $resolvedRoot = [System.IO.Path]::GetFullPath($Root)
        $rootName = [System.IO.Path]::GetFileName($resolvedRoot)
        $rootPrefix = 'rtxmon-ci-lab-'
        $parsedGuid = [Guid]::Empty
        if (-not $resolvedRoot.StartsWith(
                $resolvedTemp,
                [StringComparison]::OrdinalIgnoreCase) -or
            -not $rootName.StartsWith($rootPrefix, [StringComparison]::Ordinal) -or
            -not [Guid]::TryParseExact(
                $rootName.Substring($rootPrefix.Length),
                'N',
                [ref]$parsedGuid) -or
            -not [string]::Equals(
                [System.IO.Path]::GetFullPath($Package),
                (Join-Path $resolvedRoot 'package'),
                [StringComparison]::OrdinalIgnoreCase)) {
            Write-Warning "Skipped laboratory CI cleanup for an unexpected root: $Root"
            return
        }

        $artifactDirectory = Join-Path $Package 'artifact'
        foreach ($directory in @($resolvedRoot, $Package, $artifactDirectory)) {
            $attributes = [System.IO.File]::GetAttributes($directory)
            if (($attributes -band [System.IO.FileAttributes]::Directory) -eq 0 -or
                ($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                Write-Warning "Skipped laboratory CI cleanup for an unsafe directory: $directory"
                return
            }
        }

        $payload = Join-Path $artifactDirectory 'payload.bin'
        $manifest = Join-Path $Package 'manifest.json'
        foreach ($file in @($payload, $manifest)) {
            $attributes = [System.IO.File]::GetAttributes($file)
            if (($attributes -band (
                    [System.IO.FileAttributes]::Directory -bor
                    [System.IO.FileAttributes]::ReparsePoint -bor
                    [System.IO.FileAttributes]::ReadOnly)) -ne 0) {
                Write-Warning "Skipped laboratory CI cleanup for an unsafe file: $file"
                return
            }
        }

        $rootEntries = @([System.IO.Directory]::EnumerateFileSystemEntries($resolvedRoot) |
            Select-Object -First 2)
        $packageEntries = @([System.IO.Directory]::EnumerateFileSystemEntries($Package) |
            Select-Object -First 3)
        $artifactEntries = @([System.IO.Directory]::EnumerateFileSystemEntries(
                $artifactDirectory) |
            Select-Object -First 2)
        if ($rootEntries.Count -ne 1 -or
            $packageEntries.Count -ne 2 -or
            $artifactEntries.Count -ne 1 -or
            $rootEntries[0] -ne $Package -or
            $packageEntries -notcontains $manifest -or
            $packageEntries -notcontains $artifactDirectory -or
            $artifactEntries[0] -ne $payload) {
            Write-Warning 'Skipped laboratory CI cleanup because the fixed layout changed.'
            return
        }

        # Recheck every ancestor immediately before the only filesystem
        # mutations. Files are never made writable here; a surprise fails-leak.
        foreach ($directory in @($resolvedRoot, $Package, $artifactDirectory)) {
            if (([System.IO.File]::GetAttributes($directory) -band
                    [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                Write-Warning 'Skipped laboratory CI cleanup after a path identity change.'
                return
            }
        }
        [System.IO.File]::Delete($payload)
        [System.IO.File]::Delete($manifest)
        [System.IO.Directory]::Delete($artifactDirectory)
        [System.IO.Directory]::Delete($Package)
        [System.IO.Directory]::Delete($resolvedRoot)
    }
    catch {
        Write-Warning "Laboratory CI cleanup failed closed: $($_.Exception.Message)"
    }
}

Push-Location -LiteralPath $projectRoot
try {
    foreach ($scriptName in @(
        'capture-gpuz-device-io-control.ps1',
        'capture-gpuz-nvapi-candidate-calls.ps1',
        'capture-gpuz-nvapi-ids.ps1',
        'capture-gpuz-nvapi-therm-channel-v2.ps1',
        'capture-gpuz-procmon.ps1',
        'check-lab-access.ps1',
        'collect-gpuz-driver.ps1'
    )) {
        $scriptPath = Join-Path $PSScriptRoot $scriptName
        $tokens = $null
        $parseErrors = $null
        $null = [System.Management.Automation.Language.Parser]::ParseFile(
            $scriptPath,
            [ref]$tokens,
            [ref]$parseErrors)
        if ($parseErrors.Count -ne 0) {
            throw "PowerShell syntax validation failed for '$scriptName': $($parseErrors[0].Message)"
        }
    }

    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration

    Invoke-Checked -Description 'Managed test build' -Command {
        & dotnet build $managedTestProject --configuration $Configuration --nologo
    }

    Invoke-Checked -Description 'Storage test build' -Command {
        & dotnet build $storageTestProject --configuration $Configuration --nologo
    }

    Invoke-Checked -Description 'Service test build' -Command {
        & dotnet build $serviceTestProject --configuration $Configuration --nologo
    }

    Invoke-Checked -Description 'Laboratory test build' -Command {
        & dotnet build $labTestProject --configuration $Configuration --nologo
    }

    Invoke-Checked -Description 'Native and C++ tests' -Command {
        & ctest --preset "windows-x64-$configurationLower"
    }

    Invoke-Checked -Description 'Managed tests' -Command {
        & dotnet run `
            --project $managedTestProject `
            --configuration $Configuration `
            --no-build
    }

    Invoke-Checked -Description 'Storage tests' -Command {
        & dotnet run `
            --project $storageTestProject `
            --configuration $Configuration `
            --no-build
    }

    Invoke-Checked -Description 'Service tests' -Command {
        & dotnet run `
            --project $serviceTestProject `
            --configuration $Configuration `
            --no-build
    }

    Invoke-Checked -Description 'Laboratory tests' -Command {
        & dotnet run `
            --project $labTestProject `
            --configuration $Configuration `
            --no-build
    }

    foreach ($project in @(
        $managedProject,
        $storageProject,
        $consoleProject,
        $serviceProject,
        $labProject,
        $managedTestProject,
        $storageTestProject,
        $serviceTestProject,
        $labTestProject
    )) {
        Invoke-Checked -Description "dotnet format $project" -Command {
            & dotnet format $project --verify-no-changes --no-restore
        }
    }

    $capabilitySchema = Get-Content -Raw -LiteralPath $capabilitySchemaPath | ConvertFrom-Json
    if ($capabilitySchema.properties.schema_version.const -ne 2) {
        throw 'Capability schema must declare schema_version const 2.'
    }

    $publicTelemetrySchema = Get-Content -Raw -LiteralPath $publicTelemetrySchemaPath | ConvertFrom-Json
    if ($publicTelemetrySchema.properties.schema_version.const -ne 2 -or
        $publicTelemetrySchema.properties.fields.minItems -ne 34 -or
        $publicTelemetrySchema.properties.computed_metrics.minItems -ne 4) {
        throw 'Public telemetry schema must declare version 2, 34 fields, and four metrics.'
    }

    $eventSchemaV1 = Get-Content -Raw -LiteralPath $eventSchemaV1Path | ConvertFrom-Json
    if ($eventSchemaV1.properties.schema_version.const -ne 1) {
        throw 'Telemetry event schema v1 must remain available and declare schema_version const 1.'
    }

    $eventSchemaV2 = Get-Content -Raw -LiteralPath $eventSchemaV2Path | ConvertFrom-Json
    if ($eventSchemaV2.properties.schema_version.const -ne 2) {
        throw 'Telemetry event schema v2 must remain available and declare schema_version const 2.'
    }

    $eventSchema = Get-Content -Raw -LiteralPath $eventSchemaPath | ConvertFrom-Json
    if ($eventSchema.properties.schema_version.const -ne 4 -or
        $null -eq $eventSchema.properties.public_telemetry -or
        $null -eq $eventSchema.properties.computed_metrics) {
        throw 'Telemetry event schema must declare version 4 and its enriched reports.'
    }

    $eventTypes = @($eventSchema.properties.event_type.enum)
    foreach ($requiredEvent in @('sample', 'gap', 'recovered', 'alert_raised', 'alert_cleared')) {
        if ($requiredEvent -notin $eventTypes) {
            throw "Telemetry event schema is missing '$requiredEvent'."
        }
    }

    $evidenceSchema = Get-Content -Raw -LiteralPath $evidenceSchemaPath | ConvertFrom-Json
    $evidenceEventRefs = @($evidenceSchema.properties.event.oneOf | ForEach-Object { $_.'$ref' })
    if ($evidenceSchema.properties.evidence_schema_version.const -ne 1 -or
        $evidenceSchema.properties.store_schema_version.const -ne 1 -or
        'telemetry-event-v2.schema.json' -notin $evidenceEventRefs -or
        'telemetry-event-v3.schema.json' -notin $evidenceEventRefs -or
        'telemetry-event-v4.schema.json' -notin $evidenceEventRefs) {
        throw 'Evidence schema must declare evidence/store version 1 and accept telemetry events v2/v3/v4.'
    }

    $liveSchema = Get-Content -Raw -LiteralPath $liveSchemaPath | ConvertFrom-Json
    if ($liveSchema.properties.schema_version.const -ne 1 -or
        $liveSchema.properties.event.'$ref' -ne 'telemetry-event-v4.schema.json') {
        throw 'Live telemetry schema must declare version 1 and embed telemetry event v4.'
    }

    $streamGapSchema = Get-Content -Raw -LiteralPath $streamGapSchemaPath | ConvertFrom-Json
    $windowsTelemetrySchema = Get-Content -Raw -LiteralPath $windowsTelemetrySchemaPath | ConvertFrom-Json
    if ($windowsTelemetrySchema.properties.schema_version.const -ne 1) {
        throw 'The Windows telemetry schema is missing schema_version const 1.'
    }
    if ($streamGapSchema.properties.schema_version.const -ne 1 -or
        $streamGapSchema.properties.dropped_events.minimum -ne 1) {
        throw 'SSE stream gap schema must declare version 1 and a positive drop count.'
    }

    $openApi = Get-Content -Raw -LiteralPath $openApiPath | ConvertFrom-Json
    if ($null -eq $openApi.paths.'/api/v1/gpus/{gpu_uuid}/windows-telemetry') {
        throw 'The service OpenAPI contract is missing the Windows telemetry endpoint.'
    }
    if ($openApi.openapi -ne '3.1.0' -or
        $openApi.info.version -ne '1.0.0' -or
        $null -eq $openApi.paths.'/health' -or
        $null -eq $openApi.paths.'/api/v1/gpus' -or
        $null -eq $openApi.paths.'/api/v1/gpus/{gpu_uuid}/capabilities' -or
        $null -eq $openApi.paths.'/api/v1/gpus/{gpu_uuid}/telemetry' -or
        $null -eq $openApi.paths.'/api/v1/events' -or
        $null -eq $openApi.paths.'/api/v1/history') {
        throw 'OpenAPI must declare v1 and all six local service endpoints.'
    }
    foreach ($schemaName in @(
        'health',
        'storageHealth',
        'discoveryHealth',
        'gpuList',
        'gpuRuntime',
        'capabilities',
        'gpuIdentity',
        'board',
        'thermalProvider',
        'thermalCapability',
        'publicTelemetry',
        'publicTelemetryCoverage',
        'publicTelemetryField',
        'computedMetrics',
        'computedMetric',
        'history',
        'problem'
    )) {
        if ($null -eq $openApi.components.schemas.$schemaName) {
            throw "OpenAPI response schema is missing: $schemaName"
        }
    }
    if ($openApi.components.schemas.gpuRuntime.required -notcontains
        'last_sample_temperature_c' -or
        $openApi.components.schemas.history.properties.limit.maximum -ne 10000 -or
        $null -eq $openApi.paths.'/api/v1/events'.get.responses.'400' -or
        $openApi.paths.'/api/v1/events'.get.responses.'200'.'x-sse-events'.telemetry.'$ref' -ne
        '../schema/live-telemetry-v1.schema.json') {
        throw 'OpenAPI must fully describe GPU snapshots, bounded history, and SSE outcomes.'
    }

    $artifactManifestSchema = Get-Content -Raw -LiteralPath $artifactManifestSchemaPath |
        ConvertFrom-Json
    if ($artifactManifestSchema.'$id' -ne
            'urn:rtx-monitor:schema:artifact-package-manifest:1' -or
        $artifactManifestSchema.properties.schema_version.const -ne 1 -or
        $artifactManifestSchema.properties.source_kind.const -ne 'user_provided_local_file' -or
        $artifactManifestSchema.properties.artifact.'$ref' -ne '#/$defs/artifact' -or
        $artifactManifestSchema.'$defs'.artifact.properties.relative_path.const -ne
            'artifact/payload.bin' -or
        $artifactManifestSchema.'$defs'.artifact.properties.size_bytes.maximum -ne 268435456) {
        throw 'Artifact package manifest schema must describe the executable v1 package.'
    }

    $rawArtifactSchema = Get-Content -Raw -LiteralPath $rawArtifactSchemaPath | ConvertFrom-Json
    if ($rawArtifactSchema.'$id' -ne 'urn:rtx-monitor:schema:raw-artifact:1' -or
        $rawArtifactSchema.properties.relative_path.const -ne 'artifact/payload.bin' -or
        $rawArtifactSchema.properties.sha256.pattern -ne '^[0-9a-f]{64}$' -or
        $rawArtifactSchema.properties.size_bytes.maximum -ne 268435456) {
        throw 'Raw artifact schema must pin the payload path and lowercase SHA-256.'
    }

    $evidencePackageSchema = Get-Content -Raw -LiteralPath $evidencePackageSchemaPath |
        ConvertFrom-Json
    if ($evidencePackageSchema.'$id' -ne
            'urn:rtx-monitor:schema:evidence-package-result:1' -or
        $evidencePackageSchema.properties.manifest.'$ref' -ne '#/$defs/manifest' -or
        $evidencePackageSchema.properties.manifest_sha256.pattern -ne '^[0-9a-f]{64}$' -or
        $evidencePackageSchema.'$defs'.manifest.properties.source_kind.const -ne
            'user_provided_local_file' -or
        $evidencePackageSchema.'$defs'.artifact.properties.relative_path.const -ne
            'artifact/payload.bin') {
        throw 'Evidence package result schema must reference the executable manifest and SHA-256.'
    }

    $labCommandErrorSchema = Get-Content -Raw -LiteralPath $labCommandErrorSchemaPath |
        ConvertFrom-Json
    $expectedLabErrorCodes = @(
        'invalid_arguments',
        'package_error',
        'analysis_error',
        'unsupported_platform',
        'io_error'
    )
    $labErrorCodes = @($labCommandErrorSchema.properties.error_code.enum)
    if ($labCommandErrorSchema.'$id' -ne 'urn:rtx-monitor:schema:lab-command-error:1' -or
        $labCommandErrorSchema.properties.status.const -ne 'error' -or
        (Compare-Object $expectedLabErrorCodes $labErrorCodes).Count -ne 0) {
        throw 'Laboratory command error schema must describe the executable error envelope.'
    }

    $experimentManifestSchema = Get-Content -Raw -LiteralPath $experimentManifestSchemaPath |
        ConvertFrom-Json
    $analysisReportSchema = Get-Content -Raw -LiteralPath $analysisReportSchemaPath |
        ConvertFrom-Json
    $vbiosAnalysisSchema = Get-Content -Raw -LiteralPath $vbiosAnalysisSchemaPath |
        ConvertFrom-Json
    $gpuzReferenceSchema = Get-Content -Raw -LiteralPath $gpuzReferenceSchemaPath |
        ConvertFrom-Json
    $experimentMarkerSchema = Get-Content -Raw -LiteralPath $experimentMarkerSchemaPath |
        ConvertFrom-Json
    $gpuzCorrelationSchema = Get-Content -Raw -LiteralPath $gpuzCorrelationSchemaPath |
        ConvertFrom-Json
    $nvapiObservationSchema = Get-Content -Raw -LiteralPath $nvapiObservationSchemaPath |
        ConvertFrom-Json
    $nvapiClassificationSchema = Get-Content -Raw -LiteralPath $nvapiClassificationSchemaPath |
        ConvertFrom-Json
    $nvapiResolutionSchema = Get-Content -Raw -LiteralPath $nvapiResolutionSchemaPath |
        ConvertFrom-Json
    $nvapiCallSchema = Get-Content -Raw -LiteralPath $nvapiCallSchemaPath |
        ConvertFrom-Json
    $nvapiCandidateSchema = Get-Content -Raw -LiteralPath $nvapiCandidateSchemaPath |
        ConvertFrom-Json
    $nvapiCandidateCallSchema = Get-Content -Raw -LiteralPath $nvapiCandidateCallSchemaPath |
        ConvertFrom-Json
    $nvapiThermChannelSchema = Get-Content -Raw -LiteralPath $nvapiThermChannelSchemaPath |
        ConvertFrom-Json
    $nvapiThermCorrelationSchema = Get-Content `
        -Raw `
        -LiteralPath $nvapiThermCorrelationSchemaPath |
        ConvertFrom-Json
    $gpuzDeviceIoSchema = Get-Content -Raw -LiteralPath $gpuzDeviceIoSchemaPath |
        ConvertFrom-Json
    $gpuzDeviceInputSchema = Get-Content -Raw -LiteralPath $gpuzDeviceInputSchemaPath |
        ConvertFrom-Json
    $windowsHandleSchema = Get-Content -Raw -LiteralPath $windowsHandleSchemaPath |
        ConvertFrom-Json
    $vbiosDiagnosticCodes = @($vbiosAnalysisSchema.'$defs'.diagnostic.properties.code.enum)
    if ($experimentManifestSchema.properties.schema_version.const -ne 1 -or
        $analysisReportSchema.properties.schema_version.const -ne 1 -or
        $vbiosAnalysisSchema.properties.schema_version.const -ne 1 -or
        $gpuzReferenceSchema.properties.schema_version.const -ne 1 -or
        $gpuzReferenceSchema.properties.source_kind.const -ne 'gpuz_sensor_log_reference' -or
        $experimentMarkerSchema.properties.schema_version.const -ne 1 -or
        $gpuzCorrelationSchema.properties.source_kind.const -ne 'gpuz_internal_correlation' -or
        $nvapiObservationSchema.properties.source_kind.const -ne 'nvapi_query_interface_observation' -or
        $nvapiClassificationSchema.properties.source_kind.const -ne 'nvapi_interface_classification' -or
        $nvapiResolutionSchema.properties.source_kind.const -ne 'nvapi_query_interface_resolution_observation' -or
        $nvapiCallSchema.properties.source_kind.const -ne 'nvapi_function_call_observation' -or
        $nvapiCandidateSchema.properties.source_kind.const -ne 'nvapi_candidate_inventory' -or
        $nvapiCandidateCallSchema.properties.source_kind.const -ne 'nvapi_candidate_call_observation' -or
        'bounded_input_words' -notin $nvapiCandidateCallSchema.properties.capture_mode.enum -or
        'previously_observed_unidentified' -notin $nvapiCandidateCallSchema.properties.target_scope.enum -or
        $nvapiThermChannelSchema.properties.source_kind.const -ne 'nvapi_therm_channel_v2_observation' -or
        $nvapiThermChannelSchema.properties.interface_id.const -ne '0x65fe3aad' -or
        $nvapiThermChannelSchema.properties.structure_size_bytes.const -ne 168 -or
        $nvapiThermChannelSchema.properties.fixed_point_fractional_bits.const -ne 8 -or
        $nvapiThermCorrelationSchema.properties.source_kind.const -ne
            'nvapi_therm_channel_reference_correlation' -or
        $nvapiThermCorrelationSchema.properties.interface_id.const -ne '0x65fe3aad' -or
        $nvapiThermCorrelationSchema.properties.tolerance_celsius.const -ne 0.051 -or
        $gpuzDeviceIoSchema.properties.source_kind.const -ne 'gpuz_device_io_control_observation' -or
        $gpuzDeviceInputSchema.properties.source_kind.const -ne 'gpuz_device_io_control_input_observation' -or
        $windowsHandleSchema.properties.source_kind.const -ne 'windows_handle_identity' -or
        'ntdll!NtDeviceIoControlFile' -notin $gpuzDeviceIoSchema.properties.observed_api.enum -or
        'ntdll!NtDeviceIoControlFile' -notin $gpuzDeviceInputSchema.properties.observed_api.enum -or
        'unsupported_platform' -notin $vbiosDiagnosticCodes) {
        throw 'v0.8 laboratory schemas must declare version 1 and preserve their source and diagnostic contracts.'
    }

    foreach ($laboratorySchema in @(
        $artifactManifestSchema,
        $rawArtifactSchema,
        $evidencePackageSchema,
        $labCommandErrorSchema,
        $experimentManifestSchema,
        $analysisReportSchema,
        $vbiosAnalysisSchema,
        $gpuzReferenceSchema,
        $experimentMarkerSchema,
        $gpuzCorrelationSchema,
        $nvapiObservationSchema,
        $nvapiClassificationSchema,
        $nvapiResolutionSchema,
        $nvapiCallSchema,
        $nvapiCandidateSchema,
        $nvapiCandidateCallSchema,
        $nvapiThermChannelSchema,
        $nvapiThermCorrelationSchema,
        $gpuzDeviceIoSchema,
        $gpuzDeviceInputSchema,
        $windowsHandleSchema
    )) {
        if ([string]$laboratorySchema.'$id' -notlike 'urn:rtx-monitor:schema:*') {
            throw 'v0.8 laboratory schemas must use stable offline URN identifiers.'
        }
    }

    if (-not (Test-Path -LiteralPath $vbiosFixturePath -PathType Leaf)) {
        throw "Synthetic VBIOS fixture is missing after CTest: $vbiosFixturePath"
    }
    $vbiosJson = (& $vbiosExecutable $vbiosFixturePath | Out-String)
    if ($LASTEXITCODE -ne 0 -or
        -not ($vbiosJson | Test-Json -SchemaFile $vbiosAnalysisSchemaPath)) {
        throw 'Offline VBIOS CLI output must pass vbios-analysis-v1.schema.json.'
    }

    $labCiRoot = Join-Path `
        ([System.IO.Path]::GetTempPath()) `
        ("rtxmon-ci-lab-{0}" -f [Guid]::NewGuid().ToString('N'))
    $labCiPackage = Join-Path $labCiRoot 'package'
    $null = New-Item -ItemType Directory -Path $labCiRoot

    $labCreateJson = (& $labExecutable create `
            --input $vbiosFixturePath `
            --output $labCiPackage `
            --gpu 'Synthetic NVIDIA test device' `
            --driver-version 'test' `
            --vbios-version 'test' | Out-String)
    if ($LASTEXITCODE -ne 0 -or
        -not ($labCreateJson | Test-Json -SchemaFile $evidencePackageSchemaPath)) {
        throw 'Laboratory create output must pass evidence-package-v1.schema.json.'
    }
    $labCreate = $labCreateJson | ConvertFrom-Json

    $labManifestPath = Join-Path $labCiPackage 'manifest.json'
    $labManifestJson = Get-Content -Raw -LiteralPath $labManifestPath
    if (-not ($labManifestJson | Test-Json -SchemaFile $artifactManifestSchemaPath)) {
        throw 'Laboratory manifest must pass artifact-package-manifest-v1.schema.json.'
    }
    $labManifest = $labManifestJson | ConvertFrom-Json
    $labRawArtifactJson = $labManifest.artifact | ConvertTo-Json -Compress
    if (-not ($labRawArtifactJson | Test-Json -SchemaFile $rawArtifactSchemaPath)) {
        throw 'Laboratory artifact descriptor must pass raw-artifact-v1.schema.json.'
    }

    $labVerifyJson = (& $labExecutable verify `
            --package $labCiPackage `
            --expected-manifest-sha256 $labCreate.manifest_sha256 | Out-String)
    if ($LASTEXITCODE -ne 0 -or
        -not ($labVerifyJson | Test-Json -SchemaFile $evidencePackageSchemaPath)) {
        throw 'Laboratory verify output must pass evidence-package-v1.schema.json.'
    }

    $labErrorJson = (& $labExecutable unsupported-operation 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 2 -or
        -not ($labErrorJson | Test-Json -SchemaFile $labCommandErrorSchemaPath)) {
        throw 'Laboratory error output must pass lab-command-error-v1.schema.json.'
    }

    $gpuzReferenceJson = (& $labExecutable analyze-gpuz-log `
            --input $gpuzFixturePath | Out-String)
    if ($LASTEXITCODE -ne 0 -or
        -not ($gpuzReferenceJson | Test-Json -SchemaFile $gpuzReferenceSchemaPath)) {
        throw 'GPU-Z reference import must pass gpuz-reference-analysis-v1.schema.json.'
    }
    $gpuzReference = $gpuzReferenceJson | ConvertFrom-Json
    if ($gpuzReference.sample_count -ne 2 -or
        $gpuzReference.session_count -ne 1 -or
        $gpuzReference.channels.Count -ne 6 -or
        $gpuzReference.channels[1].name -ne 'Hot Spot' -or
        $gpuzReference.channels[4].source_scope -ne 'host_system') {
        throw 'GPU-Z reference import must preserve samples, channel order, hotspot, and host scope.'
    }

    $experimentMarkerJson = (& $labExecutable mark `
            --scenario 'ci.baseline' `
            --phase begin `
            --note 'CI marker' | Out-String)
    if ($LASTEXITCODE -ne 0 -or
        -not ($experimentMarkerJson | Test-Json -SchemaFile $experimentMarkerSchemaPath)) {
        throw 'Experiment marker output must pass experiment-marker-v1.schema.json.'
    }
    $experimentMarker = $experimentMarkerJson | ConvertFrom-Json
    if ($experimentMarker.scenario_id -ne 'ci.baseline' -or
        $experimentMarker.monotonic_ns -lt 0 -or
        $experimentMarker.monotonic_frequency_hz -lt 1) {
        throw 'Experiment marker must preserve the scenario and valid monotonic clock metadata.'
    }

    $gpuzCorrelationJson = (& $labExecutable correlate-gpuz-log `
            --input $gpuzFixturePath `
            --reference 'Hot Spot' | Out-String)
    if ($LASTEXITCODE -ne 0 -or
        -not ($gpuzCorrelationJson | Test-Json -SchemaFile $gpuzCorrelationSchemaPath)) {
        throw 'GPU-Z correlation output must pass gpuz-correlation-v1.schema.json.'
    }
    $gpuzCorrelation = $gpuzCorrelationJson | ConvertFrom-Json
    if ($gpuzCorrelation.reference_channel -ne 'Hot Spot' -or
        $gpuzCorrelation.method -ne 'pearson_zero_lag' -or
        $gpuzCorrelation.pairs.Count -lt 1) {
        throw 'GPU-Z correlation must preserve its reference, method, and candidate pairs.'
    }

    $nvapiObservationJson = Get-Content -Raw -LiteralPath $nvapiObservationFixturePath
    if (-not ($nvapiObservationJson | Test-Json -SchemaFile $nvapiObservationSchemaPath)) {
        throw 'Synthetic NVAPI observation must pass nvapi-query-observation-v1.schema.json.'
    }
    $nvapiClassificationJson = (& $labExecutable classify-nvapi-ids `
            --input $nvapiObservationFixturePath `
            --interface-table $nvapiInterfaceTableFixturePath | Out-String)
    if ($LASTEXITCODE -ne 0 -or
        -not ($nvapiClassificationJson | Test-Json -SchemaFile $nvapiClassificationSchemaPath)) {
        throw 'NVAPI classification output must pass nvapi-interface-classification-v1.schema.json.'
    }
    $nvapiClassification = $nvapiClassificationJson | ConvertFrom-Json
    if ($nvapiClassification.public_catalog_match_count -ne 1 -or
        $nvapiClassification.not_in_public_catalog_count -ne 1 -or
        $nvapiClassification.interfaces[0].public_function -ne 'NvAPI_Initialize') {
        throw 'NVAPI classification must preserve matched and unidentified interface IDs.'
    }
    $nvapiResolutionJson = Get-Content -Raw -LiteralPath $nvapiResolutionFixturePath
    if (-not ($nvapiResolutionJson | Test-Json -SchemaFile $nvapiResolutionSchemaPath)) {
        throw 'Synthetic NVAPI resolution report must pass nvapi-interface-resolution-v1.schema.json.'
    }
    $nvapiCallJson = Get-Content -Raw -LiteralPath $nvapiCallFixturePath
    if (-not ($nvapiCallJson | Test-Json -SchemaFile $nvapiCallSchemaPath)) {
        throw 'Synthetic NVAPI call report must pass nvapi-call-observation-v1.schema.json.'
    }
    $nvapiCandidateCallJson = Get-Content -Raw -LiteralPath $nvapiCandidateCallFixturePath
    if (-not ($nvapiCandidateCallJson |
            Test-Json -SchemaFile $nvapiCandidateCallSchemaPath)) {
        throw 'Synthetic attached NVAPI candidate report must pass nvapi-candidate-call-observation-v1.schema.json.'
    }
    $nvapiThermChannelJson = Get-Content -Raw -LiteralPath $nvapiThermChannelFixturePath
    if (-not ($nvapiThermChannelJson |
            Test-Json -SchemaFile $nvapiThermChannelSchemaPath)) {
        throw 'Synthetic thermal-channel report must pass nvapi-therm-channel-v2-observation-v1.schema.json.'
    }
    $nvapiThermCorrelationJson = (& $labExecutable `
            correlate-nvapi-therm-channel `
            --observation $nvapiThermChannelFixturePath `
            --gpuz-log $gpuzThermReferenceFixturePath | Out-String)
    if ($LASTEXITCODE -ne 0 -or
        -not ($nvapiThermCorrelationJson |
            Test-Json -SchemaFile $nvapiThermCorrelationSchemaPath)) {
        throw 'Thermal-channel correlation output must pass nvapi-therm-channel-correlation-v1.schema.json.'
    }
    $nvapiThermCorrelation = $nvapiThermCorrelationJson | ConvertFrom-Json
    if ($nvapiThermCorrelation.mapping_status -ne 'matched_external_reference' -or
        $nvapiThermCorrelation.mappings.Count -ne 2 -or
        $nvapiThermCorrelation.mappings[0].semantic_channel -ne
            'gpu_die_temperature' -or
        $nvapiThermCorrelation.mappings[1].semantic_channel -ne
            'gpu_hotspot_temperature') {
        throw 'Thermal-channel correlation must preserve the die and hotspot mapping.'
    }
    $nvapiVoltageObservationJson = Get-Content -Raw -LiteralPath $nvapiVoltageObservationFixturePath
    if (-not ($nvapiVoltageObservationJson |
            Test-Json -SchemaFile $nvapiVoltageObservationSchemaPath)) {
        throw 'Synthetic voltage-status observation must pass its v1 schema.'
    }
    $nvapiVoltageCorrelationJson = (& $labExecutable `
            correlate-nvapi-voltage-status `
            --observation $nvapiVoltageObservationFixturePath `
            --gpuz-log $gpuzVoltageReferenceFixturePath | Out-String)
    if ($LASTEXITCODE -ne 0 -or
        -not ($nvapiVoltageCorrelationJson |
            Test-Json -SchemaFile $nvapiVoltageCorrelationSchemaPath)) {
        throw 'Voltage-status correlation output must pass its v1 schema.'
    }
    $nvapiVoltageCorrelation = $nvapiVoltageCorrelationJson | ConvertFrom-Json
    if ($nvapiVoltageCorrelation.mapping_status -ne 'matched_external_reference' -or
        $nvapiVoltageCorrelation.mapping.semantic_field -ne 'gpu_core_voltage' -or
        $nvapiVoltageCorrelation.mapping.word_index -ne 10 -or
        $nvapiVoltageCorrelation.scale_divisor -ne 1000000) {
        throw 'Voltage-status correlation must preserve the core-voltage mapping and microvolt scale.'
    }
    $nvapiClassificationFixtureJson = Get-Content `
        -Raw `
        -LiteralPath $nvapiClassificationFixturePath
    if (-not ($nvapiClassificationFixtureJson |
            Test-Json -SchemaFile $nvapiClassificationSchemaPath)) {
        throw 'Synthetic NVAPI classification must pass nvapi-interface-classification-v1.schema.json.'
    }
    $nvapiCandidateJson = (& $labExecutable inventory-nvapi-candidates `
            --classification $nvapiClassificationFixturePath `
            --calls $nvapiCallFixturePath | Out-String)
    if ($LASTEXITCODE -ne 0 -or
        -not ($nvapiCandidateJson | Test-Json -SchemaFile $nvapiCandidateSchemaPath)) {
        throw 'NVAPI candidate inventory must pass nvapi-candidate-inventory-v1.schema.json.'
    }
    $nvapiCandidates = $nvapiCandidateJson | ConvertFrom-Json
    if ($nvapiCandidates.executed_candidate_count -ne 2 -or
        $nvapiCandidates.executed_not_in_public_catalog_count -ne 1) {
        throw 'NVAPI candidate inventory must preserve executed public and unidentified targets.'
    }

    $ciWaitHandle = [Threading.EventWaitHandle]::new(
        $false,
        [Threading.EventResetMode]::AutoReset)
    try {
        $ciRawHandle = $ciWaitHandle.SafeWaitHandle.DangerousGetHandle().ToInt64()
        $ciHandleValue = '0x{0:x}' -f $ciRawHandle
        $windowsHandleJson = (& $labExecutable resolve-windows-handle `
                --process-id $PID `
                --handle $ciHandleValue | Out-String)
        if ($LASTEXITCODE -ne 0 -or
            -not ($windowsHandleJson | Test-Json -SchemaFile $windowsHandleSchemaPath)) {
            throw 'Windows handle identity output must pass windows-handle-identity-v1.schema.json.'
        }
        $windowsHandle = $windowsHandleJson | ConvertFrom-Json
        if ($windowsHandle.object_type -ne 'Event' -or
            $null -ne $windowsHandle.object_name -or
            $windowsHandle.process_id -ne $PID) {
            throw 'Windows handle identity must preserve the duplicated test event identity.'
        }
    }
    finally {
        $ciWaitHandle.Dispose()
    }

    $cmakeProject = Get-Content -Raw -LiteralPath (Join-Path $projectRoot 'CMakeLists.txt')
    if ($cmakeProject -notmatch '(?s)project\(\s*rtx-monitor\s+VERSION\s+([0-9]+\.[0-9]+\.[0-9]+)') {
        throw 'Could not read the native project version from CMakeLists.txt.'
    }
    $nativeVersion = $Matches[1]

    [xml]$managedProps = Get-Content -Raw -LiteralPath (Join-Path $projectRoot 'csharp\Directory.Build.props')
    $managedVersion = [string]$managedProps.Project.PropertyGroup.Version
    $managedAssemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName($managedAssembly).Version.ToString(3)
    $storageAssemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName($storageAssembly).Version.ToString(3)
    $serviceAssemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName($serviceAssembly).Version.ToString(3)
    $labAssemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName($labAssembly).Version.ToString(3)
    if ($managedVersion -ne $nativeVersion -or
        $managedAssemblyVersion -ne $nativeVersion -or
        $storageAssemblyVersion -ne $nativeVersion -or
        $serviceAssemblyVersion -ne $nativeVersion -or
        $labAssemblyVersion -ne $nativeVersion) {
        throw "Version mismatch: native=$nativeVersion, managed project=$managedVersion, managed assembly=$managedAssemblyVersion, storage assembly=$storageAssemblyVersion, service assembly=$serviceAssemblyVersion, lab assembly=$labAssemblyVersion."
    }

    $null = & $cppExecutable --once --alert-threshold 80 2>$null
    if ($LASTEXITCODE -eq 0) {
        throw 'C++ must reject --alert-threshold outside --watch.'
    }

    $null = & $csharpExecutable --once --alert-threshold 80 2>$null
    if ($LASTEXITCODE -eq 0) {
        throw 'C# must reject --alert-threshold outside --watch.'
    }

    $cppHysteresisError = (& $cppExecutable --once --alert-hysteresis 0 2>&1 | Out-String)
    if ($LASTEXITCODE -eq 0 -or
        $cppHysteresisError -notmatch '--alert-hysteresis requires --alert-threshold') {
        throw 'C++ must reject --alert-hysteresis even when its explicit value is zero.'
    }

    $csharpHysteresisError = (& $csharpExecutable --once --alert-hysteresis 0 2>&1 | Out-String)
    if ($LASTEXITCODE -eq 0 -or
        $csharpHysteresisError -notmatch '--alert-hysteresis exige --alert-threshold') {
        throw 'C# must reject --alert-hysteresis even when its explicit value is zero.'
    }

    $historyWithoutDatabase = (& $csharpExecutable --history 2>&1 | Out-String)
    if ($LASTEXITCODE -eq 0 -or $historyWithoutDatabase -notmatch 'exigem --database PATH') {
        throw 'C# history must require an explicit database path.'
    }

    $missingDatabase = Join-Path `
        ([System.IO.Path]::GetTempPath()) `
        ("rtxmon-ci-missing-{0}.db" -f [Guid]::NewGuid().ToString('N'))
    $missingHistory = (& $csharpExecutable --history --database $missingDatabase --json 2>&1 | Out-String)
    if ($LASTEXITCODE -eq 0 -or
        $missingHistory -notmatch 'banco de telemetria não existe' -or
        (Test-Path -LiteralPath $missingDatabase)) {
        throw 'C# history must fail without creating a missing database.'
    }

    # The two checks above intentionally invoke a command that exits
    # non-zero. Run these successful checks last so a passing script always
    # ends on a zero-exit-code native command.
    Invoke-Checked -Description 'C++ help without GPU' -Command {
        & $cppExecutable --help | Out-Null
    }

    Invoke-Checked -Description 'Offline VBIOS help without GPU' -Command {
        & $vbiosExecutable --help | Out-Null
    }

    Invoke-Checked -Description 'C# help without GPU' -Command {
        & $csharpExecutable --help | Out-Null
    }

    Invoke-Checked -Description 'Laboratory help without GPU' -Command {
        & $labExecutable --help | Out-Null
    }

    if (-not (Test-Path -LiteralPath $serviceExecutable -PathType Leaf)) {
        throw "Service executable is missing: $serviceExecutable"
    }

    if (-not (Test-Path -LiteralPath $labExecutable -PathType Leaf)) {
        throw "Laboratory executable is missing: $labExecutable"
    }

    Write-Host 'Hardware-independent verification passed.'
    Write-Host 'C/C++: build with warnings as errors and 12 CTest tests, including VBIOS and RM thermal protocol.'
    Write-Host 'C#: build, sampler, alert, SQLite storage, local service, 34 laboratory tests, GPU-Z import/correlation, NVAPI thermal mapping, Windows handle identity, markers, and formatting.'
    Write-Host 'Schemas: stable telemetry plus v0.8 artifact, experiment, marker, analysis, VBIOS, GPU-Z, NVAPI, bounded IOCTL, and Windows handle contracts.'
    Write-Host "Version parity: C/C++ and C# $nativeVersion."
}
finally {
    if ($null -ne $labCiRoot -and $null -ne $labCiPackage) {
        Remove-LabCiPackageSafely -Root $labCiRoot -Package $labCiPackage
    }
    Pop-Location
}
