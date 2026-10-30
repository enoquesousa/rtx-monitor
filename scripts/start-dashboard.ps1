[CmdletBinding()]
param(
    [ValidateRange(1024, 65535)][int]$Port = 3060,
    [switch]$Build,
    [switch]$NoBrowser
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$dashboardRoot = Join-Path $projectRoot 'artifacts\dashboard'
$applicationRoot = Join-Path $dashboardRoot 'app'
$executable = Join-Path $applicationRoot 'RtxMonitor.Service.exe'
$dataRoot = Join-Path $dashboardRoot 'data'
$url = "http://127.0.0.1:$Port/"
New-Item -ItemType Directory -Force -Path $dashboardRoot, $dataRoot | Out-Null

function Get-Dashboard {
    try {
        $response = Invoke-RestMethod -Uri ($url + 'api') -TimeoutSec 2
        return $response.service -eq 'rtx-monitor'
    } catch { return $false }
}

if (Get-Dashboard) {
    if ($Build) { throw 'O painel está aberto. Encerre-o com stop-dashboard.ps1 antes de recompilar.' }
    if (-not $NoBrowser) { Start-Process $url }
    Write-Host "RTX 3060 Monitor: $url"
    exit 0
}

$listener = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
if ($listener) { throw "A porta $Port já está em uso por outro processo. Informe -Port com uma porta livre." }

if ($Build -or -not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    & (Join-Path $PSScriptRoot 'publish-service.ps1') -Configuration Release -OutputDirectory $applicationRoot
    if ($LASTEXITCODE -ne 0) { throw 'Falha na compilação do monitor.' }
}

$pythonCommand = Get-Command python -CommandType Application -ErrorAction Stop | Select-Object -First 1
$pythonPath = $pythonCommand.Source
$database = Join-Path $dataRoot 'telemetry.db'
$logSuffix = Get-Date -Format 'yyyyMMdd-HHmmss'
$stdout = Join-Path $dashboardRoot "monitor-$logSuffix.stdout.log"
$stderr = Join-Path $dashboardRoot "monitor-$logSuffix.stderr.log"
$arguments = @(
    "--RtxMonitor:Port=$Port",
    ('--RtxMonitor:DatabasePath="' + $database + '"'),
    '--RtxMonitor:ProfileTelemetryEnabled=false',
    '--RtxMonitor:ResearchTelemetryEnabled=true',
    ('--RtxMonitor:ResearchSnapshotPath="' + (Join-Path $dataRoot 'research.json') + '"'),
    ('--RtxMonitor:ResearchPythonPath="' + $pythonPath + '"')
)
$process = Start-Process -FilePath $executable -ArgumentList $arguments -WorkingDirectory $applicationRoot -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
[pscustomobject]@{
    process_id = $process.Id
    executable = $executable
    started_at_utc = $process.StartTime.ToUniversalTime().ToString('o')
    port = $Port
    url = $url
    database = $database
    stdout = $stdout
    stderr = $stderr
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dashboardRoot 'process.json') -Encoding utf8

for ($attempt = 0; $attempt -lt 40; $attempt++) {
    $process.Refresh()
    if ($process.HasExited) { throw "O monitor encerrou. Consulte $stderr e $stdout" }
    if (Get-Dashboard) {
        if (-not $NoBrowser) { Start-Process $url }
        Write-Host "RTX 3060 Monitor: $url"
        Write-Host 'Leituras públicas a cada segundo; consultas de pesquisa em rodízio, com horário individual.'
        Write-Host "Histórico: $database"
        exit 0
    }
    Start-Sleep -Milliseconds 500
}
throw "O monitor ainda não respondeu. Consulte $stdout e $stderr"
