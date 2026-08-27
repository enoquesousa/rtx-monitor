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
$numericSeriesSchemaPath = Join-Path $projectRoot 'docs\schema\numeric-series-v1.schema.json'
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
$nvapiThermChannelV2SchemaPath = Join-Path $projectRoot 'docs\schema\nvapi-therm-channel-v2-observation-v2.schema.json'
$nvapiCoolerStatusSchemaPath = Join-Path $projectRoot 'docs\schema\nvapi-cooler-status-v1-observation-v1.schema.json'
$nvapiCoolerStatusV2SchemaPath = Join-Path $projectRoot 'docs\schema\nvapi-cooler-status-v1-observation-v2.schema.json'
$nvapiThermCorrelationSchemaPath = Join-Path $projectRoot 'docs\schema\nvapi-therm-channel-correlation-v1.schema.json'
$nvapiThermCorrelationV2SchemaPath = Join-Path $projectRoot 'docs\schema\nvapi-therm-channel-correlation-v2.schema.json'
$nvapiVoltageObservationSchemaPath = Join-Path $projectRoot 'docs\schema\nvapi-voltage-status-v1-observation-v1.schema.json'
$nvapiVoltageCorrelationSchemaPath = Join-Path $projectRoot 'docs\schema\nvapi-voltage-status-correlation-v1.schema.json'
$nvapiVoltageObservationV2SchemaPath = Join-Path $projectRoot 'docs\schema\nvapi-voltage-status-v1-observation-v2.schema.json'
$nvapiVoltageCorrelationV2SchemaPath = Join-Path $projectRoot 'docs\schema\nvapi-voltage-status-correlation-v2.schema.json'
$privateThermalSampleSchemaPath = Join-Path $projectRoot 'docs\schema\private-thermal-sample-v1.schema.json'
$privateVoltageSampleSchemaPath = Join-Path $projectRoot 'docs\schema\private-voltage-sample-v1.schema.json'
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
$nvapiThermChannelV2FixturePath = Join-Path $projectRoot 'csharp\RtxMonitor.Lab.Tests\Fixtures\nvapi-therm-channel-v2-observation-v2.json'
$nvapiCoolerStatusFixturePath = Join-Path $projectRoot 'csharp\RtxMonitor.Lab.Tests\Fixtures\nvapi-cooler-status-v1-observation.json'
$nvapiCoolerStatusV2FixturePath = Join-Path $projectRoot 'csharp\RtxMonitor.Lab.Tests\Fixtures\nvapi-cooler-status-v1-observation-v2.json'
$gpuzThermReferenceFixturePath = Join-Path $projectRoot 'csharp\RtxMonitor.Lab.Tests\Fixtures\gpuz-therm-channel-reference.csv'
$gpuzThermReferenceV2FixturePath = Join-Path $projectRoot 'csharp\RtxMonitor.Lab.Tests\Fixtures\gpuz-therm-channel-reference-v2.csv'
$nvapiVoltageObservationFixturePath = Join-Path $projectRoot 'csharp\RtxMonitor.Lab.Tests\Fixtures\nvapi-voltage-status-v1-observation.json'
$nvapiVoltageObservationV2FixturePath = Join-Path $projectRoot 'csharp\RtxMonitor.Lab.Tests\Fixtures\nvapi-voltage-status-v1-observation-v2.json'
$gpuzVoltageReferenceFixturePath = Join-Path $projectRoot 'csharp\RtxMonitor.Lab.Tests\Fixtures\gpuz-voltage-reference.csv'
$hwinfoVoltageReferenceFixturePath = Join-Path $projectRoot 'csharp\RtxMonitor.Lab.Tests\Fixtures\hwinfo-voltage-reference.csv'
$numericSeriesFixturePath = Join-Path $projectRoot 'csharp\RtxMonitor.Lab.Tests\Fixtures\numeric-series-v1.json'
$experimentManifestDraftFixturePath = Join-Path $projectRoot 'csharp\RtxMonitor.Lab.Tests\Fixtures\experiment-manifest-draft-v1.json'
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

function Test-PrivateThermalSampleSemantics {
    param([Parameter(Mandatory)][object]$Sample)

    $expectedDelta = [double]$Sample.gpu_hotspot_temperature_c -
        [double]$Sample.gpu_die_temperature_c
    return [Math]::Abs([double]$Sample.delta_c - $expectedDelta) -le 0.0005
}

function Test-PrivateVoltageSampleSemantics {
    param([Parameter(Mandatory)][object]$Sample)

    $expectedVolts = [double]$Sample.gpu_core_voltage_microvolts / 1000000.0
    return [Math]::Abs([double]$Sample.gpu_core_voltage_v - $expectedVolts) -le 0.0000000005
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
        $experimentDraft = Join-Path $resolvedRoot 'experiment-draft.json'
        $experimentManifest = Join-Path $resolvedRoot 'experiment-manifest.json'
        $optionalRootFiles = @(
            $experimentDraft,
            $experimentManifest
        ) | Where-Object { [System.IO.File]::Exists($_) }
        foreach ($file in @($payload, $manifest) + $optionalRootFiles) {
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
            Select-Object -First 4)
        $packageEntries = @([System.IO.Directory]::EnumerateFileSystemEntries($Package) |
            Select-Object -First 3)
        $artifactEntries = @([System.IO.Directory]::EnumerateFileSystemEntries(
                $artifactDirectory) |
            Select-Object -First 2)
        $expectedRootEntries = @($Package) + $optionalRootFiles
        if ($rootEntries.Count -ne $expectedRootEntries.Count -or
            $packageEntries.Count -ne 2 -or
            $artifactEntries.Count -ne 1 -or
            @($rootEntries | Where-Object { $_ -notin $expectedRootEntries }).Count -ne 0 -or
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
        foreach ($file in $optionalRootFiles) {
            [System.IO.File]::Delete($file)
        }
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
        'capture-gpuz-nvapi-cooler-status-v1.ps1',
        'capture-gpuz-nvapi-ids.ps1',
        'capture-gpuz-nvapi-therm-channel-v2.ps1',
        'capture-gpuz-nvapi-voltage-status-v1.ps1',
        'capture-gpuz-procmon.ps1',
        'check-lab-access.ps1',
        'collect-gpuz-driver.ps1'
    )) {
        $scriptPath = Join-Path $PSScriptRoot $scriptName
        $tokens = $null
        $parseErrors = $null
        $scriptAst = [System.Management.Automation.Language.Parser]::ParseFile(
            $scriptPath,
            [ref]$tokens,
            [ref]$parseErrors)
        if ($parseErrors.Count -ne 0) {
            throw "PowerShell syntax validation failed for '$scriptName': $($parseErrors[0].Message)"
        }

        if ($scriptName -in @(
                'capture-gpuz-nvapi-cooler-status-v1.ps1',
                'capture-gpuz-nvapi-therm-channel-v2.ps1',
                'capture-gpuz-nvapi-voltage-status-v1.ps1'
            )) {
            $snapshotDefinitions = @($scriptAst.FindAll(
                    {
                        param($node)
                        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                            $node.Name -eq 'Read-AnchoredJsonSnapshot'
                    },
                    $true))
            $snapshotCalls = @($scriptAst.FindAll(
                    {
                        param($node)
                        $node -is [System.Management.Automation.Language.CommandAst] -and
                            $node.GetCommandName() -eq 'Read-AnchoredJsonSnapshot'
                    },
                    $true))
            $unsafeEvidenceReads = @($scriptAst.FindAll(
                    {
                        param($node)
                        $node -is [System.Management.Automation.Language.CommandAst] -and
                            $node.GetCommandName() -in @('Get-Content', 'Get-FileHash') -and
                            $node.Extent.Text -match
                                '\$(CandidateInventoryPath|PriorObservationPath)\b'
                    },
                    $true))
            if ($snapshotDefinitions.Count -ne 1 -or
                $snapshotCalls.Count -ne 2 -or
                $unsafeEvidenceReads.Count -ne 0) {
                throw "Anchored JSON snapshot validation failed for '$scriptName'."
            }

            $expectedDebugLogLimit = switch ($scriptName) {
                'capture-gpuz-nvapi-therm-channel-v2.ps1' { '128MB' }
                default { '16MB' }
            }
            $debugLogLimitAssignments = @($scriptAst.FindAll(
                    {
                        param($node)
                        $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                            $node.Left.Extent.Text -eq '$maximumDebugLogSizeBytes'
                    },
                    $true))
            $boundedWaitDefinitions = @($scriptAst.FindAll(
                    {
                        param($node)
                        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                            $node.Name -eq 'Wait-BoundedCaptureInterval' -and
                            $node.Extent.Text -match
                                '\$debugLog\.Length\s+-gt\s+\$MaximumSizeBytes'
                    },
                    $true))
            $boundedWaitCalls = @($scriptAst.FindAll(
                    {
                        param($node)
                        $node -is [System.Management.Automation.Language.CommandAst] -and
                            $node.GetCommandName() -eq 'Wait-BoundedCaptureInterval'
                    },
                    $true))
            $detachDefinitions = @($scriptAst.FindAll(
                    {
                        param($node)
                        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                            $node.Name -eq 'Invoke-CdbDetach' -and
                            $node.Extent.Text -match
                                '(?s)Assert-RegularLocalFile.*-MaximumSizeBytes\s+\$MaximumSizeBytes.*Get-Content\s+-LiteralPath\s+\$DebugLogPath\s+-Raw'
                    },
                    $true))
            $detachCalls = @($scriptAst.FindAll(
                    {
                        param($node)
                        $node -is [System.Management.Automation.Language.CommandAst] -and
                            $node.GetCommandName() -eq 'Invoke-CdbDetach' -and
                            $node.Extent.Text -match
                                '-MaximumSizeBytes\s+\$maximumDebugLogSizeBytes'
                    },
                    $true))
            $readyLoops = @($scriptAst.FindAll(
                    {
                        param($node)
                        $node -is [System.Management.Automation.Language.DoWhileStatementAst] -and
                            $node.Extent.Text -match 'RTXMON_ATTACH_READY' -and
                            $node.Extent.Text -match
                                '\$debugLog\.Length\s+-gt\s+\$maximumDebugLogSizeBytes'
                    },
                    $true))
            if ($debugLogLimitAssignments.Count -ne 1 -or
                $debugLogLimitAssignments[0].Right.Extent.Text -ne
                    $expectedDebugLogLimit -or
                $boundedWaitDefinitions.Count -ne 1 -or
                $boundedWaitCalls.Count -ne 2 -or
                $detachDefinitions.Count -ne 1 -or
                $detachCalls.Count -ne 2 -or
                $readyLoops.Count -ne 1) {
                throw "Debugger transcript bound validation failed for '$scriptName'."
            }

            $expectedSealedNames = switch ($scriptName) {
                'capture-gpuz-nvapi-therm-channel-v2.ps1' {
                    @{
                        '$sealedGpuzName' = 'sealed-gpuz-thermal-reference.csv'
                    }
                }
                'capture-gpuz-nvapi-voltage-status-v1.ps1' {
                    @{
                        '$sealedGpuzName' = 'sealed-gpuz-voltage-reference.csv'
                        '$sealedHwinfoName' = 'sealed-hwinfo-voltage-reference.csv'
                    }
                }
                default {
                    @{}
                }
            }
            foreach ($sealedName in $expectedSealedNames.GetEnumerator()) {
                $assignments = @($scriptAst.FindAll(
                        {
                            param($node)
                            $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                                $node.Left.Extent.Text -eq $sealedName.Key
                        },
                        $true))
                if ($assignments.Count -ne 1 -or
                    $assignments[0].Right.Extent.Text -ne "'$($sealedName.Value)'") {
                    throw "Sealed reference name validation failed for '$scriptName'."
                }
            }

            if ($scriptName -in @(
                    'capture-gpuz-nvapi-therm-channel-v2.ps1',
                    'capture-gpuz-nvapi-voltage-status-v1.ps1'
                )) {
                $gpuzLimitAssignments = @($scriptAst.FindAll(
                        {
                            param($node)
                            $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                                $node.Left.Extent.Text -eq '$maximumGpuzPrefixSizeBytes'
                        },
                        $true))
                $gpuzProbeDefinitions = @($scriptAst.FindAll(
                        {
                            param($node)
                            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                                $node.Name -eq 'Get-GpuzLogProbe' -and
                                $node.Extent.Text -match
                                    '\$complete\.size_bytes\s+-gt\s+\$maximumGpuzPrefixSizeBytes'
                        },
                        $true))
                if ($gpuzLimitAssignments.Count -ne 1 -or
                    $gpuzLimitAssignments[0].Right.Extent.Text -ne '16MB' -or
                    $gpuzProbeDefinitions.Count -ne 1) {
                    throw "GPU-Z prefix limit validation failed for '$scriptName'."
                }
            }

            if ($scriptName -eq 'capture-gpuz-nvapi-voltage-status-v1.ps1') {
                $hwinfoLimitAssignments = @($scriptAst.FindAll(
                        {
                            param($node)
                            $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                                $node.Left.Extent.Text -eq '$maximumHwinfoPrefixSizeBytes'
                        },
                        $true))
                $hwinfoProbeDefinitions = @($scriptAst.FindAll(
                        {
                            param($node)
                            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                                $node.Name -eq 'Get-HwinfoLogProbe' -and
                                $node.Extent.Text -match
                                    '\$complete\.size_bytes\s+-gt\s+\$maximumHwinfoPrefixSizeBytes'
                        },
                        $true))
                $functionRangeGuards = @($scriptAst.FindAll(
                        {
                            param($node)
                            $node -is [System.Management.Automation.Language.IfStatementAst] -and
                                $node.Extent.Text -match
                                    '\$functionRva\s+-ge\s+\(\[uint64\]\$loadedModuleEnd\s+-\s+\$loadedModuleStart\)'
                        },
                        $true))
                if ($hwinfoLimitAssignments.Count -ne 1 -or
                    $hwinfoLimitAssignments[0].Right.Extent.Text -ne '64MB' -or
                    $hwinfoProbeDefinitions.Count -ne 1 -or
                    $functionRangeGuards.Count -ne 1) {
                    throw 'Voltage capture must bound HWiNFO and prove that the allowlisted function RVA is inside the loaded module range.'
                }
            }

            if ($scriptName -eq 'capture-gpuz-nvapi-cooler-status-v1.ps1') {
                $maximumRecordAssignments = @($scriptAst.FindAll(
                        {
                            param($node)
                            $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                                $node.Left.Extent.Text -eq '$maximumCaptureRecords'
                        },
                        $true))
                $recordCountGuards = @($scriptAst.FindAll(
                        {
                            param($node)
                            $node -is [System.Management.Automation.Language.IfStatementAst] -and
                                $node.Extent.Text -match
                                    '\$hitRecords\.Count\s+-gt\s+\$maximumCaptureRecords'
                        },
                        $true))
                if ($maximumRecordAssignments.Count -ne 1 -or
                    $maximumRecordAssignments[0].Right.Extent.Text -ne '1024' -or
                    $recordCountGuards.Count -ne 1) {
                    throw 'Cooler capture must bound its materialized records.'
                }
            }
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
    $evidenceRunEventVersions = @($evidenceSchema.'$defs'.run.properties.event_schema_version.enum)
    if ($evidenceSchema.properties.evidence_schema_version.const -ne 1 -or
        $evidenceSchema.properties.store_schema_version.const -ne 1 -or
        'telemetry-event-v2.schema.json' -notin $evidenceEventRefs -or
        'telemetry-event-v3.schema.json' -notin $evidenceEventRefs -or
        'telemetry-event-v4.schema.json' -notin $evidenceEventRefs -or
        2 -notin $evidenceRunEventVersions -or
        3 -notin $evidenceRunEventVersions -or
        4 -notin $evidenceRunEventVersions) {
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
    $numericSeriesSchema = Get-Content -Raw -LiteralPath $numericSeriesSchemaPath |
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
    $nvapiThermChannelV2Schema = Get-Content `
        -Raw `
        -LiteralPath $nvapiThermChannelV2SchemaPath |
        ConvertFrom-Json
    $nvapiCoolerStatusSchema = Get-Content `
        -Raw `
        -LiteralPath $nvapiCoolerStatusSchemaPath |
        ConvertFrom-Json
    $nvapiCoolerStatusV2Schema = Get-Content `
        -Raw `
        -LiteralPath $nvapiCoolerStatusV2SchemaPath |
        ConvertFrom-Json
    $nvapiThermCorrelationSchema = Get-Content `
        -Raw `
        -LiteralPath $nvapiThermCorrelationSchemaPath |
        ConvertFrom-Json
    $nvapiThermCorrelationV2Schema = Get-Content `
        -Raw `
        -LiteralPath $nvapiThermCorrelationV2SchemaPath |
        ConvertFrom-Json
    $nvapiVoltageObservationV2Schema = Get-Content `
        -Raw `
        -LiteralPath $nvapiVoltageObservationV2SchemaPath |
        ConvertFrom-Json
    $nvapiVoltageCorrelationV2Schema = Get-Content `
        -Raw `
        -LiteralPath $nvapiVoltageCorrelationV2SchemaPath |
        ConvertFrom-Json
    $privateThermalSampleSchema = Get-Content `
        -Raw `
        -LiteralPath $privateThermalSampleSchemaPath |
        ConvertFrom-Json
    $privateVoltageSampleSchema = Get-Content `
        -Raw `
        -LiteralPath $privateVoltageSampleSchemaPath |
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
        $numericSeriesSchema.properties.schema_version.const -ne 1 -or
        $numericSeriesSchema.properties.source_kind.const -ne 'numeric_time_series' -or
        $experimentManifestSchema.'$defs'.marker.required -notcontains
            'monotonic_frequency_hz' -or
        $experimentManifestSchema.'$defs'.artifactPackage.required -notcontains
            'scenario_id' -or
        $analysisReportSchema.'$defs'.candidate.properties.source_kind.enum -notcontains
            'private_interface' -or
        $analysisReportSchema.'$defs'.candidate.required -notcontains 'value_unit' -or
        $analysisReportSchema.'$defs'.statistics.required -notcontains 'minimum_delta' -or
        $analysisReportSchema.'$defs'.statistics.required -notcontains 'maximum_delta' -or
        $analysisReportSchema.'$defs'.statistics.required -notcontains 'mean_delta' -or
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
        $nvapiThermChannelV2Schema.properties.schema_version.const -ne 2 -or
        $nvapiThermChannelV2Schema.properties.profile.properties.gpu.properties.uuid.const -ne
            'GPU-fca3647e-8390-15a8-f23b-d0f870c9accd' -or
        $nvapiThermChannelV2Schema.properties.profile.required -notcontains
            'loaded_nvapi_module' -or
        $nvapiThermChannelV2Schema.'$defs'.growing_log_reference.required -notcontains
            'sealed_relative_path' -or
        $nvapiCoolerStatusSchema.properties.schema_version.const -ne 1 -or
        $nvapiCoolerStatusSchema.required -contains 'gpu_profile' -or
        $null -ne $nvapiCoolerStatusSchema.properties.gpu_profile -or
        $nvapiCoolerStatusV2Schema.properties.schema_version.const -ne 2 -or
        $nvapiCoolerStatusV2Schema.properties.source_kind.const -ne
            'nvapi_cooler_status_v1_observation' -or
        $nvapiCoolerStatusV2Schema.properties.gpu_profile.properties.gpu_uuid.const -ne
            'GPU-fca3647e-8390-15a8-f23b-d0f870c9accd' -or
        $nvapiCoolerStatusV2Schema.properties.candidate_inventory_sha256.const -ne
            '3aaada9b367dacca7cf74511bae8532bd79b7f8bd06b9bb609056f3d9da1f1d7' -or
        $nvapiCoolerStatusV2Schema.properties.prior_observation_sha256.const -ne
            'f580f67da61df2287257fb023fe277d310fdf424f588bbd96d01ac01433f8de2' -or
        $nvapiCoolerStatusV2Schema.required -notcontains 'identity_probe_sha256' -or
        $nvapiCoolerStatusV2Schema.required -notcontains 'loaded_nvapi_module' -or
        $nvapiCoolerStatusV2Schema.properties.loaded_nvapi_module.properties.file_sha256.const -ne
            'fbc9aed43bfa5bda19b7f83a809a081a0ce454b6d6003dcabc565ecb3e6afdaf' -or
        $nvapiCoolerStatusV2Schema.properties.loaded_nvapi_module.properties.proof_source.const -ne
            'cdb_modload_target_image' -or
        $nvapiThermCorrelationSchema.properties.source_kind.const -ne
            'nvapi_therm_channel_reference_correlation' -or
        $nvapiThermCorrelationSchema.properties.interface_id.const -ne '0x65fe3aad' -or
        $nvapiThermCorrelationSchema.properties.tolerance_celsius.const -ne 0.051 -or
        $nvapiThermCorrelationV2Schema.properties.schema_version.const -ne 2 -or
        $nvapiThermCorrelationV2Schema.properties.source_kind.const -ne
            'nvapi_therm_channel_reference_correlation' -or
        $nvapiThermCorrelationV2Schema.'$defs'.selection.required -notcontains
            'selected_session_index' -or
        $nvapiThermCorrelationV2Schema.'$defs'.selection.required -notcontains
            'rejected_session_indices_with_invalid_exact_channel_data' -or
        $nvapiVoltageObservationV2Schema.properties.schema_version.const -ne 2 -or
        $nvapiVoltageObservationV2Schema.properties.source_kind.const -ne
            'nvapi_voltage_status_v1_observation' -or
        $nvapiVoltageObservationV2Schema.properties.profile.properties.interface_id.const -ne
            '0x465f9bcf' -or
        $nvapiVoltageObservationV2Schema.properties.profile.properties.caller_rva.const -ne
            '0x0021cee7' -or
        $nvapiVoltageCorrelationV2Schema.properties.schema_version.const -ne 2 -or
        $nvapiVoltageCorrelationV2Schema.properties.source_kind.const -ne
            'nvapi_voltage_status_reference_correlation' -or
        $privateThermalSampleSchema.properties.schema_version.const -ne 1 -or
        $privateThermalSampleSchema.properties.source_kind.const -ne
            'nvapi_thermal_channel' -or
        $privateThermalSampleSchema.properties.gpu_uuid.const -ne
            'GPU-fca3647e-8390-15a8-f23b-d0f870c9accd' -or
        $privateThermalSampleSchema.properties.gpu_index.maximum -ne 63 -or
        $privateThermalSampleSchema.properties.profile_evidence_stage.const -ne
            'matched_external_reference' -or
        $privateThermalSampleSchema.properties.profile_name.const -ne
            'rtx3060-2504-1536-vbios-94.06.25.00.fc-driver-610.88' -or
        $privateThermalSampleSchema.properties.interface_id.const -ne '0x65fe3aad' -or
        $privateThermalSampleSchema.properties.structure_version.const -ne '0x000200a8' -or
        $privateThermalSampleSchema.properties.nvapi_module_sha256.const -ne
            'df6455ccf83e43cfe68f405af1eec4e053c7f95da998bf358053b7583980c2f4' -or
        $privateThermalSampleSchema.properties.function_rva.const -ne '0x001e0bc0' -or
        $privateThermalSampleSchema.properties.delta_c.minimum -ne 0 -or
        $privateThermalSampleSchema.properties.delta_c.maximum -ne 80 -or
        $privateThermalSampleSchema.required -notcontains 'monotonic_ns' -or
        $privateThermalSampleSchema.required -notcontains 'monotonic_frequency_hz' -or
        $privateThermalSampleSchema.required -contains 'confidence' -or
        $privateVoltageSampleSchema.properties.schema_version.const -ne 1 -or
        $privateVoltageSampleSchema.properties.source_kind.const -ne
            'nvapi_voltage_status' -or
        $privateVoltageSampleSchema.properties.gpu_uuid.const -ne
            'GPU-fca3647e-8390-15a8-f23b-d0f870c9accd' -or
        $privateVoltageSampleSchema.properties.gpu_index.maximum -ne 63 -or
        $privateVoltageSampleSchema.properties.profile_evidence_stage.const -ne
            'matched_external_reference' -or
        $privateVoltageSampleSchema.properties.profile_name.const -ne
            'rtx3060-2504-1536-vbios-94.06.25.00.fc-driver-610.88' -or
        $privateVoltageSampleSchema.properties.interface_id.const -ne '0x465f9bcf' -or
        $privateVoltageSampleSchema.properties.structure_version.const -ne '0x0001004c' -or
        $privateVoltageSampleSchema.properties.nvapi_module_sha256.const -ne
            'df6455ccf83e43cfe68f405af1eec4e053c7f95da998bf358053b7583980c2f4' -or
        $privateVoltageSampleSchema.properties.function_rva.const -ne '0x001c9070' -or
        $privateVoltageSampleSchema.required -notcontains 'monotonic_ns' -or
        $privateVoltageSampleSchema.required -notcontains 'monotonic_frequency_hz' -or
        $privateVoltageSampleSchema.required -contains 'confidence' -or
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
        $numericSeriesSchema,
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
        $nvapiThermChannelV2Schema,
        $nvapiCoolerStatusSchema,
        $nvapiCoolerStatusV2Schema,
        $nvapiThermCorrelationSchema,
        $nvapiThermCorrelationV2Schema,
        $nvapiVoltageObservationV2Schema,
        $nvapiVoltageCorrelationV2Schema,
        $privateThermalSampleSchema,
        $privateVoltageSampleSchema,
        $gpuzDeviceIoSchema,
        $gpuzDeviceInputSchema,
        $windowsHandleSchema
    )) {
        if ([string]$laboratorySchema.'$id' -notlike 'urn:rtx-monitor:schema:*') {
            throw 'v0.8 laboratory schemas must use stable offline URN identifiers.'
        }
    }

    $privateThermalSampleJson = ([ordered]@{
            schema_version = 1
            source_kind = 'nvapi_thermal_channel'
            gpu_index = 0
            gpu_uuid = 'GPU-fca3647e-8390-15a8-f23b-d0f870c9accd'
            captured_at_utc = '2026-08-27T21:30:00.0000000Z'
            captured_at_unix_ms = 1787866200000
            monotonic_ns = 123456789000
            monotonic_frequency_hz = 10000000
            gpu_die_temperature_c = 40.0
            gpu_hotspot_temperature_c = 50.0
            delta_c = 10.0
            native_status = 0
            profile_evidence_stage = 'matched_external_reference'
            profile_name = 'rtx3060-2504-1536-vbios-94.06.25.00.fc-driver-610.88'
            interface_id = '0x65fe3aad'
            structure_version = '0x000200a8'
            nvapi_module_sha256 = 'df6455ccf83e43cfe68f405af1eec4e053c7f95da998bf358053b7583980c2f4'
            function_rva = '0x001e0bc0'
        } | ConvertTo-Json -Depth 5)
    if (-not ($privateThermalSampleJson |
            Test-Json -SchemaFile $privateThermalSampleSchemaPath)) {
        throw 'Synthetic direct thermal sample must pass private-thermal-sample-v1.schema.json.'
    }
    $privateThermalSample = $privateThermalSampleJson | ConvertFrom-Json
    if (-not (Test-PrivateThermalSampleSemantics -Sample $privateThermalSample)) {
        throw 'Direct thermal producer must preserve delta = hotspot - die.'
    }
    $contradictoryThermalSample = $privateThermalSampleJson | ConvertFrom-Json
    $contradictoryThermalSample.delta_c = 0.0
    if (Test-PrivateThermalSampleSemantics -Sample $contradictoryThermalSample) {
        throw 'Direct thermal semantic validation must reject a contradictory derived delta.'
    }
    $wrongUuidThermalSample = $privateThermalSampleJson | ConvertFrom-Json
    $wrongUuidThermalSample.gpu_uuid = 'GPU-FCA3647E-8390-15A8-F23B-D0F870C9ACCD'
    if (($wrongUuidThermalSample | ConvertTo-Json -Depth 5) |
            Test-Json -SchemaFile $privateThermalSampleSchemaPath -ErrorAction SilentlyContinue) {
        throw 'Direct thermal schema must reject a non-canonical physical GPU UUID.'
    }
    $invalidDeltaThermalSample = $privateThermalSampleJson | ConvertFrom-Json
    $invalidDeltaThermalSample.delta_c = 80.001
    if (($invalidDeltaThermalSample | ConvertTo-Json -Depth 5) |
            Test-Json -SchemaFile $privateThermalSampleSchemaPath -ErrorAction SilentlyContinue) {
        throw 'Direct thermal schema must reject a delta outside the native profile bound.'
    }

    $privateVoltageSampleJson = ([ordered]@{
            schema_version = 1
            source_kind = 'nvapi_voltage_status'
            gpu_index = 0
            gpu_uuid = 'GPU-fca3647e-8390-15a8-f23b-d0f870c9accd'
            captured_at_utc = '2026-08-27T21:30:00.0000000Z'
            captured_at_unix_ms = 1787866200000
            monotonic_ns = 123456789000
            monotonic_frequency_hz = 10000000
            gpu_core_voltage_microvolts = 956250
            gpu_core_voltage_v = 0.95625
            native_status = 0
            profile_evidence_stage = 'matched_external_reference'
            profile_name = 'rtx3060-2504-1536-vbios-94.06.25.00.fc-driver-610.88'
            interface_id = '0x465f9bcf'
            structure_version = '0x0001004c'
            nvapi_module_sha256 = 'df6455ccf83e43cfe68f405af1eec4e053c7f95da998bf358053b7583980c2f4'
            function_rva = '0x001c9070'
        } | ConvertTo-Json -Depth 5)
    if (-not ($privateVoltageSampleJson |
            Test-Json -SchemaFile $privateVoltageSampleSchemaPath)) {
        throw 'Synthetic direct voltage sample must pass private-voltage-sample-v1.schema.json.'
    }
    $privateVoltageSample = $privateVoltageSampleJson | ConvertFrom-Json
    if (-not (Test-PrivateVoltageSampleSemantics -Sample $privateVoltageSample)) {
        throw 'Direct voltage producer must preserve volts = microvolts / 1000000.'
    }
    $contradictoryVoltageSample = $privateVoltageSampleJson | ConvertFrom-Json
    $contradictoryVoltageSample.gpu_core_voltage_v = 1.5
    if (Test-PrivateVoltageSampleSemantics -Sample $contradictoryVoltageSample) {
        throw 'Direct voltage semantic validation must reject contradictory derived volts.'
    }
    $invalidIndexVoltageSample = $privateVoltageSampleJson | ConvertFrom-Json
    $invalidIndexVoltageSample.gpu_index = 64
    if (($invalidIndexVoltageSample | ConvertTo-Json -Depth 5) |
            Test-Json -SchemaFile $privateVoltageSampleSchemaPath -ErrorAction SilentlyContinue) {
        throw 'Direct voltage schema must reject an index beyond the native NVAPI handle bound.'
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

    $numericSeriesJson = Get-Content -Raw -LiteralPath $numericSeriesFixturePath
    if (-not ($numericSeriesJson | Test-Json -SchemaFile $numericSeriesSchemaPath)) {
        throw 'Synthetic numeric series must pass numeric-series-v1.schema.json.'
    }

    $labCreateJson = (& $labExecutable create `
            --input $numericSeriesFixturePath `
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

    $labExperimentDraftPath = Join-Path $labCiRoot 'experiment-draft.json'
    $labExperimentManifestPath = Join-Path $labCiRoot 'experiment-manifest.json'
    $labExperimentDraftJson = (Get-Content `
            -Raw `
            -LiteralPath $experimentManifestDraftFixturePath).Replace(
            ('0' * 64),
            [string]$labCreate.manifest_sha256).Replace(
            '"relative_path": "series-package"',
            '"relative_path": "package"')
    [System.IO.File]::WriteAllText(
        $labExperimentDraftPath,
        $labExperimentDraftJson,
        [System.Text.UTF8Encoding]::new($false))
    $labExperimentManifestJson = (& $labExecutable finalize-experiment-manifest `
            --input $labExperimentDraftPath `
            --package-root $labCiRoot | Out-String)
    if ($LASTEXITCODE -ne 0 -or
        -not ($labExperimentManifestJson |
            Test-Json -SchemaFile $experimentManifestSchemaPath)) {
        throw 'Experiment manifest output must pass experiment-manifest-v1.schema.json.'
    }
    $labExperimentManifest = $labExperimentManifestJson | ConvertFrom-Json
    if ($labExperimentManifest.status -ne 'completed' -or
        $labExperimentManifest.artifact_packages.Count -ne 1 -or
        $labExperimentManifest.artifact_packages[0].manifest_sha256 -ne
            $labCreate.manifest_sha256 -or
        $labExperimentManifest.artifact_packages[0].scenario_id -ne
            'synthetic-transition' -or
        $labExperimentManifest.markers[0].monotonic_frequency_hz -ne
            $labExperimentManifest.timebase.monotonic_frequency_hz) {
        throw 'Experiment manifest must preserve the verified package, scenario binding, and synchronized marker timebase.'
    }
    $emptyCompletedManifest = $labExperimentManifestJson | ConvertFrom-Json
    $emptyCompletedManifest.artifact_packages = @()
    if (($emptyCompletedManifest | ConvertTo-Json -Depth 20) |
            Test-Json -SchemaFile $experimentManifestSchemaPath -ErrorAction SilentlyContinue) {
        throw 'Completed experiment manifests must require at least one artifact package.'
    }
    $unboundPackageManifest = $labExperimentManifestJson | ConvertFrom-Json
    $unboundPackageManifest.artifact_packages[0].PSObject.Properties.Remove('scenario_id')
    if (($unboundPackageManifest | ConvertTo-Json -Depth 20) |
            Test-Json -SchemaFile $experimentManifestSchemaPath -ErrorAction SilentlyContinue) {
        throw 'Experiment artifact packages must require an explicit nullable scenario_id.'
    }
    [System.IO.File]::WriteAllText(
        $labExperimentManifestPath,
        $labExperimentManifestJson,
        [System.Text.UTF8Encoding]::new($false))
    $labExperimentManifestSha256 = (Get-FileHash `
            -LiteralPath $labExperimentManifestPath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
    $labAnalysisJson = (& $labExecutable analyze-experiment-series `
            --manifest $labExperimentManifestPath `
            --expected-manifest-sha256 $labExperimentManifestSha256 `
            --package-root $labCiRoot `
            --series-package package `
            --max-lag-samples 2 `
            --analysis-id 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee' `
            --created-at-utc '2026-08-27T12:20:00.0000000Z' | Out-String)
    if ($LASTEXITCODE -ne 0 -or
        -not ($labAnalysisJson | Test-Json -SchemaFile $analysisReportSchemaPath)) {
        throw 'Offline series analysis output must pass analysis-report-v1.schema.json.'
    }
    $labAnalysis = $labAnalysisJson | ConvertFrom-Json
    $labCandidate = $labAnalysis.candidates[0]
    if ($labCandidate.stage -ne 'raw_unknown' -or
        $null -ne $labCandidate.physical_name -or
        $labCandidate.value_unit -ne 'V' -or
        $labAnalysis.analyzer.parameters.scenario_id -ne 'synthetic-transition' -or
        $labAnalysis.analyzer.parameters.maximum_pair_evaluations -ne 10000000 -or
        $labCandidate.statistics.sample_count -ne 8 -or
        $labCandidate.statistics.update_period_ms -ne 1000 -or
        $labCandidate.statistics.minimum_delta -ne -1 -or
        $labCandidate.statistics.maximum_delta -ne 1 -or
        $labCandidate.statistics.mean_delta -ne 0 -or
        $labCandidate.correlations[0].method -ne 'cross_correlation' -or
        $labCandidate.correlations[0].coefficient -ne 1 -or
        $labCandidate.correlations[0].lag_ms -ne -1000) {
        throw 'Offline series analysis must preserve raw identity and deterministic statistics, deltas, and lag.'
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
    $nvapiCoolerStatusJson = Get-Content -Raw -LiteralPath $nvapiCoolerStatusFixturePath
    if (-not ($nvapiCoolerStatusJson |
            Test-Json -SchemaFile $nvapiCoolerStatusSchemaPath)) {
        throw 'Synthetic cooler-status report must pass nvapi-cooler-status-v1-observation-v1.schema.json.'
    }
    $nvapiCoolerStatus = $nvapiCoolerStatusJson | ConvertFrom-Json
    if ($nvapiCoolerStatus.call_sites.Count -ne 2 -or
        $nvapiCoolerStatus.samples.Count -ne 2 -or
        @($nvapiCoolerStatus.samples | Where-Object {
                $_.raw_words.Count -ne 426 -or
                $_.raw_entries.Count -ne $_.observed_count -or
                @($_.raw_entries | Where-Object {
                        $_.raw_field_words.Count -ne 4
                    }).Count -ne 0
            }).Count -ne 0) {
        throw 'Synthetic cooler-status report must preserve both sites, all 426 DWORDs, and four uninterpreted fields per observed entry.'
    }
    $truncatedCoolerStatus = $nvapiCoolerStatusJson | ConvertFrom-Json
    $truncatedCoolerStatus.samples[0].raw_words = @(
        $truncatedCoolerStatus.samples[0].raw_words | Select-Object -First 425
    )
    if (($truncatedCoolerStatus | ConvertTo-Json -Depth 10) |
            Test-Json `
                -SchemaFile $nvapiCoolerStatusSchemaPath `
                -ErrorAction SilentlyContinue) {
        throw 'Cooler-status schema must reject a truncated 425-DWORD observation.'
    }
    $wrongSiteCoolerStatus = $nvapiCoolerStatusJson | ConvertFrom-Json
    $wrongSiteCoolerStatus.caller_rvas[1] = '0x0021d825'
    if (($wrongSiteCoolerStatus | ConvertTo-Json -Depth 10) |
            Test-Json `
                -SchemaFile $nvapiCoolerStatusSchemaPath `
                -ErrorAction SilentlyContinue) {
        throw 'Cooler-status schema must reject a call site outside the fixed profile.'
    }
    $nvapiCoolerStatusV2Json = Get-Content `
        -Raw `
        -LiteralPath $nvapiCoolerStatusV2FixturePath
    if (-not ($nvapiCoolerStatusV2Json |
            Test-Json -SchemaFile $nvapiCoolerStatusV2SchemaPath)) {
        throw 'Synthetic cooler-status v2 report must pass nvapi-cooler-status-v1-observation-v2.schema.json.'
    }
    $nvapiCoolerStatusV2 = $nvapiCoolerStatusV2Json | ConvertFrom-Json
    $nvapiCoolerStatusV2Schema = Get-Content `
        -Raw `
        -LiteralPath $nvapiCoolerStatusV2SchemaPath |
        ConvertFrom-Json
    if ($nvapiCoolerStatusV2.call_sites.Count -ne 2 -or
        $nvapiCoolerStatusV2.samples.Count -ne 2 -or
        $nvapiCoolerStatusV2.gpu_profile.gpu_uuid -ne
            'GPU-fca3647e-8390-15a8-f23b-d0f870c9accd' -or
        $nvapiCoolerStatusV2.loaded_nvapi_module.proof_source -ne
            'cdb_modload_target_image' -or
        @($nvapiCoolerStatusV2.samples | Where-Object {
                $_.raw_words.Count -ne 426 -or
                $_.raw_entries.Count -ne $_.observed_count -or
                @($_.raw_entries | Where-Object {
                        $_.raw_field_words.Count -ne 4
                    }).Count -ne 0
            }).Count -ne 0) {
        throw 'Synthetic cooler-status v2 report must preserve the exact profile, loaded-module proof, both sites, 426 DWORDs, and four uninterpreted fields.'
    }
    if ($nvapiCoolerStatusV2Schema.properties.call_count.maximum -ne 1024 -or
        $nvapiCoolerStatusV2Schema.properties.samples.maxItems -ne 1024 -or
        $nvapiCoolerStatusV2Schema.properties.samples.items.properties.sequence.maximum -ne 1024 -or
        $nvapiCoolerStatusV2Schema.'$defs'.call_site_0021d654.properties.call_count.maximum -ne 1024 -or
        $nvapiCoolerStatusV2Schema.'$defs'.call_site_0021d824.properties.call_count.maximum -ne 1024) {
        throw 'Cooler-status v2 schema must cap total, per-site, sequence, and sample counts at 1024.'
    }
    $alternateGpuIndexCoolerStatusV2 = $nvapiCoolerStatusV2Json | ConvertFrom-Json
    $alternateGpuIndexCoolerStatusV2.gpu_profile.gpu_index = 31
    if (-not (($alternateGpuIndexCoolerStatusV2 | ConvertTo-Json -Depth 20) |
            Test-Json -SchemaFile $nvapiCoolerStatusV2SchemaPath)) {
        throw 'Cooler-status v2 schema must bind identity independently of a valid 0-31 GPU index.'
    }
    $invalidGpuIndexCoolerStatusV2 = $nvapiCoolerStatusV2Json | ConvertFrom-Json
    $invalidGpuIndexCoolerStatusV2.gpu_profile.gpu_index = 32
    if (($invalidGpuIndexCoolerStatusV2 | ConvertTo-Json -Depth 20) |
            Test-Json `
                -SchemaFile $nvapiCoolerStatusV2SchemaPath `
                -ErrorAction SilentlyContinue) {
        throw 'Cooler-status v2 schema must reject a GPU index outside 0-31.'
    }
    $oversizedCallCountCoolerStatusV2 = $nvapiCoolerStatusV2Json | ConvertFrom-Json
    $oversizedCallCountCoolerStatusV2.call_count = 1025
    if (($oversizedCallCountCoolerStatusV2 | ConvertTo-Json -Depth 20) |
            Test-Json `
                -SchemaFile $nvapiCoolerStatusV2SchemaPath `
                -ErrorAction SilentlyContinue) {
        throw 'Cooler-status v2 schema must reject more than 1024 calls.'
    }
    $wrongGpuCoolerStatusV2 = $nvapiCoolerStatusV2Json | ConvertFrom-Json
    $wrongGpuCoolerStatusV2.gpu_profile.gpu_uuid =
        'GPU-00000000-0000-0000-0000-000000000000'
    if (($wrongGpuCoolerStatusV2 | ConvertTo-Json -Depth 20) |
            Test-Json `
                -SchemaFile $nvapiCoolerStatusV2SchemaPath `
                -ErrorAction SilentlyContinue) {
        throw 'Cooler-status v2 schema must reject a different GPU UUID.'
    }
    $wrongSubsystemCoolerStatusV2 = $nvapiCoolerStatusV2Json | ConvertFrom-Json
    $wrongSubsystemCoolerStatusV2.gpu_profile.pci_subsystem_device_id = '0x0000'
    if (($wrongSubsystemCoolerStatusV2 | ConvertTo-Json -Depth 20) |
            Test-Json `
                -SchemaFile $nvapiCoolerStatusV2SchemaPath `
                -ErrorAction SilentlyContinue) {
        throw 'Cooler-status v2 schema must reject a different PCI subsystem.'
    }
    $missingIdentityProbeCoolerStatusV2 = $nvapiCoolerStatusV2Json | ConvertFrom-Json
    $missingIdentityProbeCoolerStatusV2.PSObject.Properties.Remove(
        'identity_probe_sha256'
    )
    if (($missingIdentityProbeCoolerStatusV2 | ConvertTo-Json -Depth 20) |
            Test-Json `
                -SchemaFile $nvapiCoolerStatusV2SchemaPath `
                -ErrorAction SilentlyContinue) {
        throw 'Cooler-status v2 schema must require the sealed identity probe.'
    }
    $wrongCandidateHashCoolerStatusV2 = $nvapiCoolerStatusV2Json | ConvertFrom-Json
    $wrongCandidateHashCoolerStatusV2.candidate_inventory_sha256 = '0' * 64
    if (($wrongCandidateHashCoolerStatusV2 | ConvertTo-Json -Depth 20) |
            Test-Json `
                -SchemaFile $nvapiCoolerStatusV2SchemaPath `
                -ErrorAction SilentlyContinue) {
        throw 'Cooler-status v2 schema must reject an unpinned candidate inventory.'
    }
    $wrongPriorHashCoolerStatusV2 = $nvapiCoolerStatusV2Json | ConvertFrom-Json
    $wrongPriorHashCoolerStatusV2.prior_observation_sha256 = '0' * 64
    if (($wrongPriorHashCoolerStatusV2 | ConvertTo-Json -Depth 20) |
            Test-Json `
                -SchemaFile $nvapiCoolerStatusV2SchemaPath `
                -ErrorAction SilentlyContinue) {
        throw 'Cooler-status v2 schema must reject an unpinned prior observation.'
    }
    $missingLoadedModuleCoolerStatusV2 = $nvapiCoolerStatusV2Json | ConvertFrom-Json
    $missingLoadedModuleCoolerStatusV2.PSObject.Properties.Remove(
        'loaded_nvapi_module'
    )
    if (($missingLoadedModuleCoolerStatusV2 | ConvertTo-Json -Depth 20) |
            Test-Json `
                -SchemaFile $nvapiCoolerStatusV2SchemaPath `
                -ErrorAction SilentlyContinue) {
        throw 'Cooler-status v2 schema must require proof of the loaded NVAPI module.'
    }
    $wrongLoadedHashCoolerStatusV2 = $nvapiCoolerStatusV2Json | ConvertFrom-Json
    $wrongLoadedHashCoolerStatusV2.loaded_nvapi_module.file_sha256 = '0' * 64
    if (($wrongLoadedHashCoolerStatusV2 | ConvertTo-Json -Depth 20) |
            Test-Json `
                -SchemaFile $nvapiCoolerStatusV2SchemaPath `
                -ErrorAction SilentlyContinue) {
        throw 'Cooler-status v2 schema must reject a different loaded NVAPI module.'
    }
    $wrongModuleProofCoolerStatusV2 = $nvapiCoolerStatusV2Json | ConvertFrom-Json
    $wrongModuleProofCoolerStatusV2.loaded_nvapi_module.proof_source = 'driver_store_scan'
    if (($wrongModuleProofCoolerStatusV2 | ConvertTo-Json -Depth 20) |
            Test-Json `
                -SchemaFile $nvapiCoolerStatusV2SchemaPath `
                -ErrorAction SilentlyContinue) {
        throw 'Cooler-status v2 schema must reject proof not obtained from target-image ModLoad.'
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
    $nvapiThermChannelV2Json = Get-Content `
        -Raw `
        -LiteralPath $nvapiThermChannelV2FixturePath
    if (-not ($nvapiThermChannelV2Json |
            Test-Json -SchemaFile $nvapiThermChannelV2SchemaPath)) {
        throw 'Synthetic thermal-channel v2 observation must pass its fail-closed schema.'
    }
    $nvapiThermCorrelationV2Json = (& $labExecutable `
            correlate-nvapi-therm-channel-v2 `
            --observation $nvapiThermChannelV2FixturePath `
            --gpuz-log $gpuzThermReferenceV2FixturePath | Out-String)
    if ($LASTEXITCODE -ne 0 -or
        -not ($nvapiThermCorrelationV2Json |
            Test-Json -SchemaFile $nvapiThermCorrelationV2SchemaPath)) {
        throw 'Thermal-channel v2 correlation output must pass its hardened v2 schema.'
    }
    $nvapiThermCorrelationV2 = $nvapiThermCorrelationV2Json | ConvertFrom-Json
    if ($nvapiThermCorrelationV2.mapping_status -ne 'matched_external_reference' -or
        $nvapiThermCorrelationV2.selection.selected_session_index -ne 2 -or
        $nvapiThermCorrelationV2.selection.eligible_session_count -ne 1 -or
        $nvapiThermCorrelationV2.selection.ignored_session_indices_without_exact_channels[0] -ne 0 -or
        $nvapiThermCorrelationV2.selection.rejected_session_indices_with_invalid_exact_channel_data[0] -ne 1 -or
        $nvapiThermCorrelationV2.direct_comparison.mappings[0].reference_channel -ne
            'GPU Temperature' -or
        $nvapiThermCorrelationV2.direct_comparison.mappings[1].reference_channel -ne
            'Hot Spot') {
        throw 'Thermal-channel v2 must isolate appended layouts and preserve direct mapping evidence.'
    }
    $wrongThermalV2Uuid = $nvapiThermChannelV2Json | ConvertFrom-Json
    $wrongThermalV2Uuid.profile.gpu.uuid =
        'GPU-00000000-0000-0000-0000-000000000000'
    if (($wrongThermalV2Uuid | ConvertTo-Json -Depth 20) |
            Test-Json `
                -SchemaFile $nvapiThermChannelV2SchemaPath `
                -ErrorAction SilentlyContinue) {
        throw 'Thermal-channel v2 schema must reject a different physical GPU UUID.'
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
    $nvapiVoltageObservationV2Json = Get-Content `
        -Raw `
        -LiteralPath $nvapiVoltageObservationV2FixturePath
    if (-not ($nvapiVoltageObservationV2Json |
            Test-Json -SchemaFile $nvapiVoltageObservationV2SchemaPath)) {
        throw 'Synthetic voltage-status v2 observation must pass its fail-closed schema.'
    }
    $oversizedGpuzVoltageObservation = $nvapiVoltageObservationV2Json | ConvertFrom-Json
    $oversizedGpuzVoltageObservation.references.gpuz.size_bytes_before = 16777214
    $oversizedGpuzVoltageObservation.references.gpuz.size_bytes_midpoint = 16777215
    $oversizedGpuzVoltageObservation.references.gpuz.size_bytes_after = 16777217
    if (($oversizedGpuzVoltageObservation | ConvertTo-Json -Depth 20) |
            Test-Json `
                -SchemaFile $nvapiVoltageObservationV2SchemaPath `
                -ErrorAction SilentlyContinue) {
        throw 'Voltage-status v2 schema must reject a GPU-Z prefix above 16 MiB.'
    }
    $largeHwinfoVoltageObservation = $nvapiVoltageObservationV2Json | ConvertFrom-Json
    $largeHwinfoVoltageObservation.references.hwinfo.size_bytes_before = 16777217
    $largeHwinfoVoltageObservation.references.hwinfo.size_bytes_midpoint = 16777218
    $largeHwinfoVoltageObservation.references.hwinfo.size_bytes_after = 16777219
    if (-not (($largeHwinfoVoltageObservation | ConvertTo-Json -Depth 20) |
            Test-Json -SchemaFile $nvapiVoltageObservationV2SchemaPath)) {
        throw 'Voltage-status v2 schema must preserve the separate 64 MiB HWiNFO bound.'
    }
    $oversizedHwinfoVoltageObservation = $nvapiVoltageObservationV2Json | ConvertFrom-Json
    $oversizedHwinfoVoltageObservation.references.hwinfo.size_bytes_before = 67108862
    $oversizedHwinfoVoltageObservation.references.hwinfo.size_bytes_midpoint = 67108863
    $oversizedHwinfoVoltageObservation.references.hwinfo.size_bytes_after = 67108865
    if (($oversizedHwinfoVoltageObservation | ConvertTo-Json -Depth 20) |
            Test-Json `
                -SchemaFile $nvapiVoltageObservationV2SchemaPath `
                -ErrorAction SilentlyContinue) {
        throw 'Voltage-status v2 schema must reject an HWiNFO prefix above 64 MiB.'
    }
    $nvapiVoltageCorrelationV2Json = (& $labExecutable `
            correlate-nvapi-voltage-status-v2 `
            --observation $nvapiVoltageObservationV2FixturePath `
            --gpuz-log $gpuzVoltageReferenceFixturePath `
            --hwinfo-log $hwinfoVoltageReferenceFixturePath | Out-String)
    if ($LASTEXITCODE -ne 0 -or
        -not ($nvapiVoltageCorrelationV2Json |
            Test-Json -SchemaFile $nvapiVoltageCorrelationV2SchemaPath)) {
        throw 'Voltage-status v2 correlation output must pass its v2 schema.'
    }
    $nvapiVoltageCorrelationV2 = $nvapiVoltageCorrelationV2Json | ConvertFrom-Json
    if ($nvapiVoltageCorrelationV2.mapping_status -ne 'matched_external_reference' -or
        $nvapiVoltageCorrelationV2.profile_name -ne
            'gpuz-2.70.0-nvapi-610.88-voltage-status-v1' -or
        $nvapiVoltageCorrelationV2.mapping.word_index -ne 10 -or
        $nvapiVoltageCorrelationV2.mapping.distinct_raw_value_count -ne 3 -or
        $nvapiVoltageCorrelationV2.gpuz_reference.source -ne 'GPU-Z' -or
        $nvapiVoltageCorrelationV2.hwinfo_reference.source -ne 'HWiNFO' -or
        $nvapiVoltageCorrelationV2.hwinfo_reference.maximum_alignment_delta_ms -ne 0) {
        throw 'Voltage-status v2 must retain its exact profile and explicit GPU-Z plus HWiNFO references.'
    }
    $gpuzOnlyVoltageObservation = $nvapiVoltageObservationV2Json | ConvertFrom-Json
    $gpuzOnlyVoltageObservation.references.hwinfo = $null
    if (-not (($gpuzOnlyVoltageObservation | ConvertTo-Json -Depth 10) |
            Test-Json -SchemaFile $nvapiVoltageObservationV2SchemaPath)) {
        throw 'Voltage-status v2 schema must accept an explicit null HWiNFO reference.'
    }
    $staleHwinfoVoltageObservation = $nvapiVoltageObservationV2Json | ConvertFrom-Json
    $staleHwinfoVoltageObservation.references.hwinfo.grew_during_capture = $false
    if (($staleHwinfoVoltageObservation | ConvertTo-Json -Depth 10) |
            Test-Json `
                -SchemaFile $nvapiVoltageObservationV2SchemaPath `
                -ErrorAction SilentlyContinue) {
        throw 'Voltage-status v2 schema must reject HWiNFO without recorded growth.'
    }
    $wrongDriverVoltageObservation = $nvapiVoltageObservationV2Json | ConvertFrom-Json
    $wrongDriverVoltageObservation.profile.gpu.driver_version = '611.00'
    if (($wrongDriverVoltageObservation | ConvertTo-Json -Depth 10) |
            Test-Json `
                -SchemaFile $nvapiVoltageObservationV2SchemaPath `
                -ErrorAction SilentlyContinue) {
        throw 'Voltage-status v2 schema must reject a driver outside the fixed profile.'
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
    Write-Host 'C/C++: build with warnings as errors and 14 CTest tests, including private profiles, VBIOS, and RM thermal protocol.'
    Write-Host 'C#: build, sampler, alert, SQLite storage, local service, laboratory tests, GPU-Z import/correlation, anchored experiment analysis, NVAPI thermal mapping, Windows handle identity, markers, and formatting.'
    Write-Host 'Schemas: stable telemetry plus v0.8 artifact, experiment, numeric-series, marker, analysis, VBIOS, GPU-Z, NVAPI, bounded IOCTL, and Windows handle contracts.'
    Write-Host "Version parity: C/C++ and C# $nativeVersion."
}
finally {
    if ($null -ne $labCiRoot -and $null -ne $labCiPackage) {
        Remove-LabCiPackageSafely -Root $labCiRoot -Package $labCiPackage
    }
    Pop-Location
}
