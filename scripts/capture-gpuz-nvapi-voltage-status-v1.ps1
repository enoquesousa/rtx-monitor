[CmdletBinding()]
param(
    [ValidateRange(10, 60)]
    [int]$DurationSeconds = 20,

    [int]$GpuzProcessId,

    [Parameter(Mandatory)]
    [string]$CandidateInventoryPath,

    [Parameter(Mandatory)]
    [string]$PriorObservationPath,

    [Parameter(Mandatory)]
    [string]$GpuzLogPath,

    [string]$HwinfoLogPath,

    [ValidateRange(0, 31)]
    [int]$GpuIndex = 0,

    [string]$RtxmonConsolePath,

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

$profile = [ordered]@{
    name = 'gpuz-2.70.0-nvapi-610.88-voltage-status-v1'
    gpu_name = 'NVIDIA GeForce RTX 3060'
    gpu_uuid = 'GPU-fca3647e-8390-15a8-f23b-d0f870c9accd'
    driver_version = '610.88'
    nvml_version = '13.610.88'
    pci_bus_id = '00000000:01:00.0'
    pci_vendor_id = '0x10de'
    pci_device_id = '0x2504'
    pci_subsystem_vendor_id = '0x10de'
    pci_subsystem_device_id = '0x1536'
    vbios_version = '94.06.25.00.fc'
    gpuz_sha256 = '6cb0ef29682452de81a9576808881685161411a1fad00938ba04131159979c29'
    candidate_inventory_sha256 = '3aaada9b367dacca7cf74511bae8532bd79b7f8bd06b9bb609056f3d9da1f1d7'
    prior_observation_sha256 = 'c7a63df5e6a30bccbba5ad8c1a62a9251c40d512cd74060e69e043cfc54f77b3'
    nvapi_module_name = 'nvapi_impl.dll'
    nvapi_module_sha256 = 'fbc9aed43bfa5bda19b7f83a809a081a0ce454b6d6003dcabc565ecb3e6afdaf'
    interface_id = '0x465f9bcf'
    function_rva = '0x00198010'
    caller_module_name = 'GPU-Z.exe'
    caller_rva = '0x0021cee7'
    debugger_module_name = 'GPU_Z'
    buffer_ebp_displacement_bytes = -80
    structure_version = '0x0001004c'
    structure_size_bytes = 76
    value_word_index = 10
    value_offset_bytes = 40
    scale_divisor = 1000000
}
$maximumGpuzPrefixSizeBytes = 16MB
$maximumHwinfoPrefixSizeBytes = 64MB
$maximumDebugLogSizeBytes = 16MB

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This attached voltage-status capture requires an elevated PowerShell session.'
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

function Assert-RegularLocalFile {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Description,

        [Parameter(Mandatory)]
        [long]$MaximumSizeBytes
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description was not found: '$Path'."
    }

    $item = Get-Item -LiteralPath $Path
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $item.Length -lt 1 -or $item.Length -gt $MaximumSizeBytes) {
        throw "$Description must be a regular file between 1 and $MaximumSizeBytes bytes."
    }

    $root = [IO.Path]::GetPathRoot($item.FullName)
    $drive = [IO.DriveInfo]::new($root)
    if ($drive.DriveType -ne [IO.DriveType]::Fixed) {
        throw "$Description must reside on a fixed local drive."
    }
}

function Wait-BoundedCaptureInterval {
    param(
        [Parameter(Mandatory)]
        [int]$Seconds,

        [Parameter(Mandatory)]
        [string]$DebugLogPath,

        [Parameter(Mandatory)]
        [long]$MaximumSizeBytes
    )

    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.Elapsed.TotalSeconds -lt $Seconds) {
        Start-Sleep -Milliseconds 100
        $debugLog = Get-Item -LiteralPath $DebugLogPath
        if (($debugLog.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            $debugLog.Length -gt $MaximumSizeBytes) {
            throw "The debugger transcript exceeded the bounded $MaximumSizeBytes-byte capture limit."
        }
    }
}

function Read-AnchoredJsonSnapshot {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Description,

        [Parameter(Mandatory)]
        [long]$MaximumSizeBytes
    )

    $stream = [IO.File]::Open(
        $Path,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    try {
        $length = $stream.Length
        if ($length -lt 1 -or $length -gt $MaximumSizeBytes) {
            throw "$Description must be a regular file between 1 and $MaximumSizeBytes bytes."
        }

        $bytes = [byte[]]::new([int]$length)
        $offset = 0
        while ($offset -lt $bytes.Length) {
            $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
            if ($read -le 0) {
                throw "$Description changed while its anchored snapshot was being read."
            }

            $offset += $read
        }
        if ($stream.Length -ne $length) {
            throw "$Description changed while its anchored snapshot was being read."
        }

        $textOffset = if ($bytes.Length -ge 3 -and
            $bytes[0] -eq 0xef -and
            $bytes[1] -eq 0xbb -and
            $bytes[2] -eq 0xbf) {
            3
        }
        else {
            0
        }
        try {
            $utf8 = [Text.UTF8Encoding]::new($false, $true)
            $text = $utf8.GetString(
                $bytes,
                $textOffset,
                $bytes.Length - $textOffset)
        }
        catch [Text.DecoderFallbackException] {
            throw "$Description is not valid UTF-8."
        }

        return [pscustomobject]@{
            text = $text
            sha256 = [Convert]::ToHexString(
                [Security.Cryptography.SHA256]::HashData($bytes)
            ).ToLowerInvariant()
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

    if ($ModuleName -ne 'nvapi_impl.dll') {
        throw "The fixed voltage profile does not accept module '$ModuleName'."
    }

    $windowsRoot = [Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)
    $repository = Join-Path $windowsRoot 'System32\DriverStore\FileRepository'
    $matches = @(
        Get-ChildItem `
            -LiteralPath $repository `
            -Filter $ModuleName `
            -File `
            -Recurse `
            -ErrorAction SilentlyContinue |
            ForEach-Object {
                $path = [IO.Path]::GetFullPath($_.FullName)
                $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($hash -eq $ExpectedSha256) {
                    $signature = Get-AuthenticodeSignature -LiteralPath $path
                    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
                        $null -eq $signature.SignerCertificate -or
                        ($signature.SignerCertificate.Subject -notlike '*NVIDIA Corporation*' -and
                            $signature.SignerCertificate.Subject -notlike '*Microsoft Windows Hardware Compatibility Publisher*')) {
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

function Get-PrefixSha256 {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [long]$Length
    )

    $stream = [IO.File]::Open(
        $Path,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
    try {
        if ($Length -lt 1 -or $stream.Length -lt $Length) {
            throw "The requested log prefix is no longer available: '$Path'."
        }

        $hash = [Security.Cryptography.IncrementalHash]::CreateHash(
            [Security.Cryptography.HashAlgorithmName]::SHA256)
        try {
            $buffer = [byte[]]::new(131072)
            $remaining = $Length
            while ($remaining -gt 0) {
                $requested = [int][Math]::Min($buffer.Length, $remaining)
                $read = $stream.Read($buffer, 0, $requested)
                if ($read -le 0) {
                    throw "The log prefix became truncated while hashing: '$Path'."
                }

                $hash.AppendData($buffer, 0, $read)
                $remaining -= $read
            }

            return [Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant()
        }
        finally {
            $hash.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Copy-FilePrefix {
    param(
        [Parameter(Mandatory)]
        [string]$SourcePath,

        [Parameter(Mandatory)]
        [string]$DestinationPath,

        [Parameter(Mandatory)]
        [long]$Length
    )

    $source = [IO.File]::Open(
        $SourcePath,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
    try {
        if ($Length -lt 1 -or $source.Length -lt $Length) {
            throw "The requested sealed log prefix is no longer available: '$SourcePath'."
        }

        $destination = [IO.File]::Open(
            $DestinationPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::Read)
        try {
            $buffer = [byte[]]::new(131072)
            $remaining = $Length
            while ($remaining -gt 0) {
                $requested = [int][Math]::Min($buffer.Length, $remaining)
                $read = $source.Read($buffer, 0, $requested)
                if ($read -le 0) {
                    throw "The live log became truncated while sealing its prefix: '$SourcePath'."
                }

                $destination.Write($buffer, 0, $read)
                $remaining -= $read
            }

            $destination.Flush($true)
        }
        finally {
            $destination.Dispose()
        }
    }
    finally {
        $source.Dispose()
    }
}

function Assert-ExactHeaderToken {
    param(
        [Parameter(Mandatory)]
        [string]$Header,

        [Parameter(Mandatory)]
        [string]$Token,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $escaped = [regex]::Escape($Token)
    $matches = [regex]::Matches($Header, "(?:(?<=^)|(?<=,))\s*`"?$escaped`"?\s*(?=,|$)")
    if ($matches.Count -ne 1) {
        throw "$Description must contain exactly one '$Token' column."
    }
}

function Get-CompleteTextPrefix {
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
        if ($stream.Length -lt 2) {
            throw "The live log is too short to contain a complete line: '$Path'."
        }

        $tailLength = [int][Math]::Min([long](1MB), $stream.Length)
        $tailStart = $stream.Length - $tailLength
        $stream.Position = $tailStart
        $buffer = [byte[]]::new($tailLength)
        $offset = 0
        while ($offset -lt $buffer.Length) {
            $read = $stream.Read($buffer, $offset, $buffer.Length - $offset)
            if ($read -eq 0) {
                throw "The live log changed while its complete-line boundary was read: '$Path'."
            }
            $offset += $read
        }

        $lastLf = -1
        for ($index = $buffer.Length - 1; $index -ge 0; $index--) {
            if ($buffer[$index] -eq 0x0a) {
                $lastLf = $index
                break
            }
        }
        if ($lastLf -lt 0) {
            throw "The live log tail has no complete line boundary: '$Path'."
        }

        $previousLf = -1
        for ($index = $lastLf - 1; $index -ge 0; $index--) {
            if ($buffer[$index] -eq 0x0a) {
                $previousLf = $index
                break
            }
        }
        if ($previousLf -lt 0 -and $tailStart -ne 0) {
            throw "The final complete live-log line exceeds the bounded tail: '$Path'."
        }

        $lineStart = $previousLf + 1
        $lineEnd = $lastLf
        if ($lineEnd -gt $lineStart -and $buffer[$lineEnd - 1] -eq 0x0d) {
            $lineEnd--
        }
        $line = [Text.Encoding]::UTF8.GetString(
            $buffer,
            $lineStart,
            $lineEnd - $lineStart)
        return [pscustomobject]@{
            size_bytes = $tailStart + $lastLf + 1
            last_line = $line
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-GpuzLogProbe {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $item = Get-Item -LiteralPath $Path
    $complete = Get-CompleteTextPrefix -Path $Path
    if ($complete.size_bytes -gt $maximumGpuzPrefixSizeBytes) {
        throw "The complete GPU-Z prefix exceeds the $maximumGpuzPrefixSizeBytes-byte analysis limit."
    }
    $header = Get-Content -LiteralPath $Path -TotalCount 1
    Assert-ExactHeaderToken `
        -Header $header `
        -Token 'GPU Voltage [V]' `
        -Description 'GPU-Z reference log'
    $lastSample = $complete.last_line
    if ($null -eq $lastSample -or
        $lastSample -notmatch '^(?<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}(?:\.\d{3})?)\s*,') {
        throw 'The GPU-Z reference log does not end in a timestamped sensor sample.'
    }

    return [pscustomobject]@{
        size_bytes = $complete.size_bytes
        last_write_utc = [DateTimeOffset]$item.LastWriteTimeUtc
        last_sample_local = $Matches.timestamp
    }
}

function Get-HwinfoLogProbe {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $item = Get-Item -LiteralPath $Path
    $complete = Get-CompleteTextPrefix -Path $Path
    if ($complete.size_bytes -gt $maximumHwinfoPrefixSizeBytes) {
        throw "The complete HWiNFO prefix exceeds the $maximumHwinfoPrefixSizeBytes-byte analysis limit."
    }
    $header = Get-Content -LiteralPath $Path -TotalCount 1
    Assert-ExactHeaderToken `
        -Header $header `
        -Token 'GPU Core Voltage [V]' `
        -Description 'HWiNFO reference log'
    $lastSample = $complete.last_line
    if ($null -eq $lastSample -or
        $lastSample -notmatch '^(?<date>\d{1,2}\.\d{1,2}\.\d{4}),(?<time>\d{1,2}:\d{2}:\d{2}(?:\.\d{3})?),') {
        throw 'The HWiNFO reference log does not end in a timestamped sensor sample.'
    }

    $timestampText = '{0} {1}' -f $Matches.date, $Matches.time
    $timestamp = [DateTime]::MinValue
    $formats = @('d.M.yyyy H:mm:ss.fff', 'd.M.yyyy H:mm:ss')
    if (-not [DateTime]::TryParseExact(
            $timestampText,
            $formats,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::None,
            [ref]$timestamp)) {
        throw 'The last HWiNFO sensor timestamp is invalid.'
    }

    return [pscustomobject]@{
        size_bytes = $complete.size_bytes
        last_write_utc = [DateTimeOffset]$item.LastWriteTimeUtc
        last_sample_local = $timestamp.ToString(
            'yyyy-MM-dd HH:mm:ss.fff',
            [Globalization.CultureInfo]::InvariantCulture)
    }
}

function Assert-StrictGrowth {
    param(
        [Parameter(Mandatory)]
        [object]$Before,

        [Parameter(Mandatory)]
        [object]$After,

        [Parameter(Mandatory)]
        [string]$Description
    )

    if ($After.size_bytes -le $Before.size_bytes -or
        $After.last_write_utc -le $Before.last_write_utc -or
        [string]::CompareOrdinal($After.last_sample_local, $Before.last_sample_local) -le 0) {
        throw "$Description did not grow to a newer timestamped sample."
    }
}

function Invoke-CdbDetach {
    param(
        [Parameter(Mandatory)]
        [string]$PipeName,

        [Parameter(Mandatory)]
        [Diagnostics.Process]$ServerProcess,

        [Parameter(Mandatory)]
        [string]$DebugLogPath,

        [Parameter(Mandatory)]
        [long]$MaximumSizeBytes,

        [Parameter(Mandatory)]
        [string]$DetachOutputPath,

        [Parameter(Mandatory)]
        [string]$DetachErrorPath
    )

    $remote = 'npipe:server=localhost,pipe={0}' -f $PipeName
    $client = Start-Process `
        -FilePath $CdbPath `
        -ArgumentList @('-remote', $remote, '-bonc', '-c', 'qqd') `
        -RedirectStandardOutput $DetachOutputPath `
        -RedirectStandardError $DetachErrorPath `
        -WindowStyle Hidden `
        -PassThru
    if (-not $client.WaitForExit(15000)) {
        throw 'The CDB detach client did not exit within 15 seconds.'
    }

    if (-not $ServerProcess.WaitForExit(15000)) {
        throw 'CDB did not detach normally; it was left running to protect GPU-Z.'
    }

    Assert-RegularLocalFile `
        -Path $DebugLogPath `
        -Description 'CDB detach transcript' `
        -MaximumSizeBytes $MaximumSizeBytes
    $detachTranscript = Get-Content -LiteralPath $DebugLogPath -Raw
    if ($detachTranscript -notmatch '(?ms)> qqd\s+quit:') {
        throw "CDB exited without a confirmed qqd detach; client exit code: $($client.ExitCode)."
    }
}

if (-not $IsWindows) {
    throw 'Attached NVAPI voltage-status tracing is supported only on Windows.'
}

Assert-Administrator
$projectRoot = Split-Path -Parent $PSScriptRoot
$candidateSchemaPath = Join-Path `
    $projectRoot `
    'docs\schema\nvapi-candidate-inventory-v1.schema.json'
$priorSchemaPath = Join-Path `
    $projectRoot `
    'docs\schema\nvapi-candidate-call-observation-v1.schema.json'
$reportSchemaPath = Join-Path `
    $projectRoot `
    'docs\schema\nvapi-voltage-status-v1-observation-v2.schema.json'

$CandidateInventoryPath = [IO.Path]::GetFullPath($CandidateInventoryPath)
$PriorObservationPath = [IO.Path]::GetFullPath($PriorObservationPath)
$GpuzLogPath = [IO.Path]::GetFullPath($GpuzLogPath)
Assert-RegularLocalFile `
    -Path $CandidateInventoryPath `
    -Description 'Candidate inventory' `
    -MaximumSizeBytes 16MB
Assert-RegularLocalFile `
    -Path $PriorObservationPath `
    -Description 'Prior attached observation' `
    -MaximumSizeBytes 16MB
Assert-RegularLocalFile `
    -Path $GpuzLogPath `
    -Description 'GPU-Z reference log' `
    -MaximumSizeBytes 16MB

$candidateInventorySnapshot = Read-AnchoredJsonSnapshot `
    -Path $CandidateInventoryPath `
    -Description 'Candidate inventory' `
    -MaximumSizeBytes 16MB
$candidateInventoryJson = $candidateInventorySnapshot.text
if (-not ($candidateInventoryJson | Test-Json -SchemaFile $candidateSchemaPath)) {
    throw 'Candidate inventory does not satisfy nvapi-candidate-inventory-v1.'
}

$priorObservationSnapshot = Read-AnchoredJsonSnapshot `
    -Path $PriorObservationPath `
    -Description 'Prior attached observation' `
    -MaximumSizeBytes 16MB
$priorObservationJson = $priorObservationSnapshot.text
if (-not ($priorObservationJson | Test-Json -SchemaFile $priorSchemaPath)) {
    throw 'Prior observation does not satisfy nvapi-candidate-call-observation-v1.'
}

$candidateInventory = $candidateInventoryJson | ConvertFrom-Json
$priorObservation = $priorObservationJson | ConvertFrom-Json
$candidateInventorySha256 = $candidateInventorySnapshot.sha256
$priorObservationSha256 = $priorObservationSnapshot.sha256
if ($candidateInventorySha256 -ne $profile.candidate_inventory_sha256 -or
    $priorObservationSha256 -ne $profile.prior_observation_sha256) {
    throw 'The supplied candidate inventory or prior observation is not the exact allowlisted artifact.'
}

if ($candidateInventorySha256 -ne $priorObservation.candidate_inventory_sha256 -or
    $candidateInventory.gpuz_sha256 -ne $priorObservation.gpuz_sha256 -or
    $candidateInventory.gpuz_sha256 -ne $profile.gpuz_sha256) {
    throw 'The supplied evidence chain is not anchored to the fixed GPU-Z profile.'
}

$candidateMatches = @(
    $candidateInventory.candidates |
        Where-Object {
            $_.interface_id -eq $profile.interface_id -and
            $_.module_name -eq $profile.nvapi_module_name -and
            $_.module_sha256 -eq $profile.nvapi_module_sha256 -and
            $_.rva -eq $profile.function_rva
        }
)
if ($candidateMatches.Count -ne 1) {
    throw 'The candidate inventory does not contain the exact allowlisted voltage entry.'
}

$priorTargets = @(
    $priorObservation.targets |
        Where-Object {
            $_.interface_ids -contains $profile.interface_id -and
            $_.module_name -eq $profile.nvapi_module_name -and
            $_.module_sha256 -eq $profile.nvapi_module_sha256 -and
            $_.rva -eq $profile.function_rva -and
            $_.call_count -gt 0
        }
)
if ($priorTargets.Count -ne 1) {
    throw 'The prior polling observation does not execute the allowlisted voltage entry.'
}

$priorCallSites = @(
    $priorTargets[0].call_sites |
        Where-Object {
            $_.caller_module_name -eq $profile.caller_module_name -and
            $_.caller_rva -eq $profile.caller_rva -and
            $_.call_count -gt 0
        }
)
if ($priorCallSites.Count -ne 1) {
    throw 'The prior observation does not contain the exact allowlisted GPU-Z voltage call site.'
}

$nvapiModulePath = Resolve-SignedModuleByHash `
    -ModuleName $profile.nvapi_module_name `
    -ExpectedSha256 $profile.nvapi_module_sha256
if ((Get-PeMachine -Path $nvapiModulePath) -ne 0x014c) {
    throw 'The allowlisted NVAPI implementation is not a 32-bit PE image.'
}

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
if (-not (Test-Path -LiteralPath $CdbPath -PathType Leaf) -or
    (Get-PeMachine -Path $CdbPath) -ne 0x014c) {
    throw "The signed x86 CDB executable was not found at '$CdbPath'."
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
    throw 'The fixed profile requires the signed 32-bit GPU-Z image.'
}

$gpuzSha256 = (Get-FileHash -LiteralPath $gpuzPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($gpuzSha256 -ne $profile.gpuz_sha256) {
    throw 'The running GPU-Z image does not match the fixed voltage profile.'
}

$hasHwinfoReference = -not [string]::IsNullOrWhiteSpace($HwinfoLogPath)
if ($hasHwinfoReference) {
    $HwinfoLogPath = [IO.Path]::GetFullPath($HwinfoLogPath)
    if ([string]::Equals($HwinfoLogPath, $GpuzLogPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'GPU-Z and HWiNFO reference logs must be different files.'
    }

    Assert-RegularLocalFile `
        -Path $HwinfoLogPath `
        -Description 'HWiNFO reference log' `
        -MaximumSizeBytes 64MB
    $hwinfoProcesses = @(Get-Process -Name 'HWiNFO64' -ErrorAction SilentlyContinue)
    if ($hwinfoProcesses.Count -ne 1 -or -not $hwinfoProcesses[0].Responding) {
        throw 'Exactly one responsive HWiNFO64 process is required when -HwinfoLogPath is supplied.'
    }
}

if ([string]::IsNullOrWhiteSpace($RtxmonConsolePath)) {
    $RtxmonConsolePath = Join-Path `
        $projectRoot `
        'csharp\RtxMonitor.Console\bin\Release\net8.0\RtxMonitor.Console.exe'
}

$RtxmonConsolePath = [IO.Path]::GetFullPath($RtxmonConsolePath)
Assert-RegularLocalFile `
    -Path $RtxmonConsolePath `
    -Description 'RTX Monitor identity probe' `
    -MaximumSizeBytes 16MB
$identityProbeSha256 = (Get-FileHash `
    -LiteralPath $RtxmonConsolePath `
    -Algorithm SHA256).Hash.ToLowerInvariant()
$identityText = & $RtxmonConsolePath `
    --capabilities `
    --gpu $GpuIndex `
    --json 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    throw "RTX Monitor identity probe failed with exit code $LASTEXITCODE."
}

$identity = $identityText | ConvertFrom-Json
if ($identity.schema_version -ne 2 -or
    $identity.gpu.index -ne $GpuIndex -or
    $identity.gpu.name -ne $profile.gpu_name -or
    $identity.gpu.uuid -ne $profile.gpu_uuid -or
    $identity.gpu.driver_version -ne $profile.driver_version -or
    $identity.gpu.nvml_version -ne $profile.nvml_version -or
    -not $identity.board.pci_identity_available -or
    $identity.board.pci_bus_id -ne $profile.pci_bus_id -or
    $identity.board.pci_vendor_id -ne $profile.pci_vendor_id -or
    $identity.board.pci_device_id -ne $profile.pci_device_id -or
    $identity.board.pci_subsystem_vendor_id -ne $profile.pci_subsystem_vendor_id -or
    $identity.board.pci_subsystem_device_id -ne $profile.pci_subsystem_device_id -or
    -not $identity.board.vbios_available -or
    $identity.board.vbios_version -ne $profile.vbios_version) {
    throw 'RTX Monitor identity probe does not match the exact GPU, PCI, VBIOS, and driver profile.'
}

$initialGpuzProbe = Get-GpuzLogProbe -Path $GpuzLogPath
if (([DateTimeOffset]::UtcNow - $initialGpuzProbe.last_write_utc).TotalSeconds -gt 5) {
    throw 'GPU-Z reference log is not advancing immediately before capture.'
}

$initialHwinfoProbe = $null
if ($hasHwinfoReference) {
    $initialHwinfoProbe = Get-HwinfoLogProbe -Path $HwinfoLogPath
    if (([DateTimeOffset]::UtcNow - $initialHwinfoProbe.last_write_utc).TotalSeconds -gt 5) {
        throw 'The supplied HWiNFO log is stale; omit -HwinfoLogPath for a GPU-Z-only capture.'
    }
}

$evidenceRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'evidence'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path `
        $evidenceRoot `
        ('gpuz-nvapi-voltage-status-v2-{0}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
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
$debugLogPath = Join-Path $OutputDirectory 'windbg-voltage-status-v2.log'
$debugOutputPath = Join-Path $OutputDirectory 'cdb-output.txt'
$debugErrorPath = Join-Path $OutputDirectory 'cdb-error.txt'
$detachOutputPath = Join-Path $OutputDirectory 'cdb-detach-output.txt'
$detachErrorPath = Join-Path $OutputDirectory 'cdb-detach-error.txt'
$reportPath = Join-Path $OutputDirectory 'nvapi-voltage-status-v1-observation-v2.json'

$wordFormatParts = @()
$wordExpressions = @()
for ($wordIndex = 0; $wordIndex -lt 19; $wordIndex++) {
    $wordFormatParts += 'w{0:D2}=0x%08x' -f $wordIndex
    $offset = $wordIndex * 4
    $wordExpressions += if ($offset -eq 0) {
        'poi(@ebp-0x50)'
    }
    else {
        'poi(@ebp-0x50+0x{0:x})' -f $offset
    }
}

$hitFormat = 'RTXMON_NVAPI_VOLTAGE_V2 tid=0x%08x status=0x%08x {0}\\n' -f
    ($wordFormatParts -join ' ')
$breakpointCommand = 'bp {0}+{1} ".printf \"{2}\", @$tid, @eax, {3}; gc"' -f
    $profile.debugger_module_name,
    $profile.caller_rva,
    $hitFormat,
    ($wordExpressions -join ', ')
@(
    '.echo RTXMON_ATTACH_READY',
    $breakpointCommand,
    'g'
) | Set-Content -LiteralPath $commandPath -Encoding ascii

$pipeName = 'rtx-monitor-gpuz-voltage-v2-{0}-{1}' -f `
    $gpuProcess.Id,
    ([Guid]::NewGuid().ToString('N'))
$captureSessionId = [Guid]::NewGuid().ToString('D')
$debugger = $null
$captureStartedUtc = $null
$midpointGpuzProbe = $null
$midpointHwinfoProbe = $null
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
        $ready = $false
        if (Test-Path -LiteralPath $debugLogPath -PathType Leaf) {
            $debugLog = Get-Item -LiteralPath $debugLogPath
            if (($debugLog.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
                $debugLog.Length -gt $maximumDebugLogSizeBytes) {
                throw "The debugger transcript exceeded the bounded $maximumDebugLogSizeBytes-byte capture limit."
            }
            if ($debugLog.Length -gt 0) {
                $ready = Select-String `
                    -LiteralPath $debugLogPath `
                    -Pattern 'RTXMON_ATTACH_READY' `
                    -Quiet
            }
        }
        $debugger.Refresh()
    } while (-not $ready -and -not $debugger.HasExited -and
        (Get-Date) -lt $readyDeadline)

    if (-not $ready) {
        throw 'CDB did not reach the attached voltage capture ready marker.'
    }

    $captureStartedUtc = [DateTimeOffset]::UtcNow
    $firstHalfSeconds = [Math]::Floor($DurationSeconds / 2)
    Wait-BoundedCaptureInterval `
        -Seconds $firstHalfSeconds `
        -DebugLogPath $debugLogPath `
        -MaximumSizeBytes $maximumDebugLogSizeBytes
    $midpointGpuzProbe = Get-GpuzLogProbe -Path $GpuzLogPath
    Assert-StrictGrowth `
        -Before $initialGpuzProbe `
        -After $midpointGpuzProbe `
        -Description 'GPU-Z reference log during the first half'
    if ($hasHwinfoReference) {
        $midpointHwinfoProbe = Get-HwinfoLogProbe -Path $HwinfoLogPath
        Assert-StrictGrowth `
            -Before $initialHwinfoProbe `
            -After $midpointHwinfoProbe `
            -Description 'HWiNFO reference log during the first half'
    }

    Wait-BoundedCaptureInterval `
        -Seconds ($DurationSeconds - $firstHalfSeconds) `
        -DebugLogPath $debugLogPath `
        -MaximumSizeBytes $maximumDebugLogSizeBytes
    Invoke-CdbDetach `
        -PipeName $pipeName `
        -ServerProcess $debugger `
        -DebugLogPath $debugLogPath `
        -MaximumSizeBytes $maximumDebugLogSizeBytes `
        -DetachOutputPath $detachOutputPath `
        -DetachErrorPath $detachErrorPath
}
finally {
    if ($null -ne $debugger) {
        $debugger.Refresh()
        if (-not $debugger.HasExited) {
            Invoke-CdbDetach `
                -PipeName $pipeName `
                -ServerProcess $debugger `
                -DebugLogPath $debugLogPath `
                -MaximumSizeBytes $maximumDebugLogSizeBytes `
                -DetachOutputPath $detachOutputPath `
                -DetachErrorPath $detachErrorPath
        }
    }
}

$capturedUtc = [DateTimeOffset]::UtcNow
$finalGpuzProbe = Get-GpuzLogProbe -Path $GpuzLogPath
Assert-StrictGrowth `
    -Before $midpointGpuzProbe `
    -After $finalGpuzProbe `
    -Description 'GPU-Z reference log during the second half'
$finalHwinfoProbe = $null
if ($hasHwinfoReference) {
    $finalHwinfoProbe = Get-HwinfoLogProbe -Path $HwinfoLogPath
    Assert-StrictGrowth `
        -Before $midpointHwinfoProbe `
        -After $finalHwinfoProbe `
        -Description 'HWiNFO reference log during the second half'
}

$gpuProcess.Refresh()
if ($gpuProcess.HasExited) {
    throw 'GPU-Z exited while the debugger detached.'
}

Assert-RegularLocalFile `
    -Path $debugLogPath `
    -Description 'CDB voltage capture transcript' `
    -MaximumSizeBytes $maximumDebugLogSizeBytes
$debugText = Get-Content -LiteralPath $debugLogPath -Raw
$debuggerCommandFailure = [regex]::Match(
    $debugText,
    'Command file execution failed|Syntax error|Some commands were skipped|Numeric expression missing|Couldn.t resolve|Unable to insert breakpoint',
    [Text.RegularExpressions.RegexOptions]::IgnoreCase)
if ($debuggerCommandFailure.Success) {
    throw "CDB reported a command failure: $($debuggerCommandFailure.Value)."
}

$loadedModuleMatches = [regex]::Matches(
    $debugText,
    '(?im)^ModLoad:\s+(?<start>[0-9a-f]{8})\s+(?<end>[0-9a-f]{8})\s+(?<path>[^\r\n]*\\nvapi_impl\.dll)\s*$')
if ($loadedModuleMatches.Count -ne 1) {
    throw 'The debugger transcript did not prove exactly one loaded nvapi_impl.dll image.'
}

$loadedModuleMatch = $loadedModuleMatches[0]
$loadedModulePath = [IO.Path]::GetFullPath(
    $loadedModuleMatch.Groups['path'].Value.Trim())
Assert-RegularLocalFile `
    -Path $loadedModulePath `
    -Description 'NVAPI module loaded in the GPU-Z target' `
    -MaximumSizeBytes 64MB
$loadedModuleSha256 = (Get-FileHash `
    -LiteralPath $loadedModulePath `
    -Algorithm SHA256).Hash.ToLowerInvariant()
if ($loadedModuleSha256 -ne $profile.nvapi_module_sha256) {
    throw 'The NVAPI image actually loaded in GPU-Z does not match the allowlisted SHA-256.'
}

$loadedModuleStart = [Convert]::ToUInt32(
    $loadedModuleMatch.Groups['start'].Value,
    16)
$loadedModuleEnd = [Convert]::ToUInt32(
    $loadedModuleMatch.Groups['end'].Value,
    16)
$functionRva = [Convert]::ToUInt32($profile.function_rva.Substring(2), 16)
if ($loadedModuleEnd -le $loadedModuleStart -or
    $functionRva -ge ([uint64]$loadedModuleEnd - $loadedModuleStart)) {
    throw 'The allowlisted voltage function RVA is outside the loaded NVAPI image range.'
}

$samples = [Collections.Generic.List[object]]::new()
$hitRecords = [regex]::Matches(
    $debugText,
    'RTXMON_NVAPI_VOLTAGE_V2 tid=0x[0-9a-f]{8} status=0x[0-9a-f]{8}(?: w[0-9]{2}=0x[0-9a-f]{8}){19}',
    [Text.RegularExpressions.RegexOptions]::IgnoreCase)
foreach ($hitRecord in $hitRecords) {
    $hitLine = $hitRecord.Value
    $header = [regex]::Match(
        $hitLine,
        'RTXMON_NVAPI_VOLTAGE_V2 tid=0x(?<tid>[0-9a-f]{8}) status=0x(?<status>[0-9a-f]{8})',
        [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $header.Success) {
        throw 'A voltage hit line has an invalid header.'
    }

    $wordMatches = [regex]::Matches(
        $hitLine,
        'w(?<index>[0-9]{2})=0x(?<value>[0-9a-f]{8})',
        [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if ($wordMatches.Count -ne 19) {
        throw 'A voltage hit does not contain exactly 19 bounded DWORDs.'
    }

    $rawWords = @(
        for ($index = 0; $index -lt 19; $index++) {
            $match = $wordMatches[$index]
            if ([int]$match.Groups['index'].Value -ne $index) {
                throw 'Voltage hit DWORD indices are not contiguous.'
            }

            '0x{0}' -f $match.Groups['value'].Value.ToLowerInvariant()
        }
    )
    $returnStatus = '0x{0}' -f $header.Groups['status'].Value.ToLowerInvariant()
    if ($returnStatus -ne '0x00000000') {
        throw "The fixed voltage call returned a non-success status: $returnStatus."
    }

    if ($rawWords[0] -ne $profile.structure_version) {
        throw "Voltage structure version changed from $($profile.structure_version)."
    }

    $selectedRaw = [Convert]::ToUInt32($rawWords[$profile.value_word_index].Substring(2), 16)
    if ($selectedRaw -lt 100000 -or $selectedRaw -gt 2000000) {
        throw "Voltage word 10 is outside the bounded microvolt range: $selectedRaw."
    }

    $samples.Add([pscustomobject]@{
            sequence = $samples.Count + 1
            thread_id = '0x{0}' -f $header.Groups['tid'].Value.ToLowerInvariant()
            caller_rva = $profile.caller_rva
            return_status = $returnStatus
            raw_words = $rawWords
            selected_raw_microvolts = [long]$selectedRaw
            selected_volts = $selectedRaw / [double]$profile.scale_divisor
        })
    if ($samples.Count -gt 100000) {
        throw 'The bounded capture exceeded the 100000-call observation limit.'
    }
}

if ($samples.Count -lt 3) {
    throw 'Fewer than three successful voltage-status calls were observed during the bounded window.'
}

$sealedGpuzName = 'sealed-gpuz-voltage-reference.csv'
$sealedGpuzPath = Join-Path $OutputDirectory $sealedGpuzName
Copy-FilePrefix `
    -SourcePath $GpuzLogPath `
    -DestinationPath $sealedGpuzPath `
    -Length $finalGpuzProbe.size_bytes
$sealedGpuzSha256 = (Get-FileHash `
    -LiteralPath $sealedGpuzPath `
    -Algorithm SHA256).Hash.ToLowerInvariant()

$gpuzReference = [ordered]@{
    file_name = $sealedGpuzName
    prefix_sha256 = $sealedGpuzSha256
    size_bytes_before = $initialGpuzProbe.size_bytes
    size_bytes_midpoint = $midpointGpuzProbe.size_bytes
    size_bytes_after = $finalGpuzProbe.size_bytes
    last_write_utc_before = $initialGpuzProbe.last_write_utc.ToString('O')
    last_write_utc_midpoint = $midpointGpuzProbe.last_write_utc.ToString('O')
    last_write_utc_after = $finalGpuzProbe.last_write_utc.ToString('O')
    last_sample_local_before = $initialGpuzProbe.last_sample_local
    last_sample_local_midpoint = $midpointGpuzProbe.last_sample_local
    last_sample_local_after = $finalGpuzProbe.last_sample_local
    grew_during_capture = $true
}

$hwinfoReference = $null
if ($hasHwinfoReference) {
    $sealedHwinfoName = 'sealed-hwinfo-voltage-reference.csv'
    $sealedHwinfoPath = Join-Path `
        $OutputDirectory `
        $sealedHwinfoName
    Copy-FilePrefix `
        -SourcePath $HwinfoLogPath `
        -DestinationPath $sealedHwinfoPath `
        -Length $finalHwinfoProbe.size_bytes
    $sealedHwinfoSha256 = (Get-FileHash `
        -LiteralPath $sealedHwinfoPath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    $hwinfoReference = [ordered]@{
        file_name = $sealedHwinfoName
        prefix_sha256 = $sealedHwinfoSha256
        size_bytes_before = $initialHwinfoProbe.size_bytes
        size_bytes_midpoint = $midpointHwinfoProbe.size_bytes
        size_bytes_after = $finalHwinfoProbe.size_bytes
        last_write_utc_before = $initialHwinfoProbe.last_write_utc.ToString('O')
        last_write_utc_midpoint = $midpointHwinfoProbe.last_write_utc.ToString('O')
        last_write_utc_after = $finalHwinfoProbe.last_write_utc.ToString('O')
        last_sample_local_before = $initialHwinfoProbe.last_sample_local
        last_sample_local_midpoint = $midpointHwinfoProbe.last_sample_local
        last_sample_local_after = $finalHwinfoProbe.last_sample_local
        grew_during_capture = $true
    }
}

$report = [ordered]@{
    schema_version = 2
    source_kind = 'nvapi_voltage_status_v1_observation'
    capture_session_id = $captureSessionId
    capture_started_utc = $captureStartedUtc.ToString('O')
    captured_utc = $capturedUtc.ToString('O')
    duration_seconds = $DurationSeconds
    process_id = $gpuProcess.Id
    profile = [ordered]@{
        profile_name = $profile.name
        gpu = [ordered]@{
            name = $identity.gpu.name
            uuid = $identity.gpu.uuid
            driver_version = $identity.gpu.driver_version
            nvml_version = $identity.gpu.nvml_version
            pci_bus_id = $identity.board.pci_bus_id
            pci_vendor_id = $identity.board.pci_vendor_id
            pci_device_id = $identity.board.pci_device_id
            pci_subsystem_vendor_id = $identity.board.pci_subsystem_vendor_id
            pci_subsystem_device_id = $identity.board.pci_subsystem_device_id
            vbios_version = $identity.board.vbios_version
        }
        identity_probe_sha256 = $identityProbeSha256
        gpuz_sha256 = $gpuzSha256
        debugger_sha256 = (Get-FileHash `
            -LiteralPath $CdbPath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        debugger_file_version = (Get-Item -LiteralPath $CdbPath).VersionInfo.FileVersion
        candidate_inventory_sha256 = $candidateInventorySha256
        prior_observation_sha256 = $priorObservationSha256
        nvapi_module_sha256 = $profile.nvapi_module_sha256
        loaded_nvapi_module = [ordered]@{
            file_name = [IO.Path]::GetFileName($loadedModulePath)
            file_sha256 = $loadedModuleSha256
            start_address = '0x{0:x8}' -f $loadedModuleStart
            end_address = '0x{0:x8}' -f $loadedModuleEnd
            proof_source = 'cdb_modload_target_image'
        }
        interface_id = $profile.interface_id
        function_rva = $profile.function_rva
        caller_module_name = $profile.caller_module_name
        caller_rva = $profile.caller_rva
        buffer_ebp_displacement_bytes = $profile.buffer_ebp_displacement_bytes
        structure_version = $profile.structure_version
        structure_size_bytes = $profile.structure_size_bytes
        value_word_index = $profile.value_word_index
        value_offset_bytes = $profile.value_offset_bytes
        scale_divisor = $profile.scale_divisor
    }
    references = [ordered]@{
        gpuz = $gpuzReference
        hwinfo = $hwinfoReference
    }
    call_count = $samples.Count
    samples = @($samples)
    warning = 'This fixed, fail-closed profile passively reads only the 76-byte buffer that signed GPU-Z already supplied at the allowlisted post-call site. HWiNFO is optional and is recorded only after strict three-point log growth; no private NVAPI call is initiated or generalized beyond this exact profile.'
}
$reportJson = $report | ConvertTo-Json -Depth 10
if (-not ($reportJson | Test-Json -SchemaFile $reportSchemaPath)) {
    throw 'Voltage-status report did not satisfy nvapi-voltage-status-v1-observation-v2.'
}

$reportJson | Set-Content -LiteralPath $reportPath -Encoding utf8NoBOM
if (Test-Path -LiteralPath $failurePath -PathType Leaf) {
    Remove-Item -LiteralPath $failurePath -Force
}

$report | ConvertTo-Json -Depth 10
