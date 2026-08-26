[CmdletBinding()]
param(
    [ValidateRange(5, 60)]
    [int]$DurationSeconds = 12,

    [string]$ProcmonPath,

    [string]$GpuzPath = 'C:\Program Files (x86)\GPU-Z\GPU-Z.exe',

    [string]$OutputDirectory,

    [switch]$KeepRawTrace,

    [switch]$AttachToExistingGpuZ,

    [string]$ExpectedGpuzDriverSha256 =
        '999cf056a298cfce5f5a61d44c218ffafccd36ecff53e433768512073e6bf005'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This capture must run from an elevated PowerShell session.'
    }
}

function Assert-TrustedPublisher {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$PublisherFragment
    )

    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -notlike "*$PublisherFragment*") {
        throw "Signature validation failed for '$Path'. Expected publisher '$PublisherFragment'."
    }

    return $signature
}

function Quote-ProcessArgument {
    param([Parameter(Mandatory)][string]$Value)

    if ($Value.Contains('"')) {
        throw 'Capture paths cannot contain a double-quote character.'
    }

    return '"' + $Value + '"'
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
    throw 'GPU-Z Process Monitor capture is supported only on Windows.'
}

Assert-Administrator

$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ProcmonPath)) {
    $resolvedCommand = Get-Command Procmon.exe -ErrorAction Stop
    $ProcmonPath = $resolvedCommand.Source
}

$ProcmonPath = [IO.Path]::GetFullPath($ProcmonPath)
$GpuzPath = [IO.Path]::GetFullPath($GpuzPath)
if (-not (Test-Path -LiteralPath $ProcmonPath -PathType Leaf)) {
    throw "Process Monitor was not found at '$ProcmonPath'."
}

if (-not (Test-Path -LiteralPath $GpuzPath -PathType Leaf)) {
    throw "GPU-Z was not found at '$GpuzPath'."
}

$existingProcmon = @(Get-Process -Name 'Procmon', 'Procmon64', 'Procmon64a' -ErrorAction SilentlyContinue)
if ($existingProcmon.Count -ne 0) {
    throw 'Close the existing Process Monitor session before starting a bounded GPU-Z capture.'
}

$existingGpuZ = @(Get-Process -Name 'GPU-Z' -ErrorAction SilentlyContinue)
if ($AttachToExistingGpuZ) {
    if ($existingGpuZ.Count -ne 1) {
        throw 'Attach mode requires exactly one existing GPU-Z process.'
    }
}
elseif ($existingGpuZ.Count -ne 0 -or
    @(Get-Process -Name 'GPUQuery_External' -ErrorAction SilentlyContinue).Count -ne 0) {
    throw 'Close GPU-Z and GPUQuery_External before starting a bounded capture.'
}

$preexistingDriverService = Get-CimInstance `
    -ClassName Win32_SystemDriver `
    -Filter "Name='GPU-Z-v8'" `
    -ErrorAction SilentlyContinue
$preexistingDriverFile = Join-Path ([IO.Path]::GetTempPath()) 'GPU-Z-v8.sys'
if ($AttachToExistingGpuZ) {
    if ($null -eq $preexistingDriverService -or
        -not (Test-Path -LiteralPath $preexistingDriverFile -PathType Leaf)) {
        throw 'Attach mode requires the expected running GPU-Z-v8 service and temporary driver.'
    }

    $attachedDriverSignature = Get-AuthenticodeSignature -LiteralPath $preexistingDriverFile
    $attachedDriverHash = (Get-FileHash `
            -LiteralPath $preexistingDriverFile `
            -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($preexistingDriverService.State -ne 'Running' -or
        $attachedDriverSignature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        $null -eq $attachedDriverSignature.SignerCertificate -or
        $attachedDriverSignature.SignerCertificate.Subject -notlike '*TechPowerUp LLC*' -or
        $attachedDriverHash -ne $ExpectedGpuzDriverSha256) {
        throw 'Attach mode rejected the active GPU-Z-v8 service or driver identity.'
    }
}
elseif ($null -ne $preexistingDriverService -or
    (Test-Path -LiteralPath $preexistingDriverFile)) {
    throw 'A preexisting GPU-Z-v8 service or temporary driver must be reviewed and removed before capture.'
}

$procmonSignature = Assert-TrustedPublisher -Path $ProcmonPath -PublisherFragment 'Microsoft Corporation'
$gpuzSignature = Assert-TrustedPublisher -Path $GpuzPath -PublisherFragment 'TechPowerUp LLC'

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $captureName = 'gpuz-procmon-{0}' -f (Get-Date -Format 'yyyyMMdd-HHmmss')
    $OutputDirectory = Join-Path $projectRoot (Join-Path 'evidence' $captureName)
}

$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$evidenceRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'evidence'))
$evidencePrefix = $evidenceRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) +
    [IO.Path]::DirectorySeparatorChar
if (-not $OutputDirectory.StartsWith($evidencePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must be a child of '$evidenceRoot'."
}

if (Test-Path -LiteralPath $OutputDirectory) {
    throw "OutputDirectory already exists: '$OutputDirectory'."
}

$null = New-Item -ItemType Directory -Path $OutputDirectory
$failurePath = Join-Path $OutputDirectory 'capture-error.txt'
trap {
    ($_ | Out-String) | Set-Content -LiteralPath $failurePath -Encoding utf8NoBOM
    exit 1
}

$rawTracePath = Join-Path $OutputDirectory 'procmon-raw.pml'
$fullCsvPath = Join-Path $OutputDirectory 'procmon-full.csv'
$filteredCsvPath = Join-Path $OutputDirectory 'gpuz-events.csv'
$modulesPath = Join-Path $OutputDirectory 'gpuz-modules.json'
$manifestPath = Join-Path $OutputDirectory 'capture-manifest.json'
$gpuProcess = if ($AttachToExistingGpuZ) { $existingGpuZ[0] } else { $null }
$startedGpuZ = $false
$captureStarted = $false

try {
    $captureArguments = '-accepteula -backingfile {0} -quiet -minimized' -f
        (Quote-ProcessArgument $rawTracePath)
    $null = Start-Process -FilePath $ProcmonPath -ArgumentList $captureArguments -PassThru
    $captureStarted = $true
    Start-Sleep -Seconds 2

    if (-not $AttachToExistingGpuZ) {
        $gpuProcess = Start-Process -FilePath $GpuzPath -PassThru
        $startedGpuZ = $true
    }

    Start-Sleep -Seconds ([Math]::Min(4, $DurationSeconds))

    $moduleInventory = @(
        Get-Process -Id $gpuProcess.Id -ErrorAction Stop |
            Select-Object -ExpandProperty Modules |
            ForEach-Object {
                $versionInfo = try {
                    [Diagnostics.FileVersionInfo]::GetVersionInfo($_.FileName)
                }
                catch {
                    $null
                }
                [pscustomobject]@{
                    module_name = $_.ModuleName
                    file_name = $_.FileName
                    base_address = ('0x{0:x}' -f $_.BaseAddress.ToInt64())
                    module_memory_size = $_.ModuleMemorySize
                    file_version = if ($null -ne $versionInfo) {
                        $versionInfo.FileVersion
                    }
                    else {
                        $null
                    }
                    company_name = if ($null -ne $versionInfo) {
                        $versionInfo.CompanyName
                    }
                    else {
                        $null
                    }
                }
            }
    )
    $moduleInventory | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $modulesPath -Encoding utf8NoBOM

    $remainingSeconds = $DurationSeconds - [Math]::Min(4, $DurationSeconds)
    if ($remainingSeconds -gt 0) {
        Start-Sleep -Seconds $remainingSeconds
    }
}
finally {
    try {
        if ($startedGpuZ -and $null -ne $gpuProcess -and -not $gpuProcess.HasExited) {
            $null = $gpuProcess.CloseMainWindow()
            if (-not $gpuProcess.WaitForExit(8000)) {
                Stop-Process -Id $gpuProcess.Id -Force -ErrorAction SilentlyContinue
                $gpuProcess.WaitForExit(5000) | Out-Null
            }
        }

        if ($captureStarted) {
            $terminate = Start-Process `
                -FilePath $ProcmonPath `
                -ArgumentList '-terminate -quiet' `
                -PassThru `
                -Wait
            if ($terminate.ExitCode -ne 0) {
                throw "Process Monitor termination failed with exit code $($terminate.ExitCode)."
            }
        }
    }
    finally {
        if (-not $AttachToExistingGpuZ) {
            Stop-ExactGpuZDriverIfLeftBehind
        }
    }
}

if (-not (Test-Path -LiteralPath $rawTracePath -PathType Leaf)) {
    throw "Process Monitor did not create '$rawTracePath'."
}

$exportArguments = '-openlog {0} -saveas {1} -quiet' -f
    (Quote-ProcessArgument $rawTracePath),
    (Quote-ProcessArgument $fullCsvPath)
$export = Start-Process `
    -FilePath $ProcmonPath `
    -ArgumentList $exportArguments `
    -PassThru `
    -Wait
if ($export.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $fullCsvPath -PathType Leaf)) {
    throw "Process Monitor CSV export failed with exit code $($export.ExitCode)."
}

$gpuEvents = @(
    Import-Csv -LiteralPath $fullCsvPath |
        Where-Object { $_.'Process Name' -eq 'GPU-Z.exe' } |
        Where-Object { $_.Operation -in @('CreateFile', 'DeviceIoControl', 'Load Image') }
)
$gpuEvents | Export-Csv -LiteralPath $filteredCsvPath -NoTypeInformation -Encoding utf8NoBOM
if ($gpuEvents.Count -eq 0) {
    throw 'The trace contains no GPU-Z CreateFile, DeviceIoControl, or Load Image events.'
}

$operationCounts = @(
    $gpuEvents |
        Group-Object -Property Operation |
        Sort-Object -Property Name |
        ForEach-Object {
            [pscustomobject]@{
                operation = $_.Name
                count = $_.Count
            }
        }
)

$manifest = [ordered]@{
    schema_version = 1
    captured_utc = [DateTimeOffset]::UtcNow.ToString('O')
    duration_seconds = $DurationSeconds
    capture_mode = if ($AttachToExistingGpuZ) { 'attach_existing' } else { 'launch_bounded' }
    raw_trace_retained = [bool]$KeepRawTrace
    gpuz = [ordered]@{
        path = $GpuzPath
        sha256 = (Get-FileHash -LiteralPath $GpuzPath -Algorithm SHA256).Hash.ToLowerInvariant()
        signer_subject = $gpuzSignature.SignerCertificate.Subject
        signer_thumbprint = $gpuzSignature.SignerCertificate.Thumbprint
    }
    procmon = [ordered]@{
        path = $ProcmonPath
        sha256 = (Get-FileHash -LiteralPath $ProcmonPath -Algorithm SHA256).Hash.ToLowerInvariant()
        signer_subject = $procmonSignature.SignerCertificate.Subject
        signer_thumbprint = $procmonSignature.SignerCertificate.Thumbprint
    }
    filtered_event_count = $gpuEvents.Count
    operation_counts = $operationCounts
    files = [ordered]@{
        filtered_events = 'gpuz-events.csv'
        modules = 'gpuz-modules.json'
    }
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM

Remove-Item -LiteralPath $fullCsvPath -Force
if (-not $KeepRawTrace) {
    Remove-Item -LiteralPath $rawTracePath -Force
}

$manifest | ConvertTo-Json -Depth 8
