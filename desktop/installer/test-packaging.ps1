[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$testRoot = Join-Path $projectRoot ('artifacts\desktop\installer-tests\run-' + [Guid]::NewGuid().ToString('N'))
$stage = Join-Path $testRoot 'stage'
$output = Join-Path $testRoot 'package'
New-Item -ItemType Directory -Force -Path $stage,(Join-Path $stage 'runtime\collector'),(Join-Path $stage 'assets\modelo') | Out-Null
[IO.File]::WriteAllText((Join-Path $stage 'rtx3060-monitor.exe'), 'SYNTHETIC_INSTALLER_TEST_NOT_EXECUTABLE', [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $stage 'runtime\collector\fixture.json'), '{"fixture":"synthetic-no-hardware","raw":18446744073709551615}', [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $stage 'assets\modelo\referência.txt'), 'Synthetic Unicode filename fixture', [Text.UTF8Encoding]::new($false))
& (Join-Path $projectRoot 'scripts\package-desktop.ps1') -SkipBuild -StagingDirectory $stage -OutputDirectory $output -Version 0.0.0
if ($LASTEXITCODE -ne 0) { throw 'A fixture MSI não compilou.' }
$audit = Get-Content -LiteralPath (Join-Path $output 'msi-audit.json') -Raw | ConvertFrom-Json
if (-not $audit.passed -or $audit.file_count -ne 3 -or $audit.extracted_file_hashes_verified -ne 3) { throw 'Os bytes extraídos não foram verificados.' }
$first = Get-Content -LiteralPath (Join-Path $output 'payload-manifest.json') -Raw | ConvertFrom-Json
$repeatOutput = Join-Path $testRoot 'repeat'
& (Join-Path $PSScriptRoot 'generate-source.ps1') -StagingDirectory $stage -OutputDirectory $repeatOutput -Version 0.0.0 | Out-Null
$second = Get-Content -LiteralPath (Join-Path $repeatOutput 'payload-manifest.json') -Raw | ConvertFrom-Json
if ($first.product_code -ne $second.product_code -or $first.payload_sha256 -ne $second.payload_sha256) { throw 'Identidade do payload não é estável.' }

# A mutable user database must never become an installer payload.
$dataDirectory = Join-Path $stage 'data'
New-Item -ItemType Directory -Force -Path $dataDirectory | Out-Null
[IO.File]::WriteAllText((Join-Path $dataDirectory 'user.db'), 'SYNTHETIC_USER_DATA_KEEP', [Text.UTF8Encoding]::new($false))
$rejected = $false
try { & (Join-Path $PSScriptRoot 'generate-source.ps1') -StagingDirectory $stage -OutputDirectory (Join-Path $testRoot 'invalid') -Version 0.0.0 | Out-Null }
catch { $rejected = $_.Exception.Message -match 'Dados de execução' }
if (-not $rejected) { throw 'O gerador aceitou dados mutáveis do usuário.' }
if ((Get-Content -LiteralPath (Join-Path $dataDirectory 'user.db') -Raw) -ne 'SYNTHETIC_USER_DATA_KEEP') { throw 'O teste alterou os dados que deveria preservar.' }

# A manifest cannot whitelist changed evidence merely by naming it.
$proofStage = Join-Path $testRoot 'proof-stage'
$vendor = Join-Path $proofStage 'runtime\collector\research-readers\vendor'
New-Item -ItemType Directory -Force -Path $vendor,(Join-Path $proofStage 'runtime\python') | Out-Null
Copy-Item -LiteralPath (Join-Path $stage 'rtx3060-monitor.exe') -Destination $proofStage
$readerRoot = Split-Path -Parent $vendor
[IO.File]::WriteAllText((Join-Path $readerRoot 'worker.py'), 'SYNTHETIC_READER_NOT_EXECUTED', [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $proofStage 'runtime\python\python.exe'), 'SYNTHETIC_PYTHON_NOT_EXECUTED', [Text.UTF8Encoding]::new($false))
$proofFile = Join-Path $vendor 'proof.log'
[IO.File]::WriteAllText($proofFile, 'IMMUTABLE_SYNTHETIC_PROOF', [Text.UTF8Encoding]::new($false))
$pins = @{ schema_version = 1; settings_writes_requested = $false; files = @(@{ path = 'proof.log'; sha256 = (Get-FileHash -LiteralPath $proofFile -Algorithm SHA256).Hash.ToLowerInvariant() }) }
$pins | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path (Split-Path -Parent $vendor) 'manifest.json') -Encoding utf8
$rejected = $false
try { & (Join-Path $PSScriptRoot 'generate-source.ps1') -StagingDirectory $proofStage -OutputDirectory (Join-Path $testRoot 'proof-public') -Version 0.0.0 | Out-Null }
catch { $rejected = $_.Exception.Message -match 'pacote público contém leitores privados' }
if (-not $rejected) { throw 'O gerador público aceitou leitores privados.' }
& (Join-Path $PSScriptRoot 'generate-source.ps1') -StagingDirectory $proofStage -OutputDirectory (Join-Path $testRoot 'proof-good') -Version 0.0.0 -PackageMode private | Out-Null
Remove-Item -LiteralPath $proofFile
$rejected = $false
try { & (Join-Path $PSScriptRoot 'generate-source.ps1') -StagingDirectory $proofStage -OutputDirectory (Join-Path $testRoot 'proof-missing') -Version 0.0.0 -PackageMode private | Out-Null }
catch { $rejected = $_.Exception.Message -match 'Prova revisada ausente' }
if (-not $rejected) { throw 'O gerador privado aceitou uma prova ausente.' }
[IO.File]::WriteAllText($proofFile, 'CHANGED_SYNTHETIC_PROOF', [Text.UTF8Encoding]::new($false))
$rejected = $false
try { & (Join-Path $PSScriptRoot 'generate-source.ps1') -StagingDirectory $proofStage -OutputDirectory (Join-Path $testRoot 'proof-bad') -Version 0.0.0 -PackageMode private | Out-Null }
catch { $rejected = $_.Exception.Message -match 'Prova revisada alterada' }
if (-not $rejected) { throw 'O gerador aceitou uma prova alterada.' }
[pscustomobject]@{
    passed = $true; fixture_only = $true; installed = $false; gpu_calls = 0
    msi_tables_and_cab_bytes = $true; stable_payload_identity = $true
    mutable_data_rejected_and_preserved = $true; changed_proof_rejected = $true
    result_directory = $testRoot
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testRoot 'test-results.json') -Encoding utf8
Write-Output "Packaging tests passed (synthetic payload, no install, no GPU calls): $testRoot"
