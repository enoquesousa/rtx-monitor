[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$recordPath = Join-Path $projectRoot 'artifacts\dashboard\process.json'
if (-not (Test-Path -LiteralPath $recordPath)) { Write-Host 'Nenhum processo do painel registrado.'; exit 0 }
$record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json
$process = Get-Process -Id $record.process_id -ErrorAction SilentlyContinue
if (-not $process) { Write-Host 'O painel já está encerrado.'; exit 0 }
$expectedExecutable = Join-Path $projectRoot 'artifacts\dashboard\app\RtxMonitor.Service.exe'
$started = if ($record.started_at_utc -is [DateTime]) {
    $record.started_at_utc.ToUniversalTime()
} else {
    [DateTimeOffset]::Parse([string]$record.started_at_utc, [Globalization.CultureInfo]::InvariantCulture).UtcDateTime
}
if ($process.Path -ne $expectedExecutable -or $record.executable -ne $expectedExecutable -or [Math]::Abs(($process.StartTime.ToUniversalTime() - $started).TotalSeconds) -gt 1) {
    throw 'O processo não corresponde ao painel registrado. Nenhum processo foi encerrado.'
}
# Only this explicitly identified application and its isolated reader children.
& taskkill.exe /PID $process.Id /T /F
if ($LASTEXITCODE -ne 0) { throw 'Não foi possível encerrar o painel.' }
Write-Host 'Painel encerrado. Histórico e serviço instalado foram preservados.'
