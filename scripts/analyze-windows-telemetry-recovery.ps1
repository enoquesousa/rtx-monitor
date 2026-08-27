[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$RunDirectory,
    [string]$ExpectedLuid = '0x000000000001669b',
    [ValidateRange(1, 600)]
    [int]$MinimumInterruptionSeconds = 30,
    [ValidateRange(1, 600)]
    [int]$MaximumInterruptionSeconds = 180,
    [ValidateRange(1, 60)]
    [int]$MaximumRecoverySeconds = 10
)

$ErrorActionPreference = 'Stop'
$resolvedRun = (Resolve-Path -LiteralPath $RunDirectory).Path
$samplesPath = Join-Path $resolvedRun 'samples.jsonl'
$rawSummaryPath = Join-Path $resolvedRun 'summary.json'
$outputPath = Join-Path $resolvedRun 'recovery-summary.json'
$rawSummary = Get-Content -Raw -LiteralPath $rawSummaryPath | ConvertFrom-Json
$samples = @(Get-Content -LiteralPath $samplesPath | ForEach-Object { $_ | ConvertFrom-Json })
if ($samples.Count -lt 2) { throw 'A série não possui amostras suficientes.' }

$largestGapMs = -1L
$resumeIndex = -1
for ($index = 1; $index -lt $samples.Count; $index++) {
    $gap = [long]$samples[$index].sampled_at_unix_ms -
        [long]$samples[$index - 1].sampled_at_unix_ms
    if ($gap -gt $largestGapMs) {
        $largestGapMs = $gap
        $resumeIndex = $index
    }
}

$firstAfter = $samples[$resumeIndex]
$lastBefore = $samples[$resumeIndex - 1]
$recovered = $null
for ($index = $resumeIndex; $index -lt $samples.Count; $index++) {
    $candidate = $samples[$index]
    if ($candidate.response.local_memory.state -eq 'available' -and
        [double]$candidate.response.local_memory.value -gt 0 -and
        $candidate.response.non_local_memory.state -eq 'available' -and
        $candidate.response.adapter.luid -eq $ExpectedLuid) {
        $recovered = $candidate
        break
    }
}
$recoveryMs = if ($null -eq $recovered) {
    $null
} else {
    [long]$recovered.sampled_at_unix_ms - [long]$firstAfter.sampled_at_unix_ms
}
$uniqueLuids = @($samples.response.adapter.luid | Sort-Object -Unique)
$passed = $largestGapMs -ge ($MinimumInterruptionSeconds * 1000) -and
    $largestGapMs -le ($MaximumInterruptionSeconds * 1000) -and
    $rawSummary.http_failures -eq 0 -and
    $rawSummary.contract_failures -eq 0 -and
    $rawSummary.identity_failures -eq 0 -and
    $rawSummary.timestamp_regressions -eq 0 -and
    $uniqueLuids.Count -eq 1 -and
    $uniqueLuids[0] -eq $ExpectedLuid -and
    $null -ne $recoveryMs -and
    $recoveryMs -le ($MaximumRecoverySeconds * 1000)

$summary = [ordered]@{
    schema_version = 1
    passed = $passed
    raw_continuity_passed = [bool]$rawSummary.passed
    sample_count = $samples.Count
    expected_luid = $ExpectedLuid
    unique_luids = $uniqueLuids
    interruption_ms = $largestGapMs
    last_before = $lastBefore
    first_after = $firstAfter
    recovered = $recovered
    recovery_ms = $recoveryMs
    http_failures = $rawSummary.http_failures
    contract_failures = $rawSummary.contract_failures
    identity_failures = $rawSummary.identity_failures
    timestamp_regressions = $rawSummary.timestamp_regressions
}
[IO.File]::WriteAllText(
    $outputPath,
    ($summary | ConvertTo-Json -Depth 12),
    [Text.UTF8Encoding]::new($false))
$summary | ConvertTo-Json -Depth 12
if (-not $passed) { exit 1 }
