[CmdletBinding()]
param(
    [ValidateRange(1, 10)]
    [int]$WakeAfterMinutes = 2,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'A suspensão controlada exige um processo elevado.'
}

$startedAt = [DateTimeOffset]::UtcNow
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $stamp = $startedAt.ToString('yyyyMMdd-HHmmss')
    $OutputPath = Join-Path $PSScriptRoot "..\evidence\windows-telemetry-suspend-$stamp.json"
}
$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedOutput)) | Out-Null
$taskName = "RtxMonitor-Controlled-Wake-$PID"
$wakeAt = (Get-Date).AddMinutes($WakeAfterMinutes)
$action = New-ScheduledTaskAction `
    -Execute "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
    -Argument '-NoProfile -NonInteractive -WindowStyle Hidden -Command "exit 0"'
$trigger = New-ScheduledTaskTrigger -Once -At $wakeAt
$settings = New-ScheduledTaskSettingsSet -WakeToRun -StartWhenAvailable `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 1)
$scheduled = $false
$wakeTimerText = $null
$suspendRequestedAt = $null
$resumedAt = $null
$errorMessage = $null

try {
    Register-ScheduledTask `
        -TaskName $taskName `
        -Action $action `
        -Trigger $trigger `
        -Settings $settings `
        -User 'SYSTEM' `
        -RunLevel Highest `
        -Force | Out-Null
    $scheduled = $true
    $wakeTimerText = (& powercfg.exe /waketimers 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or
        $wakeTimerText.IndexOf($taskName, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "O wake timer $taskName não apareceu em powercfg /waketimers."
    }

    $suspendRequestedAt = [DateTimeOffset]::UtcNow
    [IO.File]::WriteAllText(
        $resolvedOutput,
        ([ordered]@{
            schema_version = 1
            passed = $false
            phase = 'armed'
            task_name = $taskName
            wake_at = $wakeAt.ToUniversalTime().ToString('o')
            suspend_requested_at_unix_ms = $suspendRequestedAt.ToUnixTimeMilliseconds()
        } | ConvertTo-Json),
        [Text.UTF8Encoding]::new($false))
    & "$env:SystemRoot\System32\rundll32.exe" powrprof.dll,SetSuspendState 0,1,0
    if ($LASTEXITCODE -ne 0) {
        throw "SetSuspendState falhou com código $LASTEXITCODE."
    }
    Start-Sleep -Seconds 5
    $resumedAt = [DateTimeOffset]::UtcNow
}
catch {
    $errorMessage = $_.Exception.Message
}
finally {
    if ($scheduled) {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    }
}

$finishedAt = [DateTimeOffset]::UtcNow
$elapsedSuspendMs = if ($null -ne $suspendRequestedAt -and $null -ne $resumedAt) {
    ($resumedAt - $suspendRequestedAt).TotalMilliseconds
} else { $null }
$passed = $null -eq $errorMessage -and $elapsedSuspendMs -ge 30000
$summary = [ordered]@{
    schema_version = 1
    passed = $passed
    phase = 'completed'
    elevated_user = $identity.Name
    task_name = $taskName
    wake_at = $wakeAt.ToUniversalTime().ToString('o')
    wake_timer_confirmed = $null -ne $wakeTimerText -and
        $wakeTimerText.IndexOf($taskName, [StringComparison]::OrdinalIgnoreCase) -ge 0
    started_at_unix_ms = $startedAt.ToUnixTimeMilliseconds()
    suspend_requested_at_unix_ms = if ($null -eq $suspendRequestedAt) {
        $null
    } else { $suspendRequestedAt.ToUnixTimeMilliseconds() }
    resumed_at_unix_ms = if ($null -eq $resumedAt) {
        $null
    } else { $resumedAt.ToUnixTimeMilliseconds() }
    finished_at_unix_ms = $finishedAt.ToUnixTimeMilliseconds()
    suspend_elapsed_ms = $elapsedSuspendMs
    error = $errorMessage
    wake_timer_text = $wakeTimerText
}
[IO.File]::WriteAllText(
    $resolvedOutput,
    ($summary | ConvertTo-Json -Depth 6),
    [Text.UTF8Encoding]::new($false))
$summary | ConvertTo-Json -Depth 6
if (-not $passed) { exit 1 }
