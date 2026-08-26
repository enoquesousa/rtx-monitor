[CmdletBinding()]
param(
    [ValidateRange(10, 60)]
    [int]$DurationSeconds = 15,

    [string]$GpuzPath = 'C:\Program Files (x86)\GPU-Z\GPU-Z.exe',

    [string]$WinDbgPath = 'C:\Users\sousa\AppData\Local\Microsoft\WindowsApps\WinDbgX.exe',

    [string]$OutputDirectory,

    [switch]$InteractiveTarget,

    [string]$ExpectedGpuzDriverSha256 =
        '999cf056a298cfce5f5a61d44c218ffafccd36ecff53e433768512073e6bf005'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This debugger capture must run from an elevated PowerShell session.'
    }
}

function Stop-ExactGpuZDriverIfLeftBehind {
    $serviceName = 'GPU-Z-v8'
    $service = Get-CimInstance `
        -ClassName Win32_SystemDriver `
        -Filter "Name='$serviceName'" `
        -ErrorAction SilentlyContinue
    if ($null -ne $service) {
        & sc.exe stop $serviceName | Out-Null
        if ($LASTEXITCODE -notin @(0, 1062)) {
            throw "Stopping the leftover '$serviceName' service failed with code $LASTEXITCODE."
        }

        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            $service = Get-CimInstance `
                -ClassName Win32_SystemDriver `
                -Filter "Name='$serviceName'" `
                -ErrorAction SilentlyContinue
            if ($null -eq $service -or $service.State -eq 'Stopped') {
                break
            }

            Start-Sleep -Milliseconds 250
        }

        if ($null -ne $service -and $service.State -ne 'Stopped') {
            throw "The leftover '$serviceName' service did not stop."
        }

        & sc.exe delete $serviceName | Out-Null
        if ($LASTEXITCODE -notin @(0, 1060)) {
            throw "Deleting the leftover '$serviceName' service failed with code $LASTEXITCODE."
        }
    }

    $temporaryDriver = Join-Path ([IO.Path]::GetTempPath()) 'GPU-Z-v8.sys'
    if (Test-Path -LiteralPath $temporaryDriver -PathType Leaf) {
        $signature = Get-AuthenticodeSignature -LiteralPath $temporaryDriver
        $hash = (Get-FileHash -LiteralPath $temporaryDriver -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
            $null -eq $signature.SignerCertificate -or
            $signature.SignerCertificate.Subject -notlike '*TechPowerUp LLC*' -or
            $hash -ne $ExpectedGpuzDriverSha256) {
            throw 'The leftover GPU-Z-v8.sys does not match the trusted captured driver; it was not deleted.'
        }

        Remove-Item -LiteralPath $temporaryDriver -Force
    }
}

if (-not $IsWindows) {
    throw 'GPU-Z NVAPI capture is supported only on Windows.'
}

Assert-Administrator
$projectRoot = Split-Path -Parent $PSScriptRoot
$GpuzPath = [IO.Path]::GetFullPath($GpuzPath)
$WinDbgPath = [IO.Path]::GetFullPath($WinDbgPath)
if (-not (Test-Path -LiteralPath $GpuzPath -PathType Leaf)) {
    throw "GPU-Z was not found at '$GpuzPath'."
}

if (-not (Test-Path -LiteralPath $WinDbgPath -PathType Leaf)) {
    throw "WinDbg was not found at '$WinDbgPath'."
}

$gpuzSignature = Get-AuthenticodeSignature -LiteralPath $GpuzPath
if ($gpuzSignature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
    $null -eq $gpuzSignature.SignerCertificate -or
    $gpuzSignature.SignerCertificate.Subject -notlike '*TechPowerUp LLC*') {
    throw 'GPU-Z signature validation failed.'
}

$existingTargets = @(
    Get-Process `
        -Name 'GPU-Z', 'GPUQuery_External', 'TPU_Query_External_x86', 'WinDbgX' `
        -ErrorAction SilentlyContinue
)
if ($existingTargets.Count -ne 0) {
    throw 'Close GPU-Z, its query helper, and WinDbg before starting a bounded capture.'
}

$preexistingDriverService = Get-CimInstance `
    -ClassName Win32_SystemDriver `
    -Filter "Name='GPU-Z-v8'" `
    -ErrorAction SilentlyContinue
$preexistingDriverFile = Join-Path ([IO.Path]::GetTempPath()) 'GPU-Z-v8.sys'
if ($null -ne $preexistingDriverService -or
    (Test-Path -LiteralPath $preexistingDriverFile)) {
    throw 'A preexisting GPU-Z-v8 service or temporary driver must be reviewed and removed before capture.'
}

$evidenceRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'evidence'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $captureName = 'gpuz-nvapi-{0}' -f (Get-Date -Format 'yyyyMMdd-HHmmss')
    $OutputDirectory = Join-Path $evidenceRoot $captureName
}

$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$evidencePrefix = $evidenceRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) +
    [IO.Path]::DirectorySeparatorChar
if (-not $OutputDirectory.StartsWith($evidencePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must be a child of '$evidenceRoot'."
}

if (Test-Path -LiteralPath $OutputDirectory) {
    throw "OutputDirectory already exists: '$OutputDirectory'."
}

$null = New-Item -ItemType Directory -Path $OutputDirectory
$commandFile = Join-Path $OutputDirectory 'windbg-commands.txt'
$debugLog = Join-Path $OutputDirectory 'windbg-nvapi.log'
$reportPath = Join-Path $OutputDirectory 'nvapi-query-report.json'
$resolutionReportPath = Join-Path $OutputDirectory 'nvapi-resolution-report.json'
$callReportPath = Join-Path $OutputDirectory 'nvapi-call-report.json'
$callHandlerCommand = '.echo RTXMON_NVAPI_CALL; r eip; gc'
$commandText = @"
.logopen $debugLog
bu nvapi!nvapi_QueryInterface "r @`$t0=poi(@esp+4); ~#bp /1 poi(@esp) \".printf \\\"RTXMON_NVAPI_RESOLVE pid=%x id=0x%08x result=0x%08x\\\\n\\\", @`$tpid, @`$t0, @eax; .if (@eax != 0) { bp @eax \\\"$callHandlerCommand\\\" }; gc\"; ~#gc"
g
"@
$commandText | Set-Content -LiteralPath $commandFile -Encoding ascii

$debuggerArguments = '-Q -o -c "$$><{0}" "{1}"' -f $commandFile, $GpuzPath
$debugger = $null
$debuggerWindowStyle = if ($InteractiveTarget) { 'Normal' } else { 'Hidden' }
$startedTargetIds = @()
$forcedTargetIds = @()
$captureStart = Get-Date
try {
    $debugger = Start-Process `
        -FilePath $WinDbgPath `
        -ArgumentList $debuggerArguments `
        -WindowStyle $debuggerWindowStyle `
        -PassThru

    $deadline = (Get-Date).AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 250
        $targets = @(
            Get-Process `
                -Name 'GPU-Z', 'GPUQuery_External', 'TPU_Query_External_x86' `
                -ErrorAction SilentlyContinue |
                Where-Object { $_.StartTime -ge $captureStart.AddSeconds(-1) }
        )
    } while ($targets.Count -eq 0 -and (Get-Date) -lt $deadline)

    if ($targets.Count -eq 0) {
        throw 'WinDbg did not start GPU-Z within ten seconds.'
    }

    Start-Sleep -Seconds $DurationSeconds
    $targets = @(
        Get-Process `
            -Name 'GPU-Z', 'GPUQuery_External', 'TPU_Query_External_x86' `
            -ErrorAction SilentlyContinue |
            Where-Object { $_.StartTime -ge $captureStart.AddSeconds(-1) }
    )
    $startedTargetIds = @($targets.Id)
    foreach ($target in $targets) {
        $null = $target.CloseMainWindow()
    }

    $closeDeadline = (Get-Date).AddSeconds(8)
    do {
        Start-Sleep -Milliseconds 250
        $remainingTargets = @(
            Get-Process -Id $startedTargetIds -ErrorAction SilentlyContinue
        )
    } while ($remainingTargets.Count -ne 0 -and (Get-Date) -lt $closeDeadline)

    foreach ($target in $remainingTargets) {
        $forcedTargetIds += $target.Id
        Stop-Process -Id $target.Id -Force
    }
}
finally {
    if ($null -ne $debugger -and -not $debugger.HasExited) {
        $null = $debugger.CloseMainWindow()
        if (-not $debugger.WaitForExit(5000)) {
            Stop-Process -Id $debugger.Id -Force -ErrorAction SilentlyContinue
        }
    }

    Stop-ExactGpuZDriverIfLeftBehind
}

if (-not (Test-Path -LiteralPath $debugLog -PathType Leaf)) {
    throw "WinDbg did not create '$debugLog'."
}

$debuggerCommandFailure = Select-String `
    -LiteralPath $debugLog `
    -Pattern 'Command file execution failed|Syntax error|Some commands were skipped' `
    -CaseSensitive:$false |
    Select-Object -First 1
if ($null -ne $debuggerCommandFailure) {
    throw "WinDbg rejected a capture command: $($debuggerCommandFailure.Line.Trim())"
}

$resolutions = @(
    Select-String `
        -LiteralPath $debugLog `
        -Pattern 'RTXMON_NVAPI_RESOLVE pid=([0-9a-fA-F]+) id=0x([0-9a-fA-F]{8}) result=0x([0-9a-fA-F]{8})' |
        ForEach-Object {
            [pscustomobject]@{
                process_id = [Convert]::ToInt32($_.Matches[0].Groups[1].Value, 16)
                interface_id = '0x' + $_.Matches[0].Groups[2].Value.ToLowerInvariant()
                result_address = [Convert]::ToUInt64($_.Matches[0].Groups[3].Value, 16)
            }
        }
)
if ($resolutions.Count -eq 0) {
    throw 'WinDbg did not record any completed nvapi_QueryInterface calls.'
}

$interfaceCounts = @(
    $resolutions |
        Group-Object -Property interface_id |
        Sort-Object -Property Name |
        ForEach-Object {
            [pscustomobject]@{
                interface_id = $_.Name
                call_count = $_.Count
            }
        }
)

$moduleRanges = @(
    Select-String `
        -LiteralPath $debugLog `
        -Pattern '^ModLoad:\s+([0-9a-fA-F]{8})\s+([0-9a-fA-F]{8})\s+(.+?)\s*$' |
        ForEach-Object {
            [pscustomobject]@{
                base_address = [Convert]::ToUInt64($_.Matches[0].Groups[1].Value, 16)
                limit_address = [Convert]::ToUInt64($_.Matches[0].Groups[2].Value, 16)
                path = $_.Matches[0].Groups[3].Value.Trim()
            }
        } |
        Group-Object -Property base_address, limit_address, path |
        ForEach-Object { $_.Group[0] }
)

$resolvedModulePaths = @(
    $resolutions |
        Where-Object { $_.result_address -ne 0 } |
        ForEach-Object {
            $address = $_.result_address
            $moduleRanges |
                Where-Object {
                    $address -ge $_.base_address -and
                    $address -lt $_.limit_address
                } |
                Select-Object -ExpandProperty path -First 1
        } |
        Sort-Object -Unique
)
$moduleRecords = @(
    $moduleRanges |
        Where-Object { $resolvedModulePaths -contains $_.path } |
        Where-Object { Test-Path -LiteralPath $_.path -PathType Leaf } |
        Group-Object -Property path |
        ForEach-Object {
            $module = $_.Group[0]
            $path = [IO.Path]::GetFullPath($module.path)
            $signature = Get-AuthenticodeSignature -LiteralPath $path
            if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid) {
                throw "A module containing an NVAPI result has an invalid signature: '$path'."
            }

            [pscustomobject]@{
                module_name = [IO.Path]::GetFileName($path)
                path = $path
                size_bytes = (Get-Item -LiteralPath $path).Length
                image_size = $module.limit_address - $module.base_address
                sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        } |
        Sort-Object -Property module_name, path
)

$resolutionInterfaces = @(
    $resolutions |
        Group-Object -Property interface_id |
        Sort-Object -Property Name |
        ForEach-Object {
            $interfaceResolutions = @($_.Group)
            $resolved = @($interfaceResolutions | Where-Object { $_.result_address -ne 0 })
            $targets = @(
                $resolved |
                    ForEach-Object {
                        $address = $_.result_address
                        $moduleRange = $moduleRanges |
                            Where-Object {
                                $address -ge $_.base_address -and
                                $address -lt $_.limit_address
                            } |
                            Select-Object -First 1
                        $module = if ($null -ne $moduleRange) {
                            $moduleRecords |
                                Where-Object { $_.path -eq $moduleRange.path } |
                                Select-Object -First 1
                        }
                        else {
                            $null
                        }
                        if ($null -eq $module -or $null -eq $moduleRange) {
                            [pscustomobject]@{
                                module_name = $null
                                module_sha256 = $null
                                rva = $null
                            }
                        }
                        else {
                            [pscustomobject]@{
                                module_name = $module.module_name
                                module_sha256 = $module.sha256
                                rva = '0x{0:x8}' -f ($address - $moduleRange.base_address)
                            }
                        }
                    } |
                    Group-Object -Property module_name, module_sha256, rva |
                    ForEach-Object {
                        [pscustomobject]@{
                            module_name = $_.Group[0].module_name
                            module_sha256 = $_.Group[0].module_sha256
                            rva = $_.Group[0].rva
                            observation_count = $_.Count
                        }
                    }
            )
            [pscustomobject]@{
                interface_id = $_.Name
                call_count = $interfaceResolutions.Count
                resolved_count = $resolved.Count
                null_result_count = $interfaceResolutions.Count - $resolved.Count
                targets = $targets
            }
        }
)

$usedModuleHashes = @(
    $resolutionInterfaces |
        ForEach-Object { $_.targets } |
        Where-Object { $null -ne $_.module_sha256 } |
        Select-Object -ExpandProperty module_sha256 -Unique
)
$publishedModules = @(
    $moduleRecords |
        Where-Object {
            $usedModuleHashes -contains $_.sha256
        } |
        ForEach-Object {
            [pscustomobject]@{
                module_name = $_.module_name
                original_file_name = [IO.Path]::GetFileName($_.path)
                size_bytes = $_.size_bytes
                image_size = $_.image_size
                sha256 = $_.sha256
            }
        }
)

$capturedUtc = [DateTimeOffset]::UtcNow.ToString('O')
$gpuzSha256 = (Get-FileHash -LiteralPath $GpuzPath -Algorithm SHA256).Hash.ToLowerInvariant()
$report = [ordered]@{
    schema_version = 1
    source_kind = 'nvapi_query_interface_observation'
    captured_utc = $capturedUtc
    duration_seconds = $DurationSeconds
    gpuz_sha256 = $gpuzSha256
    forced_target_process_ids = $forcedTargetIds
    observation_count = $resolutions.Count
    unique_interface_count = $interfaceCounts.Count
    interfaces = $interfaceCounts
    warning = 'An observed interface ID identifies an NVAPI query only; it does not identify a sensor or prove that the returned function produced hotspot telemetry.'
}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8NoBOM

$resolutionReport = [ordered]@{
    schema_version = 1
    source_kind = 'nvapi_query_interface_resolution_observation'
    captured_utc = $capturedUtc
    duration_seconds = $DurationSeconds
    gpuz_sha256 = $gpuzSha256
    observation_count = $resolutions.Count
    unique_interface_count = $resolutionInterfaces.Count
    resolved_observation_count = @($resolutions | Where-Object { $_.result_address -ne 0 }).Count
    null_result_count = @($resolutions | Where-Object { $_.result_address -eq 0 }).Count
    modules = $publishedModules
    interfaces = $resolutionInterfaces
    warning = 'A resolved pointer proves only that nvapi_QueryInterface returned an address. Module identity and RVA do not reveal the function ABI, sensor meaning, unit, or returned value.'
}
$resolutionReport |
    ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath $resolutionReportPath -Encoding utf8NoBOM

$debugText = Get-Content -LiteralPath $debugLog -Raw
$callAddresses = @(
    [regex]::Matches(
        $debugText,
        'RTXMON_NVAPI_CALL\r?\n(?:[^\r\n]*\r?\n){0,2}?eip=([0-9a-fA-F]{8})',
        [Text.RegularExpressions.RegexOptions]::IgnoreCase
    ) |
        ForEach-Object {
            [Convert]::ToUInt64($_.Groups[1].Value, 16)
        }
)
$mappedCalls = @(
    foreach ($address in $callAddresses) {
        $moduleRange = $moduleRanges |
            Where-Object {
                $address -ge $_.base_address -and $address -lt $_.limit_address
            } |
            Select-Object -First 1
        if ($null -eq $moduleRange) {
            continue
        }

        $module = $moduleRecords |
            Where-Object { $_.path -eq $moduleRange.path } |
            Select-Object -First 1
        if ($null -ne $module) {
            [pscustomobject]@{
                module_name = $module.module_name
                module_sha256 = $module.sha256
                rva = '0x{0:x8}' -f ($address - $moduleRange.base_address)
            }
        }
    }
)
$callTargets = @(
    $resolutionInterfaces |
        ForEach-Object {
            $interfaceId = $_.interface_id
            foreach ($target in $_.targets) {
                if ($null -ne $target.module_name -and $null -ne $target.rva) {
                    [pscustomobject]@{
                        interface_id = $interfaceId
                        module_name = $target.module_name
                        module_sha256 = $target.module_sha256
                        rva = $target.rva
                    }
                }
            }
        } |
        Group-Object -Property module_name, module_sha256, rva |
        Sort-Object -Property Name |
        ForEach-Object {
            $target = $_.Group[0]
            $targetCallCount = @(
                $mappedCalls |
                    Where-Object {
                        $_.module_name -eq $target.module_name -and
                        $_.module_sha256 -eq $target.module_sha256 -and
                        $_.rva -eq $target.rva
                    }
            ).Count
            [pscustomobject]@{
                module_name = $target.module_name
                module_sha256 = $target.module_sha256
                rva = $target.rva
                interface_ids = @($_.Group.interface_id | Sort-Object -Unique)
                call_count = $targetCallCount
            }
        }
)
$callReport = [ordered]@{
    schema_version = 1
    source_kind = 'nvapi_function_call_observation'
    captured_utc = $capturedUtc
    duration_seconds = $DurationSeconds
    gpuz_sha256 = $gpuzSha256
    resolution_report_sha256 = (Get-FileHash `
        -LiteralPath $resolutionReportPath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    target_count = $callTargets.Count
    observed_target_count = @($callTargets | Where-Object { $_.call_count -gt 0 }).Count
    call_count = $mappedCalls.Count
    targets = $callTargets
    warning = 'A breakpoint hit proves that GPU-Z executed a resolved function entry. It does not reveal the ABI, arguments, returned fields, units, or physical sensor identity.'
}
$callReport |
    ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath $callReportPath -Encoding utf8NoBOM

$schemaRoot = Join-Path $projectRoot 'docs\schema'
$reportSchemas = @(
    [pscustomobject]@{
        report = $reportPath
        schema = Join-Path $schemaRoot 'nvapi-query-observation-v1.schema.json'
    },
    [pscustomobject]@{
        report = $resolutionReportPath
        schema = Join-Path $schemaRoot 'nvapi-interface-resolution-v1.schema.json'
    },
    [pscustomobject]@{
        report = $callReportPath
        schema = Join-Path $schemaRoot 'nvapi-call-observation-v1.schema.json'
    }
)
foreach ($validation in $reportSchemas) {
    $json = Get-Content -LiteralPath $validation.report -Raw
    if (-not ($json | Test-Json -SchemaFile $validation.schema)) {
        throw "Generated report '$($validation.report)' does not satisfy '$($validation.schema)'."
    }
}

$report | ConvertTo-Json -Depth 8
