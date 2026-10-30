[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory, [switch]$SkipNativeBuild, [switch]$IncludePrivateReaders)
$ErrorActionPreference='Stop'
$projectRoot=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$destination=[IO.Path]::GetFullPath($OutputDirectory,$projectRoot)
if (-not $destination.StartsWith((Join-Path $projectRoot 'artifacts')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'O runtime deve ser preparado dentro de artifacts.' }
$collector=Join-Path $destination 'collector'
$python=Join-Path $destination 'python'
$toolsDirectory=Join-Path $projectRoot 'artifacts\tools\python'
if ($IncludePrivateReaders) {
    $privateRoot=Join-Path $projectRoot 'scripts\research-readers'
    if (-not (Test-Path -LiteralPath (Join-Path $privateRoot 'worker.py') -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $privateRoot 'manifest.json') -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $privateRoot 'vendor') -PathType Container)) {
        throw 'O build privado exige o pacote local completo de leitores revisados.'
    }
    & python -B (Join-Path $privateRoot 'worker.py') --verify
    if ($LASTEXITCODE -ne 0) { throw 'O pacote privado não passou na verificação anterior ao build.' }
}
# Only generated directories inside the verified artifacts target are reset.
# This prevents a previous private build from leaking into a public package.
foreach ($generated in @($collector,$python)) {
    $resolved=[IO.Path]::GetFullPath($generated)
    if (-not $resolved.StartsWith($destination+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Diretório gerado fora do staging previsto.'
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
New-Item -ItemType Directory -Force -Path $collector,$python,$toolsDirectory | Out-Null
if (-not $SkipNativeBuild) {
    & cmake --build --preset windows-x64-release
    if ($LASTEXITCODE -ne 0) { throw 'Falha na biblioteca C nativa.' }
}
$publishArguments = @(
    (Join-Path $projectRoot 'csharp\RtxMonitor.Service\RtxMonitor.Service.csproj'),
    '-c','Release','-r','win-x64','--self-contained','true','-o',$collector,'--nologo',
    '-p:RtxMonNativeBundle=true','-p:StaticWebAssetsEnabled=false',
    ('-p:RtxMonIncludeResearchReaders=' + $IncludePrivateReaders.IsPresent.ToString().ToLowerInvariant())
)
if (-not $IncludePrivateReaders) {
    $publishArguments += @('-p:DebugType=none','-p:DebugSymbols=false')
    # MSBuild may otherwise reuse a previous private build with absolute PDB paths.
    & dotnet clean (Join-Path $projectRoot 'csharp\RtxMonitor.Service\RtxMonitor.Service.csproj') -c Release -r win-x64 --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Falha na limpeza dos objetos .NET antes do build público.' }
}
& dotnet publish @publishArguments
if ($LASTEXITCODE -ne 0) { throw 'Falha no coletor autônomo.' }
if (-not $IncludePrivateReaders) {
    Get-ChildItem -LiteralPath $collector -Recurse -Filter '*.pdb' -File | ForEach-Object {
        Remove-Item -LiteralPath $_.FullName -Force
    }
}
if ($IncludePrivateReaders) {
    $archive=Join-Path $toolsDirectory 'python-3.13.15-embed-amd64.zip'
    $expectedHash='d1f04d990aee1253d8569e8e5104e30fa9f5fa830899f14843448872d936a2cf'
    if (-not (Test-Path -LiteralPath $archive)) { Invoke-WebRequest -Uri 'https://www.python.org/ftp/python/3.13.15/python-3.13.15-embed-amd64.zip' -OutFile $archive }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedHash) { throw 'SHA-256 do Python diferente da publicação oficial.' }
    Expand-Archive -LiteralPath $archive -DestinationPath $python -Force
    # Exact reviewed readers use only the standard library.
    Get-ChildItem -LiteralPath $python -Filter 'vcruntime140*.dll' -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $collector -Force }
    & (Join-Path $python 'python.exe') -B (Join-Path $collector 'research-readers\worker.py') --verify
    if ($LASTEXITCODE -ne 0) { throw 'Os leitores não passaram na verificação com o runtime embarcado.' }
}
else {
    Remove-Item -LiteralPath $python -Force
    if (Test-Path -LiteralPath (Join-Path $collector 'research-readers')) { throw 'O build público incluiu arquivos de pesquisa privados.' }
}
$pins=[ordered]@{private_readers=$IncludePrivateReaders.IsPresent; native_ui='Rust + egui + wgpu'; configuration_writes=$false; global_runtime_installations=$false}
if ($IncludePrivateReaders) { $pins.python_version='3.13.15'; $pins.python_source='https://www.python.org/downloads/release/python-31315/'; $pins.python_archive_sha256=$expectedHash }
$pins | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $destination 'runtime-provenance.json')
Write-Output "Runtime autônomo preparado: $destination"
