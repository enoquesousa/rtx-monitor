[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('rtxmon-update-plan-' + [Guid]::NewGuid().ToString('N'))
$oldDirectory = Join-Path $testRoot '0.6.0'
New-Item -ItemType Directory -Path $oldDirectory | Out-Null
$oldExecutable = Join-Path $oldDirectory 'RtxMonitor.Service.exe'
[IO.File]::WriteAllText($oldExecutable, 'Offline test placeholder; never executed')
[IO.File]::WriteAllText((Join-Path $oldDirectory 'appsettings.json'), '{"RtxMonitor":{"DatabasePath":"relative.db"}}')
$database = Join-Path $oldDirectory 'relative.db'
[IO.File]::WriteAllText($database, 'Offline test placeholder; never opened')
$updater = Join-Path $PSScriptRoot '../update-service.ps1'

# Mocks exist only inside this test process. Never contact or control the real SCM.
function Get-CimInstance { [pscustomobject]@{ State='Running'; PathName=('"'+$oldExecutable+'"'); ProcessId=1234 } }
function Get-NetTCPConnection { [pscustomobject]@{ LocalAddress='127.0.0.1'; OwningProcess=1234 } }
function Invoke-RestMethod { [pscustomobject]@{ status='healthy'; service_version='0.6.0'; storage=@{ schema_version=1; database_path=$database } } }
function Stop-Service { throw 'Plan attempted to stop a service' }
function Start-Service { throw 'Plan attempted to start a service' }
function New-Service { throw 'Plan attempted to create a service' }
$checks = 0

function New-TestArchive([string]$name, [string]$mode) {
    $path = Join-Path $testRoot ($name+'.zip')
    $data = [Text.Encoding]::UTF8.GetBytes('package fixture')
    $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($data)).ToLowerInvariant()
    $names = @('RtxMonitor.Service.exe', 'rtxmon_native.dll', 'appsettings.json')
    if ($mode -eq 'traversal') { $names += '../escape.txt' }
    if ($mode -eq 'ads') { $names += 'file:stream' }
    if ($mode -eq 'emptysegment') { $names += 'folder//file.txt' }
    if ($mode -eq 'reserved') { $names += 'NUL.txt' }
    if ($mode -eq 'trailingdot') { $names += 'folder./file.txt' }
    $items = @($names | ForEach-Object { @{ path=$_; size_bytes=$data.Length; sha256=$digest } })
    $manifest = @{ product_version='0.11.0'; abi_version=8; files=$items }
    if ($mode -eq 'version') { $manifest.product_version='0.12.0' }
    $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $zip.CreateEntry('package-manifest.json')
        $writer = [IO.StreamWriter]::new($entry.Open())
        try { $writer.Write(($manifest | ConvertTo-Json -Depth 10)) } finally { $writer.Dispose() }
        if ($mode -eq 'extra') { $names += 'unlisted.txt' }
        if ($mode -eq 'duplicate') { $names += 'rtxmon_native.dll' }
        if ($mode -eq 'missing') { $names = @($names | Where-Object { $_ -ne 'rtxmon_native.dll' }) }
        foreach ($item in $names) {
            $stream = $zip.CreateEntry($item).Open()
            try {
                if ($mode -eq 'tamper' -and $item -eq 'rtxmon_native.dll') { $stream.WriteByte(0) }
                else { $stream.Write($data, 0, $data.Length) }
            }
            finally { $stream.Dispose() }
        }
    }
    finally { $zip.Dispose() }
    return $path
}

try {
    $valid = New-TestArchive 'valid' 'valid'
    $validHash = (Get-FileHash -LiteralPath $valid).Hash
    $plan = & $updater -PackageArchive $valid -ExpectedSha256 $validHash -EnableProfileTelemetry | ConvertFrom-Json
    if ($plan.mode -ne 'plan_only' -or $plan.database -ne $database -or
        (Test-Path -LiteralPath (Join-Path $testRoot '0.11.0'))) { throw 'Read-only plan regression' }
    $checks++
    foreach ($mode in @('hash', 'traversal', 'ads', 'emptysegment', 'reserved', 'trailingdot', 'version', 'extra', 'duplicate', 'missing', 'tamper')) {
        $archive = if ($mode -eq 'hash') { $valid } else { New-TestArchive $mode $mode }
        $digest = if ($mode -eq 'hash') { '0'*64 } else { (Get-FileHash -LiteralPath $archive).Hash }
        $rejected = $false
        try { & $updater -PackageArchive $archive -ExpectedSha256 $digest | Out-Null }
        catch { $rejected = $true }
        if (-not $rejected -or (Test-Path -LiteralPath (Join-Path $testRoot '0.11.0'))) {
            throw "Unsafe plan accepted: $mode"
        }
        $checks++
    }
    New-Item -ItemType Directory -Path (Join-Path $testRoot '0.11.0') | Out-Null
    $rejected = $false
    try { & $updater -PackageArchive $valid -ExpectedSha256 $validHash | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw 'Existing installation would be overwritten' }
    $checks++
    @{ passed=$true; checks=$checks; scope='offline package and read-only plan; no real SCM or Apply invocation' } | ConvertTo-Json
}
finally {
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    if ($resolvedRoot.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedRoot) -match '^rtxmon-update-plan-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
    }
}
