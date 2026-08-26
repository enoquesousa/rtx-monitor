[CmdletBinding()]
param(
    [string]$SourceDriverPath = 'C:\Users\sousa\AppData\Local\Temp\GPU-Z-v8.sys',

    [string]$OutputDirectory,

    [string]$ServiceName = 'GPU-Z-v8'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This collection must run from an elevated PowerShell session.'
    }
}

if (-not $IsWindows) {
    throw 'GPU-Z driver collection is supported only on Windows.'
}

Assert-Administrator
if ($ServiceName -ne 'GPU-Z-v8') {
    throw "Only the exact GPU-Z-v8 service is accepted; received '$ServiceName'."
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$evidenceRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'evidence'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $collectionName = 'gpuz-driver-{0}' -f (Get-Date -Format 'yyyyMMdd-HHmmss')
    $OutputDirectory = Join-Path $evidenceRoot $collectionName
}

$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$evidencePrefix = $evidenceRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) +
    [IO.Path]::DirectorySeparatorChar
if (-not $OutputDirectory.StartsWith($evidencePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must be a child of '$evidenceRoot'."
}

if (-not (Test-Path -LiteralPath $SourceDriverPath -PathType Leaf)) {
    throw "GPU-Z driver source was not found at '$SourceDriverPath'."
}

$SourceDriverPath = [IO.Path]::GetFullPath($SourceDriverPath)
$expectedSourcePath = [IO.Path]::GetFullPath(
    (Join-Path ([IO.Path]::GetTempPath()) 'GPU-Z-v8.sys'))
if (-not $SourceDriverPath.Equals($expectedSourcePath, [StringComparison]::OrdinalIgnoreCase)) {
    throw "SourceDriverPath must be the exact GPU-Z temporary driver '$expectedSourcePath'."
}

$serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
if (-not (Test-Path -LiteralPath $serviceKey)) {
    throw "The '$ServiceName' service registry key does not exist."
}

$serviceConfiguration = Get-ItemProperty -LiteralPath $serviceKey
$registeredPath = ([string]$serviceConfiguration.ImagePath) -replace '^\\\?\?\\', ''
$registeredPath = [IO.Path]::GetFullPath($registeredPath)
if (-not $registeredPath.Equals($SourceDriverPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The '$ServiceName' service points to '$registeredPath', not '$SourceDriverPath'."
}

if (Test-Path -LiteralPath $OutputDirectory) {
    throw "OutputDirectory already exists: '$OutputDirectory'."
}

$null = New-Item -ItemType Directory -Path $OutputDirectory
$copyPath = Join-Path $OutputDirectory 'GPU-Z-v8.sys'
$manifestPath = Join-Path $OutputDirectory 'driver-manifest.json'

$sourceSignature = Get-AuthenticodeSignature -LiteralPath $SourceDriverPath
if ($sourceSignature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
    $null -eq $sourceSignature.SignerCertificate) {
    throw "The GPU-Z driver signature is not valid: $($sourceSignature.Status)."
}

$sourceHash = (Get-FileHash -LiteralPath $SourceDriverPath -Algorithm SHA256).Hash.ToLowerInvariant()
Copy-Item -LiteralPath $SourceDriverPath -Destination $copyPath
$copyHash = (Get-FileHash -LiteralPath $copyPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($copyHash -ne $sourceHash) {
    throw 'The collected driver hash does not match the source driver hash.'
}

$copySignature = Get-AuthenticodeSignature -LiteralPath $copyPath
if ($copySignature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
    $copySignature.SignerCertificate.Thumbprint -ne
        $sourceSignature.SignerCertificate.Thumbprint) {
    throw 'The collected driver signature does not match the validated source signature.'
}

$driverItem = Get-Item -LiteralPath $copyPath
$cleanup = [ordered]@{
    service_stopped = $false
    service_deleted = $false
    temporary_source_deleted = $false
}

try {
    & sc.exe stop $ServiceName | Out-Null
    if ($LASTEXITCODE -notin @(0, 1062)) {
        throw "Stopping '$ServiceName' failed with sc.exe exit code $LASTEXITCODE."
    }

    $stopped = $false
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        $driverService = Get-CimInstance `
            -ClassName Win32_SystemDriver `
            -Filter "Name='$ServiceName'" `
            -ErrorAction SilentlyContinue
        if ($null -eq $driverService -or $driverService.State -eq 'Stopped') {
            $stopped = $true
            break
        }

        Start-Sleep -Milliseconds 250
    }

    if (-not $stopped) {
        throw "The '$ServiceName' driver did not reach the stopped state."
    }

    $cleanup.service_stopped = $true
    & sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -notin @(0, 1060)) {
        throw "Deleting '$ServiceName' failed with sc.exe exit code $LASTEXITCODE."
    }

    $cleanup.service_deleted = $true
    if (Test-Path -LiteralPath $SourceDriverPath -PathType Leaf) {
        $currentSourceHash = (Get-FileHash -LiteralPath $SourceDriverPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($currentSourceHash -ne $copyHash) {
            throw 'The temporary source changed after collection; it will not be deleted.'
        }

        Remove-Item -LiteralPath $SourceDriverPath -Force
    }

    $cleanup.temporary_source_deleted = -not (Test-Path -LiteralPath $SourceDriverPath)
}
finally {
    $manifest = [ordered]@{
        schema_version = 1
        collected_utc = [DateTimeOffset]::UtcNow.ToString('O')
        source_path = $SourceDriverPath
        collected_file = 'GPU-Z-v8.sys'
        size_bytes = $driverItem.Length
        sha256 = $copyHash
        file_version = $driverItem.VersionInfo.FileVersion
        product_name = $driverItem.VersionInfo.ProductName
        company_name = $driverItem.VersionInfo.CompanyName
        signature = [ordered]@{
            status = [string]$copySignature.Status
            subject = $copySignature.SignerCertificate.Subject
            issuer = $copySignature.SignerCertificate.Issuer
            thumbprint = $copySignature.SignerCertificate.Thumbprint
        }
        service = [ordered]@{
            name = $ServiceName
            image_path = [string]$serviceConfiguration.ImagePath
            type = [int]$serviceConfiguration.Type
            start = [int]$serviceConfiguration.Start
            error_control = [int]$serviceConfiguration.ErrorControl
        }
        cleanup = $cleanup
    }
    $manifest | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
}

Get-Content -Raw -LiteralPath $manifestPath
