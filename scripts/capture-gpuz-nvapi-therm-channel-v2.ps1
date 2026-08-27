[CmdletBinding()]
param(
    [ValidateRange(10, 60)]
    [int]$DurationSeconds = 10,

    [int]$GpuzProcessId,

    [Parameter(Mandatory)]
    [string]$CandidateInventoryPath,

    [Parameter(Mandatory)]
    [string]$PriorObservationPath,

    [Parameter(Mandatory)]
    [string]$GpuzLogPath,

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
    name = 'gpuz-2.70.0-nvapi-610.88-therm-channel-status-v2'
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
    interface_id = '0x65fe3aad'
    function_rva = '0x001ad310'
    caller_module_name = 'GPU-Z.exe'
    caller_rva = '0x002225b5'
    debugger_module_name = 'GPU_Z'
    structure_version = '0x000200a8'
    structure_size_bytes = 168
    fixed_point_fractional_bits = 8
    buffer_ebp_displacement_bytes = -172
    value_word_indices = @(10, 11)
}
$maximumGpuzPrefixSizeBytes = 16MB
$maximumDebugLogSizeBytes = 128MB

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This attached thermal-channel capture requires an elevated PowerShell session.'
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

    if ($ModuleName -ne 'nvapi_impl.dll') {
        throw "The fixed thermal profile does not accept module '$ModuleName'."
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
            throw "The live log tail has no complete LF boundary: '$Path'."
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

        return [pscustomobject]@{
            size_bytes = $tailStart + $lastLf + 1
            last_line = [Text.Encoding]::UTF8.GetString(
                $buffer,
                $lineStart,
                $lineEnd - $lineStart)
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

    $complete = Get-CompleteTextPrefix -Path $Path
    if ($complete.size_bytes -gt $maximumGpuzPrefixSizeBytes) {
        throw "The complete GPU-Z prefix exceeds the $maximumGpuzPrefixSizeBytes-byte analysis limit."
    }
    $lastSample = $complete.last_line
    if ($null -eq $lastSample -or
        $lastSample -notmatch '^(?<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}(?:\.\d{3})?)\s*,') {
        throw 'The GPU-Z reference log does not end in a timestamped sensor sample.'
    }

    $item = Get-Item -LiteralPath $Path
    return [pscustomobject]@{
        size_bytes = $complete.size_bytes
        last_write_utc = [DateTimeOffset]$item.LastWriteTimeUtc
        last_sample_local = $Matches.timestamp
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
        throw "$Description did not grow to a newer timestamped LF-complete sample."
    }
}

function Copy-SealedPrefix {
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
            throw "The requested LF-complete prefix is no longer available: '$SourcePath'."
        }

        $destination = [IO.File]::Open(
            $DestinationPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::Read)
        try {
            $hash = [Security.Cryptography.IncrementalHash]::CreateHash(
                [Security.Cryptography.HashAlgorithmName]::SHA256)
            try {
                $buffer = [byte[]]::new(131072)
                $remaining = $Length
                [byte]$lastByte = 0
                while ($remaining -gt 0) {
                    $requested = [int][Math]::Min($buffer.Length, $remaining)
                    $read = $source.Read($buffer, 0, $requested)
                    if ($read -le 0) {
                        throw "The live log became truncated while sealing its prefix: '$SourcePath'."
                    }

                    $destination.Write($buffer, 0, $read)
                    $hash.AppendData($buffer, 0, $read)
                    $lastByte = $buffer[$read - 1]
                    $remaining -= $read
                }

                if ($lastByte -ne 0x0a) {
                    throw 'The sealed GPU-Z prefix does not end at an LF-complete boundary.'
                }

                $destination.Flush($true)
                return [pscustomobject]@{
                    file_name = [IO.Path]::GetFileName($DestinationPath)
                    size_bytes = $Length
                    sha256 = [Convert]::ToHexString(
                        $hash.GetHashAndReset()).ToLowerInvariant()
                }
            }
            finally {
                $hash.Dispose()
            }
        }
        finally {
            $destination.Dispose()
        }
    }
    finally {
        $source.Dispose()
    }
}

function Convert-HexWordToSignedInt32 {
    param(
        [Parameter(Mandatory)]
        [string]$Word
    )

    $unsigned = [Convert]::ToUInt32($Word.Substring(2), 16)
    return [BitConverter]::ToInt32([BitConverter]::GetBytes($unsigned), 0)
}

function Get-LoadedNvapiModuleProof {
    param(
        [Parameter(Mandatory)]
        [string]$DebugText,

        [Parameter(Mandatory)]
        [string]$ExpectedSha256,

        [Parameter(Mandatory)]
        [string]$FunctionRva
    )

    $proofBlocks = [regex]::Matches(
        $DebugText,
        'RTXMON_NVAPI_MODULE_PROOF_BEGIN\s*(?<body>.*?)RTXMON_NVAPI_MODULE_PROOF_END',
        [Text.RegularExpressions.RegexOptions]::Singleline -bor
            [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if ($proofBlocks.Count -ne 1) {
        throw 'The debugger transcript does not contain exactly one bounded lmv m nvapi_impl proof.'
    }

    $proofBody = $proofBlocks[0].Groups['body'].Value
    $rangeMatches = [regex]::Matches(
        $proofBody,
        '(?im)^\s*(?<start>[0-9a-f`]{8,17})\s+(?<end>[0-9a-f`]{8,17})\s+nvapi_impl(?:\s|$)')
    if ($rangeMatches.Count -ne 1) {
        throw 'lmv m nvapi_impl did not report exactly one mapped nvapi_impl range.'
    }

    $pathMatches = [regex]::Matches(
        $proofBody,
        '(?im)^\s*(?:Image path|Loaded image file):\s*(?<path>[^\r\n]+?nvapi_impl\.dll)\s*$')
    $loadedPaths = @(
        $pathMatches |
            ForEach-Object {
                [IO.Path]::GetFullPath($_.Groups['path'].Value.Trim().Trim('"'))
            } |
            Sort-Object -Unique
    )
    if ($loadedPaths.Count -ne 1) {
        throw 'lmv m nvapi_impl did not identify exactly one loaded nvapi_impl.dll file path.'
    }

    $loadedModulePath = $loadedPaths[0]
    if ([IO.Path]::GetFileName($loadedModulePath) -cne 'nvapi_impl.dll') {
        throw 'The loaded NVAPI proof resolved an unexpected module filename.'
    }

    Assert-RegularLocalFile `
        -Path $loadedModulePath `
        -Description 'NVAPI module proven loaded by lmv' `
        -MaximumSizeBytes 64MB
    if ((Get-PeMachine -Path $loadedModulePath) -ne 0x014c) {
        throw 'The nvapi_impl.dll proven loaded by lmv is not a 32-bit PE image.'
    }

    $loadedSignature = Get-AuthenticodeSignature -LiteralPath $loadedModulePath
    if ($loadedSignature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        $null -eq $loadedSignature.SignerCertificate -or
        ($loadedSignature.SignerCertificate.Subject -notlike '*NVIDIA Corporation*' -and
            $loadedSignature.SignerCertificate.Subject -notlike '*Microsoft Windows Hardware Compatibility Publisher*')) {
        throw 'The nvapi_impl.dll proven loaded by lmv does not have an allowlisted valid signature.'
    }

    $loadedModuleSha256 = (Get-FileHash `
        -LiteralPath $loadedModulePath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($loadedModuleSha256 -ne $ExpectedSha256) {
        throw 'The nvapi_impl.dll actually proven loaded by lmv does not match the allowlisted SHA-256.'
    }

    $startText = $rangeMatches[0].Groups['start'].Value.Replace('`', '')
    $endText = $rangeMatches[0].Groups['end'].Value.Replace('`', '')
    $mappedStart = [Convert]::ToUInt64($startText, 16)
    $mappedEnd = [Convert]::ToUInt64($endText, 16)
    if ($mappedStart -gt [uint32]::MaxValue -or
        $mappedEnd -gt [uint32]::MaxValue -or
        $mappedEnd -le $mappedStart) {
        throw 'lmv m nvapi_impl reported an invalid 32-bit mapped range.'
    }

    $mappedSize = [long]($mappedEnd - $mappedStart)
    if ($mappedSize -gt 16MB) {
        throw 'The nvapi_impl.dll mapped range exceeds the bounded 16 MiB profile.'
    }

    $functionOffset = [Convert]::ToUInt64($FunctionRva.Substring(2), 16)
    $functionAddress = $mappedStart + $functionOffset
    if ($functionAddress -lt $mappedStart -or $functionAddress -ge $mappedEnd) {
        throw 'The allowlisted thermal function RVA is outside the lmv-proven mapped range.'
    }

    return [pscustomobject][ordered]@{
        module_name = 'nvapi_impl.dll'
        proof_command = 'lmv m nvapi_impl'
        file_sha256 = $loadedModuleSha256
        mapped_start = '0x{0:x8}' -f $mappedStart
        mapped_end_exclusive = '0x{0:x8}' -f $mappedEnd
        mapped_size_bytes = $mappedSize
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
    throw 'Attached NVAPI thermal-channel tracing is supported only on Windows.'
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
    'docs\schema\nvapi-therm-channel-v2-observation-v2.schema.json'

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
    $candidateInventory.gpuz_sha256 -ne $priorObservation.gpuz_sha256) {
    throw 'Prior observation is not anchored to the supplied candidate inventory.'
}

if ($candidateInventory.gpuz_sha256 -ne $profile.gpuz_sha256) {
    throw "GPU-Z hash is not allowlisted by fixed profile '$($profile.name)'."
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
    throw 'The candidate inventory does not contain the exact allowlisted thermal entry.'
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
    throw 'The prior polling observation does not execute the allowlisted thermal entry.'
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
    throw 'The prior observation does not contain the exact allowlisted GPU-Z call site.'
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
    throw 'The running GPU-Z image does not match the fixed thermal profile.'
}

Assert-RegularLocalFile `
    -Path $GpuzLogPath `
    -Description 'GPU-Z reference log' `
    -MaximumSizeBytes 16MB

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
    throw 'RTX Monitor identity probe does not match the exact GPU, PCI, subsystem, VBIOS, driver, and NVML profile.'
}

$initialLogProbe = Get-GpuzLogProbe -Path $GpuzLogPath
if (([DateTimeOffset]::UtcNow - $initialLogProbe.last_write_utc).TotalSeconds -gt 5) {
    throw 'GPU-Z reference log is not advancing immediately before capture.'
}

$evidenceRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'evidence'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path `
        $evidenceRoot `
        ('gpuz-nvapi-therm-channel-v2-{0}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
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
$debugLogPath = Join-Path $OutputDirectory 'windbg-therm-channel-v2.log'
$debugOutputPath = Join-Path $OutputDirectory 'cdb-output.txt'
$debugErrorPath = Join-Path $OutputDirectory 'cdb-error.txt'
$detachOutputPath = Join-Path $OutputDirectory 'cdb-detach-output.txt'
$detachErrorPath = Join-Path $OutputDirectory 'cdb-detach-error.txt'
$reportPath = Join-Path $OutputDirectory 'nvapi-therm-channel-v2-observation-v2.json'

$wordFormatParts = @()
$wordExpressions = @()
for ($wordIndex = 0; $wordIndex -lt 42; $wordIndex++) {
    $wordFormatParts += 'w{0:D2}=0x%08x' -f $wordIndex
    $offset = $wordIndex * 4
    $wordExpressions += if ($offset -eq 0) {
        'poi(@ebp-0xac)'
    }
    else {
        'poi(@ebp-0xac+0x{0:x})' -f $offset
    }
}

$hitFormat = 'RTXMON_NVAPI_THERM_V2 tid=0x%08x channel=0x%08x status=0x%08x {0}\\n' -f
    ($wordFormatParts -join ' ')
$breakpointCommand = 'bp {0}+{1} ".printf \"{2}\", @$tid, @ebx, @eax, {3}; gc"' -f
    $profile.debugger_module_name,
    $profile.caller_rva,
    $hitFormat,
    ($wordExpressions -join ', ')
@(
    '.echo RTXMON_NVAPI_MODULE_PROOF_BEGIN',
    'lmv m nvapi_impl',
    '.echo RTXMON_NVAPI_MODULE_PROOF_END',
    '.echo RTXMON_ATTACH_READY',
    $breakpointCommand,
    'g'
) | Set-Content -LiteralPath $commandPath -Encoding ascii

$pipeName = 'rtx-monitor-gpuz-therm-v2-{0}-{1}' -f `
    $gpuProcess.Id,
    ([Guid]::NewGuid().ToString('N'))
$captureSessionId = [Guid]::NewGuid().ToString('D')
$debugger = $null
$captureStartedUtc = $null
$midpointLogProbe = $null
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
        throw 'CDB did not reach the attached thermal capture ready marker.'
    }

    $captureStartedUtc = [DateTimeOffset]::UtcNow
    $firstHalfSeconds = [Math]::Floor($DurationSeconds / 2)
    Wait-BoundedCaptureInterval `
        -Seconds $firstHalfSeconds `
        -DebugLogPath $debugLogPath `
        -MaximumSizeBytes $maximumDebugLogSizeBytes
    $midpointLogProbe = Get-GpuzLogProbe -Path $GpuzLogPath
    Assert-StrictGrowth `
        -Before $initialLogProbe `
        -After $midpointLogProbe `
        -Description 'GPU-Z reference log during the first half'

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
$finalLogProbe = Get-GpuzLogProbe -Path $GpuzLogPath
Assert-StrictGrowth `
    -Before $midpointLogProbe `
    -After $finalLogProbe `
    -Description 'GPU-Z reference log during the second half'

$gpuProcess.Refresh()
if ($gpuProcess.HasExited) {
    throw 'GPU-Z exited while the debugger detached.'
}

Assert-RegularLocalFile `
    -Path $debugLogPath `
    -Description 'CDB thermal capture transcript' `
    -MaximumSizeBytes $maximumDebugLogSizeBytes
$debugText = Get-Content -LiteralPath $debugLogPath -Raw
$debuggerCommandFailure = [regex]::Match(
    $debugText,
    'Command file execution failed|Syntax error|Some commands were skipped|Numeric expression missing|Couldn.t resolve|Unable to insert breakpoint',
    [Text.RegularExpressions.RegexOptions]::IgnoreCase)
if ($debuggerCommandFailure.Success) {
    throw "CDB reported a command failure: $($debuggerCommandFailure.Value)."
}

$loadedNvapiModule = Get-LoadedNvapiModuleProof `
    -DebugText $debugText `
    -ExpectedSha256 $profile.nvapi_module_sha256 `
    -FunctionRva $profile.function_rva

$samples = [Collections.Generic.List[object]]::new()
$hitRecords = [regex]::Matches(
    $debugText,
    'RTXMON_NVAPI_THERM_V2 tid=0x[0-9a-f]{8} channel=0x[0-9a-f]{8} status=0x[0-9a-f]{8}(?: w[0-9]{2}=0x[0-9a-f]{8}){42}',
    [Text.RegularExpressions.RegexOptions]::IgnoreCase)
foreach ($hitRecord in $hitRecords) {
    $hitLine = $hitRecord.Value
    $header = [regex]::Match(
        $hitLine,
        'RTXMON_NVAPI_THERM_V2 tid=0x(?<tid>[0-9a-f]{8}) channel=0x(?<channel>[0-9a-f]{8}) status=0x(?<status>[0-9a-f]{8})',
        [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $header.Success) {
        throw 'A thermal hit line has an invalid header.'
    }

    $wordMatches = [regex]::Matches(
        $hitLine,
        'w(?<index>[0-9]{2})=0x(?<value>[0-9a-f]{8})',
        [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if ($wordMatches.Count -ne 42) {
        throw 'A thermal hit does not contain exactly 42 bounded DWORDs.'
    }

    $rawWords = @(
        for ($index = 0; $index -lt 42; $index++) {
            $match = $wordMatches[$index]
            if ([int]$match.Groups['index'].Value -ne $index) {
                throw 'Thermal hit DWORD indices are not contiguous.'
            }

            '0x{0}' -f $match.Groups['value'].Value.ToLowerInvariant()
        }
    )
    $channelIndex = [Convert]::ToInt32($header.Groups['channel'].Value, 16)
    if ($channelIndex -lt 0 -or $channelIndex -gt 1) {
        throw "The fixed profile observed an unexpected thermal channel index: $channelIndex."
    }

    $returnStatus = '0x{0}' -f $header.Groups['status'].Value.ToLowerInvariant()
    if ($returnStatus -ne '0x00000000') {
        throw "The fixed thermal call returned a non-success status: $returnStatus."
    }

    if ($rawWords[0] -ne $profile.structure_version) {
        throw "Thermal structure version changed from $($profile.structure_version)."
    }

    $expectedMask = '0x{0:x8}' -f (1 -shl $channelIndex)
    if ($rawWords[1] -ne $expectedMask) {
        throw "Thermal channel mask $($rawWords[1]) does not match channel $channelIndex."
    }

    $selectedWordIndex = $profile.value_word_indices[$channelIndex]
    $selectedRaw = Convert-HexWordToSignedInt32 -Word $rawWords[$selectedWordIndex]
    $samples.Add([pscustomobject]@{
            sequence = $samples.Count + 1
            thread_id = '0x{0}' -f $header.Groups['tid'].Value.ToLowerInvariant()
            caller_rva = $profile.caller_rva
            channel_index = $channelIndex
            return_status = $returnStatus
            structure_version = $rawWords[0]
            channel_mask = $rawWords[1]
            raw_words = $rawWords
            selected_word_index = $selectedWordIndex
            selected_raw_fixed_8 = $selectedRaw
            selected_celsius = $selectedRaw / [Math]::Pow(2, $profile.fixed_point_fractional_bits)
        })
    if ($samples.Count -gt 100000) {
        throw 'The bounded capture exceeded the 100000-call thermal observation limit.'
    }
}

$channel0Count = @($samples | Where-Object { $_.channel_index -eq 0 }).Count
$channel1Count = @($samples | Where-Object { $_.channel_index -eq 1 }).Count
if ($channel0Count -lt 3 -or $channel0Count -ne $channel1Count) {
    throw 'The bounded window did not produce at least three balanced successful samples for each thermal channel.'
}

$sealedGpuzName = 'sealed-gpuz-thermal-reference.csv'
$sealedGpuzPath = Join-Path $OutputDirectory $sealedGpuzName
$sealedGpuz = Copy-SealedPrefix `
    -SourcePath $GpuzLogPath `
    -DestinationPath $sealedGpuzPath `
    -Length $finalLogProbe.size_bytes
$sealedGpuzItem = Get-Item -LiteralPath $sealedGpuzPath
$sealedGpuzHash = (Get-FileHash `
    -LiteralPath $sealedGpuzPath `
    -Algorithm SHA256).Hash.ToLowerInvariant()
if ($sealedGpuzItem.Length -ne $sealedGpuz.size_bytes -or
    $sealedGpuzHash -ne $sealedGpuz.sha256) {
    throw 'The sealed GPU-Z LF-complete prefix changed before the observation was committed.'
}

$debuggerSha256 = (Get-FileHash `
    -LiteralPath $CdbPath `
    -Algorithm SHA256).Hash.ToLowerInvariant()
$debuggerFileVersion = (Get-Item -LiteralPath $CdbPath).VersionInfo.FileVersion
$report = [ordered]@{
    schema_version = 2
    source_kind = 'nvapi_therm_channel_v2_observation'
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
        debugger_sha256 = $debuggerSha256
        debugger_file_version = $debuggerFileVersion
        candidate_inventory_sha256 = $candidateInventorySha256
        prior_observation_sha256 = $priorObservationSha256
        nvapi_module_sha256 = $profile.nvapi_module_sha256
        loaded_nvapi_module = $loadedNvapiModule
        interface_id = $profile.interface_id
        function_rva = $profile.function_rva
        caller_module_name = $profile.caller_module_name
        caller_rva = $profile.caller_rva
        buffer_ebp_displacement_bytes = $profile.buffer_ebp_displacement_bytes
        structure_version = $profile.structure_version
        structure_size_bytes = $profile.structure_size_bytes
        fixed_point_fractional_bits = $profile.fixed_point_fractional_bits
        value_word_indices = $profile.value_word_indices
    }
    references = [ordered]@{
        gpuz = [ordered]@{
            file_name = $sealedGpuz.file_name
            sealed_relative_path = $sealedGpuz.file_name
            prefix_sha256 = $sealedGpuz.sha256
            prefix_size_bytes = $sealedGpuz.size_bytes
            size_bytes_before = $initialLogProbe.size_bytes
            size_bytes_midpoint = $midpointLogProbe.size_bytes
            size_bytes_after = $finalLogProbe.size_bytes
            last_write_utc_before = $initialLogProbe.last_write_utc.ToString('O')
            last_write_utc_midpoint = $midpointLogProbe.last_write_utc.ToString('O')
            last_write_utc_after = $finalLogProbe.last_write_utc.ToString('O')
            last_sample_local_before = $initialLogProbe.last_sample_local
            last_sample_local_midpoint = $midpointLogProbe.last_sample_local
            last_sample_local_after = $finalLogProbe.last_sample_local
            grew_during_capture = $true
        }
    }
    call_count = $samples.Count
    samples = @($samples)
    warning = 'This fixed, fail-closed profile passively reads only the 168-byte buffer already supplied by signed GPU-Z at the allowlisted post-call site. The observation seals the exact LF-complete GPU-Z prefix and the lmv m nvapi_impl proof for the actually loaded signed module; it does not initiate NVAPI calls or assign general thermal semantics beyond this exact profile.'
}
$reportJson = $report | ConvertTo-Json -Depth 10
if (-not ($reportJson | Test-Json -SchemaFile $reportSchemaPath)) {
    throw 'Thermal-channel report did not satisfy nvapi-therm-channel-v2-observation-v2.'
}

$reportJson | Set-Content -LiteralPath $reportPath -Encoding utf8NoBOM
if (Test-Path -LiteralPath $failurePath -PathType Leaf) {
    Remove-Item -LiteralPath $failurePath -Force
}

$report | ConvertTo-Json -Depth 10
