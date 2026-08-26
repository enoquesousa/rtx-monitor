[CmdletBinding()]
param(
    [ValidateRange(10, 60)]
    [int]$DurationSeconds = 15,

    [int]$GpuzProcessId,

    [Parameter(Mandatory)]
    [string]$CandidateInventoryPath,

    [ValidateSet('All', 'ObservedUnidentified')]
    [string]$TargetScope = 'All',

    [string]$PriorObservationPath,

    [switch]$CaptureInputWords,

    [string]$CdbPath,

    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$failurePath = $null

trap {
    $failureText = $_ | Out-String
    if (-not [string]::IsNullOrWhiteSpace($failurePath)) {
        $failureText | Set-Content -LiteralPath $failurePath -Encoding utf8NoBOM
    }

    [Console]::Error.WriteLine($failureText.TrimEnd())
    exit 1
}

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This attached debugger capture requires an elevated PowerShell session.'
    }
}

function Get-PeMachine {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $stream = [IO.File]::Open(
        $Path,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
    try {
        if ($stream.Length -lt 64) {
            throw "The PE image is truncated: '$Path'."
        }

        $reader = [IO.BinaryReader]::new($stream)
        try {
            $stream.Position = 0x3c
            $peOffset = $reader.ReadUInt32()
            if ($peOffset -gt $stream.Length - 6) {
                throw "The PE header offset is invalid: '$Path'."
            }

            $stream.Position = $peOffset
            if ($reader.ReadUInt32() -ne 0x00004550) {
                throw "The PE signature is invalid: '$Path'."
            }

            return $reader.ReadUInt16()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Resolve-SignedModuleByHash {
    param(
        [Parameter(Mandatory)]
        [string]$ModuleName,

        [Parameter(Mandatory)]
        [string]$ExpectedSha256
    )

    $windowsRoot = [Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)
    $candidates = if ($ModuleName -eq 'nvapi.dll') {
        @(Join-Path $windowsRoot 'SysWOW64\nvapi.dll')
    }
    elseif ($ModuleName -eq 'nvapi_impl.dll') {
        @(Get-ChildItem `
            -LiteralPath (Join-Path $windowsRoot 'System32\DriverStore\FileRepository') `
            -Filter 'nvapi_impl.dll' `
            -File `
            -Recurse `
            -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty FullName)
    }
    else {
        throw "Unsupported candidate module '$ModuleName'."
    }

    $matches = @(
        $candidates |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
            ForEach-Object {
                $path = [IO.Path]::GetFullPath($_)
                $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($hash -eq $ExpectedSha256) {
                    $signature = Get-AuthenticodeSignature -LiteralPath $path
                    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid) {
                        throw "NVAPI module signature validation failed: '$path'."
                    }

                    $path
                }
            } |
            Sort-Object -Unique
    )
    if ($matches.Count -eq 0) {
        throw "No signed '$ModuleName' matches SHA-256 $ExpectedSha256."
    }

    return $matches[0]
}

if (-not $IsWindows) {
    throw 'Attached NVAPI candidate tracing is supported only on Windows.'
}

Assert-Administrator
$projectRoot = Split-Path -Parent $PSScriptRoot
$CandidateInventoryPath = [IO.Path]::GetFullPath($CandidateInventoryPath)
if (-not (Test-Path -LiteralPath $CandidateInventoryPath -PathType Leaf)) {
    throw "Candidate inventory was not found: '$CandidateInventoryPath'."
}

$inventoryItem = Get-Item -LiteralPath $CandidateInventoryPath
if (($inventoryItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
    $inventoryItem.Length -gt 16MB) {
    throw 'Candidate inventory must be a regular local file no larger than 16 MiB.'
}

$candidateSchemaPath = Join-Path `
    $projectRoot `
    'docs\schema\nvapi-candidate-inventory-v1.schema.json'
$candidateInventoryJson = Get-Content -LiteralPath $CandidateInventoryPath -Raw
if (-not ($candidateInventoryJson | Test-Json -SchemaFile $candidateSchemaPath)) {
    throw 'Candidate inventory does not satisfy nvapi-candidate-inventory-v1.'
}

$candidateInventory = $candidateInventoryJson | ConvertFrom-Json
if ($candidateInventory.candidate_count -ne $candidateInventory.candidates.Count) {
    throw 'Candidate inventory count is inconsistent with its candidate array.'
}
$candidateInventorySha256 = (Get-FileHash `
    -LiteralPath $CandidateInventoryPath `
    -Algorithm SHA256).Hash.ToLowerInvariant()

if ([string]::IsNullOrWhiteSpace($CdbPath)) {
    $winDbgPackage = Get-AppxPackage -Name 'Microsoft.WinDbg' |
        Sort-Object -Property Version -Descending |
        Select-Object -First 1
    if ($null -eq $winDbgPackage) {
        throw 'The signed Microsoft WinDbg package is required.'
    }

    $CdbPath = Join-Path $winDbgPackage.InstallLocation 'x86\cdb.exe'
}

$CdbPath = [IO.Path]::GetFullPath($CdbPath)
if (-not (Test-Path -LiteralPath $CdbPath -PathType Leaf)) {
    throw "The x86 CDB executable was not found at '$CdbPath'."
}

$cdbSignature = Get-AuthenticodeSignature -LiteralPath $CdbPath
if ($cdbSignature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
    $null -eq $cdbSignature.SignerCertificate -or
    $cdbSignature.SignerCertificate.Subject -notlike '*Microsoft Corporation*') {
    throw 'Microsoft CDB signature validation failed.'
}

if (@(Get-Process -Name 'WinDbgX', 'cdb' -ErrorAction SilentlyContinue).Count -ne 0) {
    throw 'Close every existing WinDbg or CDB session before attaching to GPU-Z.'
}

$gpuProcesses = @(
    if ($GpuzProcessId -gt 0) {
        Get-Process -Id $GpuzProcessId -ErrorAction Stop
    }
    else {
        Get-Process -Name 'GPU-Z' -ErrorAction SilentlyContinue
    }
)
if ($gpuProcesses.Count -ne 1 -or $gpuProcesses[0].ProcessName -ne 'GPU-Z') {
    throw 'Exactly one GPU-Z target process is required.'
}

$gpuProcess = $gpuProcesses[0]
$gpuzPath = $gpuProcess.Path
if ([string]::IsNullOrWhiteSpace($gpuzPath)) {
    throw 'The elevated capture could not resolve the GPU-Z executable path.'
}

$gpuzSignature = Get-AuthenticodeSignature -LiteralPath $gpuzPath
if ($gpuzSignature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
    $null -eq $gpuzSignature.SignerCertificate -or
    $gpuzSignature.SignerCertificate.Subject -notlike '*TechPowerUp LLC*') {
    throw 'GPU-Z signature validation failed.'
}

if ((Get-PeMachine -Path $gpuzPath) -ne 0x014c) {
    throw 'This collector requires the 32-bit GPU-Z process used by the x86 CDB.'
}

$gpuzSha256 = (Get-FileHash -LiteralPath $gpuzPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($gpuzSha256 -ne $candidateInventory.gpuz_sha256) {
    throw 'GPU-Z does not match the executable anchored by the candidate inventory.'
}

$candidateRecords = @(
    foreach ($candidate in $candidateInventory.candidates) {
        [pscustomobject]@{
            target_key = '{0}|{1}|{2}' -f `
                $candidate.module_name,
                $candidate.module_sha256,
                $candidate.rva
            candidate = $candidate
        }
    }
)
$targetDefinitions = @(
    $candidateRecords |
        Group-Object -Property target_key |
        ForEach-Object {
            $group = @($_.Group.candidate)
            [pscustomobject]@{
                target_key = $_.Name
                trace_key = '{0}|{1}' -f $group[0].module_name, $group[0].rva
                module_name = $group[0].module_name
                module_sha256 = $group[0].module_sha256
                rva = $group[0].rva
                interface_ids = @($group.interface_id | Sort-Object -Unique)
                catalog_statuses = @($group.catalog_status | Sort-Object -Unique)
                public_functions = @(
                    $group.public_function |
                        Where-Object { $null -ne $_ } |
                        Sort-Object -Unique
                )
            }
        } |
        Sort-Object -Property module_name, rva
)
if ($targetDefinitions.Count -eq 0) {
    throw 'Candidate inventory did not produce any breakpoint target.'
}

$priorObservationSha256 = $null
if ($TargetScope -eq 'ObservedUnidentified') {
    if ([string]::IsNullOrWhiteSpace($PriorObservationPath)) {
        throw 'PriorObservationPath is required for the ObservedUnidentified target scope.'
    }

    $PriorObservationPath = [IO.Path]::GetFullPath($PriorObservationPath)
    if (-not (Test-Path -LiteralPath $PriorObservationPath -PathType Leaf)) {
        throw "Prior attached observation was not found: '$PriorObservationPath'."
    }

    $priorItem = Get-Item -LiteralPath $PriorObservationPath
    if (($priorItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $priorItem.Length -gt 16MB) {
        throw 'Prior attached observation must be a regular local file no larger than 16 MiB.'
    }

    $attachedSchemaPath = Join-Path `
        $projectRoot `
        'docs\schema\nvapi-candidate-call-observation-v1.schema.json'
    $priorJson = Get-Content -LiteralPath $PriorObservationPath -Raw
    if (-not ($priorJson | Test-Json -SchemaFile $attachedSchemaPath)) {
        throw 'Prior observation does not satisfy nvapi-candidate-call-observation-v1.'
    }

    $priorObservation = $priorJson | ConvertFrom-Json
    if ($priorObservation.gpuz_sha256 -ne $gpuzSha256 -or
        $priorObservation.candidate_inventory_sha256 -ne $candidateInventorySha256) {
        throw 'Prior observation does not match this GPU-Z image and candidate inventory.'
    }

    $priorTargetKeys = @(
        $priorObservation.targets |
            Where-Object {
                $_.call_count -gt 0 -and
                $_.catalog_statuses -contains 'not_in_public_catalog'
            } |
            ForEach-Object { '{0}|{1}' -f $_.module_name, $_.rva } |
            Sort-Object -Unique
    )
    $targetDefinitions = @(
        $targetDefinitions |
            Where-Object { $priorTargetKeys -contains $_.trace_key }
    )
    if ($targetDefinitions.Count -eq 0) {
        throw 'Prior observation did not contain any executed unidentified target.'
    }

    $priorObservationSha256 = (Get-FileHash `
        -LiteralPath $PriorObservationPath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
}
elseif (-not [string]::IsNullOrWhiteSpace($PriorObservationPath)) {
    throw 'PriorObservationPath is accepted only with TargetScope ObservedUnidentified.'
}

$moduleFiles = @{}
foreach ($moduleGroup in @($targetDefinitions | Group-Object -Property module_name, module_sha256)) {
    $module = $moduleGroup.Group[0]
    $moduleFiles[$module.module_name] = Resolve-SignedModuleByHash `
        -ModuleName $module.module_name `
        -ExpectedSha256 $module.module_sha256
}

$evidenceRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'evidence'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path `
        $evidenceRoot `
        ('gpuz-nvapi-attached-{0}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
}

$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$evidencePrefix = $evidenceRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) +
    [IO.Path]::DirectorySeparatorChar
if (-not $OutputDirectory.StartsWith(
        $evidencePrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must be a child of '$evidenceRoot'."
}

if (Test-Path -LiteralPath $OutputDirectory) {
    throw "OutputDirectory already exists: '$OutputDirectory'."
}

$null = New-Item -ItemType Directory -Path $OutputDirectory
$failurePath = Join-Path $OutputDirectory 'capture-error.txt'
$commandPath = Join-Path $OutputDirectory 'windbg-commands.txt'
$debugLogPath = Join-Path $OutputDirectory 'windbg-nvapi-attached.log'
$debugOutputPath = Join-Path $OutputDirectory 'cdb-output.txt'
$debugErrorPath = Join-Path $OutputDirectory 'cdb-error.txt'
$detachOutputPath = Join-Path $OutputDirectory 'cdb-detach-output.txt'
$detachErrorPath = Join-Path $OutputDirectory 'cdb-detach-error.txt'
$reportPath = Join-Path $OutputDirectory 'nvapi-candidate-call-report.json'

$commandLines = [Collections.Generic.List[string]]::new()
$commandLines.Add('.echo RTXMON_ATTACH_READY')
foreach ($target in $targetDefinitions) {
    $moduleSymbol = [IO.Path]::GetFileNameWithoutExtension($target.module_name)
    if ($CaptureInputWords) {
        $commandLines.Add((
                'bp {0}+{1} ".printf \"RTXMON_NVAPI_TARGET module={2} rva={1} tid=0x%08x caller=0x%08x ecx=0x%08x edx=0x%08x a0=0x%08x a1=0x%08x a2=0x%08x a3=0x%08x\\n\", @$tid, poi(@esp), @ecx, @edx, poi(@esp+4), poi(@esp+8), poi(@esp+c), poi(@esp+10); gc"' -f
                $moduleSymbol,
                $target.rva,
                $target.module_name
            ))
    }
    else {
        $commandLines.Add((
                'bp {0}+{1} ".printf \"RTXMON_NVAPI_TARGET module={2} rva={1} tid=0x%08x caller=0x%08x\\n\", @$tid, poi(@esp); gc"' -f
                $moduleSymbol,
                $target.rva,
                $target.module_name
            ))
    }
}
$commandLines.Add('g')
$commandLines | Set-Content -LiteralPath $commandPath -Encoding ascii

function Invoke-CdbDetach {
    param(
        [Parameter(Mandatory)]
        [string]$PipeName,

        [Parameter(Mandatory)]
        [Diagnostics.Process]$ServerProcess
    )

    $remote = 'npipe:server=localhost,pipe={0}' -f $PipeName
    $client = Start-Process `
        -FilePath $CdbPath `
        -ArgumentList @('-remote', $remote, '-bonc', '-c', 'qqd') `
        -RedirectStandardOutput $detachOutputPath `
        -RedirectStandardError $detachErrorPath `
        -WindowStyle Hidden `
        -PassThru

    if (-not $client.WaitForExit(15000)) {
        throw 'The CDB detach client did not exit within 15 seconds.'
    }

    if (-not $ServerProcess.WaitForExit(15000)) {
        throw 'CDB did not detach normally; it was left running to protect GPU-Z.'
    }

    $detachTranscript = Get-Content -LiteralPath $debugLogPath -Raw
    if ($detachTranscript -notmatch '(?ms)> qqd\s+quit:') {
        throw "CDB exited without a confirmed qqd detach; client exit code: $($client.ExitCode)."
    }
}

$pipeName = 'rtx-monitor-gpuz-nvapi-{0}-{1}' -f `
    $gpuProcess.Id,
    ([Guid]::NewGuid().ToString('N'))
$debugger = $null
try {
    $debugger = Start-Process `
        -FilePath $CdbPath `
        -ArgumentList @(
            '-server', ('npipe:pipe={0}' -f $pipeName),
            '-pd',
            '-noshell',
            '-nosqm',
            '-logo', ('"{0}"' -f $debugLogPath),
            '-p', [string]$gpuProcess.Id,
            '-cf', ('"{0}"' -f $commandPath)
        ) `
        -RedirectStandardOutput $debugOutputPath `
        -RedirectStandardError $debugErrorPath `
        -WindowStyle Hidden `
        -PassThru

    $readyDeadline = (Get-Date).AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 250
        $ready = (Test-Path -LiteralPath $debugLogPath -PathType Leaf) -and
            (Select-String `
                -LiteralPath $debugLogPath `
                -Pattern 'RTXMON_ATTACH_READY' `
                -Quiet)
        $debugger.Refresh()
    } while (-not $ready -and -not $debugger.HasExited -and
        (Get-Date) -lt $readyDeadline)

    if (-not $ready) {
        throw 'CDB did not reach the attached NVAPI capture ready marker.'
    }

    Start-Sleep -Seconds $DurationSeconds
    Invoke-CdbDetach -PipeName $pipeName -ServerProcess $debugger
}
finally {
    if ($null -ne $debugger) {
        $debugger.Refresh()
        if (-not $debugger.HasExited) {
            Invoke-CdbDetach -PipeName $pipeName -ServerProcess $debugger
        }
    }
}

$gpuProcess.Refresh()
if ($gpuProcess.HasExited) {
    throw 'GPU-Z exited while the debugger detached.'
}

$debugText = Get-Content -LiteralPath $debugLogPath -Raw
$debuggerCommandFailure = [regex]::Match(
    $debugText,
    'Command file execution failed|Syntax error|Some commands were skipped|Numeric expression missing|Couldn.t resolve|Unable to insert breakpoint',
    [Text.RegularExpressions.RegexOptions]::IgnoreCase)
if ($debuggerCommandFailure.Success) {
    throw "WinDbg rejected a capture command: $($debuggerCommandFailure.Value)"
}

$hitPattern = if ($CaptureInputWords) {
    'RTXMON_NVAPI_TARGET module=(?<module>nvapi(?:_impl)?\.dll) rva=(?<rva>0x[0-9a-f]{8}) tid=0x(?<tid>[0-9a-f]{8}) caller=0x(?<caller>[0-9a-f]{8}) ecx=0x(?<ecx>[0-9a-f]{8}) edx=0x(?<edx>[0-9a-f]{8}) a0=0x(?<a0>[0-9a-f]{8}) a1=0x(?<a1>[0-9a-f]{8}) a2=0x(?<a2>[0-9a-f]{8}) a3=0x(?<a3>[0-9a-f]{8})'
}
else {
    'RTXMON_NVAPI_TARGET module=(?<module>nvapi(?:_impl)?\.dll) rva=(?<rva>0x[0-9a-f]{8}) tid=0x(?<tid>[0-9a-f]{8}) caller=0x(?<caller>[0-9a-f]{8})'
}
$hits = @(
    [regex]::Matches(
        $debugText,
        $hitPattern,
        [Text.RegularExpressions.RegexOptions]::IgnoreCase) |
        ForEach-Object {
            $inputWords = if ($CaptureInputWords) {
                @(
                    '0x' + $_.Groups['ecx'].Value.ToLowerInvariant()
                    '0x' + $_.Groups['edx'].Value.ToLowerInvariant()
                    '0x' + $_.Groups['a0'].Value.ToLowerInvariant()
                    '0x' + $_.Groups['a1'].Value.ToLowerInvariant()
                    '0x' + $_.Groups['a2'].Value.ToLowerInvariant()
                    '0x' + $_.Groups['a3'].Value.ToLowerInvariant()
                )
            }
            else {
                @()
            }
            [pscustomobject]@{
                target_key = '{0}|{1}' -f `
                    $_.Groups['module'].Value.ToLowerInvariant(),
                    $_.Groups['rva'].Value.ToLowerInvariant()
                module_name = $_.Groups['module'].Value.ToLowerInvariant()
                rva = $_.Groups['rva'].Value.ToLowerInvariant()
                thread_id = '0x' + $_.Groups['tid'].Value.ToLowerInvariant()
                caller_address = [Convert]::ToUInt64($_.Groups['caller'].Value, 16)
                input_words = $inputWords
                input_key = $inputWords -join '|'
            }
        }
)

$moduleRanges = @(
    [regex]::Matches(
        $debugText,
        '(?m)^ModLoad:\s+(?<base>[0-9a-f]{8})\s+(?<limit>[0-9a-f]{8})\s+(?<path>.+?)\s*$') |
        ForEach-Object {
            [pscustomobject]@{
                base_address = [Convert]::ToUInt64($_.Groups['base'].Value, 16)
                limit_address = [Convert]::ToUInt64($_.Groups['limit'].Value, 16)
                path = $_.Groups['path'].Value.Trim()
            }
        }
)
$gpuzRange = $moduleRanges |
    Where-Object { [IO.Path]::GetFileName($_.path) -eq 'GPU-Z.exe' } |
    Select-Object -First 1
if ($null -eq $gpuzRange) {
    throw 'CDB log did not contain the GPU-Z image range needed for caller normalization.'
}

$knownModuleRanges = @(
    foreach ($range in $moduleRanges) {
        $fileName = [IO.Path]::GetFileName($range.path)
        if ($fileName -eq 'GPU-Z.exe') {
            [pscustomobject]@{
                base_address = $range.base_address
                limit_address = $range.limit_address
                module_name = 'GPU-Z.exe'
                module_sha256 = $gpuzSha256
            }
            continue
        }

        $knownTarget = $targetDefinitions |
            Where-Object { $_.module_name -eq $fileName } |
            Select-Object -First 1
        if ($null -ne $knownTarget) {
            [pscustomobject]@{
                base_address = $range.base_address
                limit_address = $range.limit_address
                module_name = $knownTarget.module_name
                module_sha256 = $knownTarget.module_sha256
            }
        }
    }
)

$normalizedHits = @(
    foreach ($hit in $hits) {
        $callerRange = $knownModuleRanges |
            Where-Object {
                $hit.caller_address -ge $_.base_address -and
                $hit.caller_address -lt $_.limit_address
            } |
            Select-Object -First 1
        [pscustomobject]@{
            target_key = $hit.target_key
            thread_id = $hit.thread_id
            caller_key = if ($null -ne $callerRange) {
                '{0}|0x{1:x8}' -f `
                    $callerRange.module_name,
                    ($hit.caller_address - $callerRange.base_address)
            }
            else {
                'unresolved'
            }
            caller_module_name = if ($null -ne $callerRange) {
                $callerRange.module_name
            }
            else {
                $null
            }
            caller_module_sha256 = if ($null -ne $callerRange) {
                $callerRange.module_sha256
            }
            else {
                $null
            }
            caller_rva = if ($null -ne $callerRange) {
                '0x{0:x8}' -f ($hit.caller_address - $callerRange.base_address)
            }
            else {
                $null
            }
            input_words = $hit.input_words
            input_key = $hit.input_key
        }
    }
)

$targets = @(
    foreach ($target in $targetDefinitions) {
        $targetKey = '{0}|{1}' -f $target.module_name, $target.rva
        $targetHits = @($normalizedHits | Where-Object { $_.target_key -eq $targetKey })
        $callSites = @(
            $targetHits |
                Group-Object -Property caller_key |
                ForEach-Object {
                    $site = $_.Group[0]
                    [pscustomobject]@{
                        caller_module_name = $site.caller_module_name
                        caller_module_sha256 = $site.caller_module_sha256
                        caller_rva = $site.caller_rva
                        thread_ids = @($_.Group.thread_id | Sort-Object -Unique)
                        call_count = $_.Count
                    }
                } |
                Sort-Object -Property caller_module_name, caller_rva
        )
        $targetReport = [ordered]@{
            module_name = $target.module_name
            module_sha256 = $target.module_sha256
            rva = $target.rva
            interface_ids = $target.interface_ids
            catalog_statuses = $target.catalog_statuses
            public_functions = $target.public_functions
            call_count = $targetHits.Count
            call_sites = $callSites
        }
        if ($CaptureInputWords) {
            $targetReport.bounded_input_patterns = @(
                $targetHits |
                    Group-Object -Property input_key |
                    ForEach-Object {
                        $words = $_.Group[0].input_words
                        [pscustomobject]@{
                            ecx = $words[0]
                            edx = $words[1]
                            stack_dwords = @($words[2], $words[3], $words[4], $words[5])
                            call_count = $_.Count
                        }
                    } |
                    Sort-Object -Property @{ Expression = 'call_count'; Descending = $true }
            )
        }

        [pscustomobject]$targetReport
    }
)
if (($targets | Measure-Object -Property call_count -Sum).Sum -ne $hits.Count) {
    throw 'Attached NVAPI hit totals are inconsistent after target normalization.'
}

$capturedUtc = [DateTimeOffset]::UtcNow.ToString('O')
$report = [ordered]@{
    schema_version = 1
    source_kind = 'nvapi_candidate_call_observation'
    captured_utc = $capturedUtc
    duration_seconds = $DurationSeconds
    gpuz_sha256 = $gpuzSha256
    process_id = $gpuProcess.Id
    debugger_sha256 = (Get-FileHash `
        -LiteralPath $CdbPath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    debugger_file_version = (Get-Item -LiteralPath $CdbPath).VersionInfo.FileVersion
    candidate_inventory_sha256 = $candidateInventorySha256
    capture_mode = if ($CaptureInputWords) {
        'bounded_input_words'
    }
    else {
        'call_sites_only'
    }
    target_scope = if ($TargetScope -eq 'ObservedUnidentified') {
        'previously_observed_unidentified'
    }
    else {
        'all_inventory_targets'
    }
    prior_observation_sha256 = $priorObservationSha256
    target_count = $targets.Count
    observed_target_count = @($targets | Where-Object { $_.call_count -gt 0 }).Count
    call_count = $hits.Count
    targets = $targets
    warning = if ($CaptureInputWords) {
        'This attached trace observes existing function entries, caller return addresses, ECX/EDX, and four stack dwords without dereferencing them. These bounded machine words do not establish an ABI, argument count, type, direction, unit, returned field, or physical sensor.'
    }
    else {
        'This attached trace observes only existing function-entry calls and caller return addresses. It does not call NVAPI, inspect arguments or buffers, read return values, or identify a physical sensor.'
    }
}
$report |
    ConvertTo-Json -Depth 12 |
    Set-Content -LiteralPath $reportPath -Encoding utf8NoBOM

$schemaPath = Join-Path `
    $projectRoot `
    'docs\schema\nvapi-candidate-call-observation-v1.schema.json'
$reportJson = Get-Content -LiteralPath $reportPath -Raw
if (-not ($reportJson | Test-Json -SchemaFile $schemaPath)) {
    throw 'Generated attached NVAPI candidate report does not satisfy its schema.'
}

$reportJson
