[CmdletBinding()]
param(
    [ValidateRange(10, 60)]
    [int]$DurationSeconds = 15,

    [ValidateSet('DeviceIoControl', 'NtDeviceIoControlFile')]
    [string]$ObservedApi = 'DeviceIoControl',

    [int]$GpuzProcessId,

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

if (-not $IsWindows) {
    throw 'GPU-Z DeviceIoControl tracing is supported only on Windows.'
}

Assert-Administrator
$projectRoot = Split-Path -Parent $PSScriptRoot
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

$evidenceRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'evidence'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path `
        $evidenceRoot `
        ('gpuz-device-io-control-{0}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
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

$commandPath = Join-Path $OutputDirectory 'windbg-commands.txt'
$debugLogPath = Join-Path $OutputDirectory 'windbg-device-io-control.log'
$debugOutputPath = Join-Path $OutputDirectory 'cdb-output.txt'
$debugErrorPath = Join-Path $OutputDirectory 'cdb-error.txt'
$detachOutputPath = Join-Path $OutputDirectory 'cdb-detach-output.txt'
$detachErrorPath = Join-Path $OutputDirectory 'cdb-detach-error.txt'
$reportPath = Join-Path $OutputDirectory 'device-io-control-report.json'
$inputReportPath = Join-Path $OutputDirectory 'device-io-control-input-report.json'
$breakpointCommand = if ($ObservedApi -eq 'DeviceIoControl') {
    @'
bu kernelbase!DeviceIoControl ".if (@$t0 == 0) { r @$t1=poi(@esp+4); .echo RTXMON_HANDLE_BEGIN; !handle @$t1 f; .echo RTXMON_HANDLE_END; r @$t0=1 }; r @$t2=poi(@esp+c); .if (dwo(@esp+10) == 4) { .if (@$t2 != 0) { .printf \"RTXMON_IOCTL_INPUT code=0x%08x size=4 d0=0x%08x\\n\", dwo(@esp+8), dwo(@$t2) } }; .if (dwo(@esp+10) == 0xc) { .if (@$t2 != 0) { .printf \"RTXMON_IOCTL_INPUT code=0x%08x size=12 d0=0x%08x d1=0x%08x d2=0x%08x\\n\", dwo(@esp+8), dwo(@$t2), dwo(@$t2+4), dwo(@$t2+8) } }; .printf \"RTXMON_DEVICE_IO_CONTROL handle=0x%08x code=0x%08x in=0x%08x in_size=%u out=0x%08x out_size=%u bytes=0x%08x overlapped=0x%08x\\n\", poi(@esp+4), dwo(@esp+8), poi(@esp+c), dwo(@esp+10), poi(@esp+14), dwo(@esp+18), poi(@esp+1c), poi(@esp+20); gc"
'@
}
else {
    @'
bu ntdll!NtDeviceIoControlFile ".if (@$t0 == 0) { r @$t1=poi(@esp+4); .echo RTXMON_HANDLE_BEGIN; !handle @$t1 f; .echo RTXMON_HANDLE_END; r @$t0=1 }; r @$t2=poi(@esp+1c); .if (dwo(@esp+20) == 4) { .if (@$t2 != 0) { .printf \"RTXMON_IOCTL_INPUT code=0x%08x size=4 d0=0x%08x\\n\", dwo(@esp+18), dwo(@$t2) } }; .if (dwo(@esp+20) == 0xc) { .if (@$t2 != 0) { .printf \"RTXMON_IOCTL_INPUT code=0x%08x size=12 d0=0x%08x d1=0x%08x d2=0x%08x\\n\", dwo(@esp+18), dwo(@$t2), dwo(@$t2+4), dwo(@$t2+8) } }; .printf \"RTXMON_DEVICE_IO_CONTROL handle=0x%08x code=0x%08x in=0x%08x in_size=%u out=0x%08x out_size=%u bytes=0x%08x overlapped=0x%08x\\n\", poi(@esp+4), dwo(@esp+18), poi(@esp+1c), dwo(@esp+20), poi(@esp+24), dwo(@esp+28), poi(@esp+14), poi(@esp+8); gc"
'@
}

$commandText = @'
.echo RTXMON_ATTACH_READY
r @$t0=0
__RTXMON_BREAKPOINT__
g
'@.Replace('__RTXMON_BREAKPOINT__', $breakpointCommand)
$commandText | Set-Content -LiteralPath $commandPath -Encoding ascii

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

$pipeName = 'rtx-monitor-gpuz-{0}-{1}' -f `
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

    $readyDeadline = (Get-Date).AddSeconds(15)
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
        throw 'CDB did not reach the attached capture ready marker.'
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
    'Command file execution failed|Syntax error|Some commands were skipped|Numeric expression missing|Couldn.t resolve',
    [Text.RegularExpressions.RegexOptions]::IgnoreCase)
if ($debuggerCommandFailure.Success) {
    throw "WinDbg rejected a capture command: $($debuggerCommandFailure.Value)"
}

$observations = @(
    [regex]::Matches(
        $debugText,
        'RTXMON_DEVICE_IO_CONTROL handle=0x(?<handle>[0-9a-fA-F]{8}) code=0x(?<code>[0-9a-fA-F]{8}) in=0x(?<input>[0-9a-fA-F]{8}) in_size=(?<inputSize>[0-9]+) out=0x(?<output>[0-9a-fA-F]{8}) out_size=(?<outputSize>[0-9]+) bytes=0x(?<bytes>[0-9a-fA-F]{8}) overlapped=0x(?<overlapped>[0-9a-fA-F]{8})') |
        ForEach-Object {
            [pscustomobject]@{
                handle = '0x' + $_.Groups['handle'].Value.ToLowerInvariant()
                code_value = [Convert]::ToUInt32($_.Groups['code'].Value, 16)
                input_size = [Convert]::ToInt32($_.Groups['inputSize'].Value, 10)
                output_size = [Convert]::ToInt32($_.Groups['outputSize'].Value, 10)
            }
        }
)

$inputObservations = @(
    [regex]::Matches(
        $debugText,
        'RTXMON_IOCTL_INPUT code=0x(?<code>[0-9a-fA-F]{8}) size=4 d0=0x(?<d0>[0-9a-fA-F]{8})') |
        ForEach-Object {
            $code = '0x' + $_.Groups['code'].Value.ToLowerInvariant()
            $dwords = @('0x' + $_.Groups['d0'].Value.ToLowerInvariant())
            [pscustomobject]@{
                signature_key = '{0}|4|{1}' -f $code, ($dwords -join ',')
                control_code = $code
                input_size = 4
                dwords = $dwords
            }
        }
    [regex]::Matches(
        $debugText,
        'RTXMON_IOCTL_INPUT code=0x(?<code>[0-9a-fA-F]{8}) size=12 d0=0x(?<d0>[0-9a-fA-F]{8}) d1=0x(?<d1>[0-9a-fA-F]{8}) d2=0x(?<d2>[0-9a-fA-F]{8})') |
        ForEach-Object {
            $code = '0x' + $_.Groups['code'].Value.ToLowerInvariant()
            $dwords = @(
                '0x' + $_.Groups['d0'].Value.ToLowerInvariant()
                '0x' + $_.Groups['d1'].Value.ToLowerInvariant()
                '0x' + $_.Groups['d2'].Value.ToLowerInvariant()
            )
            [pscustomobject]@{
                signature_key = '{0}|12|{1}' -f $code, ($dwords -join ',')
                control_code = $code
                input_size = 12
                dwords = $dwords
            }
        }
)

$expectedInputRecordCount = @(
    $observations | Where-Object { $_.input_size -in @(4, 12) }
).Count
if ($inputObservations.Count -ne $expectedInputRecordCount) {
    throw 'The bounded IOCTL input records do not match the observed 4/12-byte calls.'
}

$inputs = @(
    $inputObservations |
        Group-Object -Property signature_key |
        ForEach-Object {
            $item = $_.Group[0]
            [pscustomobject]@{
                control_code = $item.control_code
                input_size = $item.input_size
                dwords = $item.dwords
                call_count = $_.Count
            }
        } |
        Sort-Object -Property control_code, input_size, @{ Expression = 'call_count'; Descending = $true }
)

$signatures = @(
    $observations |
        Group-Object -Property code_value, input_size, output_size |
        ForEach-Object {
            $item = $_.Group[0]
            $code = [uint32]$item.code_value
            [pscustomobject]@{
                control_code = '0x{0:x8}' -f $code
                device_type = '0x{0:x4}' -f ($code -shr 16)
                access = ($code -shr 14) -band 0x3
                function = '0x{0:x3}' -f (($code -shr 2) -band 0xfff)
                method = $code -band 0x3
                input_size = $item.input_size
                output_size = $item.output_size
                handles = @($_.Group.handle | Sort-Object -Unique)
                call_count = $_.Count
            }
        } |
        Sort-Object -Property @{ Expression = 'call_count'; Descending = $true }, control_code
)

$capturedUtc = [DateTimeOffset]::UtcNow.ToString('O')
$gpuzSha256 = (Get-FileHash -LiteralPath $gpuzPath -Algorithm SHA256).Hash.ToLowerInvariant()
$observedApiName = if ($ObservedApi -eq 'DeviceIoControl') {
    'kernelbase!DeviceIoControl'
}
else {
    'ntdll!NtDeviceIoControlFile'
}
$report = [ordered]@{
    schema_version = 1
    source_kind = 'gpuz_device_io_control_observation'
    captured_utc = $capturedUtc
    duration_seconds = $DurationSeconds
    gpuz_sha256 = $gpuzSha256
    process_id = $gpuProcess.Id
    observed_api = $observedApiName
    observation_count = $observations.Count
    unique_signature_count = $signatures.Count
    signatures = $signatures
    warning = 'This passive trace records call metadata only. A control code and buffer size do not identify direction, ABI, returned fields, units, or a physical sensor.'
}
$report |
    ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $reportPath -Encoding utf8NoBOM

$schemaPath = Join-Path `
    $projectRoot `
    'docs\schema\gpuz-device-io-control-observation-v1.schema.json'
$reportJson = Get-Content -LiteralPath $reportPath -Raw
if (-not ($reportJson | Test-Json -SchemaFile $schemaPath)) {
    throw 'Generated DeviceIoControl report does not satisfy its schema.'
}

$inputReport = [ordered]@{
    schema_version = 1
    source_kind = 'gpuz_device_io_control_input_observation'
    captured_utc = $capturedUtc
    duration_seconds = $DurationSeconds
    gpuz_sha256 = $gpuzSha256
    process_id = $gpuProcess.Id
    observed_api = $observedApiName
    observation_report_sha256 = (
        Get-FileHash -LiteralPath $reportPath -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    debug_log_sha256 = (
        Get-FileHash -LiteralPath $debugLogPath -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    input_record_count = $inputObservations.Count
    unique_input_count = $inputs.Count
    inputs = $inputs
    warning = 'This passive trace records only bounded input selectors from calls declaring 4 or 12 input bytes. It never records output buffers. Input selectors do not by themselves identify units, returned fields, or a physical sensor.'
}
$inputReport |
    ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $inputReportPath -Encoding utf8NoBOM

$inputSchemaPath = Join-Path `
    $projectRoot `
    'docs\schema\gpuz-device-io-control-input-v1.schema.json'
$inputReportJson = Get-Content -LiteralPath $inputReportPath -Raw
if (-not ($inputReportJson | Test-Json -SchemaFile $inputSchemaPath)) {
    throw 'Generated DeviceIoControl input report does not satisfy its schema.'
}

$reportJson
