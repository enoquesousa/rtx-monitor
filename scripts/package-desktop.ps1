[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.12.4',
    [string]$StagingDirectory,
    [string]$OutputDirectory,
    [switch]$SkipBuild,
    [switch]$SkipNativeBuild,
    [switch]$IncludePrivateReaders
)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($StagingDirectory)) {
    $StagingDirectory = Join-Path $projectRoot $(if ($IncludePrivateReaders) { 'artifacts\desktop\dist-private' } else { 'artifacts\desktop\dist-public' })
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot $(if ($IncludePrivateReaders) { 'artifacts\desktop\installer-private' } else { 'artifacts\desktop\installer-public' })
}
$stage = [IO.Path]::GetFullPath($StagingDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $SkipBuild) {
    $buildArguments = @{ OutputDirectory = $stage }
    if ($SkipNativeBuild) { $buildArguments.SkipNativeBuild = $true }
    if ($IncludePrivateReaders) { $buildArguments.IncludePrivateReaders = $true }
    & (Join-Path $PSScriptRoot 'build-desktop.ps1') @buildArguments
    if ($LASTEXITCODE -ne 0) { throw 'O build nativo não foi concluído.' }
}
New-Item -ItemType Directory -Force -Path $output | Out-Null
$tools = & (Join-Path $projectRoot 'desktop\installer\ensure-wix.ps1')
$packageMode = if ($IncludePrivateReaders) { 'private' } else { 'public' }
$generated = & (Join-Path $projectRoot 'desktop\installer\generate-source.ps1') -StagingDirectory $stage -OutputDirectory $output -Version $Version -PackageMode $packageMode
$objectFile = Join-Path $output 'Rtx3060Monitor.wixobj'
$msi = Join-Path $output "RTX3060Monitor-$Version-x64.msi"
& $tools.Candle -nologo -arch x64 -wx -out $objectFile $generated.Source
if ($LASTEXITCODE -ne 0) { throw 'Falha no compilador WiX.' }
# ICE91 warns about per-machine propagation into per-user folders. This package
# deliberately rejects ALLUSERS, which is independently checked by audit-msi.
# Keep every other ICE check and treat its warnings as errors.
# Binder notice 1101 is limited to the explicitly authored neutral-language
# fallback for e_sqlite3.dll, whose original resources omit an MSI language.
& $tools.Light -nologo -wx -sice:ICE91 -sw1101 -out $msi $objectFile
if ($LASTEXITCODE -ne 0) { throw 'Falha no linker ou na validação ICE do MSI.' }
& (Join-Path $projectRoot 'desktop\installer\audit-msi.ps1') -MsiPath $msi -PayloadManifest $generated.Manifest -OutputPath (Join-Path $output 'msi-audit.json') -DarkPath $tools.Dark
if ($LASTEXITCODE -ne 0) { throw 'O MSI não passou na auditoria.' }
$hash = (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash.ToLowerInvariant()
($hash + '  ' + [IO.Path]::GetFileName($msi)) | Set-Content -LiteralPath ($msi + '.sha256') -Encoding ascii
Write-Host "Instalador MSI criado e auditado: $msi"
Write-Host "SHA-256: $hash"
