[CmdletBinding()]
param(
    [string]$PublishDirectory,

    [ValidatePattern('^[A-Za-z0-9_.-]{1,128}$')]
    [string]$ServiceName = 'RtxMonitorService',

    [switch]$Start
)

$ErrorActionPreference = 'Stop'

if (-not [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [Runtime.InteropServices.OSPlatform]::Windows)) {
    throw 'A instalação como Windows Service exige Windows.'
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Execute este script em um PowerShell como Administrador.'
}

$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    $PublishDirectory = Join-Path $projectRoot 'artifacts\service\win-x64'
}
$resolvedPublish = (Resolve-Path -LiteralPath $PublishDirectory).Path
$requiredFiles = @(
    'RtxMonitor.Service.exe',
    'rtxmon_native.dll',
    'appsettings.json'
)
foreach ($requiredFile in $requiredFiles) {
    $requiredPath = Join-Path $resolvedPublish $requiredFile
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Publicação incompleta; arquivo ausente: $requiredPath"
    }
}
$executable = Join-Path $resolvedPublish 'RtxMonitor.Service.exe'
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    throw "O serviço '$ServiceName' já existe. Remova-o ou atualize seu binário explicitamente."
}

$service = New-Service `
    -Name $ServiceName `
    -BinaryPathName "`"$executable`"" `
    -DisplayName 'RTX Monitor Service' `
    -Description 'Coleta local somente leitura de telemetria NVIDIA.' `
    -StartupType Automatic

& sc.exe failure $ServiceName 'reset=' 86400 'actions=' 'restart/5000/restart/15000/restart/60000' | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Não foi possível configurar a recuperação do serviço '$ServiceName'."
}

if ($Start) {
    Start-Service -Name $ServiceName
    $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, '00:00:30')
}

Write-Host "Windows Service installed: $ServiceName"
Write-Host "Executable: $executable"
Write-Host 'Database default: %ProgramData%\RtxMonitor\telemetry.db'
