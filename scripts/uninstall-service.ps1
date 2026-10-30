[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [ValidatePattern('^[A-Za-z0-9_.-]{1,128}$')]
    [string]$ServiceName = 'RtxMonitorService'
)

$ErrorActionPreference = 'Stop'

if (-not [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [Runtime.InteropServices.OSPlatform]::Windows)) {
    throw 'A remoção de um Windows Service exige Windows.'
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Execute este script em um PowerShell como Administrador.'
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -eq $service) {
    throw "O serviço '$ServiceName' não existe."
}
if (-not $PSCmdlet.ShouldProcess($ServiceName, 'parar e remover o Windows Service')) {
    return
}

if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
    Stop-Service -Name $ServiceName
    $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, '00:00:30')
}

& sc.exe delete $ServiceName | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Não foi possível remover o serviço '$ServiceName'."
}
$service.Dispose()

$deletionDeadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
while ($null -ne (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) -and
    [DateTimeOffset]::UtcNow -lt $deletionDeadline) {
    Start-Sleep -Milliseconds 200
}
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    throw "O serviço '$ServiceName' foi marcado para remoção, mas ainda está em uso."
}

Write-Host "Windows Service removed: $ServiceName"
Write-Host 'Os binários e o banco de telemetria foram preservados.'
