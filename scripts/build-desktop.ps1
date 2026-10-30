[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [switch]$SkipRustBuild,
    [switch]$SkipRuntime,
    [switch]$SkipNativeBuild,
    [switch]$IncludePrivateReaders
)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot $(if ($IncludePrivateReaders) { 'artifacts\desktop\dist-private' } else { 'artifacts\desktop\dist-public' })
}
$stage = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $stage | Out-Null
$manifest = Join-Path $projectRoot 'desktop\Cargo.toml'
if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { throw 'O projeto Rust desktop/Cargo.toml não foi encontrado.' }
if (-not $SkipRustBuild) {
    $previousRustFlags = $env:RUSTFLAGS
    try {
        if (-not $IncludePrivateReaders -and -not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
            $remap = '--remap-path-prefix=' + [IO.Path]::GetFullPath($env:USERPROFILE).TrimEnd('\','/') + '=/source-home'
            $env:RUSTFLAGS = (($previousRustFlags, $remap) -join ' ').Trim()
        }
        & cargo build --manifest-path $manifest --release --locked
        if ($LASTEXITCODE -ne 0) { throw 'Falha na compilação do aplicativo Rust.' }
    }
    finally { $env:RUSTFLAGS = $previousRustFlags }
}
$targetRoot = if ($env:CARGO_TARGET_DIR) {
    if ([IO.Path]::IsPathRooted($env:CARGO_TARGET_DIR)) { $env:CARGO_TARGET_DIR } else { Join-Path $projectRoot $env:CARGO_TARGET_DIR }
} else { Join-Path $projectRoot 'desktop\target' }
$executable = Join-Path $targetRoot 'release\rtx3060-monitor.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Executável Rust ausente: $executable" }
Copy-Item -LiteralPath $executable -Destination (Join-Path $stage 'rtx3060-monitor.exe') -Force
if (-not $SkipRuntime) {
    $runtimeScript = Join-Path $PSScriptRoot 'build-native-runtime.ps1'
    if (-not (Test-Path -LiteralPath $runtimeScript -PathType Leaf)) { throw 'O empacotador do runtime não foi encontrado.' }
    $runtimeArguments = @{ OutputDirectory = (Join-Path $stage 'runtime') }
    if ($SkipNativeBuild) { $runtimeArguments.SkipNativeBuild = $true }
    if ($IncludePrivateReaders) { $runtimeArguments.IncludePrivateReaders = $true }
    & $runtimeScript @runtimeArguments
    if ($LASTEXITCODE -ne 0) { throw 'Falha na preparação do runtime de telemetria.' }
}
foreach ($document in @('THIRD_PARTY_NOTICES.md', 'LICENSE')) {
    $source = Join-Path $projectRoot $document
    if (Test-Path -LiteralPath $source -PathType Leaf) { Copy-Item -LiteralPath $source -Destination $stage -Force }
}
$artwork = Join-Path $projectRoot 'docs\assets\rtx-monitor-rust-enoquesousa.png'
if (-not (Test-Path -LiteralPath $artwork -PathType Leaf)) { throw 'A arte do projeto não foi encontrada.' }
Copy-Item -LiteralPath $artwork -Destination (Join-Path $stage 'rtx-monitor-art.png') -Force
Write-Host "Aplicativo nativo preparado: $stage"
