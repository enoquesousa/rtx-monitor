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
    name = 'gpuz-2.70.0-nvapi-610.88-cooler-status-v1'
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
    prior_observation_sha256 = 'f580f67da61df2287257fb023fe277d310fdf424f588bbd96d01ac01433f8de2'
    nvapi_module_name = 'nvapi_impl.dll'
    nvapi_module_sha256 = 'fbc9aed43bfa5bda19b7f83a809a081a0ce454b6d6003dcabc565ecb3e6afdaf'
    interface_id = '0x35aed5e8'
    function_rva = '0x001b9f10'
    caller_module_name = 'GPU-Z.exe'
    caller_rvas = @('0x0021d654', '0x0021d824')
    debugger_module_name = 'GPU_Z'
    capture_point = 'post_call_return'
    buffer_pointer_expression = 'poi(@esp+4)'
    return_status_register = 'eax'
    structure_version = '0x000106a8'
    structure_size_bytes = 1704
    structure_word_count = 426
    count_byte_offset = 4
    entry_base_offset = 40
    entry_stride_bytes = 52
    entry_capacity = 32
    raw_field_byte_offsets_within_entry = @(4, 8, 12, 16)
}
$maximumCaptureRecords = 1024
$maximumDebugLogSizeBytes = 16MB

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This attached cooler-status capture requires an elevated PowerShell session.'
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
        throw "The fixed cooler-status profile does not accept module '$ModuleName'."
    }

    $windowsRoot = [Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)
    $repository = Join-Path $windowsRoot 'System32\DriverStore\FileRepository'
    $matches = @(
        Get-ChildItem -LiteralPath $repository -Filter $ModuleName -File -Recurse -ErrorAction SilentlyContinue |
            ForEach-Object {
                $path = [IO.Path]::GetFullPath($_.FullName)
                $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($hash -eq $ExpectedSha256) {
                    $signature = Get-AuthenticodeSignature -LiteralPath $path
                    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
                        $null -eq $signature.SignerCertificate -or
                        ($signature.SignerCertificate.Subject -notlike '*NVIDIA Corporation*' -and
                            $signature.SignerCertificate.Subject -notlike
                                '*Microsoft Windows Hardware Compatibility Publisher*')) {
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
        $item.Length -lt 1 -or
        $item.Length -gt $MaximumSizeBytes) {
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

function Get-LogProbe {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $item = Get-Item -LiteralPath $Path
    $lastSample = Get-Content -LiteralPath $Path -Tail 16 |
        Where-Object { $_ -match '^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\s*,' } |
        Select-Object -Last 1
    if ($null -eq $lastSample -or
        $lastSample -notmatch
            '^(?<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\s*,') {
        throw 'The GPU-Z reference log does not end in a timestamped sensor sample.'
    }

    return [pscustomobject]@{
        size_bytes = $item.Length
        last_write_utc = [DateTimeOffset]$item.LastWriteTimeUtc
        last_sample_local = $Matches.timestamp
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
    $client = Start-Process -FilePath $CdbPath -ArgumentList @(
        '-remote',
        $remote,
        '-bonc',
        '-c',
        'qqd'
    ) -RedirectStandardOutput $DetachOutputPath -RedirectStandardError $DetachErrorPath -WindowStyle Hidden -PassThru

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
    throw 'Attached NVAPI cooler-status tracing is supported only on Windows.'
}

Assert-Administrator
$projectRoot = Split-Path -Parent $PSScriptRoot
$candidateSchemaPath = Join-Path $projectRoot 'docs\schema\nvapi-candidate-inventory-v1.schema.json'
$priorSchemaPath = Join-Path $projectRoot 'docs\schema\nvapi-candidate-call-observation-v1.schema.json'
$reportSchemaPath = Join-Path $projectRoot 'docs\schema\nvapi-cooler-status-v1-observation-v2.schema.json'

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
            $_.catalog_status -eq 'not_in_public_catalog' -and
            $_.module_name -eq $profile.nvapi_module_name -and
            $_.module_sha256 -eq $profile.nvapi_module_sha256 -and
            $_.rva -eq $profile.function_rva -and
            $_.execution_status -eq 'executed_entry'
        }
)
if ($candidateMatches.Count -ne 1) {
    throw 'The inventory does not contain the exact executed cooler-status candidate.'
}

if ($priorObservation.capture_mode -ne 'bounded_input_words') {
    throw 'The fixed profile requires a prior bounded-input observation.'
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
    throw 'The prior observation does not execute the exact cooler-status candidate.'
}

$priorCallSites = @($priorTargets[0].call_sites)
if ($priorCallSites.Count -ne $profile.caller_rvas.Count) {
    throw 'The prior observation does not contain exactly the two allowlisted call sites.'
}

foreach ($callerRva in $profile.caller_rvas) {
    $matches = @(
        $priorCallSites |
            Where-Object {
                $_.caller_module_name -eq $profile.caller_module_name -and
                $_.caller_module_sha256 -eq $profile.gpuz_sha256 -and
                $_.caller_rva -eq $callerRva -and
                $_.call_count -gt 0
            }
    )
    if ($matches.Count -ne 1) {
        throw "The prior observation does not anchor allowlisted call site $callerRva."
    }
}

$unexpectedCallSites = @(
    $priorCallSites |
        Where-Object {
            $_.caller_module_name -ne $profile.caller_module_name -or
            $_.caller_module_sha256 -ne $profile.gpuz_sha256 -or
            $profile.caller_rvas -notcontains $_.caller_rva
        }
)
if ($unexpectedCallSites.Count -ne 0) {
    throw 'The prior observation contains a call site outside the fixed profile.'
}

$nvapiModulePath = Resolve-SignedModuleByHash -ModuleName $profile.nvapi_module_name -ExpectedSha256 $profile.nvapi_module_sha256
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

$gpuzSha256 = (
    Get-FileHash -LiteralPath $gpuzPath -Algorithm SHA256
).Hash.ToLowerInvariant()
if ($gpuzSha256 -ne $profile.gpuz_sha256) {
    throw 'The running GPU-Z image does not match the fixed cooler-status profile.'
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

if (-not (Test-Path -LiteralPath $GpuzLogPath -PathType Leaf)) {
    throw "GPU-Z reference log was not found: '$GpuzLogPath'."
}

$gpuzLogItem = Get-Item -LiteralPath $GpuzLogPath
if (($gpuzLogItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
    $gpuzLogItem.Length -lt 1 -or
    $gpuzLogItem.Length -gt 64MB) {
    throw 'GPU-Z reference log must be a regular local file between 1 byte and 64 MiB.'
}

$logRoot = [IO.Path]::GetPathRoot($GpuzLogPath)
$logDrive = [IO.DriveInfo]::new($logRoot)
if ($logDrive.DriveType -ne [IO.DriveType]::Fixed) {
    throw 'GPU-Z reference log must reside on a fixed local drive.'
}

$initialLogProbe = Get-LogProbe -Path $GpuzLogPath
if (([DateTimeOffset]::UtcNow - $initialLogProbe.last_write_utc).TotalSeconds -gt 5) {
    throw 'GPU-Z reference log is not advancing immediately before capture.'
}

$evidenceRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'evidence'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $evidenceRoot (
        'gpuz-nvapi-cooler-status-v1-{0}' -f (Get-Date -Format 'yyyyMMdd-HHmmss')
    )
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
$debugLogPath = Join-Path $OutputDirectory 'windbg-cooler-status-v1.log'
$debugOutputPath = Join-Path $OutputDirectory 'cdb-output.txt'
$debugErrorPath = Join-Path $OutputDirectory 'cdb-error.txt'
$detachOutputPath = Join-Path $OutputDirectory 'cdb-detach-output.txt'
$detachErrorPath = Join-Path $OutputDirectory 'cdb-detach-error.txt'
$reportPath = Join-Path $OutputDirectory 'nvapi-cooler-status-v1-observation-v2.json'

$breakpointCommands = @(
    foreach ($callerRva in $profile.caller_rvas) {
        $hitFormat = (
            'RTXMON_NVAPI_COOLER_V1_BEGIN site={0} tid=0x%08x status=0x%08x buffer=0x%08x\\n' -f
                $callerRva
        )
        'bp {0}+{1} ".printf \"{2}\", @$tid, @eax, poi(@esp+4); dd /c 1 poi(@esp+4) L1aa; .echo RTXMON_NVAPI_COOLER_V1_END site={1}; gc"' -f
            $profile.debugger_module_name,
            $callerRva,
            $hitFormat
    }
)
@(
    '.echo RTXMON_ATTACH_READY'
    $breakpointCommands
    'g'
) | Set-Content -LiteralPath $commandPath -Encoding ascii

$pipeName = 'rtx-monitor-gpuz-cooler-v1-{0}-{1}' -f
    $gpuProcess.Id,
    ([Guid]::NewGuid().ToString('N'))
$debugger = $null
$captureStartedUtc = $null
$midpointLogProbe = $null
try {
    $debugger = Start-Process -FilePath $CdbPath -ArgumentList @(
        '-server',
        ('npipe:pipe={0}' -f $pipeName),
        '-pd',
        '-noshell',
        '-nosqm',
        '-logo',
        ('"{0}"' -f $debugLogPath),
        '-p',
        [string]$gpuProcess.Id,
        '-cf',
        ('"{0}"' -f $commandPath)
    ) -RedirectStandardOutput $debugOutputPath -RedirectStandardError $debugErrorPath -WindowStyle Hidden -PassThru

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
        throw 'CDB did not reach the attached cooler-status capture ready marker.'
    }

    $captureStartedUtc = [DateTimeOffset]::UtcNow
    $firstHalfSeconds = [Math]::Floor($DurationSeconds / 2)
    Wait-BoundedCaptureInterval `
        -Seconds $firstHalfSeconds `
        -DebugLogPath $debugLogPath `
        -MaximumSizeBytes $maximumDebugLogSizeBytes
    $midpointLogProbe = Get-LogProbe -Path $GpuzLogPath
    if ($midpointLogProbe.size_bytes -le $initialLogProbe.size_bytes -or
        $midpointLogProbe.last_write_utc -le $initialLogProbe.last_write_utc) {
        throw 'GPU-Z reference log did not grow during the first half of capture.'
    }

    Wait-BoundedCaptureInterval `
        -Seconds ($DurationSeconds - $firstHalfSeconds) `
        -DebugLogPath $debugLogPath `
        -MaximumSizeBytes $maximumDebugLogSizeBytes
    Invoke-CdbDetach -PipeName $pipeName -ServerProcess $debugger -DebugLogPath $debugLogPath -MaximumSizeBytes $maximumDebugLogSizeBytes -DetachOutputPath $detachOutputPath -DetachErrorPath $detachErrorPath
}
finally {
    if ($null -ne $debugger) {
        $debugger.Refresh()
        if (-not $debugger.HasExited) {
            Invoke-CdbDetach -PipeName $pipeName -ServerProcess $debugger -DebugLogPath $debugLogPath -MaximumSizeBytes $maximumDebugLogSizeBytes -DetachOutputPath $detachOutputPath -DetachErrorPath $detachErrorPath
        }
    }
}

$capturedUtc = [DateTimeOffset]::UtcNow
$finalLogProbe = Get-LogProbe -Path $GpuzLogPath
if ($null -eq $midpointLogProbe -or
    $finalLogProbe.size_bytes -le $midpointLogProbe.size_bytes -or
    $finalLogProbe.last_write_utc -le $midpointLogProbe.last_write_utc) {
    throw 'GPU-Z reference log did not continue growing through the capture window.'
}

$gpuProcess.Refresh()
if ($gpuProcess.HasExited -or -not $gpuProcess.Responding) {
    throw 'GPU-Z was not alive and responsive after the confirmed debugger detach.'
}

Assert-RegularLocalFile `
    -Path $debugLogPath `
    -Description 'Debugger transcript' `
    -MaximumSizeBytes $maximumDebugLogSizeBytes
$debugText = Get-Content -LiteralPath $debugLogPath -Raw
$debuggerCommandFailure = [regex]::Match(
    $debugText,
    'Command file execution failed|Syntax error|Some commands were skipped|Numeric expression missing|Couldn.t resolve|Unable to insert breakpoint|Memory access error',
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
if ((Get-PeMachine -Path $loadedModulePath) -ne 0x014c) {
    throw 'The NVAPI implementation loaded in GPU-Z is not a 32-bit PE image.'
}

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
    throw 'The allowlisted cooler function RVA is outside the loaded NVAPI image range.'
}

$recordPattern = (
    '(?ms)^RTXMON_NVAPI_COOLER_V1_BEGIN site=(?<site>0x[0-9a-f]{8}) ' +
    'tid=0x(?<tid>[0-9a-f]{8}) status=0x(?<status>[0-9a-f]{8}) ' +
    'buffer=0x(?<buffer>[0-9a-f]{8})\r?\n' +
    '(?<dump>.*?)' +
    '^RTXMON_NVAPI_COOLER_V1_END site=(?<end_site>0x[0-9a-f]{8})\r?$'
)
$hitRecords = [regex]::Matches(
    $debugText,
    $recordPattern,
    [Text.RegularExpressions.RegexOptions]::IgnoreCase
)
if ($hitRecords.Count -lt 1 -or $hitRecords.Count -gt $maximumCaptureRecords) {
    throw "Cooler-status record count must be between 1 and $maximumCaptureRecords."
}
$samples = [Collections.Generic.List[object]]::new()
foreach ($hitRecord in $hitRecords) {
    $callerRva = $hitRecord.Groups['site'].Value.ToLowerInvariant()
    $endCallerRva = $hitRecord.Groups['end_site'].Value.ToLowerInvariant()
    if ($callerRva -ne $endCallerRva -or
        $profile.caller_rvas -notcontains $callerRva) {
        throw 'A cooler-status record has an invalid call-site boundary.'
    }

    $returnStatus = '0x{0}' -f
        $hitRecord.Groups['status'].Value.ToLowerInvariant()
    if ($returnStatus -ne '0x00000000') {
        throw "The fixed cooler-status call returned a non-success status: $returnStatus."
    }

    $bufferAddress = '0x{0}' -f
        $hitRecord.Groups['buffer'].Value.ToLowerInvariant()
    $bufferAddressValue = [Convert]::ToUInt64($bufferAddress.Substring(2), 16)
    if ($bufferAddressValue -eq 0) {
        throw 'The cooler-status call returned a null buffer pointer.'
    }

    $wordMatches = [regex]::Matches(
        $hitRecord.Groups['dump'].Value,
        '(?im)^(?<address>[0-9a-f]{8}(?:\x60[0-9a-f]{8})?)[ \t]+(?<value>[0-9a-f]{8})[ \t]*\r?$',
        [Text.RegularExpressions.RegexOptions]::IgnoreCase
    )
    if ($wordMatches.Count -ne $profile.structure_word_count) {
        throw "A cooler-status hit does not contain exactly $($profile.structure_word_count) bounded DWORDs."
    }

    $rawWords = @(
        for ($wordIndex = 0; $wordIndex -lt $profile.structure_word_count; $wordIndex++) {
            $wordMatch = $wordMatches[$wordIndex]
            $addressText = $wordMatch.Groups['address'].Value.Replace(
                ([char]0x60).ToString(),
                ''
            )
            $observedAddress = [Convert]::ToUInt64($addressText, 16)
            $expectedAddress = $bufferAddressValue + [uint64]($wordIndex * 4)
            if ($observedAddress -ne $expectedAddress) {
                throw "Cooler-status DWORD addresses are not contiguous at index $wordIndex."
            }

            '0x{0}' -f $wordMatch.Groups['value'].Value.ToLowerInvariant()
        }
    )
    if ($rawWords[0] -ne $profile.structure_version) {
        throw "Cooler-status structure version changed from $($profile.structure_version)."
    }

    $countWord = [Convert]::ToUInt32($rawWords[1].Substring(2), 16)
    $observedCount = [int]($countWord -band [uint32]0xff)
    if ($observedCount -lt 1 -or $observedCount -gt $profile.entry_capacity) {
        throw "Cooler-status count $observedCount is outside the fixed capacity."
    }

    $rawEntries = @(
        for ($entryIndex = 0; $entryIndex -lt $observedCount; $entryIndex++) {
            $baseByteOffset = $profile.entry_base_offset +
                ($entryIndex * $profile.entry_stride_bytes)
            $baseWordIndex = [int]($baseByteOffset / 4)
            [pscustomobject]@{
                entry_index = $entryIndex
                base_word_index = $baseWordIndex
                raw_identifier_word = $rawWords[$baseWordIndex]
                raw_field_words = @(
                    foreach ($byteOffset in $profile.raw_field_byte_offsets_within_entry) {
                        $rawWords[$baseWordIndex + [int]($byteOffset / 4)]
                    }
                )
            }
        }
    )

    $samples.Add([pscustomobject]@{
            sequence = $samples.Count + 1
            thread_id = '0x{0}' -f
                $hitRecord.Groups['tid'].Value.ToLowerInvariant()
            caller_rva = $callerRva
            return_status = $returnStatus
            buffer_address = $bufferAddress
            structure_version = $rawWords[0]
            observed_count = $observedCount
            raw_words = $rawWords
            raw_entries = $rawEntries
        })
}

if ($samples.Count -eq 0) {
    throw 'No successful cooler-status v1 call was observed during the bounded window.'
}

$callSites = @(
    foreach ($callerRva in $profile.caller_rvas) {
        $siteCallCount = @($samples | Where-Object { $_.caller_rva -eq $callerRva }).Count
        if ($siteCallCount -lt 1) {
            throw "No complete 426-DWORD observation was captured at call site $callerRva."
        }

        [ordered]@{
            caller_rva = $callerRva
            call_count = $siteCallCount
        }
    }
)

$report = [ordered]@{
    schema_version = 2
    source_kind = 'nvapi_cooler_status_v1_observation'
    profile_name = $profile.name
    capture_started_utc = $captureStartedUtc.ToString('O')
    captured_utc = $capturedUtc.ToString('O')
    duration_seconds = $DurationSeconds
    process_id = $gpuProcess.Id
    gpuz_sha256 = $gpuzSha256
    debugger_sha256 = (
        Get-FileHash -LiteralPath $CdbPath -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    debugger_file_version = (Get-Item -LiteralPath $CdbPath).VersionInfo.FileVersion
    gpu_profile = [ordered]@{
        gpu_index = $identity.gpu.index
        gpu_name = $identity.gpu.name
        gpu_uuid = $identity.gpu.uuid
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
    caller_rvas = $profile.caller_rvas
    capture_point = $profile.capture_point
    buffer_pointer_expression = $profile.buffer_pointer_expression
    return_status_register = $profile.return_status_register
    structure_version = $profile.structure_version
    structure_size_bytes = $profile.structure_size_bytes
    structure_word_count = $profile.structure_word_count
    count_byte_offset = $profile.count_byte_offset
    entry_base_offset = $profile.entry_base_offset
    entry_stride_bytes = $profile.entry_stride_bytes
    entry_capacity = $profile.entry_capacity
    raw_field_byte_offsets_within_entry =
        $profile.raw_field_byte_offsets_within_entry
    reference_log = [ordered]@{
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
    call_count = $samples.Count
    call_sites = $callSites
    samples = @($samples)
    warning = 'This fixed, fail-closed GPU, PCI, VBIOS, driver, artifact, and loaded-module profile passively reads the complete 426-DWORD buffer supplied by signed GPU-Z at two post-call return sites. It never calls NVAPI, writes memory, interprets the four raw per-entry fields, or generalizes this private interface beyond the anchored profile.'
}
$reportJson = $report | ConvertTo-Json -Depth 10
if (-not ($reportJson | Test-Json -SchemaFile $reportSchemaPath)) {
    throw 'Cooler-status report did not satisfy nvapi-cooler-status-v1-observation-v2.'
}

$reportJson | Set-Content -LiteralPath $reportPath -Encoding utf8NoBOM
if (Test-Path -LiteralPath $failurePath -PathType Leaf) {
    Remove-Item -LiteralPath $failurePath -Force
}

$report | ConvertTo-Json -Depth 10
