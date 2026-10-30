[CmdletBinding()]
param(
    [switch]$Json
)

$ErrorActionPreference = 'Stop'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-OptionalCommandPath {
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    $command = Get-Command $Name -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $command) {
        return $null
    }

    return $command.Source
}

function Get-TrustedNvidiaSmiPath {
    $windowsFolder = [Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)
    $programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    $candidates = @(
        (Join-Path $windowsFolder 'System32\nvidia-smi.exe')
    )
    if (-not [string]::IsNullOrWhiteSpace($programFiles)) {
        $candidates += Join-Path $programFiles 'NVIDIA Corporation\NVSMI\nvidia-smi.exe'
    }

    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            continue
        }

        $resolved = [System.IO.Path]::GetFullPath($candidate)
        $root = [System.IO.Path]::GetPathRoot($resolved)
        $current = $root
        $safePath = $true
        foreach ($component in $resolved.Substring($root.Length).Split(
                [System.IO.Path]::DirectorySeparatorChar,
                [StringSplitOptions]::RemoveEmptyEntries)) {
            $current = Join-Path $current $component
            $attributes = [System.IO.File]::GetAttributes($current)
            if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                $safePath = $false
                break
            }
        }
        if (-not $safePath) {
            continue
        }

        $item = Get-Item -LiteralPath $resolved
        $signature = Get-AuthenticodeSignature -LiteralPath $resolved
        $publisherAccepted = $null -ne $signature.SignerCertificate -and
            ($signature.SignerCertificate.Subject -match
                'O=(NVIDIA Corporation|Microsoft Corporation)(,|$)')
        if ($signature.Status -eq [System.Management.Automation.SignatureStatus]::Valid -and
            $publisherAccepted -and
            $item.VersionInfo.CompanyName -match '^NVIDIA( Corporation)?$') {
            return $resolved
        }
    }

    return $null
}

function Invoke-ProcessWithTimeout {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [ValidateRange(1000, 60000)]
        [int]$TimeoutMilliseconds = 10000
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Path
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    if ($null -ne $startInfo.ArgumentList) {
        foreach ($argument in $Arguments) {
            $startInfo.ArgumentList.Add($argument)
        }
    }
    else {
        # All current callers use fixed arguments without whitespace or quotes.
        $startInfo.Arguments = $Arguments -join ' '
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Could not start trusted process: $Path"
        }

        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutMilliseconds)) {
            try {
                $process.Kill($true)
            }
            catch {
                # The process may have exited between the timeout and Kill().
            }
            $terminated = $process.WaitForExit(2000)
            $stdoutText = if ($terminated -and $stdout.Wait(2000)) {
                $stdout.GetAwaiter().GetResult()
            }
            else {
                ''
            }
            $stderrText = if ($terminated -and $stderr.Wait(2000)) {
                $stderr.GetAwaiter().GetResult()
            }
            else {
                ''
            }
            return [ordered]@{
                status = 'timed_out'
                exit_code = $null
                stdout = $stdoutText
                stderr = $stderrText
            }
        }

        if (-not $stdout.Wait(2000) -or -not $stderr.Wait(2000)) {
            return [ordered]@{
                status = 'timed_out'
                exit_code = $null
                stdout = ''
                stderr = ''
            }
        }
        return [ordered]@{
            status = 'completed'
            exit_code = $process.ExitCode
            stdout = $stdout.GetAwaiter().GetResult()
            stderr = $stderr.GetAwaiter().GetResult()
        }
    }
    finally {
        $process.Dispose()
    }
}

function Get-GpuInventory {
    param(
        [Parameter(Mandatory)]
        [bool]$AllowExternalProcess
    )

    if (-not $AllowExternalProcess) {
        return [ordered]@{
            status = 'skipped_while_elevated'
            tool = $null
            devices = @()
        }
    }

    $nvidiaSmi = Get-TrustedNvidiaSmiPath
    if ($null -eq $nvidiaSmi) {
        return [ordered]@{
            status = 'unavailable'
            tool = $null
            devices = @()
        }
    }

    $execution = Invoke-ProcessWithTimeout `
        -Path $nvidiaSmi `
        -Arguments @(
            '--query-gpu=name,uuid,pci.bus_id,pci.device_id,pci.sub_device_id,driver_version,vbios_version',
            '--format=csv,noheader'
        ) `
        -TimeoutMilliseconds 10000
    if ($execution.status -eq 'timed_out') {
        return [ordered]@{
            status = 'query_timed_out'
            tool = $nvidiaSmi
            devices = @()
        }
    }

    if ($execution.exit_code -ne 0) {
        return [ordered]@{
            status = 'query_failed'
            tool = $nvidiaSmi
            devices = @()
        }
    }

    $lines = @(
        $execution.stdout -split '\r?\n' |
        Where-Object { $_.Length -gt 0 } |
        ForEach-Object { [string]$_ }
    )

    return [ordered]@{
        status = 'available'
        tool = $nvidiaSmi
        devices = $lines
    }
}

function Get-DeviceGuardState {
    try {
        $state = Get-CimInstance `
            -Namespace 'root\Microsoft\Windows\DeviceGuard' `
            -ClassName 'Win32_DeviceGuard' `
            -OperationTimeoutSec 5
        return [ordered]@{
            status = 'available'
            virtualization_based_security_status = $state.VirtualizationBasedSecurityStatus
            code_integrity_policy_enforcement_status = $state.CodeIntegrityPolicyEnforcementStatus
            security_services_running = @($state.SecurityServicesRunning)
        }
    }
    catch {
        return [ordered]@{
            status = 'query_failed'
            error = $_.Exception.Message
        }
    }
}

function Get-SecureBootState {
    try {
        return [ordered]@{
            status = 'available'
            enabled = [bool](Confirm-SecureBootUEFI)
        }
    }
    catch {
        return [ordered]@{
            status = 'query_failed'
            error = $_.Exception.Message
        }
    }
}

function Get-DriverToolchainState {
    $programFilesX86 = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::ProgramFilesX86)
    $kitsRoot = Join-Path $programFilesX86 'Windows Kits\10'
    $selectedKit = $null
    $kitVersions = @(
        Get-ChildItem `
            -LiteralPath (Join-Path $kitsRoot 'Include') `
            -Directory `
            -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
        Sort-Object { [version]$_.Name } -Descending
    )
    foreach ($versionDirectory in $kitVersions) {
        $version = $versionDirectory.Name
        $candidate = [ordered]@{
            version = $version
            kernel_header = Join-Path $kitsRoot "Include\$version\km\wdm.h"
            inf2cat = Join-Path $kitsRoot "bin\$version\x86\Inf2Cat.exe"
            signtool_x64 = Join-Path $kitsRoot "bin\$version\x64\signtool.exe"
            driver_build_props = Join-Path $kitsRoot "build\$version\WindowsDriver.Default.props"
        }
        if (@($candidate.Values | Select-Object -Skip 1 | Where-Object {
                    -not (Test-Path -LiteralPath $_ -PathType Leaf)
                }).Count -eq 0) {
            $selectedKit = $candidate
            break
        }
    }

    $selectedKmdf = $null
    $kmdfIncludeRoot = Join-Path $kitsRoot 'Include\wdf\kmdf'
    $kmdfVersions = @(
        Get-ChildItem `
            -LiteralPath $kmdfIncludeRoot `
            -Directory `
            -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d+\.\d+$' } |
        Sort-Object { [version]$_.Name } -Descending
    )
    foreach ($versionDirectory in $kmdfVersions) {
        $version = $versionDirectory.Name
        $candidate = [ordered]@{
            version = $version
            wdf_header = Join-Path $versionDirectory.FullName 'wdf.h'
            wdf_driver_entry_library =
                Join-Path $kitsRoot "Lib\wdf\kmdf\x64\$version\WdfDriverEntry.lib"
        }
        if (@($candidate.Values | Select-Object -Skip 1 | Where-Object {
                    -not (Test-Path -LiteralPath $_ -PathType Leaf)
                }).Count -eq 0) {
            $selectedKmdf = $candidate
            break
        }
    }

    $staticPrerequisitesDetected = $null -ne $selectedKit -and $null -ne $selectedKmdf

    return [ordered]@{
        status = if ($staticPrerequisitesDetected) {
            'static_prerequisites_detected_build_probe_required'
        }
        else {
            'incomplete'
        }
        selected_windows_kit = $selectedKit
        selected_kmdf = $selectedKmdf
        static_prerequisites_detected = $staticPrerequisitesDetected
        build_probe_performed = $false
        ready_for_kmdf_build = $false
    }
}

$isAdministrator = Test-IsAdministrator
try {
    $os = Get-CimInstance -ClassName Win32_OperatingSystem -OperationTimeoutSec 5
    $osStatus = 'available'
    $osError = $null
}
catch {
    $os = $null
    $osStatus = 'query_failed'
    $osError = $_.Exception.Message
}
$wslPath = Get-OptionalCommandPath -Name 'wsl.exe'
$nvflashPath = Get-OptionalCommandPath -Name 'nvflash64.exe'
if ($null -eq $nvflashPath) {
    $nvflashPath = Get-OptionalCommandPath -Name 'nvflash.exe'
}

$gpuInventory = Get-GpuInventory -AllowExternalProcess (-not $isAdministrator)
$report = [ordered]@{
    schema_version = 1
    generated_at_utc = [DateTimeOffset]::UtcNow.ToString('O')
    host = [ordered]@{
        computer_name = $env:COMPUTERNAME
        operating_system_status = $osStatus
        operating_system_error = $osError
        windows_caption = $os.Caption
        windows_version = $os.Version
        windows_build = $os.BuildNumber
        process_is_administrator = $isAdministrator
        device_guard = Get-DeviceGuardState
        secure_boot = Get-SecureBootState
    }
    gpu = $gpuInventory
    tools = [ordered]@{
        nvflash = $nvflashPath
        wsl = $wslPath
        windows_driver_toolchain = Get-DriverToolchainState
    }
    access_tiers = [ordered]@{
        public_apis_and_offline_analysis = [ordered]@{
            status = 'ready'
            administrator_required = $false
        }
        trusted_vbios_capture_tool = [ordered]@{
            status = if ($null -eq $nvflashPath) {
                'capture_tool_not_found_do_not_elevate_yet'
            }
            elseif ($isAdministrator) {
                'tool_found_administrator_ready_operator_review_required'
            }
            else {
                'tool_found_requires_administrator'
            }
            administrator_required = $true
            capture_tool_found = $null -ne $nvflashPath
        }
        windows_pci_config_or_allowlisted_mmio = [ordered]@{
            status = 'requires_administrator_and_signed_hvci_compatible_driver'
            administrator_alone_is_sufficient = $false
        }
        linux_pci_sysfs = [ordered]@{
            status = 'requires_root_on_native_linux_or_a_dedicated_linux_host'
            wsl_is_sufficient = $false
        }
    }
    safety = [ordered]@{
        blind_mmio_scan_allowed = $false
        blind_i2c_scan_allowed = $false
        register_writes_allowed = $false
        vulnerable_physical_memory_driver_allowed = $false
    }
}

if ($Json) {
    $report | ConvertTo-Json -Depth 8
}
else {
    Write-Host 'RTX Monitor v0.8 laboratory access report'
    Write-Host "Administrator: $isAdministrator"
    if ($null -eq $os) {
        Write-Host "Windows inventory: $osStatus"
    }
    else {
        Write-Host "Windows: $($os.Caption) $($os.Version) (build $($os.BuildNumber))"
    }
    Write-Host "NVIDIA inventory: $($report.gpu.status)"
    Write-Host "NVFlash found: $($null -ne $nvflashPath)"
    Write-Host "WSL found: $($null -ne $wslPath) (WSL is not sufficient for raw PCI ROM access)"
    Write-Host "WDK/KMDF static prerequisite status: $($report.tools.windows_driver_toolchain.status)"
    Write-Host ''
    Write-Host 'Authority required by stage:'
    Write-Host '  Public APIs and offline parsing: standard user.'
    if ($null -eq $nvflashPath) {
        Write-Host '  Trusted VBIOS capture: install and review a signed capture tool first; do not elevate yet.'
    }
    else {
        Write-Host '  Trusted VBIOS capture tool: Administrator after operator review.'
    }
    Write-Host '  PCI config or allowlisted MMIO on Windows: Administrator plus a signed, HVCI-compatible driver.'
    Write-Host '  Linux PCI config/ROM: root on native Linux or a dedicated Linux host; WSL is not enough.'
    Write-Host ''
    Write-Host 'No capture, device write, driver load, or hardware probe was performed.'
}
